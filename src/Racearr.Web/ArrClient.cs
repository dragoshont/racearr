using System.Net.Http.Json;
using System.Text.Json;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Talks to a Radarr/Sonarr instance over the stable v3 API. Maps the nested v3 JSON into the
/// flat <see cref="Racearr.Core"/> domain records so the engine and decision logic stay clean.
/// Mutations (grab / delete-from-queue / forced search) flow only through here.
/// </summary>
public sealed class ArrClient(HttpClient http) : IArrClient
{
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

    public async Task<IReadOnlyList<QueueRecord>> GetQueueAsync(ArrInstance inst, CancellationToken ct)
    {
        var query = inst.Kind == ArrKind.Radarr
            ? "queue?includeUnknownMovieItems=false"
            : "queue?includeUnknownSeriesItems=true";
        var records = await GetPagedRecordsAsync(inst, query, 400, ct);
        var list = new List<QueueRecord>();
        foreach (var r in records)
        {
            list.Add(new QueueRecord
            {
                Id = GetInt(r, "id") ?? 0,
                ItemId = GetInt(r, inst.ItemField),
                DownloadId = (GetStr(r, "downloadId") ?? "").ToLowerInvariant(),
                Title = GetStr(r, "title") ?? "",
                Indexer = GetStr(r, "indexer") ?? "",
                Size = GetLong(r, "size") ?? 0,
                SizeLeft = GetLong(r, "sizeleft") ?? 0,
                TrackedDownloadState = GetStr(r, "trackedDownloadState"),
                TrackedDownloadStatus = GetStr(r, "trackedDownloadStatus"),
                TimeLeftSeconds = GetTimeLeftSeconds(r),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<WantedItem>> GetWantedMissingAsync(ArrInstance inst, CancellationToken ct)
    {
        var records = await GetPagedRecordsAsync(inst,
            "wanted/missing?sortDirection=descending&sortKey=id&monitored=true", 200, ct);
        var list = new List<WantedItem>();
        foreach (var r in records)
        {
            var id = GetInt(r, "id")
                ?? throw new InvalidDataException("Paged *arr response contained a record without an integer id.");
            var title = GetStr(r, "title")
                ?? (r.TryGetProperty("movie", out var m) ? GetStr(m, "title") : null)
                ?? "?";
            list.Add(new WantedItem(id, title));
        }
        return list;
    }

    private async Task<IReadOnlyList<JsonElement>> GetPagedRecordsAsync(
        ArrInstance inst, string pathAndQuery, int pageSize, CancellationToken ct)
    {
        var records = new List<JsonElement>();
        var ids = new HashSet<int>();
        int? expectedTotal = null;

        for (var page = 1; ; page++)
        {
            var root = await GetJsonAsync(inst, $"{pathAndQuery}&page={page}&pageSize={pageSize}", ct);
            var total = GetInt(root, "totalRecords")
                ?? throw new InvalidDataException("Paged *arr response did not contain totalRecords.");
            expectedTotal ??= total;
            if (total != expectedTotal)
                throw new InvalidDataException("Paged *arr response changed totalRecords while it was being read.");
            if (!root.TryGetProperty("records", out var pageRecords) || pageRecords.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Paged *arr response did not contain a records array.");

            var rawCount = 0;
            var added = 0;
            foreach (var record in pageRecords.EnumerateArray())
            {
                rawCount++;
                var id = GetInt(record, "id")
                    ?? throw new InvalidDataException("Paged *arr response contained a record without an integer id.");
                if (ids.Add(id))
                {
                    records.Add(record.Clone());
                    added++;
                }
            }

            if (records.Count == expectedTotal) return records;
            if (records.Count > expectedTotal || rawCount == 0 || added == 0 || rawCount < pageSize)
                throw new InvalidDataException(
                    $"Paged *arr response was incomplete: received {records.Count} of {expectedTotal} unique records.");
        }
    }

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(ArrInstance inst, int itemId, CancellationToken ct)
    {
        var root = await GetJsonAsync(inst, $"release?{inst.SearchParam}={itemId}", ct);
        var list = new List<Release>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in root.EnumerateArray())
            {
                var rejections = new List<string>();
                if (r.TryGetProperty("rejections", out var rj) && rj.ValueKind == JsonValueKind.Array)
                    foreach (var x in rj.EnumerateArray())
                        rejections.Add(x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString());

                var res = 0;
                var qname = "?";
                if (r.TryGetProperty("quality", out var qroot) && qroot.TryGetProperty("quality", out var qq))
                {
                    res = GetInt(qq, "resolution") ?? 0;
                    qname = GetStr(qq, "name") ?? "?";
                }

                list.Add(new Release
                {
                    Protocol = GetStr(r, "protocol") ?? "",
                    Seeders = GetInt(r, "seeders") ?? 0,
                    Resolution = res,
                    QualityName = qname,
                    Rejected = GetBool(r, "rejected") ?? false,
                    Rejections = rejections,
                    Indexer = GetStr(r, "indexer") ?? "",
                    IndexerId = GetInt(r, "indexerId"),
                    InfoHash = GetStr(r, "infoHash") ?? "",
                    Guid = GetStr(r, "guid") ?? "",
                    Title = GetStr(r, "title") ?? "",
                    Size = GetLong(r, "size") ?? 0,
                });
            }
        }
        return list;
    }

    public async Task<ArrMutationResult> ForceSearchAsync(ArrInstance inst, int itemId, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["name"] = inst.SearchCommand, [inst.SearchIdsField] = new[] { itemId } };
        var req = Build(HttpMethod.Post, inst, "command");
        req.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(req, ct);
        return new ArrMutationResult(resp.IsSuccessStatusCode, (int)resp.StatusCode);
    }

    public async Task<GrabResult> GrabAsync(ArrInstance inst, int itemId, Release release, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["guid"] = release.Guid, ["indexerId"] = release.IndexerId };
        using var req = Build(HttpMethod.Post, inst, "release");
        req.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return new GrabResult(GrabOutcome.Accepted, (int)resp.StatusCode);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            return new GrabResult(GrabOutcome.AlreadyPresent, (int)resp.StatusCode);

        // Sonarr currently wraps qBittorrent's duplicate-add 409 as a generic 500. Reconcile against
        // the live queue before classifying the response; never infer "duplicate" from the 500 alone.
        try
        {
            var queued = await GetQueueAsync(inst, ct);
            var alreadyPresent = queued.Where(q => q.ItemId == itemId).Any(q =>
                (!string.IsNullOrEmpty(release.InfoHash) &&
                 string.Equals(q.DownloadId, release.InfoHash, StringComparison.OrdinalIgnoreCase)) ||
                (string.IsNullOrEmpty(release.InfoHash) && RaceDecisions.IsSameRelease(release, q)));
            if (alreadyPresent) return new GrabResult(GrabOutcome.AlreadyPresent, (int)resp.StatusCode);
        }
        catch
        {
            // Preserve the original classified failure when reconciliation itself is unavailable.
        }

        var outcome = resp.StatusCode is System.Net.HttpStatusCode.BadRequest or
            System.Net.HttpStatusCode.NotAcceptable or System.Net.HttpStatusCode.UnprocessableEntity
            ? GrabOutcome.Rejected
            : GrabOutcome.Failed;
        return new GrabResult(outcome, (int)resp.StatusCode);
    }

    public async Task<ArrMutationResult> DeleteQueueAsync(ArrInstance inst, int recordId, bool removeFromClient, bool blocklist, CancellationToken ct)
    {
        var query = $"queue/{recordId}?removeFromClient={(removeFromClient ? "true" : "false")}" +
                    $"&blocklist={(blocklist ? "true" : "false")}&skipRedownload=true";
        using var resp = await http.SendAsync(Build(HttpMethod.Delete, inst, query), ct);
        return new ArrMutationResult(resp.IsSuccessStatusCode, (int)resp.StatusCode);
    }

    public async Task<LibraryStats> GetLibraryStatsAsync(ArrInstance inst, CancellationToken ct)
    {
        // Radarr /movie and Sonarr /series each return the whole library as one array.
        var root = await GetJsonAsync(inst, inst.Kind == ArrKind.Radarr ? "movie" : "series", ct);
        var total = 0;
        var downloaded = 0;
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var e in root.EnumerateArray())
            {
                total++;
                if (inst.Kind == ArrKind.Radarr)
                {
                    if (GetBool(e, "hasFile") == true) downloaded++;
                }
                else if (e.TryGetProperty("statistics", out var st) && (GetInt(st, "episodeFileCount") ?? 0) > 0)
                {
                    downloaded++;
                }
            }
        return new LibraryStats(inst.Name, total, downloaded);
    }

