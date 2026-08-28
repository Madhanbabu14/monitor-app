namespace Monitor.Files.Domain;

/// <summary>Direct translation of <c>ListFilesResult</c> (features/s3/s3.service.ts).</summary>
public sealed record ListFilesResult(
    IReadOnlyList<S3FileItem> Files,
    int Total,
    long TotalSize,
    int Page,
    int Limit,
    int TotalPages,
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Suggestions,
    string Bucket,
    string RootPrefix);
