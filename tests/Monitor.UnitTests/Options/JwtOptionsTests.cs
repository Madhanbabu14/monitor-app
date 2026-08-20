using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

public class JwtOptionsTests
{
    [Fact]
    public void Default_ExpiresIn_MatchesSource()
    {
        var options = new JwtOptions { Secret = "s" };

        Assert.Equal("8h", options.ExpiresIn);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Secret_MissingOrEmpty_FailsValidation(string? value)
    {
        var options = new JwtOptions { Secret = value! };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(JwtOptions.Secret)));
    }

    [Fact]
    public void Valid_PassesValidation()
    {
        var options = new JwtOptions { Secret = "super-secret", ExpiresIn = "1h" };

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void ExpiresIn_HasNoRequiredAttribute_EmptyStillValid()
    {
        var options = new JwtOptions { Secret = "super-secret", ExpiresIn = string.Empty };

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void SectionName_MatchesConfigurationSection()
    {
        Assert.Equal("Jwt", JwtOptions.SectionName);
    }
}
