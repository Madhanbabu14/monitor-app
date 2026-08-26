using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;
using Monitor.Data.DependencyInjection;
using Monitor.Data.HealthChecks;

namespace Monitor.UnitTests.DependencyInjection;

/// <summary>
/// Covers the health-check registration half of <see cref="ServiceCollectionExtensions.AddMonitorData"/>
/// that <c>ServiceCollectionExtensionsTests</c> doesn't touch: that
/// <see cref="OperationalDatabaseHealthCheck"/> is wired up under the name
/// "operational-database", tagged "database"/"operational", with a defensive
/// <see cref="HealthStatus.Degraded"/> failure status — and that running the
/// registered check end-to-end (via <see cref="HealthCheckService"/>) reflects
/// the optional-operational-DB contract without needing a live Postgres.
/// </summary>
public class ServiceCollectionExtensionsHealthCheckTests
{
    private static ServiceProvider BuildProvider(DatabaseOptions databaseOptions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
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
    public void AddMonitorData_RegistersOperationalDatabaseHealthCheck_WithExpectedNameTagsAndFailureStatus()
    {
        using var provider = BuildProvider(MakeOptions());

        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        var registration = Assert.Single(registrations, r => r.Name == "operational-database");

        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
        Assert.Contains("database", registration.Tags);
        Assert.Contains("operational", registration.Tags);
        Assert.IsType<OperationalDatabaseHealthCheck>(registration.Factory(provider));
    }

    [Fact]
    public void AddMonitorData_RegistersExactlyOneHealthCheck()
    {
        using var provider = BuildProvider(MakeOptions());

        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        Assert.Single(registrations);
    }

    [Fact]
    public async Task AddMonitorData_HealthCheckService_ReportsHealthy_WhenOperationalNotConfigured()
    {
        using var provider = BuildProvider(MakeOptions(operationalConnectionString: null));
        var healthCheckService = provider.GetRequiredService<HealthCheckService>();

        var report = await healthCheckService.CheckHealthAsync();

        var entry = Assert.Single(report.Entries, kvp => kvp.Key == "operational-database").Value;
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    [Fact]
    public void AddMonitorData_HealthCheckRegistration_CanResolveIOperationalDbFromContainer()
    {
        // The health check's constructor depends on IOperationalDb; make sure the
        // registration's factory can actually resolve a fully-built instance from
        // the same container AddMonitorData wired up (not a fresh throwaway one).
        using var provider = BuildProvider(MakeOptions(operationalConnectionString: "Host=replica;Database=replica_db"));
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        var registration = Assert.Single(registrations, r => r.Name == "operational-database");

        var instance = registration.Factory(provider);

        Assert.NotNull(instance);
        Assert.IsType<OperationalDatabaseHealthCheck>(instance);
    }
}
