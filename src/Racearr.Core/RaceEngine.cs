using Microsoft.Extensions.Logging;

namespace Racearr.Core;

/// <summary>
/// The racearr control engine — a faithful port of <c>racearr.py</c>'s <c>process_instance</c>,
/// pickup-SLA pass and baseline priming. All decision logic delegates to <see cref="RaceDecisions"/>;
/// this type owns the mutable, single-threaded runtime state (like the Python module globals) and
/// orchestrates IO through <see cref="IArrClient"/> / <see cref="IQbitClient"/>.
/// <para>
/// Threading: <see cref="TickAsync"/> is invoked sequentially by the hosted service, so the
/// internal dictionaries need no locking. Only the published counters/gauges (via
/// <see cref="RaceEngineState"/>) cross threads, and those use interlocked/atomic access.
/// </para>
/// </summary>
public sealed class RaceEngine
{
    private const double Mb = RaceDecisions.MB;

    private readonly RacearrOptions _o;
    private readonly IReadOnlyList<ArrInstance> _instances;
    private readonly IArrClient _arr;
    private readonly IQbitClient _qbit;
    private readonly IEngineMetrics _metrics;
    private readonly IEventSink _events;
    private readonly RaceEngineState _state;
    private readonly ILogger<RaceEngine> _log;

    // Mutable engine state (single-threaded; mirrors the Python module globals).
    private readonly Dictionary<string, DownloadState> _dl = new();          // infohash -> tracking
    private readonly Dictionary<string, DateTimeOffset> _raceStart = new();  // "kind:iid" -> race start
    private readonly Dictionary<string, DateTimeOffset> _cooldown = new();   // "kind:iid" -> suppress re-race until
    private readonly Dictionary<string, PickupState> _pickup = new();        // "kind:iid" -> pickup tracking
    private readonly HashSet<string> _baselineDl = new();                    // downloads present at startup
    private readonly HashSet<string> _baselineWanted = new();                // wanted keys present at startup
    private readonly HashSet<string> _reaped = new();                        // fake download-ids already reported (avoid dup incidents)
    private bool _primed;

    public RaceEngine(
        RacearrOptions options,
        IArrClient arr,
        IQbitClient qbit,
        IEngineMetrics metrics,
        IEventSink events,
        RaceEngineState state,
        ILogger<RaceEngine> log)
    {
        _o = options;
        _instances = ArrInstance.FromOptions(options);
        _arr = arr;
        _qbit = qbit;
        _metrics = metrics;
        _events = events;
        _state = state;
        _log = log;
    }

    public IReadOnlyList<ArrInstance> Instances => _instances;

