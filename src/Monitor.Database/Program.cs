using System.ComponentModel.DataAnnotations;
using System.Reflection;
using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Configuration;
using Monitor.Core.Options;
using Monitor.Data.DataSources;
using Monitor.Database.Seed;

// Monitor.Database: a standalone DbUp console with its own deploy cadence,
// run once ahead of Monitor.Api (never at Monitor.Api runtime). It applies
// Scripts/*.sql - embedded resources, executed in filename order - against
// the SAME "used tables first" schema.sql this repo replaces:
//   Script0001_UsedTables.sql      -> users, pipelines, recovery_jobs
//   Script0002_RemainingTables.sql -> everything else (dead-but-unchanged)
//
// Files whose name starts with "Seed" (Scripts/Seed0001_DevPipelinesAndRuns.sql,
// plus the code-based Seed0002_DevUserCredentials below) are the translation
// of the source repo's database/seeds/seed.sql. They are dev-only: included
// in this run ONLY when DOTNET_ENVIRONMENT=Development, so production and
// staging deploys never see demo data or demo accounts.
//
// Connection info is bound from the same Database:Primary:* configuration
// shape as Monitor.Api (appsettings.json / environment variables such as
// Database__Primary__ConnectionString / command-line args), and run through
// the shared NpgsqlDataSourceFactory so this tool honours the exact same
// SslMode=VerifyFull-by-default + optional CA bundle policy as the API -
// there is no separate, looser TLS path for migrations.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")}.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var databaseOptions = new DatabaseOptions();
configuration.GetSection(DatabaseOptions.SectionName).Bind(databaseOptions);

var validationResults = databaseOptions.Validate(new ValidationContext(databaseOptions)).ToList();
if (string.IsNullOrWhiteSpace(databaseOptions.Primary.ConnectionString))
{
    validationResults.Add(new ValidationResult(
        $"{DatabaseOptions.SectionName}:Primary:ConnectionString is required to run migrations.",
        new[] { "Primary.ConnectionString" }));
}

if (validationResults.Count > 0)
{
    foreach (var result in validationResults)
    {
        Console.Error.WriteLine($"Configuration error: {result}");
    }

    return 1;
}

using var dataSource = NpgsqlDataSourceFactory.CreatePrimary(databaseOptions.Primary);
var connectionString = dataSource.ConnectionString;

EnsureDatabase.For.PostgresqlDatabase(connectionString);

// Dev-only seed gate: no hard-coded credential or demo data ever ships to a
// non-Development environment. Mirrors the same DOTNET_ENVIRONMENT signal
// already used above to pick appsettings.{ENV}.json.
var isDevelopment = string.Equals(
    Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
    "Development",
    StringComparison.OrdinalIgnoreCase);

var upgraderBuilder =
    DeployChanges.To
        .PostgresqlDatabase(connectionString)
        // Schema scripts (Script000N_*) always run. Seed scripts (Seed000N_*)
        // are the source's seed.sql translation and only run in Development.
        .WithScriptsEmbeddedInAssembly(
            Assembly.GetExecutingAssembly(),
            scriptName => isDevelopment || !ScriptNameIndicatesSeedData(scriptName))
        .LogToConsole();

if (isDevelopment)
{
    // Code-based seed step (can't be a plain .sql file: it generates a fresh
    // random password and bcrypt-hashes it per user, per run - see
    // Seed/DevUserSeedScript.cs for why "no hard-coded credentials, forced
    // first-login reset" needs C#, not SQL). Named so it sorts after both
    // Script0001_UsedTables.sql (creates the users table) and
    // Seed0001_DevPipelinesAndRuns.sql in DbUp's ordinal journal ordering.
    upgraderBuilder = upgraderBuilder.WithScripts(
        _ => "Seed0002_DevUserCredentials",
        new IScript[] { new DevUserSeedScript() });
}

var upgrader = upgraderBuilder.Build();

var upgradeResult = upgrader.PerformUpgrade();

if (!upgradeResult.Successful)
{
    Console.Error.WriteLine(upgradeResult.Error);
    return -1;
}

Console.WriteLine("Monitor.Database: migrations applied successfully.");
return 0;

// Embedded resource names look like "Monitor.Database.Scripts.Seed0001_DevPipelinesAndRuns.sql".
// Matching on ".Scripts.Seed" (rather than a bare "Seed" substring) avoids
// accidentally excluding a future non-seed script that merely contains the
// word "seed" somewhere else in its name.
static bool ScriptNameIndicatesSeedData(string scriptName) =>
    scriptName.Contains(".Scripts.Seed", StringComparison.OrdinalIgnoreCase);
