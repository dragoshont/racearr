using System.Text.Json;
using Racearr.Web;
using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// Tests the Seerr webhook parser and shared-secret check with realistic Overseerr-compatible
/// payloads. The parser must tolerate missing fields, ignore test pings, and never throw.
/// </summary>
public class SeerrWebhookTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Parse_ApprovedMovie_RecordsRequestForRadarr()
    {
        var evt = SeerrWebhook.Parse(Json("""
        {
            "notification_type": "MEDIA_APPROVED",
            "subject": "Sicario (2015)",
            "media": { "media_type": "movie" },
            "request": { "requestedBy_username": "dragos" }
        }
        """));

        Assert.NotNull(evt);
        Assert.Equal("request", evt!.Kind);
        Assert.Equal("radarr", evt.Instance);
        Assert.Equal("media_approved", evt.Outcome);
        Assert.Contains("Sicario (2015)", evt.Detail);
        Assert.Contains("dragos", evt.Detail);
    }

    [Fact]
    public void Parse_AvailableTv_MapsToSonarr()
    {
        var evt = SeerrWebhook.Parse(Json("""
        { "notification_type": "MEDIA_AVAILABLE", "subject": "Chernobyl", "media": { "media_type": "tv" } }
        """));

        Assert.NotNull(evt);
        Assert.Equal("sonarr", evt!.Instance);
        Assert.Equal("media_available", evt.Outcome);
        Assert.Equal("Chernobyl", evt.Detail); // no requester -> just the subject
    }

    [Fact]
    public void Parse_TestNotification_IsIgnored()
        => Assert.Null(SeerrWebhook.Parse(Json("""{ "notification_type": "TEST_NOTIFICATION", "subject": "Test" }""")));

    [Fact]
    public void Parse_MissingSubject_ReturnsNull()
        => Assert.Null(SeerrWebhook.Parse(Json("""{ "notification_type": "MEDIA_PENDING" }""")));

    [Fact]
    public void Parse_UnknownMediaType_KeptRaw_AndDetailTruncated()
    {
        var longSubject = new string('x', 700);
        var evt = SeerrWebhook.Parse(Json($$"""
        { "notification_type": "MEDIA_PENDING", "subject": "{{longSubject}}", "media": { "media_type": "music" } }
        """));

        Assert.NotNull(evt);
        Assert.Equal("music", evt!.Instance);
        Assert.True(evt.Detail.Length <= 512); // oversized field is truncated to the column bound
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"just a string\"")]
    [InlineData("123")]
    public void Parse_NonObjectRoot_ReturnsNullWithoutThrowing(string body)
        => Assert.Null(SeerrWebhook.Parse(Json(body)));

    [Fact]
    public void Parse_OverlongTypeAndMediaType_TruncatedToColumnBounds()
    {
        var evt = SeerrWebhook.Parse(Json($$"""
        {
            "notification_type": "{{new string('T', 200)}}",
            "subject": "X",
            "media": { "media_type": "{{new string('m', 200)}}" }
        }
        """));

        Assert.NotNull(evt);
        Assert.True(evt!.Outcome!.Length <= 48);   // Outcome column bound
        Assert.True(evt.Instance!.Length <= 16);   // Instance column bound
    }

    [Theory]
    [InlineData(null, "anything", true)]   // no token configured -> netpol-gated, allow
    [InlineData("", "anything", true)]
    [InlineData("s3cret", "s3cret", true)] // configured + match
    [InlineData("s3cret", "wrong", false)] // different length
    [InlineData("s3cret", "s3crXt", false)] // same length, wrong -> not merely a length check
    [InlineData("s3cret", "", false)]
    [InlineData("s3cret", null, false)]
    public void IsAuthorized_EnforcesTokenOnlyWhenConfigured(string? configured, string? provided, bool expected)
        => Assert.Equal(expected, SeerrWebhook.IsAuthorized(configured, provided));
}
