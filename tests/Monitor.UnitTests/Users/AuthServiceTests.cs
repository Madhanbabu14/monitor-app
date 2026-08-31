using Monitor.Core.Domain;
using Monitor.Core.Errors;
using Monitor.Identity.Authentication;
using Monitor.Identity.Users;

namespace Monitor.UnitTests.Users;

/// <summary>
/// Exercises <see cref="AuthService"/> — the direct translation of auth.service.ts's
/// <c>AuthService</c> class (local BCrypt login + Azure AD SSO token exchange/user
/// upsert + app-JWT issuance). Uses hand-written fakes for
/// <see cref="IUserRepository"/>, <see cref="IAzureTokenValidator"/> and
/// <see cref="IAppJwtIssuer"/> (no mocking library referenced by this test project),
/// matching the pattern already used by <c>PrimaryDatabaseStartupCheckTests</c>.
/// </summary>
public class AuthServiceTests
{
    private const string Password = "correct-password";

    private static readonly string HashedPassword = BCrypt.Net.BCrypt.HashPassword(Password);

    private sealed class FakeUserRepository : IUserRepository
    {
        public UserRow? ByEmail;
        public UserRow? ByAzureOid;
        public UserRow UpsertResult = new() { Id = Guid.NewGuid(), Email = "new@example.com", DisplayName = "New", Role = "Viewer", IsActive = true };
        public Exception? UpsertThrows;

        public (string Email, string DisplayName, string AzureOid)? UpsertCalledWith { get; private set; }
        public Guid? LastLoginUpdatedId { get; private set; }
        public (Guid Id, string AzureOid)? LastLoginAndOidUpdatedWith { get; private set; }
        public int UpdateLastLoginCallCount;
        public int UpdateLastLoginAndAzureOidCallCount;

        public Task<UserRow?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(ByEmail);

        public Task<UserRow?> FindByAzureOidAsync(string azureOid, CancellationToken cancellationToken = default) =>
            Task.FromResult(ByAzureOid);

        public Task<UserRow> UpsertByEmailAsync(string email, string displayName, string azureOid, CancellationToken cancellationToken = default)
        {
            UpsertCalledWith = (email, displayName, azureOid);
            if (UpsertThrows is not null) throw UpsertThrows;
            return Task.FromResult(UpsertResult);
        }

        public Task UpdateLastLoginAsync(Guid id, CancellationToken cancellationToken = default)
        {
            UpdateLastLoginCallCount++;
            LastLoginUpdatedId = id;
            return Task.CompletedTask;
        }

