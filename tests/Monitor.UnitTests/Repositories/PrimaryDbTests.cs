using Microsoft.Extensions.Logging.Abstractions;
using Monitor.Data.DataSources;
using Monitor.Data.Repositories;
using Npgsql;

namespace Monitor.UnitTests.Repositories;

/// <summary>
/// PrimaryDb is a thin Dapper wrapper around a real <see cref="NpgsqlDataSource"/> — there's no
/// fake-able seam below it (unlike OperationalDb, it takes no circuit breaker and does no
/// swallowing), so a live Postgres would be required to exercise its "happy path" query
/// mapping. What IS unit-testable without a database, deterministically and fast, is the
/// contract that most distinguishes PrimaryDb from OperationalDb: PrimaryDb never degrades a
/// connectivity failure — it always lets the underlying exception propagate — and it honours
/// caller-requested cancellation the same way. Connecting to TCP port 1 on loopback is refused
/// immediately by the OS (no listener can ever bind to it), so these tests fail fast without
/// flakiness and without needing a running database.
/// </summary>
public class PrimaryDbTests
{
    private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=nope;Timeout=2";

    private static PrimaryDb BuildUnreachablePrimaryDb()
    {
        var dataSource = new NpgsqlDataSourceBuilder(UnreachableConnectionString).Build();
        return new PrimaryDb(new PrimaryNpgsqlDataSource(dataSource), NullLogger<PrimaryDb>.Instance);
    }

    [Fact]
    public async Task QueryAsync_PropagatesNpgsqlException_OnConnectionFailure()
    {
        // Unlike OperationalDb.QueryAsync, PrimaryDb has no catch-and-degrade path: a bad
        // primary connection must surface as a hard failure, since the primary DB is not
        // allowed to silently "go missing" the way the optional replica can.
        var db = BuildUnreachablePrimaryDb();

        await Assert.ThrowsAsync<NpgsqlException>(() => db.QueryAsync<int>("SELECT 1"));
    }

    [Fact]
    public async Task QuerySingleAsync_PropagatesNpgsqlException_OnConnectionFailure()
    {
        var db = BuildUnreachablePrimaryDb();

        await Assert.ThrowsAsync<NpgsqlException>(() => db.QuerySingleAsync<int>("SELECT 1"));
    }

    [Fact]
    public async Task WithTransactionAsync_PropagatesException_AndNeverInvokesAction_WhenConnectionFails()
    {
        var db = BuildUnreachablePrimaryDb();
        var actionInvoked = false;

        await Assert.ThrowsAsync<NpgsqlException>(() => db.WithTransactionAsync<int>((_, _) =>
        {
            actionInvoked = true;
            return Task.FromResult(0);
        }));

        Assert.False(actionInvoked);
    }

    [Fact]
    public async Task TestConnectionAsync_PropagatesException_WhenPrimaryIsUnreachable()
    {
        // Analogue of the source's testConnection() rethrowing after logging — this is what
        // PrimaryDatabaseStartupCheck relies on to abort host startup.
        var db = BuildUnreachablePrimaryDb();

        await Assert.ThrowsAsync<NpgsqlException>(() => db.TestConnectionAsync());
    }

    [Fact]
    public async Task QueryAsync_PropagatesOperationCanceledException_WhenCallerTokenAlreadyCancelled()
    {
        var db = BuildUnreachablePrimaryDb();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.QueryAsync<int>("SELECT 1", cancellationToken: cts.Token));
    }

    [Fact]
    public void Constructor_ExtractsUnderlyingDataSource_FromTheWrapper()
    {
        // PrimaryDb unwraps PrimaryNpgsqlDataSource eagerly in its constructor (unlike
        // OperationalDb, which keeps a nullable field) — a null wrapper is a programmer error
        // (Program.cs/ServiceCollectionExtensions always supplies one), so it should surface
        // immediately as a NullReferenceException rather than being silently tolerated.
        Assert.Throws<NullReferenceException>(() => new PrimaryDb(null!, NullLogger<PrimaryDb>.Instance));
    }
}
