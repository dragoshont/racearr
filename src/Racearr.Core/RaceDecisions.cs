namespace Racearr.Core;

/// <summary>
/// Pure, side-effect-free decision logic ported verbatim from <c>racearr.py</c>. Kept apart
/// from all IO so it can be exhaustively unit-tested for behavioural parity with the Python
/// engine (ADR-0001, "Core parity tests"). Every method here has a 1:1 Python counterpart.
/// </summary>
public static class RaceDecisions
{
    /// <summary>Bytes per megabyte, matching the Python <c>MB = 1024 * 1024</c>.</summary>
    public const double MB = 1024 * 1024;

    /// <summary>
    /// A release is raceable if it is <b>not</b> rejected, or rejected <b>only</b> because an
    /// equal-quality release already meets the cutoff / is not an upgrade — precisely the case a
    /// manual <c>POST /release</c> grabs anyway. Any other rejection reason (quality-not-wanted,
    /// size, …) makes it non-raceable. Port of <c>_raceable_rejection</c>.
    /// </summary>
    public static bool IsRaceableRejection(Release r)
    {
        if (!r.Rejected) return true;
        var rej = r.Rejections;
        return rej.Count > 0 && rej.All(x =>
        {
            var s = x.ToLowerInvariant();
            return s.Contains("cutoff") || s.Contains("upgrade");
        });
    }

    /// <summary>True if the release comes from a protected (private) indexer. Port of <c>_is_private_release</c>.</summary>
    public static bool IsPrivateRelease(Release r, RacearrOptions o)
    {
        var name = r.Indexer.ToLowerInvariant();
        return o.PrivateIndexers.Any(name.Contains);
    }

    /// <summary>True if the torrent is on a protected (private) tracker. Port of <c>_is_private_torrent</c>.</summary>
    public static bool IsPrivateTorrent(TorrentInfo? t, RacearrOptions o)
    {
        if (t is null) return false;
        var blob = (t.Tracker + " " + t.MagnetUri).ToLowerInvariant();
        return o.PrivateTrackerDomains.Any(blob.Contains);
    }

    /// <summary>
    /// Highest-seeded, 1080p-first torrent releases suitable to race — including the same-quality
    /// alternates the *arr auto-rejects as "already meets cutoff". Port of <c>candidate_releases</c>
    /// (minus the IO fetch). Filters then sorts by descending seeders (stable).
    /// </summary>
    public static IReadOnlyList<Release> SelectCandidates(
        IEnumerable<Release> releases, IReadOnlySet<string> excludeHashes, RacearrOptions o)
    {
        var outp = new List<Release>();
        foreach (var r in releases)
        {
            if (r.Protocol != "torrent") continue;
            if (r.Seeders < o.RaceMinSeeders) continue;
            // Reject implausibly-small releases (fakes/samples) up front; unknown size (0) is allowed.
            if (o.RaceMinSizeMb > 0 && r.Size > 0 && r.Size < o.RaceMinSizeMb * (long)MB) continue;
            // Only reject a *known* resolution above the cap (unknown res = 0 is allowed).
            if (o.RaceMaxResolution > 0 && r.Resolution > 0 && r.Resolution > o.RaceMaxResolution) continue;
            if (!IsRaceableRejection(r)) continue;
            if (o.ProtectPrivate && IsPrivateRelease(r, o)) continue;
            var ih = r.InfoHash.ToLowerInvariant();
            if (ih.Length > 0 && excludeHashes.Contains(ih)) continue;
            outp.Add(r);
        }
        // OrderByDescending is stable in .NET, preserving the source order among equal seeders.
        return outp.OrderByDescending(r => r.Seeders).ToList();
    }

    /// <summary>Pickup result label: within the pickup-SLA window, or breached. Port of the pickup classification.</summary>
    public static string ClassifyPickup(double latencySeconds, RacearrOptions o)
        => latencySeconds <= o.PickupSlaSeconds ? "in_sla" : "breached";

    /// <summary>
    /// Whether a slow, non-baseline item should trigger a race: it has been downloading at least
    /// <c>SpeedSlaSeconds</c>, its best observed speed is below <c>SpeedSlaMbps</c>, and it is not
    /// in cooldown. Port of the speed-SLA trigger condition.
    /// </summary>
    public static bool ShouldStartRace(double oldestAgeSeconds, double bestSpeedBytes, bool inCooldown, RacearrOptions o)
        => oldestAgeSeconds >= o.SpeedSlaSeconds
           && bestSpeedBytes < o.SpeedSlaMbps * MB
           && !inCooldown;

    /// <summary>
    /// True when qBittorrent reports a torrent as definitively stuck — stalled (no connections
    /// transferring) or fetching metadata — with zero connected seeds. Such a download will not
    /// recover on its own, so racearr can race alternates on a shorter fuse than the slow-speed
    /// grace. A stalled torrent that still has connected seeds is left to the normal grace (it may
    /// pick up). Pure/side-effect-free.
    /// </summary>
    public static bool IsStalledDead(TorrentInfo? t)
        => t is not null && t.NumSeeds <= 0 && (t.State == "stalledDL" || t.State == "metaDL");

    /// <summary>Whether a non-baseline item with a definitively-stalled download should race on the
    /// shorter stall fuse (before the normal slow-speed grace has elapsed).</summary>
    public static bool ShouldStartRaceStalled(double oldestAgeSeconds, bool anyStalledDead, bool inCooldown, RacearrOptions o)
        => anyStalledDead && oldestAgeSeconds >= o.RaceStallSeconds && !inCooldown;

    /// <summary>Race outcome label: whether the kept (fastest) candidate reached the target speed.</summary>
    public static string RaceOutcome(bool haveWinner)
        => haveWinner ? "won_target" : "kept_below_target";

    /// <summary>
    /// A downloading candidate is "runt-sized" — implausibly small to be the real media — and must
    /// never be allowed to win a race. A tiny fake / sample / malware torrent downloads fastest and
    /// finishes first, so a pure speed heuristic would keep it and cull the genuine releases. Judged
    /// absolutely (below <c>RaceMinSizeMb</c>) and relatively (a fraction <c>RaceRuntRatio</c> of the
    /// largest same-item candidate). Unknown size (0) is never treated as a runt. Pure/side-effect-free.
    /// </summary>
    public static bool IsRuntSize(long sizeBytes, long largestCandidateBytes, RacearrOptions o)
    {
        if (sizeBytes <= 0) return false;
        if (o.RaceMinSizeMb > 0 && sizeBytes < o.RaceMinSizeMb * (long)MB) return true;
        if (o.RaceRuntRatio > 0 && largestCandidateBytes > 0 && sizeBytes < largestCandidateBytes * o.RaceRuntRatio) return true;
        return false;
    }

    /// <summary>
    /// True when a download finished but the *arr cannot import it — a terminal dead-end for
    /// time-to-Plex. Covers <c>importBlocked</c> (manual intervention needed), <c>importFailed</c>
    /// and <c>failedPending</c>. A normal, transient <c>importPending</c> is deliberately NOT treated
    /// as failed (the import may still be completing). Pure/side-effect-free.
    /// </summary>
    public static bool IsImportFailed(QueueRecord rec)
        => rec.TrackedDownloadState is "importBlocked" or "importFailed" or "failedPending";
}
