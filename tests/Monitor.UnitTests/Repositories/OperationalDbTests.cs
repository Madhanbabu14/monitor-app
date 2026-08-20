using Microsoft.Extensions.Logging.Abstractions;
using Monitor.Data.Repositories;

namespace Monitor.UnitTests.Repositories;

public class OperationalDbTests
{
    [Fact]
    public void IsConfigured_IsFalse_WhenNoDataSourceProvided()
    {
        var db = new OperationalDb(dataSource: null, NullLogger<OperationalDb>.Instance);

        Assert.False(db.IsConfigured);
    }

    [Fact]
    public async Task QueryAsync_ReturnsUnavailable_WithoutTouchingTheNetwork_WhenNotConfigured()
    {
        // No NpgsqlDataSource is ever constructed here, so this proves the
        // "not configured" path short-circuits before attempting any I/O -
        // matching the source having no operational connection at all today.
        var db = new OperationalDb(dataSource: null, NullLogger<OperationalDb>.Instance);

        var result = await db.QueryAsync<int>("SELECT 1");

        Assert.False(result.Available);
        Assert.Null(result.Value);
    }
}
