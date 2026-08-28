using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Monitor.UnitTests.Files.Fakes;

/// <summary>
/// Minimal authentication handler for exercising <c>FilesEndpoints</c>'s
/// <c>.RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser)</c> gate without
/// pulling in the real dual-JWT (app JWT / Azure AD) scheme from Monitor.Identity (out of
/// this slice's scope). Authenticates the caller as a valid user whenever the request
/// carries the <see cref="AuthenticatedHeaderName"/> header; otherwise reports "no
/// result", which ASP.NET Core's authorization middleware turns into a 401 for any
/// endpoint that requires authentication - exactly like an anonymous request against the
/// real scheme.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string AuthenticatedHeaderName = "X-Test-Authenticated";

#pragma warning disable CS0618 // ISystemClock is obsolete (superseded by TimeProvider) but this is the only ctor this SDK/framework combination exposes.
    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock)
        : base(options, logger, encoder, clock)
    {
    }
#pragma warning restore CS0618

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey(AuthenticatedHeaderName))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "test-user-id"), new Claim(ClaimTypes.Name, "Test User") };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
