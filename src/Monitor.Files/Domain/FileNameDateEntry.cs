namespace Monitor.Files.Domain;

/// <summary>
/// Direct translation of the anonymous <c>{ name: string; lastModified: string }[]</c>
/// shape returned by the source's <c>getFileNamesForDateRange</c> — consumed by the
/// (not-yet-translated) Monitor.Operations bounded context for S3↔log reconciliation.
/// </summary>
public sealed record FileNameDateEntry(string Name, string LastModified);
