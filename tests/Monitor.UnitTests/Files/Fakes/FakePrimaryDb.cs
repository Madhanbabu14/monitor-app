using Monitor.Data.Repositories;
using Npgsql;

namespace Monitor.UnitTests.Files.Fakes;

/// <summary>
/// Test double for <see cref="IPrimaryDb"/>. <see cref="FilesService.RetriggerFileAsync"/>
/// is the only member of <see cref="Monitor.Files.FilesService"/> that touches the
/// primary database, and it only ever calls <see cref="QueryAsync{T}"/> — recorded here
/// verbatim (sql text + the anonymous parameter object) so tests can assert exactly what
/// was about to be sent to Postgres without needing a real connection.
/// </summary>
public sealed class FakePrimaryDb : IPrimaryDb
{
    public sealed record RecordedQuery(string Sql, object? Parameters);

    public List<RecordedQuery> Queries { get; } = new();

    public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        Queries.Add(new RecordedQuery(sql, parameters));
        return Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
    }

    public Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        Queries.Add(new RecordedQuery(sql, parameters));
        return Task.FromResult<T?>(default);
    }

    public Task<TResult> WithTransactionAsync<TResult>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Not used by FilesService.");
    }

    public Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Not used by FilesService.");
    }

    public static object? GetParam(object? parameters, string name) =>
        parameters?.GetType().GetProperty(name)?.GetValue(parameters);
}
