using Npgsql;

namespace Monitor.Data.DataSources;

/// <summary>
/// .NET 6 has no keyed DI services (that's a .NET 8 feature), so the two
/// named connections the source's config distinguished implicitly
/// (<c>DATABASE_URL</c> vs. the undocumented <c>PRODUCTION_DATABASE_URL</c>)
/// are told apart by wrapper type instead of a DI key.
/// </summary>
public sealed class PrimaryNpgsqlDataSource
{
    public NpgsqlDataSource DataSource { get; }

    public PrimaryNpgsqlDataSource(NpgsqlDataSource dataSource)
    {
        DataSource = dataSource;
    }
}

/// <summary>
/// Present in DI only when <c>Database:Operational:ConnectionString</c> is
/// actually configured; <see cref="Data.Repositories.OperationalDb"/> treats
/// its absence as "operational source not configured" rather than an error.
/// </summary>
public sealed class OperationalNpgsqlDataSource
{
    public NpgsqlDataSource DataSource { get; }

    public OperationalNpgsqlDataSource(NpgsqlDataSource dataSource)
    {
        DataSource = dataSource;
    }
}
