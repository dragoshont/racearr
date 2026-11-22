using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Racearr.Core;

/// <summary>
/// What a watchlist entry <em>is</em>, after routing. <see cref="Unsupported"/> is deliberate:
/// an entry whose category we do not recognise is skipped and counted, never guessed at. A wrong
/// guess here is not a harmless mistake — it POSTs a real add into the wrong *arr, which then
/// grabs a release for something the user did not ask for in the shape they did not ask for it.
/// </summary>
public enum WatchlistKind { Movie, Series, Unsupported }

/// <summary>
/// One item from a Plex watchlist RSS feed.
/// </summary>
/// <remarks>
/// Plex hands us better identity than this job historically deserved. Every item carries a
/// <c>guids</c> array holding <c>imdb://</c>, <c>tmdb://</c> and <c>tvdb://</c> at once, plus a
/// <c>category</c> of <c>movie</c> or <c>show</c> (verified against the live feed 2026-11-22:
/// 10/10 items carried all three schemes). So the arr lookup is an exact id match and the title
/// is decoration for logs. There is no fuzzy title matching anywhere in this pipeline, and there
/// must never be: "Blade Runner 2099" is a <em>show</em> in this feed, and any title-and-year
/// matcher would have filed it in Radarr next to "Blade Runner 2049".
/// </remarks>
public sealed record WatchlistItem
{
    public required string Title { get; init; }
    public required WatchlistKind Kind { get; init; }

    /// <summary>Which feed this came from, as a stable index label (<c>feed1</c>, …). Never the
    /// UUID: the feed UUID is a read credential and must not reach metrics or logs.</summary>
    public required string Feed { get; init; }

    public int? Year { get; init; }
    public string? ImdbId { get; init; }
    public int? TmdbId { get; init; }
    public int? TvdbId { get; init; }

    /// <summary>The feed's <c>pubDate</c> — for a watchlist that is when the user pressed +.</summary>
    public DateTimeOffset? AddedAt { get; init; }

    public string? Link { get; init; }

    /// <summary>
    /// Whether we hold an id the target *arr can be queried by. Radarr matches TMDb (IMDb is the
    /// fallback; it is unique and stable); Sonarr matches TVDb, which is the id the TV indexers
    /// key on — matching a series by TMDb id in Sonarr would silently miss it.
    /// </summary>
    public bool HasArrKey => Kind switch
    {
        WatchlistKind.Movie => TmdbId is > 0 || ImdbId is not null,
        WatchlistKind.Series => TvdbId is > 0,
        _ => false,
    };

    /// <summary>Stable identity for dedupe across feeds and for retry accounting. Two household
    /// members pressing + on the same title produce two items with one key.</summary>
    public string Key => Kind switch
    {
        WatchlistKind.Movie when TmdbId is > 0 => $"tmdb:{TmdbId}",
        WatchlistKind.Movie when ImdbId is not null => $"imdb:{ImdbId}",
        WatchlistKind.Series when TvdbId is > 0 => $"tvdb:{TvdbId}",
        // Only reachable for items that are already routed away from adding; keeps the key
        // non-null and non-colliding rather than inventing a fake identity.
        _ => $"{Feed}|{AddedAt?.ToUnixTimeSeconds() ?? 0}|{Title}",
    };

    public string Display => Year is > 0 ? $"{Title} ({Year})" : Title;
}

