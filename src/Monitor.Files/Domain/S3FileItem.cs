namespace Monitor.Files.Domain;

/// <summary>
/// Direct translation of the <c>S3FileItem</c> interface (features/s3/s3.service.ts).
/// <see cref="LastModified"/> is kept as the ISO-8601 string the source produced via
/// <c>obj.LastModified?.toISOString()</c> (falling back to the epoch) rather than a
/// .NET <see cref="DateTime"/>, so date-range filtering/sorting reproduces the exact
/// same string-vs-string comparison semantics as the source's <c>new Date(...)</c> parsing.
/// </summary>
public sealed record S3FileItem(
    string Key,
    string Name,
    string Prefix,
    long Size,
    string LastModified,
    string ETag,
    string? PipelineName);
