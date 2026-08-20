using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

public class AzureAdOptionsTests
{
    [Fact]
    public void Valid_PassesValidation()
    {
        var options = new AzureAdOptions { TenantId = "tenant", ClientId = "client" };

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TenantId_MissingOrEmpty_FailsValidation(string? value)
    {
        var options = new AzureAdOptions { TenantId = value!, ClientId = "client" };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AzureAdOptions.TenantId)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ClientId_MissingOrEmpty_FailsValidation(string? value)
    {
        var options = new AzureAdOptions { TenantId = "tenant", ClientId = value! };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AzureAdOptions.ClientId)));
    }

    [Fact]
    public void BothMissing_ReportsBothMembers()
    {
        var options = new AzureAdOptions { TenantId = "", ClientId = "" };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AzureAdOptions.TenantId)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AzureAdOptions.ClientId)));
    }

    [Fact]
    public void SectionName_MatchesConfigurationSection()
    {
        Assert.Equal("AzureAd", AzureAdOptions.SectionName);
    }
}
