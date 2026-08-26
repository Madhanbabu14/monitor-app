using System.ComponentModel.DataAnnotations;
using System.Reflection;
using DbUp;
using Microsoft.Extensions.Configuration;
using Monitor.Core.Options;
using Monitor.Data.DataSources;

// Monitor.Database: a standalone DbUp console with its own deploy cadence,
// run once ahead of Monitor.Api (never at Monitor.Api runtime). It applies
// Scripts/*.sql - embedded resources, executed in filename order - against
// the SAME "used tables first" schema.sql this repo replaces:
//   Script0001_UsedTables.sql      -> users, pipelines, recovery_jobs
//   Script0002_RemainingTables.sql -> everything else (dead-but-unchanged)
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

var upgrader =
    DeployChanges.To
        .PostgresqlDatabase(connectionString)
        .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
        .LogToConsole()
        .Build();

var upgradeResult = upgrader.PerformUpgrade();

if (!upgradeResult.Successful)
{
    Console.Error.WriteLine(upgradeResult.Error);
    return -1;
}

Console.WriteLine("Monitor.Database: migrations applied successfully.");
return 0;
