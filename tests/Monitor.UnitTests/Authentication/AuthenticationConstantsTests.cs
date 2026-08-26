using Monitor.Identity.Authentication;
using Monitor.Identity.Authorization;

namespace Monitor.UnitTests.Authentication;

/// <summary>
/// Pins the exact wire/scheme/policy names down as a regression guard: these strings
/// are load-bearing (scheme selection, claim lookup, `[Authorize(Policy = ...)]` call
/// sites in future endpoint slices) even though nothing here is complex logic.
/// </summary>
public class AuthenticationConstantsTests
{
    [Fact]
    public void AuthenticationSchemes_HaveExpectedNames()
    {
        Assert.Equal("AppJwt", AuthenticationSchemes.AppJwt);
        Assert.Equal("AzureAd", AuthenticationSchemes.AzureAd);
        Assert.Equal("DualJwt", AuthenticationSchemes.DualJwt);
    }

    [Fact]
    public void AuthenticationSchemes_AreAllDistinct()
    {
        var names = new[] { AuthenticationSchemes.AppJwt, AuthenticationSchemes.AzureAd, AuthenticationSchemes.DualJwt };

        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void AuthorizationPolicyNames_HaveExpectedNames()
    {
        Assert.Equal("AuthenticatedUser", AuthorizationPolicyNames.AuthenticatedUser);
        Assert.Equal("RequireAdmin", AuthorizationPolicyNames.RequireAdmin);
        Assert.Equal("RequireOperator", AuthorizationPolicyNames.RequireOperator);
        Assert.Equal("RequireViewer", AuthorizationPolicyNames.RequireViewer);
    }

    [Fact]
    public void AuthorizationPolicyNames_AreAllDistinct()
    {
        var names = new[]
        {
            AuthorizationPolicyNames.AuthenticatedUser,
            AuthorizationPolicyNames.RequireAdmin,
            AuthorizationPolicyNames.RequireOperator,
            AuthorizationPolicyNames.RequireViewer,
        };

        Assert.Equal(names.Length, names.Distinct().Count());
    }
}
