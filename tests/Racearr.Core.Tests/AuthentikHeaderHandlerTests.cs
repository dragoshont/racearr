using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Racearr.Web;
using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// The Authentik forward-auth header handler is informational (surfaces the signed-in user in the
/// UI) and must never fail closed: a request without the header stays anonymous so the in-cluster
/// /metrics, /healthz, /status and webhook callers are unaffected.
/// </summary>
public class AuthentikHeaderHandlerTests
{
    private static async Task<AuthenticateResult> AuthenticateAsync(Action<HttpRequest> setup)
    {
        var handler = new AuthentikHeaderHandler(new StubMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default);
        var context = new DefaultHttpContext();
        setup(context.Request);
        var scheme = new AuthenticationScheme(AuthentikHeaderHandler.SchemeName, null, typeof(AuthentikHeaderHandler));
        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task No_header_stays_anonymous()
    {
        var result = await AuthenticateAsync(_ => { });
        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task Username_header_becomes_the_signed_in_user()
    {
        var result = await AuthenticateAsync(r =>
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
        var result = await AuthenticateAsync(r => r.Headers["X-authentik-username"] = "svc-account");
        Assert.True(result.Succeeded);
        Assert.Equal("svc-account", result.Principal!.Identity!.Name);
    }

    private sealed class StubMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
