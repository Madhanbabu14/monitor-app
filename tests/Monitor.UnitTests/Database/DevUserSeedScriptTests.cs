using System.Data;
using System.Text.RegularExpressions;
using Monitor.Database.Seed;

namespace Monitor.UnitTests.Database;

/// <summary>
/// Unit tests for <see cref="DevUserSeedScript"/>, the dev-only DbUp code migration that
/// replaces the source repo's <c>database/seeds/seed.sql</c> hard-coded admin/operator/viewer
/// accounts (all three sharing the single bcrypt hash
/// <c>$2a$10$B6c.W3zIC9kC9O1yE7/W4...</c> committed to source control) with a fresh,
/// cryptographically random, bcrypt-hashed password generated per user per run.
///
/// DbUp's <c>IScript</c> contract only requires <c>ProvideScript(Func&lt;IDbCommand&gt;)</c> to
/// return a string that is executed as SQL and to actually perform its work through the
/// supplied ADO.NET command factory - so these tests exercise the real script against
/// hand-rolled <see cref="IDbCommand"/>/<see cref="IDbDataParameter"/> fakes rather than a real
/// Postgres connection, and capture the human-readable output the class also prints to
/// <see cref="Console"/> (the one and only place the generated plaintext password is ever
/// surfaced).
/// </summary>
public class DevUserSeedScriptTests
{
    private static readonly (string Email, string DisplayName, string Role)[] ExpectedDemoUsers =
    {
        ("admin@company.com", "Admin User", "Admin"),
        ("operator@company.com", "Ops User", "Operator"),
        ("viewer@company.com", "View User", "Viewer"),
    };

