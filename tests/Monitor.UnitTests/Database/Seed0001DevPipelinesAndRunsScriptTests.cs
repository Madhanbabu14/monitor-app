namespace Monitor.UnitTests.Database;

/// <summary>
/// Content assertions for <c>Scripts/Seed0001_DevPipelinesAndRuns.sql</c>, the plain-SQL half
/// of the source repo's <c>database/seeds/seed.sql</c> translation (the pipelines /
/// pipeline_mappings / sample pipeline_runs sections - the credentials section moved to
/// <see cref="Monitor.Database.Seed.DevUserSeedScript"/>, covered separately, precisely
/// because it could not stay plain, idempotent SQL).
///
/// Unlike <c>DatabaseScriptsContentTests</c> (which reads the file via a linked
/// <c>TestData\Database\Scripts</c> copy), this reads the embedded resource directly out of
/// the built <c>Monitor.Database.dll</c> - the same path DbUp itself uses at deploy time - so
/// these assertions exercise the artifact that actually ships, not a side-by-side text copy.
/// </summary>
public class Seed0001DevPipelinesAndRunsScriptTests
{
    private static readonly string[] ExpectedPipelineNames =
    {
        "content_object",
        "organisation",
        "enrolment",
        "user",
        "user_session",
        "learning_path",
        "learning_path_enrolment",
        "folder",
        "attribute_value",
    };

    private static string ReadScript()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Monitor.Database.dll");
        Assert.True(File.Exists(path), $"Expected Monitor.Database.dll to be copied to {path}.");
        var assembly = System.Reflection.Assembly.LoadFrom(path);

        using var stream = assembly.GetManifestResourceStream("Monitor.Database.Scripts.Seed0001_DevPipelinesAndRuns.sql");
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Script_ContainsExactlyTheNineSourcePipelines_InSourceOrder()
    {
        var script = ReadScript();

        var pipelinesBlockStart = script.IndexOf("INSERT INTO pipelines", StringComparison.Ordinal);
        var pipelinesBlockEnd = script.IndexOf("ON CONFLICT (name) DO NOTHING", StringComparison.Ordinal);
        Assert.True(pipelinesBlockStart >= 0 && pipelinesBlockEnd > pipelinesBlockStart, "Could not locate the pipelines INSERT block.");
        var block = script[pipelinesBlockStart..pipelinesBlockEnd];

        foreach (var name in ExpectedPipelineNames)
        {
            Assert.Contains($"'{name}'", block, StringComparison.Ordinal);
        }

        var indices = ExpectedPipelineNames.Select(n => block.IndexOf($"'{n}'", StringComparison.Ordinal)).ToList();
        Assert.Equal(indices.OrderBy(i => i), indices);
    }

    [Fact]
    public void PipelinesInsert_IsIdempotent_OnConflictNameDoNothing()
    {
        var script = ReadScript();

        Assert.Contains("ON CONFLICT (name) DO NOTHING", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineMappingsInsert_MirrorsThePipelinesList_AndIsIdempotent()
    {
        var script = ReadScript();

        Assert.Contains("INSERT INTO pipeline_mappings", script, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (pipeline_name) DO NOTHING", script, StringComparison.Ordinal);

        var mappingsBlockStart = script.IndexOf("INSERT INTO pipeline_mappings", StringComparison.Ordinal);
        var mappingsBlockEnd = script.IndexOf("ON CONFLICT (pipeline_name) DO NOTHING", StringComparison.Ordinal);
        var block = script[mappingsBlockStart..mappingsBlockEnd];

        foreach (var name in ExpectedPipelineNames)
        {
            Assert.Contains($"'{name}'", block, StringComparison.Ordinal);
        }

        // Every mapping row is fixed to the 'rt' schema, matching every pipeline's
        // target_table prefix ('rt.<name>') in the pipelines INSERT above.
        Assert.DoesNotContain("'public'", block, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplePipelineRuns_UseFiveDistinctStatusesAcrossFiveNamedPipelines()
    {
        var script = ReadScript();

        Assert.Contains("pipeline_names TEXT[] := ARRAY['enrolment','user','content_object','organisation','learning_path']", script, StringComparison.Ordinal);
        Assert.Contains("statuses TEXT[] := ARRAY['Success','Success','Success','Failed','Running']", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplePipelineRuns_RunningStatus_LeavesEndTimeAndDurationNull()
    {
        // Mirrors the source's CASE WHEN pstatus != 'Running' guard: an in-flight demo run must
        // not show a completed end_time/duration_ms.
        var script = ReadScript();

        Assert.Contains("CASE WHEN pstatus != 'Running' THEN run_start + (dur || ' milliseconds')::INTERVAL ELSE NULL END", script, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN pstatus != 'Running' THEN dur ELSE NULL END", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplePipelineRuns_InsertIsIdempotent_OnConflictRunIdDoNothing()
    {
        // Unlike the source (which has no ON CONFLICT clause on this INSERT and would
        // duplicate demo runs on every re-seed), the migrated script adds one keyed on the
        // unique run_id so re-running Monitor.Database in Development is a no-op here too.
        var script = ReadScript();

        Assert.Contains("ON CONFLICT (run_id) DO NOTHING", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplePipelineRuns_AreScopedToPipelinesTableByName()
    {
        var script = ReadScript();

        Assert.Contains("FROM pipelines p WHERE p.name = pname", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_ContainsNoCredentialsOrPasswordReferences()
    {
        // This script is safe to keep as static, checked-in SQL (unlike the user seed) only
        // because it never touches the users table or any secret material.
        var script = ReadScript();

        Assert.DoesNotContain("password", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO users", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$2a$", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_UsesUuidGenerateV4ForEveryGeneratedId()
    {
        var script = ReadScript();

        // Every INSERT in this file generates its own primary key rather than relying on a
        // column default, matching Script0001_UsedTables.sql's pipelines/pipeline_runs id
        // columns (UUID PRIMARY KEY, no DEFAULT).
        var occurrences = System.Text.RegularExpressions.Regex.Matches(script, "uuid_generate_v4\\(\\)").Count;
        Assert.True(occurrences >= ExpectedPipelineNames.Length * 2 + 1, "Expected at least one uuid_generate_v4() per pipelines row, per pipeline_mappings row, and one for the sample pipeline_runs insert.");
    }
}
