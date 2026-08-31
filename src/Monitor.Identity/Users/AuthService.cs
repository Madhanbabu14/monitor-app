using Monitor.Core.Domain;
using Monitor.Core.Errors;
using Monitor.Identity.Authentication;

namespace Monitor.Identity.Users;

/// <inheritdoc cref="IAuthService"/>
public sealed class AuthService : IAuthService
{
    private readonly IUserRepository _users;
    private readonly IAzureTokenValidator _azureTokenValidator;
    private readonly IAppJwtIssuer _jwtIssuer;

    public AuthService(IUserRepository users, IAzureTokenValidator azureTokenValidator, IAppJwtIssuer jwtIssuer)
    {
        _users = users;
        _azureTokenValidator = azureTokenValidator;
        _jwtIssuer = jwtIssuer;
    }

    public async Task<(AuthenticatedUser User, string Token)> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var row = await _users.FindByEmailAsync(email, cancellationToken);
        if (row is null)
        {
            throw new AppException(401, "Invalid email or password");
        }

        if (!row.IsActive)
        {
            throw new AppException(403, "Account is inactive. Contact your administrator.");
        }

        if (string.IsNullOrEmpty(row.PasswordHash))
        {
            throw new AppException(401, "Password login not configured for this account");
        }

        if (!BCrypt.Net.BCrypt.Verify(password, row.PasswordHash))
        {
            throw new AppException(401, "Invalid email or password");
        }

        await _users.UpdateLastLoginAsync(row.Id, cancellationToken);

        var role = ParseRole(row.Role);
        var idString = row.Id.ToString();
        var azureOid = row.AzureOid ?? string.Empty;

        var token = _jwtIssuer.IssueToken(idString, row.Email, row.DisplayName, role);
        var user = new AuthenticatedUser(idString, row.Email, row.DisplayName, role, azureOid);
        return (user, token);
    }

    public async Task<(AuthenticatedUser User, string Token)> SsoLoginAsync(string azureToken, CancellationToken cancellationToken = default)
    {
        var claims = await _azureTokenValidator.ValidateAsync(azureToken, cancellationToken);

        // 1. Find by azure_oid first — handles email changes in Azure AD.
        var row = await _users.FindByAzureOidAsync(claims.Oid, cancellationToken);

        // 2. Upsert by email — links an existing password-login account to
        //    Azure on first SSO use, or creates a new Viewer account.
        row ??= await _users.UpsertByEmailAsync(claims.Email, claims.Name, claims.Oid, cancellationToken);

        if (!row.IsActive)
        {
            throw new AppException(403, "Account is inactive. Contact your administrator.");
        }

        await _users.UpdateLastLoginAndAzureOidAsync(row.Id, claims.Oid, cancellationToken);

        var role = ParseRole(row.Role);
        var idString = row.Id.ToString();

        var token = _jwtIssuer.IssueToken(idString, row.Email, row.DisplayName, role, claims.Oid);
        var user = new AuthenticatedUser(idString, row.Email, row.DisplayName, role, claims.Oid);
        return (user, token);
    }

    private static UserRole ParseRole(string raw) =>
        UserRoleExtensions.TryParse(raw, out var role) ? role : UserRole.Viewer;
}
