namespace Racearr.Core;

/// <summary>
/// A persisted, runtime-tunable setting (the SLA knobs). Secrets (API keys) and connection URLs
/// deliberately stay environment-only — they are bootstrap + sensitive, never written to the DB.
/// </summary>
public sealed class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

/// <summary>
/// An append-only history event (pickups, races, incidents, kills) persisted for the UI (Phase 3)
/// and audit. The engine emits these as side-effects; they never influence decisions.
/// </summary>
public sealed class RaceEvent
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>One of: <c>pickup</c>, <c>race_started</c>, <c>race_outcome</c>, <c>incident</c>, <c>kill</c>.</summary>
    public required string Kind { get; set; }

    /// <summary><c>radarr</c> / <c>sonarr</c>, or null for instance-less events.</summary>
    public string? Instance { get; set; }

    /// <summary>The *arr item id the event concerns, when applicable.</summary>
    public int? ItemId { get; set; }

    public string Detail { get; set; } = "";

    /// <summary><c>in_sla</c>/<c>breached</c>/<c>won_target</c>/<c>kept_below_target</c>, or the incident type.</summary>
    public string? Outcome { get; set; }

    /// <summary>Winner speed in MB/s for <c>race_outcome</c> events.</summary>
    public double? Mbps { get; set; }
}

/// <summary>Append-only sink for history events; implemented over the database in the web host.</summary>
public interface IEventSink
{
    void Record(RaceEvent evt);
}

/// <summary>A no-op sink for tests and history-less runs.</summary>
public sealed class NullEventSink : IEventSink
{
    public static readonly NullEventSink Instance = new();
    public void Record(RaceEvent evt) { }
}

/// <summary>Read-side history query for the UI: recent events, newest first, optionally by kind.</summary>
public interface IEventHistory
{
    IReadOnlyList<RaceEvent> Recent(int limit, string? kind = null);
}

/// <summary>
/// Persisted settings store: seeds the tunable knobs from environment-derived defaults on first run
/// and lets them be read/edited (the Phase 3 UI writes here). Effective config = DB value ?? env.
/// </summary>
public interface ISettingsStore
{
    /// <summary>Insert any missing keys from <paramref name="defaults"/>, then return all persisted settings.</summary>
    IReadOnlyDictionary<string, string> SeedAndLoad(IReadOnlyDictionary<string, string> defaults);
    IReadOnlyDictionary<string, string> GetAll();
    void Set(string key, string value);
}

/// <summary>The setting keys that are persisted + runtime-tunable. Secrets and URLs stay env-only.</summary>
public static class SettingKeys
{
    public static readonly IReadOnlyList<string> Tunable =
    [
        "POLL_SECONDS", "PICKUP_SLA_SECONDS", "SPEED_SLA_SECONDS", "SPEED_SLA_MBPS",
        "RACE_TARGET_MBPS", "RACE_CULL_AFTER_SECONDS", "RACE_MONITOR_SECONDS", "RACE_COOLDOWN_SECONDS",
        "MAX_CONCURRENT_PER_ITEM", "MAX_ACTIVE_RACES", "RACE_MIN_SEEDERS", "RACE_MAX_RESOLUTION",
        "PROTECT_PRIVATE", "DRY_RUN",
    ];
}
