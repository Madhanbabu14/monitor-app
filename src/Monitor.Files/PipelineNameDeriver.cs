using System.Globalization;
using System.Text.RegularExpressions;
using Monitor.Files.Domain;

namespace Monitor.Files;

/// <summary>
/// Direct translation of the free functions at the top of s3.service.ts:
/// <c>extractSuggestions</c>, <c>derivePipelineName</c>, <c>extractDateFromFilename</c>.
/// </summary>
public static class PipelineNameDeriver
{
    // /^(.+?)_\d{8,}/ — e.g. enrolment_000000009718_... -> enrolment
    private static readonly Regex PipelineNamePattern = new(@"^(.+?)_\d{8,}", RegexOptions.Compiled);

    // /(20\d{2})(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\d{4,}/
    // Must match a realistic year (20xx), month (01-12), day (01-31) to skip
    // record IDs like 000000009718.
    private static readonly Regex DatePattern = new(
        @"(20\d{2})(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\d{4,}",
        RegexOptions.Compiled);

    /// <summary>Derive pipeline name from filename: enrolment_000000009718_... → enrolment.</summary>
    public static string? DerivePipelineName(string fileName)
    {
        var match = PipelineNamePattern.Match(fileName);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Extract date from filename: enrolment_000000009718_20260629010224.csv → 2026-06-29.</summary>
    public static string? ExtractDateFromFilename(string fileName)
    {
        var match = DatePattern.Match(fileName);
        if (!match.Success) return null;
        return $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}";
    }

    public static IReadOnlyList<string> ExtractSuggestions(IEnumerable<S3FileItem> items)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var match = PipelineNamePattern.Match(item.Name);
            var suggestion = match.Success
                ? match.Groups[1].Value
                : Regex.Replace(item.Name, @"\.[^.]+$", string.Empty);
            if (!string.IsNullOrEmpty(suggestion)) seen.Add(suggestion);
        }
        return seen.ToList();
    }

    /// <summary>
    /// Title-cases a pipeline name for the auto-created pipeline's display_name:
    /// <c>pipelineName.replace(/_/g, ' ').replace(/\b\w/g, (c) =&gt; c.toUpperCase())</c>.
    /// </summary>
    public static string ToDisplayName(string pipelineName)
    {
        var spaced = pipelineName.Replace('_', ' ');
        return Regex.Replace(spaced, @"\b\w", m => m.Value.ToUpper(CultureInfo.InvariantCulture));
    }
}
