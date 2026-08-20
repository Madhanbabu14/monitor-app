using Dapper;
using Microsoft.Extensions.Logging;
using Monitor.Data.DataSources;
using Npgsql;

namespace Monitor.Data.Repositories;

/// <inheritdoc cref="IPrimaryDb"/>
public sealed class PrimaryDb : IPrimaryDb
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PrimaryDb> _logger;

    public PrimaryDb(PrimaryNpgsqlDataSource dataSource, ILogger<PrimaryDb> logger)
    {
        _dataSource = dataSource.DataSource;
        _logger = logger;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<T>(command);
        return rows.AsList();
    }

    public async Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync<T>(sql, parameters, cancellationToken);
        return rows.Count > 0 ? rows[0] : default;
    }

    public async Task<TResult> WithTransactionAsync<TResult>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await QueryAsync<int>("SELECT 1", cancellationToken: cancellationToken);
            _logger.LogInformation("PostgreSQL connection established");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostgreSQL connection failed");
            throw;
        }
    }
}
