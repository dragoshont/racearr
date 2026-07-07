using System.Text.Json.Serialization;

namespace Racearr.Core;

/// <summary>
/// Thread-safe, in-memory runtime state of the race engine. This is the single source of
/// truth that both the <c>/status</c> endpoint and the Prometheus gauges read from. There is
/// deliberately no persistence here — like the Python original, live race state is ephemeral
/// and re-primed from the *arr queues on start (see ADR-0001 "re-baseline on restart").
/// </summary>
public sealed class RaceEngineState
{
    private readonly Lock _gate = new();

    private long _loops;
    private DateTimeOffset _lastLoopUtc;
    private long _incidents;
    private long _racesStarted;
    private long _candidatesGrabbed;
    private long _losersKilled;

    public RaceEngineState(bool dryRun) => DryRun = dryRun;

    public bool DryRun { get; }

    private int _managedDownloads;
    private int _activeRaces;

    /// <summary>Count of downloads first seen after startup that the engine is actively managing.</summary>
    public int ManagedDownloads
    {
        get => Volatile.Read(ref _managedDownloads);
        set => Volatile.Write(ref _managedDownloads, value);
    }

    /// <summary>Count of races currently in progress.</summary>
    public int ActiveRaces
    {
        get => Volatile.Read(ref _activeRaces);
        set => Volatile.Write(ref _activeRaces, value);
    }

    public long Loops => Interlocked.Read(ref _loops);
    public long Incidents => Interlocked.Read(ref _incidents);
    public long RacesStarted => Interlocked.Read(ref _racesStarted);
    public long CandidatesGrabbed => Interlocked.Read(ref _candidatesGrabbed);
    public long LosersKilled => Interlocked.Read(ref _losersKilled);

    public void MarkLoop()
    {
        Interlocked.Increment(ref _loops);
        lock (_gate) _lastLoopUtc = DateTimeOffset.UtcNow;
    }

    public void AddIncident() => Interlocked.Increment(ref _incidents);
    public void AddRaceStarted() => Interlocked.Increment(ref _racesStarted);
    public void AddCandidatesGrabbed(long n) => Interlocked.Add(ref _candidatesGrabbed, n);
    public void AddLosersKilled(long n) => Interlocked.Add(ref _losersKilled, n);

    /// <summary>Seconds since the control loop last ticked (0 before the first tick).</summary>
    public double LastLoopAgeSeconds
    {
        get
        {
            lock (_gate)
                return _lastLoopUtc == default ? 0 : (DateTimeOffset.UtcNow - _lastLoopUtc).TotalSeconds;
        }
    }

    public StatusSnapshot Snapshot() => new(
        Loops, Incidents, RacesStarted, CandidatesGrabbed, LosersKilled,
        ActiveRaces, ManagedDownloads, DryRun, Math.Round(LastLoopAgeSeconds, 1));
}

/// <summary>JSON shape returned by <c>GET /status</c> (field names mirror the Python service).</summary>
public sealed record StatusSnapshot(
    [property: JsonPropertyName("loops")] long Loops,
    [property: JsonPropertyName("incidents")] long Incidents,
    [property: JsonPropertyName("races_started")] long RacesStarted,
    [property: JsonPropertyName("candidates_grabbed")] long CandidatesGrabbed,
    [property: JsonPropertyName("losers_killed")] long LosersKilled,
    [property: JsonPropertyName("active_races")] int ActiveRaces,
    [property: JsonPropertyName("managed_downloads")] int ManagedDownloads,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("last_loop_age_seconds")] double LastLoopAgeSeconds);
