namespace Monitor.Identity.Authentication;

/// <summary>
/// Raw JWT payload claim names used by both token kinds. Both JwtBearer
/// handlers set <c>MapInboundClaims = false</c> so these come through
/// verbatim instead of being remapped to the long legacy XML/SOAP claim
/// URIs .NET otherwise substitutes for well-known JWT claims (`sub`, `email`,
/// ...) — the source reads `payload.sub`, `payload.oid`, etc. directly, and
/// this keeps the .NET side reading the same keys.
/// </summary>
internal static class JwtClaimTypes
{
    public const string Subject = "sub";
    public const string ObjectId = "oid";
    public const string Email = "email";
    public const string PreferredUsername = "preferred_username";
    public const string Name = "name";

    /// <summary>
    /// Matches the source's <c>RoleClaimType</c> usage (`payload.role`) and is
    /// also configured as <c>TokenValidationParameters.RoleClaimType</c> so
    /// ASP.NET Core's role-based authorization (`RequireRole`, `[Authorize(Roles=...)]`)
    /// reads the same claim.
    /// </summary>
    public const string Role = "role";
}
