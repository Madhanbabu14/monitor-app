using Monitor.Core.Errors;
using Monitor.Data.Repositories;

namespace Monitor.Identity.Users;

/// <inheritdoc cref="IUserRepository"/>
public sealed class UserRepository : IUserRepository
{
    private readonly IPrimaryDb _db;

    public UserRepository(IPrimaryDb db)
    {
        _db = db;
    }

    public Task<UserRow?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        _db.QuerySingleAsync<UserRow>(
            @"SELECT id, email, display_name AS ""DisplayName"", role,
                     azure_oid AS ""AzureOid"", is_active AS ""IsActive"",
                     password_hash AS ""PasswordHash""
              FROM users WHERE email = @email",
            new { email },
            cancellationToken);

    public Task<UserRow?> FindByAzureOidAsync(string azureOid, CancellationToken cancellationToken = default) =>
        _db.QuerySingleAsync<UserRow>(
            @"SELECT id, email, display_name AS ""DisplayName"", role,
                     azure_oid AS ""AzureOid"", is_active AS ""IsActive""
              FROM users WHERE azure_oid = @azureOid",
            new { azureOid },
            cancellationToken);

    public async Task<UserRow> UpsertByEmailAsync(string email, string displayName, string azureOid, CancellationToken cancellationToken = default)
    {
        var row = await _db.QuerySingleAsync<UserRow>(
            @"INSERT INTO users (email, display_name, role, azure_oid, is_active, last_login_at)
              VALUES (@email, @displayName, 'Viewer', @azureOid, true, NOW())
              ON CONFLICT (email) DO UPDATE SET
                azure_oid     = EXCLUDED.azure_oid,
                display_name  = EXCLUDED.display_name,
                last_login_at = NOW()
              RETURNING id, email, display_name AS ""DisplayName"", role,
                        azure_oid AS ""AzureOid"", is_active AS ""IsActive""",
            new { email, displayName, azureOid },
            cancellationToken);

        // The RETURNING clause always yields exactly one row for an
        // INSERT ... ON CONFLICT DO UPDATE; a null here is the same
        // "shouldn't happen but the source guarded it anyway" case
        // `ssoLogin` covers with `if (!row) throw new AppError(500, 'Failed to process Azure AD user')`.
        return row ?? throw new AppException(500, "Failed to process Azure AD user");
    }

    public async Task UpdateLastLoginAsync(Guid id, CancellationToken cancellationToken = default) =>
        // No RETURNING clause on this UPDATE (matches the source, which also
        // discards the result) — QueryAsync<int> against Npgsql's command tag
        // is the IPrimaryDb-shaped way to execute a statement with no result set.
        await _db.QueryAsync<int>(
            "UPDATE users SET last_login_at = NOW() WHERE id = @id",
            new { id },
            cancellationToken);

    public async Task UpdateLastLoginAndAzureOidAsync(Guid id, string azureOid, CancellationToken cancellationToken = default) =>
        await _db.QueryAsync<int>(
            "UPDATE users SET last_login_at = NOW(), azure_oid = @azureOid WHERE id = @id",
            new { id, azureOid },
            cancellationToken);
}
