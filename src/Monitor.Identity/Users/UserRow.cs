namespace Monitor.Identity.Users;

/// <summary>
/// Raw `users` table row (Dapper projection target). Direct analogue of
/// auth.service.ts's local <c>UserRow</c> type
/// (<c>User &amp; {{ isActive: boolean; passwordHash: string | null }}</c>),
/// minus the columns neither login query actually selects
/// (<c>last_login_at</c>, <c>created_at</c>) — the source's runtime object
/// only ever has the fields its SQL `SELECT` lists name, even though the TS
/// type is a wider superset.
/// </summary>
public sealed class UserRow
{
    public Guid Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Raw wire string ("Admin"/"Operator"/"Viewer") — the `role` column.</summary>
    public string Role { get; init; } = "Viewer";

    public string? AzureOid { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Null for Azure-only accounts, mirrors `password_hash` being nullable.</summary>
    public string? PasswordHash { get; init; }
}
