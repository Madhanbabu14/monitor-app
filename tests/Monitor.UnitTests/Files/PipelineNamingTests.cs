using Monitor.Files;

namespace Monitor.UnitTests.Files;

/// <summary>
/// Unit tests for <see cref="PipelineNaming"/> — the pure regex helpers translated
/// verbatim from features/s3/s3.service.ts's module-level <c>derivePipelineName</c>,
/// <c>extractDateFromFilename</c>, and the per-item logic inside
/// <c>extractSuggestions</c>. These are pure/side-effect-free so every case here is
/// exercised directly against the same kind of filename fixtures the source's docstring
/// examples use.
/// </summary>
public class PipelineNamingTests
{
    // ── DerivePipelineName ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("enrolment_000000009718_20260629010224.csv", "enrolment")]
    [InlineData("claims_intake_000000000001_20260101000000.csv", "claims_intake")]
    [InlineData("a_12345678", "a")] // exactly 8 digits still satisfies \d{8,}
    public void DerivePipelineName_MatchingFilenames_ReturnsPrefixBeforeUnderscoreDigitRun(string fileName, string expected)
    {
        Assert.Equal(expected, PipelineNaming.DerivePipelineName(fileName));
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("")]
    [InlineData("no_underscore_but_only_1234567")] // 7 digits: below the 8-digit floor
    [InlineData("_00000001")] // group must be non-empty (.+? requires >=1 char)
    public void DerivePipelineName_NonMatchingFilenames_ReturnsNull(string fileName)
    {
        Assert.Null(PipelineNaming.DerivePipelineName(fileName));
    }

    [Fact]
    public void DerivePipelineName_NonGreedyGroup_StopsAtFirstUnderscoreFollowedByEightDigits()
    {
        // The lazy `.+?` expands the minimum number of characters necessary; here the
        // first underscore ("daily") is followed by "report_..." (not digits), so the
        // group must expand all the way to "daily_report" before "_20260101" satisfies
        // the \d{8,} tail.
        Assert.Equal("daily_report", PipelineNaming.DerivePipelineName("daily_report_20260101_00000001.csv"));
    }

    [Fact]
    public void DerivePipelineName_DigitsImmediatelyAfterUnderscoreButFewerThanEight_KeepsScanning()
    {
        // "seg_1234567" (7 digits) does not satisfy \d{8,}; the lazy group must keep
        // expanding past it to the next underscore that is followed by 8+ digits.
        Assert.Equal("seg_1234567_pipeline", PipelineNaming.DerivePipelineName("seg_1234567_pipeline_20260101.csv"));
    }

    // ── ExtractDateFromFilename ─────────────────────────────────────────────────

    [Theory]
    [InlineData("enrolment_000000009718_20260629010224.csv", "2026-06-29")]
    [InlineData("report_202606290001.csv", "2026-06-29")] // exactly 4 trailing digits (the floor)
    [InlineData("x_20260101999999999.dat", "2026-01-01")]
    public void ExtractDateFromFilename_RealisticDateFollowedByFourOrMoreDigits_ExtractsIsoDate(string fileName, string expected)
    {
        Assert.Equal(expected, PipelineNaming.ExtractDateFromFilename(fileName));
    }

    [Theory]
    [InlineData("report_20260630.csv")] // exactly 8 digits, no >=4-digit tail
    [InlineData("report_202606300.csv")] // only 1 trailing digit
    [InlineData("seq_000000009718.csv")] // looks like a record id, not 20xx-prefixed
    [InlineData("plainname.csv")]
    [InlineData("")]
    public void ExtractDateFromFilename_NoRealisticDatePattern_ReturnsNull(string fileName)
    {
        Assert.Null(PipelineNaming.ExtractDateFromFilename(fileName));
    }

    [Theory]
    [InlineData("bad_19991231000000.csv")] // year not 20xx
    [InlineData("bad_20261301000000.csv")] // month 13 invalid
    [InlineData("bad_20260132000000.csv")] // day 32 invalid
    public void ExtractDateFromFilename_OutOfRangeMonthDayOrYear_ReturnsNull(string fileName)
    {
        Assert.Null(PipelineNaming.ExtractDateFromFilename(fileName));
    }

    [Fact]
    public void ExtractDateFromFilename_December31_IsAcceptedAsAValidBoundary()
    {
        Assert.Equal("2026-12-31", PipelineNaming.ExtractDateFromFilename("bad_20261231000000.csv"));
    }

    // ── SuggestionFor ───────────────────────────────────────────────────────────

    [Fact]
    public void SuggestionFor_WhenPipelineNameDerivable_ReturnsSameValueAsDerivePipelineName()
    {
        const string fileName = "enrolment_000000009718_20260629010224.csv";
        Assert.Equal(PipelineNaming.DerivePipelineName(fileName), PipelineNaming.SuggestionFor(fileName));
    }

    [Theory]
    [InlineData("readme.txt", "readme")]
    [InlineData("archive.tar.gz", "archive.tar")] // only the trailing extension is stripped
    [InlineData("README", "README")] // no dot at all: regex doesn't match, no-op
    [InlineData("noext.", "noext.")] // trailing bare dot has nothing after it: \.[^.]+$ needs >=1 char, so no match
    public void SuggestionFor_WhenNoPipelineNameDerivable_StripsTrailingExtensionOnly(string fileName, string expected)
    {
        Assert.Equal(expected, PipelineNaming.SuggestionFor(fileName));
    }

    [Fact]
    public void SuggestionFor_NeverReturnsNull_EvenForEmptyInput()
    {
        // Unlike DerivePipelineName, SuggestionFor's contract is "never null" - the
        // caller decides whether to skip an empty string.
        Assert.NotNull(PipelineNaming.SuggestionFor(string.Empty));
        Assert.Equal(string.Empty, PipelineNaming.SuggestionFor(string.Empty));
    }
}
