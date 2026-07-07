using System.Net;
using System.Text.Json;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Tests whether a configured service connection is actually reachable, for the Connections UI's
/// "Test" button. Radarr/Sonarr answer <c>GET /api/v3/system/status</c> (keyed); qBittorrent answers
/// <c>GET /api/v2/app/version</c>, logging in first when it demands credentials.
/// </summary>
public sealed class ConnectionTester(HttpClient http) : IConnectionTester
{
    public async Task<ConnectionTestResult> TestAsync(Connection c, CancellationToken ct = default)
    {
        var url = (c.Url ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
            return new ConnectionTestResult(false, "No URL set.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            return new ConnectionTestResult(false, "URL must be an absolute http(s) address.");

        try
        {
            return c.Kind switch
            {
                ConnectionKinds.Radarr or ConnectionKinds.Sonarr => await TestArrAsync(c, url, ct),
                ConnectionKinds.Qbittorrent => await TestQbitAsync(c, url, ct),
                _ => new ConnectionTestResult(false, $"Unknown kind '{c.Kind}'."),
            };
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, $"Failed: {ex.Message}");
        }
    }

    private async Task<ConnectionTestResult> TestArrAsync(Connection c, string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.ApiKey))
            return new ConnectionTestResult(false, "No API key set.");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/v3/system/status");
        req.Headers.Add("X-Api-Key", c.ApiKey);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized)
            return new ConnectionTestResult(false, "Rejected — check the API key.");
        if (!resp.IsSuccessStatusCode)
            return new ConnectionTestResult(false, $"HTTP {(int)resp.StatusCode}.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var version = doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new ConnectionTestResult(true, $"Connected — {c.Kind} {version ?? "ok"}.");
    }

    private async Task<ConnectionTestResult> TestQbitAsync(Connection c, string url, CancellationToken ct)
    {
        var versionUrl = $"{url}/api/v2/app/version";
        using (var resp = await http.GetAsync(versionUrl, ct))
        {
            if (resp.IsSuccessStatusCode)
                return new ConnectionTestResult(true, $"Connected — qBittorrent {(await resp.Content.ReadAsStringAsync(ct)).Trim()}.");
            if (resp.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                return new ConnectionTestResult(false, $"HTTP {(int)resp.StatusCode}.");
        }

        if (string.IsNullOrWhiteSpace(c.Username) || string.IsNullOrWhiteSpace(c.Password))
            return new ConnectionTestResult(false, "Requires login — set a username and password.");

        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = c.Username!, ["password"] = c.Password! });
        using var loginReq = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/v2/auth/login") { Content = form };
        loginReq.Headers.Referrer = new Uri(url);
        using var loginResp = await http.SendAsync(loginReq, ct);
        var body = (await loginResp.Content.ReadAsStringAsync(ct)).Trim();
        if (!(loginResp.IsSuccessStatusCode && body == "Ok."))
            return new ConnectionTestResult(false, "Login rejected — check the username/password.");

        using var retry = await http.GetAsync(versionUrl, ct);
        return retry.IsSuccessStatusCode
            ? new ConnectionTestResult(true, $"Connected — qBittorrent {(await retry.Content.ReadAsStringAsync(ct)).Trim()}.")
            : new ConnectionTestResult(false, $"HTTP {(int)retry.StatusCode} after login.");
    }
}
