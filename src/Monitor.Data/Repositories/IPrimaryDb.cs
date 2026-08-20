using Npgsql;

namespace Monitor.Data.Repositories;

/// <summary>
/// Dapper-backed access to the primary database. Direct analogue of
/// infrastructure/database/connection.ts's <c>query</c> / <c>queryOne</c> /
/// <c>withTransaction</c> / <c>testConnection</c> free functions, collected
/// behind an interface so feature repositories (Monitor.Identity,
/// Monitor.Files, Monitor.Operations) can be unit-tested against a fake.
/// </summary>
public interface IPrimaryDb
{
    /// <summary>Analogue of <c>query&lt;T&gt;()</c> — always returns a (possibly empty) list.</summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Analogue of <c>queryOne&lt;T&gt;()</c> — first row or null, never throws on zero rows.</summary>
    Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analogue of <c>withTransaction()</c>: BEGIN, run <paramref name="action"/>,
    /// COMMIT; ROLLBACK and rethrow on any exception from <paramref name="action"/>.
    /// </summary>
    Task<TResult> WithTransactionAsync<TResult>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> action,
        CancellationToken cancellationToken = default);

    /// <summary>Analogue of <c>testConnection()</c> — used at startup to fail fast if the primary DB is unreachable.</summary>
    Task TestConnectionAsync(CancellationToken cancellationToken = default);
}
