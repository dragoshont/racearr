using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// The <see cref="IArrClient"/> half that racing never needed: look a title up by external id and
/// add it to the library. Kept as its own class and its own typed client rather than grown onto
/// <c>ArrClient</c> so the race engine's HTTP path is untouched by this feature — a bug here must
/// not be able to take down ratio enforcement, which is the reason racearr is deployed at all.
/// </summary>
/// <remarks>
/// Ordering matters and is load-bearing: <b>never POST without a successful lookup.</b> Reading a
/// transient 500 as "not in the library" would re-add an existing title every tick, which in
/// Radarr means a fresh <c>MoviesSearch</c> and a duplicate grab attempt every 120 seconds. So
/// <see cref="ExistsAsync"/> throws on any non-success and the caller skips the item; only an
/// authoritative empty array counts as absent.
/// </remarks>
public sealed class WatchlistArrClient(
    HttpClient http,
    RacearrOptions options,
    ILogger<WatchlistArrClient> log) : IWatchlistArrGateway
{
    /// <summary>Resolved add targets, keyed by instance name. Profiles and root folders change
    /// rarely, but they do change in the UI, so the cache expires instead of being fixed at startup.</summary>
    private readonly ConcurrentDictionary<string, (ArrTarget? Target, DateTimeOffset At)> _targets =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan TargetTtl = TimeSpan.FromMinutes(15);

    private static HttpRequestMessage Build(HttpMethod method, ArrInstance inst, string pathAndQuery)
    {
        var req = new HttpRequestMessage(method, $"{inst.Url}/api/v3/{pathAndQuery}");
        req.Headers.Add("X-Api-Key", inst.ApiKey);
        return req;
    }

    private async Task<JsonElement> GetJsonAsync(ArrInstance inst, string pathAndQuery, CancellationToken ct)
    {
        using var resp = await http.SendAsync(Build(HttpMethod.Get, inst, pathAndQuery), ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(ArrInstance inst, WatchlistItem item, CancellationToken ct)
    {
        var path = inst.Kind == ArrKind.Radarr
            ? item.TmdbId is > 0 ? $"movie?tmdbId={item.TmdbId}" : $"movie?imdbId={Uri.EscapeDataString(item.ImdbId!)}"
            : $"series?tvdbId={item.TvdbId}";

        var root = await GetJsonAsync(inst, path, ct);
        return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0;
    }

    /// <inheritdoc />
    public async Task<ArrTarget?> ResolveTargetAsync(ArrInstance inst, CancellationToken ct)
    {
        if (_targets.TryGetValue(inst.Name, out var cached) && DateTimeOffset.UtcNow - cached.At < TargetTtl)
            return cached.Target;

        ArrTarget? target = null;
        try
        {
            var profile = await ResolveProfileAsync(inst, ct);
            if (profile is null)
            {
                // Loud on purpose. Falling back to "the first profile" would add titles with a
                // quality the user never chose, they would never satisfy the cutoff, and the
                // racer would then chase releases that get rejected — a failure that takes weeks
                // to notice instead of showing up as a counter the same minute.
                log.LogWarning("{Instance}: no quality profile named {Profile}; watchlist adds refused until one exists",
                    inst.Name, options.WatchlistProfile);
            }
            else
            {
                var root = await ResolveRootAsync(inst, ct);
                target = root is null ? null : new ArrTarget(profile.Value, root, await ResolveTagAsync(inst, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning("{Instance}: cannot resolve watchlist add target ({Reason})", inst.Name, ex.GetType().Name);
        }

        _targets[inst.Name] = (target, DateTimeOffset.UtcNow);
        return target;
    }

    private async Task<int?> ResolveProfileAsync(ArrInstance inst, CancellationToken ct)
    {
        var profiles = await GetJsonAsync(inst, "qualityprofile", ct);
        if (profiles.ValueKind != JsonValueKind.Array) return null;
        foreach (var p in profiles.EnumerateArray())
        {
            var name = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var id = p.TryGetProperty("id", out var i) && i.TryGetInt32(out var pid) ? pid : (int?)null;
            if (id is not null && string.Equals(name, options.WatchlistProfile, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return null;
    }

    private async Task<string?> ResolveRootAsync(ArrInstance inst, CancellationToken ct)
    {
        var wanted = inst.Kind == ArrKind.Radarr ? options.WatchlistMoviesRoot : options.WatchlistShowsRoot;
        var folders = await GetJsonAsync(inst, "rootfolder", ct);
        if (folders.ValueKind != JsonValueKind.Array) return null;

        string? firstAccessible = null;
        foreach (var f in folders.EnumerateArray())
        {
            if (f.TryGetProperty("accessible", out var acc) && acc.ValueKind == JsonValueKind.False) continue;
            var path = f.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (wanted is not null && string.Equals(path.TrimEnd('/'), wanted.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return path;
            firstAccessible ??= path;
        }

        if (wanted is not null)
        {
            // An explicitly configured root that the *arr cannot see is a mount problem. Writing
            // to it anyway would produce a library entry that can never import, which is worse
            // than not adding: it looks like a download failure forever after.
            log.LogWarning("{Instance}: configured root {Root} is not offered by the {Kind} API; adds refused",
                inst.Name, wanted, inst.Kind);
            return null;
        }
        return firstAccessible;
    }

    /// <summary>
    /// Find or create the provenance tag. Failure is non-fatal by design: tags answer "what added
    /// this?" later, and losing that is a poor reason to refuse the title the user asked for.
    /// </summary>
    private async Task<int?> ResolveTagAsync(ArrInstance inst, CancellationToken ct)
    {
        try
        {
            var label = options.WatchlistTag;
            var tags = await GetJsonAsync(inst, "tag", ct);
            if (tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tags.EnumerateArray())
                {
                    var name = t.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                    if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase)
                        && t.TryGetProperty("id", out var id) && id.TryGetInt32(out var tid))
                        return tid;
                }
            }

            var req = Build(HttpMethod.Post, inst, "tag");
            req.Content = JsonContent.Create(new Dictionary<string, object> { ["label"] = label });
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var created = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return created.RootElement.TryGetProperty("id", out var nid) && nid.TryGetInt32(out var ntid) ? ntid : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "{Instance}: could not ensure watchlist tag", inst.Name);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ArrMutationResult> AddAsync(ArrInstance inst, WatchlistItem item, ArrTarget target, CancellationToken ct)
    {
        // Shapes are the ones the *arr UI posts. Field names are NOT symmetric between the two:
        // Radarr keys on tmdbId, Sonarr on tvdbId; Radarr takes minimumAvailability, Sonarr takes
        // monitor + seasonFolder + seriesType. Sending the other product's fields is silently
        // ignored, which is why this is spelled out per kind instead of shared.
        Dictionary<string, object?> body = inst.Kind == ArrKind.Radarr
            ? new Dictionary<string, object?>
            {
                ["tmdbId"] = item.TmdbId,
                ["title"] = item.Title,
                ["year"] = item.Year,
                ["qualityProfileId"] = target.QualityProfileId,
                ["rootFolderPath"] = target.RootFolder,
                ["monitored"] = true,
                ["minimumAvailability"] = "released",
                // A watchlist entry is already released by definition of being watchable in Plex;
                // "announced"/"inCinemas" here would make Radarr grab prereleases.
                ["addOptions"] = new Dictionary<string, object>
                {
                    ["searchForMissing"] = options.WatchlistSearchOnAdd,
                },
            }
            : new Dictionary<string, object?>
            {
                ["tvdbId"] = item.TvdbId,
                ["title"] = item.Title,
                ["year"] = item.Year,
                ["qualityProfileId"] = target.QualityProfileId,
                ["rootFolderPath"] = target.RootFolder,
                ["monitored"] = true,
                ["seasonFolder"] = true,
                ["seriesType"] = "standard",
                ["useSceneNumbering"] = false,
                ["addOptions"] = new Dictionary<string, object>
                {
                    ["searchForExistingEpisodes"] = options.WatchlistSearchOnAdd,
                    ["monitorNewItems"] = "same",
                },
            };

        if (item.ImdbId is not null) body["imdbId"] = item.ImdbId;
        if (target.TagId is not null) body["tags"] = new[] { target.TagId.Value };

        var req = Build(HttpMethod.Post, inst, inst.Kind == ArrKind.Radarr ? "movie" : "series");
        req.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(req, ct);

        // 4xx means our payload or id is wrong for THIS title, so the caller should back it off;
        // 5xx could be transient, and is reported with its status so it stays distinguishable.
        return new ArrMutationResult(resp.IsSuccessStatusCode, (int)resp.StatusCode);
    }

    /// <summary>Drop cached targets (tests; also called if an add is rejected as invalid).</summary>
    public void InvalidateTargets() => _targets.Clear();
}
