using System.Collections.Concurrent;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// BETA universal download-status source. Instead of talking to a specific torrent client, it reads
/// the aggregated Radarr + Sonarr <c>/queue</c> — which already normalises qBittorrent, Deluge,
/// Transmission, rTorrent, SABnzbd, etc. behind one API. Progress comes straight from the queue;
/// speed is derived from how much <c>sizeleft</c> shrank between polls (with the queue's
/// <c>timeleft</c> as a first-tick fallback). Tracker URL and seed count are not exposed by the
/// queue, so private-tracker protection cannot run on this path — which is why it is labelled beta.
/// </summary>
public sealed class ArrQueueProbe(IArrClient arr, RacearrOptions options, ILogger<ArrQueueProbe> log) : IQbitClient
{
    private readonly IReadOnlyList<ArrInstance> _instances = ArrInstance.FromOptions(options);
    // hash -> (sizeleft, observedUtc) from the previous poll, for Δ-based speed.
    private readonly ConcurrentDictionary<string, (long SizeLeft, DateTimeOffset At)> _last = new();

    public async Task<TorrentSnapshot> GetByHashAsync(CancellationToken ct)
    {
        var records = new List<QueueRecord>();
        var reachedAny = false;
        foreach (var inst in _instances)
        {
            try
            {
                records.AddRange(await arr.GetQueueAsync(inst, ct));
                reachedAny = true;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "queue probe: {Kind} queue unavailable", inst.Name);
            }
        }

        // If no *arr answered, the snapshot is unavailable — never treat that as "all torrents at 0 B/s".
        if (!reachedAny) return TorrentSnapshot.Unavailable;

        var now = DateTimeOffset.UtcNow;
        var items = new Dictionary<string, TorrentInfo>();
        var seen = new HashSet<string>();

        // One torrent (hash) can back several queue rows (a season pack -> many episodes); collapse to
        // the largest row so the download is measured once.
        foreach (var rec in records
                     .Where(r => !string.IsNullOrEmpty(r.DownloadId))
                     .GroupBy(r => r.DownloadId)
                     .Select(g => g.OrderByDescending(r => r.Size).First()))
        {
            seen.Add(rec.DownloadId);
            var progress = rec.Size > 0 ? Math.Clamp(1 - (double)rec.SizeLeft / rec.Size, 0, 1) : 0;

            double speed = 0;
            if (_last.TryGetValue(rec.DownloadId, out var prev))
            {
                var dt = (now - prev.At).TotalSeconds;
                var deltaBytes = prev.SizeLeft - rec.SizeLeft; // shrinking sizeleft = bytes downloaded
                if (dt > 0 && deltaBytes > 0) speed = deltaBytes / dt;
            }
            // First-tick fallback (no delta yet): the *arr's own remaining-time estimate.
            if (speed <= 0 && rec.TimeLeftSeconds is > 0 && rec.SizeLeft > 0)
                speed = rec.SizeLeft / rec.TimeLeftSeconds.Value;

            _last[rec.DownloadId] = (rec.SizeLeft, now);

            items[rec.DownloadId] = new TorrentInfo
            {
                Name = rec.Title,
                DlSpeed = speed,
                Progress = progress,
                Size = rec.Size,
                Eta = rec.TimeLeftSeconds is > 0 ? (long)rec.TimeLeftSeconds.Value : 0,
                State = rec.TrackedDownloadState ?? "",
                // Not available through the *arr queue (the beta limitation): no tracker, no seed count.
                Tracker = "",
                NumSeeds = 0,
            };
        }

        // Forget torrents that left the queue so the delta-state map doesn't grow unbounded.
        foreach (var stale in _last.Keys.Where(k => !seen.Contains(k)).ToList())
            _last.TryRemove(stale, out _);

        return new TorrentSnapshot(true, items);
    }
}