/// <summary>
/// Parsing and routing for the Plex watchlist feed. Pure: takes JSON text, returns items. No
/// HTTP, no clock, no filesystem — so every hostile input Plex could ever put on that feed is
/// unit-testable in a millisecond without a network (see PlexWatchlistTests).
/// </summary>
public static class PlexWatchlistFeed
{
    // Trailing "(2026)" — the feed's own title format. Anchored to the end and required to be
    // four digits so "Ocean's 8 (2018)" yields 2018 while "Blade Runner 2099 (2026)" keeps
    // 2099 in the title. A year is never used to match, only to display, so a title whose
    // parenthetical is not a year is simply left intact.
    private static readonly Regex TrailingYear = new(@"\s*\((\d{4})\)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Parse a <c>?format=json</c> watchlist document.
    /// </summary>
    /// <exception cref="JsonException">The document is not JSON at all, or is JSON in a shape
    /// that carries no <c>items</c> array. Both are feed-level failures (an HTML error page from a
    /// captive portal, a Cloudflare challenge, a truncated body, a revoked feed answering
    /// <c>{}</c>) and the caller must record a probe failure rather than treat it as an empty
    /// watchlist: "empty" clears the stall alert, "unrecognisable" must not. Note that
    /// <c>JsonReaderException</c> derives from <c>JsonException</c>, so catching the base catches
    /// every malformed-input case here.</exception>
    /// <remarks>Individual items are skipped, never fatal. One malformed entry must not cost the
    /// user the other nine, which is the failure mode that made the old integration so quiet:
    /// it surfaced one exception for the whole feed and dropped everything.</remarks>
    public static IReadOnlyList<WatchlistItem> Parse(string json, string feed)
    {
        var items = new List<WatchlistItem>();
        using var doc = JsonDocument.Parse(json);   // throws JsonException on non-JSON: intended

        // Three shapes are accepted; everything else RAISES rather than returning empty.
        // An object with an items array is the documented feed. A bare array is tolerated so a
        // Plex reshuffle in that direction degrades to "works". Anything else — {} , {"code":410},
        // a proxy's JSON error — is a shape we cannot interpret, and interpreting it as
        // "zero watchlist items" is exactly the lie that made the last outage invisible: an empty
        // result satisfies the stall alert. Unreadable must cost a page; idle must not.
        JsonElement arr;
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (root.ValueKind == JsonValueKind.Object
                 && root.TryGetProperty("items", out var declared)
                 && declared.ValueKind == JsonValueKind.Array) arr = declared;
        else
            throw new JsonException(
                $"unrecognised watchlist document shape: {root.ValueKind} with no items array");

        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var title = Str(el, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;   // nothing to match or name by

            var item = Read(el, title!, feed);
            if (item is not null) items.Add(item);
        }

        return items;
    }

    private static WatchlistItem? Read(JsonElement el, string rawTitle, string feed)
    {
        var (title, year) = SplitTitle(rawTitle);
        string? imdb = null;
        int? tmdb = null, tvdb = null;

        if (el.TryGetProperty("guids", out var guids) && guids.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in guids.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.String) continue;
                var s = g.GetString();
                if (s is null) continue;
                var i = s.IndexOf("://", StringComparison.Ordinal);
                if (i <= 0) continue;
                var scheme = s[..i].ToLowerInvariant();
                var value = s[(i + 3)..].Trim();
                switch (scheme)
                {
                    case "imdb" when value.Length > 0: imdb ??= value; break;
                    case "tmdb" when int.TryParse(value, out var t) && t > 0: tmdb ??= t; break;
                    case "tvdb" when int.TryParse(value, out var v) && v > 0: tvdb ??= v; break;
                    // plex://, dbone:// and anything invented later are ignored on purpose:
                    // neither *arr can look them up, and treating "unknown" as "movie" is how
                    // a feed change turns into wrong library adds.
                }
            }
        }

