using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monitor.Data.HealthChecks;
using Monitor.Data.Repositories;

namespace Monitor.UnitTests.HealthChecks;

/// <summary>
/// Exercises <see cref="OperationalDatabaseHealthCheck"/> against a fake
/// <see cref="IOperationalDb"/> so the three first-class states the source
/// never modelled — "not configured" (Healthy), "reachable" (Healthy), and
/// "unreachable / breaker open" (Degraded, never Unhealthy) — are pinned
/// down without touching a live Postgres instance. Mirrors the contract
/// documented on <see cref="IOperationalDb.QueryAsync{T}"/>: an optional,
/// read-only replica must never be able to flip the service's aggregate
/// health to failing.
/// </summary>
public class OperationalDatabaseHealthCheckTests
{
    private sealed class FakeOperationalDb : IOperationalDb
    {
        public bool IsConfigured { get; set; }

        public OperationalResult<IReadOnlyList<int>> Result { get; set; } =
            OperationalResult<IReadOnlyList<int>>.Unavailable();

        public string? LastSql { get; private set; }

        public object? LastParameters { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public int CallCount { get; private set; }

        public Task<OperationalResult<IReadOnlyList<T>>> QueryAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSql = sql;
            LastParameters = parameters;
            LastCancellationToken = cancellationToken;

            // The health check only ever calls QueryAsync<int>; casting the boxed
            // int-typed fixture result lets one fake serve every test below.
            return Task.FromResult((OperationalResult<IReadOnlyList<T>>)(object)Result);
        }
    }

    private static HealthCheckContext MakeContext() => new();

    [Fact]
    public async Task CheckHealthAsync_WhenNotConfigured_ReturnsHealthy_WithoutQuerying()
    {
        var db = new FakeOperationalDb { IsConfigured = false };
        var check = new OperationalDatabaseHealthCheck(db);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Operational database is not configured", result.Description);
        Assert.Equal(0, db.CallCount);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenConfiguredAndAvailable_ReturnsHealthy_UsingSelectOneProbe()
    {
        var db = new FakeOperationalDb
        {
            IsConfigured = true,
            Result = OperationalResult<IReadOnlyList<int>>.Ok(new List<int> { 1 }),
        };
        var check = new OperationalDatabaseHealthCheck(db);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Operational database is reachable", result.Description);
        Assert.Equal("SELECT 1 AS ok", db.LastSql);
        Assert.Equal(1, db.CallCount);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenConfiguredButUnavailable_ReturnsDegraded_NeverUnhealthy()
    {
        var db = new FakeOperationalDb
        {
            IsConfigured = true,
            Result = OperationalResult<IReadOnlyList<int>>.Unavailable(),
        };
        var check = new OperationalDatabaseHealthCheck(db);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("unreachable or its circuit breaker is open", result.Description);
        Assert.Contains("optional and read-only", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_PropagatesCancellationTokenThroughToTheQuery()
    {
        var db = new FakeOperationalDb
        {
            IsConfigured = true,
            Result = OperationalResult<IReadOnlyList<int>>.Ok(new List<int> { 1 }),
        };
        var check = new OperationalDatabaseHealthCheck(db);
        using var cts = new CancellationTokenSource();

        await check.CheckHealthAsync(MakeContext(), cts.Token);

        Assert.Equal(cts.Token, db.LastCancellationToken);
    }

    [Fact]
    public async Task CheckHealthAsync_DoesNotPassAnyParameters_ForThePlainProbeQuery()
    {
        var db = new FakeOperationalDb
        {
            IsConfigured = true,
            Result = OperationalResult<IReadOnlyList<int>>.Ok(new List<int> { 1 }),
        };
        var check = new OperationalDatabaseHealthCheck(db);

        await check.CheckHealthAsync(MakeContext());

        Assert.Null(db.LastParameters);
    }

    [Fact]
    public async Task CheckHealthAsync_ConfiguredFalse_TakesPrecedence_EvenIfResultWouldBeAvailable()
    {
        // IsConfigured is checked first; a fake that would otherwise report Available
        // must still short-circuit to the "not configured" Healthy branch without
        // ever invoking QueryAsync, exercising the guard clause's ordering.
        var db = new FakeOperationalDb
        {
            IsConfigured = false,
            Result = OperationalResult<IReadOnlyList<int>>.Ok(new List<int> { 1 }),
        };
        var check = new OperationalDatabaseHealthCheck(db);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Operational database is not configured", result.Description);
        Assert.Equal(0, db.CallCount);
    }
}
