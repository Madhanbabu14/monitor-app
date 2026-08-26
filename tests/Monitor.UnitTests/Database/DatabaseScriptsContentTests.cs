using System.Text.RegularExpressions;

namespace Monitor.UnitTests.Database;

/// <summary>
/// Asserts the checked-in Scripts/*.sql content against the source of truth this migration
/// is meant to reproduce exactly (nj-01/database/schema.sql), just split into "used tables"
/// (Script0001: users, pipelines, recovery_jobs - the only tables the live /api/auth,
/// /api/s3, /api/monitor code paths touch) and "everything else, unchanged" (Script0002).
///
/// The expected table sets below were transcribed directly from the source schema.sql; this
/// intentionally does NOT read the source file at test time so the test has no cross-repo
/// path dependency and fails loudly (rather than silently no-op'ing) if either script drifts.
/// </summary>
public class DatabaseScriptsContentTests
{
    private static readonly string[] UsedTables = { "users", "pipelines", "recovery_jobs" };

    private static readonly string[] RemainingTables =
    {
        "pipeline_runs",
        "activities",
        "pipeline_errors",
        "pipeline_mappings",
        "validation_results",
        "pipeline_run_files",
        "audit_logs",
    };

    private static string Script0001() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Database", "Scripts", "Script0001_UsedTables.sql"));

    private static string Script0002() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Database", "Scripts", "Script0002_RemainingTables.sql"));

    private static IReadOnlyList<string> ExtractCreateTableNames(string sql) =>
        Regex.Matches(sql, @"CREATE TABLE\s+(\w+)\s*\(", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .ToList();

    [Fact]
    public void Script0001_CreatesExactlyTheUsedTables_InOrder()
    {
        var tables = ExtractCreateTableNames(Script0001());

        Assert.Equal(UsedTables, tables);
    }

    [Fact]
    public void Script0002_CreatesExactlyTheRemainingTables_InSourceOrder()
    {
        var tables = ExtractCreateTableNames(Script0002());

        Assert.Equal(RemainingTables, tables);
    }

    [Fact]
    public void CombinedScripts_CreateTheSameTableSet_AsTheOriginalSourceSchema_NoneMissingNoneExtra()
    {
        var combined = ExtractCreateTableNames(Script0001()).Concat(ExtractCreateTableNames(Script0002())).ToList();
        var expected = UsedTables.Concat(RemainingTables).ToList();

        Assert.Equal(expected.Count, combined.Count);
        Assert.Equal(expected.ToHashSet(), combined.ToHashSet());
    }

    [Fact]
    public void UsedTables_AllAppearInScript0001_NotScript0002()
    {
        var script1 = Script0001();
        var script2 = Script0002();

        foreach (var table in UsedTables)
        {
            Assert.Contains($"CREATE TABLE {table}", script1, StringComparison.Ordinal);
            Assert.DoesNotContain($"CREATE TABLE {table}", script2, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RemainingTables_AllAppearInScript0002_NotScript0001()
    {
        var script1 = Script0001();
        var script2 = Script0002();

        foreach (var table in RemainingTables)
        {
            Assert.Contains($"CREATE TABLE {table}", script2, StringComparison.Ordinal);
            Assert.DoesNotContain($"CREATE TABLE {table}", script1, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Script0001_CreatesRequiredExtensions()
    {
        var script = Script0001();

        Assert.Contains("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS \"pg_trgm\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script0002_DoesNotRedeclareExtensions()
    {
        // Extensions are created once, in Script0001; DbUp runs each script exactly once,
        // but duplicating "CREATE EXTENSION IF NOT EXISTS" would still be silently harmless -
        // guard against it anyway so a future edit doesn't grow duplicate DDL across scripts.
        var script = Script0002();

        Assert.DoesNotContain("CREATE EXTENSION", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatedAtTriggerFunction_IsDefinedExactlyOnce_InScript0001()
    {
        var script1 = Script0001();
        var script2 = Script0002();

        Assert.Contains("CREATE OR REPLACE FUNCTION update_updated_at_column()", script1, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE OR REPLACE FUNCTION update_updated_at_column()", script2, StringComparison.Ordinal);
    }

    [Fact]
    public void Script0001_CreatesUpdatedAtTriggers_ForUsersAndPipelinesOnly()
    {
        var script = Script0001();

        Assert.Contains("CREATE TRIGGER update_users_updated_at", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER update_pipelines_updated_at", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TRIGGER update_pipeline_mappings_updated_at", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script0002_CreatesUpdatedAtTrigger_ForPipelineMappings()
    {
        // pipeline_mappings lives in Script0002 (it's not a "used" table) but its trigger
        // still needs the shared function from Script0001 - this only works because
        // Script0001 runs first (see EmbeddedScriptNames_SortUsedTablesScriptBeforeRemainingTablesScript).
        var script = Script0002();

        Assert.Contains("CREATE TRIGGER update_pipeline_mappings_updated_at", script, StringComparison.Ordinal);
        Assert.Contains(
            "EXECUTE FUNCTION update_updated_at_column()",
            script,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("recovery_jobs", "idx_recovery_jobs_pipeline_name")]
    [InlineData("recovery_jobs", "idx_recovery_jobs_started_at")]
    public void Script0001_CreatesExpectedIndexes(string table, string indexName)
    {
        var script = Script0001();

        Assert.Contains($"CREATE INDEX {indexName} ON {table}", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pipeline_runs", "idx_pipeline_runs_pipeline_name")]
    [InlineData("activities", "idx_activities_pipeline_run_id")]
    [InlineData("pipeline_errors", "idx_pipeline_errors_run_id")]
    [InlineData("validation_results", "idx_validation_results_pipeline_name")]
    [InlineData("pipeline_run_files", "idx_pipeline_run_files_file_name")]
    [InlineData("audit_logs", "idx_audit_logs_action")]
    public void Script0002_CreatesExpectedIndexes(string table, string indexName)
    {
        var script = Script0002();

        Assert.Contains($"CREATE INDEX {indexName} ON {table}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UsersTable_RolesCheckConstraint_MatchesSource()
    {
        var script = Script0001();

        Assert.Contains(
            "CHECK (role IN ('Admin', 'Operator', 'Viewer'))",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryJobsTable_StatusCheckConstraint_MatchesSource()
    {
        var script = Script0001();

        Assert.Contains(
            "CHECK (status IN ('Pending','Running','Success','Failed','Partial'))",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineRunsTable_ReferencesPipelines_WithSetNullOnDelete()
    {
        // pipeline_runs (Script0002) has a foreign key into pipelines (Script0001) - only
        // valid at all if Script0001 has already run, reinforcing the ordering requirement.
        var script = Script0002();

        Assert.Contains(
            "pipeline_id     UUID REFERENCES pipelines(id) ON DELETE SET NULL",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesTable_ReferencesPipelineRuns_WithCascadeOnDelete()
    {
        var script = Script0002();

        Assert.Contains(
            "pipeline_run_id  UUID NOT NULL REFERENCES pipeline_runs(id) ON DELETE CASCADE",
            script,
            StringComparison.Ordinal);
    }
}
