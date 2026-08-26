namespace Monitor.Identity.Users;

/// <summary>
/// Data access for the `users` table backing local login and Azure AD SSO.
/// Direct analogue of the raw <c>queryOne&lt;UserRow&gt;(...)</c> calls
/// scattered through auth.service.ts, collected behind an interface so
/// <see cref="AuthService"/> can be unit-tested against a fake.
/// </summary>
public interface IUserRepository
{
    /// <summary>Analogue of the `SELECT ... FROM users WHERE email = $1` in `login`.</summary>
    Task<UserRow?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Analogue of the `SELECT ... FROM users WHERE azure_oid = $1` in `ssoLogin`.</summary>
    Task<UserRow?> FindByAzureOidAsync(string azureOid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analogue of `ssoLogin`'s `INSERT ... ON CONFLICT (email) DO UPDATE ... RETURNING`:
    /// links an existing password-login account to Azure on first SSO use
    /// (updates azure_oid/display_name/last_login_at), or creates a new
    /// Viewer account for a first-time Azure user.
    /// </summary>
    Task<UserRow> UpsertByEmailAsync(string email, string displayName, string azureOid, CancellationToken cancellationToken = default);

    /// <summary>Analogue of `UPDATE users SET last_login_at = NOW() WHERE id = $1` in `login`.</summary>
    Task UpdateLastLoginAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analogue of `UPDATE users SET last_login_at = NOW(), azure_oid = $2 WHERE id = $1`
    /// in `ssoLogin` (re-stamps azure_oid even on the "found by oid" path).
    /// </summary>
    Task UpdateLastLoginAndAzureOidAsync(Guid id, string azureOid, CancellationToken cancellationToken = default);
}
