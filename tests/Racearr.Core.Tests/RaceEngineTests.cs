using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// Engine-level parity tests using in-memory fakes for the *arr / qBittorrent IO. They assert the
/// orchestration (baseline protection, pickup accounting, speed-SLA racing) behaves like the Python
/// service without touching the network.
/// </summary>
public class RaceEngineTests
{
    private sealed class FakeArr : IArrClient
    {
        public List<QueueRecord> Queue = [];
        public List<WantedItem> Wanted = [];
        public List<Release> Releases = [];
        public List<int> ForcedSearches = [];
        public List<string> Grabbed = [];
        public List<int> Deleted = [];
        public List<(int Id, bool RemoveFromClient, bool Blocklist)> DeleteCalls = [];

        public Task<IReadOnlyList<QueueRecord>> GetQueueAsync(ArrInstance i, CancellationToken ct) => Task.FromResult<IReadOnlyList<QueueRecord>>(Queue);
        public Task<IReadOnlyList<WantedItem>> GetWantedMissingAsync(ArrInstance i, CancellationToken ct) => Task.FromResult<IReadOnlyList<WantedItem>>(Wanted);
        public Task<IReadOnlyList<Release>> GetReleasesAsync(ArrInstance i, int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<Release>>(Releases);
        public Task ForceSearchAsync(ArrInstance i, int id, CancellationToken ct) { ForcedSearches.Add(id); return Task.CompletedTask; }
        public Task<bool> GrabAsync(ArrInstance i, Release r, CancellationToken ct) { Grabbed.Add(r.InfoHash); return Task.FromResult(true); }
        public Task DeleteQueueAsync(ArrInstance i, int id, bool rc, bool bl, CancellationToken ct) { Deleted.Add(id); DeleteCalls.Add((id, rc, bl)); return Task.CompletedTask; }
    }

    private sealed class FakeQbit : IQbitClient
    {
        public Dictionary<string, TorrentInfo> Torrents = [];
        public Task<IReadOnlyDictionary<string, TorrentInfo>> GetByHashAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, TorrentInfo>>(Torrents);
    }

    private sealed class CountingMetrics : IEngineMetrics
    {
        public readonly Dictionary<string, int> Pickups = [];
        public readonly List<string> IncidentTypes = [];
        public readonly List<string> RaceOutcomes = [];
        public int Incidents;
        public int RacesStarted;
        public int CandidatesGrabbed;
        public int LosersKilled;
        public int ReachedTarget;
        public void IncIncident(string type) { Incidents++; IncidentTypes.Add(type); }
        public void ObservePickupLatency(double s) { }
        public void IncPickup(string instance, string result) => Pickups[result] = Pickups.GetValueOrDefault(result) + 1;
        public void IncRaceStarted(string instance) => RacesStarted++;
        public void IncCandidatesGrabbed(string instance, double c) => CandidatesGrabbed += (int)c;
        public void IncLosersKilled(string instance) => LosersKilled++;
        public void IncReachedTarget(string instance) => ReachedTarget++;
        public void ObserveTimeToTarget(double s) { }
        public void ObserveRaceWinnerMbps(double m) { }
        public void IncRaceOutcome(string instance, string outcome) => RaceOutcomes.Add(outcome);
    }

    private sealed class CountingEventSink : IEventSink
    {
        public readonly List<RaceEvent> Events = [];
        public void Record(RaceEvent evt) => Events.Add(evt);
    }

    private static RaceEngine NewEngine(RacearrOptions o, FakeArr arr, FakeQbit qbit, IEngineMetrics m, IEventSink? events = null)
        => new(o, arr, qbit, m, events ?? NullEventSink.Instance, new RaceEngineState(o.DryRun), NullLogger<RaceEngine>.Instance);

