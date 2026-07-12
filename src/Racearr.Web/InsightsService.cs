using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Periodically-refreshed, cached snapshot of the "value" data the dashboard shows but that is too
/// slow to fetch on every 2-second UI tick: library sizes (Radarr/Sonarr) and the Plex-refresh
/// health of each *arr instance. Held in one place so the page renders instantly and a manual
/// "re-check" can force a refresh.
/// </summary>
public sealed class InsightsService(IArrClient arr, RacearrOptions options, ILogger<InsightsService> log)
{
    private readonly IReadOnlyList<ArrInstance> _instances = ArrInstance.FromOptions(options);
    private volatile Insights _current = Insights.Empty;

    /// <summary>The latest cached insights (never null; <see cref="Insights.Empty"/> before the first refresh).</summary>
    public Insights Current => _current;

    /// <summary>Refetch library sizes + Plex-link health for every configured instance and cache the result.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var library = new List<LibraryStats>();
        var plex = new List<PlexLinkStatus>();
        foreach (var inst in _instances)
        {
            try { library.Add(await arr.GetLibraryStatsAsync(inst, ct)); }
            catch (Exception ex) { log.LogDebug(ex, "library stats failed for {Kind}", inst.Name); }
            try { plex.Add(await arr.GetPlexLinkStatusAsync(inst, ct)); }
            catch (Exception ex) { log.LogDebug(ex, "plex link check failed for {Kind}", inst.Name); }
        }
        _current = new Insights(library, plex, DateTimeOffset.UtcNow);
    }
}

/// <summary>Cached dashboard insights: library sizes + Plex-refresh health, with derived roll-ups.</summary>
public sealed record Insights(
    IReadOnlyList<LibraryStats> Library,
    IReadOnlyList<PlexLinkStatus> PlexLinks,
    DateTimeOffset? UpdatedUtc)
{
    public static readonly Insights Empty = new([], [], null);

    public int Movies => Library.Where(l => l.Instance == "radarr").Sum(l => l.Total);
    public int Shows => Library.Where(l => l.Instance == "sonarr").Sum(l => l.Total);
    public int MoviesDownloaded => Library.Where(l => l.Instance == "radarr").Sum(l => l.Downloaded);
    public int ShowsDownloaded => Library.Where(l => l.Instance == "sonarr").Sum(l => l.Downloaded);

    /// <summary>Plex links we can reach that won't refresh Plex on import — the actionable fixes.
    /// Unreachable instances are excluded (that is a connection problem, handled elsewhere).</summary>
    public IReadOnlyList<PlexLinkStatus> PlexIssues => PlexLinks.Where(p => p.Reachable && !p.Healthy).ToList();

    /// <summary>Reachable links that will correctly refresh Plex — the healthy ones.</summary>
    public IReadOnlyList<PlexLinkStatus> PlexHealthy => PlexLinks.Where(p => p.Healthy).ToList();
}

/// <summary>Refreshes <see cref="InsightsService"/> on a slow cadence (and once at startup).</summary>
public sealed class InsightsRefreshService(InsightsService insights, ILogger<InsightsRefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try { await insights.RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogDebug(ex, "insights refresh failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
