using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

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
    private readonly IEngineStateStore _stateStore;
    private readonly RaceEngineState _state;
    private readonly ILogger<RaceEngine> _log;

    // Mutable engine state (single-threaded; mirrors the Python module globals).
    private readonly Dictionary<string, DownloadState> _dl = new();          // infohash -> tracking
    private readonly Dictionary<string, DateTimeOffset> _raceStart = new();  // "kind:iid" -> race start
    private readonly Dictionary<string, EngineItemState> _owned = new();     // durable racearr-owned item state
    private readonly HashSet<string> _baselineDl = new();                    // downloads present at startup
    private readonly HashSet<string> _baselineWanted = new();                // wanted keys present at startup
    private readonly HashSet<string> _primedInstances = new();               // complete per-instance baselines
    private readonly HashSet<string> _reaped = new();                        // fake download-ids already reported (avoid dup incidents)
    private readonly Dictionary<string, PackState> _packs = new();           // season-pack remediation state (dead-since + cooldown), keyed "inst:pack:series:season"
    private bool _stateLoaded;

    public RaceEngine(
        RacearrOptions options,
        IArrClient arr,
        IQbitClient qbit,
        IEngineMetrics metrics,
        IEventSink events,
        IEngineStateStore stateStore,
        RaceEngineState state,
        ILogger<RaceEngine> log)
    {
        _o = options;
        _instances = ArrInstance.FromOptions(options);
        _arr = arr;
        _qbit = qbit;
        _metrics = metrics;
        _events = events;
        _stateStore = stateStore;
        _state = state;
        _log = log;
    }

    public IReadOnlyList<ArrInstance> Instances => _instances;

    /// <summary>Snapshot existing downloads + wanted items so the pre-existing backlog is never managed.</summary>
    public async Task PrimeBaselineAsync(CancellationToken ct)
    {
        if (!_stateLoaded)
        {
            try
            {
                foreach (var item in _stateStore.Load()) _owned[item.Key] = item;
                _stateLoaded = true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "engine state load failed; all instances remain unprimed");
                return;
            }
        }

        foreach (var inst in _instances)
        {
            if (_primedInstances.Contains(inst.Name)) continue;
            try
            {
                // Fetch both complete snapshots into locals before mutating baseline state. If either
                // request fails, this instance remains unprimed and no partial backlog is adopted.
                var queue = await _arr.GetQueueAsync(inst, ct);
                var wanted = await _arr.GetWantedMissingAsync(inst, ct);
                var queueIds = queue.Where(r => r.ItemId is not null).Select(r => r.ItemId!.Value).ToHashSet();
                var wantedIds = wanted.Select(w => w.Id).ToHashSet();

                var baselineHashes = queue
                    .Where(r => !string.IsNullOrEmpty(r.DownloadId))
                    .GroupBy(r => r.DownloadId.ToLowerInvariant())
                    .Where(group => group.Any(r => r.ItemId is not int id || !_owned.ContainsKey($"{inst.Name}:{id}")))
                    .Select(group => group.Key)
                    .ToList();
                var baselineWanted = wanted
                    .Select(w => $"{inst.Name}:{w.Id}")
                    .Where(key => !_owned.ContainsKey(key))
                    .ToList();

                foreach (var hash in baselineHashes) _baselineDl.Add(hash);
                foreach (var key in baselineWanted) _baselineWanted.Add(key);
                foreach (var stale in _owned.Values
                    .Where(item => item.Instance == inst.Name && !queueIds.Contains(item.ItemId) && !wantedIds.Contains(item.ItemId))
                    .Select(item => item.Key).ToList())
                    RemoveOwned(stale);

                _primedInstances.Add(inst.Name);
                _log.LogInformation("baseline primed ({Kind}): {Dl} existing downloads, {Wanted} wanted items (protected)",
                    inst.Name, baselineHashes.Count, baselineWanted.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "baseline prime failed ({Kind})", inst.Name);
            }
        }
    }

    /// <summary>One control-loop iteration: read live speeds, then process every configured instance.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await PrimeBaselineAsync(ct);
        try
        {
            var snapshot = await _qbit.GetByHashAsync(ct);
            if (!snapshot.Available)
            {
                _log.LogWarning("qbit snapshot unavailable; skipping all race and pickup actions this tick");
            }
            else
            {
                foreach (var inst in _instances)
                {
                    if (!_primedInstances.Contains(inst.Name)) continue;
                    try { await ProcessInstanceAsync(inst, snapshot.Items, ct); }
                    catch (Exception ex) { _log.LogError(ex, "instance {Kind} pass error", inst.Name); }
                }
                _state.Downloads = _dl.Keys
                    .Where(h => !_baselineDl.Contains(h) && snapshot.Items.ContainsKey(h))
                    .Select(h => snapshot.Items[h])
                    .Select(t => new DownloadStatus(t.Name, t.DlSpeed, t.Eta, t.Progress, t.State))
                    .OrderByDescending(d => d.SpeedBytesPerSec)
                    .ToList();
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

        var activeHashes = records.Select(r => r.DownloadId.ToLowerInvariant())
            .Where(hash => hash.Length > 0).ToHashSet();
        foreach (var staleHash in _dl.Where(pair => pair.Value.Kind == inst.Name && !activeHashes.Contains(pair.Key))
            .Select(pair => pair.Key).ToList())
            _dl.Remove(staleHash);
        var activeKeys = groups.Keys.Select(id => $"{inst.Name}:{id}").ToHashSet();
        foreach (var staleRace in _raceStart.Keys
            .Where(key => key.StartsWith(inst.Name + ":", StringComparison.Ordinal) && !activeKeys.Contains(key))
            .ToList())
            _raceStart.Remove(staleRace);

        var activeRaces = _raceStart.Keys.Count(k => k.StartsWith(inst.Name + ":", StringComparison.Ordinal));

        // ---- SEASON-PACK REMEDIATION: a pack (one torrent -> many episodes) is never raced
        // episode-by-episode; when its single torrent is dead (orphaned from the client or stalled with
        // no seeds) the pack-correct fix is to blocklist it and re-search the whole season. ----
        await RemediateDeadPacksAsync(inst, groups, hashToItems, qbt, now, ct);

        foreach (var (iid, recs) in groups)
        {
            var gkey = $"{inst.Name}:{iid}";
            var managed = recs.Any(rec => !_baselineDl.Contains(rec.DownloadId.ToLowerInvariant()));
            var itemState = managed || _owned.ContainsKey(gkey) ? EnsureQueueState(inst, iid, recs, now) : null;
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
                    st = _dl[dlid] = new DownloadState
                    {
                        FirstSeen = itemState?.QueueFirstSeenUtc ?? now,
                        Kind = inst.Name,
                    };
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
                var removed = new HashSet<string>();
                foreach (var f in cand.Where(IsFake))
                {
                    if (_reaped.Add(f.Hash))
                    {
                        Incident("fake_rejected",
                            $"{inst.Name} item {iid}: rejecting runt {f.Record.Size / Mb:0} MB '{Trunc(f.Record.Title, 50)}' " +
                            $"(largest alternate {largestSize / Mb:0} MB) — blocklisting so it is not re-grabbed", inst.Name, iid);
                        _events.Record(new RaceEvent { Kind = "fake_rejected", Instance = inst.Name, ItemId = iid, Detail = Trunc(f.Record.Title, 60) });
                    }
                    if (await KillAsync(inst, f.Record, f.Torrent, ct, forceBlocklist: true))
                        removed.Add(f.Hash);
                }
                cand = cand.Where(c => !removed.Contains(c.Hash)).ToList();
                // Every candidate was a fake: they are blocklisted, so re-search for a genuine release
                // (cooldown-gated to avoid churn) and move on.
                if (cand.Count == 0 || cand.All(c => c.Baseline))
                {
                    _raceStart.Remove(gkey);
                    if (itemState is not null && !InRetry(itemState, now))
                    {
                        if (await ForceSearchAsync(inst, iid, ct)) SetCooldown(itemState, now);
                        else ScheduleRetry(itemState, now);
                    }
                    continue;
                }
            }

            // ---- IMPORT-FAILED EVICTION: a download that finished but the *arr can't import
            // (importBlocked / importFailed / failedPending) is a dead end for time-to-Plex —
            // blocklist it and search for a different release so the title still lands. ----
            if (cand.Any(c => !c.Baseline && RaceDecisions.IsImportFailed(c.Record)))
            {
                var removed = new HashSet<string>();
                foreach (var s in cand.Where(c => !c.Baseline && RaceDecisions.IsImportFailed(c.Record)))
                {
                    if (_reaped.Add(s.Hash))
                    {
                        Incident("import_failed",
                            $"{inst.Name} item {iid}: '{Trunc(s.Record.Title, 50)}' finished but won't import " +
                            $"({s.Record.TrackedDownloadState}) — blocklisting + searching a different release", inst.Name, iid);
                        _events.Record(new RaceEvent { Kind = "import_failed", Instance = inst.Name, ItemId = iid, Detail = Trunc(s.Record.Title, 60) });
                    }
                    if (await KillAsync(inst, s.Record, s.Torrent, ct, forceBlocklist: true))
                        removed.Add(s.Hash);
                }
                cand = cand.Where(c => !removed.Contains(c.Hash)).ToList();
                if (cand.Count == 0 || cand.All(c => c.Baseline))
                {
                    _raceStart.Remove(gkey);
                    if (itemState is not null && !InRetry(itemState, now))
                    {
                        if (await ForceSearchAsync(inst, iid, ct)) SetCooldown(itemState, now);
                        else ScheduleRetry(itemState, now);
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
                var cullSucceeded = true;
                foreach (var c in cand.Where(c => !c.Baseline && c.Hash != first.Hash && c.Progress < 0.999))
                    cullSucceeded &= await KillAsync(inst, c.Record, c.Torrent, ct);
                if (cullSucceeded)
                {
                    _raceStart.Remove(gkey);
                    if (itemState is not null) SetCooldown(itemState, now);
                }
                continue;
            }

            if (!_raceStart.ContainsKey(gkey))
            {
                // ---- SPEED SLA: trigger a race on a slow, non-baseline item ----
                var oldest = cand.Max(c => c.Age);
                var bestSpeed = cand.Max(c => c.MaxSpeed);
                var inCooldown = itemState is not null && InRetry(itemState, now);
                var anyStalledDead = cand.Any(c => !c.Baseline && RaceDecisions.IsStalledDead(c.Torrent));
                var slow = RaceDecisions.ShouldStartRace(oldest, bestSpeed, inCooldown, _o);
                var stalled = RaceDecisions.ShouldStartRaceStalled(oldest, anyStalledDead, inCooldown, _o);
                if (slow || stalled)
                {
                    if (activeRaces >= _o.MaxActiveRaces) continue;
                    var incidentType = stalled && !slow ? "stalled_dead" : "speed_sla";
                    if (itemState?.LastIncidentType != incidentType)
                    {
                        Incident(incidentType, stalled && !slow
                            ? $"{inst.Name} item {iid} stalled/dead after {(int)oldest}s (no connected peers) — racing alternates"
                            : $"{inst.Name} item {iid} at {bestSpeed / Mb:0.00} MB/s after {(int)oldest}s " +
                              $"(< {_o.SpeedSlaMbps} MB/s) — racing alternates", inst.Name, iid);
                        if (itemState is not null)
                        {
                            itemState.LastIncidentType = incidentType;
                            SaveOwned(itemState);
                        }
                    }

                    var slots = _o.MaxConcurrentPerItem - cand.Count;
                    var grabbed = 0;
                    var search = await GetRaceCandidatesAsync(inst, iid, exclude, recs, ct);
                    if (!search.Succeeded)
                    {
                        _metrics.IncRaceAttempt(inst.Name, "search_failed");
                        _events.Record(new RaceEvent
                        {
                            Kind = "race_attempt", Instance = inst.Name, ItemId = iid,
                            Outcome = "search_failed", Detail = "interactive release search failed",
                        });
                    }
                    else if (search.Releases.Count == 0)
                    {
                        _metrics.IncRaceAttempt(inst.Name, "no_candidates");
                        _events.Record(new RaceEvent
                        {
                            Kind = "race_attempt", Instance = inst.Name, ItemId = iid,
                            Outcome = "no_candidates", Detail = "interactive release search returned no raceable alternates",
                        });
                    }
                    foreach (var r in search.Releases)
                    {
                        if (grabbed >= slots) break;
                        var result = await GrabAsync(inst, iid, r, ct);
                        var attempt = AttemptLabel(result.Outcome);
                        _metrics.IncRaceAttempt(inst.Name, attempt);
                        _events.Record(new RaceEvent
                        {
                            Kind = "race_attempt", Instance = inst.Name, ItemId = iid,
                            Outcome = attempt, Detail = Trunc(r.Title, 60),
                        });
                        if (result.Outcome == GrabOutcome.Accepted)
                        {
                            var ih = r.InfoHash.ToLowerInvariant();
                            if (ih.Length > 0) exclude.Add(ih);
                            grabbed++;
                        }
                    }

                    if (grabbed > 0)
                    {
                        _state.AddRaceStarted();
                        _state.AddCandidatesGrabbed(grabbed);
                        _metrics.IncRaceStarted(inst.Name);
                        _metrics.IncCandidatesGrabbed(inst.Name, grabbed);
                        _events.Record(new RaceEvent { Kind = "race_started", Instance = inst.Name, ItemId = iid, Detail = $"grabbed {grabbed} alternate(s)" });
                        _raceStart[gkey] = now;
                        activeRaces++;
                        if (itemState is not null) ResetRetry(itemState);
                    }
                    else if (itemState is not null) ScheduleRetry(itemState, now);
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
                            $"{(int)raceAge}s; keeping fastest {fastest / Mb:0.00} MB/s", inst.Name, iid);

                    var cullSucceeded = true;
                    foreach (var c in cand.Where(c => !c.Baseline && c.Hash != winner.Hash))
                        cullSucceeded &= await KillAsync(inst, c.Record, c.Torrent, ct);

                    var managedCandidates = cand.Count(c => !c.Baseline);
                    if (cullSucceeded && (managedCandidates <= 1 || timedOut))
                    {
                        var outcome = RaceDecisions.RaceOutcome(haveWinner);
                        _metrics.ObserveRaceWinnerMbps(fastest / Mb);
                        _metrics.IncRaceOutcome(inst.Name, outcome);
                        _events.Record(new RaceEvent { Kind = "race_outcome", Instance = inst.Name, ItemId = iid, Outcome = outcome, Mbps = Math.Round(fastest / Mb, 2) });
                        _raceStart.Remove(gkey);
                        if (itemState is not null) SetCooldown(itemState, now);
                    }
                }
            }
        }

        // ---- PICKUP SLA: freshly-wanted items that never entered the queue ----
        var queuedItems = groups.Keys.ToHashSet();
        var wanted = await _arr.GetWantedMissingAsync(inst, ct);
        var wantedItems = wanted.Select(w => w.Id).ToHashSet();
        foreach (var w in wanted)
        {
            var gkey = $"{inst.Name}:{w.Id}";
            if (_baselineWanted.Contains(gkey)) continue;

            if (queuedItems.Contains(w.Id))
            {
                if (_owned.TryGetValue(gkey, out var queuedState) && queuedState.PickupFirstSeenUtc is DateTimeOffset firstSeen)
                {
                    var lat = (now - firstSeen).TotalSeconds;
                    var result = RaceDecisions.ClassifyPickup(lat, _o);
                    _metrics.ObservePickupLatency(lat);
                    _metrics.IncPickup(inst.Name, result);
                    _events.Record(new RaceEvent { Kind = "pickup", Instance = inst.Name, ItemId = w.Id, Outcome = result, Detail = $"picked up in {lat:0}s" });
                    queuedState.PickupFirstSeenUtc = null;
                    queuedState.PickupAlerted = false;
                    SaveOwned(queuedState);
                }
                continue;
            }

            var p = EnsureOwned(inst, w.Id);
            var changed = false;
            if (p.QueueFingerprint is not null)
            {
                p.QueueFingerprint = null;
                p.QueueFirstSeenUtc = null;
                p.RetryCount = 0;
                p.NextRetryUtc = null;
                p.LastIncidentType = null;
                changed = true;
            }
            if (p.PickupFirstSeenUtc is null)
            {
                p.PickupFirstSeenUtc = now;
                changed = true;
            }
            if (changed) SaveOwned(p);
            if (!p.PickupAlerted && (now - p.PickupFirstSeenUtc.Value).TotalSeconds >= _o.PickupSlaSeconds)
            {
                if (p.LastIncidentType != "pickup_sla")
                {
                    Incident("pickup_sla",
                        $"{inst.Name} '{w.Title}' wanted for {(int)(now - p.PickupFirstSeenUtc.Value).TotalSeconds}s with no grab " +
                        $"(> {_o.PickupSlaSeconds}s) — forcing search", inst.Name, w.Id);
                    p.LastIncidentType = "pickup_sla";
                    SaveOwned(p);
                }
                if (!InRetry(p, now))
                {
                    if (await ForceSearchAsync(inst, w.Id, ct))
                    {
                        p.PickupAlerted = true;
                        ResetRetry(p);
                    }
                    else
                    {
                        ScheduleRetry(p, now);
                    }
                }
            }
        }

        foreach (var stale in _owned.Values
            .Where(item => item.Instance == inst.Name && !wantedItems.Contains(item.ItemId) && !queuedItems.Contains(item.ItemId))
            .Select(item => item.Key).ToList())
            RemoveOwned(stale);
    }

    /// <summary>Fetch + filter + rank raceable alternate releases for an item.</summary>
    private async Task<CandidateSearchResult> GetRaceCandidatesAsync(
        ArrInstance inst, int itemId, IReadOnlySet<string> exclude,
        IReadOnlyList<QueueRecord> activeRecords, CancellationToken ct)
    {
        IReadOnlyList<Release> releases;
        try { releases = await _arr.GetReleasesAsync(inst, itemId, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "release search failed ({Kind} {Id})", inst.Name, itemId);
            return new CandidateSearchResult(false, []);
        }
        return new CandidateSearchResult(true, RaceDecisions.SelectCandidates(releases, exclude, _o, activeRecords));
    }

    // ---- IO wrappers: honour DRY_RUN (kill switch) + emit logs/metrics, keeping clients pure ----

    private async Task<GrabResult> GrabAsync(ArrInstance inst, int itemId, Release r, CancellationToken ct)
    {
        if (_o.DryRun)
        {
            _log.LogInformation("[dry-run] would GRAB S={Seeders} {Quality} {Title}",
                r.Seeders, r.QualityName, Trunc(r.Title, 70));
            return new GrabResult(GrabOutcome.DryRun);
        }
        try
        {
            var result = await _arr.GrabAsync(inst, itemId, r, ct);
            if (result.Outcome == GrabOutcome.Accepted)
                _log.LogInformation("GRAB S={Seeders} {Quality} {Title}", r.Seeders, r.QualityName, Trunc(r.Title, 70));
            else
                _log.LogInformation("GRAB {Outcome} S={Seeders} {Quality} {Title}",
                    result.Outcome, r.Seeders, r.QualityName, Trunc(r.Title, 70));
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "grab failed ({Title})", Trunc(r.Title, 70));
            return new GrabResult(GrabOutcome.Failed);
        }
    }

    private async Task<bool> ForceSearchAsync(ArrInstance inst, int itemId, CancellationToken ct)
    {
        if (_o.DryRun)
        {
            _log.LogInformation("[dry-run] would force {Command} for {Kind} {Id}", inst.SearchCommand, inst.Name, itemId);
            return true;
        }
        try
        {
            var result = await _arr.ForceSearchAsync(inst, itemId, ct);
            if (!result.Succeeded)
                _log.LogWarning("force search failed ({Kind} {Id}) HTTP {Status}", inst.Name, itemId, result.StatusCode);
            return result.Succeeded;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "force search failed ({Kind} {Id})", inst.Name, itemId);
            return false;
        }
    }

    /// <summary>
    /// Remove a losing queue record + its torrent, without triggering an *arr re-search. Private
    /// torrents are never removed from the client (hit-and-run safety) — only detached from the
    /// queue so they keep seeding. Port of <c>kill_queue_record</c>.
    /// </summary>
    private async Task<bool> KillAsync(ArrInstance inst, QueueRecord rec, TorrentInfo? torrent, CancellationToken ct, bool forceBlocklist = false)
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
            return true;
        }
        try
        {
            var result = await _arr.DeleteQueueAsync(inst, rec.Id, removeFromClient, removeFromClient, ct);
            if (!result.Succeeded)
            {
                _log.LogWarning("kill failed ({Title}) HTTP {Status}", title, result.StatusCode);
                return false;
            }
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
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "kill failed ({Title})", title);
            return false;
        }
    }

    /// <summary>
    /// Season-pack remediation. A pack (one torrent -> many episodes) is never raced episode-by-episode;
    /// when its single torrent is dead — orphaned from the client (hash absent from the snapshot) or
    /// stalled with no seeds — continuously for the stall fuse, blocklist every pack record and force a
    /// fresh season search so the *arr grabs a different release. Honours DRY_RUN, baseline protection and
    /// the race cooldown; Sonarr-only (Radarr movies are never packs).
    /// </summary>
    private async Task RemediateDeadPacksAsync(
        ArrInstance inst,
        IReadOnlyDictionary<int, List<QueueRecord>> groups,
        IReadOnlyDictionary<string, HashSet<int>> hashToItems,
        IReadOnlyDictionary<string, TorrentInfo> qbt,
        DateTimeOffset now, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        foreach (var (dlid, itemIds) in hashToItems)
        {
            if (itemIds.Count <= 1) continue;              // singles are raced, not season-remediated
            if (_baselineDl.Contains(dlid)) continue;       // never manage the pre-existing backlog

            var packRecs = new List<QueueRecord>();
            foreach (var iid in itemIds)
                if (groups.TryGetValue(iid, out var g))
                    foreach (var r in g)
                        if (r.DownloadId.Equals(dlid, StringComparison.OrdinalIgnoreCase)) packRecs.Add(r);
            if (packRecs.Count == 0) continue;

            var head = packRecs[0];
            if (head.SeriesId is not int seriesId || head.SeasonNumber is not int seasonNumber) continue; // Sonarr-only

            var key = $"{inst.Name}:pack:{seriesId}:{seasonNumber}";
            seen.Add(key);
            if (!_packs.TryGetValue(key, out var pstate)) pstate = _packs[key] = new PackState();

            qbt.TryGetValue(dlid, out var t);
            var dead = RaceDecisions.IsDownloadDead(t);
            if (dead) pstate.DeadSinceUtc ??= now;
            else { pstate.DeadSinceUtc = null; pstate.LastIncidentType = null; }

            var deadFor = pstate.DeadSinceUtc is DateTimeOffset ds ? (now - ds).TotalSeconds : 0;
            var inCooldown = pstate.NextEligibleUtc is DateTimeOffset ne && now < ne;
            if (RaceDecisions.ShouldRemediatePack(deadFor, dead, inCooldown, _o))
                await RemediateDeadPackAsync(inst, seriesId, seasonNumber, packRecs, t, pstate, now, ct);
        }

        // Drop state for packs no longer present in this instance's queue.
        foreach (var stale in _packs.Keys
            .Where(k => k.StartsWith(inst.Name + ":pack:", StringComparison.Ordinal) && !seen.Contains(k)).ToList())
            _packs.Remove(stale);
    }

    private async Task RemediateDeadPackAsync(
        ArrInstance inst, int seriesId, int seasonNumber, IReadOnlyList<QueueRecord> packRecs,
        TorrentInfo? torrent, PackState pstate, DateTimeOffset now, CancellationToken ct)
    {
        var label = $"{inst.Name} S{seasonNumber:00} pack (series {seriesId})";
        var why = torrent is null ? "orphaned from client" : $"stalled ({torrent.State}, no seeds)";
        if (pstate.LastIncidentType != "season_pack_dead")
        {
            Incident("season_pack_dead",
                $"{label} '{Trunc(packRecs[0].Title, 50)}' is dead — {why} — blocklisting {packRecs.Count} record(s) + season re-search",
                inst.Name, seriesId);
            _events.Record(new RaceEvent { Kind = "season_pack_dead", Instance = inst.Name, ItemId = seriesId, Detail = Trunc(packRecs[0].Title, 60) });
            pstate.LastIncidentType = "season_pack_dead";
        }

        if (_o.DryRun)
        {
            _log.LogInformation("[dry-run] would blocklist {N} pack record(s) + SeasonSearch {Label}", packRecs.Count, label);
            _events.Record(new RaceEvent { Kind = "season_remediation", Instance = inst.Name, ItemId = seriesId, Outcome = "dry_run", Detail = label });
            pstate.NextEligibleUtc = now.AddSeconds(_o.RaceCooldownSeconds);
            return;
        }

        var removedAll = true;
        foreach (var rec in packRecs)
        {
            ArrMutationResult res;
            try { res = await _arr.DeleteQueueAsync(inst, rec.Id, removeFromClient: true, blocklist: true, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "season remediation: delete failed ({Label} rec {Id})", label, rec.Id); removedAll = false; continue; }
            // A 404 means the record already vanished (the torrent removal cascaded) — success for us.
            removedAll &= res.Succeeded || res.StatusCode == 404;
        }

        if (!removedAll)
        {
            _log.LogWarning("season remediation: not every pack record was removed for {Label}; retrying after cooldown", label);
            pstate.NextEligibleUtc = now.AddSeconds(_o.RaceCooldownSeconds);
            return;
        }

        var searched = await _arr.SeasonSearchAsync(inst, seriesId, seasonNumber, ct);
        _events.Record(new RaceEvent
        {
            Kind = "season_remediation", Instance = inst.Name, ItemId = seriesId,
            Outcome = searched.Succeeded ? "searched" : "search_failed", Detail = label,
        });
        if (searched.Succeeded)
            _log.LogInformation("SEASON RE-SEARCH {Label} (blocklisted dead pack, grabbing a replacement)", label);
        else
            _log.LogWarning("season remediation: SeasonSearch failed for {Label} HTTP {Status}", label, searched.StatusCode);

        pstate.NextEligibleUtc = now.AddSeconds(_o.RaceCooldownSeconds);
        pstate.DeadSinceUtc = null;
    }

    private void Incident(string type, string message, string? instance = null, int? itemId = null)
    {
        _state.AddIncident();
        _metrics.IncIncident(type);
        _events.Record(new RaceEvent { Kind = "incident", Instance = instance, ItemId = itemId, Outcome = type, Detail = message });
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

    // Season-pack remediation bookkeeping (in-memory; a restart re-primes the baseline that supersedes it).
    private sealed class PackState
    {
        public DateTimeOffset? DeadSinceUtc { get; set; }   // first tick the pack's torrent was observed dead (reset when alive)
        public DateTimeOffset? NextEligibleUtc { get; set; } // cooldown after a remediation attempt
        public string? LastIncidentType { get; set; }
    }

    private EngineItemState EnsureOwned(ArrInstance inst, int itemId)
    {
        var key = $"{inst.Name}:{itemId}";
        if (_owned.TryGetValue(key, out var state)) return state;
        state = new EngineItemState { Key = key, Instance = inst.Name, ItemId = itemId };
        _owned[key] = state;
        return state;
    }

    private EngineItemState EnsureQueueState(ArrInstance inst, int itemId, IReadOnlyList<QueueRecord> records, DateTimeOffset now)
    {
        var state = EnsureOwned(inst, itemId);
        var hashes = records.Select(r => r.DownloadId.ToLowerInvariant()).Where(h => h.Length > 0).Distinct().Order().ToList();
        var fingerprint = hashes.Count == 0
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', hashes)))).ToLowerInvariant();
        var changed = false;
        if (fingerprint is not null && fingerprint != state.QueueFingerprint)
        {
            state.QueueFingerprint = fingerprint;
            state.QueueFirstSeenUtc = now;
            state.RetryCount = 0;
            if (state.NextRetryUtc <= now) state.NextRetryUtc = null;
            state.LastIncidentType = null;
            changed = true;
        }
        if (state.QueueFirstSeenUtc is null)
        {
            state.QueueFirstSeenUtc = now;
            changed = true;
        }
        if (changed) SaveOwned(state);
        return state;
    }

    private static bool InRetry(EngineItemState state, DateTimeOffset now)
        => state.NextRetryUtc is DateTimeOffset until && now < until;

    private void ScheduleRetry(EngineItemState state, DateTimeOffset now)
    {
        state.RetryCount++;
        state.NextRetryUtc = now.AddSeconds(RaceDecisions.RetryDelaySeconds(state.RetryCount, _o));
        SaveOwned(state);
    }

    private void SetCooldown(EngineItemState state, DateTimeOffset now)
    {
        state.RetryCount = 0;
        state.NextRetryUtc = now.AddSeconds(_o.RaceCooldownSeconds);
        SaveOwned(state);
    }

    private void ResetRetry(EngineItemState state)
    {
        state.RetryCount = 0;
        state.NextRetryUtc = null;
        SaveOwned(state);
    }

    private void SaveOwned(EngineItemState state)
    {
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        _stateStore.Upsert(state);
    }

    private void RemoveOwned(string key)
    {
        _owned.Remove(key);
        _stateStore.Delete(key);
    }

    private static string AttemptLabel(GrabOutcome outcome) => outcome switch
    {
        GrabOutcome.Accepted => "accepted",
        GrabOutcome.AlreadyPresent => "already_present",
        GrabOutcome.Rejected => "rejected",
        GrabOutcome.Failed => "failed",
        GrabOutcome.DryRun => "dry_run",
        _ => "failed",
    };

    private readonly record struct Candidate(
        QueueRecord Record, string Hash, TorrentInfo? Torrent,
        double Speed, double Progress, double Age, double MaxSpeed, bool Baseline);

    private readonly record struct CandidateSearchResult(bool Succeeded, IReadOnlyList<Release> Releases);
}