    private static (string ScriptResult, string ConsoleOutput, RecordingCommandFactory Factory) RunScript(
        Func<FakeDbCommand, int> executeNonQueryResult)
    {
        var factory = new RecordingCommandFactory { ExecuteNonQueryResult = executeNonQueryResult };
        var script = new DevUserSeedScript();

        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        string result;
        try
        {
            result = script.ProvideScript(factory.Create);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return (result, writer.ToString(), factory);
    }

    [Fact]
    public void ProvideScript_CreatesExactlyThreeCommands_OnePerDemoUser()
    {
        var (_, _, factory) = RunScript(_ => 1);

        Assert.Equal(3, factory.Commands.Count);
    }

    [Fact]
    public void ProvideScript_UsesInsertWithOnConflictDoNothing_OnEmail()
    {
        var (_, _, factory) = RunScript(_ => 1);

        foreach (var command in factory.Commands)
        {
            Assert.Contains("INSERT INTO users", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("ON CONFLICT (email) DO NOTHING", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("uuid_generate_v4()", command.CommandText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProvideScript_BindsExpectedDemoUsers_InOrder_WithFourParametersEach()
    {
        var (_, _, factory) = RunScript(_ => 1);

        Assert.Equal(ExpectedDemoUsers.Length, factory.Commands.Count);

        for (var i = 0; i < ExpectedDemoUsers.Length; i++)
        {
            var (email, displayName, role) = ExpectedDemoUsers[i];
            var command = factory.Commands[i];

            Assert.Equal(4, command.Parameters.Count);
            Assert.Equal(email, GetParameterValue(command, "email"));
            Assert.Equal(displayName, GetParameterValue(command, "display_name"));
            Assert.Equal(role, GetParameterValue(command, "role"));
        }
    }

    [Fact]
    public void ProvideScript_NeverBindsAHardCodedPasswordHash()
    {
        // The source's seed.sql bound the SAME literal hash string for every user; the
        // migrated script must never do that - each hash must be freshly computed.
        const string SourceHardCodedHash = "$2a$10$B6c.W3zIC9kC9O1yE7/W4.ef9SWH.lIQoBMXgy0T4q8NA9GQ5lkPi";

        var (_, _, factory) = RunScript(_ => 1);

        var hashes = factory.Commands
            .Select(c => (string)GetParameterValue(c, "password_hash")!)
            .ToList();

        Assert.All(hashes, hash => Assert.NotEqual(SourceHardCodedHash, hash));
        // No two demo users should share a hash either (each has its own random password).
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public void ProvideScript_PasswordHashes_AreBcryptWorkFactor10()
    {
        var (_, _, factory) = RunScript(_ => 1);

        foreach (var command in factory.Commands)
        {
            var hash = (string)GetParameterValue(command, "password_hash")!;
            Assert.StartsWith("$2a$10$", hash, StringComparison.Ordinal);
            // bcrypt hash strings are always exactly 60 characters.
            Assert.Equal(60, hash.Length);
        }
    }

    [Fact]
    public void ProvideScript_GeneratedPasswords_ArePrintedOnce_AndVerifyAgainstTheBoundHash()
    {
        var (_, consoleOutput, factory) = RunScript(_ => 1);

        var printed = ExtractPrintedPasswords(consoleOutput);
        Assert.Equal(3, printed.Count);

        foreach (var (email, _, role) in ExpectedDemoUsers)
        {
            Assert.True(printed.ContainsKey(email), $"Expected a printed password line for {email}.");
            var password = printed[email];

            var command = Assert.Single(factory.Commands, c => Equals(GetParameterValue(c, "email"), email));
            var hash = (string)GetParameterValue(command, "password_hash")!;

            Assert.True(BCrypt.Net.BCrypt.Verify(password, hash), $"Printed password for {email} must verify against its bound bcrypt hash.");
            Assert.Contains(role, consoleOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProvideScript_GeneratedPasswords_AreUrlSafeBase64_AndDistinctAcrossUsers()
    {
        var (_, consoleOutput, _) = RunScript(_ => 1);

        var printed = ExtractPrintedPasswords(consoleOutput);
        Assert.Equal(3, printed.Count);

        foreach (var password in printed.Values)
        {
            // 24 random bytes, base64-encoded (24 is a multiple of 3, so no '=' padding is
            // even produced by Convert.ToBase64String), then made URL-safe.
            Assert.Equal(32, password.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", password);
            Assert.DoesNotContain("+", password, StringComparison.Ordinal);
            Assert.DoesNotContain("/", password, StringComparison.Ordinal);
            Assert.DoesNotContain("=", password, StringComparison.Ordinal);
        }

        Assert.Equal(printed.Values.Count, printed.Values.Distinct().Count());
    }

    [Fact]
    public void ProvideScript_CalledTwice_GeneratesDifferentPasswordsEachRun()
    {
        // Nothing about this script may be deterministic/reusable across runs - otherwise it
        // would effectively be a hard-coded credential by another name.
        var (_, consoleOutput1, _) = RunScript(_ => 1);
        var (_, consoleOutput2, _) = RunScript(_ => 1);

        var passwords1 = ExtractPrintedPasswords(consoleOutput1);
        var passwords2 = ExtractPrintedPasswords(consoleOutput2);

        foreach (var email in passwords1.Keys)
        {
            Assert.NotEqual(passwords1[email], passwords2[email]);
        }
    }

    [Fact]
    public void ProvideScript_WhenUserAlreadyExists_DoesNotPrintAPassword_AndLogsLeftUnchanged()
    {
        // ExecuteNonQuery() returning 0 (no rows affected) mirrors ON CONFLICT (email) DO
        // NOTHING silently skipping an existing row - re-running Monitor.Database against an
        // already-seeded dev database must be a no-op here, just like the source's identical
        // ON CONFLICT clause.
        var (result, consoleOutput, factory) = RunScript(_ => 0);

        Assert.Equal(3, factory.Commands.Count);
        Assert.DoesNotContain("password:", consoleOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("[dev-seed] created", consoleOutput, StringComparison.Ordinal);

        foreach (var (email, _, _) in ExpectedDemoUsers)
        {
            Assert.Contains($"-- Demo user {email} already exists - left unchanged.", result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProvideScript_MixedExistingAndNewUsers_OnlyPrintsPasswordsForNewlyCreatedUsers()
    {
        // admin already exists, operator/viewer are new - only the latter two should surface
        // a plaintext password anywhere.
        var (result, consoleOutput, _) = RunScript(command =>
            Equals(GetParameterValue(command, "email"), "admin@company.com") ? 0 : 1);

        var printed = ExtractPrintedPasswords(consoleOutput);

        Assert.False(printed.ContainsKey("admin@company.com"));
        Assert.True(printed.ContainsKey("operator@company.com"));
        Assert.True(printed.ContainsKey("viewer@company.com"));

        Assert.Contains("-- Demo user admin@company.com already exists - left unchanged.", result, StringComparison.Ordinal);
        Assert.Contains("-- Created demo user operator@company.com (Operator).", result, StringComparison.Ordinal);
        Assert.Contains("-- Created demo user viewer@company.com (Viewer).", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvideScript_ReturnValue_IsASqlCommentLog_SafeIfExecutedAgainstTheDatabase()
    {
        // Program.cs registers this class as an IScript whose returned string DbUp will
        // execute as a SQL command against the target database; every non-blank line must
        // therefore be a "--" SQL comment so this stays a safe no-op if DbUp ever runs it
        // verbatim rather than merely logging it.
        var (result, _, _) = RunScript(_ => 1);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.StartsWith("--", line, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Monitor.Database dev seed", StringComparison.Ordinal));
    }

    [Fact]
    public void ProvideScript_UsersRoleMatchesUserRoleEnumWireStrings()
    {
        // Guards the three demo users against drifting from Monitor.Core.Domain.UserRole's
        // wire strings (the users.role CHECK constraint in Script0001_UsedTables.sql only
        // accepts 'Admin' | 'Operator' | 'Viewer').
        var (_, _, factory) = RunScript(_ => 1);

        var roles = factory.Commands.Select(c => (string)GetParameterValue(c, "role")!).ToList();

        Assert.Equal(new[] { "Admin", "Operator", "Viewer" }, roles);
        Assert.All(roles, role => Assert.True(Monitor.Core.Domain.UserRoleExtensions.TryParse(role, out _)));
    }

    [Fact]
    public void ProvideScript_IsActiveIsNotAParameter_UsersAreInsertedAsActiveByHardCodedLiteralTrue()
    {
        // is_active is passed as the literal `true` in the INSERT's VALUES list, not bound as
        // a parameter - assert that stays a fixed SQL literal (any regression that turns it
        // into a parameter, or flips it to false, would break the demo accounts).
        var (_, _, factory) = RunScript(_ => 1);

        foreach (var command in factory.Commands)
        {
            Assert.Contains("true)", command.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("@is_active", command.CommandText, StringComparison.Ordinal);
        }
    }

    private static object? GetParameterValue(FakeDbCommand command, string name) =>
        command.Parameters.Cast<IDataParameter>().Single(p => p.ParameterName == name).Value;

    private static Dictionary<string, string> ExtractPrintedPasswords(string consoleOutput)
    {
        // Matches lines like:
        // [dev-seed] created Admin    admin@company.com          password: AbC...  (one-time - rotate this at first login)
        var matches = Regex.Matches(
            consoleOutput,
            @"\[dev-seed\] created\s+\S+\s+(?<email>\S+)\s+password:\s+(?<password>\S+)\s+",
            RegexOptions.Multiline);

        return matches
            .Select(m => (Email: m.Groups["email"].Value, Password: m.Groups["password"].Value))
            .ToDictionary(x => x.Email, x => x.Password);
    }

    /// <summary>Minimal <see cref="IDbCommand"/> fake sufficient for <see cref="DevUserSeedScript"/>.</summary>
    private sealed class FakeDbCommand : IDbCommand
    {
        public string? CommandText { get; set; }

        public int CommandTimeout { get; set; }

        public CommandType CommandType { get; set; } = CommandType.Text;

        public IDbConnection? Connection { get; set; }

        public IDataParameterCollection Parameters { get; } = new FakeParameterCollection();

        public IDbTransaction? Transaction { get; set; }

        public UpdateRowSource UpdatedRowSource { get; set; }

        public Func<int> OnExecuteNonQuery { get; set; } = () => 0;

        public void Cancel()
        {
        }

        public IDbDataParameter CreateParameter() => new FakeDbParameter();

        public void Dispose()
        {
        }

        public int ExecuteNonQuery() => OnExecuteNonQuery();

        public IDataReader ExecuteReader() => throw new NotSupportedException();

        public IDataReader ExecuteReader(CommandBehavior behavior) => throw new NotSupportedException();

        public object? ExecuteScalar() => throw new NotSupportedException();

        public void Prepare()
        {
        }
    }

    private sealed class FakeDbParameter : IDbDataParameter
    {
        public DbType DbType { get; set; }

        public ParameterDirection Direction { get; set; }

        public bool IsNullable => true;

        public string ParameterName { get; set; } = string.Empty;

        public string SourceColumn { get; set; } = string.Empty;

        public DataRowVersion SourceVersion { get; set; }

        public object? Value { get; set; }

        public byte Precision { get; set; }

        public byte Scale { get; set; }

        public int Size { get; set; }
    }

    /// <summary>
    /// A <see cref="List{T}"/> of <see cref="IDataParameter"/> already implements the
    /// non-generic <c>IList</c>/<c>ICollection</c>/<c>IEnumerable</c> members
    /// <see cref="IDataParameterCollection"/> extends; only the name-indexed members need
    /// adding here.
    /// </summary>
    private sealed class FakeParameterCollection : List<IDataParameter>, IDataParameterCollection
    {
        public bool Contains(string parameterName) => this.Any(p => p.ParameterName == parameterName);

        public int IndexOf(string parameterName) => this.FindIndex(p => p.ParameterName == parameterName);

        public void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);
            if (index >= 0)
            {
                RemoveAt(index);
            }
        }

        public object? this[string parameterName]
        {
            get => this.First(p => p.ParameterName == parameterName).Value;
            set
            {
                var index = IndexOf(parameterName);
                if (index >= 0 && value is IDataParameter parameter)
                {
                    this[index] = parameter;
                }
            }
        }
    }

    private sealed class RecordingCommandFactory
    {
        public List<FakeDbCommand> Commands { get; } = new();

        public Func<FakeDbCommand, int> ExecuteNonQueryResult { get; set; } = _ => 1;

        public IDbCommand Create()
        {
            var command = new FakeDbCommand();
            Commands.Add(command);
            command.OnExecuteNonQuery = () => ExecuteNonQueryResult(command);
            return command;
        }
    }
}
