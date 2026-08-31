using Monitor.Identity.Users;

namespace Monitor.UnitTests.Users;

/// <summary>
/// Exercises <see cref="JwtExpiry.Parse"/>, the port of the `ms`-style shorthand
/// duration string `jsonwebtoken`'s `expiresIn` option accepts (config.jwt.expiresIn,
/// e.g. "8h") used to compute the app-JWT's expiry in <see cref="AppJwtIssuer"/>.
/// </summary>
public class JwtExpiryTests
{
    [Fact]
    public void Parse_Null_ReturnsDefaultEightHours()
    {
        Assert.Equal(TimeSpan.FromHours(8), JwtExpiry.Parse(null));
    }

    [Fact]
    public void Parse_EmptyString_ReturnsDefault()
    {
        Assert.Equal(JwtExpiry.Default, JwtExpiry.Parse(string.Empty));
    }

    [Fact]
    public void Parse_Whitespace_ReturnsDefault()
    {
        Assert.Equal(JwtExpiry.Default, JwtExpiry.Parse("   "));
    }

    [Theory]
    [InlineData("10s", 10)]
    [InlineData("0s", 0)]
    public void Parse_SecondsSuffix_ReturnsExpectedTimeSpan(string input, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), JwtExpiry.Parse(input));
    }

    [Fact]
    public void Parse_MinutesSuffix_ReturnsExpectedTimeSpan()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), JwtExpiry.Parse("5m"));
    }

    [Fact]
    public void Parse_HoursSuffix_ReturnsExpectedTimeSpan()
    {
        Assert.Equal(TimeSpan.FromHours(8), JwtExpiry.Parse("8h"));
    }

    [Fact]
    public void Parse_DaysSuffix_ReturnsExpectedTimeSpan()
    {
        Assert.Equal(TimeSpan.FromDays(3), JwtExpiry.Parse("3d"));
    }

    [Fact]
    public void Parse_WeeksSuffix_ReturnsSevenTimesDays()
    {
        Assert.Equal(TimeSpan.FromDays(7), JwtExpiry.Parse("1w"));
        Assert.Equal(TimeSpan.FromDays(14), JwtExpiry.Parse("2w"));
    }

    [Fact]
    public void Parse_UppercaseSuffix_IsCaseInsensitive()
    {
        Assert.Equal(TimeSpan.FromHours(2), JwtExpiry.Parse("2H"));
    }

    [Fact]
    public void Parse_DecimalValueWithSuffix_ParsesFractionalAmount()
    {
        Assert.Equal(TimeSpan.FromHours(1.5), JwtExpiry.Parse("1.5h"));
    }

    [Fact]
    public void Parse_BareInteger_IsInterpretedAsSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), JwtExpiry.Parse("45"));
    }

    [Fact]
    public void Parse_LeadingAndTrailingWhitespace_IsTrimmedBeforeParsing()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), JwtExpiry.Parse("  5m  "));
        Assert.Equal(TimeSpan.FromSeconds(45), JwtExpiry.Parse("  45  "));
    }

    [Fact]
    public void Parse_UnknownSuffix_FallsBackToDefault()
    {
        Assert.Equal(JwtExpiry.Default, JwtExpiry.Parse("10x"));
    }

    [Fact]
    public void Parse_NonNumericBeforeSuffix_FallsBackToDefault()
    {
        Assert.Equal(JwtExpiry.Default, JwtExpiry.Parse("abch"));
    }

    [Fact]
    public void Parse_SingleCharacterGarbage_FallsBackToDefault()
    {
        // numberPart is empty once the trailing unit char is stripped.
        Assert.Equal(JwtExpiry.Default, JwtExpiry.Parse("h"));
    }
}