    public async Task<PlexLinkStatus> GetPlexLinkStatusAsync(ArrInstance inst, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            root = await GetJsonAsync(inst, "notification", ct);
        }
        catch (Exception ex)
        {
            return new PlexLinkStatus(inst.Name, Reachable: false, Configured: false, NotifiesOnImport: false, ex.Message);
        }

        if (root.ValueKind == JsonValueKind.Array)
            foreach (var n in root.EnumerateArray())
            {
                if (!string.Equals(GetStr(n, "implementation"), "PlexServer", StringComparison.OrdinalIgnoreCase)) continue;

                // Plex refreshes on import (onDownload) or on a quality upgrade (onUpgrade).
                var onImport = (GetBool(n, "onDownload") ?? false) || (GetBool(n, "onUpgrade") ?? false);
                string? host = null;
                if (n.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                    foreach (var f in fields.EnumerateArray())
                        if (string.Equals(GetStr(f, "name"), "host", StringComparison.OrdinalIgnoreCase))
                            host = GetStr(f, "value");
                var where = host is null ? "Plex" : $"Plex ({host})";
                var detail = onImport
                    ? $"{where} is refreshed on import."
                    : $"{where} connection exists but its On Import / On Upgrade events are off.";
                return new PlexLinkStatus(inst.Name, true, true, onImport, detail);
            }

        return new PlexLinkStatus(inst.Name, true, false, false, "No Plex Media Server connection is configured.");
    }

    private static string? GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static long? GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
    private static bool? GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    // *arr reports queue timeleft as "HH:MM:SS" or "d.HH:MM:SS"; absent/empty when it can't estimate.
    private static double? GetTimeLeftSeconds(JsonElement e)
        => e.TryGetProperty("timeleft", out var v) && v.ValueKind == JsonValueKind.String
            && TimeSpan.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var ts)
            ? ts.TotalSeconds : null;
}
