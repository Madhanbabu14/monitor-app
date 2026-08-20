using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

public class WebhookOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Secret_MissingOrEmpty_FailsValidation(string? value)
    {
        var options = new WebhookOptions { Secret = value! };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(WebhookOptions.Secret)));
    }

    [Fact]
    public void Valid_PassesValidation()
    {
        var options = new WebhookOptions { Secret = "wh-secret" };

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void SectionName_MatchesConfigurationSection()
    {
        Assert.Equal("Webhook", WebhookOptions.SectionName);
    }
}
