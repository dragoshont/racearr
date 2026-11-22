using System.Collections.Concurrent;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Live intake state, read by the metrics layer at scrape time.
/// </summary>
/// <remarks>
/// Last-add stamps are seeded to process start rather than left unset, so a freshly deployed pod
/// reports a small age and does not page. The alternative — a sentinel for "never added" — makes a
/// brand-new deployment indistinguishable from a front door that has been shut for a week, and the
/// one thing this feature is supposed to buy is that "stuck" and "just started" are different signals.
/// </remarks>
public sealed class WatchlistIntakeState
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAdd = new(StringComparer.OrdinalIgnoreCase);

    public long Loops;
    public DateTimeOffset? LastProbeUtc { get; set; }
    public DateTimeOffset? LastLoopUtc { get; set; }

    /// <summary>Titles parked by <see cref="IntakeBackoff"/>. Exported because a parked title is
    /// exactly the shape of the outage this replaced: quietly delivering nothing, no page.</summary>
    public int Parked { get; set; }

    public void Seed(string instance, DateTimeOffset at) => _lastAdd.TryAdd(instance, at);
    public void RecordAdd(string instance, DateTimeOffset at) => _lastAdd[instance] = at;

    public double LastAddAgeSeconds(string instance, DateTimeOffset now)
        => _lastAdd.TryGetValue(instance, out var at) ? Math.Max(0, (now - at).TotalSeconds) : 0;
}

