using Monitor.Identity.Authentication;

namespace Monitor.Identity.Users;

/// <summary>
/// Business logic behind POST /api/auth/login and POST /api/auth/sso.
/// Direct translation of auth.service.ts's <c>AuthService</c> class.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Local (BCrypt) login. Direct translation of `AuthService.login`.
    /// Throws <see cref="Monitor.Core.Errors.AppException"/>:
    ///   401 "Invalid email or password" — no such user, or wrong password;
    ///   403 "Account is inactive. Contact your administrator." — is_active = false;
    ///   401 "Password login not configured for this account" — password_hash is null.
    /// </summary>
    Task<(AuthenticatedUser User, string Token)> LoginAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Azure AD SSO token exchange + user upsert. Direct translation of
    /// `AuthService.ssoLogin`: validates the Azure token, resolves the user
    /// by azure_oid first then falls back to an upsert-by-email, and issues
    /// an app JWT embedding the verified `oid`.
    /// </summary>
    Task<(AuthenticatedUser User, string Token)> SsoLoginAsync(string azureToken, CancellationToken cancellationToken = default);
}
