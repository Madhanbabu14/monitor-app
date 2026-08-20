using Monitor.Core.Options;
using Monitor.Data.DataSources;
using Npgsql;

namespace Monitor.UnitTests.DataSources;

/// <summary>
/// These only exercise connection-string construction — no live Postgres is
/// available in the unit-test environment, and <see cref="NpgsqlDataSource"/>
/// connects lazily, so building one (without opening it) is safe and fast.
/// </summary>
public class NpgsqlDataSourceFactoryTests
{
    [Fact]
    public void CreatePrimary_DefaultsToVerifyFull_MatchingLockedSslMode()
    {
        var options = new PrimaryDatabaseOptions
        {
            ConnectionString = "Host=localhost;Database=db;Username=u;Password=p",
        };

        using var dataSource = NpgsqlDataSourceFactory.CreatePrimary(options);
        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
    }

    [Fact]
    public void CreatePrimary_NeverHonoursRejectUnauthorizedStyleDowngrade()
    {
        // Regression guard for the locked-SslMode decision: even if a caller
        // somehow passed a lowercase/loosely-cased "disable", any value other
        // than the enumerated modes must NOT silently fall back to something
        // insecure - it falls back to the locked default (VerifyFull).
        var options = new PrimaryDatabaseOptions
        {
            ConnectionString = "Host=localhost;Database=db",
            SslMode = "not-a-real-mode",
        };

        using var dataSource = NpgsqlDataSourceFactory.CreatePrimary(options);
        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
    }

    [Fact]
    public void CreatePrimary_MapsPoolingAndTimeoutOptions()
    {
        var options = new PrimaryDatabaseOptions
        {
            ConnectionString = "Host=localhost;Database=db",
            MaxConnections = 42,
            IdleTimeoutMs = 15000,
            ConnectionTimeoutMs = 2500,
        };

        using var dataSource = NpgsqlDataSourceFactory.CreatePrimary(options);
        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal(42, builder.MaxPoolSize);
        Assert.Equal(15, builder.ConnectionIdleLifetime);
        Assert.Equal(3, builder.Timeout); // ceil(2500ms / 1000)
    }

    [Fact]
    public void CreatePrimary_SetsRootCertificate_WhenCaPathConfigured()
    {
        var options = new PrimaryDatabaseOptions
        {
            ConnectionString = "Host=localhost;Database=db",
            SslCaCertificatePath = "/etc/ssl/certs/rds-ca-bundle.pem",
        };

        using var dataSource = NpgsqlDataSourceFactory.CreatePrimary(options);
        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal("/etc/ssl/certs/rds-ca-bundle.pem", builder.RootCertificate);
    }

    [Fact]
    public void CreateOperational_Throws_WhenConnectionStringMissing()
    {
        var options = new OperationalDatabaseOptions { ConnectionString = null };

        Assert.Throws<InvalidOperationException>(() => NpgsqlDataSourceFactory.CreateOperational(options));
    }

    [Fact]
    public void CreateOperational_BuildsDataSource_WhenConfigured()
    {
        var options = new OperationalDatabaseOptions
        {
            ConnectionString = "Host=replica;Database=db",
        };

        using var dataSource = NpgsqlDataSourceFactory.CreateOperational(options);
        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
        Assert.Equal("replica", builder.Host);
    }
}
