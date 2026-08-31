using Monitor.Files.Domain;

namespace Monitor.Files;

/// <summary>
/// Direct translation of <c>S3Service</c> (s3.service.ts). Backs the
/// `/api/s3` endpoints (Monitor.Api's FilesEndpoints) and is also consumed by
/// the (not-yet-translated) Monitor.Operations bounded context via
/// <see cref="GetFileNamesForDateRangeAsync"/> for dashboard S3↔log reconciliation.
/// </summary>
public interface IS3FilesService
{
    Task<ListFilesResult> ListFilesAsync(ListFilesParams parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the S3 object at <paramref name="key"/> for the download endpoint
    /// to stream to the caller. Direct translation of <c>streamFile</c>
    /// (s3.service.ts) minus the response-writing, which lives in the endpoint.
    /// Throws <see cref="Monitor.Core.Errors.AppException"/> (via
    /// <see cref="S3ErrorMapper"/>) on any S3 failure, and 404 if the body is empty.
    /// </summary>
    Task<S3FileDownload> GetFileDownloadAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken = default);

    Task<RetriggerResult> RetriggerFileAsync(string fileName, string s3Key, string? pipelineNameOverride, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileNameDateEntry>> GetFileNamesForDateRangeAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default);
}
