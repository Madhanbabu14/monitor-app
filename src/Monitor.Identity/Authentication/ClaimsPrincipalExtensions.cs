using System.Security.Claims;
using Monitor.Core.Domain;

namespace Monitor.Identity.Authentication;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Reconstructs the `req.user` shape from a validated
    /// <see cref="ClaimsPrincipal"/>. Mirrors the fallback chain in
    /// `authenticate` (auth.middleware.ts) exactly:
    /// id = sub ?? oid ?? ''; email = email ?? preferred_username ?? '';
    /// displayName = name ?? ''; role = role ?? 'Viewer'; azureOid = oid ?? ''.
    ///
    /// One deliberate hardening beyond the source: the source cast an
    /// arbitrary `payload.role` string straight to the `UserRole` TS type
    /// with no runtime check, so a malformed/unexpected role claim would
    /// silently flow through as an invalid "UserRole". .NET has no
    /// equivalent unchecked cast for an enum, so an unparseable role value
    /// falls back to Viewer (the same default used when the claim is absent)
    /// rather than throwing.
    /// </summary>
    public static AuthenticatedUser ToAuthenticatedUser(this ClaimsPrincipal principal)
    {
        var azureOid = principal.FindFirstValue(JwtClaimTypes.ObjectId) ?? string.Empty;

        var id = principal.FindFirstValue(JwtClaimTypes.Subject) ?? azureOid;

        var email = principal.FindFirstValue(JwtClaimTypes.Email)
            ?? principal.FindFirstValue(JwtClaimTypes.PreferredUsername)
            ?? string.Empty;

        var displayName = principal.FindFirstValue(JwtClaimTypes.Name) ?? string.Empty;

        var roleClaim = principal.FindFirstValue(JwtClaimTypes.Role);
        if (roleClaim is null || !UserRoleExtensions.TryParse(roleClaim, out var role))
        {
            role = UserRole.Viewer;
        }

        return new AuthenticatedUser(id, email, displayName, role, azureOid);
    }
}
