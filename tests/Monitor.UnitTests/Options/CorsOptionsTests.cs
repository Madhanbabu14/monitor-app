using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

public class CorsOptionsTests
{
    [Fact]
    public void Default_MatchesSourceHardDefault()
    {
        var options = new CorsOptions();

        Assert.Equal("http://localhost:3000", options.Origin);
    }

    [Fact]
    public void NoRequiredAttribute_EmptyOriginStillPassesValidation()
    {
        var options = new CorsOptions { Origin = string.Empty };

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void CustomOrigin_IsAssignable()
    {
        var options = new CorsOptions { Origin = "https://app.example.com" };

        Assert.Equal("https://app.example.com", options.Origin);
    }

    [Fact]
    public void SectionName_MatchesConfigurationSection()
    {
        Assert.Equal("Cors", CorsOptions.SectionName);
    }
}
