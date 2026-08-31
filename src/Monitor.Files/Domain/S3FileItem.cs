namespace Monitor.Files.Domain;

/// <summary>
/// Direct translation of the source's <c>S3FileItem</c> interface (s3.service.ts).
/// <see cref="LastModified"/> is kept as the exact ISO-8601 string the wire
/// contract expects (<c>obj.LastModified.toISOString()</c>), not a typed
/// DateTime, so JSON serialization matches byte-for-byte.
/// </summary>
public sealed record S3FileItem(
    string Key,
    string Name,
    string Prefix,
    long Size,
    string LastModified,
    string ETag,
    string? PipelineName);