    [Fact]
    public async Task Baseline_ProtectsPreExistingDownloadsAndWanted()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = true };
        var arr = new FakeArr
        {
            Queue = { new QueueRecord { Id = 1, ItemId = 100, DownloadId = "abc" } },
            Wanted = { new WantedItem(200, "Old Movie") },
        };
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, new FakeQbit(), metrics);

        await engine.PrimeBaselineAsync(CancellationToken.None);
        await engine.TickAsync(CancellationToken.None);

        Assert.Empty(arr.ForcedSearches);       // baselined wanted item is never force-searched
        Assert.Equal(0, metrics.Incidents);     // and never raises a pickup incident
        Assert.Empty(metrics.Pickups);          // pre-existing item is not counted as a pickup
    }

    [Fact]
    public async Task Pickup_NewWantedThenQueued_RecordsInSla()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = true };
        var arr = new FakeArr();
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, new FakeQbit(), metrics);

        await engine.PrimeBaselineAsync(CancellationToken.None); // empty baseline

        // A NEW wanted item appears (not baselined), not yet in the queue -> starts the pickup clock.
        arr.Wanted = [new WantedItem(300, "New Movie")];
        await engine.TickAsync(CancellationToken.None);

        // It then enters the queue -> pickup recorded, within SLA (latency ~0).
        arr.Queue = [new QueueRecord { Id = 5, ItemId = 300, DownloadId = "newhash" }];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal(1, metrics.Pickups.GetValueOrDefault("in_sla"));
        Assert.Equal(0, metrics.Pickups.GetValueOrDefault("breached"));
    }

    [Fact]
    public async Task SpeedSla_SlowDownload_GrabsFastestAlternates()
    {
        var o = new RacearrOptions
        {
            RadarrApiKey = "x",
            DryRun = false,           // armed, so the real grab client is exercised
            SpeedSlaSeconds = 0,      // any age qualifies
            RaceMinSeeders = 3,
            MaxConcurrentPerItem = 4,
        };
        var arr = new FakeArr
        {
            Releases =
            {
                new Release { Protocol = "torrent", Seeders = 80, Resolution = 1080, InfoHash = "fast1", Guid = "g1" },
                new Release { Protocol = "torrent", Seeders = 50, Resolution = 1080, InfoHash = "fast2", Guid = "g2" },
                new Release { Protocol = "torrent", Seeders = 1,  Resolution = 1080, InfoHash = "lowseed", Guid = "g3" },
            },
        };
        var qbit = new FakeQbit { Torrents = { ["slow"] = new TorrentInfo { DlSpeed = 100_000, Progress = 0.1 } } };
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, qbit, metrics);

        await engine.PrimeBaselineAsync(CancellationToken.None); // empty baseline (slow appears after)
        arr.Queue = [new QueueRecord { Id = 9, ItemId = 400, DownloadId = "slow" }];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal(1, metrics.RacesStarted);
        Assert.Equal(2, metrics.CandidatesGrabbed);                       // low-seed alternate filtered out
        Assert.Equal(["fast1", "fast2"], arr.Grabbed);                    // highest-seeded first
        Assert.Contains("speed_sla", metrics.IncidentTypes);              // the incident type raised
        Assert.Equal(1, metrics.Incidents);
    }

    [Fact]
    public async Task Race_TimesOutWithNoWinner_RaisesRaceNoTargetAndKeptBelowTarget()
    {
        var o = new RacearrOptions
        {
            RadarrApiKey = "x", DryRun = false,
            SpeedSlaSeconds = 0, RaceCullAfterSeconds = 0, RaceMonitorSeconds = 0, RaceMinSeeders = 3,
        };
        var arr = new FakeArr
        {
            Releases = { new Release { Protocol = "torrent", Seeders = 50, Resolution = 1080, InfoHash = "alt", Guid = "g" } },
        };
        var qbit = new FakeQbit { Torrents = { ["slow"] = new TorrentInfo { DlSpeed = 50_000, Progress = 0.2 } } };
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, qbit, metrics);

        await engine.PrimeBaselineAsync(CancellationToken.None);
        arr.Queue = [new QueueRecord { Id = 1, ItemId = 500, DownloadId = "slow" }];
        await engine.TickAsync(CancellationToken.None); // starts the race
        await engine.TickAsync(CancellationToken.None); // race age > 0 >= cull/monitor (0) -> times out with no winner

        Assert.Contains("race_no_target", metrics.IncidentTypes);
        Assert.Contains("kept_below_target", metrics.RaceOutcomes);
    }

    [Fact]
    public async Task DoneBranch_FinishedDownload_CullsUnfinishedLoser()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false };
        var arr = new FakeArr();
        var qbit = new FakeQbit
        {
            Torrents =
            {
                ["done1"] = new TorrentInfo { Progress = 1.0 },
                ["loser1"] = new TorrentInfo { Progress = 0.5 },
            },
        };
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, qbit, metrics);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue =
        [
            new QueueRecord { Id = 10, ItemId = 600, DownloadId = "done1" },
            new QueueRecord { Id = 11, ItemId = 600, DownloadId = "loser1" },
        ];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal([11], arr.Deleted);        // the unfinished loser is culled; the finished one kept
        Assert.Equal(1, metrics.LosersKilled);
    }

    [Fact]
    public async Task Kill_PrivateTorrent_DetachesWithoutIncrementingLosersKilled()
    {
        var o = new RacearrOptions
        {
            RadarrApiKey = "x", DryRun = false,
            ProtectPrivate = true, PrivateTrackerDomains = ["avistaz"],
        };
        var arr = new FakeArr();
        var qbit = new FakeQbit
        {
            Torrents =
            {
                ["winner"] = new TorrentInfo { Progress = 1.0 },
                ["priv"] = new TorrentInfo { Progress = 0.5, Tracker = "https://tracker.avistaz.to/announce" },
            },
        };
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, qbit, metrics);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue =
        [
            new QueueRecord { Id = 20, ItemId = 700, DownloadId = "winner" },
            new QueueRecord { Id = 21, ItemId = 700, DownloadId = "priv" },
        ];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal([21], arr.Deleted);        // private torrent is detached from the *arr queue
        Assert.Equal(0, metrics.LosersKilled);   // but never removed from the client / counted (hit-and-run safety)
    }

    [Fact]
    public async Task DryRun_DoesNotForceSearchOrKill()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = true, PickupSlaSeconds = 0 };
        var arr = new FakeArr();
        var qbit = new FakeQbit
        {
            Torrents =
            {
                ["done1"] = new TorrentInfo { Progress = 1.0 },
                ["loser1"] = new TorrentInfo { Progress = 0.5 },
            },
        };
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, qbit, metrics);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        // A done-branch cull and a pickup breach would both mutate when armed; in dry-run neither hits the client.
        arr.Queue =
        [
            new QueueRecord { Id = 30, ItemId = 800, DownloadId = "done1" },
            new QueueRecord { Id = 31, ItemId = 800, DownloadId = "loser1" },
        ];
        arr.Wanted = [new WantedItem(801, "Wanted")];
        await engine.TickAsync(CancellationToken.None);

        Assert.Empty(arr.Deleted);        // no real kill in dry-run
        Assert.Empty(arr.ForcedSearches); // no real force-search in dry-run
    }

    [Fact]
    public async Task Pickup_Breach_RaisesIncidentOnceThenClassifiesBreached()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false, PickupSlaSeconds = 0 };
        var arr = new FakeArr();
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, new FakeQbit(), metrics);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Wanted = [new WantedItem(900, "Slow Pickup")];
        await engine.TickAsync(CancellationToken.None); // breaches immediately (SLA 0) -> incident + force search
        await engine.TickAsync(CancellationToken.None); // still not queued -> alert-once: no second incident/search
        Assert.Equal(1, metrics.Incidents);
        Assert.Equal([900], arr.ForcedSearches);
        Assert.Contains("pickup_sla", metrics.IncidentTypes);

        arr.Queue = [new QueueRecord { Id = 40, ItemId = 900, DownloadId = "late" }];
        await engine.TickAsync(CancellationToken.None); // now queued -> breached (latency > 0 > SLA 0)
        Assert.Equal(1, metrics.Pickups.GetValueOrDefault("breached"));
    }

    [Fact]
    public async Task SeasonPack_OneHashManyItems_IsMonitoredOnlyNeverRaced()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false, SpeedSlaSeconds = 0, RaceMinSeeders = 3 };
        var arr = new FakeArr
        {
            Releases = { new Release { Protocol = "torrent", Seeders = 99, Resolution = 1080, InfoHash = "alt", Guid = "g" } },
        };
        var qbit = new FakeQbit { Torrents = { ["pack"] = new TorrentInfo { DlSpeed = 1000, Progress = 0.1 } } };
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, qbit, metrics);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        // One download hash serves two episodes -> a season pack -> monitor only, never raced.
        arr.Queue =
        [
            new QueueRecord { Id = 50, ItemId = 910, DownloadId = "pack" },
            new QueueRecord { Id = 51, ItemId = 911, DownloadId = "pack" },
        ];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal(0, metrics.RacesStarted);
        Assert.Empty(arr.Grabbed);
    }

    [Fact]
    public async Task MaxActiveRaces_CapPreventsExtraConcurrentRaces()
    {
        var o = new RacearrOptions
        {
            RadarrApiKey = "x", DryRun = false, SpeedSlaSeconds = 0, RaceMinSeeders = 3, MaxActiveRaces = 1,
        };
        var arr = new FakeArr
        {
            Releases = { new Release { Protocol = "torrent", Seeders = 40, Resolution = 1080, InfoHash = "alt", Guid = "g" } },
        };
        var qbit = new FakeQbit
        {
            Torrents =
            {
                ["slow1"] = new TorrentInfo { DlSpeed = 1000, Progress = 0.1 },
                ["slow2"] = new TorrentInfo { DlSpeed = 1000, Progress = 0.1 },
            },
        };
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, qbit, metrics);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue =
        [
            new QueueRecord { Id = 60, ItemId = 1000, DownloadId = "slow1" },
            new QueueRecord { Id = 61, ItemId = 1001, DownloadId = "slow2" },
        ];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal(1, metrics.RacesStarted); // only one race despite two slow items (cap = 1)
    }

    [Fact]
    public async Task SpeedSla_NoRaceableCandidate_ForcesSearchAndBacksOff()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false, SpeedSlaSeconds = 0, RaceMinSeeders = 3 };
        var arr = new FakeArr(); // no releases -> no raceable candidates
        var qbit = new FakeQbit { Torrents = { ["slow"] = new TorrentInfo { DlSpeed = 1000, Progress = 0.1 } } };
        var metrics = new CountingMetrics();
        var engine = NewEngine(o, arr, qbit, metrics);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue = [new QueueRecord { Id = 70, ItemId = 1100, DownloadId = "slow" }];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal(1, metrics.RacesStarted);          // the race attempt is counted
        Assert.Equal(0, metrics.CandidatesGrabbed);     // but nothing is grabbed
        Assert.Equal([1100], arr.ForcedSearches);       // it falls back to a forced search
        Assert.Contains("speed_sla", metrics.IncidentTypes);
    }

    [Fact]
    public async Task Engine_EmitsHistoryEventsToSink()
    {
        var o = new RacearrOptions
        {
            RadarrApiKey = "x", DryRun = false, SpeedSlaSeconds = 0, RaceMinSeeders = 3, MaxConcurrentPerItem = 4,
        };
        var arr = new FakeArr
        {
            Releases = { new Release { Protocol = "torrent", Seeders = 80, Resolution = 1080, InfoHash = "fast1", Guid = "g1" } },
        };
        var qbit = new FakeQbit { Torrents = { ["slow"] = new TorrentInfo { DlSpeed = 100_000, Progress = 0.1 } } };
        var events = new CountingEventSink();
        var engine = NewEngine(o, arr, qbit, new CountingMetrics(), events);

        await engine.PrimeBaselineAsync(CancellationToken.None);
        arr.Queue = [new QueueRecord { Id = 9, ItemId = 400, DownloadId = "slow" }];
        await engine.TickAsync(CancellationToken.None);

        // The engine records its decisions to the history sink (a "race_started" plus the speed-SLA "incident").
        Assert.Contains(events.Events, e => e.Kind == "race_started" && e.ItemId == 400);
        Assert.Contains(events.Events, e => e.Kind == "incident" && e.Outcome == "speed_sla");
        Assert.All(events.Events, e => Assert.False(string.IsNullOrWhiteSpace(e.Kind)));
    }

    [Fact]
    public async Task Kill_RecordsRemovedEvent_ForRealCull()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false };
        var arr = new FakeArr();
        var qbit = new FakeQbit
        {
            Torrents = { ["done1"] = new TorrentInfo { Progress = 1.0 }, ["loser1"] = new TorrentInfo { Progress = 0.5 } },
        };
        var events = new CountingEventSink();
        var engine = NewEngine(o, arr, qbit, new CountingMetrics(), events);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue =
        [
            new QueueRecord { Id = 10, ItemId = 600, DownloadId = "done1" },
            new QueueRecord { Id = 11, ItemId = 600, DownloadId = "loser1" },
        ];
        await engine.TickAsync(CancellationToken.None);

        Assert.Contains(events.Events, e => e.Kind == "kill" && e.Outcome == "removed");
    }

    [Fact]
    public async Task Kill_RecordsDetachOnlyEvent_ForPrivateTorrent()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false, ProtectPrivate = true, PrivateTrackerDomains = ["avistaz"] };
        var arr = new FakeArr();
        var qbit = new FakeQbit
        {
            Torrents =
            {
                ["winner"] = new TorrentInfo { Progress = 1.0 },
                ["priv"] = new TorrentInfo { Progress = 0.5, Tracker = "https://tracker.avistaz.to/announce" },
            },
        };
        var events = new CountingEventSink();
        var engine = NewEngine(o, arr, qbit, new CountingMetrics(), events);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue =
        [
            new QueueRecord { Id = 20, ItemId = 700, DownloadId = "winner" },
            new QueueRecord { Id = 21, ItemId = 700, DownloadId = "priv" },
        ];
        await engine.TickAsync(CancellationToken.None);

        Assert.Contains(events.Events, e => e.Kind == "kill" && e.Outcome == "detach_only");
    }

    [Fact]
    public async Task Kill_RecordsDryRunEvent_WithoutMutating()
    {
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = true };
        var arr = new FakeArr();
        var qbit = new FakeQbit
        {
            Torrents = { ["done1"] = new TorrentInfo { Progress = 1.0 }, ["loser1"] = new TorrentInfo { Progress = 0.5 } },
        };
        var events = new CountingEventSink();
        var engine = NewEngine(o, arr, qbit, new CountingMetrics(), events);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue =
        [
            new QueueRecord { Id = 10, ItemId = 600, DownloadId = "done1" },
            new QueueRecord { Id = 11, ItemId = 600, DownloadId = "loser1" },
        ];
        await engine.TickAsync(CancellationToken.None);

        Assert.Contains(events.Events, e => e.Kind == "kill" && e.Outcome == "dry_run");
        Assert.Empty(arr.Deleted); // dry-run records intent but performs no mutation
    }

    [Fact]
    public async Task FakeGuard_FastRunt_IsBlocklisted_RealCandidateSurvives()
    {
        // A tiny fake torrent downloads fastest; without the guard it would "win" and the real
        // release would be culled. The guard must blocklist the runt and keep the genuine release.
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false };
        var qbit = new FakeQbit
        {
            Torrents =
            {
                ["realdl"] = new TorrentInfo { Progress = 0.4, DlSpeed = 0.5 * 1024 * 1024 },
                ["fakefast"] = new TorrentInfo { Progress = 0.95, DlSpeed = 12.0 * 1024 * 1024 },
            },
        };
        var metrics = new CountingMetrics();
        var events = new CountingEventSink();
        var arr = new FakeArr();
        var engine = NewEngine(o, arr, qbit, metrics, events);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue =
        [
            new QueueRecord { Id = 40, ItemId = 1200, DownloadId = "realdl", Size = 2_000_000_000 },
            new QueueRecord { Id = 41, ItemId = 1200, DownloadId = "fakefast", Size = 5_000_000 },
        ];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal([41], arr.Deleted);                          // only the fake runt is removed + blocklisted
        Assert.Contains(arr.DeleteCalls, d => d.Id == 41 && d.RemoveFromClient && d.Blocklist); // removed from client AND blocklisted
        Assert.Equal(1, metrics.LosersKilled);
        Assert.Contains(events.Events, e => e.Kind == "fake_rejected" && e.ItemId == 1200);
        Assert.Contains(metrics.IncidentTypes, t => t == "fake_rejected");
    }

    [Fact]
    public async Task FakeGuard_CompletedRunt_DoesNotEndTheRace()
    {
        // The dangerous case: a tiny fake COMPLETES first. The done-branch must not treat it as the
        // winner and cull the real release; the runt is blocklisted and the real download continues.
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false };
        var qbit = new FakeQbit
        {
            Torrents =
            {
                ["realdl"] = new TorrentInfo { Progress = 0.5, DlSpeed = 1.0 * 1024 * 1024 },
                ["fakedone"] = new TorrentInfo { Progress = 1.0, DlSpeed = 0 },
            },
        };
        var events = new CountingEventSink();
        var arr = new FakeArr();
        var engine = NewEngine(o, arr, qbit, new CountingMetrics(), events);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue =
        [
            new QueueRecord { Id = 50, ItemId = 1300, DownloadId = "realdl", Size = 2_000_000_000 },
            new QueueRecord { Id = 51, ItemId = 1300, DownloadId = "fakedone", Size = 4_000_000 },
        ];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal([51], arr.Deleted);   // the completed fake is reaped, NOT kept as the winner
        Assert.DoesNotContain(50, arr.Deleted);
        Assert.Contains(events.Events, e => e.Kind == "fake_rejected");
    }

    [Fact]
    public async Task FakeGuard_AllCandidatesFake_BlocklistsThenReSearches()
    {
        // When every candidate is a runt, blocklist them all and force a fresh search so the *arr
        // grabs a genuine release next (the blocklist prevents re-grabbing the same fakes).
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false };
        var qbit = new FakeQbit { Torrents = { ["onlyfake"] = new TorrentInfo { Progress = 0.8, DlSpeed = 8.0 * 1024 * 1024 } } };
        var events = new CountingEventSink();
        var arr = new FakeArr();
        var engine = NewEngine(o, arr, qbit, new CountingMetrics(), events);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue = [new QueueRecord { Id = 60, ItemId = 1400, DownloadId = "onlyfake", Size = 3_000_000 }];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal([60], arr.Deleted);            // the lone fake is blocklisted
        Assert.Contains(1400, arr.ForcedSearches);  // and a genuine release is searched for
        Assert.Contains(events.Events, e => e.Kind == "fake_rejected");
    }

    [Fact]
    public async Task ImportFailed_BlocklistsAndReSearches()
    {
        // A download finished but the *arr can't import it (importBlocked). It must be blocklisted
        // and a different release searched — not left to sit forever blocking the title from Plex.
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false };
        var qbit = new FakeQbit { Torrents = { ["stuck"] = new TorrentInfo { Progress = 1.0, DlSpeed = 0 } } };
        var events = new CountingEventSink();
        var arr = new FakeArr();
        var engine = NewEngine(o, arr, qbit, new CountingMetrics(), events);
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue = [new QueueRecord { Id = 70, ItemId = 1500, DownloadId = "stuck", Size = 2_000_000_000, TrackedDownloadState = "importBlocked", TrackedDownloadStatus = "warning" }];
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal([70], arr.Deleted);            // the un-importable download is blocklisted
        Assert.Contains(arr.DeleteCalls, d => d.Id == 70 && d.RemoveFromClient && d.Blocklist);
        Assert.Contains(1500, arr.ForcedSearches);  // and a different release is searched for
        Assert.Contains(events.Events, e => e.Kind == "import_failed");
    }

    [Fact]
    public async Task ImportPending_IsNotTreatedAsFailed()
    {
        // A normal, transient importPending must NOT be evicted (the import may still be completing).
        var o = new RacearrOptions { RadarrApiKey = "x", DryRun = false };
        var qbit = new FakeQbit { Torrents = { ["okimport"] = new TorrentInfo { Progress = 1.0, DlSpeed = 0 } } };
        var arr = new FakeArr();
        var engine = NewEngine(o, arr, qbit, new CountingMetrics());
        await engine.PrimeBaselineAsync(CancellationToken.None);

        arr.Queue = [new QueueRecord { Id = 71, ItemId = 1501, DownloadId = "okimport", Size = 2_000_000_000, TrackedDownloadState = "importPending" }];
        await engine.TickAsync(CancellationToken.None);

        Assert.Empty(arr.Deleted);                       // not evicted
        Assert.DoesNotContain(1501, arr.ForcedSearches);
    }
}
