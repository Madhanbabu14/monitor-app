using Monitor.Files;
using Monitor.Files.Domain;

namespace Monitor.UnitTests.Files.Fakes;

/// <summary>
/// Test double for <see cref="IFilesService"/>, used to unit-test
/// <c>Monitor.Api.Endpoints.FilesEndpoints</c> in isolation from
/// <see cref="Monitor.Files.FilesService"/>'s own logic (which has its own dedicated
/// tests in <c>FilesServiceTests</c>). Records the last query/args it was called with so
/// tests can assert the endpoint module's request-parsing/defaulting logic.
/// </summary>
public sealed class FakeFilesService : IFilesService
{
    public ListFilesQuery? LastListFilesQuery { get; private set; }
    public Func<ListFilesQuery, ListFilesResult>? ListFilesResultFactory { get; set; }

    public string? LastStreamedKey { get; private set; }
    public Func<string, S3DownloadResult>? StreamFileResultFactory { get; set; }
    public Exception? StreamFileException { get; set; }

    public Func<IReadOnlyList<string>>? PipelineNamesFactory { get; set; }

    public (string FileName, string S3Key, string? PipelineNameOverride)? LastRetrigger { get; private set; }
    public Func<RetriggerResult>? RetriggerResultFactory { get; set; }
    public Exception? RetriggerException { get; set; }

    public Task<ListFilesResult> ListFilesAsync(ListFilesQuery query, CancellationToken cancellationToken)
    {
        LastListFilesQuery = query;
        var factory = ListFilesResultFactory
            ?? (_ => new ListFilesResult(Array.Empty<S3FileItem>(), 0, 0, query.Page, query.Limit, 0, Array.Empty<string>(), Array.Empty<string>(), "bucket", "root/"));
        return Task.FromResult(factory(query));
    }

    public Task<S3DownloadResult> StreamFileAsync(string key, CancellationToken cancellationToken)
    {
        LastStreamedKey = key;
        if (StreamFileException is not null)
        {
            throw StreamFileException;
        }

        var factory = StreamFileResultFactory ?? (k => new S3DownloadResult(new MemoryStream(), "application/octet-stream", null, k));
        return Task.FromResult(factory(key));
    }

    public Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken)
    {
        var factory = PipelineNamesFactory ?? (() => Array.Empty<string>());
        return Task.FromResult(factory());
    }

    public Task<RetriggerResult> RetriggerFileAsync(string fileName, string s3Key, string? pipelineNameOverride, CancellationToken cancellationToken)
    {
        LastRetrigger = (fileName, s3Key, pipelineNameOverride);
        if (RetriggerException is not null)
        {
            throw RetriggerException;
        }

        var factory = RetriggerResultFactory ?? (() => new RetriggerResult(Guid.NewGuid().ToString(), pipelineNameOverride ?? "derived", "queued"));
        return Task.FromResult(factory());
    }

    public Task<IReadOnlyList<FileDateEntry>> GetFileNamesForDateRangeAsync(string? startDate, string? endDate, CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised through the Files endpoints.");
}
