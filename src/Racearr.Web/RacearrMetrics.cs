using Prometheus;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// The Prometheus metric surface. Names, labels and histogram buckets are byte-for-byte
/// compatible with the Python service so the existing Grafana dashboard (uid <c>racearr</c>)
/// and the alert rules keep working unchanged after cutover (ADR-0001, "metric compatibility").
/// Gauges are refreshed at scrape time via a before-collect callback; counters/histograms are
/// emitted by the engine as events occur and pre-initialised to 0 for the known label sets.
/// </summary>
public sealed class RacearrMetrics : IEngineMetrics
{
    // Bucket boundaries copied verbatim from racearr.py.
    private static readonly double[] PickupBuckets = [15, 30, 60, 90, 120, 180, 300, 600];
    private static readonly double[] TimeToTargetBuckets = [30, 60, 90, 120, 180, 300, 600];
    private static readonly double[] MbpsBuckets = [0.5, 1, 2, 3, 5, 8, 16, 32];

    public Gauge Up { get; }
    public Gauge DryRun { get; }
    public Gauge ActiveRaces { get; }
    public Gauge ManagedDownloads { get; }
    public Gauge Loops { get; }
    public Gauge LastLoopAge { get; }

    public Counter Incidents { get; }            // {type}
    public Counter Pickups { get; }              // {instance,result}
    public Counter RacesStarted { get; }         // {instance}
    public Counter RaceAttempts { get; }         // {instance,outcome}
    public Counter CandidatesGrabbed { get; }    // {instance}
    public Counter LosersKilled { get; }         // {instance}
    public Counter ReachedTarget { get; }        // {instance}
    public Counter RaceOutcomes { get; }         // {instance,outcome}

    public Histogram PickupLatencySeconds { get; }
    public Histogram TimeToTargetSeconds { get; }
    public Histogram RaceWinnerMbps { get; }

    // ---- Plex watchlist intake ----
    // Published even when intake is disabled (at 0), so "intake off", "intake broken" and "intake
    // idle" are three distinguishable pictures instead of one empty graph.
    public Gauge WatchlistEnabled { get; }
    public Gauge WatchlistProbeSuccess { get; }        // {feed}   1|0, per feed
    public Gauge WatchlistHttpStatus { get; }          // {feed}   last status; 0 = transport failure
    public Gauge WatchlistItems { get; }               // {feed}   -1 when unreadable, else count
    public Gauge WatchlistLastAddAge { get; }          // {arr}
    public Gauge WatchlistParked { get; }
    public Gauge WatchlistLoops { get; }
    public Counter WatchlistAdds { get; }              // {arr,outcome}

