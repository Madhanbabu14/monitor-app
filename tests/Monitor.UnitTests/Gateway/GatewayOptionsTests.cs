using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;
using Monitor.Gateway.Options;

namespace Monitor.UnitTests.Gateway;

/// <summary>
/// <see cref="GatewayOptions"/> is the one process-level option Monitor.Gateway itself
/// owns (everything else lives in YARP's own "ReverseProxy" config tree, bound directly
/// by <c>AddReverseProxy().LoadFromConfig</c> rather than through this class). These tests
/// pin down its default, its [Range(1,65535)] boundaries, and that it goes through the
/// same <see cref="OptionsValidationExtensions.AddValidatedOptions{TOptions}"/> fail-fast
/// pipeline as every other bounded context's options.
/// </summary>
public class GatewayOptionsTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Port_DefaultsTo8080_WhenNotConfigured()
    {
        var options = new GatewayOptions();

        Assert.Equal(8080, options.Port);
    }

    [Fact]
    public void SectionName_IsGateway()
    {
        Assert.Equal("Gateway", GatewayOptions.SectionName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void Validate_WithinRange_ProducesNoValidationErrors(int port)
    {
        var options = new GatewayOptions { Port = port };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void Validate_OutOfRange_ProducesValidationError(int port)
    {
        var options = new GatewayOptions { Port = port };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(GatewayOptions.Port)));
    }

    [Fact]
    public void AddValidatedOptions_BindsPort_FromGatewaySection()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:Port"] = "9090",
        });

        var services = new ServiceCollection();
        services.AddValidatedOptions<GatewayOptions>(config, GatewayOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GatewayOptions>>().Value;

        Assert.Equal(9090, options.Port);
    }

    [Fact]
    public void AddValidatedOptions_MissingSection_FallsBackToClassDefault()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var services = new ServiceCollection();
        services.AddValidatedOptions<GatewayOptions>(config, GatewayOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GatewayOptions>>().Value;

        Assert.Equal(8080, options.Port);
    }

    [Fact]
    public void AddValidatedOptions_OutOfRangePort_ThrowsOptionsValidationException_OnResolve()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:Port"] = "99999",
        });

        var services = new ServiceCollection();
        services.AddValidatedOptions<GatewayOptions>(config, GatewayOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<GatewayOptions>>().Value);

        Assert.Contains("Port", ex.Message);
    }

    [Fact]
    public void AddValidatedOptions_NonNumericPort_ThrowsOnResolve()
    {
        // Node's config had no equivalent to this: a non-numeric PORT would have coerced to
        // NaN and slipped past `required(...)`. The .NET binder fails to convert the string
        // at all here, which is strictly stricter/safer than the source's behavior.
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:Port"] = "not-a-number",
        });

        var services = new ServiceCollection();
        services.AddValidatedOptions<GatewayOptions>(config, GatewayOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        Assert.ThrowsAny<Exception>(
            () => provider.GetRequiredService<IOptions<GatewayOptions>>().Value);
    }
}
