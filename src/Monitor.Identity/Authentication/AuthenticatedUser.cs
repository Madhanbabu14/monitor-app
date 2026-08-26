using Monitor.Core.Domain;

namespace Monitor.Identity.Authentication;

/// <summary>
/// Direct translation of the `req.user` shape assembled at the bottom of
/// `authenticate` (auth.middleware.ts):
/// <code>
/// req.user = {
///   id: payload.sub ?? payload.oid ?? '',
///   email: payload.email ?? payload.preferred_username ?? '',
///   displayName: payload.name ?? '',
///   role: (payload.role as UserRole) ?? 'Viewer',
///   azureOid: payload.oid ?? '',
/// };
/// </code>
/// Populated from <see cref="System.Security.Claims.ClaimsPrincipal"/> via
/// <see cref="ClaimsPrincipalExtensions.ToAuthenticatedUser"/> once either
/// JwtBearer scheme has validated the token.
/// </summary>
public sealed record AuthenticatedUser(
    string Id,
    string Email,
    string DisplayName,
    UserRole Role,
    string AzureOid);
