using Monitor.Files;
using Monitor.Files.Domain;

namespace Monitor.UnitTests.Files;

/// <summary>
/// Exercises <see cref="PipelineNameDeriver"/> — the direct translation of the
/// three regex-based free functions at the top of s3.service.ts
/// (extractSuggestions, derivePipelineName, extractDateFromFilename).
/// </summary>
public class PipelineNameDeriverTests
{
    [Theory]
    [InlineData("enrolment_000000009718_20260629010224.csv", "enrolment")]
    [InlineData("claims_2026_01_02_99999999.csv", "claims_2026_01_02")]
    [InlineData("no-underscore-digits.csv", null)]
    [InlineData("short_1234.csv", null)] // fewer than 8 digits after underscore
    public void DerivePipelineName_MatchesSourceRegex(string fileName, string? expected)
    {
        Assert.Equal(expected, PipelineNameDeriver.DerivePipelineName(fileName));
    }

    [Theory]
    [InlineData("enrolment_000000009718_20260629010224.csv", "2026-06-29")]
    [InlineData("enrolment_000000009718.csv", null)] // no realistic date embedded
    [InlineData("claims_19990101010101.csv", null)] // no "20xx" year prefix present
    public void ExtractDateFromFilename_MatchesSourceRegex(string fileName, string? expected)
    {
        Assert.Equal(expected, PipelineNameDeriver.ExtractDateFromFilename(fileName));
    }

    [Fact]
    public void ExtractSuggestions_DerivesFromPatternOrStripsExtension_AndSortsDistinct()
    {
        var items = new[]
        {
            new S3FileItem("k1", "enrolment_000000009718_20260629010224.csv", "", 1, "2026-06-29T00:00:00.000Z", "e1", "enrolment"),
            new S3FileItem("k2", "enrolment_000000009999_20260630010224.csv", "", 1, "2026-06-30T00:00:00.000Z", "e2", "enrolment"),
            new S3FileItem("k3", "readme.txt", "", 1, "2026-06-30T00:00:00.000Z", "e3", null),
        };

        var suggestions = PipelineNameDeriver.ExtractSuggestions(items);

        Assert.Equal(new[] { "enrolment", "readme" }, suggestions);
    }

    [Theory]
    [InlineData("enrolment_data", "Enrolment Data")]
    [InlineData("claims", "Claims")]
    public void ToDisplayName_TitleCasesUnderscoreSeparatedName(string pipelineName, string expected)
    {
        Assert.Equal(expected, PipelineNameDeriver.ToDisplayName(pipelineName));
    }
}