        public Task UpdateLastLoginAndAzureOidAsync(Guid id, string azureOid, CancellationToken cancellationToken = default)
        {
            UpdateLastLoginAndAzureOidCallCount++;
            LastLoginAndOidUpdatedWith = (id, azureOid);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAzureTokenValidator : IAzureTokenValidator
    {
        public AzureClaims? ClaimsToReturn;
        public Exception? ExceptionToThrow;
        public string? LastToken { get; private set; }

        public Task<AzureClaims> ValidateAsync(string token, CancellationToken cancellationToken = default)
        {
            LastToken = token;
            if (ExceptionToThrow is not null) throw ExceptionToThrow;
            return Task.FromResult(ClaimsToReturn!);
        }
    }

    private sealed class FakeJwtIssuer : IAppJwtIssuer
    {
        public string TokenToReturn = "fake-jwt-token";
        public (string Subject, string Email, string DisplayName, UserRole Role, string? AzureOid)? LastCall { get; private set; }

        public string IssueToken(string subject, string email, string displayName, UserRole role, string? azureOid = null)
        {
            LastCall = (subject, email, displayName, role, azureOid);
            return TokenToReturn;
        }
    }

    private static UserRow MakeRow(
        string email = "alice@example.com",
        string displayName = "Alice",
        string role = "Admin",
        bool isActive = true,
        string? passwordHash = null,
        string? azureOid = null,
        Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            Email = email,
            DisplayName = displayName,
            Role = role,
            IsActive = isActive,
            PasswordHash = passwordHash,
            AzureOid = azureOid,
        };

    private static (AuthService Service, FakeUserRepository Users, FakeAzureTokenValidator AzureValidator, FakeJwtIssuer JwtIssuer) CreateService()
    {
        var users = new FakeUserRepository();
        var azureValidator = new FakeAzureTokenValidator();
        var jwtIssuer = new FakeJwtIssuer();
        var service = new AuthService(users, azureValidator, jwtIssuer);
        return (service, users, azureValidator, jwtIssuer);
    }

    // ── LoginAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_NoSuchUser_Throws401InvalidEmailOrPassword()
    {
        var (service, users, _, _) = CreateService();
        users.ByEmail = null;

        var ex = await Assert.ThrowsAsync<AppException>(() => service.LoginAsync("missing@example.com", Password));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Invalid email or password", ex.Message);
    }

    [Fact]
    public async Task LoginAsync_InactiveAccount_Throws403()
    {
        var (service, users, _, _) = CreateService();
        users.ByEmail = MakeRow(isActive: false, passwordHash: HashedPassword);

        var ex = await Assert.ThrowsAsync<AppException>(() => service.LoginAsync("alice@example.com", Password));

        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("Account is inactive. Contact your administrator.", ex.Message);
    }

    [Fact]
    public async Task LoginAsync_NullPasswordHash_Throws401PasswordLoginNotConfigured()
    {
        var (service, users, _, _) = CreateService();
        users.ByEmail = MakeRow(isActive: true, passwordHash: null);

        var ex = await Assert.ThrowsAsync<AppException>(() => service.LoginAsync("alice@example.com", Password));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Password login not configured for this account", ex.Message);
    }

    [Fact]
    public async Task LoginAsync_EmptyPasswordHash_Throws401PasswordLoginNotConfigured()
    {
        var (service, users, _, _) = CreateService();
        users.ByEmail = MakeRow(isActive: true, passwordHash: string.Empty);

        var ex = await Assert.ThrowsAsync<AppException>(() => service.LoginAsync("alice@example.com", Password));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Password login not configured for this account", ex.Message);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_Throws401InvalidEmailOrPassword()
    {
        var (service, users, _, _) = CreateService();
        users.ByEmail = MakeRow(isActive: true, passwordHash: HashedPassword);

        var ex = await Assert.ThrowsAsync<AppException>(() => service.LoginAsync("alice@example.com", "wrong-password"));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Invalid email or password", ex.Message);
    }

    [Fact]
    public async Task LoginAsync_CorrectPassword_UpdatesLastLoginForThatUserId()
    {
        var (service, users, _, _) = CreateService();
        var id = Guid.NewGuid();
        users.ByEmail = MakeRow(isActive: true, passwordHash: HashedPassword, id: id);

        await service.LoginAsync("alice@example.com", Password);

        Assert.Equal(1, users.UpdateLastLoginCallCount);
        Assert.Equal(id, users.LastLoginUpdatedId);
    }

    [Fact]
    public async Task LoginAsync_CorrectPassword_IssuesTokenWithRowClaimsAndNoAzureOid()
    {
        var (service, users, _, jwtIssuer) = CreateService();
        var id = Guid.NewGuid();
        users.ByEmail = MakeRow(email: "alice@example.com", displayName: "Alice Example", role: "Operator", passwordHash: HashedPassword, id: id);

        var (user, token) = await service.LoginAsync("alice@example.com", Password);

        Assert.Equal("fake-jwt-token", token);
        Assert.NotNull(jwtIssuer.LastCall);
        Assert.Equal(id.ToString(), jwtIssuer.LastCall!.Value.Subject);
        Assert.Equal("alice@example.com", jwtIssuer.LastCall!.Value.Email);
        Assert.Equal("Alice Example", jwtIssuer.LastCall!.Value.DisplayName);
        Assert.Equal(UserRole.Operator, jwtIssuer.LastCall!.Value.Role);
        // login's jwt.sign call in the source never includes `oid`.
        Assert.Null(jwtIssuer.LastCall!.Value.AzureOid);

        Assert.Equal(id.ToString(), user.Id);
        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal("Alice Example", user.DisplayName);
        Assert.Equal(UserRole.Operator, user.Role);
    }

    [Fact]
    public async Task LoginAsync_RowWithNullAzureOid_ReturnedUserAzureOidIsEmptyString()
    {
        var (service, users, _, _) = CreateService();
        users.ByEmail = MakeRow(passwordHash: HashedPassword, azureOid: null);

        var (user, _) = await service.LoginAsync("alice@example.com", Password);

        Assert.Equal(string.Empty, user.AzureOid);
    }

    [Fact]
    public async Task LoginAsync_RowWithAzureOid_ReturnedUserCarriesIt()
    {
        var (service, users, _, _) = CreateService();
        users.ByEmail = MakeRow(passwordHash: HashedPassword, azureOid: "linked-oid-1");

        var (user, _) = await service.LoginAsync("alice@example.com", Password);

        Assert.Equal("linked-oid-1", user.AzureOid);
    }

    [Fact]
    public async Task LoginAsync_UnrecognizedRoleString_FallsBackToViewer()
    {
        var (service, users, _, _) = CreateService();
        users.ByEmail = MakeRow(role: "SuperAdmin", passwordHash: HashedPassword);

        var (user, _) = await service.LoginAsync("alice@example.com", Password);

        Assert.Equal(UserRole.Viewer, user.Role);
    }

    [Theory]
    [InlineData("Admin", UserRole.Admin)]
    [InlineData("Operator", UserRole.Operator)]
    [InlineData("Viewer", UserRole.Viewer)]
    public async Task LoginAsync_RecognizedRoleString_MapsToExpectedEnum(string role, UserRole expected)
    {
        var (service, users, _, _) = CreateService();
        users.ByEmail = MakeRow(role: role, passwordHash: HashedPassword);

        var (user, _) = await service.LoginAsync("alice@example.com", Password);

        Assert.Equal(expected, user.Role);
    }

    // ── SsoLoginAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SsoLoginAsync_ValidatesTheSuppliedTokenThroughTheAzureValidator()
    {
        var (service, users, azureValidator, _) = CreateService();
        azureValidator.ClaimsToReturn = new AzureClaims("oid-1", "alice@example.com", "Alice");
        users.ByAzureOid = MakeRow(isActive: true);

        await service.SsoLoginAsync("raw-azure-token");

        Assert.Equal("raw-azure-token", azureValidator.LastToken);
    }

    [Fact]
    public async Task SsoLoginAsync_AzureValidatorThrows_PropagatesTheException()
    {
        var (service, _, azureValidator, _) = CreateService();
        azureValidator.ExceptionToThrow = new AppException(401, "Invalid Azure AD token: missing key ID");

        var ex = await Assert.ThrowsAsync<AppException>(() => service.SsoLoginAsync("bad-token"));

        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task SsoLoginAsync_FoundByAzureOid_DoesNotUpsertByEmail()
    {
        var (service, users, azureValidator, _) = CreateService();
        azureValidator.ClaimsToReturn = new AzureClaims("oid-1", "alice@example.com", "Alice");
        users.ByAzureOid = MakeRow(isActive: true);

        await service.SsoLoginAsync("token");

        Assert.Null(users.UpsertCalledWith);
    }

    [Fact]
    public async Task SsoLoginAsync_NotFoundByAzureOid_UpsertsByEmailWithClaims()
    {
        var (service, users, azureValidator, _) = CreateService();
        azureValidator.ClaimsToReturn = new AzureClaims("oid-2", "bob@example.com", "Bob Example");
        users.ByAzureOid = null;
        users.UpsertResult = MakeRow(email: "bob@example.com", displayName: "Bob Example", role: "Viewer", isActive: true);

        await service.SsoLoginAsync("token");

        Assert.NotNull(users.UpsertCalledWith);
        Assert.Equal(("bob@example.com", "Bob Example", "oid-2"), users.UpsertCalledWith!.Value);
    }

    [Fact]
    public async Task SsoLoginAsync_UpsertThrows_Propagates500FailedToProcessAzureAdUser()
    {
        var (service, users, azureValidator, _) = CreateService();
        azureValidator.ClaimsToReturn = new AzureClaims("oid-2", "bob@example.com", "Bob");
        users.ByAzureOid = null;
        users.UpsertThrows = new AppException(500, "Failed to process Azure AD user");

        var ex = await Assert.ThrowsAsync<AppException>(() => service.SsoLoginAsync("token"));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Failed to process Azure AD user", ex.Message);
    }

    [Fact]
    public async Task SsoLoginAsync_InactiveAccount_FoundByOid_Throws403()
    {
        var (service, users, azureValidator, _) = CreateService();
        azureValidator.ClaimsToReturn = new AzureClaims("oid-1", "alice@example.com", "Alice");
        users.ByAzureOid = MakeRow(isActive: false);

        var ex = await Assert.ThrowsAsync<AppException>(() => service.SsoLoginAsync("token"));

        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("Account is inactive. Contact your administrator.", ex.Message);
    }

    [Fact]
    public async Task SsoLoginAsync_InactiveAccount_FromUpsert_Throws403()
    {
        var (service, users, azureValidator, _) = CreateService();
        azureValidator.ClaimsToReturn = new AzureClaims("oid-2", "bob@example.com", "Bob");
        users.ByAzureOid = null;
        users.UpsertResult = MakeRow(isActive: false);

        var ex = await Assert.ThrowsAsync<AppException>(() => service.SsoLoginAsync("token"));

        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task SsoLoginAsync_FoundByOid_StillRestampsLastLoginAndAzureOid()
    {
        var (service, users, azureValidator, _) = CreateService();
        var id = Guid.NewGuid();
        azureValidator.ClaimsToReturn = new AzureClaims("fresh-oid", "alice@example.com", "Alice");
        users.ByAzureOid = MakeRow(isActive: true, azureOid: "fresh-oid", id: id);

        await service.SsoLoginAsync("token");

        Assert.Equal(1, users.UpdateLastLoginAndAzureOidCallCount);
        Assert.Equal((id, "fresh-oid"), users.LastLoginAndOidUpdatedWith);
        // The plain (non-oid) last-login update path must not also fire.
        Assert.Equal(0, users.UpdateLastLoginCallCount);
    }

    [Fact]
    public async Task SsoLoginAsync_Success_IssuesTokenWithClaimsOidRegardlessOfRowAzureOid()
    {
        var (service, users, azureValidator, jwtIssuer) = CreateService();
        var id = Guid.NewGuid();
        azureValidator.ClaimsToReturn = new AzureClaims("claims-oid", "alice@example.com", "Alice Example");
        users.ByAzureOid = MakeRow(email: "alice@example.com", displayName: "Alice Example", role: "Admin", isActive: true, azureOid: "stale-oid-on-row", id: id);

        var (user, token) = await service.SsoLoginAsync("token");

        Assert.Equal("fake-jwt-token", token);
        Assert.NotNull(jwtIssuer.LastCall);
        Assert.Equal(id.ToString(), jwtIssuer.LastCall!.Value.Subject);
        Assert.Equal(UserRole.Admin, jwtIssuer.LastCall!.Value.Role);
        Assert.Equal("claims-oid", jwtIssuer.LastCall!.Value.AzureOid);

        // The returned AuthenticatedUser also carries the freshly-validated oid, not
        // whatever azure_oid happened to already be on the row.
        Assert.Equal("claims-oid", user.AzureOid);
    }

    [Fact]
    public async Task SsoLoginAsync_UnrecognizedRoleString_FallsBackToViewer()
    {
        var (service, users, azureValidator, _) = CreateService();
        azureValidator.ClaimsToReturn = new AzureClaims("oid-1", "alice@example.com", "Alice");
        users.ByAzureOid = MakeRow(role: "NotARealRole", isActive: true);

        var (user, _) = await service.SsoLoginAsync("token");

        Assert.Equal(UserRole.Viewer, user.Role);
    }
}
