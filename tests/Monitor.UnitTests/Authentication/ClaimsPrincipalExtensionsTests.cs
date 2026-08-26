using System.Security.Claims;
using Monitor.Core.Domain;
using Monitor.Identity.Authentication;

namespace Monitor.UnitTests.Authentication;

/// <summary>
/// Exercises <see cref="ClaimsPrincipalExtensions.ToAuthenticatedUser"/>, which
/// reconstructs the `req.user` shape assembled at the bottom of `authenticate`
/// (auth.middleware.ts):
/// <code>
/// req.user = {
///   id: payload.sub ?? payload.oid ?? '',
///   email: payload.email ?? payload.preferred_username ?? '',
///   displayName: payload.name ?? '',
///   role: (payload.role as UserRole) ?? 'Viewer',
///   azureOid: payload.oid ?? '',
/// };
/// </code>
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal MakePrincipal(params (string type, string value)[] claims)
    {
        var identity = new ClaimsIdentity(claims.Select(c => new Claim(c.type, c.value)));
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void ToAuthenticatedUser_AllClaimsPresent_MapsEveryFieldDirectly()
    {
        var principal = MakePrincipal(
            ("sub", "user-123"),
            ("oid", "azure-oid-456"),
            ("email", "alice@example.com"),
            ("preferred_username", "alice-upn@example.com"),
            ("name", "Alice Example"),
            ("role", "Admin"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal("user-123", user.Id);
        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal("Alice Example", user.DisplayName);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal("azure-oid-456", user.AzureOid);
    }

    [Fact]
    public void ToAuthenticatedUser_NoSub_FallsBackIdToOid()
    {
        var principal = MakePrincipal(("oid", "azure-oid-456"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal("azure-oid-456", user.Id);
        Assert.Equal("azure-oid-456", user.AzureOid);
    }

    [Fact]
    public void ToAuthenticatedUser_NoSubAndNoOid_IdIsEmptyString()
    {
        var principal = MakePrincipal(("email", "bob@example.com"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal(string.Empty, user.Id);
        Assert.Equal(string.Empty, user.AzureOid);
    }

    [Fact]
    public void ToAuthenticatedUser_NoEmail_FallsBackToPreferredUsername()
    {
        var principal = MakePrincipal(("preferred_username", "carol-upn@example.com"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal("carol-upn@example.com", user.Email);
    }

    [Fact]
    public void ToAuthenticatedUser_NoEmailAndNoPreferredUsername_EmailIsEmptyString()
    {
        var principal = MakePrincipal(("sub", "user-1"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal(string.Empty, user.Email);
    }

    [Fact]
    public void ToAuthenticatedUser_NoName_DisplayNameIsEmptyString()
    {
        var principal = MakePrincipal(("sub", "user-1"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal(string.Empty, user.DisplayName);
    }

    [Fact]
    public void ToAuthenticatedUser_NoRoleClaim_DefaultsToViewer()
    {
        var principal = MakePrincipal(("sub", "user-1"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal(UserRole.Viewer, user.Role);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("admin")] // wrong case
    [InlineData("")]
    public void ToAuthenticatedUser_UnparseableRoleClaim_FallsBackToViewer_HardeningBeyondSource(string roleValue)
    {
        // The source cast an arbitrary `payload.role` string straight to the TS
        // `UserRole` type with no runtime check. .NET has no equivalent unchecked
        // cast for an enum, so an unparseable role value falls back to Viewer
        // (same default as an absent claim) rather than throwing.
        var principal = MakePrincipal(("sub", "user-1"), ("role", roleValue));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal(UserRole.Viewer, user.Role);
    }

    [Theory]
    [InlineData("Admin", UserRole.Admin)]
    [InlineData("Operator", UserRole.Operator)]
    [InlineData("Viewer", UserRole.Viewer)]
    public void ToAuthenticatedUser_ValidRoleClaim_MapsToExpectedEnumValue(string roleValue, UserRole expected)
    {
        var principal = MakePrincipal(("sub", "user-1"), ("role", roleValue));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal(expected, user.Role);
    }

    [Fact]
    public void ToAuthenticatedUser_NoOid_AzureOidIsEmptyString()
    {
        var principal = MakePrincipal(("sub", "user-1"), ("email", "d@example.com"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal(string.Empty, user.AzureOid);
    }

    [Fact]
    public void ToAuthenticatedUser_EmptyPrincipal_EveryFieldFallsBackToDefault()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var user = principal.ToAuthenticatedUser();

        Assert.Equal(string.Empty, user.Id);
        Assert.Equal(string.Empty, user.Email);
        Assert.Equal(string.Empty, user.DisplayName);
        Assert.Equal(UserRole.Viewer, user.Role);
        Assert.Equal(string.Empty, user.AzureOid);
    }

    [Fact]
    public void ToAuthenticatedUser_EmailPreferredOverPreferredUsername_WhenBothPresent()
    {
        var principal = MakePrincipal(
            ("email", "primary@example.com"),
            ("preferred_username", "secondary@example.com"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal("primary@example.com", user.Email);
    }

    [Fact]
    public void ToAuthenticatedUser_SubPreferredOverOid_WhenBothPresent()
    {
        var principal = MakePrincipal(("sub", "sub-id"), ("oid", "oid-id"));

        var user = principal.ToAuthenticatedUser();

        Assert.Equal("sub-id", user.Id);
        // azureOid is always sourced from `oid` independently of the id fallback chain.
        Assert.Equal("oid-id", user.AzureOid);
    }
}
