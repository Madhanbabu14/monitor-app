using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Monitor.Core.Errors;

namespace Monitor.Identity.Authentication;

/// <summary>
/// Shared `JwtBearerEvents.OnChallenge` / `OnForbidden` handlers wired into
/// both the <see cref="AuthenticationSchemes.AppJwt"/> and
/// <see cref="AuthenticationSchemes.AzureAd"/> schemes so that authentication
/// and authorization failures produce the exact same JSON envelope the
/// source emitted, even though — unlike `AppException` — these failures
/// never flow through the terminal exception-handling middleware (ASP.NET
/// Core's authentication/authorization middleware short-circuits before an
/// endpoint, and hence before that middleware, ever runs).
///
/// Maps back to `auth.middleware.ts`:
///   - no/malformed `Authorization` header -&gt; AppError(401, 'No authentication token provided')
///   - token fails to decode at all       -&gt; AppError(401, 'Invalid token format')
///   - any other verification failure     -&gt; AppError(401, 'Invalid or expired token')
///   - role check fails (authorize(...))  -&gt; AppError(403, 'Insufficient permissions')
/// </summary>
public static class AuthResponseWriter
{
    public static Task OnChallenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();

        var authHeader = context.Request.Headers.Authorization.ToString();
        var hasBearerToken = !string.IsNullOrEmpty(authHeader)
            && authHeader.StartsWith("Bearer ", StringComparison.Ordinal);

        string message;
        if (!hasBearerToken)
        {
            message = "No authentication token provided";
        }
        else if (context.AuthenticateFailure is SecurityTokenMalformedException)
        {
            message = "Invalid token format";
        }
        else
        {
            message = "Invalid or expired token";
        }

        return WriteEnvelope(context.Response, StatusCodes.Status401Unauthorized, message);
    }

    public static Task OnForbidden(ForbiddenContext context)
        => WriteEnvelope(context.Response, StatusCodes.Status403Forbidden, "Insufficient permissions");

    private static Task WriteEnvelope(HttpResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        return response.WriteAsJsonAsync(new ErrorEnvelope(message));
    }
}
