using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Surfaces "who is signed in" from a reverse-proxy forward-auth provider, for DISPLAY ONLY, so
/// racearr matches the Sonarr/Radarr/Seerr experience behind an SSO proxy. The header scheme is
/// configurable via <c>AUTH_PROXY</c>: Authentik (default), Authelia, oauth2-proxy,
/// tinyauth/traefik/generic (<c>Remote-*</c>), or fully custom header names.
/// <para>
/// It enforces nothing. The proxy at the ingress is the real gate, and any request without the
/// configured header stays anonymous, so racearr behaves identically with or without a proxy and
/// the in-cluster callers (Prometheus <c>/metrics</c>, <c>/healthz</c>, <c>/status</c>, the Seerr
/// webhook) are unaffected.
/// </para>
/// </summary>
/// <remarks>
/// TRUST BOUNDARY: these headers are trusted ONLY because the ingress sets them from the auth
/// provider's response and overwrites any client-supplied copy. The resulting identity is used for
/// DISPLAY ONLY. Do NOT add role/claim-based authorization on these headers
/// (<c>[Authorize(Roles=…)]</c>, a <c>FallbackPolicy</c>, or <c>RequireAuthorization()</c>) without
/// first guaranteeing the ingress strips inbound copies of the configured headers on every routed
/// path — otherwise a client that reaches the pod directly could forge an identity.
/// </remarks>
public sealed class ForwardAuthHeaderHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    RacearrOptions racearr) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "forward-auth";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = racearr.ResolveForwardAuthHeaders();
        if (headers is null)
            return Task.FromResult(AuthenticateResult.NoResult()); // display disabled, or no username header

        var username = Request.Headers[headers.User].ToString();
        if (string.IsNullOrWhiteSpace(username))
            return Task.FromResult(AuthenticateResult.NoResult()); // no proxy header -> stay anonymous

        var name = string.IsNullOrWhiteSpace(headers.Name) ? "" : Request.Headers[headers.Name].ToString();
        var email = string.IsNullOrWhiteSpace(headers.Email) ? "" : Request.Headers[headers.Email].ToString();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? username : name),
        };
        if (!string.IsNullOrWhiteSpace(email))
            claims.Add(new Claim(ClaimTypes.Email, email));

        var groups = string.IsNullOrWhiteSpace(headers.Groups) ? "" : Request.Headers[headers.Groups].ToString();
        if (!string.IsNullOrWhiteSpace(groups))
            foreach (var g in groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                claims.Add(new Claim(ClaimTypes.Role, g));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
