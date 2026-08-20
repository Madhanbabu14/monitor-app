using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Monitor.Data.DataSources;
using Monitor.Data.Repositories;
using Npgsql;

namespace Monitor.UnitTests.Repositories;

public class OperationalDbTests
{
    // Port 1 on loopback is refused by the OS immediately (nothing can ever bind to it), so
    // failures here are fast and deterministic without needing a live/fake Postgres server.
    private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=nope;Timeout=2";

    /// <summary>Minimal capturing logger — this test project references no mocking library.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static OperationalDb BuildConfiguredDb(ILogger<OperationalDb>? logger = null)
    {
        var dataSource = new NpgsqlDataSourceBuilder(UnreachableConnectionString).Build();
        return new OperationalDb(new OperationalNpgsqlDataSource(dataSource), logger ?? NullLogger<OperationalDb>.Instance);
    }

    [Fact]
    public void IsConfigured_IsFalse_WhenNoDataSourceProvided()
    {
        var db = new OperationalDb(dataSource: null, NullLogger<OperationalDb>.Instance);

        Assert.False(db.IsConfigured);
    }

    [Fact]
    public void IsConfigured_IsTrue_WhenDataSourceProvided()
    {
        var db = BuildConfiguredDb();

        Assert.True(db.IsConfigured);
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

    [Fact]
    public async Task QueryAsync_WhenNotConfigured_IgnoresCancellationAndStillReturnsUnavailable()
    {
        // Documents an actual branch order in OperationalDb.QueryAsync: the "not configured"
        // short-circuit happens before any cancellation check, so even an already-cancelled
        // token does not turn into an OperationCanceledException when there's no data source -
        // it degrades to Unavailable exactly like every other "not configured" call.
        var db = new OperationalDb(dataSource: null, NullLogger<OperationalDb>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await db.QueryAsync<int>("SELECT 1", cancellationToken: cts.Token);

        Assert.False(result.Available);
    }

    [Fact]
    public async Task QueryAsync_ReturnsUnavailable_NeverThrows_WhenConfiguredButUnreachable()
    {
        // The central contract that distinguishes OperationalDb from PrimaryDb: a broken
        // replica connection must degrade to Unavailable, not bubble up as an exception, so a
        // flaky operational DB can never take down the primary request path.
        var db = BuildConfiguredDb();

        var result = await db.QueryAsync<int>("SELECT 1");

        Assert.False(result.Available);
        Assert.Equal(default, result.Value);
    }

    [Fact]
    public async Task QueryAsync_PropagatesCallerCancellation_EvenWhenConfigured()
    {
        // A caller-requested cancellation is explicitly carved out of the "degrade to
        // Unavailable" contract (see CallerCancelledException in OperationalDb) - it must
        // still surface as an ordinary OperationCanceledException, not be swallowed.
        var db = BuildConfiguredDb();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.QueryAsync<int>("SELECT 1", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task QueryAsync_OpensCircuitBreaker_AfterThreeConsecutiveFailures()
    {
        var logger = new CapturingLogger<OperationalDb>();
        var db = BuildConfiguredDb(logger);

        // exceptionsAllowedBeforeBreaking: 3 - each of these hits the real (refused) connection
        // and is individually caught as a plain NpgsqlException failure.
        for (var i = 0; i < 3; i++)
        {
            var result = await db.QueryAsync<int>("SELECT 1");
            Assert.False(result.Available);
        }

        // The 4th call must short-circuit via the now-open breaker rather than attempting
        // another connection - still degrades to Unavailable, but for a different reason.
        var fourth = await db.QueryAsync<int>("SELECT 1");
        Assert.False(fourth.Available);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("circuit breaker opened", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("circuit breaker is open", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QueryAsync_GenericTypeParameter_IsIndependentPerCall()
    {
        // Sanity check that OperationalResult<T>'s type parameter follows the caller's request
        // rather than being fixed by the class - e.g. a caller can request IReadOnlyList<string>
        // on one call and IReadOnlyList<int> on another against the same OperationalDb instance.
        var db = BuildConfiguredDb();

        var intResult = await db.QueryAsync<int>("SELECT 1");
        var stringResult = await db.QueryAsync<string>("SELECT 'x'");

        Assert.False(intResult.Available);
        Assert.False(stringResult.Available);
    }
}
