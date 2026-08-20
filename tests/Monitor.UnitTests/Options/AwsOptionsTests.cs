using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

public class AwsOptionsTests
{
    private static AwsOptions Valid() => new()
    {
        AccessKeyId = "AKIA...",
        SecretAccessKey = "secret",
        S3Bucket = "my-bucket",
    };

    [Fact]
    public void Valid_PassesValidation()
    {
        Assert.True(ValidationTestHelper.IsValid(Valid()));
    }

    [Fact]
    public void Defaults_RegionAndPrefix_MatchSource()
    {
        var options = new AwsOptions();

        Assert.Equal("us-east-1", options.Region);
        Assert.Equal("data/", options.S3Prefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AccessKeyId_MissingOrEmpty_FailsValidation(string? value)
    {
        var options = Valid();
        options.AccessKeyId = value!;

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AwsOptions.AccessKeyId)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void SecretAccessKey_MissingOrEmpty_FailsValidation(string? value)
    {
        var options = Valid();
        options.SecretAccessKey = value!;

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AwsOptions.SecretAccessKey)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void S3Bucket_MissingOrEmpty_FailsValidation(string? value)
    {
        var options = Valid();
        options.S3Bucket = value!;

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AwsOptions.S3Bucket)));
    }

    [Fact]
    public void Region_And_S3Prefix_HaveNoRequiredAttribute_EmptyStillValid()
    {
        var options = Valid();
        options.Region = string.Empty;
        options.S3Prefix = string.Empty;

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void SectionName_MatchesConfigurationSection()
    {
        Assert.Equal("Aws", AwsOptions.SectionName);
    }
}
