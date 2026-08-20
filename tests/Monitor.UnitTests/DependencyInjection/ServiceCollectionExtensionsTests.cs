using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;
using Monitor.Data.DataSources;
using Monitor.Data.DependencyInjection;
using Monitor.Data.Repositories;
using Monitor.Data.Startup;
using Npgsql;

namespace Monitor.UnitTests.DependencyInjection;

/// <summary>
/// Exercises <see cref="ServiceCollectionExtensions.AddMonitorData"/> purely through
/// registration/resolution — no live Postgres is touched because <see cref="NpgsqlDataSource"/>
/// connects lazily (mirrored by <c>NpgsqlDataSourceFactoryTests</c>). These tests pin down the
/// wiring contract Program.cs relies on: which concrete types back the interfaces, and — the
/// most behaviourally significant branch in this file — that the operational data source is
/// registered as `null` (not omitted, not throwing) when unconfigured, so
/// <see cref="OperationalDb.IsConfigured"/> can observe that as "false" via ordinary
/// constructor injection rather than the caller having to special-case a missing registration.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider(DatabaseOptions databaseOptions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        // PrimaryDb/OperationalDb each take an ILogger<T>; NullLoggerFactory alone doesn't
        // satisfy that via constructor injection, so register the open-generic the same way
        // Microsoft.Extensions.Logging's AddLogging() would.
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptions<DatabaseOptions>>(Microsoft.Extensions.Options.Options.Create(databaseOptions));

        services.AddMonitorData();

        return services.BuildServiceProvider();
    }

    private static DatabaseOptions MakeOptions(string? operationalConnectionString = null) => new()
    {
        Primary = new PrimaryDatabaseOptions
        {
            ConnectionString = "Host=primary-host;Database=primary_db;Username=u;Password=p",
        },
        Operational = operationalConnectionString is null
            ? null
            : new OperationalDatabaseOptions { ConnectionString = operationalConnectionString },
    };

    [Fact]
    public void AddMonitorData_RegistersPrimaryDataSource_BuiltFromDatabaseOptionsPrimary()
    {
        using var provider = BuildProvider(MakeOptions());

        var primary = provider.GetRequiredService<PrimaryNpgsqlDataSource>();

        var builder = new NpgsqlConnectionStringBuilder(primary.DataSource.ConnectionString);
        Assert.Equal("primary-host", builder.Host);
        Assert.Equal("primary_db", builder.Database);
    }

    [Fact]
    public void AddMonitorData_RegistersPrimaryDataSource_AsSingleton()
    {
        using var provider = BuildProvider(MakeOptions());

        var first = provider.GetRequiredService<PrimaryNpgsqlDataSource>();
        var second = provider.GetRequiredService<PrimaryNpgsqlDataSource>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddMonitorData_RegistersIPrimaryDb_BackedByPrimaryDb()
    {
        using var provider = BuildProvider(MakeOptions());

        var primaryDb = provider.GetRequiredService<IPrimaryDb>();

        Assert.IsType<PrimaryDb>(primaryDb);
    }

    [Fact]
    public void AddMonitorData_WhenOperationalNotConfigured_RegistersNullOperationalDataSource()
    {
        using var provider = BuildProvider(MakeOptions(operationalConnectionString: null));

        // GetService (not GetRequiredService): the factory delegate intentionally returns
        // `null!`, and .NET's DI container surfaces that as an ordinary null rather than
        // throwing, exactly like it does for the OperationalNpgsqlDataSource? constructor
        // parameter on OperationalDb below.
        var operationalDataSource = provider.GetService<OperationalNpgsqlDataSource>();

        Assert.Null(operationalDataSource);
    }

    [Fact]
    public void AddMonitorData_WhenOperationalNotConfigured_IOperationalDbReportsNotConfigured()
    {
        using var provider = BuildProvider(MakeOptions(operationalConnectionString: null));

        var operationalDb = provider.GetRequiredService<IOperationalDb>();

        Assert.IsType<OperationalDb>(operationalDb);
        Assert.False(operationalDb.IsConfigured);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddMonitorData_TreatsMissingOrBlankOperationalConnectionString_AsNotConfigured(string? connectionString)
    {
        var options = MakeOptions();
        options.Operational = new OperationalDatabaseOptions { ConnectionString = connectionString };
        using var provider = BuildProvider(options);

        Assert.Null(provider.GetService<OperationalNpgsqlDataSource>());
        Assert.False(provider.GetRequiredService<IOperationalDb>().IsConfigured);
    }

    [Fact]
    public void AddMonitorData_WhenOperationalConfigured_BuildsOperationalDataSource_AndMarksConfigured()
    {
        using var provider = BuildProvider(MakeOptions(operationalConnectionString: "Host=replica-host;Database=replica_db"));

        var operationalDataSource = provider.GetRequiredService<OperationalNpgsqlDataSource>();
        var builder = new NpgsqlConnectionStringBuilder(operationalDataSource.DataSource.ConnectionString);
        Assert.Equal("replica-host", builder.Host);

        var operationalDb = provider.GetRequiredService<IOperationalDb>();
        Assert.True(operationalDb.IsConfigured);
    }

    [Fact]
    public void AddMonitorData_RegistersPrimaryDatabaseStartupCheck_AsAHostedService()
    {
        using var provider = BuildProvider(MakeOptions());

        var hostedServices = provider.GetServices<IHostedService>();

        Assert.Contains(hostedServices, s => s is PrimaryDatabaseStartupCheck);
    }

    [Fact]
    public void AddMonitorData_ReturnsTheSameServiceCollection_ForFluentChaining()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IOptions<DatabaseOptions>>(Microsoft.Extensions.Options.Options.Create(MakeOptions()));

        var returned = services.AddMonitorData();

        Assert.Same(services, returned);
    }
}
