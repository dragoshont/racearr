using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Racearr.Core;
using Racearr.Web;
using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// The forward-auth header handler is informational (surfaces the signed-in user in the UI) and must
/// never fail closed: a request without the configured header stays anonymous so the in-cluster
/// /metrics, /healthz, /status and webhook callers are unaffected. The header scheme is configurable
/// (AUTH_PROXY) so racearr works behind Authentik, Authelia, oauth2-proxy, or a custom proxy — or
/// with no proxy at all.
/// </summary>
public class ForwardAuthHeaderHandlerTests
{
    private static async Task<AuthenticateResult> AuthenticateAsync(RacearrOptions options, Action<HttpRequest> setup)
    {
        var handler = new ForwardAuthHeaderHandler(new StubMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default, options);
        var context = new DefaultHttpContext();
        setup(context.Request);
        var scheme = new AuthenticationScheme(ForwardAuthHeaderHandler.SchemeName, null, typeof(ForwardAuthHeaderHandler));
        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task No_header_stays_anonymous()
    {
        var result = await AuthenticateAsync(new RacearrOptions(), _ => { });
        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task Authentik_is_the_default_scheme()
    {
        var result = await AuthenticateAsync(new RacearrOptions(), r =>
        {
            r.Headers["X-authentik-username"] = "dragos";
            r.Headers["X-authentik-name"] = "Dragos Hont";
            r.Headers["X-authentik-email"] = "dragos@hont.ro";
            r.Headers["X-authentik-groups"] = "admins, media";
        });

        Assert.True(result.Succeeded);
        var p = result.Principal!;
        Assert.Equal("Dragos Hont", p.Identity!.Name);
        Assert.Equal("dragos@hont.ro", p.FindFirst(ClaimTypes.Email)!.Value);
        Assert.True(p.IsInRole("admins"));
        Assert.True(p.IsInRole("media"));
    }

    [Fact]
    public async Task Username_without_display_name_falls_back_to_username()
    {
        var result = await AuthenticateAsync(new RacearrOptions(), r => r.Headers["X-authentik-username"] = "svc-account");
        Assert.True(result.Succeeded);
        Assert.Equal("svc-account", result.Principal!.Identity!.Name);
    }

    [Fact]
    public async Task Authelia_preset_reads_remote_headers()
    {
        var result = await AuthenticateAsync(new RacearrOptions { AuthProxy = "authelia" }, r =>
        {
            r.Headers["Remote-User"] = "alice";
            r.Headers["Remote-Name"] = "Alice A";
            r.Headers["Remote-Groups"] = "admins";
            r.Headers["X-authentik-username"] = "ignored"; // the wrong scheme is not read
        });

        Assert.True(result.Succeeded);
        Assert.Equal("Alice A", result.Principal!.Identity!.Name);
        Assert.True(result.Principal.IsInRole("admins"));
    }

    [Fact]
    public async Task OAuth2Proxy_preset_reads_forwarded_headers()
    {
        var result = await AuthenticateAsync(new RacearrOptions { AuthProxy = "oauth2-proxy" }, r =>
        {
            r.Headers["X-Forwarded-User"] = "u123";
            r.Headers["X-Forwarded-Preferred-Username"] = "Bob";
            r.Headers["X-Forwarded-Email"] = "bob@example.com";
        });

        Assert.True(result.Succeeded);
        Assert.Equal("Bob", result.Principal!.Identity!.Name);
        Assert.Equal("bob@example.com", result.Principal.FindFirst(ClaimTypes.Email)!.Value);
    }

    [Fact]
    public async Task Custom_header_override_is_honoured()
    {
        var opts = new RacearrOptions { AuthProxy = "custom", AuthProxyUserHeader = "X-My-User" };
        var result = await AuthenticateAsync(opts, r => r.Headers["X-My-User"] = "carol");
        Assert.True(result.Succeeded);
        Assert.Equal("carol", result.Principal!.Identity!.Name);
    }

    [Fact]
    public async Task None_disables_identity_display()
    {
        var result = await AuthenticateAsync(new RacearrOptions { AuthProxy = "none" }, r =>
            r.Headers["X-authentik-username"] = "dragos");
        Assert.True(result.None);
    }

    private sealed class StubMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
