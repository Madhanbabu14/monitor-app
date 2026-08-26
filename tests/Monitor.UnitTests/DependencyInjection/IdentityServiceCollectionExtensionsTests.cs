using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Monitor.Identity.Authentication;
using Monitor.Identity.Authorization;
using Monitor.Identity.DependencyInjection;

namespace Monitor.UnitTests.DependencyInjection;

/// <summary>
/// Exercises <see cref="IdentityServiceCollectionExtensions.AddMonitorIdentity"/> —
/// the "auth shell" wiring translating `middleware/auth.middleware.ts`
/// (`authenticate` + `authorize`) and `utils/azureAuth.ts` (`validateAzureToken`)
/// into two JwtBearer schemes fronted by a forwarding policy scheme, plus the named
/// authorization policies. Everything here is resolved purely through DI
/// registration/options binding — no live Postgres, no network call to Azure AD's
/// discovery endpoint is ever made (AzureAd's OIDC metadata is fetched lazily, only
/// when a request is actually authenticated against that scheme).
/// </summary>
public class IdentityServiceCollectionExtensionsTests
{
    private static IConfiguration MakeConfiguration(string? jwtSecret = "unit-test-secret-value-0123456789", string? tenantId = "tenant-abc", string? clientId = "client-xyz")
    {
        var values = new Dictionary<string, string?>();
        if (jwtSecret is not null) values["Jwt:Secret"] = jwtSecret;
        if (tenantId is not null) values["AzureAd:TenantId"] = tenantId;
        if (clientId is not null) values["AzureAd:ClientId"] = clientId;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMonitorIdentity(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddMonitorIdentity_ReturnsTheSameServiceCollection_ForFluentChaining()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var returned = services.AddMonitorIdentity(MakeConfiguration());

        Assert.Same(services, returned);
    }

    [Fact]
    public async Task AddMonitorIdentity_RegistersAllThreeSchemes()
    {
        using var provider = BuildProvider(MakeConfiguration());
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemeProvider.GetSchemeAsync(AuthenticationSchemes.AppJwt));
        Assert.NotNull(await schemeProvider.GetSchemeAsync(AuthenticationSchemes.AzureAd));
        Assert.NotNull(await schemeProvider.GetSchemeAsync(AuthenticationSchemes.DualJwt));
    }

    [Fact]
    public async Task AddMonitorIdentity_DualJwt_IsTheDefaultAuthenticateChallengeAndForbidScheme()
    {
        using var provider = BuildProvider(MakeConfiguration());
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        var defaultAuthenticate = await schemeProvider.GetDefaultAuthenticateSchemeAsync();
        var defaultChallenge = await schemeProvider.GetDefaultChallengeSchemeAsync();
        var defaultForbid = await schemeProvider.GetDefaultForbidSchemeAsync();

        Assert.Equal(AuthenticationSchemes.DualJwt, defaultAuthenticate?.Name);
        Assert.Equal(AuthenticationSchemes.DualJwt, defaultChallenge?.Name);
        Assert.Equal(AuthenticationSchemes.DualJwt, defaultForbid?.Name);
    }

    [Fact]
    public void AddMonitorIdentity_AppJwtOptions_UseSymmetricKeyBuiltFromConfiguredSecret()
    {
        using var provider = BuildProvider(MakeConfiguration(jwtSecret: "correct-horse-battery-staple"));
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var options = monitor.Get(AuthenticationSchemes.AppJwt);

        var key = Assert.IsType<SymmetricSecurityKey>(options.TokenValidationParameters.IssuerSigningKey);
        Assert.Equal(Encoding.UTF8.GetBytes("correct-horse-battery-staple"), key.Key);
    }

    [Fact]
    public void AddMonitorIdentity_AppJwtOptions_MatchExpectedTokenValidationParameters()
    {
        using var provider = BuildProvider(MakeConfiguration());
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var options = monitor.Get(AuthenticationSchemes.AppJwt);
        var parameters = options.TokenValidationParameters;

        Assert.False(options.MapInboundClaims);
        Assert.False(parameters.ValidateIssuer);
        Assert.False(parameters.ValidateAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.ValidateIssuerSigningKey);
        Assert.Equal("role", parameters.RoleClaimType);
        Assert.Equal("sub", parameters.NameClaimType);
        Assert.Equal(TimeSpan.FromSeconds(30), parameters.ClockSkew);
    }

    [Fact]
    public void AddMonitorIdentity_AppJwtOptions_WiresAuthResponseWriterEvents()
    {
        using var provider = BuildProvider(MakeConfiguration());
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var options = monitor.Get(AuthenticationSchemes.AppJwt);

        Assert.Equal(new Func<JwtBearerChallengeContext, Task>(AuthResponseWriter.OnChallenge), options.Events!.OnChallenge);
        Assert.Equal(new Func<ForbiddenContext, Task>(AuthResponseWriter.OnForbidden), options.Events!.OnForbidden);
    }

