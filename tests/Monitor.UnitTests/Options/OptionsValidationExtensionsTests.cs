using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

public class OptionsValidationExtensionsTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void AddValidatedOptions_BindsSectionValues_OntoStronglyTypedOptions()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Webhook:Secret"] = "abc123",
        });

        var services = new ServiceCollection();
        services.AddValidatedOptions<WebhookOptions>(config, WebhookOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<WebhookOptions>>().Value;

        Assert.Equal("abc123", options.Secret);
    }

    [Fact]
    public void AddValidatedOptions_MissingRequiredValue_ThrowsOptionsValidationException_OnResolve()
    {
        // No Webhook:Secret present at all -> binds to the class default (empty string),
        // which violates [Required(AllowEmptyStrings = false)].
        var config = BuildConfig(new Dictionary<string, string?>());

        var services = new ServiceCollection();
        services.AddValidatedOptions<WebhookOptions>(config, WebhookOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<WebhookOptions>>().Value);

        Assert.Contains("Secret", ex.Message);
    }

    [Fact]
    public void AddValidatedOptions_InvalidRangeValue_ThrowsOptionsValidationException()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["App:Environment"] = "Production",
            ["App:Port"] = "70000", // out of [Range(1,65535)]
        });

        var services = new ServiceCollection();
        services.AddValidatedOptions<AppOptions>(config, AppOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AppOptions>>().Value);
    }

    [Fact]
    public void AddValidatedOptions_ValidConfiguration_ResolvesWithoutThrowing()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["App:Environment"] = "Development",
            ["App:Port"] = "5000",
        });

        var services = new ServiceCollection();
        services.AddValidatedOptions<AppOptions>(config, AppOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AppOptions>>().Value;

        Assert.Equal("Development", options.Environment);
        Assert.Equal(5000, options.Port);
    }

    [Fact]
    public void AddValidatedOptions_MissingSectionEntirely_FallsBackToClassDefaults_AndStillValidates()
    {
        // AppOptions has valid class-level defaults (Environment="Development", Port=4000),
        // so binding an absent section should not throw.
        var config = BuildConfig(new Dictionary<string, string?>());

        var services = new ServiceCollection();
        services.AddValidatedOptions<AppOptions>(config, AppOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AppOptions>>().Value;

        Assert.Equal("Development", options.Environment);
        Assert.Equal(4000, options.Port);
    }

    [Fact]
    public void AddValidatedOptions_NestedDatabaseOptions_ValidatesPrimaryConnectionString()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Database:Primary:ConnectionString"] = "",
        });

        var services = new ServiceCollection();
        services.AddValidatedOptions<DatabaseOptions>(config, DatabaseOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);

        Assert.Contains("Primary.ConnectionString", ex.Message);
    }

    [Fact]
    public void AddValidatedOptions_NestedDatabaseOptions_ValidConnectionString_ResolvesFine()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Database:Primary:ConnectionString"] = "Host=localhost;Database=db",
        });

        var services = new ServiceCollection();
        services.AddValidatedOptions<DatabaseOptions>(config, DatabaseOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        Assert.Equal("Host=localhost;Database=db", options.Primary.ConnectionString);
    }
}