/// <summary>
/// The Plex watchlist → *arr front door: poll the feed, route each title by its
/// <c>category</c>, look it up in the target *arr by external id, and add it if absent.
/// </summary>
/// <remarks>
/// <para>Written as a <see cref="BackgroundService"/> over a <see cref="PeriodicTimer"/> because
/// that is already how the race loop and <see cref="InsightsRefreshService"/> run — one process,
/// one metrics port, no second workload whose own death would need its own monitoring. The
/// consolidation is deliberate: this replaced a dedicated Deployment whose silence was the
/// incident.</para>
///
/// <para><b>What this must never do</b> is confuse three states: feed unreadable, feed empty, and
/// nothing to add. The 2026-11-22 outage was invisible precisely because "the pipeline is idle" and
/// "the pipeline is broken" produced the same picture. Each tick therefore publishes an explicit
/// per-feed probe result, and a failed feed publishes <c>items = -1</c> rather than 0, so
/// "unreadable" can never be mistaken for "the user wants nothing tonight" — either in a query or
/// in someone's glance at a dashboard at midnight.</para>
/// </remarks>
public sealed class WatchlistIntakeService(
    RacearrOptions options,
    IPlexFeedSource feeds,
    IWatchlistArrGateway gateway,
    RacearrMetrics metrics,
    WatchlistIntakeState state,
    ILogger<WatchlistIntakeService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.HasWatchlist)
        {
            // Explicit and info-level: "intake is off" has to be a stated state, not the absence
            // of evidence. This is the distinction the last outage hinged on.
            log.LogInformation("watchlist intake disabled (WATCHLIST_ENABLED={Enabled}, feeds={Feeds})",
                options.WatchlistEnabled, options.WatchlistFeedIds.Count);
            return;
        }

        var instances = ArrInstance.FromOptions(options);
        var radarr = instances.FirstOrDefault(i => i.Kind == ArrKind.Radarr);
        var sonarr = instances.FirstOrDefault(i => i.Kind == ArrKind.Sonarr);
        if (radarr is null && sonarr is null)
        {
            log.LogWarning("watchlist intake enabled but no *arr instance is configured; nothing will be added");
            return;
        }

        var seed = DateTimeOffset.UtcNow;
        if (radarr is not null) state.Seed(radarr.Name, seed);
        if (sonarr is not null) state.Seed(sonarr.Name, seed);

        var backoff = new IntakeBackoff(
            TimeSpan.FromMinutes(Math.Max(1, options.WatchlistBackoffMinutes)),
            maxAttempts: options.WatchlistMaxAttempts);

        var period = TimeSpan.FromSeconds(options.WatchlistIntervalSeconds);
        log.LogInformation("watchlist intake started: {Feeds} feed(s), every {Seconds}s, dry-run={DryRun}, profile={Profile}",
            options.WatchlistFeedIds.Count, options.WatchlistIntervalSeconds, state is not null && options.DryRun, options.WatchlistProfile);

        using var timer = new PeriodicTimer(period);
        do
        {
            try
            {
                await TickAsync(radarr, sonarr, backoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // One malformed response must never end the loop; the loop ending is the failure
                // mode that looks like an idle pipeline.
                log.LogWarning(ex, "watchlist intake tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One poll of every feed, followed by at most one action per unrouted-free item.</summary>
    internal async Task TickAsync(
        ArrInstance? radarr, ArrInstance? sonarr, IntakeBackoff backoff, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var all = new List<WatchlistItem>();
        var anyReadable = false;

        for (var i = 0; i < options.WatchlistFeedIds.Count; i++)
        {
            // Labels are positional, never the id: the id is a read credential and must not be
            // recoverable from a metric, a log line or a Grafana panel.
            var label = $"feed{i + 1}";
            var result = await feeds.FetchAsync(options.WatchlistFeedIds[i], ct);

            metrics.WatchlistProbeSuccess.WithLabels(label).Set(result.Ok ? 1 : 0);
            metrics.WatchlistHttpStatus.WithLabels(label).Set(result.StatusCode);

            if (!result.Ok)
            {
                // -1, not 0. See the class remarks: empty and unreadable must not collide.
                metrics.WatchlistItems.WithLabels(label).Set(-1);
                continue;
            }

            IReadOnlyList<WatchlistItem> items;
            try
            {
                items = PlexWatchlistFeed.Parse(result.Body!, label);
            }
            catch (Exception ex)
            {
                // Valid HTTP, unparseable body (a challenge page, a truncated transfer). Treat as
                // a failed probe, not an empty watchlist.
                log.LogWarning("watchlist {Feed} body unparseable ({Reason}, {Length} bytes)",
                    label, ex.GetType().Name, result.Body!.Length);
                metrics.WatchlistProbeSuccess.WithLabels(label).Set(0);
                metrics.WatchlistItems.WithLabels(label).Set(-1);
                continue;
            }

            metrics.WatchlistItems.WithLabels(label).Set(items.Count);
            all.AddRange(items);
            anyReadable = true;
        }

        state.Loops++;
        state.LastLoopUtc = now;
        if (anyReadable) state.LastProbeUtc = now;
        state.Parked = backoff.Parked.Count;

        if (!anyReadable) return;   // nothing authoritative this tick; leave every title untouched

        foreach (var item in PlexWatchlistFeed.Dedupe(all))
        {
            var instance = item.Kind == WatchlistKind.Movie ? radarr : sonarr;
            await HandleAsync(item, instance, backoff, now, ct);
        }
    }

    private async Task HandleAsync(
        WatchlistItem item, ArrInstance? instance, IntakeBackoff backoff, DateTimeOffset now, CancellationToken ct)
    {
        // The instance label falls back to the kind so a decision still gets counted when the
        // target is simply not deployed (e.g. a Radarr-only install receiving show entries).
        var arrLabel = instance?.Name ?? (item.Kind == WatchlistKind.Movie ? "radarr" : "sonarr");

        if (item.Kind == WatchlistKind.Unsupported)
        {
            metrics.WatchlistAdds.WithLabels(arrLabel, "unsupported").Inc();
            log.LogInformation("watchlist item not routable (category unknown): {Title}", item.Display);
            return;
        }

        if (instance is null)
        {
            metrics.WatchlistAdds.WithLabels(arrLabel, "no_instance").Inc();
            return;
        }

        if (!item.HasArrKey)
        {
            metrics.WatchlistAdds.WithLabels(arrLabel, "no_identity").Inc();
            log.LogWarning("watchlist item has no usable external id: {Title}", item.Display);
            return;
        }

        if (!backoff.ShouldAttempt(item.Key, now))
        {
            // Two different states, therefore two outcomes. "deferred" is a healthy retry window
            // and needs nobody's attention; "parked" means attempts are exhausted and this title
            // will never be added unless a human intervenes. Collapsing them makes a routine
            // backoff window read as permanent data loss on a dashboard — and, worse, buries real
            // parking among routine deferrals so nobody can page on the thing that matters.
            var outcome = backoff.IsParked(item.Key) ? "parked" : "deferred";
            metrics.WatchlistAdds.WithLabels(arrLabel, outcome).Inc();
            // Deliberately no per-tick log: the gauge and counter are the signal, and a warning
            // every interval is how a real error drowns.
            return;
        }

        bool present;
        try
        {
            present = await gateway.ExistsAsync(instance, item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Lookup failed: skip. Never treat an unknown as absent (see class remarks).
            metrics.WatchlistAdds.WithLabels(arrLabel, "lookup_failed").Inc();
            backoff.RecordFailure(item.Key, now);
            log.LogWarning("could not check {Arr} for {Title} ({Reason})", arrLabel, item.Display, ex.GetType().Name);
            return;
        }

        var action = WatchlistPolicy.Decide(item, present, options.DryRun);
        switch (action)
        {
            case IntakeAction.AlreadyPresent:
                metrics.WatchlistAdds.WithLabels(arrLabel, "already_present").Inc();
                backoff.RecordSuccess(item.Key);
                return;

            case IntakeAction.ObserveAdd:
                metrics.WatchlistAdds.WithLabels(arrLabel, "dry_run").Inc();
                log.LogInformation("DRY-RUN would add to {Arr}: {Title} [{Ids}]", arrLabel, item.Display, Ids(item));
                return;

            case IntakeAction.Add:
                break;

            default:
                return;
        }

        var target = await gateway.ResolveTargetAsync(instance, ct);
        if (target is null)
        {
            metrics.WatchlistAdds.WithLabels(arrLabel, "no_target").Inc();
            backoff.RecordFailure(item.Key, now);
            return;
        }

        var result = await gateway.AddAsync(instance, item, target, ct);
        if (result.Succeeded)
        {
            metrics.WatchlistAdds.WithLabels(arrLabel, "added").Inc();
            state.RecordAdd(arrLabel, DateTimeOffset.UtcNow);
            backoff.RecordSuccess(item.Key);
            log.LogInformation("added to {Arr}: {Title} [{Ids}]", arrLabel, item.Display, Ids(item));
            return;
        }

        metrics.WatchlistAdds.WithLabels(arrLabel, $"rejected_{result.StatusCode}").Inc();
        backoff.RecordFailure(item.Key, now);
        log.LogWarning("add to {Arr} rejected {Title} with HTTP {Status}", arrLabel, item.Display, result.StatusCode);
    }

    /// <summary>Ids are safe to log and are the first thing needed when a title goes missing;
    /// the feed id is not, and is never included.</summary>
    private static string Ids(WatchlistItem item)
        => string.Join('/', new[] { item.ImdbId, item.TmdbId is > 0 ? $"tmdb-{item.TmdbId}" : null, item.TvdbId is > 0 ? $"tvdb-{item.TvdbId}" : null }
            .Where(s => s is not null));
}
