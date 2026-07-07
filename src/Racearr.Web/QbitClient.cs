using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Read-only qBittorrent client. Works credential-free when qBit bypasses auth for this client
/// (localhost / whitelisted subnet); otherwise logs in with QBIT_USERNAME/QBIT_PASSWORD and keeps
/// the session cookie (the typed <see cref="HttpClient"/> uses a cookie container), re-authenticating
/// on 401/403. Port of the Python <c>qbit_get</c> / <c>qbit_by_hash</c>.
/// </summary>
public sealed class QbitClient(HttpClient http, RacearrOptions options, ILogger<QbitClient> log) : IQbitClient
{
    public async Task<IReadOnlyDictionary<string, TorrentInfo>> GetByHashAsync(CancellationToken ct)
    {
        try
        {
            var json = await GetTorrentsAsync(ct);
            var dict = new Dictionary<string, TorrentInfo>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    var hash = (Str(t, "hash") ?? "").ToLowerInvariant();
                    if (hash.Length == 0) continue;
                    dict[hash] = new TorrentInfo
                    {
                        DlSpeed = Num(t, "dlspeed"),
                        Progress = Num(t, "progress"),
                        Tracker = Str(t, "tracker") ?? "",
                        MagnetUri = Str(t, "magnet_uri") ?? "",
                        State = Str(t, "state") ?? "",
                        NumSeeds = (int)Num(t, "num_seeds"),
                    };
                }
            }
            return dict;
        }
        catch (Exception ex)
        {
            // Never let a download-client hiccup crash the loop (matches the Python behaviour).
            log.LogWarning(ex, "qbit fetch failed");
            return new Dictionary<string, TorrentInfo>();
        }
    }

    private async Task<string> GetTorrentsAsync(CancellationToken ct)
    {
        var url = $"{options.QbitUrl}/api/v2/torrents/info";
        var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && await LoginAsync(ct))
        {
            resp.Dispose();
            resp = await http.GetAsync(url, ct);
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<bool> LoginAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.QbitUsername) || string.IsNullOrWhiteSpace(options.QbitPassword))
            return false;
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = options.QbitUsername!,
                ["password"] = options.QbitPassword!,
            });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{options.QbitUrl}/api/v2/auth/login") { Content = form };
            req.Headers.Referrer = new Uri(options.QbitUrl);
            using var resp = await http.SendAsync(req, ct);
            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            var ok = resp.IsSuccessStatusCode && body == "Ok.";
            if (!ok) log.LogWarning("qbit login rejected — check QBIT_USERNAME/QBIT_PASSWORD");
            return ok;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "qbit login failed");
            return false;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
}
