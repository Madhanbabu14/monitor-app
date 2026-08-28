namespace Monitor.Files.Domain;

/// <summary>
/// Direct translation of the anonymous <c>{ name, lastModified }</c> shape returned by
/// <c>getFileNamesForDateRange</c> (features/s3/s3.service.ts) — consumed by the
/// Monitor.Operations bounded context's S3&lt;-&gt;log reconciliation, not by this slice.
/// </summary>
public sealed record FileDateEntry(string Name, string LastModified);
