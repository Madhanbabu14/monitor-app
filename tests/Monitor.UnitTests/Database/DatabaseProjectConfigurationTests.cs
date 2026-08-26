namespace Monitor.UnitTests.Database;

/// <summary>
/// Sanity checks on Monitor.Database's own project file and its place in the solution,
/// reading the actual checked-in Monitor.Database.csproj / Monitor.sln text directly (these
/// aren't runtime config, so they aren't worth plumbing through MSBuild APIs - a plain text
/// read is both simpler and closer to "what a reviewer would grep for").
/// </summary>
public class DatabaseProjectConfigurationTests
{
    // Walk up from the test output directory to the repo's src/ folder rather than
    // hard-coding a fixed number of ".." segments tied to a specific configuration/TFM.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Monitor.Database")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (a directory containing src/Monitor.Database) above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static string ReadCsproj() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Monitor.Database", "Monitor.Database.csproj"));

    private static string ReadSln() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "Monitor.sln"));

    [Fact]
    public void Csproj_IsAConsoleExecutable_TargetingNet6()
    {
        var csproj = ReadCsproj();

        Assert.Contains("<OutputType>Exe</OutputType>", csproj, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net6.0</TargetFramework>", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Csproj_EmbedsScriptsFolder_AsEmbeddedResource_AndExcludesThemFromNone()
    {
        var csproj = ReadCsproj();

        Assert.Contains(@"<EmbeddedResource Include=""Scripts\*.sql"" />", csproj, StringComparison.Ordinal);
        Assert.Contains(@"<None Remove=""Scripts\*.sql"" />", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Csproj_ReferencesDbUpPostgresql_AndNpgsql()
    {
        var csproj = ReadCsproj();

        Assert.Contains(@"<PackageReference Include=""dbup-postgresql""", csproj, StringComparison.Ordinal);
        Assert.Contains(@"<PackageReference Include=""Npgsql""", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Csproj_ReferencesCoreAndData_ForSharedOptionsAndDataSourceFactory()
    {
        var csproj = ReadCsproj();

        Assert.Contains(@"Monitor.Core\Monitor.Core.csproj", csproj, StringComparison.Ordinal);
        Assert.Contains(@"Monitor.Data\Monitor.Data.csproj", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Csproj_CopiesBothAppsettingsFiles_ToOutputDirectory()
    {
        var csproj = ReadCsproj();

        Assert.Contains(@"<None Include=""appsettings.json"" CopyToOutputDirectory=""PreserveNewest"" />", csproj, StringComparison.Ordinal);
        Assert.Contains("appsettings.Development.json", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorApi_DoesNotProjectReference_MonitorDatabase()
    {
        // Documented invariant (also called out in Monitor.Database.csproj's own comments):
        // Monitor.Api must never apply schema changes at runtime, so it must not reference
        // this project at all.
        var apiCsprojPath = Path.Combine(FindRepoRoot(), "src", "Monitor.Api", "Monitor.Api.csproj");
        Assert.True(File.Exists(apiCsprojPath), $"Expected {apiCsprojPath} to exist.");

        var apiCsproj = File.ReadAllText(apiCsprojPath);

        Assert.DoesNotContain("Monitor.Database", apiCsproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Solution_IncludesMonitorDatabaseProject_UnderSrc()
    {
        var sln = ReadSln();

        Assert.Contains(@"""Monitor.Database"", ""src\Monitor.Database\Monitor.Database.csproj""", sln, StringComparison.Ordinal);
    }

    [Fact]
    public void Solution_BuildsMonitorDatabase_ForBothDebugAndReleaseAnyCpu()
    {
        var sln = ReadSln();
        var lines = sln.Split('\n');

        // Find Monitor.Database's project GUID from its Project(...) declaration line, then
        // confirm that GUID has both a Debug and a Release ActiveCfg entry further down.
        var declarationLine = Assert.Single(lines, l => l.Contains(@"""Monitor.Database"", ""src\Monitor.Database\Monitor.Database.csproj"""));
        var guidStart = declarationLine.LastIndexOf('{');
        var guidEnd = declarationLine.LastIndexOf('}');
        Assert.True(guidStart >= 0 && guidEnd > guidStart, "Could not find project GUID in declaration line: " + declarationLine);
        var guid = declarationLine.Substring(guidStart, guidEnd - guidStart + 1);

        Assert.Contains(lines, l => l.Contains(guid) && l.Contains("Debug|Any CPU.ActiveCfg"));
        Assert.Contains(lines, l => l.Contains(guid) && l.Contains("Release|Any CPU.ActiveCfg"));
    }
}
