namespace Racearr.Core;

/// <summary>
/// Read/write access to an *arr instance's v3 API. All engine mutations flow through here
/// (grab, delete-from-queue, forced search) — the engine never talks to a download client
/// for mutations, exactly like the Python service.
/// </summary>
public interface IArrClient
{
    Task<IReadOnlyList<QueueRecord>> GetQueueAsync(ArrInstance inst, CancellationToken ct);
    Task<IReadOnlyList<WantedItem>> GetWantedMissingAsync(ArrInstance inst, CancellationToken ct);
    Task<IReadOnlyList<Release>> GetReleasesAsync(ArrInstance inst, int itemId, CancellationToken ct);
    Task<ArrMutationResult> ForceSearchAsync(ArrInstance inst, int itemId, CancellationToken ct);
    /// <summary>Force a Sonarr season-pack search — the pack-correct replacement for a dead season pack
    /// (a pack is never raced episode-by-episode). Returns failure for Radarr, which has no seasons.</summary>
    Task<ArrMutationResult> SeasonSearchAsync(ArrInstance inst, int seriesId, int seasonNumber, CancellationToken ct);
    /// <summary>Force-grab a release (bypasses the "already meets cutoff" auto-rejection). Returns success.</summary>
    Task<GrabResult> GrabAsync(ArrInstance inst, int itemId, Release release, CancellationToken ct);
    /// <summary>Remove a queue record; <paramref name="removeFromClient"/> also deletes + blocklists the torrent.</summary>
    Task<ArrMutationResult> DeleteQueueAsync(ArrInstance inst, int recordId, bool removeFromClient, bool blocklist, CancellationToken ct);

    /// <summary>Library size (movies for Radarr, series for Sonarr) for the dashboard impact summary.</summary>
    Task<LibraryStats> GetLibraryStatsAsync(ArrInstance inst, CancellationToken ct);

    /// <summary>Whether this instance is wired to refresh Plex on import (the "Plex Media Server" connection).</summary>
    Task<PlexLinkStatus> GetPlexLinkStatusAsync(ArrInstance inst, CancellationToken ct);
}

/// <summary>Read-only view of the download client, used only to read live per-torrent speed.</summary>
public interface IQbitClient
{
    /// <summary>Torrents keyed by lowercase infohash.</summary>
    Task<TorrentSnapshot> GetByHashAsync(CancellationToken ct);
}

/// <summary>
/// The metric side-effects the engine emits. Implemented in the web host by the prometheus-net
/// surface (<c>RacearrMetrics</c>); kept as an interface so <c>Racearr.Core</c> has no web deps
/// and the engine is unit-testable with a no-op sink.
/// </summary>
public interface IEngineMetrics
{
    void IncIncident(string type);
    void ObservePickupLatency(double seconds);
    void IncPickup(string instance, string result);
    void IncRaceStarted(string instance);
    void IncRaceAttempt(string instance, string outcome);
    void IncCandidatesGrabbed(string instance, double count);
    void IncLosersKilled(string instance);
    void IncReachedTarget(string instance);
    void ObserveTimeToTarget(double seconds);
    void ObserveRaceWinnerMbps(double mbps);
    void IncRaceOutcome(string instance, string outcome);
}

/// <summary>A no-op metric sink for tests and metric-less runs.</summary>
public sealed class NullEngineMetrics : IEngineMetrics
{
    public static readonly NullEngineMetrics Instance = new();
    public void IncIncident(string type) { }
    public void ObservePickupLatency(double seconds) { }
    public void IncPickup(string instance, string result) { }
    public void IncRaceStarted(string instance) { }
    public void IncRaceAttempt(string instance, string outcome) { }
    public void IncCandidatesGrabbed(string instance, double count) { }
    public void IncLosersKilled(string instance) { }
    public void IncReachedTarget(string instance) { }
    public void ObserveTimeToTarget(double seconds) { }
    public void ObserveRaceWinnerMbps(double mbps) { }
    public void IncRaceOutcome(string instance, string outcome) { }
}

/// <summary>Result of a connection reachability test: success + a short human-readable message.</summary>
public sealed record ConnectionTestResult(bool Ok, string Message);

/// <summary>Tests whether a configured service connection is reachable (drives the Connections UI "Test").</summary>
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(Connection connection, CancellationToken ct = default);
}

/// <summary>Outcome of one feed fetch. A non-OK result is data, not an exception: "empty",
/// "rejected" and "unreachable" must stay distinguishable all the way to a graph.</summary>
public sealed record PlexFeedResult(int StatusCode, string? Body, string? Error = null)
{
    /// <summary>2xx with a body. Everything else — including 3xx after redirect exhaustion,
    /// which is what a wedged redirect chain looks like from here — is a failed probe.</summary>
    public bool Ok => StatusCode is >= 200 and < 300 && Body is not null;

    public static PlexFeedResult Delivered(int status, string body) => new(status, body);
    public static PlexFeedResult Rejected(int status, string body) => new(status, body);
    public static PlexFeedResult TransportFailure(int status, string error) => new(status, null, error);
}

/// <summary>Transport for a Plex watchlist RSS feed. Kept off the arr client so the one
/// internet-facing call in the process has its own timeout, its own log text and its own tests.</summary>
public interface IPlexFeedSource
{
    Task<PlexFeedResult> FetchAsync(string feedId, CancellationToken ct);
}

/// <summary>Where an add is allowed to land: a profile that exists by name and a root folder the
/// *arr can actually see. There is deliberately no implicit default in this record — if either is
/// missing the caller refuses the add rather than picking one.</summary>
public sealed record ArrTarget(int QualityProfileId, string RootFolder, int? TagId);

/// <summary>The read/add surface the watchlist intake needs from a *arr. Separate from
/// <see cref="IArrClient"/> so growing the intake path cannot destabilise the race engine, which
/// shares neither the endpoints nor the failure semantics.</summary>
public interface IWatchlistArrGateway
{
    /// <summary>Is this title already in the library? Throws when the lookup did not succeed —
    /// an unknown must never be reported as absent, because absent means "POST an add".</summary>
    Task<bool> ExistsAsync(ArrInstance inst, WatchlistItem item, CancellationToken ct);

    /// <summary>Resolve (and cache) the profile / root folder / tag for adds, or null when the
    /// configured target does not exist on this instance.</summary>
    Task<ArrTarget?> ResolveTargetAsync(ArrInstance inst, CancellationToken ct);

    Task<ArrMutationResult> AddAsync(ArrInstance inst, WatchlistItem item, ArrTarget target, CancellationToken ct);
}