    public RacearrMetrics(IMetricFactory factory)
    {
        Up = factory.CreateGauge("racearr_up", "1 while the racearr process is running.");
        DryRun = factory.CreateGauge("racearr_dry_run", "1 when observe-only (kill switch), else 0.");
        ActiveRaces = factory.CreateGauge("racearr_active_races", "Races currently in progress.");
        ManagedDownloads = factory.CreateGauge("racearr_managed_downloads", "Downloads first seen after startup that are actively managed.");
        Loops = factory.CreateGauge("racearr_loops_total", "Control-loop iterations since start.");
        LastLoopAge = factory.CreateGauge("racearr_last_loop_age_seconds", "Seconds since the control loop last ticked.");

        Incidents = factory.CreateCounter("racearr_incidents_total", "SLA incidents raised.",
            new CounterConfiguration { LabelNames = ["type"] });
        Pickups = factory.CreateCounter("racearr_pickups_total", "Wanted items picked up (grabbed).",
            new CounterConfiguration { LabelNames = ["instance", "result"] });
        RacesStarted = factory.CreateCounter("racearr_races_started_total", "Speed-SLA races started.",
            new CounterConfiguration { LabelNames = ["instance"] });
        RaceAttempts = factory.CreateCounter("racearr_race_attempts_total", "Race attempts by classified outcome.",
            new CounterConfiguration { LabelNames = ["instance", "outcome"] });
        CandidatesGrabbed = factory.CreateCounter("racearr_candidates_grabbed_total", "Alternate releases grabbed to race.",
            new CounterConfiguration { LabelNames = ["instance"] });
        LosersKilled = factory.CreateCounter("racearr_losers_killed_total", "Slower race candidates removed.",
            new CounterConfiguration { LabelNames = ["instance"] });
        ReachedTarget = factory.CreateCounter("racearr_downloads_reached_target_total", "Downloads that reached the target speed.",
            new CounterConfiguration { LabelNames = ["instance"] });
        RaceOutcomes = factory.CreateCounter("racearr_race_outcomes_total", "Race outcomes by result.",
            new CounterConfiguration { LabelNames = ["instance", "outcome"] });

        PickupLatencySeconds = factory.CreateHistogram("racearr_pickup_latency_seconds",
            "Time from wanted to grabbed.", new HistogramConfiguration { Buckets = PickupBuckets });
        TimeToTargetSeconds = factory.CreateHistogram("racearr_time_to_target_seconds",
            "Time from first-seen to reaching the target speed.", new HistogramConfiguration { Buckets = TimeToTargetBuckets });
        RaceWinnerMbps = factory.CreateHistogram("racearr_race_winner_mbps",
            "Winning candidate speed in MB/s.", new HistogramConfiguration { Buckets = MbpsBuckets });

        WatchlistEnabled = factory.CreateGauge("racearr_watchlist_enabled",
            "1 when Plex watchlist intake is configured and running.");
        WatchlistProbeSuccess = factory.CreateGauge("racearr_watchlist_feed_probe_success",
            "1 when the last fetch of this Plex feed returned a readable watchlist.",
            new GaugeConfiguration { LabelNames = ["feed"] });
        WatchlistHttpStatus = factory.CreateGauge("racearr_watchlist_feed_http_status",
            "HTTP status of the last fetch of this Plex feed (0 = transport failure).",
            new GaugeConfiguration { LabelNames = ["feed"] });
        WatchlistItems = factory.CreateGauge("racearr_watchlist_feed_items",
            "Items in this Plex feed at the last poll; -1 when the feed was unreadable.",
            new GaugeConfiguration { LabelNames = ["feed"] });
        WatchlistLastAddAge = factory.CreateGauge("racearr_watchlist_last_add_age_seconds",
            "Seconds since watchlist intake last added anything to this *arr.",
            new GaugeConfiguration { LabelNames = ["arr"] });
        WatchlistParked = factory.CreateGauge("racearr_watchlist_parked_items",
            "Feed items parked after repeated rejections (no longer retried until restart).");
        WatchlistLoops = factory.CreateGauge("racearr_watchlist_loops_total",
            "Watchlist intake polls since start.");
        WatchlistAdds = factory.CreateCounter("racearr_watchlist_adds_total",
            "Watchlist intake decisions by target and outcome.",
            new CounterConfiguration { LabelNames = ["arr", "outcome"] });
    }

