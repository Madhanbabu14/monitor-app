using System.Data;
using System.Security.Cryptography;
using System.Text;
using DbUp.Engine;

namespace Monitor.Database.Seed;

/// <summary>
/// Dev-only DbUp code migration that provisions the three demo accounts the
/// source repo's <c>database/seeds/seed.sql</c> hard-coded
/// (admin@company.com / operator@company.com / viewer@company.com, one per
/// <see cref="Monitor.Core.Domain.UserRole"/>) - WITHOUT the source's
/// hard-coded bcrypt hash (<c>$2a$10$B6c.W3zIC9kC9O1yE7/W4...</c>, the same
/// hash for all three users, committed to source control and therefore not a
/// real secret in any environment that ran this seed).
/// </summary>
/// <remarks>
/// Behaviour:
///  - Only registered by Program.cs when DOTNET_ENVIRONMENT=Development (see
///    the isDevelopment guard around upgrader construction). Never runs in
///    production/staging.
///  - Runs as a normal DbUp step (via <c>IScript</c>, journaled under the
///    name "Seed0002_DevUserCredentials" so it executes after
///    Script0001_UsedTables.sql creates the users table, and only ONCE per
///    database - re-running Monitor.Database on an already-seeded dev
///    database is a no-op here, same as the source's
///    "ON CONFLICT (email) DO NOTHING").
///  - For each demo user that does not already exist, generates a fresh
///    cryptographically random password (never hard-coded, never written to
///    a file/journal/log - it exists only in this process's memory and on
///    the operator's console for the duration of this run), hashes it with
///    bcrypt (work factor 10, matching users.password_hash's existing
///    "$2a$10$..." shape) and inserts the user row.
///  - "Forced first-login reset": seeded rows leave `last_login_at` at its
///    schema default (NULL - see Script0001_UsedTables.sql). Monitor.Identity
///    (a later slice) is expected to treat `last_login_at IS NULL` as "this
///    account has never completed a real login" and require a password
///    change before issuing a normal session - this script only establishes
///    that precondition; it does not (and today, before Monitor.Identity
///    exists, cannot) enforce the reset itself. No new schema/column is
///    introduced here to keep this a straight, additive dev-tooling change.
/// </remarks>
public sealed class DevUserSeedScript : IScript
{
    private static readonly (string Email, string DisplayName, string Role)[] DemoUsers =
    {
        ("admin@company.com", "Admin User", "Admin"),
        ("operator@company.com", "Ops User", "Operator"),
        ("viewer@company.com", "View User", "Viewer"),
    };

    public string ProvideScript(Func<IDbCommand> dbCommandFactory)
    {
        // NOTE: DbUp executes whatever this method returns as a SQL command
        // against the database (it is not merely a log message), so the
        // summary below is emitted as a SQL comment - safe/no-op if it
        // reaches the server - while the human-readable output (including
        // the one-time generated passwords) goes to Console.WriteLine.
        var log = new StringBuilder();
        log.AppendLine("-- Monitor.Database dev seed: provisioning demo accounts with generated (non-hard-coded) passwords.");

        foreach (var (email, displayName, role) in DemoUsers)
        {
            var password = GenerateRandomPassword();
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 10);

            using var command = dbCommandFactory();
            command.CommandText = @"
INSERT INTO users (id, email, display_name, password_hash, role, is_active)
VALUES (uuid_generate_v4(), @email, @display_name, @password_hash, @role, true)
ON CONFLICT (email) DO NOTHING";
            AddParameter(command, "email", email);
            AddParameter(command, "display_name", displayName);
            AddParameter(command, "password_hash", passwordHash);
            AddParameter(command, "role", role);

            var inserted = command.ExecuteNonQuery();
            if (inserted > 0)
            {
                // Printed exactly once, at creation time only - this is the
                // ONLY place this plaintext password is ever surfaced.
                Console.WriteLine(
                    $"[dev-seed] created {role,-8} {email,-24} password: {password}  " +
                    "(one-time - rotate this at first login)");
                log.AppendLine($"-- Created demo user {email} ({role}).");
            }
            else
            {
                log.AppendLine($"-- Demo user {email} already exists - left unchanged.");
            }
        }

        return log.ToString();
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Cryptographically random, URL-safe password - replaces the source's
    /// single shared hard-coded credential ("Admin@123" for every demo user
    /// in every environment) with a unique-per-seed-run secret.
    /// </summary>
    private static string GenerateRandomPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