        return new WatchlistItem
        {
            Title = title,
            Year = year,
            ImdbId = imdb,
            TmdbId = tmdb,
            TvdbId = tvdb,
            Kind = Route(Str(el, "category")),
            Feed = feed,
            AddedAt = ParseDate(Str(el, "pubDate")),
            Link = Str(el, "link"),
        };
    }

    /// <summary>
    /// Map the feed's <c>category</c> onto a target. <c>show</c> is what Plex actually emits for
    /// series; <c>series</c> and <c>episode</c> are accepted as belt-and-braces in case the
    /// vocabulary changes under us, because misrouting here is a wrong-library write, not a miss.
    /// </summary>
    public static WatchlistKind Route(string? category)
        => (category ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "movie" => WatchlistKind.Movie,
            "show" or "series" or "episode" => WatchlistKind.Series,
            _ => WatchlistKind.Unsupported,
        };

    /// <summary>Split "Everything Must Go (2010)" into name + year. No match keeps the title
    /// whole and returns no year — the name is what a human reads in a log line.</summary>
    public static (string Title, int? Year) SplitTitle(string raw)
    {
        var m = TrailingYear.Match(raw);
        if (!m.Success) return (raw.Trim(), null);
        return (raw[..m.Index].Trim(), int.TryParse(m.Groups[1].Value, out var y) ? y : null);
    }

    /// <summary>
    /// Collapse duplicates across feeds, keeping the earliest <c>pubDate</c> (the first person to
    /// ask for it owns the entry, and its title is the one we log). Items with no usable id pass
    /// through untouched so <see cref="WatchlistPolicy"/> still counts them as un-routable rather
    /// than silently losing them.
    /// </summary>
    public static IReadOnlyList<WatchlistItem> Dedupe(IEnumerable<WatchlistItem> items)
    {
        var byKey = new Dictionary<string, WatchlistItem>(StringComparer.Ordinal);
        var passthrough = new List<WatchlistItem>();

        foreach (var item in items)
        {
            if (!item.HasArrKey)
            {
                passthrough.Add(item);
                continue;
            }

            if (!byKey.TryGetValue(item.Key, out var existing))
            {
                byKey[item.Key] = item;
                continue;
            }

            // Two feeds, one title: keep whichever the user asked for first. A missing date sorts
            // last, so an undated item never displaces a dated one.
            if (item.AddedAt is not null && existing.AddedAt is null) byKey[item.Key] = item;
            else if (item.AddedAt is { } a && existing.AddedAt is { } b && a < b) byKey[item.Key] = item;
        }

        return byKey.Values.Concat(passthrough).ToList();
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // pubDate is RFC-1123 GMT. Fall back to invariant before giving up, but never to local
        // conventions: an added-at that parses into the wrong zone misorders the dedupe above.
        if (DateTimeOffset.TryParseExact(raw, "R", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rfc))
            return rfc;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso))
            return iso;
        return null;
    }
}

/// <summary>What the intake loop decided to do with one item. Every value is a metric label, so
/// this enumeration is the dashboard's vocabulary for "why didn't that title arrive?".</summary>
public enum IntakeAction
{
    /// <summary>Absent from the *arr and dry-run is off: POST the add.</summary>
    Add,
    /// <summary>Absent from the *arr but observe-only. Counts and logs; performs no write.</summary>
    ObserveAdd,
    /// <summary>Already in the library (or already added by a previous tick). Normal, not an error.</summary>
    AlreadyPresent,
    /// <summary>Category we do not route. Counted, never guessed.</summary>
    Unsupported,
    /// <summary>Routable but no usable external id. Almost certainly a Plex feed change.</summary>
    NoIdentity,
}

/// <summary>
/// Security constants for the feed path, in Core so they can be pinned by a unit test.
/// </summary>
public static class PlexFeedSecurity
{
    /// <summary>
    /// The logger category <c>IHttpClientFactory</c> uses for the Plex feed typed client:
    /// <c>System.Net.Http.HttpClient.&lt;TypedClientInterfaceName&gt;.(Logical|Client)Handler</c>.
    /// </summary>
    /// <remarks>
    /// This category MUST be filtered to Warning or above (see Program.cs). At Information the
    /// framework logs "Starting HTTP request GET {RequestUri}", and a Plex feed's RequestUri path
    /// <b>is</b> the household's watchlist read credential — no token, no cookie required. Left at
    /// Information, every poll writes the credential into the container log, which is how the
    /// previous integration leaked its Plex account token.
    ///
    /// The value is duplicated here rather than written inline so the accompanying test can assert
    /// it still matches <c>nameof(IPlexFeedSource)</c>: rename that interface and the filter
    /// silently stops applying while every test stays green and the credential starts leaking
    /// again. That silent-detachment is the whole reason this is a constant and not a string.
    /// </remarks>
    public const string HttpClientLogCategory = "System.Net.Http.HttpClient.IPlexFeedSource";
}

