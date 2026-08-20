using Monitor.Data.DataSources;
using Npgsql;

namespace Monitor.UnitTests.DataSources;

/// <summary>
/// <see cref="PrimaryNpgsqlDataSource"/> and <see cref="OperationalNpgsqlDataSource"/> are the
/// .NET 6 stand-in for the keyed-DI feature that doesn't exist until .NET 8: two distinct
/// wrapper types let the DI container and consumers (PrimaryDb / OperationalDb) tell apart the
/// "DATABASE_URL" and undocumented "PRODUCTION_DATABASE_URL" connections from the source by
/// static type instead of a DI key. These tests just pin down that the wrappers are transparent
/// pass-throughs and are distinct types (so a consumer can never accidentally receive the wrong
/// one through normal constructor injection).
/// </summary>
public class NamedDataSourcesTests
{
    private static NpgsqlDataSource BuildDataSource(string connectionString) =>
        new NpgsqlDataSourceBuilder(connectionString).Build();

    [Fact]
    public void PrimaryNpgsqlDataSource_ExposesTheExactInstancePassedIn()
    {
        using var inner = BuildDataSource("Host=localhost;Database=primary_db");

        var wrapper = new PrimaryNpgsqlDataSource(inner);

        Assert.Same(inner, wrapper.DataSource);
    }

    [Fact]
    public void OperationalNpgsqlDataSource_ExposesTheExactInstancePassedIn()
    {
        using var inner = BuildDataSource("Host=replica;Database=operational_db");

        var wrapper = new OperationalNpgsqlDataSource(inner);

        Assert.Same(inner, wrapper.DataSource);
    }

    [Fact]
    public void PrimaryAndOperationalWrappers_AreDistinctTypes_EvenWrappingTheSameDataSource()
    {
        // The whole point of these wrappers is compile-time distinguishability: a
        // PrimaryNpgsqlDataSource can never satisfy a constructor parameter that asks for
        // OperationalNpgsqlDataSource (or vice-versa), regardless of what they wrap.
        using var inner = BuildDataSource("Host=localhost;Database=db");

        var primary = new PrimaryNpgsqlDataSource(inner);
        var operational = new OperationalNpgsqlDataSource(inner);

        Assert.IsType<PrimaryNpgsqlDataSource>(primary);
        Assert.IsType<OperationalNpgsqlDataSource>(operational);
        Assert.IsNotType<OperationalNpgsqlDataSource>(primary);
        Assert.IsNotType<PrimaryNpgsqlDataSource>(operational);
        // Both wrap the same underlying data source instance without any copying/mutation.
        Assert.Same(primary.DataSource, operational.DataSource);
    }
}
