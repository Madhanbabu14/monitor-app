using System.Text;
using Microsoft.AspNetCore.Http;
using Monitor.Identity.Authentication;

namespace Monitor.UnitTests.Authentication;

/// <summary>
/// Exercises <see cref="JwtSchemeSelector.SelectScheme"/>, the .NET analogue of
/// `authenticate`'s branch on `decoded.header.kid` (auth.middleware.ts). This never
/// verifies a signature; it only decides which concrete JwtBearer handler
/// (<see cref="AuthenticationSchemes.AppJwt"/> or <see cref="AuthenticationSchemes.AzureAd"/>)
/// should attempt verification, based on whether the JOSE header carries a "kid".
/// </summary>
public class JwtSchemeSelectorTests
{
    private static string Base64UrlEncode(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string MakeToken(string headerJson, string payload = "e30", string signature = "sig")
        => $"{Base64UrlEncode(headerJson)}.{payload}.{signature}";

    private static HttpContext MakeContext(string? authorizationHeader)
    {
        var context = new DefaultHttpContext();
        if (authorizationHeader is not null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        return context;
    }

    [Fact]
    public void SelectScheme_NoAuthorizationHeader_ReturnsAppJwt()
    {
        var context = MakeContext(authorizationHeader: null);

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_EmptyAuthorizationHeader_ReturnsAppJwt()
    {
        var context = MakeContext(string.Empty);

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("bearer sometoken")] // wrong case, source used exact "Bearer " prefix
    [InlineData("Bearertoken")] // missing separating space
    public void SelectScheme_NonBearerAuthorizationHeader_ReturnsAppJwt(string header)
    {
        var context = MakeContext(header);

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_BearerWithNoTokenAtAll_ReturnsAppJwt()
    {
        var context = MakeContext("Bearer ");

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_HeaderWithoutKid_ReturnsAppJwt()
    {
        var token = MakeToken("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var context = MakeContext($"Bearer {token}");

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_HeaderWithKid_ReturnsAzureAd()
    {
        var token = MakeToken("{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":\"abc123\"}");
        var context = MakeContext($"Bearer {token}");

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AzureAd, scheme);
    }

    [Fact]
    public void SelectScheme_HeaderWithEmptyStringKid_ReturnsAppJwt()
    {
        var token = MakeToken("{\"alg\":\"RS256\",\"kid\":\"\"}");
        var context = MakeContext($"Bearer {token}");

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_HeaderWithNonStringKid_ReturnsAppJwt()
    {
        // A "kid" that isn't a JSON string should not satisfy the ValueKind == String check.
        var token = MakeToken("{\"alg\":\"RS256\",\"kid\":12345}");
        var context = MakeContext($"Bearer {token}");

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_HeaderWithNullKid_ReturnsAppJwt()
    {
        var token = MakeToken("{\"alg\":\"RS256\",\"kid\":null}");
        var context = MakeContext($"Bearer {token}");

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_MalformedBase64Header_ReturnsAppJwt()
    {
        // "!!!" is not valid base64url and cannot be decoded — falls into the catch
        // block, matching the source's malformed-token path routing to AppJwt so its
        // own JwtSecurityTokenHandler raises SecurityTokenMalformedException.
        var context = MakeContext("Bearer !!!.payload.sig");

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_HeaderSegmentIsValidBase64ButNotJson_ReturnsAppJwt()
    {
        var notJson = Convert.ToBase64String(Encoding.UTF8.GetBytes("not-json-at-all"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var context = MakeContext($"Bearer {notJson}.payload.sig");

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_TokenWithNoDots_ReturnsAppJwt()
    {
        var context = MakeContext("Bearer justoneopaquestring");

        var scheme = JwtSchemeSelector.SelectScheme(context);

        Assert.Equal(AuthenticationSchemes.AppJwt, scheme);
    }

    [Fact]
    public void SelectScheme_HeaderSegmentRequiringBase64UrlPadding_DecodesSuccessfully()
    {
        // Regression guard for the manual padding logic in Base64UrlDecode: pick a
        // header whose base64url form needs each of the two possible padding lengths.
        var tokenNoKid = MakeToken("{\"alg\":\"HS256\"}");
        var tokenWithKid = MakeToken("{\"alg\":\"RS256\",\"kid\":\"k1\"}");

        Assert.Equal(AuthenticationSchemes.AppJwt, JwtSchemeSelector.SelectScheme(MakeContext($"Bearer {tokenNoKid}")));
        Assert.Equal(AuthenticationSchemes.AzureAd, JwtSchemeSelector.SelectScheme(MakeContext($"Bearer {tokenWithKid}")));
    }
}
