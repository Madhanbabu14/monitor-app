using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Monitor.Identity.Authentication;

namespace Monitor.UnitTests.Authentication;

/// <summary>
/// Exercises <see cref="AuthResponseWriter"/>, the shared `JwtBearerEvents.OnChallenge`
/// / `OnForbidden` handlers that reproduce the exact JSON envelope
/// `middleware/auth.middleware.ts` produced for authentication/authorization failures,
/// even though ASP.NET Core's auth middleware short-circuits before the terminal
/// exception-handling middleware would otherwise run.
/// </summary>
public class AuthResponseWriterTests
{
    private static readonly AuthenticationScheme Scheme =
        new(AuthenticationSchemes.AppJwt, AuthenticationSchemes.AppJwt, typeof(JwtBearerHandler));

    private static DefaultHttpContext MakeHttpContext(string? authorizationHeader)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (authorizationHeader is not null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        return context;
    }

    private static async Task<(int statusCode, string? contentType, JsonDocument body)> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var text = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, context.Response.ContentType, JsonDocument.Parse(text));
    }

    private static JwtBearerChallengeContext MakeChallengeContext(string? authorizationHeader, Exception? authenticateFailure)
    {
        var httpContext = MakeHttpContext(authorizationHeader);
        var context = new JwtBearerChallengeContext(httpContext, Scheme, new JwtBearerOptions(), new AuthenticationProperties())
        {
            AuthenticateFailure = authenticateFailure!,
        };
        return context;
    }

    [Fact]
    public async Task OnChallenge_NoAuthorizationHeader_Returns401WithNoTokenMessage()
    {
        var context = MakeChallengeContext(authorizationHeader: null, authenticateFailure: null);

        await AuthResponseWriter.OnChallenge(context);

        var (statusCode, contentType, body) = await ReadResponseAsync(context.HttpContext);
        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
        // WriteAsJsonAsync(value) with no explicit contentType argument unconditionally
        // overwrites the ContentType WriteEnvelope set moments earlier, appending
        // "; charset=utf-8" — the "response.ContentType = application/json" statement in
        // AuthResponseWriter.WriteEnvelope is therefore dead code, but the effective
        // content type is still correct (and still starts with "application/json").
        Assert.Equal("application/json; charset=utf-8", contentType);
        Assert.Equal("error", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("No authentication token provided", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OnChallenge_NonBearerAuthorizationHeader_Returns401WithNoTokenMessage()
    {
        var context = MakeChallengeContext("Basic dXNlcjpwYXNz", authenticateFailure: null);

        await AuthResponseWriter.OnChallenge(context);

        var (statusCode, _, body) = await ReadResponseAsync(context.HttpContext);
        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
        Assert.Equal("No authentication token provided", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OnChallenge_MalformedTokenFailure_Returns401WithInvalidFormatMessage()
    {
        var context = MakeChallengeContext(
            "Bearer some.malformed.token",
            authenticateFailure: new SecurityTokenMalformedException("malformed"));

        await AuthResponseWriter.OnChallenge(context);

        var (statusCode, _, body) = await ReadResponseAsync(context.HttpContext);
        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
        Assert.Equal("Invalid token format", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OnChallenge_ExpiredTokenFailure_Returns401WithInvalidOrExpiredMessage()
    {
        var context = MakeChallengeContext(
            "Bearer some.valid.jwt",
            authenticateFailure: new SecurityTokenExpiredException("expired"));

        await AuthResponseWriter.OnChallenge(context);

        var (statusCode, _, body) = await ReadResponseAsync(context.HttpContext);
        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
        Assert.Equal("Invalid or expired token", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OnChallenge_BearerPresentButNoFailureRecorded_Returns401WithInvalidOrExpiredMessage()
    {
        // e.g. signature verification failed with a plain SecurityTokenException,
        // not specifically a "malformed" one — should still fall into the generic bucket.
        var context = MakeChallengeContext("Bearer some.valid.jwt", authenticateFailure: null);

        await AuthResponseWriter.OnChallenge(context);

        var (_, _, body) = await ReadResponseAsync(context.HttpContext);
        Assert.Equal("Invalid or expired token", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OnChallenge_GenericSecurityTokenException_Returns401WithInvalidOrExpiredMessage()
    {
        var context = MakeChallengeContext(
            "Bearer some.valid.jwt",
            authenticateFailure: new SecurityTokenInvalidSignatureException("bad signature"));

        await AuthResponseWriter.OnChallenge(context);

        var (_, _, body) = await ReadResponseAsync(context.HttpContext);
        Assert.Equal("Invalid or expired token", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OnChallenge_CallsHandleResponse_SoDefaultChallengeBehaviorIsSuppressed()
    {
        var context = MakeChallengeContext(authorizationHeader: null, authenticateFailure: null);

        await AuthResponseWriter.OnChallenge(context);

        Assert.True(context.Handled);
    }

    [Fact]
    public async Task OnForbidden_Returns403WithInsufficientPermissionsMessage()
    {
        var httpContext = MakeHttpContext(authorizationHeader: "Bearer some.valid.jwt");
        var context = new ForbiddenContext(httpContext, Scheme, new JwtBearerOptions());

        await AuthResponseWriter.OnForbidden(context);

        var (statusCode, contentType, body) = await ReadResponseAsync(httpContext);
        Assert.Equal(StatusCodes.Status403Forbidden, statusCode);
        // See the matching comment in OnChallenge_NoAuthorizationHeader_...: WriteAsJsonAsync
        // overwrites the ContentType to include "; charset=utf-8".
        Assert.Equal("application/json; charset=utf-8", contentType);
        Assert.Equal("error", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Insufficient permissions", body.RootElement.GetProperty("message").GetString());
    }
}
