using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Monitor.Identity.Authentication;

/// <summary>
/// Picks which JwtBearer scheme should handle the current request, by
/// peeking at the bearer token's JOSE header — exactly what `authenticate`
/// (auth.middleware.ts) did before verifying:
/// <code>
/// const decoded = jwt.decode(token, { complete: true });
/// if (decoded.header.kid) { // Azure AD: verify via JWKS }
/// else { // app-issued: verify via HS256 secret }
/// </code>
/// This never verifies the signature — it only decides which handler's
/// <c>TokenValidationParameters</c> should attempt verification. An
/// undecodable/malformed header still routes to <see cref="AuthenticationSchemes.AppJwt"/>
/// so that handler's own token parsing raises the "malformed token" failure
/// path (see <see cref="AuthResponseWriter"/>), matching the source's
/// `AppError(401, 'Invalid token format')` for a token that fails
/// `jwt.decode`.
/// </summary>
public static class JwtSchemeSelector
{
    public static string SelectScheme(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            // No/malformed Authorization header — forward to AppJwt anyway so the
            // "missing token" 401 path is produced consistently by one place
            // (AuthResponseWriter.OnChallenge).
            return AuthenticationSchemes.AppJwt;
        }

        var token = authHeader["Bearer ".Length..];
        return HasKeyId(token) ? AuthenticationSchemes.AzureAd : AuthenticationSchemes.AppJwt;
    }

    private static bool HasKeyId(string token)
    {
        try
        {
            var headerSegment = token.Split('.', 3)[0];
            var headerJson = Base64UrlDecode(headerSegment);
            using var doc = JsonDocument.Parse(headerJson);
            return doc.RootElement.TryGetProperty("kid", out var kid)
                && kid.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(kid.GetString());
        }
        catch
        {
            // Malformed token: fall through to AppJwt, whose own
            // JwtSecurityTokenHandler will reject it with a proper
            // SecurityTokenMalformedException.
            return false;
        }
    }

    private static string Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
