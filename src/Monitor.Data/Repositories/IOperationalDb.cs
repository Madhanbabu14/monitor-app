namespace Monitor.Data.Repositories;

/// <summary>
/// Read-only access to the operational (production replica) database. First
/// class, documented, and optional — <see cref="IsConfigured"/> is false
/// when <c>Database:Operational:ConnectionString</c> was never set, in which
/// case every query call degrades to <c>Available = false</c> instead of
/// throwing.
/// </summary>
public interface IOperationalDb
{
    /// <summary>False when the operational connection was never configured; queries then always return Unavailable.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Runs a read-only query behind a circuit breaker. Never throws for
    /// operational-database failures (connectivity, timeout, breaker open) —
    /// those degrade to <see cref="OperationalResult{T}.Unavailable"/> so a
    /// flaky replica cannot take down the primary request path. A
    /// caller-requested cancellation (<paramref name="cancellationToken"/>)
    /// still propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<OperationalResult<IReadOnlyList<T>>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);
}
