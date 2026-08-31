using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Monitor.Core.Domain;
using Monitor.Core.Options;
using Monitor.Identity.Authentication;
using Monitor.Identity.Users;

namespace Monitor.UnitTests.Users;

/// <summary>
/// Exercises <see cref="AppJwtIssuer"/> — the analogue of the `jwt.sign(..., config.jwt.secret,
/// { expiresIn: config.jwt.expiresIn })` calls at the end of both `login` and `ssoLogin`
/// (auth.service.ts). Verifies the emitted token is a valid, verifiable HS256 JWT carrying
/// exactly the claims the source set, with `oid` only present when supplied.
/// </summary>
public class AppJwtIssuerTests
{
    private const string Secret = "unit-test-signing-secret-0123456789";

    private static AppJwtIssuer CreateIssuer(string secret = Secret, string expiresIn = "8h") =>
        new(Microsoft.Extensions.Options.Options.Create(new JwtOptions { Secret = secret, ExpiresIn = expiresIn }));

    private static JwtSecurityToken Decode(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    [Fact]
    public void IssueToken_WithoutAzureOid_SetsSubEmailNameAndRoleClaims_AndOmitsOid()
    {
        var issuer = CreateIssuer();

        var token = issuer.IssueToken("user-1", "alice@example.com", "Alice Example", UserRole.Admin);
        var jwt = Decode(token);

        Assert.Equal("user-1", jwt.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal("alice@example.com", jwt.Claims.Single(c => c.Type == "email").Value);
        Assert.Equal("Alice Example", jwt.Claims.Single(c => c.Type == "name").Value);
        Assert.Equal("Admin", jwt.Claims.Single(c => c.Type == "role").Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "oid");
    }

    [Theory]
    [InlineData(UserRole.Admin, "Admin")]
    [InlineData(UserRole.Operator, "Operator")]
    [InlineData(UserRole.Viewer, "Viewer")]
    public void IssueToken_RoleClaim_UsesExactWireString(UserRole role, string expectedWireValue)
    {
        var issuer = CreateIssuer();

        var token = issuer.IssueToken("user-1", "a@b.com", "A B", role);
        var jwt = Decode(token);

        Assert.Equal(expectedWireValue, jwt.Claims.Single(c => c.Type == "role").Value);
    }

    [Fact]
    public void IssueToken_WithAzureOid_IncludesOidClaim()
    {
        var issuer = CreateIssuer();

        var token = issuer.IssueToken("user-1", "alice@example.com", "Alice", UserRole.Viewer, "azure-oid-123");
        var jwt = Decode(token);

        Assert.Equal("azure-oid-123", jwt.Claims.Single(c => c.Type == "oid").Value);
    }

    [Fact]
    public void IssueToken_EmptyAzureOid_OmitsOidClaim_SameAsNull()
    {
        var issuer = CreateIssuer();

        var token = issuer.IssueToken("user-1", "alice@example.com", "Alice", UserRole.Viewer, string.Empty);
        var jwt = Decode(token);

        Assert.DoesNotContain(jwt.Claims, c => c.Type == "oid");
    }

    [Fact]
    public void IssueToken_IsSignedWithHmacSha256UsingConfiguredSecret()
    {
        var issuer = CreateIssuer(secret: "correct-horse-battery-staple-key");

        var token = issuer.IssueToken("user-1", "a@b.com", "A", UserRole.Viewer);
        var jwt = Decode(token);

        Assert.Equal(SecurityAlgorithms.HmacSha256, jwt.Header.Alg);

        // Round-trip through full signature validation with the same secret to prove the
        // token isn't merely well-formed but actually verifiable end to end. Clear the
        // handler's default inbound claim-type remapping (e.g. "sub" -> the long
        // ClaimTypes.NameIdentifier URI) so FindFirst("sub") below sees the raw claim
        // type — the same effect AppJwtIssuer's consumer gets via JwtBearerOptions'
        // MapInboundClaims = false.
        var handler = new JwtSecurityTokenHandler { InboundClaimTypeMap = new Dictionary<string, string>() };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("correct-horse-battery-staple-key")),
        };
        var principal = handler.ValidateToken(token, parameters, out _);
        Assert.Equal("user-1", principal.FindFirst("sub")!.Value);
    }

    [Fact]
    public void IssueToken_WrongSecret_FailsSignatureValidation()
    {
        var issuer = CreateIssuer(secret: "correct-horse-battery-staple-key");
        var token = issuer.IssueToken("user-1", "a@b.com", "A", UserRole.Viewer);

        var handler = new JwtSecurityTokenHandler();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-completely-different-secret-value")),
        };

        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(token, parameters, out _));
    }

    [Fact]
    public void IssueToken_ExpiryMatchesConfiguredExpiresIn_WithinTolerance()
    {
        var issuer = CreateIssuer(expiresIn: "2h");
        var before = DateTime.UtcNow;

        var token = issuer.IssueToken("user-1", "a@b.com", "A", UserRole.Viewer);
        var jwt = Decode(token);

        var expected = before.Add(TimeSpan.FromHours(2));
        Assert.True(Math.Abs((jwt.ValidTo - expected).TotalSeconds) < 5,
            $"Expected expiry near {expected:o}, got {jwt.ValidTo:o}");
    }

    [Fact]
    public void IssueToken_MissingExpiresIn_FallsBackToJwtExpiryDefault()
    {
        var issuer = CreateIssuer(expiresIn: "not-a-real-duration");
        var before = DateTime.UtcNow;

        var token = issuer.IssueToken("user-1", "a@b.com", "A", UserRole.Viewer);
        var jwt = Decode(token);

        var expected = before.Add(JwtExpiry.Default);
        Assert.True(Math.Abs((jwt.ValidTo - expected).TotalSeconds) < 5,
            $"Expected expiry near {expected:o}, got {jwt.ValidTo:o}");
    }

    [Fact]
    public void IssueToken_DistinctSubjectsProduceDistinctTokens()
    {
        var issuer = CreateIssuer();

        var tokenA = issuer.IssueToken("user-a", "a@example.com", "A", UserRole.Viewer);
        var tokenB = issuer.IssueToken("user-b", "b@example.com", "B", UserRole.Viewer);

        Assert.NotEqual(tokenA, tokenB);
    }
}
