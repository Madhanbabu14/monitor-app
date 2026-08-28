namespace Monitor.Files.Domain;

/// <summary>
/// Direct translation of <c>ListFilesParams</c> (features/s3/s3.service.ts), after the
/// controller-level defaulting done in <c>S3Controller.listFiles</c>
/// (page defaults to 1, limit defaults to 20 and is capped at 100, sortBy/sortOrder
/// default to 'lastModified'/'desc'). The endpoint module applies those defaults before
/// constructing this record so the service itself never sees a missing value.
/// </summary>
public sealed record ListFilesQuery(
    string? Prefix,
    string? Search,
    string? StartDate,
    string? EndDate,
    int Page,
    int Limit,
    string SortBy,
    string SortOrder);
