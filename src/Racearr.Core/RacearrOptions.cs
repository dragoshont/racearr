namespace Racearr.Core;

/// <summary>
/// Strongly-typed configuration for racearr, sourced from environment variables so the
/// container stays config-file-free and drop-in compatible with the Python <c>racearr.py</c>
/// contract. SLA thresholds may later be overridden at runtime from the database (Phase 2),
/// but the environment remains the bootstrap source of truth (API keys, URLs).
/// </summary>
public sealed class RacearrOptions
{
    // ----- connections -----
    public string? RadarrUrl { get; init; }
    public string? RadarrApiKey { get; init; }
    public string? SonarrUrl { get; init; }
    public string? SonarrApiKey { get; init; }
    public string QbitUrl { get; init; } = "http://localhost:8080";
    public string? QbitUsername { get; init; }
    public string? QbitPassword { get; init; }

    // ----- SLA contract (see ADR-0001; mirrors the Python defaults exactly) -----
    public int PollSeconds { get; init; } = 12;
    public int PickupSlaSeconds { get; init; } = 180;
    public int SpeedSlaSeconds { get; init; } = 120;
    public double SpeedSlaMbps { get; init; } = 1.0;
    public double RaceTargetMbps { get; init; } = 2.0;
    public int RaceCullAfterSeconds { get; init; } = 60;
    public int RaceMonitorSeconds { get; init; } = 180;
    public int RaceCooldownSeconds { get; init; } = 600;
    public int RaceRetryMaxSeconds { get; init; } = 21600;
    public int MaxConcurrentPerItem { get; init; } = 4;
    public int MaxActiveRaces { get; init; } = 6;
    public int RaceMinSeeders { get; init; } = 3;
    public int RaceMaxResolution { get; init; } = 1080;

    /// <summary>Releases/downloads smaller than this are treated as fakes — a runt that races fast
    /// but carries no real media (a sample, a *.lnk/*.zipx, malware). 0 disables the absolute floor.</summary>
    public int RaceMinSizeMb { get; init; } = 50;
    /// <summary>A downloading candidate smaller than this fraction of the largest same-item candidate
    /// is a runt/fake and may never win a race. 0 disables (default): racearr prefers fast low-quality
    /// releases, so the absolute floor + import-failure eviction are the primary fake guards; enable
    /// this only for libraries where a much-smaller alternate is reliably a fake.</summary>
    public double RaceRuntRatio { get; init; } = 0.0;

    /// <summary>A definitively-stalled / metadata-stuck download with no connected peers is raced on
    /// this shorter fuse instead of waiting the full slow-speed grace (it will not recover on its own).</summary>
    public int RaceStallSeconds { get; init; } = 45;

    // ----- safety -----
    public bool ProtectPrivate { get; init; } = true;
    public IReadOnlyList<string> PrivateIndexers { get; init; } = [];
    public IReadOnlyList<string> PrivateTrackerDomains { get; init; } = [];

    /// <summary>When true, racearr observes and logs but never grabs or kills (kill switch).</summary>
    public bool DryRun { get; init; } = true;

    // ----- runtime -----
    public int HealthPort { get; init; } = 9797;
    public string? IncidentWebhookUrl { get; init; }

    /// <summary>Optional shared secret required on the Seerr webhook endpoint (env-only, never persisted).</summary>
    public string? WebhookToken { get; init; }

    /// <summary>True if at least one *arr instance is configured (an API key present).</summary>
    public bool HasAnyInstance => !string.IsNullOrWhiteSpace(RadarrApiKey) || !string.IsNullOrWhiteSpace(SonarrApiKey);

