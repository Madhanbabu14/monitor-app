namespace Monitor.Files.Domain;

/// <summary>Direct translation of the source's <c>ListFilesParams</c> interface (s3.service.ts).</summary>
public sealed record ListFilesParams(
    string? Prefix = null,
    string? Search = null,
    string? StartDate = null,
    string? EndDate = null,
    int Page = 1,
    int Limit = 20,
    string SortBy = "lastModified",
    string SortOrder = "desc");
