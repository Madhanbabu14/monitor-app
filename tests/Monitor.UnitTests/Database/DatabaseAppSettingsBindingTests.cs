using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Monitor.Core.Options;

namespace Monitor.UnitTests.Database;

/// <summary>
/// Monitor.Database's Program.cs builds its own <see cref="ConfigurationBuilder"/>
/// (appsettings.json -&gt; appsettings.{DOTNET_ENVIRONMENT}.json -&gt; environment variables -&gt;
/// command-line args) and binds/validates <see cref="DatabaseOptions"/> from the SAME
/// "Database" section shape Monitor.Api uses, then fails fast (Console.Error + non-zero
/// exit) if Primary.ConnectionString is still blank. These tests exercise that binding +
/// validation pipeline directly (without spawning a process) against the actual shipped
/// appsettings.json / appsettings.Development.json for Monitor.Database.
/// </summary>
public class DatabaseAppSettingsBindingTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Database", fileName);

    private static DatabaseOptions Bind(IConfiguration configuration)
    {
        var options = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(options);
        return options;
    }

    private static IList<ValidationResult> ValidateLikeProgram(DatabaseOptions options)
    {
        // Mirrors Program.cs's own validation exactly: DatabaseOptions.Validate() plus the
        // explicit extra check for a blank Primary.ConnectionString.
        var results = options.Validate(new ValidationContext(options)).ToList();

        if (string.IsNullOrWhiteSpace(options.Primary.ConnectionString))
        {
            results.Add(new ValidationResult(
                $"{DatabaseOptions.SectionName}:Primary:ConnectionString is required to run migrations.",
                new[] { "Primary.ConnectionString" }));
        }

        return results;
    }

    [Fact]
    public void BaseAppSettingsJson_BindsExpectedDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
            .Build();

        var options = Bind(configuration);

        Assert.Equal(string.Empty, options.Primary.ConnectionString);
        Assert.Equal(20, options.Primary.MaxConnections);
        Assert.Equal(30000, options.Primary.IdleTimeoutMs);
        Assert.Equal(5000, options.Primary.ConnectionTimeoutMs);
        Assert.Equal("VerifyFull", options.Primary.SslMode);
        Assert.Null(options.Operational);
    }

    [Fact]
    public void BaseAppSettingsJson_Alone_FailsFastValidation_BlankConnectionString()
    {
        // This is exactly the shape a fresh/misconfigured deploy would hit: Program.cs must
        // refuse to run migrations rather than attempt EnsureDatabase() against a blank string.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
            .Build();

        var options = Bind(configuration);
        var results = ValidateLikeProgram(options);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("Primary.ConnectionString"));
    }

    [Fact]
    public void DevelopmentOverlay_OnTopOfBase_ProducesNonBlankConnectionString_AndPassesValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
            .AddJsonFile(TestDataPath("appsettings.Development.json"), optional: false)
            .Build();

        var options = Bind(configuration);

        Assert.Equal(
            "Host=localhost;Port=5432;Database=pipeline_control_center;Username=pcc_user;Password=pcc_password",
            options.Primary.ConnectionString);

        var results = ValidateLikeProgram(options);
        Assert.Empty(results);
    }

    [Fact]
    public void DevelopmentOverlay_OnlyOverridesConnectionString_OtherPrimaryValuesKeepBaseDefaults()
    {
        // appsettings.Development.json only sets Database:Primary:ConnectionString - .NET's
        // JSON configuration provider merges by key, so MaxConnections/SslMode/etc. must still
        // come from the base appsettings.json rather than being reset/blanked out.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
            .AddJsonFile(TestDataPath("appsettings.Development.json"), optional: false)
            .Build();

        var options = Bind(configuration);

        Assert.Equal(20, options.Primary.MaxConnections);
        Assert.Equal(30000, options.Primary.IdleTimeoutMs);
        Assert.Equal(5000, options.Primary.ConnectionTimeoutMs);
        Assert.Equal("VerifyFull", options.Primary.SslMode);
    }

    /// <summary>
    /// Sets a real process environment variable for the duration of <paramref name="action"/>,
    /// restoring the prior value afterwards. Deliberately uses actual environment variables
    /// (not <c>AddInMemoryCollection</c>) because the "__" -&gt; ":" delimiter translation these
    /// tests rely on is specific behaviour of <see cref="ConfigurationBuilder.AddEnvironmentVariables()"/> -
    /// an in-memory collection would take the key literally and silently not exercise it.
    /// </summary>
    private static void WithEnvironmentVariable(string name, string value, Action action) =>
        WithEnvironmentVariables(new Dictionary<string, string> { [name] = value }, action);

    private static void WithEnvironmentVariables(IDictionary<string, string> variables, Action action)
    {
        var previous = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in variables)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            action();
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    [Fact]
    public void EnvironmentVariable_WithDoubleUnderscoreSeparator_OverridesConnectionString()
    {
        WithEnvironmentVariable("Database__Primary__ConnectionString", "Host=env-host;Database=env-db", () =>
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
                .AddEnvironmentVariables()
                .Build();

            var options = Bind(configuration);

            Assert.Equal("Host=env-host;Database=env-db", options.Primary.ConnectionString);
            Assert.Empty(ValidateLikeProgram(options));
        });
    }

    [Fact]
    public void CommandLineArgument_OverridesConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
            .AddCommandLine(new[] { "--Database:Primary:ConnectionString=Host=cli-host;Database=cli-db" })
            .Build();

        var options = Bind(configuration);

        Assert.Equal("Host=cli-host;Database=cli-db", options.Primary.ConnectionString);
    }

    [Fact]
    public void PrecedenceOrder_CommandLine_BeatsEnvironmentVariable_BeatsJson()
    {
        // Mirrors Program.cs's own builder order: json -> json(env-specific) -> env vars ->
        // command line, later sources winning on key conflicts.
        WithEnvironmentVariable("Database__Primary__ConnectionString", "Host=env-host;Database=env-db", () =>
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
                .AddJsonFile(TestDataPath("appsettings.Development.json"), optional: false)
                .AddEnvironmentVariables()
                .AddCommandLine(new[] { "--Database:Primary:ConnectionString=Host=cli-host;Database=cli-db" })
                .Build();

            var options = Bind(configuration);

            Assert.Equal("Host=cli-host;Database=cli-db", options.Primary.ConnectionString);
        });
    }

    [Fact]
    public void PrecedenceOrder_EnvironmentVariable_BeatsDevelopmentJsonOverlay_WhenCommandLineAbsent()
    {
        WithEnvironmentVariable("Database__Primary__ConnectionString", "Host=env-host;Database=env-db", () =>
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
                .AddJsonFile(TestDataPath("appsettings.Development.json"), optional: false)
                .AddEnvironmentVariables()
                .Build();

            var options = Bind(configuration);

            Assert.Equal("Host=env-host;Database=env-db", options.Primary.ConnectionString);
        });
    }

    [Fact]
    public void EnvironmentVariable_OverridingMaxConnectionsToZero_FailsValidation()
    {
        WithEnvironmentVariables(
            new Dictionary<string, string>
            {
                ["Database__Primary__ConnectionString"] = "Host=localhost;Database=db",
                ["Database__Primary__MaxConnections"] = "0",
            },
            () =>
            {
                var configuration = new ConfigurationBuilder()
                    .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
                    .AddEnvironmentVariables()
                    .Build();

                var options = Bind(configuration);
                var results = ValidateLikeProgram(options);

                Assert.Contains(results, r => r.MemberNames.Contains("Primary.MaxConnections"));
                Assert.DoesNotContain(results, r => r.MemberNames.Contains("Primary.ConnectionString"));
            });
    }

    [Fact]
    public void WhitespaceOnlyConnectionString_StillFailsFastValidation()
    {
        // The extra check in Program.cs uses IsNullOrWhiteSpace (not just IsNullOrEmpty), so
        // a config value of pure whitespace must not slip through as "configured".
        WithEnvironmentVariable("Database__Primary__ConnectionString", "   ", () =>
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(TestDataPath("appsettings.json"), optional: false)
                .AddEnvironmentVariables()
                .Build();

            var options = Bind(configuration);
            var results = ValidateLikeProgram(options);

            Assert.Contains(results, r => r.MemberNames.Contains("Primary.ConnectionString"));
        });
    }

    [Fact]
    public void SectionName_UsedByDatabaseOptions_IsDatabase()
    {
        // Program.cs calls configuration.GetSection(DatabaseOptions.SectionName) - if this
        // constant ever drifted from "Database" it would silently bind an empty options
        // object against the real "Database" JSON section.
        Assert.Equal("Database", DatabaseOptions.SectionName);
    }
}
