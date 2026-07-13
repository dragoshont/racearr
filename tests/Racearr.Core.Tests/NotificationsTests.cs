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

    [Fact]
    public void Telegram_builds_a_bot_sendmessage_request()
    {
        var o = new RacearrOptions { TelegramBotToken = "123:ABC", TelegramChatId = "-100999" };
        var n = Assert.Single(IncidentNotifications.Build(o, "race", "Dune"));

        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", n.Url);
        Assert.Equal("application/json", n.ContentType);
        Assert.Contains("\"chat_id\":\"-100999\"", n.Body);
        Assert.Contains("Dune", n.Body);
    }

    [Fact]
    public void Gotify_builds_a_message_request_with_token_and_priority()
    {
        var o = new RacearrOptions { GotifyUrl = "https://gotify.example.com/", GotifyToken = "tok", GotifyPriority = 8 };
        var n = Assert.Single(IncidentNotifications.Build(o, "kill", "loser killed"));

        Assert.Equal("https://gotify.example.com/message?token=tok", n.Url);   // trailing slash trimmed, token in query
        Assert.Equal("application/json", n.ContentType);
        Assert.Contains("\"priority\":8", n.Body);
        Assert.Contains("loser killed", n.Body);
    }

    [Fact]
    public void Pushover_builds_a_form_encoded_request()
    {
        var o = new RacearrOptions { PushoverToken = "app", PushoverUser = "usr", PushoverPriority = "1" };
        var n = Assert.Single(IncidentNotifications.Build(o, "pickup", "The Matrix"));

        Assert.Equal("https://api.pushover.net/1/messages.json", n.Url);
        Assert.Equal("application/x-www-form-urlencoded", n.ContentType);
        Assert.Contains("token=app", n.Body);
        Assert.Contains("user=usr", n.Body);
        Assert.Contains("priority=1", n.Body);
        Assert.Contains("message=The%20Matrix", n.Body);   // form-encoded
    }

    [Fact]
    public void Apprise_builds_a_gateway_request()
    {
        var o = new RacearrOptions { AppriseUrl = "http://apprise:8000/notify/key", AppriseTag = "media" };
        var n = Assert.Single(IncidentNotifications.Build(o, "incident", "x"));

        Assert.Equal("http://apprise:8000/notify/key", n.Url);
        Assert.Equal("application/json", n.ContentType);
        Assert.Contains("\"body\":", n.Body);
        Assert.Contains("\"tag\":\"media\"", n.Body);
    }

    [Fact]
    public void Each_channel_is_independent_and_all_can_fire_together()
    {
        var o = new RacearrOptions
        {
            IncidentWebhookUrl = "https://discord.com/api/webhooks/1/2",
            NtfyUrl = "https://ntfy.sh", NtfyTopic = "racearr",
            TelegramBotToken = "1:2", TelegramChatId = "3",
            GotifyUrl = "https://g", GotifyToken = "t",
            PushoverToken = "a", PushoverUser = "u",
            AppriseUrl = "http://apprise:8000/notify",
        };
        Assert.Equal(6, IncidentNotifications.Build(o, "incident", "x").Count);
    }
}
