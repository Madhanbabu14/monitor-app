using Monitor.Data.Repositories;
using Monitor.Data.Startup;

namespace Monitor.UnitTests.Startup;

/// <summary>
/// <see cref="PrimaryDatabaseStartupCheck"/> is the fail-fast analogue of the source calling
/// <c>testConnection()</c> before <c>app.listen()</c>. These tests use a hand-written
/// <see cref="IPrimaryDb"/> fake (no mocking library is referenced by this test project) so the
/// startup-check's own logic — delegate to TestConnectionAsync, propagate its failures, do
/// nothing on stop — can be verified without a real database.
/// </summary>
public class PrimaryDatabaseStartupCheckTests
{
    private sealed class FakePrimaryDb : IPrimaryDb
    {
        public CancellationToken? TestConnectionCalledWithToken { get; private set; }
        public int TestConnectionCallCount { get; private set; }
        public Exception? ExceptionToThrow { get; set; }

        public Task TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            TestConnectionCallCount++;
            TestConnectionCalledWithToken = cancellationToken;
            return ExceptionToThrow is null
                ? Task.CompletedTask
                : Task.FromException(ExceptionToThrow);
        }

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by PrimaryDatabaseStartupCheck.");

        public Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by PrimaryDatabaseStartupCheck.");

        public Task<TResult> WithTransactionAsync<TResult>(Func<Npgsql.NpgsqlConnection, Npgsql.NpgsqlTransaction, Task<TResult>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by PrimaryDatabaseStartupCheck.");
    }

    [Fact]
    public async Task StartAsync_DelegatesToPrimaryDbTestConnectionAsync()
    {
        var fakeDb = new FakePrimaryDb();
        var check = new PrimaryDatabaseStartupCheck(fakeDb);

        await check.StartAsync(CancellationToken.None);

        Assert.Equal(1, fakeDb.TestConnectionCallCount);
    }

    [Fact]
    public async Task StartAsync_ForwardsTheSuppliedCancellationToken()
    {
        var fakeDb = new FakePrimaryDb();
        var check = new PrimaryDatabaseStartupCheck(fakeDb);
        using var cts = new CancellationTokenSource();

        await check.StartAsync(cts.Token);

        Assert.Equal(cts.Token, fakeDb.TestConnectionCalledWithToken);
    }

    [Fact]
    public async Task StartAsync_PropagatesException_AbortingHostStartup()
    {
        // This is the whole point of the class: an unreachable primary DB must prevent the
        // host from ever reaching "listening" state, exactly like the source refusing to call
        // app.listen() until testConnection() resolves.
        var fakeDb = new FakePrimaryDb { ExceptionToThrow = new InvalidOperationException("primary db unreachable") };
        var check = new PrimaryDatabaseStartupCheck(fakeDb);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));
        Assert.Equal("primary db unreachable", ex.Message);
    }

    [Fact]
    public async Task StartAsync_CompletesSuccessfully_WhenPrimaryDbIsReachable()
    {
        var fakeDb = new FakePrimaryDb();
        var check = new PrimaryDatabaseStartupCheck(fakeDb);

        var task = check.StartAsync(CancellationToken.None);

        await task; // must not throw
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately_WithoutTouchingPrimaryDb()
    {
        var fakeDb = new FakePrimaryDb();
        var check = new PrimaryDatabaseStartupCheck(fakeDb);

        var task = check.StopAsync(CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(0, fakeDb.TestConnectionCallCount);
    }
}
