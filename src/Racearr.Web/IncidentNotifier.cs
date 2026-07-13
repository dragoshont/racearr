namespace Racearr.Web;

using System.Text;
using Racearr.Core;

/// <summary>
/// Delivers racearr incident notifications over HTTP (Discord / ntfy / generic webhook).
/// Fire-and-forget: <see cref="Notify"/> returns immediately and delivery never throws into the
/// engine's control loop — a dead webhook must never stall or crash the racer.
/// </summary>
public sealed class IncidentNotifier(HttpClient http, RacearrOptions options, ILogger<IncidentNotifier> log)
    : IIncidentNotifier
{
    public void Notify(string type, string message)
    {
        var reqs = IncidentNotifications.Build(options, type, message);
        if (reqs.Count == 0) return;
        _ = SendAllAsync(reqs);
    }

    private async Task SendAllAsync(IReadOnlyList<OutboundNotification> reqs)
    {
        foreach (var n in reqs)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, n.Url)
                {
                    Content = new StringContent(n.Body, Encoding.UTF8, n.ContentType),
                };
                foreach (var (k, v) in n.Headers) req.Headers.TryAddWithoutValidation(k, v);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var resp = await http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                    log.LogWarning("notification to {Host} returned HTTP {Status}", SafeHost(n.Url), (int)resp.StatusCode);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "notification delivery failed for {Host}", SafeHost(n.Url));
            }
        }
    }

    private static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";
}
