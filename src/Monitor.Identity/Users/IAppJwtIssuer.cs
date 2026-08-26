using Monitor.Core.Domain;

namespace Monitor.Identity.Users;

/// <summary>
/// Issues the app's own HS256 JWTs — the analogue of the `jwt.sign(...,
/// config.jwt.secret, { expiresIn: config.jwt.expiresIn })` calls at the end
/// of both `login` and `ssoLogin` (auth.service.ts). Tokens issued here are
/// exactly what <see cref="Monitor.Identity.Authentication.AuthenticationSchemes.AppJwt"/>
/// validates on the way back in.
/// </summary>
public interface IAppJwtIssuer
{
    /// <summary>
    /// Builds and signs a token with claims sub/email/name/role (and oid,
    /// when supplied — only `ssoLogin` includes it in the source).
    /// </summary>
    string IssueToken(string subject, string email, string displayName, UserRole role, string? azureOid = null);
}
