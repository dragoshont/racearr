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

    /// <summary>Which torrent client racearr races. <c>qbittorrent</c> reads live speed directly
    /// (full fidelity); <c>deluge</c> / <c>transmission</c> (BETA) read status through the
    /// Radarr/Sonarr queue instead — no per-client integration, at the cost of estimated speed and
    /// no private-tracker protection on that path.</summary>
    public string TorrentClient { get; init; } = "qbittorrent";

    /// <summary>True when download status is read through the *arr queue rather than qBittorrent directly.</summary>
    public bool UsesArrQueueProbe => !string.Equals(TorrentClient, "qbittorrent", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Optional incident webhook. Discord (<c>discord.com/api/webhooks/…</c>) is auto-detected
    /// and posted as <c>{"content":…}</c>; any other URL (Slack, Mattermost, generic) receives <c>{"text":…}</c>.</summary>
    public string? IncidentWebhookUrl { get; init; }

    /// <summary>ntfy server base URL (e.g. <c>https://ntfy.sh</c>). With <see cref="NtfyTopic"/> set,
    /// incidents are pushed to ntfy in addition to any <see cref="IncidentWebhookUrl"/>.</summary>
    public string? NtfyUrl { get; init; }
    /// <summary>ntfy topic to publish incidents to.</summary>
    public string? NtfyTopic { get; init; }
    /// <summary>Optional ntfy access token (sent as <c>Authorization: Bearer …</c>) for protected topics.</summary>
    public string? NtfyToken { get; init; }
    /// <summary>Optional ntfy priority (<c>1</c>..<c>5</c> / <c>min</c>..<c>urgent</c>).</summary>
    public string? NtfyPriority { get; init; }

    // ----- additional notification channels (all optional + off by default; tokens stay env-only) -----
    /// <summary>Telegram bot token (from @BotFather). With <see cref="TelegramChatId"/>, incidents are
    /// sent via the Telegram Bot API.</summary>
    public string? TelegramBotToken { get; init; }
    /// <summary>Telegram chat id (a user/group id, or <c>@channelname</c>).</summary>
    public string? TelegramChatId { get; init; }

    /// <summary>Gotify server base URL (e.g. <c>https://gotify.example.com</c>). With <see cref="GotifyToken"/>,
    /// incidents are pushed to Gotify.</summary>
    public string? GotifyUrl { get; init; }
    /// <summary>Gotify application token.</summary>
    public string? GotifyToken { get; init; }
    /// <summary>Gotify message priority (0..10).</summary>
    public int GotifyPriority { get; init; } = 5;

    /// <summary>Pushover application/API token. With <see cref="PushoverUser"/>, incidents are sent to Pushover.</summary>
    public string? PushoverToken { get; init; }
    /// <summary>Pushover user (or group) key.</summary>
    public string? PushoverUser { get; init; }
    /// <summary>Optional Pushover priority (<c>-2</c>..<c>2</c>).</summary>
    public string? PushoverPriority { get; init; }

    /// <summary>Apprise API notify endpoint (e.g. <c>http://apprise:8000/notify/&lt;key&gt;</c>) — one URL
    /// fans incidents out to 100+ services. See github.com/caronc/apprise-api.</summary>
    public string? AppriseUrl { get; init; }
    /// <summary>Optional Apprise tag to route the notification within the Apprise config.</summary>
    public string? AppriseTag { get; init; }

    /// <summary>Optional shared secret required on the Seerr webhook endpoint (env-only, never persisted).</summary>
    public string? WebhookToken { get; init; }

    // ----- reverse-proxy identity display (optional; racearr runs fully anonymous without it) -----
    /// <summary>Which forward-auth header scheme to read the signed-in identity from (DISPLAY ONLY).
    /// One of <c>authentik</c> (default), <c>authelia</c>, <c>oauth2-proxy</c>, <c>tinyauth</c>,
    /// <c>traefik</c>, <c>generic</c>, <c>custom</c>, or <c>none</c>. <c>custom</c> uses the
    /// <c>AUTH_PROXY_*_HEADER</c> overrides; any preset can also be overridden per-header.</summary>
    public string AuthProxy { get; init; } = "authentik";
    public string? AuthProxyUserHeader { get; init; }
    public string? AuthProxyNameHeader { get; init; }
    public string? AuthProxyEmailHeader { get; init; }
    public string? AuthProxyGroupsHeader { get; init; }

    /// <summary>Additional *arr instances beyond the primary Radarr/Sonarr, parsed from <c>ARR_INSTANCES</c>
    /// (<c>kind|url|apikey|label;…</c>). Lets racearr race across e.g. a 1080p and a 4K Radarr at once.</summary>
    public IReadOnlyList<ArrInstanceConfig> ExtraArrInstances { get; init; } = [];

    /// <summary>True if at least one *arr instance is configured (a primary API key or an extra instance).</summary>
    public bool HasAnyInstance =>
        !string.IsNullOrWhiteSpace(RadarrApiKey) || !string.IsNullOrWhiteSpace(SonarrApiKey) || ExtraArrInstances.Count > 0;

    /// <summary>Resolve the forward-auth header names for <see cref="AuthProxy"/> (with per-header
    /// overrides applied). Returns <c>null</c> when identity display is disabled (<c>AUTH_PROXY=none</c>)
    /// or no usable username header is configured.</summary>
    public ForwardAuthHeaders? ResolveForwardAuthHeaders()
    {
        var proxy = (AuthProxy ?? "authentik").Trim().ToLowerInvariant();
        if (proxy == "none") return null;
        var (u, n, e, g) = proxy switch
        {
            "authelia" or "tinyauth" or "traefik" or "generic"
                => ("Remote-User", "Remote-Name", "Remote-Email", "Remote-Groups"),
            "oauth2-proxy" or "oauth2proxy"
                => ("X-Forwarded-User", "X-Forwarded-Preferred-Username", "X-Forwarded-Email", "X-Forwarded-Groups"),
            "custom"
                => ("", "", "", ""),
            _ /* authentik (default) */
                => ("X-authentik-username", "X-authentik-name", "X-authentik-email", "X-authentik-groups"),
        };
        var user = AuthProxyUserHeader ?? u;
        return string.IsNullOrWhiteSpace(user)
            ? null
            : new ForwardAuthHeaders(user, AuthProxyNameHeader ?? n, AuthProxyEmailHeader ?? e, AuthProxyGroupsHeader ?? g);
    }

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

        // Only the clients racearr recognises; anything else falls back to qBittorrent (direct).
        static string NormalizeClient(string v)
        {
            var c = v.Trim().ToLowerInvariant();
            return c is "qbittorrent" or "deluge" or "transmission" ? c : "qbittorrent";
        }

        return new RacearrOptions
        {
            RadarrUrl = Str("RADARR_URL")?.TrimEnd('/'),
            RadarrApiKey = Str("RADARR_API_KEY"),
            SonarrUrl = Str("SONARR_URL")?.TrimEnd('/'),
            SonarrApiKey = Str("SONARR_API_KEY"),
            QbitUrl = (Str("QBIT_URL", "http://localhost:8080")!).TrimEnd('/'),
            QbitUsername = Str("QBIT_USERNAME"),
            QbitPassword = Str("QBIT_PASSWORD"),
            TorrentClient = NormalizeClient(Str("TORRENT_CLIENT", "qbittorrent")!),

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
            NtfyUrl = Str("NTFY_URL")?.TrimEnd('/'),
            NtfyTopic = Str("NTFY_TOPIC"),
            NtfyToken = Str("NTFY_TOKEN"),
            NtfyPriority = Str("NTFY_PRIORITY"),
            TelegramBotToken = Str("TELEGRAM_BOT_TOKEN"),
            TelegramChatId = Str("TELEGRAM_CHAT_ID"),
            GotifyUrl = Str("GOTIFY_URL")?.TrimEnd('/'),
            GotifyToken = Str("GOTIFY_TOKEN"),
            GotifyPriority = Int("GOTIFY_PRIORITY", 5),
            PushoverToken = Str("PUSHOVER_TOKEN"),
            PushoverUser = Str("PUSHOVER_USER"),
            PushoverPriority = Str("PUSHOVER_PRIORITY"),
            AppriseUrl = Str("APPRISE_URL")?.TrimEnd('/'),
            AppriseTag = Str("APPRISE_TAG"),
            WebhookToken = Str("WEBHOOK_TOKEN"),
            AuthProxy = Str("AUTH_PROXY", "authentik")!,
            AuthProxyUserHeader = Str("AUTH_PROXY_USER_HEADER"),
            AuthProxyNameHeader = Str("AUTH_PROXY_NAME_HEADER"),
            AuthProxyEmailHeader = Str("AUTH_PROXY_EMAIL_HEADER"),
            AuthProxyGroupsHeader = Str("AUTH_PROXY_GROUPS_HEADER"),
            ExtraArrInstances = ParseExtraInstances(Str("ARR_INSTANCES")),
        };
    }

    /// <summary>
    /// Parse <c>ARR_INSTANCES</c> — a <c>;</c>-separated list of extra instances, each
    /// <c>kind|url|apikey[|label]</c> (kind = <c>radarr</c>/<c>sonarr</c>). Malformed entries are
    /// skipped rather than failing startup, so one typo never takes racearr down.
    /// </summary>
    internal static IReadOnlyList<ArrInstanceConfig> ParseExtraInstances(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var res = new List<ArrInstanceConfig>();
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) continue; // need at least kind|url|apikey
            ArrKind? kind = parts[0].ToLowerInvariant() switch
            {
                "radarr" => ArrKind.Radarr,
                "sonarr" => ArrKind.Sonarr,
                _ => null,
            };
            if (kind is null || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2])) continue;
            var label = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : null;
            res.Add(new ArrInstanceConfig(kind.Value, parts[1].TrimEnd('/'), parts[2], label));
        }
        return res;
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

/// <summary>The reverse-proxy header names that carry the signed-in identity (display only). An empty
/// <see cref="Name"/>/<see cref="Email"/>/<see cref="Groups"/> means that field is not read.</summary>
public sealed record ForwardAuthHeaders(string User, string Name, string Email, string Groups);