    /// <summary>Publish the known label sets at 0 so dashboards read clean zeros before the first event.</summary>
    public void PreInitialize(RacearrOptions options)
    {
        var instances = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.RadarrApiKey)) instances.Add("radarr");
        if (!string.IsNullOrWhiteSpace(options.SonarrApiKey)) instances.Add("sonarr");

        foreach (var instance in instances)
        {
            RacesStarted.WithLabels(instance).IncTo(0);
            foreach (var outcome in new[] { "accepted", "already_present", "rejected", "failed", "dry_run", "no_candidates", "search_failed" })
                RaceAttempts.WithLabels(instance, outcome).IncTo(0);
            CandidatesGrabbed.WithLabels(instance).IncTo(0);
            LosersKilled.WithLabels(instance).IncTo(0);
            ReachedTarget.WithLabels(instance).IncTo(0);
            Pickups.WithLabels(instance, "in_sla").IncTo(0);
            Pickups.WithLabels(instance, "breached").IncTo(0);
            RaceOutcomes.WithLabels(instance, "won_target").IncTo(0);
            RaceOutcomes.WithLabels(instance, "kept_below_target").IncTo(0);
        }

        foreach (var type in new[] { "pickup_sla", "speed_sla", "stalled_dead", "race_no_target", "fake_rejected", "import_failed" })
            Incidents.WithLabels(type).IncTo(0);

        // Intake label sets, published at 0 up front. A series that only appears after the first
        // success cannot be alerted on in the failure case — and the failure case is the entire
        // reason the intake metrics exist. Feed labels are positional (feed1, feed2): the feed id
        // is a read credential and must never become a metric label.
        for (var i = 0; i < options.WatchlistFeedIds.Count; i++)
        {
            var feed = $"feed{i + 1}";
            WatchlistProbeSuccess.WithLabels(feed).Set(0);
            WatchlistHttpStatus.WithLabels(feed).Set(0);
            WatchlistItems.WithLabels(feed).Set(-1);
        }

        foreach (var instance in ArrInstance.FromOptions(options))
        {
            WatchlistLastAddAge.WithLabels(instance.Name).Set(0);
            foreach (var outcome in new[] { "added", "already_present", "dry_run", "unsupported", "no_identity", "no_instance", "no_target", "lookup_failed", "deferred", "parked" })
                WatchlistAdds.WithLabels(instance.Name, outcome).IncTo(0);
        }

        WatchlistEnabled.Set(options.HasWatchlist ? 1 : 0);
        WatchlistParked.Set(0);
        WatchlistLoops.Set(0);
    }

    /// <summary>Refresh intake gauges from live state; wired as a second before-collect callback.</summary>
    public void RefreshWatchlist(WatchlistIntakeState state, RacearrOptions options)
    {
        var now = DateTimeOffset.UtcNow;
        WatchlistEnabled.Set(options.HasWatchlist ? 1 : 0);
        WatchlistParked.Set(state.Parked);
        WatchlistLoops.Set(state.Loops);
        foreach (var instance in ArrInstance.FromOptions(options))
            WatchlistLastAddAge.WithLabels(instance.Name).Set(state.LastAddAgeSeconds(instance.Name, now));
    }

    /// <summary>Refresh gauges from live engine state; wired as a before-collect callback.</summary>
    public void RefreshGauges(RaceEngineState state)
    {
        Up.Set(1);
        DryRun.Set(state.DryRun ? 1 : 0);
        ActiveRaces.Set(state.ActiveRaces);
        ManagedDownloads.Set(state.ManagedDownloads);
        Loops.Set(state.Loops);
        LastLoopAge.Set(state.LastLoopAgeSeconds);
    }

    // ---- IEngineMetrics: the event side-effects the engine emits ----
    public void IncIncident(string type) => Incidents.WithLabels(type).Inc();
    public void ObservePickupLatency(double seconds) => PickupLatencySeconds.Observe(seconds);
    public void IncPickup(string instance, string result) => Pickups.WithLabels(instance, result).Inc();
    public void IncRaceStarted(string instance) => RacesStarted.WithLabels(instance).Inc();
    public void IncRaceAttempt(string instance, string outcome) => RaceAttempts.WithLabels(instance, outcome).Inc();
    public void IncCandidatesGrabbed(string instance, double count) => CandidatesGrabbed.WithLabels(instance).Inc(count);
    public void IncLosersKilled(string instance) => LosersKilled.WithLabels(instance).Inc();
    public void IncReachedTarget(string instance) => ReachedTarget.WithLabels(instance).Inc();
    public void ObserveTimeToTarget(double seconds) => TimeToTargetSeconds.Observe(seconds);
    public void ObserveRaceWinnerMbps(double mbps) => RaceWinnerMbps.Observe(mbps);
    public void IncRaceOutcome(string instance, string outcome) => RaceOutcomes.WithLabels(instance, outcome).Inc();
}
