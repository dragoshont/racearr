using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Racearr.Web;

/// <summary>
/// Trusts the identity headers injected by the Authentik forward-auth proxy at the ingress
/// (<c>X-authentik-username</c> / <c>-name</c> / <c>-email</c> / <c>-groups</c>). This is purely
/// informational — it surfaces "who is signed in" in the UI so racearr matches the Seerr/Authentik
/// experience. It enforces nothing: Authentik at the ingress is the real gate, and the in-cluster
/// callers (Prometheus <c>/metrics</c>, <c>/healthz</c>, <c>/status</c>, the Seerr webhook) simply
/// arrive with no header and stay anonymous.
/// </summary>
/// <remarks>
/// TRUST BOUNDARY: these headers are trusted ONLY because the ingress (Traefik forward-auth) sets
/// them from Authentik's response and overwrites any client-supplied copy. The resulting identity is
/// used for DISPLAY ONLY — nothing here authorises a request. Do NOT add role/claim-based
/// authorization on these headers (<c>[Authorize(Roles=…)]</c>, a <c>FallbackPolicy</c>, or
/// <c>RequireAuthorization()</c>) without first guaranteeing the ingress strips inbound
/// <c>X-authentik-*</c> on every routed path — otherwise a client that reaches the pod directly
/// could forge an identity.
/// </remarks>
public sealed class AuthentikHeaderHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "authentik";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var username = Request.Headers["X-authentik-username"].ToString();
        if (string.IsNullOrWhiteSpace(username))
            return Task.FromResult(AuthenticateResult.NoResult());

        var name = Request.Headers["X-authentik-name"].ToString();
        var email = Request.Headers["X-authentik-email"].ToString();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? username : name),
        };
        if (!string.IsNullOrWhiteSpace(email))
            claims.Add(new Claim(ClaimTypes.Email, email));

        var groups = Request.Headers["X-authentik-groups"].ToString();
        if (!string.IsNullOrWhiteSpace(groups))
            foreach (var g in groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                claims.Add(new Claim(ClaimTypes.Role, g));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
