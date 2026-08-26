using System.Diagnostics;

namespace Monitor.UnitTests.Database;

/// <summary>
/// Monitor.Database's Program.cs is top-level statements in a console Exe - there is no
/// public method to call in-process without also running DbUp's real
/// <c>EnsureDatabase</c>/<c>PerformUpgrade</c> against Postgres. These tests instead launch
/// the actual built Monitor.Database.dll as a child process (via <c>dotnet exec</c>) and
/// assert its observable contract: exit code and stderr/stdout, driven purely through the
/// same configuration surface (environment variables) Program.cs itself reads. No real
/// Postgres server is required for the scenarios that matter most - the fail-fast
/// configuration-validation path runs entirely before Program.cs ever touches the network.
/// </summary>
public class DatabaseMigratorProcessTests
{
    private static string DatabaseDllPath()
    {
        // Deliberately NOT the copy of Monitor.Database.dll that lands next to this test
        // assembly via the ProjectReference: MSBuild's cross-project output-folder merge
        // dedupes some of Monitor.Database's own dependencies (e.g. Microsoft.Extensions.
        // Configuration.dll) against the higher versions Monitor.Api/ASP.NET Core pull in
        // elsewhere in this shared test bin folder, without actually copying the physical
        // files Monitor.Database.deps.json still expects to find there - which makes the
        // *copied* Monitor.Database.dll unable to start standalone. Monitor.Database's own
        // build output (src/Monitor.Database/bin/**/net6.0/Monitor.Database.dll) does not
        // have that problem, so run the real one from there instead.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Monitor.Database")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root above " + AppContext.BaseDirectory);

        var candidates = Directory.GetFiles(
            Path.Combine(dir!.FullName, "src", "Monitor.Database", "bin"),
            "Monitor.Database.dll",
            SearchOption.AllDirectories);

        Assert.True(candidates.Length > 0, "Could not find a built Monitor.Database.dll under src/Monitor.Database/bin. Build the solution first.");

        // If both Debug and Release happen to be built, prefer whichever was built most recently.
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(
        IDictionary<string, string> environmentOverrides,
        string[]? args = null,
        int timeoutMs = 20_000)
    {
        var dllPath = DatabaseDllPath();
        Assert.True(File.Exists(dllPath), $"Expected {dllPath} to exist. Build src/Monitor.Database first.");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dllPath)!,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dllPath);
        foreach (var arg in args ?? Array.Empty<string>())
        {
            psi.ArgumentList.Add(arg);
        }

        // Force a clean, known environment: no ambient DOTNET_ENVIRONMENT (which would pull in
        // the shipped appsettings.Development.json's real-looking Postgres connection string
        // and attempt a genuine network connection), and roll forward onto whatever
        // Microsoft.NETCore.App major version is actually installed on the machine running
        // the tests (the shipped project targets net6.0; this keeps the process test portable
        // across machines that only have a newer runtime installed side by side).
        psi.Environment["DOTNET_ENVIRONMENT"] = "UnitTestNoSuchEnvironment";
        psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        foreach (var (key, value) in environmentOverrides)
        {
            psi.Environment[key] = value;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet exec process.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Monitor.Database process did not exit within {timeoutMs}ms.");
        }

        // Process is exited; the async reads complete promptly.
        Task.WaitAll(stdOutTask, stdErrTask);

        return (process.ExitCode, stdOutTask.Result, stdErrTask.Result);
    }

    [Fact]
    public void BlankConnectionString_FromShippedAppsettingsAlone_ExitsWithCode1_AndReportsBothErrors()
    {
        // No overrides at all: the shipped appsettings.json ships an empty
        // Database:Primary:ConnectionString, and our forced DOTNET_ENVIRONMENT means the
        // Development overlay never applies. This is the "fresh checkout, no env configured"
        // scenario Program.cs's fail-fast validation exists for.
        var result = Run(new Dictionary<string, string>());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration error", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("ConnectionString field is required", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("Database:Primary:ConnectionString is required to run migrations", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void WhitespaceOnlyConnectionString_ViaEnvironmentVariable_ExitsWithCode1()
    {
        var result = Run(new Dictionary<string, string>
        {
            ["Database__Primary__ConnectionString"] = "   ",
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Database:Primary:ConnectionString is required to run migrations", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidMaxConnections_ExitsWithCode1_WithoutAttemptingADatabaseConnection()
    {
        // A syntactically fine connection string but an out-of-range MaxConnections must
        // still fail in the configuration-validation step - before EnsureDatabase ever runs -
        // so this stays fast and network-free even though the host looks connectable.
        var result = Run(new Dictionary<string, string>
        {
            ["Database__Primary__ConnectionString"] = "Host=localhost;Database=db;Username=u;Password=p",
            ["Database__Primary__MaxConnections"] = "0",
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration error", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("MaxConnections must be between 1 and", result.StdErr, StringComparison.Ordinal);
        // The "field is required"/blank-connection-string message must NOT appear - the
        // connection string itself was valid.
        Assert.DoesNotContain("Database:Primary:ConnectionString is required to run migrations", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeIdleTimeout_ExitsWithCode1()
    {
        var result = Run(new Dictionary<string, string>
        {
            ["Database__Primary__ConnectionString"] = "Host=localhost;Database=db",
            ["Database__Primary__IdleTimeoutMs"] = "-1",
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("IdleTimeoutMs must be between 0 and", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineArgument_CanSupplyConnectionString_PassingConfigValidation()
    {
        // A syntactically valid but unroutable connection string (TEST-NET-1, RFC 5737) so
        // this exercises "config validation passes" without this test depending on which
        // exception shape a refused-connection failure takes on the host running it. We only
        // assert it gets PAST validation (i.e. it does not print the blank/required error).
        var result = Run(
            environmentOverrides: new Dictionary<string, string>(),
            args: new[] { "--Database:Primary:ConnectionString=Host=192.0.2.1;Database=db;Timeout=1", "--Database:Primary:ConnectionTimeoutMs=200" },
            timeoutMs: 10_000);

        Assert.DoesNotContain("Database:Primary:ConnectionString is required to run migrations", result.StdErr, StringComparison.Ordinal);
        // Whatever happens next (connection timeout/refusal against an unroutable test
        // address) is not a clean validation failure, so the process must not exit 1 here.
        Assert.NotEqual(1, result.ExitCode);
    }

    [Fact]
    public void UnreachableButValidConnectionString_DoesNotExitCleanly()
    {
        // Documents current behaviour: EnsureDatabase() (and PerformUpgrade()) are not
        // wrapped in a try/catch in Program.cs, so a connection failure at that stage
        // surfaces as an *unhandled exception* (non-zero/aborting exit code) rather than the
        // same clean "Console.Error + return 1/-1" path used for configuration errors and
        // failed upgrades. This test only pins "it does not report success and does not exit
        // 0" - it deliberately avoids asserting an exact process exit code because unhandled
        // .NET exceptions can surface as different codes across platforms.
        var result = Run(new Dictionary<string, string>
        {
            ["Database__Primary__ConnectionString"] = "Host=127.0.0.1;Port=1;Database=nonexistent;Username=u;Password=p;Timeout=2",
            ["Database__Primary__SslMode"] = "Disable",
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("migrations applied successfully", result.StdOut, StringComparison.Ordinal);
    }
}
