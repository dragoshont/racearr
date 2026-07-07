using System.Net;
using Racearr.Core;
using Racearr.Web;
using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// Phase-2 tests for <see cref="ConnectionTester"/>. A routing fake <see cref="HttpMessageHandler"/>
/// stands in for Radarr/Sonarr/qBittorrent so the reachability logic (key header, 401 handling,
/// qBittorrent login retry) is exercised without a network.
/// </summary>
public class ConnectionTesterTests
{
    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(route(request));
    }

    private static ConnectionTester Tester(Func<HttpRequestMessage, HttpResponseMessage> route)
        => new(new HttpClient(new RouteHandler(route)));

    private static readonly Func<HttpRequestMessage, HttpResponseMessage> MustNotSend =
        _ => throw new InvalidOperationException("no HTTP request should have been sent");

    [Fact]
    public async Task Empty_url_is_rejected_without_a_request()
    {
        var r = await Tester(MustNotSend).TestAsync(new Connection { Kind = ConnectionKinds.Radarr, Url = "", ApiKey = "k" });
        Assert.False(r.Ok);
        Assert.Contains("No URL", r.Message);
    }

    [Fact]
    public async Task Non_http_url_is_rejected_without_a_request()
    {
        var r = await Tester(MustNotSend).TestAsync(new Connection { Kind = ConnectionKinds.Radarr, Url = "ftp://x", ApiKey = "k" });
        Assert.False(r.Ok);
        Assert.Contains("absolute http", r.Message);
    }

    [Fact]
    public async Task Arr_without_api_key_is_rejected_without_a_request()
    {
        var r = await Tester(MustNotSend).TestAsync(new Connection { Kind = ConnectionKinds.Sonarr, Url = "http://s:8989" });
        Assert.False(r.Ok);
        Assert.Contains("API key", r.Message);
    }

    [Fact]
    public async Task Arr_success_reports_version_and_sends_the_key_header()
    {
        string? sentKey = null;
        var r = await Tester(req =>
        {
            sentKey = req.Headers.TryGetValues("X-Api-Key", out var v) ? v.First() : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":\"5.1.0\"}") };
        }).TestAsync(new Connection { Kind = ConnectionKinds.Radarr, Url = "http://r:7878/", ApiKey = "secret" });

        Assert.True(r.Ok);
        Assert.Contains("5.1.0", r.Message);
        Assert.Equal("secret", sentKey);
    }

    [Fact]
    public async Task Arr_401_reports_a_key_problem()
    {
        var r = await Tester(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
            .TestAsync(new Connection { Kind = ConnectionKinds.Radarr, Url = "http://r:7878", ApiKey = "bad" });
        Assert.False(r.Ok);
        Assert.Contains("API key", r.Message);
    }

    [Fact]
    public async Task Qbit_open_instance_connects_without_login()
    {
        var r = await Tester(req =>
        {
            Assert.EndsWith("/api/v2/app/version", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v4.6.0") };
        }).TestAsync(new Connection { Kind = ConnectionKinds.Qbittorrent, Url = "http://q:8080" });

        Assert.True(r.Ok);
        Assert.Contains("v4.6.0", r.Message);
    }

    [Fact]
    public async Task Qbit_login_required_without_credentials_is_rejected()
    {
        var r = await Tester(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))
            .TestAsync(new Connection { Kind = ConnectionKinds.Qbittorrent, Url = "http://q:8080" });
        Assert.False(r.Ok);
        Assert.Contains("login", r.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task Qbit_logs_in_then_reads_version()
    {
        int versionCalls = 0;
        var r = await Tester(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
            versionCalls++;
            return versionCalls == 1
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v4.6.0") };
        }).TestAsync(new Connection { Kind = ConnectionKinds.Qbittorrent, Url = "http://q:8080", Username = "admin", Password = "pw" });

        Assert.True(r.Ok);
        Assert.Contains("v4.6.0", r.Message);
        Assert.Equal(2, versionCalls);
    }
}