    /// <summary>
    /// Build options from an environment getter (defaults to the process environment).
    /// The getter indirection keeps this unit-testable without mutating global state.
    /// </summary>
    public static RacearrOptions FromEnvironment(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        string? Str(string key, string? dflt = null)
        {
            var v = get(key);
            return string.IsNullOrWhiteSpace(v) ? dflt : v.Trim();
        }

        int Int(string key, int dflt)
            => int.TryParse(get(key), out var v) ? v : dflt;

        double Dbl(string key, double dflt)
            => double.TryParse(get(key), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;

        bool Bool(string key, bool dflt)
        {
            var v = get(key);
            if (string.IsNullOrWhiteSpace(v)) return dflt;
            return v.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
        }

        IReadOnlyList<string> List(string key)
            => (get(key) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .ToArray();

        return new RacearrOptions
        {
            RadarrUrl = Str("RADARR_URL")?.TrimEnd('/'),
            RadarrApiKey = Str("RADARR_API_KEY"),
            SonarrUrl = Str("SONARR_URL")?.TrimEnd('/'),
            SonarrApiKey = Str("SONARR_API_KEY"),
            QbitUrl = (Str("QBIT_URL", "http://localhost:8080")!).TrimEnd('/'),
            QbitUsername = Str("QBIT_USERNAME"),
            QbitPassword = Str("QBIT_PASSWORD"),

            PollSeconds = Int("POLL_SECONDS", 12),
            PickupSlaSeconds = Int("PICKUP_SLA_SECONDS", 180),
            SpeedSlaSeconds = Int("SPEED_SLA_SECONDS", 120),
            SpeedSlaMbps = Dbl("SPEED_SLA_MBPS", 1.0),
            RaceTargetMbps = Dbl("RACE_TARGET_MBPS", 2.0),
            RaceCullAfterSeconds = Int("RACE_CULL_AFTER_SECONDS", 60),
            RaceMonitorSeconds = Int("RACE_MONITOR_SECONDS", 180),
            RaceCooldownSeconds = Int("RACE_COOLDOWN_SECONDS", 600),
            RaceRetryMaxSeconds = Int("RACE_RETRY_MAX_SECONDS", 21600),
            MaxConcurrentPerItem = Int("MAX_CONCURRENT_PER_ITEM", 4),
            MaxActiveRaces = Int("MAX_ACTIVE_RACES", 6),
            RaceMinSeeders = Int("RACE_MIN_SEEDERS", 3),
            RaceMaxResolution = Int("RACE_MAX_RESOLUTION", 1080),
            RaceMinSizeMb = Int("RACE_MIN_SIZE_MB", 50),
            RaceRuntRatio = Dbl("RACE_RUNT_RATIO", 0.0),
            RaceStallSeconds = Int("RACE_STALL_SECONDS", 45),

            ProtectPrivate = Bool("PROTECT_PRIVATE", true),
            PrivateIndexers = List("PRIVATE_INDEXERS"),
            PrivateTrackerDomains = List("PRIVATE_TRACKER_DOMAINS"),

            DryRun = Bool("DRY_RUN", true),
            HealthPort = Int("HEALTH_PORT", 9797),
            IncidentWebhookUrl = Str("INCIDENT_WEBHOOK_URL"),
            WebhookToken = Str("WEBHOOK_TOKEN"),
        };
    }

    /// <summary>The tunable SLA knobs as a string map, used to seed + persist the settings store.</summary>
    public IReadOnlyDictionary<string, string> TunableSettings()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new Dictionary<string, string>
        {
            ["POLL_SECONDS"] = PollSeconds.ToString(inv),
            ["PICKUP_SLA_SECONDS"] = PickupSlaSeconds.ToString(inv),
            ["SPEED_SLA_SECONDS"] = SpeedSlaSeconds.ToString(inv),
            ["SPEED_SLA_MBPS"] = SpeedSlaMbps.ToString(inv),
            ["RACE_TARGET_MBPS"] = RaceTargetMbps.ToString(inv),
            ["RACE_CULL_AFTER_SECONDS"] = RaceCullAfterSeconds.ToString(inv),
            ["RACE_MONITOR_SECONDS"] = RaceMonitorSeconds.ToString(inv),
            ["RACE_COOLDOWN_SECONDS"] = RaceCooldownSeconds.ToString(inv),
            ["RACE_RETRY_MAX_SECONDS"] = RaceRetryMaxSeconds.ToString(inv),
            ["MAX_CONCURRENT_PER_ITEM"] = MaxConcurrentPerItem.ToString(inv),
            ["MAX_ACTIVE_RACES"] = MaxActiveRaces.ToString(inv),
            ["RACE_MIN_SEEDERS"] = RaceMinSeeders.ToString(inv),
            ["RACE_MAX_RESOLUTION"] = RaceMaxResolution.ToString(inv),
            ["RACE_MIN_SIZE_MB"] = RaceMinSizeMb.ToString(inv),
            ["RACE_RUNT_RATIO"] = RaceRuntRatio.ToString(inv),
            ["RACE_STALL_SECONDS"] = RaceStallSeconds.ToString(inv),
            ["PROTECT_PRIVATE"] = ProtectPrivate ? "true" : "false",
            // DRY_RUN is deliberately absent: it is an env-only kill switch, never persisted.
        };
    }
}
