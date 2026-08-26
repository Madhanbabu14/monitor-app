using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Monitor.Core.Errors;
using Monitor.Core.Options;

namespace Monitor.Identity.Users;

/// <inheritdoc cref="IAzureTokenValidator"/>
/// <remarks>
/// Uses ASP.NET Core's own OIDC discovery/JWKS caching
/// (<see cref="ConfigurationManager{T}"/> over the tenant's well-known
/// metadata document) instead of porting the source's hand-rolled
/// `jwks-rsa` client (utils/azureAuth.ts) — the same substitution already
/// made for the <see cref="Monitor.Identity.Authentication.AuthenticationSchemes.AzureAd"/>
/// JwtBearer scheme, applied here because this validator checks a token
/// carried in the POST /api/auth/sso request body rather than the
/// Authorization header, so it can't just reuse that scheme's pipeline.
/// </remarks>
public sealed class AzureTokenValidator : IAzureTokenValidator
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly AzureAdOptions _options;
    private readonly ILogger<AzureTokenValidator> _logger;

    public AzureTokenValidator(IOptions<AzureAdOptions> options, ILogger<AzureTokenValidator> logger)
    {
        _options = options.Value;
        _logger = logger;

        var metadataAddress =
            $"https://login.microsoftonline.com/{_options.TenantId}/v2.0/.well-known/openid-configuration";
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }

    public async Task<AzureClaims> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        var handler = new JwtSecurityTokenHandler();

        // Source: `jwt.decode(token, { complete: true })` then bail out if the
        // header has no `kid` — mirrored here as "can this even be read, and
        // does it carry a key id" before attempting signature verification.
        JwtSecurityToken? jwtToken;
        if (!handler.CanReadToken(token) || (jwtToken = SafeReadToken(handler, token)) is null || string.IsNullOrEmpty(jwtToken.Header.Kid))
        {
            _logger.LogError("[SSO] Token decode failed — missing or malformed header");
            throw new AppException(401, "Invalid Azure AD token: missing key ID");
        }

        var expectedIssuer = $"https://login.microsoftonline.com/{_options.TenantId}/v2.0";
        _logger.LogInformation(
            "[SSO] Token received — pre-validation claims aud={Audience} iss={Issuer}",
            jwtToken.Audiences.FirstOrDefault(),
            jwtToken.Issuer);

        var configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = expectedIssuer,
            ValidateAudience = true,
            ValidAudience = _options.ClientId,
            ValidateLifetime = true,
            IssuerSigningKeys = configuration.SigningKeys,
        };

        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(token, validationParameters, out _);
            _logger.LogInformation("[SSO] Signature + claims verification passed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SSO] Token verification failed");
            throw new AppException(401, $"Azure AD token validation failed: {ex.Message}");
        }

        var oid = principal.FindFirstValue("oid") ?? principal.FindFirstValue("sub") ?? string.Empty;
        var email = principal.FindFirstValue("email") ?? principal.FindFirstValue("preferred_username") ?? string.Empty;
        var name = principal.FindFirstValue("name") ?? email;

        if (string.IsNullOrEmpty(oid) || string.IsNullOrEmpty(email))
        {
            _logger.LogError("[SSO] Required claims missing after verification");
            throw new AppException(401, "Azure AD token is missing required claims (oid, email)");
        }

        _logger.LogInformation("[SSO] Token validated successfully for {Email}", email);
        return new AzureClaims(oid, email, name);
    }

    private static JwtSecurityToken? SafeReadToken(JwtSecurityTokenHandler handler, string token)
    {
        try
        {
            return handler.ReadJwtToken(token);
        }
        catch
        {
            return null;
        }
    }
}
