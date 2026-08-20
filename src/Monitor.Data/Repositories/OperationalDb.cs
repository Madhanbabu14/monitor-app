using Dapper;
using Microsoft.Extensions.Logging;
using Monitor.Data.DataSources;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;

namespace Monitor.Data.Repositories;

/// <inheritdoc cref="IOperationalDb"/>
/// <remarks>
/// Deliberately narrow exception handling: only <see cref="NpgsqlException"/>,
/// <see cref="TimeoutException"/>, and an internally-triggered
/// <see cref="OperationCanceledException"/> (e.g. a command timeout) are
/// treated as "operational DB is unavailable right now" and swallowed into
/// <see cref="OperationalResult{T}.Unavailable"/>. A caller-requested
/// cancellation (the token passed in was itself cancelled) is NOT swallowed
/// — it propagates like any other cancellation. Never <c>catch (Exception)</c>:
/// a bug in this class should surface, not silently degrade.
/// </remarks>
public sealed class OperationalDb : IOperationalDb
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly ILogger<OperationalDb> _logger;
    private readonly IAsyncPolicy _circuitBreaker;

    public OperationalDb(OperationalNpgsqlDataSource? dataSource, ILogger<OperationalDb> logger)
    {
        _dataSource = dataSource?.DataSource;
        _logger = logger;
        _circuitBreaker = Policy
            .Handle<NpgsqlException>()
            .Or<TimeoutException>()
            .Or<OperationCanceledException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (ex, breakDelay) =>
                    _logger.LogWarning(ex, "Operational DB circuit breaker opened for {BreakDelaySeconds}s", breakDelay.TotalSeconds),
                onReset: () => _logger.LogInformation("Operational DB circuit breaker reset"),
                onHalfOpen: () => _logger.LogInformation("Operational DB circuit breaker half-open"));
    }

    public bool IsConfigured => _dataSource is not null;

    public async Task<OperationalResult<IReadOnlyList<T>>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_dataSource is null)
        {
            return OperationalResult<IReadOnlyList<T>>.Unavailable();
        }

        try
        {
            return await _circuitBreaker.ExecuteAsync(async ct =>
            {
                await using var connection = await _dataSource.OpenConnectionAsync(ct);
                var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
                var rows = await connection.QueryAsync<T>(command);
                return OperationalResult<IReadOnlyList<T>>.Ok(rows.AsList());
            }, cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(ex, "Operational DB circuit breaker is open; skipping query");
            return OperationalResult<IReadOnlyList<T>>.Unavailable();
        }
        catch (NpgsqlException ex)
        {
            _logger.LogWarning(ex, "Operational DB query failed");
            return OperationalResult<IReadOnlyList<T>>.Unavailable();
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Operational DB query timed out");
            return OperationalResult<IReadOnlyList<T>>.Unavailable();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Operational DB query cancelled internally (e.g. command timeout)");
            return OperationalResult<IReadOnlyList<T>>.Unavailable();
        }
    }
}
