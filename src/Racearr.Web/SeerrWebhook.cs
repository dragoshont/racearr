using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Ingests Seerr / Overseerr-compatible webhook notifications. The parser is deliberately tolerant
/// of missing or reordered fields (payloads are user-customizable in Seerr); test/ping notifications
/// produce no history event. Webhook events are informational only — they never drive grabs or kills.
/// </summary>
public static class SeerrWebhook
{
    /// <summary>
    /// Constant-time shared-secret check. When no token is configured the endpoint relies on network
    /// policy alone; when one is configured a matching <c>X-Webhook-Token</c> header is required.
    /// </summary>
    public static bool IsAuthorized(string? configuredToken, string? providedToken)
    {
        if (string.IsNullOrEmpty(configuredToken)) return true;
        if (string.IsNullOrEmpty(providedToken)) return false;
        // Hash both to a fixed 32 bytes first so the constant-time compare never leaks token length.
        Span<byte> configured = stackalloc byte[32];
        Span<byte> provided = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken), configured);
        SHA256.HashData(Encoding.UTF8.GetBytes(providedToken), provided);
        return CryptographicOperations.FixedTimeEquals(configured, provided);
    }

    /// <summary>
    /// Map a Seerr webhook payload to a <c>request</c> history event, or <c>null</c> when there is
    /// nothing meaningful to record (a test ping, or no subject).
    /// </summary>
    public static RaceEvent? Parse(JsonElement root)
    {
        // A well-formed but non-object body ([], null, "x", 123) is not a webhook we understand;
        // TryGetProperty throws on non-objects, so guard here rather than let it escape as a 500.
        if (root.ValueKind != JsonValueKind.Object) return null;

        var type = Str(root, "notification_type");
        // Seerr posts TEST_NOTIFICATION when the user clicks "Test" — acknowledge but don't record it.
        if (string.Equals(type, "TEST_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
            return null;

        var subject = Str(root, "subject");
        if (string.IsNullOrWhiteSpace(subject)) return null;

        string? mediaType = null, requester = null;
        if (root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
            mediaType = Str(media, "media_type");
        if (root.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
            requester = Str(request, "requestedBy_username");

        var detail = string.IsNullOrWhiteSpace(requester) ? subject! : $"{subject} — requested by {requester}";
        return new RaceEvent
        {
            Kind = "request",
            Instance = MapInstance(mediaType),
            Outcome = Trunc((type ?? "request").ToLowerInvariant(), 48),
            Detail = Trunc(detail, 512)!,
        };
    }

    private static string? MapInstance(string? mediaType) => mediaType?.Trim().ToLowerInvariant() switch
    {
        "movie" => "radarr",
        "tv" or "show" or "series" => "sonarr",
        _ => Trunc(mediaType, 16),
    };

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Trunc(string? s, int max)
        => s is null ? null : s.Length <= max ? s : s[..max];
}
