using System.Net;
using System.Text;
using Prometheus;
using Racearr.Web;
using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// Web-infrastructure tests: they lock in the Prometheus exposition (names/labels/buckets the
/// existing Grafana dashboard + alerts depend on) and the *arr v3 JSON → domain mapping, which
/// the pure engine tests can't cover.
/// </summary>
public class WebInfraTests
{
    [Fact]
    public async Task Metrics_Exposition_HasPythonCompatibleNamesLabelsAndBuckets()
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new RacearrMetrics(Metrics.WithCustomRegistry(registry));
        metrics.PreInitialize(new RacearrOptions { RadarrApiKey = "x", SonarrApiKey = "y" });
        metrics.IncPickup("radarr", "in_sla");
        metrics.IncIncident("speed_sla");

        using var ms = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(ms, CancellationToken.None);
        var text = Encoding.UTF8.GetString(ms.ToArray());

        // Counter names + label sets (must match racearr.py exactly).
        Assert.Contains("racearr_pickups_total{instance=\"radarr\",result=\"in_sla\"}", text);
        Assert.Contains("racearr_incidents_total{type=\"speed_sla\"}", text);
        Assert.Contains("racearr_race_outcomes_total{instance=\"sonarr\",outcome=\"won_target\"}", text);
        // Histogram bucket boundaries (must match the Python bucket arrays exactly).
        Assert.Contains("racearr_pickup_latency_seconds_bucket{le=\"15\"}", text);
        Assert.Contains("racearr_pickup_latency_seconds_bucket{le=\"600\"}", text);
        Assert.Contains("racearr_race_winner_mbps_bucket{le=\"0.5\"}", text);
        Assert.Contains("racearr_time_to_target_seconds_bucket{le=\"30\"}", text);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private static ArrInstance RadarrInstance => new() { Kind = ArrKind.Radarr, Url = "http://arr", ApiKey = "k" };

    [Fact]
    public async Task ArrClient_MapsReleaseJson_IncludingNestedQualityAndRejections()
    {
        const string json = """
        [
          { "protocol": "torrent", "seeders": 42, "rejected": true,
            "rejections": ["Release already meets cutoff"],
            "quality": { "quality": { "resolution": 1080, "name": "Bluray-1080p" } },
            "indexer": "1337x", "indexerId": 3, "infoHash": "ABCDEF", "guid": "g1", "title": "Movie 1080p" }
        ]
        """;
        var client = new ArrClient(new HttpClient(new StubHandler(json)));
        var releases = await client.GetReleasesAsync(RadarrInstance, 1, CancellationToken.None);

        var r = Assert.Single(releases);
        Assert.Equal("torrent", r.Protocol);
        Assert.Equal(42, r.Seeders);
        Assert.Equal(1080, r.Resolution);                                   // nested quality.quality.resolution
        Assert.True(r.Rejected);
        Assert.Equal("Release already meets cutoff", Assert.Single(r.Rejections));
        Assert.Equal(3, r.IndexerId);
        Assert.Equal("ABCDEF", r.InfoHash);
    }

    [Fact]
    public async Task ArrClient_MapsQueueJson_LowercasesDownloadIdAndReadsSizeleft()
    {
        const string json = """
        { "records": [
          { "id": 7, "movieId": 55, "downloadId": "ABC123", "title": "Q",
            "size": 1000, "sizeleft": 250, "trackedDownloadState": "downloading" }
        ] }
        """;
        var client = new ArrClient(new HttpClient(new StubHandler(json)));
        var queue = await client.GetQueueAsync(RadarrInstance, CancellationToken.None);

        var q = Assert.Single(queue);
        Assert.Equal(7, q.Id);
        Assert.Equal(55, q.ItemId);            // movieId -> ItemId for a Radarr instance
        Assert.Equal("abc123", q.DownloadId);  // lowercased to match qBittorrent hash keys
        Assert.Equal(250, q.SizeLeft);
        Assert.Equal("downloading", q.TrackedDownloadState);
    }
}
