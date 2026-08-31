using Monitor.Core.Errors;
using Monitor.Data.Repositories;
using Monitor.Identity.Users;
using Npgsql;

namespace Monitor.UnitTests.Users;

/// <summary>
/// Exercises <see cref="UserRepository"/> — the Dapper-backed analogue of the raw
/// <c>queryOne&lt;UserRow&gt;(...)</c> calls scattered through auth.service.ts. Uses a
/// hand-written <see cref="IPrimaryDb"/> fake (no mocking library referenced by this test
/// project, matching <c>PrimaryDatabaseStartupCheckTests</c>) that records the SQL text and
/// parameter object handed to it, so each method's query shape and forwarded arguments can
/// be pinned down without a real Postgres connection.
/// </summary>
public class UserRepositoryTests
{
    private sealed class FakePrimaryDb : IPrimaryDb
    {
        public string? LastSql;
        public object? LastParameters;
        public CancellationToken LastCancellationToken;
        public int QuerySingleCallCount;
        public int QueryCallCount;
        public object? QuerySingleResult;

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            QueryCallCount++;
            LastSql = sql;
            LastParameters = parameters;
            LastCancellationToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
        }

        public Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            QuerySingleCallCount++;
            LastSql = sql;
            LastParameters = parameters;
            LastCancellationToken = cancellationToken;
            return Task.FromResult((T?)QuerySingleResult);
        }

        public Task<TResult> WithTransactionAsync<TResult>(Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by UserRepository.");

        public Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by UserRepository.");
    }

    private static object? GetParam(object? parameters, string name) =>
        parameters?.GetType().GetProperty(name)?.GetValue(parameters);

    [Fact]
    public async Task FindByEmailAsync_QueriesUsersByEmail_AndSelectsPasswordHash()
    {
        var db = new FakePrimaryDb();
        var expectedRow = new UserRow { Id = Guid.NewGuid(), Email = "alice@example.com" };
        db.QuerySingleResult = expectedRow;
        var repository = new UserRepository(db);

        var result = await repository.FindByEmailAsync("alice@example.com");

        Assert.Same(expectedRow, result);
        Assert.Equal(1, db.QuerySingleCallCount);
        Assert.Contains("WHERE email = @email", db.LastSql);
        Assert.Contains("password_hash", db.LastSql);
        Assert.Equal("alice@example.com", GetParam(db.LastParameters, "email"));
    }

    [Fact]
    public async Task FindByEmailAsync_NoMatch_ReturnsNull()
    {
        var db = new FakePrimaryDb { QuerySingleResult = null };
        var repository = new UserRepository(db);

        var result = await repository.FindByEmailAsync("missing@example.com");

        Assert.Null(result);
    }

    [Fact]
    public async Task FindByEmailAsync_ForwardsCancellationToken()
    {
        var db = new FakePrimaryDb();
        var repository = new UserRepository(db);
        using var cts = new CancellationTokenSource();

        await repository.FindByEmailAsync("alice@example.com", cts.Token);

        Assert.Equal(cts.Token, db.LastCancellationToken);
    }

    [Fact]
    public async Task FindByAzureOidAsync_QueriesUsersByAzureOid_AndDoesNotSelectPasswordHash()
    {
        var db = new FakePrimaryDb();
        var expectedRow = new UserRow { Id = Guid.NewGuid(), AzureOid = "oid-1" };
        db.QuerySingleResult = expectedRow;
        var repository = new UserRepository(db);

        var result = await repository.FindByAzureOidAsync("oid-1");

        Assert.Same(expectedRow, result);
        Assert.Contains("WHERE azure_oid = @azureOid", db.LastSql);
        Assert.DoesNotContain("password_hash", db.LastSql);
        Assert.Equal("oid-1", GetParam(db.LastParameters, "azureOid"));
    }

    [Fact]
    public async Task FindByAzureOidAsync_NoMatch_ReturnsNull()
    {
        var db = new FakePrimaryDb { QuerySingleResult = null };
        var repository = new UserRepository(db);

        var result = await repository.FindByAzureOidAsync("no-such-oid");

        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertByEmailAsync_IssuesInsertOnConflictWithExpectedParameters()
    {
        var db = new FakePrimaryDb();
        var expectedRow = new UserRow { Id = Guid.NewGuid(), Email = "new@example.com", Role = "Viewer" };
        db.QuerySingleResult = expectedRow;
        var repository = new UserRepository(db);

        var result = await repository.UpsertByEmailAsync("new@example.com", "New User", "azure-oid-99");

        Assert.Same(expectedRow, result);
        Assert.Contains("INSERT INTO users", db.LastSql);
        Assert.Contains("ON CONFLICT (email) DO UPDATE", db.LastSql);
        Assert.Contains("'Viewer'", db.LastSql);
        Assert.Equal("new@example.com", GetParam(db.LastParameters, "email"));
        Assert.Equal("New User", GetParam(db.LastParameters, "displayName"));
        Assert.Equal("azure-oid-99", GetParam(db.LastParameters, "azureOid"));
    }

    [Fact]
    public async Task UpsertByEmailAsync_NullReturningRow_Throws500FailedToProcessAzureAdUser()
    {
        // The RETURNING clause of INSERT ... ON CONFLICT DO UPDATE always yields exactly one
        // row in real Postgres; this null-guard is the same "shouldn't happen but the source
        // guarded it anyway" case ssoLogin's `if (!row) throw new AppError(500, ...)` covers.
        var db = new FakePrimaryDb { QuerySingleResult = null };
        var repository = new UserRepository(db);

        var ex = await Assert.ThrowsAsync<AppException>(() => repository.UpsertByEmailAsync("a@b.com", "A", "oid-1"));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Failed to process Azure AD user", ex.Message);
    }

    [Fact]
    public async Task UpdateLastLoginAsync_IssuesUpdateWithId_AndNoResultSet()
    {
        var db = new FakePrimaryDb();
        var repository = new UserRepository(db);
        var id = Guid.NewGuid();

        await repository.UpdateLastLoginAsync(id);

        Assert.Equal(1, db.QueryCallCount);
        Assert.Contains("UPDATE users SET last_login_at = NOW() WHERE id = @id", db.LastSql);
        Assert.Equal(id, GetParam(db.LastParameters, "id"));
    }

    [Fact]
    public async Task UpdateLastLoginAndAzureOidAsync_IssuesUpdateWithIdAndAzureOid()
    {
        var db = new FakePrimaryDb();
        var repository = new UserRepository(db);
        var id = Guid.NewGuid();

        await repository.UpdateLastLoginAndAzureOidAsync(id, "oid-42");

        Assert.Equal(1, db.QueryCallCount);
        Assert.Contains("azure_oid = @azureOid", db.LastSql);
        Assert.Contains("last_login_at = NOW()", db.LastSql);
        Assert.Equal(id, GetParam(db.LastParameters, "id"));
        Assert.Equal("oid-42", GetParam(db.LastParameters, "azureOid"));
    }
}
