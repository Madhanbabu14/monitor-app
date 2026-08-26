using System.Security.Claims;
using Monitor.Core.Domain;
using Monitor.Core.Errors;
using Monitor.Identity.Authentication;
using Monitor.Identity.Authorization;
using Monitor.Identity.Users;

namespace Monitor.Api.Endpoints;

/// <summary>
/// One endpoint module for the whole `/api/auth` surface — the .NET
/// collapse of auth.routes.ts (route wiring) + auth.service.ts (business
/// logic, called through <see cref="IAuthService"/>) per the Architect's
/// "route + policy + validation + DTO mapping live together" layering.
/// Response envelope matches the source's `res.json({ status: 'success',
/// data: ... })` exactly for the strangler window.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // No MapGroup() — that's a .NET 7 minimal-API addition and this
        // target is net6.0, so each route spells out the "/api/auth" prefix.

        // POST /auth/login (auth.routes.ts)
        app.MapPost("/api/auth/login", async (LoginRequest? request, IAuthService authService, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request?.Email) || string.IsNullOrEmpty(request.Password))
            {
                throw new AppException(400, "Email and password are required");
            }

            var (user, token) = await authService.LoginAsync(request.Email, request.Password, cancellationToken);
            return Results.Json(new { status = "success", data = new { user = ToWireUser(user), token } });
        });

        // POST /auth/sso — Azure AD SSO (auth.routes.ts)
        app.MapPost("/api/auth/sso", async (SsoRequest? request, IAuthService authService, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request?.AzureToken))
            {
                throw new AppException(400, "azureToken is required");
            }

            var (user, token) = await authService.SsoLoginAsync(request.AzureToken, cancellationToken);
            return Results.Json(new { status = "success", data = new { user = ToWireUser(user), token } });
        });

        // GET /auth/me (auth.routes.ts) — bare `authenticate` middleware, no role check.
        app.MapGet("/api/auth/me", (ClaimsPrincipal principal) =>
        {
            var user = principal.ToAuthenticatedUser();
            return Results.Json(new { status = "success", data = ToWireUser(user) });
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        return app;
    }

    /// <summary>
    /// `req.user` / the login-response `safeUser` shape: id, email,
    /// displayName, role, azureOid — never password_hash/is_active.
    /// </summary>
    private static object ToWireUser(AuthenticatedUser user) => new
    {
        id = user.Id,
        email = user.Email,
        displayName = user.DisplayName,
        role = user.Role.ToWireString(),
        azureOid = user.AzureOid,
    };
}

// Top-level (not nested in the static AuthEndpoints class) so System.Text.Json's
// reflection-based body binding always has an unambiguous public constructor to
// call, regardless of JsonSerializerOptions — matches auth.routes.ts's
// `const { email, password } = req.body;` / `const { azureToken } = req.body;`.
internal sealed record LoginRequest(string? Email, string? Password);

internal sealed record SsoRequest(string? AzureToken);
