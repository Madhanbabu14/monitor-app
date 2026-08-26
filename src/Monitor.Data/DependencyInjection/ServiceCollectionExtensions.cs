using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;
using Monitor.Data.DataSources;
using Monitor.Data.HealthChecks;
using Monitor.Data.Repositories;
using Monitor.Data.Startup;

namespace Monitor.Data.DependencyInjection;

/// <summary>
/// Wires up the two named <see cref="Npgsql.NpgsqlDataSource"/>s and their
/// repository abstractions. Called once from Monitor.Api's Program.cs,
/// after <c>AddValidatedOptions&lt;DatabaseOptions&gt;</c> has registered the
/// (fail-fast-on-missing-config) options tree this depends on.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorData(this IServiceCollection services)
    {
        services.AddSingleton<PrimaryNpgsqlDataSource>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Primary;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new PrimaryNpgsqlDataSource(NpgsqlDataSourceFactory.CreatePrimary(options, loggerFactory));
        });
        services.AddSingleton<IPrimaryDb, PrimaryDb>();

        // Registered even when unconfigured: the factory delegate returns null in that case,
        // and OperationalDb's constructor accepts a null OperationalNpgsqlDataSource to mean
        // "operational source not configured" (IOperationalDb.IsConfigured => false).
        services.AddSingleton<OperationalNpgsqlDataSource>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Operational;
            if (options is null || string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                return null!;
            }

            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new OperationalNpgsqlDataSource(NpgsqlDataSourceFactory.CreateOperational(options, loggerFactory));
        });
        services.AddSingleton<IOperationalDb, OperationalDb>();

        // Fail-fast: abort host startup if the primary DB can't actually be reached,
        // mirroring the source calling testConnection() before app.listen().
        services.AddHostedService<PrimaryDatabaseStartupCheck>();

        // Degraded (never Unhealthy) signal for the optional operational replica —
        // see OperationalDatabaseHealthCheck. failureStatus is set defensively in
        // case a future caller filters by tag without reading the check's own
        // result; the check itself already returns Degraded, not Unhealthy, on
        // every failure path (including "not configured", which is Healthy).
        services.AddHealthChecks()
            .AddCheck<OperationalDatabaseHealthCheck>(
                "operational-database",
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "database", "operational" });

        return services;
    }
}
