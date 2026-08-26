using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Monitor.Core.Domain;
using Monitor.Core.Options;
using Monitor.Identity.Authentication;
using Monitor.Identity.Authorization;

namespace Monitor.Identity.DependencyInjection;

/// <summary>
/// Registers the "auth shell": two JwtBearer schemes (app-issued HS256,
/// Azure AD JWKS/RS256) fronted by one forwarding scheme, plus the named
/// authorization policies used to gate endpoints. Direct translation of
/// `middleware/auth.middleware.ts` (`authenticate` + `authorize`) and
/// `utils/azureAuth.ts` (`validateAzureToken`) into ASP.NET Core's
/// authentication/authorization pipeline, so downstream endpoint modules can
/// use `.RequireAuthorization()` / `.RequireAuthorization(AuthorizationPolicyNames.RequireAdmin)`
/// instead of hand-rolled middleware.
///
/// Called once from Monitor.Api's Program.cs, after the validated
/// <see cref="JwtOptions"/> / <see cref="AzureAdOptions"/> sections have been
/// registered via <c>AddValidatedOptions</c>. User/login/SSO-exchange
/// services (the rest of the Monitor.Identity bounded context) are added by
/// their own slice(s) — this method only wires the shell.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        var azureAdOptions = configuration.GetSection(AzureAdOptions.SectionName).Get<AzureAdOptions>() ?? new AzureAdOptions();

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = AuthenticationSchemes.DualJwt;
                options.DefaultAuthenticateScheme = AuthenticationSchemes.DualJwt;
                options.DefaultChallengeScheme = AuthenticationSchemes.DualJwt;
                options.DefaultForbidScheme = AuthenticationSchemes.DualJwt;
            })
            // The .NET analogue of `authenticate`'s branch on `decoded.header.kid`:
            // forward to whichever concrete JwtBearer scheme should attempt
            // verification, without itself doing any verification.
            .AddPolicyScheme(AuthenticationSchemes.DualJwt, "App JWT or Azure AD JWT", options =>
            {
                options.ForwardDefaultSelector = JwtSchemeSelector.SelectScheme;
            })
            .AddJwtBearer(AuthenticationSchemes.AppJwt, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),
                    RoleClaimType = JwtClaimTypes.Role,
                    NameClaimType = JwtClaimTypes.Subject,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = AuthResponseWriter.OnChallenge,
                    OnForbidden = AuthResponseWriter.OnForbidden,
                };
            })
            .AddJwtBearer(AuthenticationSchemes.AzureAd, options =>
            {
                options.MapInboundClaims = false;
                // Authority drives OIDC discovery -> jwks_uri, the .NET built-in
                // equivalent of the source's hand-rolled `jwks-rsa` client
                // (utils/azureAuth.ts's `jwksClient({ jwksUri: ..., cache: true,
                // rateLimit: true })`); the framework's own JWKS cache replaces
                // both the `cache`/`rateLimit` options.
                options.Authority = $"https://login.microsoftonline.com/{azureAdOptions.TenantId}/v2.0";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = $"https://login.microsoftonline.com/{azureAdOptions.TenantId}/v2.0",
                    ValidateAudience = true,
                    ValidAudience = azureAdOptions.ClientId,
                    ValidateLifetime = true,
                    RoleClaimType = JwtClaimTypes.Role,
                    NameClaimType = JwtClaimTypes.Subject,
                };
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = AuthResponseWriter.OnChallenge,
                    OnForbidden = AuthResponseWriter.OnForbidden,
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicyNames.AuthenticatedUser, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AuthorizationPolicyNames.RequireAdmin, policy => policy.RequireRole(UserRole.Admin.ToWireString()));
            options.AddPolicy(AuthorizationPolicyNames.RequireOperator, policy => policy.RequireRole(UserRole.Operator.ToWireString()));
            options.AddPolicy(AuthorizationPolicyNames.RequireViewer, policy => policy.RequireRole(UserRole.Viewer.ToWireString()));
        });

        return services;
    }
}
