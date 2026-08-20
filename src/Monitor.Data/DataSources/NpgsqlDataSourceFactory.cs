using Microsoft.Extensions.Logging;
using Monitor.Core.Options;
using Npgsql;

namespace Monitor.Data.DataSources;

/// <summary>
/// Builds the two named <see cref="NpgsqlDataSource"/>s (Primary, Operational)
/// that replace the source's single lazily-created `pg.Pool`
/// (infrastructure/database/connection.ts's <c>getPool()</c>).
/// </summary>
/// <remarks>
/// Unlike the source — which chose TLS at runtime via
/// <c>config.env === 'production' || process.env.DB_SSL === 'true' ? { rejectUnauthorized: false } : false</c> —
/// SSL Mode is always driven by <see cref="PrimaryDatabaseOptions.SslMode"/> /
/// <see cref="OperationalDatabaseOptions.SslMode"/>, which default to and are
/// locked to <c>VerifyFull</c>. The insecure <c>rejectUnauthorized:false</c>
/// escape hatch does not survive the migration; an optional CA bundle
/// (<c>SslCaCertificatePath</c>) is honoured via Npgsql's own
/// <c>Root Certificate</c> connection string keyword so VerifyFull can
/// validate against a private CA without relying on the OS trust store.
/// </remarks>
public static class NpgsqlDataSourceFactory
{
    public static NpgsqlDataSource CreatePrimary(PrimaryDatabaseOptions options, ILoggerFactory? loggerFactory = null) =>
        Create(
            options.ConnectionString,
            options.SslMode,
            options.SslCaCertificatePath,
            maxPoolSize: options.MaxConnections,
            idleLifetimeMs: options.IdleTimeoutMs,
            timeoutMs: options.ConnectionTimeoutMs,
            loggerFactory);

    public static NpgsqlDataSource CreateOperational(OperationalDatabaseOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "CreateOperational requires a non-empty ConnectionString; callers must check " +
                "configuration before invoking this (the operational source is optional).");
        }

        return Create(options.ConnectionString, options.SslMode, options.SslCaCertificatePath, loggerFactory: loggerFactory);
    }

    private static NpgsqlDataSource Create(
        string connectionString,
        string sslMode,
        string? sslCaCertificatePath,
        int? maxPoolSize = null,
        int? idleLifetimeMs = null,
        int? timeoutMs = null,
        ILoggerFactory? loggerFactory = null)
    {
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SslMode = ParseSslMode(sslMode),
        };

        if (maxPoolSize is > 0)
        {
            connectionStringBuilder.MaxPoolSize = maxPoolSize.Value;
        }

        if (idleLifetimeMs is >= 0)
        {
            // pg Pool's idleTimeoutMillis <-> Npgsql's "Connection Idle Lifetime" (seconds).
            connectionStringBuilder.ConnectionIdleLifetime = (int)Math.Round(idleLifetimeMs.Value / 1000.0);
        }

        if (timeoutMs is >= 0)
        {
            // pg Pool's connectionTimeoutMillis <-> Npgsql's "Timeout" (seconds, connection establishment).
            connectionStringBuilder.Timeout = (int)Math.Ceiling(timeoutMs.Value / 1000.0);
        }

        if (!string.IsNullOrWhiteSpace(sslCaCertificatePath))
        {
            connectionStringBuilder.RootCertificate = sslCaCertificatePath;
        }

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionStringBuilder.ConnectionString);
        if (loggerFactory is not null)
        {
            dataSourceBuilder.UseLoggerFactory(loggerFactory);
        }

        return dataSourceBuilder.Build();
    }

    private static SslMode ParseSslMode(string sslMode) => sslMode.Trim().ToLowerInvariant() switch
    {
        "disable" => Npgsql.SslMode.Disable,
        "allow" => Npgsql.SslMode.Allow,
        "prefer" => Npgsql.SslMode.Prefer,
        "require" => Npgsql.SslMode.Require,
        "verifyca" or "verify-ca" => Npgsql.SslMode.VerifyCA,
        _ => Npgsql.SslMode.VerifyFull,
    };
}
