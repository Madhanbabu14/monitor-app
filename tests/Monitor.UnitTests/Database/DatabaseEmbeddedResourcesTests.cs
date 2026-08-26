using System.Reflection;

namespace Monitor.UnitTests.Database;

/// <summary>
/// Monitor.Database's Program.cs runs
/// <c>DeployChanges.To.PostgresqlDatabase(...).WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())</c>,
/// which discovers scripts purely from embedded-resource names and, by DbUp's default
/// <see cref="DbUp.Engine.Filters.DefaultScriptExecutionOrder"/>, runs them in ordinal
/// (alphabetical) order. These tests pin down the actual embedded-resource surface of the
/// built Monitor.Database.dll so that "used tables first" (Script0001 before Script0002) is
/// enforced by naming, not by a comment that can rot.
/// </summary>
public class DatabaseEmbeddedResourcesTests
{
    private static Assembly LoadDatabaseAssembly()
    {
        // Monitor.Database.dll is copied next to this test assembly because
        // Monitor.UnitTests.csproj carries a ProjectReference to it.
        var path = Path.Combine(AppContext.BaseDirectory, "Monitor.Database.dll");
        Assert.True(File.Exists(path), $"Expected Monitor.Database.dll to be copied to {path}.");
        return Assembly.LoadFrom(path);
    }

    [Fact]
    public void EmbeddedResources_ContainExactlyTheTwoExpectedScripts()
    {
        var assembly = LoadDatabaseAssembly();

        var sqlResources = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Monitor.Database.Scripts.Script0001_UsedTables.sql",
                "Monitor.Database.Scripts.Script0002_RemainingTables.sql",
            },
            sqlResources);
    }

    [Fact]
    public void EmbeddedScriptNames_SortUsedTablesScriptBeforeRemainingTablesScript()
    {
        // DbUp's default execution order is a plain ordinal sort of script names - this is
        // what actually guarantees "used tables first", independent of any comment saying so.
        var assembly = LoadDatabaseAssembly();
        var names = assembly.GetManifestResourceNames();

        var usedTables = Assert.Single(names, n => n.Contains("Script0001_UsedTables", StringComparison.Ordinal));
        var remainingTables = Assert.Single(names, n => n.Contains("Script0002_RemainingTables", StringComparison.Ordinal));

        Assert.True(
            string.CompareOrdinal(usedTables, remainingTables) < 0,
            "Script0001_UsedTables must sort before Script0002_RemainingTables under an ordinal comparison.");
    }

    [Fact]
    public void NoUnexpectedScriptsAreEmbedded()
    {
        // Regression guard: a stray Scripts/*.sql file dropped in later must show up here
        // rather than silently being picked up (or not) by DbUp at deploy time.
        var assembly = LoadDatabaseAssembly();

        var sqlResourceCount = assembly.GetManifestResourceNames()
            .Count(name => name.EndsWith(".sql", StringComparison.Ordinal));

        Assert.Equal(2, sqlResourceCount);
    }

    [Fact]
    public void EmbeddedScriptContent_IsReadableAndNonEmpty()
    {
        var assembly = LoadDatabaseAssembly();

        foreach (var resourceName in new[]
                 {
                     "Monitor.Database.Scripts.Script0001_UsedTables.sql",
                     "Monitor.Database.Scripts.Script0002_RemainingTables.sql",
                 })
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);

            using var reader = new StreamReader(stream!);
            var content = reader.ReadToEnd();

            Assert.False(string.IsNullOrWhiteSpace(content));
            Assert.Contains("CREATE TABLE", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExecutableAssemblyName_IsMonitorDatabase()
    {
        // Program.cs calls Assembly.GetExecutingAssembly() (not a hardcoded name) to locate
        // the embedded scripts; pinning the assembly name guards against a rename silently
        // breaking that self-reference.
        var assembly = LoadDatabaseAssembly();

        Assert.Equal("Monitor.Database", assembly.GetName().Name);
    }
}
