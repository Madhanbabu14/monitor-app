namespace Monitor.Identity.Users;

/// <summary>
/// Validates an Azure AD access token handed to POST /api/auth/sso in the
/// request body (as opposed to a bearer token on the Authorization header,
/// which the <see cref="Monitor.Identity.Authentication.AuthenticationSchemes.AzureAd"/>
/// JwtBearer scheme already validates for incoming requests). Direct
/// translation of utils/azureAuth.ts's `validateAzureToken`: signature via
/// JWKS, issuer/audience/expiry, then extracts oid/email/name.
/// </summary>
public interface IAzureTokenValidator
{
    /// <summary>Throws <see cref="Monitor.Core.Errors.AppException"/>(401, ...) on any validation failure.</summary>
    Task<AzureClaims> ValidateAsync(string token, CancellationToken cancellationToken = default);
}
