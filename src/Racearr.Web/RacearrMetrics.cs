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
public sealed class RacearrMetrics
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
    public Counter CandidatesGrabbed { get; }    // {instance}
    public Counter LosersKilled { get; }         // {instance}
    public Counter ReachedTarget { get; }        // {instance}
    public Counter RaceOutcomes { get; }         // {instance,outcome}

    public Histogram PickupLatencySeconds { get; }
    public Histogram TimeToTargetSeconds { get; }
    public Histogram RaceWinnerMbps { get; }

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
            CandidatesGrabbed.WithLabels(instance).IncTo(0);
            LosersKilled.WithLabels(instance).IncTo(0);
            ReachedTarget.WithLabels(instance).IncTo(0);
            Pickups.WithLabels(instance, "in_sla").IncTo(0);
            Pickups.WithLabels(instance, "breached").IncTo(0);
            RaceOutcomes.WithLabels(instance, "won_target").IncTo(0);
            RaceOutcomes.WithLabels(instance, "kept_below_target").IncTo(0);
        }

        foreach (var type in new[] { "pickup_sla", "speed_sla", "race_no_target" })
            Incidents.WithLabels(type).IncTo(0);
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
}