    [Fact]
    public void AddMonitorIdentity_AzureAdOptions_AuthorityBuiltFromConfiguredTenantId()
    {
        using var provider = BuildProvider(MakeConfiguration(tenantId: "my-tenant-guid"));
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var options = monitor.Get(AuthenticationSchemes.AzureAd);

        Assert.Equal("https://login.microsoftonline.com/my-tenant-guid/v2.0", options.Authority);
    }

    [Fact]
    public void AddMonitorIdentity_AzureAdOptions_MatchExpectedTokenValidationParameters()
    {
        using var provider = BuildProvider(MakeConfiguration(tenantId: "tenant-1", clientId: "client-1"));
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var options = monitor.Get(AuthenticationSchemes.AzureAd);
        var parameters = options.TokenValidationParameters;

        Assert.False(options.MapInboundClaims);
        Assert.True(parameters.ValidateIssuer);
        Assert.Equal("https://login.microsoftonline.com/tenant-1/v2.0", parameters.ValidIssuer);
        Assert.True(parameters.ValidateAudience);
        Assert.Equal("client-1", parameters.ValidAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.Equal("role", parameters.RoleClaimType);
        Assert.Equal("sub", parameters.NameClaimType);
    }

    [Fact]
    public void AddMonitorIdentity_AzureAdOptions_WiresAuthResponseWriterEvents()
    {
        using var provider = BuildProvider(MakeConfiguration());
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var options = monitor.Get(AuthenticationSchemes.AzureAd);

        Assert.Equal(new Func<JwtBearerChallengeContext, Task>(AuthResponseWriter.OnChallenge), options.Events!.OnChallenge);
        Assert.Equal(new Func<ForbiddenContext, Task>(AuthResponseWriter.OnForbidden), options.Events!.OnForbidden);
    }

    [Fact]
    public void AddMonitorIdentity_MissingAzureAdSection_FallsBackToEmptyTenantAndClientId()
    {
        // configuration.GetSection(AzureAdOptions.SectionName).Get<AzureAdOptions>() ?? new AzureAdOptions()
        // — an absent section binds to null, and the null-coalescing fallback substitutes
        // an all-default (empty-string) AzureAdOptions rather than throwing at registration time.
        using var provider = BuildProvider(MakeConfiguration(tenantId: null, clientId: null));
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var options = monitor.Get(AuthenticationSchemes.AzureAd);

        Assert.Equal("https://login.microsoftonline.com//v2.0", options.Authority);
        Assert.Equal(string.Empty, options.TokenValidationParameters.ValidAudience);
    }

    [Fact]
    public void AddMonitorIdentity_MissingJwtSecret_DefersFailureToOptionsResolution_NotRegistration()
    {
        // AddJwtBearer's configure callback (which builds the SymmetricSecurityKey) runs
        // lazily via IOptionsFactory, not at AddMonitorIdentity() call time — so a missing
        // secret should not throw during registration...
        var services = new ServiceCollection();
        services.AddLogging();
        var exception = Record.Exception(() => services.AddMonitorIdentity(MakeConfiguration(jwtSecret: null)));
        Assert.Null(exception);

        // ...but SymmetricSecurityKey rejects a zero-length key, so resolving the AppJwt
        // options (which happens the moment a request needs to be authenticated against
        // it) surfaces the misconfiguration loudly instead of silently accepting an
        // empty signing key.
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        Assert.ThrowsAny<ArgumentException>(() => monitor.Get(AuthenticationSchemes.AppJwt));
    }

    [Fact]
    public async Task AddMonitorIdentity_RegistersAuthenticatedUserPolicy_DenyingAnonymous()
    {
        using var provider = BuildProvider(MakeConfiguration());
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await policyProvider.GetPolicyAsync(AuthorizationPolicyNames.AuthenticatedUser);

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Theory]
    [InlineData("RequireAdmin", "Admin")]
    [InlineData("RequireOperator", "Operator")]
    [InlineData("RequireViewer", "Viewer")]
    public async Task AddMonitorIdentity_RegistersPerRolePolicies_RequiringExactlyThatRole(string policyName, string expectedRole)
    {
        using var provider = BuildProvider(MakeConfiguration());
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await policyProvider.GetPolicyAsync(policyName);

        Assert.NotNull(policy);
        var rolesRequirement = Assert.Single(policy!.Requirements.OfType<RolesAuthorizationRequirement>());
        Assert.Equal(new[] { expectedRole }, rolesRequirement.AllowedRoles);
    }

    [Fact]
    public async Task AddMonitorIdentity_UnknownPolicyName_ResolvesToNull()
    {
        using var provider = BuildProvider(MakeConfiguration());
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await policyProvider.GetPolicyAsync("SomethingThatWasNeverRegistered");

        Assert.Null(policy);
    }
}
