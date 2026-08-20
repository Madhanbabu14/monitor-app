using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

public class RateLimitOptionsTests
{
    [Fact]
    public void Defaults_MatchSource()
    {
        var options = new RateLimitOptions();

        Assert.Equal(900_000, options.WindowMs);
        Assert.Equal(100, options.MaxRequests);
        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WindowMs_LessThanOne_FailsValidation(int windowMs)
    {
        var options = new RateLimitOptions { WindowMs = windowMs, MaxRequests = 100 };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RateLimitOptions.WindowMs)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxRequests_LessThanOne_FailsValidation(int maxRequests)
    {
        var options = new RateLimitOptions { WindowMs = 900_000, MaxRequests = maxRequests };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RateLimitOptions.MaxRequests)));
    }

    [Fact]
    public void MinimumBoundaryValues_AreValid()
    {
        var options = new RateLimitOptions { WindowMs = 1, MaxRequests = 1 };

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void SectionName_MatchesConfigurationSection()
    {
        Assert.Equal("RateLimit", RateLimitOptions.SectionName);
    }
}
