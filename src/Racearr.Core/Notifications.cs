namespace Racearr.Core;

using System.Text.Json;

/// <summary>
/// Delivers racearr incident notifications to the configured channels (a generic/Discord/Slack
/// webhook and/or ntfy). Fire-and-forget by contract: implementations never block or throw into
/// the engine's control loop.
/// </summary>
public interface IIncidentNotifier
{
    void Notify(string type, string message);
}

/// <summary>No-op notifier for tests and notification-less runs.</summary>
public sealed class NullIncidentNotifier : IIncidentNotifier
{
    public static readonly NullIncidentNotifier Instance = new();
    public void Notify(string type, string message) { }
}

/// <summary>One outbound notification HTTP request, built from config and delivered by the web host.</summary>
public sealed record OutboundNotification(
    string Url, string Body, string ContentType, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Builds the outbound notification requests for a racearr incident from the configured channels:
/// <list type="bullet">
/// <item>a generic webhook (<c>INCIDENT_WEBHOOK_URL</c>) — Discord is auto-detected and sent as
/// <c>{"content":…}</c>, everything else (Slack/Mattermost/generic) as <c>{"text":…}</c>;</item>
/// <item>ntfy (<c>NTFY_URL</c> + <c>NTFY_TOPIC</c>) — a plain-text push with Title/Tags/Priority headers.</item>
/// </list>
/// Pure, so it is unit-testable without any HTTP.
/// </summary>
public static class IncidentNotifications
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public static IReadOnlyList<OutboundNotification> Build(RacearrOptions o, string type, string message)
    {
        var text = $"\ud83c\udfc1 racearr \u2014 {type}: {message}"; // "🏁 racearr — <type>: <message>"
        var outs = new List<OutboundNotification>();

        var hook = o.IncidentWebhookUrl;
        if (!string.IsNullOrWhiteSpace(hook))
        {
            var isDiscord = hook.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase)
                         || hook.Contains("discordapp.com/api/webhooks", StringComparison.OrdinalIgnoreCase);
            var field = isDiscord ? "content" : "text"; // Discord wants "content"; Slack/generic accept "text"
            var body = $"{{{Json(field)}:{Json(text)}}}";
            outs.Add(new OutboundNotification(hook!, body, "application/json", NoHeaders));
        }

        if (!string.IsNullOrWhiteSpace(o.NtfyUrl) && !string.IsNullOrWhiteSpace(o.NtfyTopic))
        {
            var url = $"{o.NtfyUrl!.TrimEnd('/')}/{o.NtfyTopic}";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Title"] = $"racearr {type}",
                ["Tags"] = "checkered_flag",
            };
            if (!string.IsNullOrWhiteSpace(o.NtfyPriority)) headers["Priority"] = o.NtfyPriority!;
            if (!string.IsNullOrWhiteSpace(o.NtfyToken)) headers["Authorization"] = $"Bearer {o.NtfyToken}";
            outs.Add(new OutboundNotification(url, message, "text/plain", headers));
        }

        return outs;
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);
}
