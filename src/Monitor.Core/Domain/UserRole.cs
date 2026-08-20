namespace Monitor.Core.Domain;

/// <summary>
/// Direct translation of `export type UserRole = 'Admin' | 'Operator' | 'Viewer'`
/// (types/index.ts). Shared across Monitor.Identity (JWT claims, user upsert),
/// Monitor.Api (authorization policies), and any endpoint that gates on role.
/// Kept in Core because every bounded context needs it without depending on
/// Monitor.Identity itself.
/// </summary>
public enum UserRole
{
    Admin,
    Operator,
    Viewer,
}

public static class UserRoleExtensions
{
    /// <summary>
    /// Parses the exact string values used on the wire (JWT claim, DB column,
    /// JSON payloads) — case-sensitive, matching the TS string-literal union.
    /// </summary>
    public static bool TryParse(string? value, out UserRole role)
    {
        switch (value)
        {
            case "Admin":
                role = UserRole.Admin;
                return true;
            case "Operator":
                role = UserRole.Operator;
                return true;
            case "Viewer":
                role = UserRole.Viewer;
                return true;
            default:
                role = default;
                return false;
        }
    }

    /// <summary>Renders back to the exact wire string ("Admin"/"Operator"/"Viewer").</summary>
    public static string ToWireString(this UserRole role) => role switch
    {
        UserRole.Admin => "Admin",
        UserRole.Operator => "Operator",
        UserRole.Viewer => "Viewer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown UserRole"),
    };
}
