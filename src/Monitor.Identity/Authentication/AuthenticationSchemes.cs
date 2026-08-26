namespace Monitor.Identity.Authentication;

/// <summary>
/// Scheme names for the two JwtBearer handlers this bounded context registers,
/// plus the forwarding "shell" scheme that picks between them per-request.
/// Direct translation of `middleware/auth.middleware.ts#authenticate`, which
/// branched on `decoded.header.kid`: present -&gt; verify against Azure AD's
/// JWKS (utils/azureAuth.ts), absent -&gt; verify against the app's own HS256
/// secret (config.jwt.secret).
/// </summary>
public static class AuthenticationSchemes
{
    /// <summary>App-issued HS256 JWTs (local login + post-SSO exchange).</summary>
    public const string AppJwt = "AppJwt";

    /// <summary>Azure AD access tokens, validated via tenant JWKS (RS256).</summary>
    public const string AzureAd = "AzureAd";

    /// <summary>
    /// Default/forwarding scheme registered as a policy scheme
    /// (<see cref="Microsoft.AspNetCore.Authentication.AuthenticationBuilder.AddPolicyScheme"/>)
    /// that inspects the bearer token's JOSE header and forwards
    /// authenticate/challenge/forbid to <see cref="AppJwt"/> or <see cref="AzureAd"/>.
    /// This is the .NET analogue of the single `authenticate` middleware that
    /// transparently handled both token kinds.
    /// </summary>
    public const string DualJwt = "DualJwt";
}
