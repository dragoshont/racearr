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

    /// <summary>Race outcome label: whether the kept (fastest) candidate reached the target speed.</summary>
    public static string RaceOutcome(bool haveWinner)
        => haveWinner ? "won_target" : "kept_below_target";
}
