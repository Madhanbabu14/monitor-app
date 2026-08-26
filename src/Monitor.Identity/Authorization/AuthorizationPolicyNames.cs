namespace Monitor.Identity.Authorization;

/// <summary>
/// Named policies standing in for `authorize(...roles: UserRole[])`
/// (auth.middleware.ts). The source factory took an arbitrary role list per
/// call site; no call site currently passes more than one role (every route
/// today only uses the bare `authenticate` middleware — `authorize` is
/// exported but unused), so this exposes one named policy per
/// <see cref="Monitor.Core.Domain.UserRole"/> value. A future endpoint that
/// needs an OR of multiple roles (the source's `authorize('Admin', 'Operator')`
/// shape) can register an additional named policy here with
/// `policy.RequireRole(Role1, Role2, ...)` — RequireRole itself is already
/// OR-semantics across the roles passed to a single call, matching
/// `roles.includes(req.user.role)`.
/// </summary>
public static class AuthorizationPolicyNames
{
    /// <summary>Any authenticated caller, no role restriction — the bare `authenticate` middleware.</summary>
    public const string AuthenticatedUser = "AuthenticatedUser";

    public const string RequireAdmin = "RequireAdmin";
    public const string RequireOperator = "RequireOperator";
    public const string RequireViewer = "RequireViewer";
}
