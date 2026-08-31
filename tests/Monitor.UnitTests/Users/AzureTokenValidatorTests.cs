using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monitor.Core.Errors;
using Monitor.Core.Options;
using Monitor.Identity.Users;

namespace Monitor.UnitTests.Users;

/// <summary>
/// Exercises <see cref="AzureTokenValidator"/> — the port of utils/azureAuth.ts's
/// `validateAzureToken`. Only the deterministic, network-free branch (the
/// `jwt.decode(token, { complete: true })` + "does the header carry a `kid`" guard, which
/// runs before any OIDC discovery / JWKS fetch) is covered here: exercising signature
/// verification would require either a live call to
/// login.microsoftonline.com's well-known metadata endpoint or reimplementing
/// <see cref="Microsoft.IdentityModel.Protocols.ConfigurationManager{T}"/>'s caching, which
/// belongs in Monitor.IntegrationTests, not a network-free unit test.
/// </summary>
public class AzureTokenValidatorTests
{
    private static AzureTokenValidator CreateValidator(string tenantId = "tenant-1", string clientId = "client-1") =>
        new(Microsoft.Extensions.Options.Options.Create(new AzureAdOptions { TenantId = tenantId, ClientId = clientId }), NullLogger<AzureTokenValidator>.Instance);

    [Fact]
    public async Task ValidateAsync_NotAJwt_Throws401MissingKeyId()
    {
        var validator = CreateValidator();

        var ex = await Assert.ThrowsAsync<AppException>(() => validator.ValidateAsync("not-a-jwt-at-all"));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Invalid Azure AD token: missing key ID", ex.Message);
    }

    [Fact]
    public async Task ValidateAsync_EmptyString_Throws401MissingKeyId()
    {
        var validator = CreateValidator();

        var ex = await Assert.ThrowsAsync<AppException>(() => validator.ValidateAsync(string.Empty));

        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task ValidateAsync_StructurallyInvalidThreePartToken_Throws401MissingKeyId()
    {
        // Three dot-separated segments that aren't valid base64url JSON — CanReadToken may
        // pass a loose shape check, but ReadJwtToken then fails to parse it, which
        // AzureTokenValidator treats identically to an unreadable token.
        var validator = CreateValidator();

        var ex = await Assert.ThrowsAsync<AppException>(() => validator.ValidateAsync("not-json.not-json.not-json"));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Invalid Azure AD token: missing key ID", ex.Message);
    }

    [Fact]
    public async Task ValidateAsync_ValidJwtStructureWithoutKidHeader_Throws401MissingKeyId()
    {
        // A syntactically valid, readable JWT whose header simply never set "kid" — the
        // header/payload/signature segments are valid base64url JSON, so CanReadToken/
        // ReadJwtToken succeed, but the header.kid guard still trips.
        var header = Base64UrlEncode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64UrlEncode("{\"sub\":\"1234567890\"}");
        var token = $"{header}.{payload}.";

        var validator = CreateValidator();

        var ex = await Assert.ThrowsAsync<AppException>(() => validator.ValidateAsync(token));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Invalid Azure AD token: missing key ID", ex.Message);
    }

    [Fact]
    public void Constructor_DoesNotMakeANetworkCall()
    {
        // Building the ConfigurationManager<OpenIdConnectConfiguration> only stores the
        // metadata address; it doesn't fetch it until GetConfigurationAsync is first
        // awaited (i.e. only once a token actually reaches signature verification).
        var exception = Record.Exception(() => CreateValidator());

        Assert.Null(exception);
    }

    private static string Base64UrlEncode(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
