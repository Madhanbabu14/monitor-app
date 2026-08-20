using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

/// <summary>
/// Binds and validates the actual appsettings.json / appsettings.Development.json files
/// shipped with Monitor.Api (linked into TestData/ via the csproj), the same way
/// Program.cs's builder.Configuration would layer them. This guards against the checked-in
/// JSON silently drifting out of sync with the Options classes' required members.
/// </summary>
public class AppSettingsBindingTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    private static IConfiguration BuildConfiguration(bool includeDevelopmentOverlay)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(TestDataPath("appsettings.json"), optional: false);

        if (includeDevelopmentOverlay)
        {
            builder.AddJsonFile(TestDataPath("appsettings.Development.json"), optional: false);
        }

        return builder.Build();
    }

    private static void RegisterAllOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<AppOptions>(configuration, AppOptions.SectionName);
        services.AddValidatedOptions<DatabaseOptions>(configuration, DatabaseOptions.SectionName);
        services.AddValidatedOptions<AzureAdOptions>(configuration, AzureAdOptions.SectionName);
        services.AddValidatedOptions<AwsOptions>(configuration, AwsOptions.SectionName);
        services.AddValidatedOptions<JwtOptions>(configuration, JwtOptions.SectionName);
        services.AddValidatedOptions<CorsOptions>(configuration, CorsOptions.SectionName);
        services.AddValidatedOptions<RateLimitOptions>(configuration, RateLimitOptions.SectionName);
        services.AddValidatedOptions<WebhookOptions>(configuration, WebhookOptions.SectionName);
    }

    [Fact]
    public void BaseAppSettingsJson_AlonePlaceholderValues_FailsRequiredOptionsValidation()
    {
        // appsettings.json (Production defaults) ships blank placeholders for every secret;
        // it must never validate cleanly on its own, or a real prod deploy without env-specific
        // overrides would silently start with empty connection strings/credentials.
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);
        var services = new ServiceCollection();
        RegisterAllOptions(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureAdOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AwsOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JwtOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<WebhookOptions>>().Value);
    }

    [Fact]
    public void BaseAppSettingsJson_Alone_NonSecretOptions_AreValid()
    {
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);
        var services = new ServiceCollection();
        RegisterAllOptions(services, configuration);
        using var provider = services.BuildServiceProvider();

        var app = provider.GetRequiredService<IOptions<AppOptions>>().Value;
        Assert.Equal("Production", app.Environment);
        Assert.Equal(4000, app.Port);

        var cors = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        Assert.Equal("http://localhost:3000", cors.Origin);

        var rateLimit = provider.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        Assert.Equal(900000, rateLimit.WindowMs);
        Assert.Equal(100, rateLimit.MaxRequests);
    }

    [Fact]
    public void DevelopmentOverlay_OnTopOfBase_MakesEverySectionValid()
    {
        // Mirrors ASP.NET Core's own configuration layering: appsettings.json then
        // appsettings.{Environment}.json overlaid on top.
        var configuration = BuildConfiguration(includeDevelopmentOverlay: true);
        var services = new ServiceCollection();
        RegisterAllOptions(services, configuration);
        using var provider = services.BuildServiceProvider();

        var app = provider.GetRequiredService<IOptions<AppOptions>>().Value;
        Assert.Equal("Development", app.Environment);

        var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        Assert.Equal(
            "Host=localhost;Port=5432;Database=pipeline_control_center;Username=pcc_user;Password=pcc_password",
            database.Primary.ConnectionString);
        Assert.Null(database.Operational);

        var azureAd = provider.GetRequiredService<IOptions<AzureAdOptions>>().Value;
        Assert.Equal("your-tenant-id", azureAd.TenantId);
        Assert.Equal("your-client-id", azureAd.ClientId);

        var aws = provider.GetRequiredService<IOptions<AwsOptions>>().Value;
        Assert.Equal("your-access-key", aws.AccessKeyId);
        Assert.Equal("your-bucket-name", aws.S3Bucket);

        var jwt = provider.GetRequiredService<IOptions<JwtOptions>>().Value;
        Assert.Equal("dev-super-secret-jwt-key-min-32-characters", jwt.Secret);

        var webhook = provider.GetRequiredService<IOptions<WebhookOptions>>().Value;
        Assert.Equal("dev-webhook-secret", webhook.Secret);

        // RateLimit isn't overridden in the Development overlay - base values still apply.
        var rateLimit = provider.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        Assert.Equal(900000, rateLimit.WindowMs);
        Assert.Equal(100, rateLimit.MaxRequests);
    }

    [Fact]
    public void AppSettingsJson_SerilogSection_IsPresentButNotBoundByAnyValidatedOptions()
    {
        // "Serilog": { "MinimumLevel": "Information" } / "AllowedHosts" exist only for
        // Microsoft.Extensions.Logging/host plumbing - not part of any Monitor.Core Options
        // class, so they should simply be ignored by our binder rather than erroring.
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);

        Assert.Equal("Information", configuration["Serilog:MinimumLevel"]);
        Assert.Equal("*", configuration["AllowedHosts"]);
    }
}