/// <summary>
/// The one decision function for the intake path, kept pure so the dry-run guarantee is a
/// testable property rather than a code-review promise. CONTRIBUTING requires that an
/// observe-only run "performs no real action"; that is enforced here, not at each call site.
/// </summary>
public static class WatchlistPolicy
{
    public static IntakeAction Decide(WatchlistItem item, bool alreadyPresent, bool dryRun)
    {
        if (item.Kind == WatchlistKind.Unsupported) return IntakeAction.Unsupported;
        if (!item.HasArrKey) return IntakeAction.NoIdentity;
        if (alreadyPresent) return IntakeAction.AlreadyPresent;
        return dryRun ? IntakeAction.ObserveAdd : IntakeAction.Add;
    }
}

/// <summary>
/// Per-item retry suppression.
/// </summary>
/// <remarks>
/// A title that legitimately cannot be added — withdrawn from TMDb, an id the *arr rejects, a
/// series with no TVDb entry — must not be re-POSTed every tick forever. Unbounded retries at
/// 120s are ~30 calls a day per stuck title against an API that rate-limits, and worse, they
/// bury the log lines that matter. So failures back off exponentially and then park: after
/// <c>maxAttempts</c> the item is left alone until the process restarts, and <see cref="Parked"/>
/// feeds a gauge so "3 titles parked" is visible instead of quietly ignored.
///
/// The clock is a parameter rather than <c>DateTimeOffset.UtcNow</c> for exactly the reason the
/// backoff exists: the interesting cases (boundary of the window, the park transition, pruning)
/// have to be tested at the boundary, and you cannot pin a boundary you cannot advance.
/// </remarks>
public sealed class IntakeBackoff(TimeSpan? baseline = null, TimeSpan? cap = null, int maxAttempts = 5)
{
    private readonly TimeSpan _baseline = baseline ?? TimeSpan.FromMinutes(5);
    private readonly TimeSpan _cap = cap ?? TimeSpan.FromHours(6);
    private readonly int _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
    private readonly Dictionary<string, (int Attempts, DateTimeOffset RetryAt)> _state = new(StringComparer.Ordinal);

    /// <summary>Whether this item may be acted on now.</summary>
    public bool ShouldAttempt(string key, DateTimeOffset now)
    {
        if (!_state.TryGetValue(key, out var entry)) return true;
        if (entry.Attempts >= _maxAttempts) return false;       // parked until restart
        return now >= entry.RetryAt;
    }

    /// <summary>Record a failure and schedule the next try. Delay doubles per attempt, capped.</summary>
    public void RecordFailure(string key, DateTimeOffset now)
    {
        var attempts = _state.TryGetValue(key, out var e) ? Math.Min(e.Attempts + 1, _maxAttempts) : 1;
        // 2^(n-1) with the shift clamped: at 20 attempts 1<<19 minutes is already past the cap,
        // and an unclamped shift would overflow to negative and retry immediately forever.
        var shift = Math.Min(attempts - 1, 16);
        var delay = TimeSpan.FromMinutes((double)_baseline.TotalMinutes * (1 << shift));
        if (delay > _cap) delay = _cap;
        _state[key] = (attempts, now + delay);
    }

    /// <summary>Success clears the history, so a title that later becomes addable is retried at
    /// normal cadence rather than inheriting a six-hour penalty.</summary>
    public void RecordSuccess(string key) => _state.Remove(key);

    public int Attempts(string key) => _state.TryGetValue(key, out var e) ? e.Attempts : 0;

    /// <summary>True when this key has exhausted its attempts and will not be retried until the
    /// process restarts. Distinct from "currently inside a backoff window", which is healthy.</summary>
    public bool IsParked(string key) => _state.TryGetValue(key, out var e) && e.Attempts >= _maxAttempts;

    /// <summary>Items permanently parked this process life. Must be exported: a parked item is
    /// the exact shape of the bug this whole component replaced — working quietly, delivering
    /// nothing, and nobody knowing.</summary>
    public IReadOnlyList<string> Parked
        => _state.Where(kv => kv.Value.Attempts >= _maxAttempts).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Forget everything (tests, and re-config at runtime).</summary>
    public void Clear() => _state.Clear();

    public int TrackedCount => _state.Count;
}
