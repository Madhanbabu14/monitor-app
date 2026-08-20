using Monitor.Core.Options;

namespace Monitor.UnitTests.Options;

public class DatabaseOptionsTests
{
    private static DatabaseOptions ValidPrimaryOnly() => new()
    {
        Primary = new PrimaryDatabaseOptions
        {
            ConnectionString = "Host=localhost;Database=db",
        },
    };

    [Fact]
    public void ValidPrimary_NoOperational_PassesValidation()
    {
        var options = ValidPrimaryOnly();

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void PrimaryDefaults_MatchSource()
    {
        var primary = new PrimaryDatabaseOptions();

        Assert.Equal(20, primary.MaxConnections);
        Assert.Equal(30000, primary.IdleTimeoutMs);
        Assert.Equal(5000, primary.ConnectionTimeoutMs);
        Assert.Equal("VerifyFull", primary.SslMode);
    }

    [Fact]
    public void MissingPrimaryConnectionString_FailsValidation_WithPrefixedMemberName()
    {
        var options = new DatabaseOptions
        {
            Primary = new PrimaryDatabaseOptions { ConnectionString = "" },
        };

        var results = ValidationTestHelper.Validate(options);

        // The nested attribute validation is re-run explicitly by DatabaseOptions.Validate()
        // and is expected to prefix the member name with "Primary." since .NET's
        // ValidateDataAnnotations() does not recurse into nested option classes on its own.
        Assert.Contains(results, r => r.MemberNames.Contains("Primary.ConnectionString"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PrimaryMaxConnectionsOutOfRange_FailsValidation_WithPrefixedMemberName(int maxConnections)
    {
        var options = new DatabaseOptions
        {
            Primary = new PrimaryDatabaseOptions
            {
                ConnectionString = "Host=localhost",
                MaxConnections = maxConnections,
            },
        };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains("Primary.MaxConnections"));
    }

    [Fact]
    public void OperationalNull_DoesNotContributeValidationErrors()
    {
        var options = ValidPrimaryOnly();
        options.Operational = null;

        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void OperationalPresent_WithNoConnectionString_StillPasses_BecauseItsOptional()
    {
        var options = ValidPrimaryOnly();
        options.Operational = new OperationalDatabaseOptions
        {
            ConnectionString = null,
        };

        // OperationalDatabaseOptions has no [Required] attributes at all -
        // it is a first-class-but-optional section per the class remarks.
        Assert.True(ValidationTestHelper.IsValid(options));
    }

    [Fact]
    public void OperationalDefaults_SslModeMatchesPrimaryDefault()
    {
        var operational = new OperationalDatabaseOptions();

        Assert.Equal("VerifyFull", operational.SslMode);
        Assert.Null(operational.ConnectionString);
        Assert.Null(operational.SslCaCertificatePath);
    }

    [Fact]
    public void BothPrimaryAndOperational_Invalid_ReportsBothAsErrors_WhenOperationalHasNestedAttributes()
    {
        // Regression-guard: even though OperationalDatabaseOptions currently has no
        // [Required] members, the Validate() method must still walk into it whenever
        // it is non-null so that a future attribute added there is enforced too.
        var options = new DatabaseOptions
        {
            Primary = new PrimaryDatabaseOptions { ConnectionString = "" },
            Operational = new OperationalDatabaseOptions(),
        };

        var results = ValidationTestHelper.Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains("Primary.ConnectionString"));
    }

    [Fact]
    public void SectionName_MatchesConfigurationSection()
    {
        Assert.Equal("Database", DatabaseOptions.SectionName);
    }
}
