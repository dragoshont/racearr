using Racearr.Core;

namespace Racearr.Core.Tests;

public class NotificationsTests
{
    [Fact]
    public void No_channels_configured_produces_no_requests()
        => Assert.Empty(IncidentNotifications.Build(new RacearrOptions(), "speed_breach", "Movie too slow"));

    [Fact]
    public void Discord_webhook_is_auto_detected_and_sent_as_content()
    {
        var o = new RacearrOptions { IncidentWebhookUrl = "https://discord.com/api/webhooks/123/abc" };
        var n = Assert.Single(IncidentNotifications.Build(o, "pickup_breach", "The Matrix"));

        Assert.Equal("https://discord.com/api/webhooks/123/abc", n.Url);
        Assert.Equal("application/json", n.ContentType);
        Assert.Contains("\"content\":", n.Body);      // Discord's field
        Assert.DoesNotContain("\"text\":", n.Body);
        Assert.Contains("The Matrix", n.Body);
    }

    [Fact]
    public void Generic_webhook_is_sent_as_text()
    {
        var o = new RacearrOptions { IncidentWebhookUrl = "https://hooks.slack.com/services/x/y/z" };
        var n = Assert.Single(IncidentNotifications.Build(o, "race_failed", "Dune"));

        Assert.Equal("application/json", n.ContentType);
        Assert.Contains("\"text\":", n.Body);          // Slack/Mattermost/generic field
        Assert.DoesNotContain("\"content\":", n.Body);
    }

    [Fact]
    public void Ntfy_builds_a_plaintext_push_with_headers()
    {
        var o = new RacearrOptions
        {
            NtfyUrl = "https://ntfy.sh/",   // trailing slash tolerated
            NtfyTopic = "racearr",
            NtfyPriority = "high",
            NtfyToken = "tk_secret",
        };
        var n = Assert.Single(IncidentNotifications.Build(o, "kill", "loser killed"));

        Assert.Equal("https://ntfy.sh/racearr", n.Url);
        Assert.Equal("text/plain", n.ContentType);
        Assert.Equal("loser killed", n.Body);
        Assert.Equal("racearr kill", n.Headers["Title"]);
        Assert.Equal("checkered_flag", n.Headers["Tags"]);
        Assert.Equal("high", n.Headers["Priority"]);
        Assert.Equal("Bearer tk_secret", n.Headers["Authorization"]);
    }

    [Fact]
    public void Ntfy_without_topic_is_ignored()
        => Assert.Empty(IncidentNotifications.Build(new RacearrOptions { NtfyUrl = "https://ntfy.sh" }, "x", "y"));

    [Fact]
    public void Both_channels_configured_produce_two_requests()
    {
        var o = new RacearrOptions
        {
            IncidentWebhookUrl = "https://discord.com/api/webhooks/1/2",
            NtfyUrl = "https://ntfy.sh",
            NtfyTopic = "racearr",
        };
        Assert.Equal(2, IncidentNotifications.Build(o, "incident", "x").Count);
    }
}
