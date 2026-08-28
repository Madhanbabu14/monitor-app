using Monitor.Files.Domain;

namespace Monitor.Files;

/// <summary>
/// Direct translation of <c>S3Service</c> (features/s3/s3.service.ts). Consumed by
/// Monitor.Api's Files endpoint module directly, and by the Monitor.Operations bounded
/// context (dashboard S3&lt;-&gt;log reconciliation) via <see cref="GetFileNamesForDateRangeAsync"/>.
/// </summary>
public interface IFilesService
{
    Task<ListFilesResult> ListFilesAsync(ListFilesQuery query, CancellationToken cancellationToken);

    Task<S3DownloadResult> StreamFileAsync(string key, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken);

    Task<RetriggerResult> RetriggerFileAsync(
        string fileName,
        string s3Key,
        string? pipelineNameOverride,
        CancellationToken cancellationToken);

    /// <summary>
    /// Uses the 60-second raw-listing cache — safe to call from the dashboard and file
    /// monitor in the same session without triggering duplicate S3 API round-trips.
    /// </summary>
    Task<IReadOnlyList<FileDateEntry>> GetFileNamesForDateRangeAsync(
        string? startDate,
        string? endDate,
        CancellationToken cancellationToken);
}
