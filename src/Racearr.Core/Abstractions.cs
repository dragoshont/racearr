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
