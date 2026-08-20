using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

public class AppOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var options = new AppOptions();

        Assert.Equal("Development", options.Environment);
        Assert.Equal(4000, options.Port);
        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Environment_MissingOrEmpty_FailsValidation(string? environment)
    {
        var options = new AppOptions { Environment = environment!, Port = 4000 };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AppOptions.Environment)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void Port_OutOfRange_FailsValidation(int port)
    {
        var options = new AppOptions { Environment = "Production", Port = port };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AppOptions.Port)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4000)]
    [InlineData(65535)]
    public void Port_InRange_PassesValidation(int port)
    {
        var options = new AppOptions { Environment = "Production", Port = port };

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void SectionName_MatchesConfigurationSection()
    {
        Assert.Equal("App", AppOptions.SectionName);
    }
}
