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

    private sealed class PagingStubHandler(int total = 222, bool truncateLastPage = false) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var page = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
            var remainder = Math.Max(0, total - 200);
            var firstId = page == 1 ? remainder + 1 : 1;
            var count = page == 1 ? Math.Min(200, total) : Math.Max(0, remainder - (truncateLastPage ? 1 : 0));
            var records = Enumerable.Range(0, count)
                .Select(offset => $$"""{"id":{{firstId + offset}},"title":"Episode {{firstId + offset}}"}""");
            var json = $$"""{"page":{{page}},"pageSize":200,"totalRecords":{{total}},"records":[{{string.Join(',', records)}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class SequenceStubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            return Task.FromResult(responses[Math.Min(_index++, responses.Length - 1)](request));
        }
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
            "indexer": "1337x", "indexerId": 3, "infoHash": "ABCDEF", "guid": "g1", "title": "Movie 1080p", "size": 1500000000 }
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
        Assert.Equal(1500000000, r.Size);                                   // size drives the fake/runt guard
    }

    [Fact]
    public async Task ArrClient_MapsQueueJson_LowercasesDownloadIdAndReadsSizeleft()
    {
        const string json = """
        { "totalRecords": 1, "records": [
          { "id": 7, "movieId": 55, "downloadId": "ABC123", "title": "Q",
            "size": 1000, "sizeleft": 250, "trackedDownloadState": "downloading", "trackedDownloadStatus": "warning" }
        ] }
        """;
        var client = new ArrClient(new HttpClient(new StubHandler(json)));
        var queue = await client.GetQueueAsync(RadarrInstance, CancellationToken.None);

        var q = Assert.Single(queue);
        Assert.Equal(7, q.Id);
        Assert.Equal(55, q.ItemId);            // movieId -> ItemId for a Radarr instance
        Assert.Equal("abc123", q.DownloadId);  // lowercased to match qBittorrent hash keys
        Assert.Equal(1000, q.Size);
        Assert.Equal(250, q.SizeLeft);
        Assert.Equal("downloading", q.TrackedDownloadState);
        Assert.Equal("warning", q.TrackedDownloadStatus);
    }

    [Fact]
    public async Task ArrClient_WantedMissing_PaginatesAllRecords()
    {
        var handler = new PagingStubHandler();
        var client = new ArrClient(new HttpClient(handler));

        var wanted = await client.GetWantedMissingAsync(RadarrInstance, CancellationToken.None);

        Assert.Equal(222, wanted.Count);
        Assert.Equal(222, wanted.Select(item => item.Id).Distinct().Count());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=1", handler.Requests[0].Query);
        Assert.Contains("page=2", handler.Requests[1].Query);
    }

    [Fact]
    public async Task ArrClient_WantedMissing_RejectsIncompleteFinalPage()
    {
        var client = new ArrClient(new HttpClient(new PagingStubHandler(truncateLastPage: true)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetWantedMissingAsync(RadarrInstance, CancellationToken.None));
    }

    [Fact]
    public async Task ArrClient_MapsQueueIndexer()
    {
        const string json = """
        { "totalRecords": 1, "records": [
          { "id": 7, "movieId": 55, "downloadId": "ABC123", "title": "Q",
            "indexer": "TorrentDownload", "size": 1000, "sizeleft": 250 }
        ] }
        """;
        var client = new ArrClient(new HttpClient(new StubHandler(json)));

        var record = Assert.Single(await client.GetQueueAsync(RadarrInstance, CancellationToken.None));

        Assert.Equal("TorrentDownload", record.Indexer);
    }

    [Fact]
    public async Task ArrClient_Grab_ReconcilesWrappedDuplicateAgainstQueue()
    {
        const string queue = """
        { "totalRecords": 1, "records": [
          { "id": 7, "movieId": 55, "downloadId": "", "title": "Show.S01E01 [Group]",
            "indexer": "Indexer", "size": 1000, "sizeleft": 500 }
        ] }
        """;
        var handler = new SequenceStubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(queue) });
        var client = new ArrClient(new HttpClient(handler));
        var release = new Release
        {
            Guid = "g", IndexerId = 1, Indexer = "Indexer", Title = "Show S01E01", Size = 1000,
        };

        var result = await client.GrabAsync(RadarrInstance, 55, release, CancellationToken.None);

        Assert.Equal(GrabOutcome.AlreadyPresent, result.Outcome);
        Assert.Equal([HttpMethod.Post, HttpMethod.Get], handler.Methods);
    }
}
