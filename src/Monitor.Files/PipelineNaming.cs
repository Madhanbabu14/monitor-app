using System.Text.RegularExpressions;

namespace Monitor.Files;

/// <summary>
/// Pure regex helpers translated verbatim from features/s3/s3.service.ts's
/// module-level <c>derivePipelineName</c>, <c>extractDateFromFilename</c>, and the
/// per-item logic inside <c>extractSuggestions</c>. Kept as static, side-effect-free
/// functions (no S3/DB dependency) so they can be unit-tested directly against the
/// same filename fixtures as the source.
/// </summary>
public static class PipelineNaming
{
    // Source: /^(.+?)_\d{8,}/ — non-greedy prefix, then an underscore, then 8+ digits.
    // e.g. enrolment_000000009718_20260629010224.csv -> "enrolment"
    private static readonly Regex PipelineNamePattern = new(@"^(.+?)_\d{8,}", RegexOptions.Compiled);

    // Source: /(20\d{2})(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\d{4,}/ — must look like a
    // realistic yyyyMMdd (year 20xx, month 01-12, day 01-31) followed by 4+ more digits,
    // so it skips plain record IDs like 000000009718.
    private static readonly Regex DatePattern = new(@"(20\d{2})(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\d{4,}", RegexOptions.Compiled);

    // Source: /\.[^.]+$/ — strips a trailing ".ext".
    private static readonly Regex ExtensionPattern = new(@"\.[^.]+$", RegexOptions.Compiled);

    /// <summary>Derive pipeline name from filename: enrolment_000000009718_... -&gt; enrolment.</summary>
    public static string? DerivePipelineName(string fileName)
    {
        var match = PipelineNamePattern.Match(fileName);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Extract date from filename, e.g. ..._20260629010224.csv -&gt; "2026-06-29".</summary>
    public static string? ExtractDateFromFilename(string fileName)
    {
        var match = DatePattern.Match(fileName);
        if (!match.Success) return null;
        return $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}";
    }

    /// <summary>
    /// Per-item value fed into <c>extractSuggestions</c>'s dedup set: the derived
    /// pipeline name, or (if none) the filename with its extension stripped. Never
    /// null — callers should skip adding it to the set only when it's empty, exactly
    /// like the source's <c>if (suggestion) seen.add(suggestion)</c>.
    /// </summary>
    public static string SuggestionFor(string fileName)
    {
        var match = PipelineNamePattern.Match(fileName);
        return match.Success ? match.Groups[1].Value : ExtensionPattern.Replace(fileName, string.Empty);
    }
}
