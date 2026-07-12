namespace Racearr.Web;

/// <summary>Human-readable presentation metadata for a tunable setting (UI only).</summary>
public sealed record SettingInfo(string Key, string Label, string Help, string Unit, string Group, bool IsBool);

/// <summary>
/// Friendly labels, help text, units and grouping for the tunable knobs so the Settings screen
/// reads in plain language instead of raw environment-variable names. Order defines display order.
/// </summary>
public static class SettingsCatalog
{
    public static readonly IReadOnlyList<SettingInfo> All =
    [
        new("POLL_SECONDS", "Check interval", "How often racearr looks at the download queues.", "seconds", "Timing", false),
        new("PICKUP_SLA_SECONDS", "Pickup deadline", "A requested title must start downloading within this long, or it raises an incident.", "seconds", "Timing", false),
        new("SPEED_SLA_SECONDS", "Slow-download grace", "How long a download may crawl before racing kicks in.", "seconds", "Timing", false),
        new("SPEED_SLA_MBPS", "Too-slow threshold", "Below this speed within the grace window, the title is raced.", "MB/s", "Racing", false),
        new("RACE_TARGET_MBPS", "Good-enough speed", "A racing alternate at or above this speed wins the race.", "MB/s", "Racing", false),
        new("RACE_CULL_AFTER_SECONDS", "Drop losers after", "Once a winner is found, slower alternates are removed after this long.", "seconds", "Racing", false),
        new("RACE_MONITOR_SECONDS", "Race time limit", "Give up racing after this long and keep the fastest so far.", "seconds", "Racing", false),
        new("RACE_COOLDOWN_SECONDS", "Cooldown between races", "Wait this long before racing the same title again.", "seconds", "Racing", false),
        new("RACE_RETRY_MAX_SECONDS", "Maximum retry delay", "Cap the exponential back-off after repeated failed or empty release searches.", "seconds", "Racing", false),
        new("MAX_CONCURRENT_PER_ITEM", "Parallel tries per title", "How many alternates may download at once for one movie or episode.", "downloads", "Racing", false),
        new("MAX_ACTIVE_RACES", "Max races at once", "Cap on races running across everything at the same time.", "races", "Racing", false),
        new("RACE_MIN_SEEDERS", "Minimum seeders", "Only race alternate releases with at least this many seeders.", "seeders", "Racing", false),
        new("RACE_MAX_RESOLUTION", "Max resolution to race", "Don't grab racing alternates above this vertical resolution.", "pixels", "Racing", false),
        new("RACE_MIN_SIZE_MB", "Minimum release size", "Downloads smaller than this are treated as fakes — a tiny torrent that races fast but carries no real media is blocklisted, never kept. Typical fake/sample torrents are under 10 MB; raise it only if your library holds very short or ultra-compressed legitimate media.", "MB", "Racing", false),
        new("RACE_RUNT_RATIO", "Fake-vs-real size ratio", "Optional (0 = off): a racing download smaller than this fraction of the biggest alternate for the same title is treated as a fake. Off by default so fast low-quality releases aren't dropped.", "ratio 0–1", "Racing", false),
        new("RACE_STALL_SECONDS", "Stalled-download fuse", "A download stuck with no peers (stalled, or fetching metadata) is raced after this long — sooner than the slow-download grace — because it won't recover on its own.", "seconds", "Racing", false),
        new("PROTECT_PRIVATE", "Protect private trackers", "Never delete private-tracker torrents (avoids hit-and-run bans) — detach only.", "", "Safety", true),
    ];
}