    /// <summary>Snapshot existing downloads + wanted items so the pre-existing backlog is never managed.</summary>
    public async Task PrimeBaselineAsync(CancellationToken ct)
    {
        if (_primed) return;
        foreach (var inst in _instances)
        {
            try
            {
                foreach (var rec in await _arr.GetQueueAsync(inst, ct))
                {
                    var dlid = rec.DownloadId.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(dlid)) _baselineDl.Add(dlid);
                }
                foreach (var w in await _arr.GetWantedMissingAsync(inst, ct))
                    _baselineWanted.Add($"{inst.Name}:{w.Id}");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "baseline prime failed ({Kind})", inst.Name);
            }
        }
        _primed = true;
        _log.LogInformation("baseline primed: {Dl} existing downloads, {Wanted} wanted items (protected)",
            _baselineDl.Count, _baselineWanted.Count);
    }

    /// <summary>One control-loop iteration: read live speeds, then process every configured instance.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!_primed) await PrimeBaselineAsync(ct);
        try
        {
            var qbt = await _qbit.GetByHashAsync(ct);
            foreach (var inst in _instances)
            {
                try { await ProcessInstanceAsync(inst, qbt, ct); }
                catch (Exception ex) { _log.LogError(ex, "instance {Kind} pass error", inst.Name); }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "loop error");
        }
        _state.MarkLoop();
        _state.ManagedDownloads = _dl.Keys.Count(h => !_baselineDl.Contains(h));
        _state.ActiveRaces = _raceStart.Count;
    }

    private async Task ProcessInstanceAsync(ArrInstance inst, IReadOnlyDictionary<string, TorrentInfo> qbt, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var records = await _arr.GetQueueAsync(inst, ct);

        // Group active download records by *arr item id, tracking multi-episode "pack" hashes.
        var groups = new Dictionary<int, List<QueueRecord>>();
        var hashToItems = new Dictionary<string, HashSet<int>>();
        foreach (var rec in records)
        {
            if (rec.ItemId is not int iid) continue;
            var dlid = rec.DownloadId.ToLowerInvariant();
            if (string.IsNullOrEmpty(dlid)) continue;
            (groups.TryGetValue(iid, out var g) ? g : groups[iid] = []).Add(rec);
            (hashToItems.TryGetValue(dlid, out var s) ? s : hashToItems[dlid] = []).Add(iid);
        }

        var activeRaces = _raceStart.Keys.Count(k => k.StartsWith(inst.Name + ":", StringComparison.Ordinal));

        foreach (var (iid, recs) in groups)
        {
            var gkey = $"{inst.Name}:{iid}";
            var cand = new List<Candidate>();
            foreach (var rec in recs)
            {
                var dlid = rec.DownloadId.ToLowerInvariant();
                if (string.IsNullOrEmpty(dlid)) continue;
                // Skip season-pack downloads (one torrent -> many episodes): monitor only, never race.
                if (hashToItems.TryGetValue(dlid, out var items) && items.Count > 1) continue;

                qbt.TryGetValue(dlid, out var t);
                var speed = t?.DlSpeed ?? 0;
                var progress = t is not null ? t.Progress : 1 - (double)rec.SizeLeft / Math.Max(rec.Size, 1);

                if (!_dl.TryGetValue(dlid, out var st))
                    st = _dl[dlid] = new DownloadState { FirstSeen = now, Kind = inst.Name };
                st.MaxSpeed = Math.Max(st.MaxSpeed, speed);

                if (st.TargetHit is null && speed >= _o.RaceTargetMbps * Mb && !_baselineDl.Contains(dlid))
                {
                    st.TargetHit = now;
                    _metrics.ObserveTimeToTarget((now - st.FirstSeen).TotalSeconds);
                    _metrics.IncReachedTarget(inst.Name);
                }

                cand.Add(new Candidate(rec, dlid, t, speed, progress,
                    (now - st.FirstSeen).TotalSeconds, st.MaxSpeed, _baselineDl.Contains(dlid)));
            }

            if (cand.Count == 0) continue;
            if (cand.All(c => c.Baseline)) continue; // never manage the pre-existing backlog

            // ---- FAKE GUARD: reap runt-sized candidates — tiny fake/sample/malware torrents that
            // download fastest and would otherwise "win" a race while the genuine releases are culled.
            // They are always blocklisted so the *arr won't re-grab them; baseline items are untouched. ----
            var largestSize = cand.Max(c => c.Record.Size);
            bool IsFake(Candidate c) => !c.Baseline && RaceDecisions.IsRuntSize(c.Record.Size, largestSize, _o);
            if (cand.Any(IsFake))
            {
                foreach (var f in cand.Where(IsFake))
                {
                    if (_reaped.Add(f.Hash))
                    {
                        Incident("fake_rejected",
                            $"{inst.Name} item {iid}: rejecting runt {f.Record.Size / Mb:0} MB '{Trunc(f.Record.Title, 50)}' " +
                            $"(largest alternate {largestSize / Mb:0} MB) — blocklisting so it is not re-grabbed");
                        _events.Record(new RaceEvent { Kind = "fake_rejected", Instance = inst.Name, ItemId = iid, Detail = Trunc(f.Record.Title, 60) });
                    }
                    await KillAsync(inst, f.Record, f.Torrent, ct, forceBlocklist: true);
                }
                cand = cand.Where(c => !IsFake(c)).ToList();
                // Every candidate was a fake: they are blocklisted, so re-search for a genuine release
                // (cooldown-gated to avoid churn) and move on.
                if (cand.Count == 0 || cand.All(c => c.Baseline))
                {
                    _raceStart.Remove(gkey);
                    if (!(_cooldown.TryGetValue(gkey, out var cu) && now < cu))
                    {
                        await ForceSearchAsync(inst, iid, ct);
                        _cooldown[gkey] = now.AddSeconds(_o.RaceCooldownSeconds);
                    }
                    continue;
                }
            }

            // ---- IMPORT-FAILED EVICTION: a download that finished but the *arr can't import
            // (importBlocked / importFailed / failedPending) is a dead end for time-to-Plex —
            // blocklist it and search for a different release so the title still lands. ----
            if (cand.Any(c => !c.Baseline && RaceDecisions.IsImportFailed(c.Record)))
            {
                foreach (var s in cand.Where(c => !c.Baseline && RaceDecisions.IsImportFailed(c.Record)))
                {
                    if (_reaped.Add(s.Hash))
                    {
                        Incident("import_failed",
                            $"{inst.Name} item {iid}: '{Trunc(s.Record.Title, 50)}' finished but won't import " +
                            $"({s.Record.TrackedDownloadState}) — blocklisting + searching a different release");
                        _events.Record(new RaceEvent { Kind = "import_failed", Instance = inst.Name, ItemId = iid, Detail = Trunc(s.Record.Title, 60) });
                    }
                    await KillAsync(inst, s.Record, s.Torrent, ct, forceBlocklist: true);
                }
                cand = cand.Where(c => !RaceDecisions.IsImportFailed(c.Record)).ToList();
                if (cand.Count == 0 || cand.All(c => c.Baseline))
                {
                    _raceStart.Remove(gkey);
                    if (!(_cooldown.TryGetValue(gkey, out var cu2) && now < cu2))
                    {
                        await ForceSearchAsync(inst, iid, ct);
                        _cooldown[gkey] = now.AddSeconds(_o.RaceCooldownSeconds);
                    }
                    continue;
                }
            }

            var exclude = cand.Select(c => c.Hash).ToHashSet();
            var winner = cand.MaxBy(c => c.Speed);
            var done = cand.Where(c => c.Progress >= 0.999 ||
                c.Record.TrackedDownloadState is "importPending" or "imported" or "importing").ToList();

            // If something already finished, the rest lost — cull them and clear the race.
            if (done.Count > 0)
            {
                var first = done[0];
                foreach (var c in cand)
                    if (c.Hash != first.Hash && c.Progress < 0.999)
                        await KillAsync(inst, c.Record, c.Torrent, ct);
                _raceStart.Remove(gkey);
                continue;
            }

            if (!_raceStart.ContainsKey(gkey))
            {
                // ---- SPEED SLA: trigger a race on a slow, non-baseline item ----
                var oldest = cand.Max(c => c.Age);
                var bestSpeed = cand.Max(c => c.MaxSpeed);
                var inCooldown = _cooldown.TryGetValue(gkey, out var until) && now < until;
                var anyStalledDead = cand.Any(c => !c.Baseline && RaceDecisions.IsStalledDead(c.Torrent));
                var slow = RaceDecisions.ShouldStartRace(oldest, bestSpeed, inCooldown, _o);
                var stalled = RaceDecisions.ShouldStartRaceStalled(oldest, anyStalledDead, inCooldown, _o);
                if (slow || stalled)
                {
                    if (activeRaces >= _o.MaxActiveRaces) continue;
                    Incident(stalled && !slow ? "stalled_dead" : "speed_sla",
                        stalled && !slow
                            ? $"{inst.Name} item {iid} stalled/dead after {(int)oldest}s (no connected peers) — racing alternates"
                            : $"{inst.Name} item {iid} at {bestSpeed / Mb:0.00} MB/s after {(int)oldest}s " +
                              $"(< {_o.SpeedSlaMbps} MB/s) — racing alternates");

                    var slots = _o.MaxConcurrentPerItem - cand.Count;
                    var grabbed = 0;
                    var releases = await GetRaceCandidatesAsync(inst, iid, exclude, ct);
                    foreach (var r in releases)
                    {
                        if (grabbed >= slots) break;
                        if (await GrabAsync(inst, r, ct))
                        {
                            var ih = r.InfoHash.ToLowerInvariant();
                            if (ih.Length > 0) exclude.Add(ih);
                            grabbed++;
                        }
                    }

                    _state.AddRaceStarted();
                    _state.AddCandidatesGrabbed(grabbed);
                    _metrics.IncRaceStarted(inst.Name);
                    if (grabbed > 0)
                    {
                        _metrics.IncCandidatesGrabbed(inst.Name, grabbed);
                        _events.Record(new RaceEvent { Kind = "race_started", Instance = inst.Name, ItemId = iid, Detail = $"grabbed {grabbed} alternate(s)" });
                        _raceStart[gkey] = now;
                        activeRaces++;
                    }
                    else
                    {
                        // No better-seeded alternate exists — force a re-search and back off so we
                        // do not churn on genuinely scarce content.
                        await ForceSearchAsync(inst, iid, ct);
                        _cooldown[gkey] = now.AddSeconds(_o.RaceCooldownSeconds);
                    }
                }
            }
            else
            {
                // ---- CULL: keep the fastest, kill the losers; persist across loops so late-arriving
                // alternates (grab lag) are also culled until only the winner remains ----
                var raceAge = (now - _raceStart[gkey]).TotalSeconds;
                var fastest = winner.Speed;
                var haveWinner = fastest >= _o.RaceTargetMbps * Mb;
                var timedOut = raceAge >= _o.RaceMonitorSeconds;
                if (raceAge >= _o.RaceCullAfterSeconds && (haveWinner || timedOut))
                {
                    if (timedOut && !haveWinner)
                        Incident("race_no_target",
                            $"{inst.Name} item {iid}: no candidate reached {_o.RaceTargetMbps} MB/s in " +
                            $"{(int)raceAge}s; keeping fastest {fastest / Mb:0.00} MB/s");

                    foreach (var c in cand)
                        if (c.Hash != winner.Hash)
                            await KillAsync(inst, c.Record, c.Torrent, ct);

                    if (cand.Count <= 1 || timedOut)
                    {
                        var outcome = RaceDecisions.RaceOutcome(haveWinner);
                        _metrics.ObserveRaceWinnerMbps(fastest / Mb);
                        _metrics.IncRaceOutcome(inst.Name, outcome);
                        _events.Record(new RaceEvent { Kind = "race_outcome", Instance = inst.Name, ItemId = iid, Outcome = outcome, Mbps = Math.Round(fastest / Mb, 2) });
                        _raceStart.Remove(gkey);
                        _cooldown[gkey] = now.AddSeconds(_o.RaceCooldownSeconds);
                    }
                }
            }
        }

        // ---- PICKUP SLA: freshly-wanted items that never entered the queue ----
        var queuedItems = groups.Keys.ToHashSet();
        var wanted = await _arr.GetWantedMissingAsync(inst, ct);
        foreach (var w in wanted)
        {
            var gkey = $"{inst.Name}:{w.Id}";
            if (_baselineWanted.Contains(gkey)) continue;

            if (queuedItems.Contains(w.Id))
            {
                if (_pickup.Remove(gkey, out var ps))
                {
                    var lat = (now - ps.FirstSeen).TotalSeconds;
                    var result = RaceDecisions.ClassifyPickup(lat, _o);
                    _metrics.ObservePickupLatency(lat);
                    _metrics.IncPickup(inst.Name, result);
                    _events.Record(new RaceEvent { Kind = "pickup", Instance = inst.Name, ItemId = w.Id, Outcome = result, Detail = $"picked up in {lat:0}s" });
                }
                continue;
            }

            if (!_pickup.TryGetValue(gkey, out var p))
                p = _pickup[gkey] = new PickupState { FirstSeen = now };
            if (!p.Alerted && (now - p.FirstSeen).TotalSeconds >= _o.PickupSlaSeconds)
            {
                Incident("pickup_sla",
                    $"{inst.Name} '{w.Title}' wanted for {(int)(now - p.FirstSeen).TotalSeconds}s with no grab " +
                    $"(> {_o.PickupSlaSeconds}s) — forcing search");
                await ForceSearchAsync(inst, w.Id, ct);
                p.Alerted = true;
            }
        }
    }

    /// <summary>Fetch + filter + rank raceable alternate releases for an item.</summary>
    private async Task<IReadOnlyList<Release>> GetRaceCandidatesAsync(
        ArrInstance inst, int itemId, IReadOnlySet<string> exclude, CancellationToken ct)
    {
        IReadOnlyList<Release> releases;
        try { releases = await _arr.GetReleasesAsync(inst, itemId, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "release search failed ({Kind} {Id})", inst.Name, itemId);
            return [];
        }
        return RaceDecisions.SelectCandidates(releases, exclude, _o);
    }

    // ---- IO wrappers: honour DRY_RUN (kill switch) + emit logs/metrics, keeping clients pure ----

    private async Task<bool> GrabAsync(ArrInstance inst, Release r, CancellationToken ct)
    {
        if (_o.DryRun)
        {
            _log.LogInformation("[dry-run] would GRAB S={Seeders} {Quality} {Title}",
                r.Seeders, r.QualityName, Trunc(r.Title, 70));
            return true;
        }
        try
        {
            var ok = await _arr.GrabAsync(inst, r, ct);
            if (ok) _log.LogInformation("GRAB S={Seeders} {Quality} {Title}", r.Seeders, r.QualityName, Trunc(r.Title, 70));
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "grab failed ({Title})", Trunc(r.Title, 70));
            return false;
        }
    }

    private async Task ForceSearchAsync(ArrInstance inst, int itemId, CancellationToken ct)
    {
        if (_o.DryRun)
        {
            _log.LogInformation("[dry-run] would force {Command} for {Kind} {Id}", inst.SearchCommand, inst.Name, itemId);
            return;
        }
        try { await _arr.ForceSearchAsync(inst, itemId, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "force search failed ({Kind} {Id})", inst.Name, itemId); }
    }

    /// <summary>
    /// Remove a losing queue record + its torrent, without triggering an *arr re-search. Private
    /// torrents are never removed from the client (hit-and-run safety) — only detached from the
    /// queue so they keep seeding. Port of <c>kill_queue_record</c>.
    /// </summary>
    private async Task KillAsync(ArrInstance inst, QueueRecord rec, TorrentInfo? torrent, CancellationToken ct, bool forceBlocklist = false)
    {
        // A fake (forceBlocklist) is always removed + blocklisted so the *arr won't re-grab it; the
        // private-tracker hit-and-run protection does not apply to a torrent that carries no real media.
        var isPrivate = !forceBlocklist && _o.ProtectPrivate && RaceDecisions.IsPrivateTorrent(torrent, _o);
        var removeFromClient = !isPrivate;
        var mode = isPrivate ? " (detach-only, private)" : "";
        var title = Trunc(rec.Title, 60);
        if (_o.DryRun)
        {
            _log.LogInformation("[dry-run] would KILL{Mode} {Title}", mode, title);
            // Record the would-kill so a DRY_RUN soak shows what the engine would have done.
            _events.Record(new RaceEvent { Kind = "kill", Instance = inst.Name, Outcome = "dry_run", Detail = title });
            return;
        }
        try
        {
            await _arr.DeleteQueueAsync(inst, rec.Id, removeFromClient, removeFromClient, ct);
            if (removeFromClient)
            {
                _state.AddLosersKilled(1);
                _metrics.IncLosersKilled(inst.Name);
            }
            // Both a full removal and a private detach mutate the *arr queue — record either, honestly
            // labelled, while losers_killed counts only real client removals.
            _events.Record(new RaceEvent
            {
                Kind = "kill",
                Instance = inst.Name,
                Outcome = removeFromClient ? "removed" : "detach_only",
                Detail = title,
            });
            _log.LogInformation("KILL{Mode} {Title}", mode, title);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "kill failed ({Title})", title);
        }
    }

    private void Incident(string type, string message)
    {
        _state.AddIncident();
        _metrics.IncIncident(type);
        _events.Record(new RaceEvent { Kind = "incident", Outcome = type, Detail = message });
        _log.LogWarning("INCIDENT {Type} {Message}", type, message);
        // INCIDENT_WEBHOOK notification is deferred to Phase 4 (ADR-0001) alongside the seerr webhook.
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed class DownloadState
    {
        public DateTimeOffset FirstSeen { get; init; }
        public double MaxSpeed { get; set; }
        public string Kind { get; init; } = "";
        public DateTimeOffset? TargetHit { get; set; }
    }

    private sealed class PickupState
    {
        public DateTimeOffset FirstSeen { get; init; }
        public bool Alerted { get; set; }
    }

    private readonly record struct Candidate(
        QueueRecord Record, string Hash, TorrentInfo? Torrent,
        double Speed, double Progress, double Age, double MaxSpeed, bool Baseline);
}
