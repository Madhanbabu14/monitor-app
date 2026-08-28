using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monitor.Core.Errors;
using Monitor.Core.Options;
using Monitor.Files;
using Monitor.Files.Domain;
using Monitor.UnitTests.Files.Fakes;

namespace Monitor.UnitTests.Files;

/// <summary>
/// Unit tests for <see cref="FilesService"/> (direct translation of <c>S3Service</c>,
/// features/s3/s3.service.ts). The AWS SDK client and primary-DB repository are replaced
/// with hand-rolled fakes (<see cref="FakeAmazonS3Client"/>, <see cref="FakePrimaryDb"/>);
/// a real <see cref="MemoryCache"/> is used since it is cheap, deterministic here, and
/// exercising the actual cache/AsyncLazy interaction is the point of the caching tests.
/// </summary>
public class FilesServiceTests : IDisposable
{
    private const string Bucket = "test-bucket";
    private const string RootPrefix = "data/";

    private readonly FakeAmazonS3Client _s3 = new();
    private readonly FakePrimaryDb _db = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private FilesService CreateService(string? s3Prefix = RootPrefix)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AwsOptions
        {
            AccessKeyId = "fake",
            SecretAccessKey = "fake",
            Region = "us-east-1",
            S3Bucket = Bucket,
            S3Prefix = s3Prefix ?? string.Empty,
        });

        return new FilesService(_s3, _db, _cache, options, NullLogger<FilesService>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private static S3Object MakeObject(string key, long size, DateTime lastModifiedUtc, string etag = "abc123")
        => new() { Key = key, Size = size, LastModified = lastModifiedUtc, ETag = $"\"{etag}\"" };

    private void QueueSinglePage(params S3Object[] objects)
    {
        _s3.ListObjectsV2Responses.Enqueue(_ => new ListObjectsV2Response
        {
            S3Objects = objects.ToList(),
            IsTruncated = false,
        });
    }

    // ── ListFilesAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListFilesAsync_HappyPath_ReturnsBucketRootPrefixAndAggregatesSize()
    {
        QueueSinglePage(
            MakeObject("data/enrolment_00000001_20260101010101.csv", 100, new DateTime(2026, 1, 1, 1, 1, 1, DateTimeKind.Utc)),
            MakeObject("data/sub/report_00000001_20260102010101.csv", 250, new DateTime(2026, 1, 2, 1, 1, 1, DateTimeKind.Utc)));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(Bucket, result.Bucket);
        Assert.Equal(RootPrefix, result.RootPrefix);
        Assert.Equal(2, result.Total);
        Assert.Equal(350, result.TotalSize);
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.Limit);
        Assert.Equal(1, result.TotalPages);
    }

    [Fact]
    public async Task ListFilesAsync_FolderMarkerKeys_AreExcludedFromFilesButFoldersAreDerived()
    {
        QueueSinglePage(
            MakeObject("data/sub/", 0, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            MakeObject("data/sub/report_00000001_20260102010101.csv", 10, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(1, result.Total); // the "directory marker" key itself never becomes a file
        Assert.Contains("data/sub/", result.Folders);
    }

    [Fact]
    public async Task ListFilesAsync_NestedFolders_AreAccumulatedAtEachLevel()
    {
        QueueSinglePage(MakeObject("data/a/b/file_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(new[] { "data/a/", "data/a/b/" }, result.Folders);
    }

    [Fact]
    public async Task ListFilesAsync_Suggestions_DerivedFromPipelineNameOrExtensionStrippedFilename()
    {
        QueueSinglePage(
            MakeObject("data/enrolment_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/plainfile.txt", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Contains("enrolment", result.Suggestions);
        Assert.Contains("plainfile", result.Suggestions);
    }

    [Fact]
    public async Task ListFilesAsync_PrefixDifferentFromRoot_IsSentToS3AndAppliedAsKeyFilter()
    {
        QueueSinglePage(
            MakeObject("data/sub/keep_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/sub/keep2_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery("data/sub/", null, null, null, 1, 20, "lastModified", "desc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal("data/sub/", _s3.ListObjectsV2Calls.Single().Prefix);
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public async Task ListFilesAsync_PrefixEqualToRoot_FetchesRootPrefixVerbatim()
    {
        QueueSinglePage(MakeObject("data/file_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(RootPrefix, null, null, null, 1, 20, "lastModified", "desc");

        await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(RootPrefix, _s3.ListObjectsV2Calls.Single().Prefix);
    }

    [Fact]
    public async Task ListFilesAsync_Search_IsTrimmedAndCaseInsensitive()
    {
        QueueSinglePage(
            MakeObject("data/Enrolment_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/other_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(null, "  ENROL  ", null, null, 1, 20, "lastModified", "desc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal("Enrolment_00000001_20260101010101.csv", result.Files.Single().Name);
    }

    [Fact]
    public async Task ListFilesAsync_WhitespaceOnlySearch_IsIgnored()
    {
        QueueSinglePage(
            MakeObject("data/a_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/b_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(null, "   ", null, null, 1, 20, "lastModified", "desc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(2, result.Total);
    }

    [Fact]
    public async Task ListFilesAsync_StartDate_IsInclusiveLowerBound()
    {
        QueueSinglePage(
            MakeObject("data/old_00000001_20260101010101.csv", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            MakeObject("data/boundary_00000001_20260101010101.csv", 1, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            MakeObject("data/new_00000001_20260101010101.csv", 1, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, "2026-01-02T00:00:00Z", null, 1, 20, "lastModified", "desc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(2, result.Total);
        Assert.DoesNotContain(result.Files, f => f.Name.StartsWith("old", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListFilesAsync_EndDate_IsEndOfDayInclusive()
    {
        QueueSinglePage(
            MakeObject("data/within_00000001_20260101010101.csv", 1, new DateTime(2026, 1, 2, 23, 59, 59, 999, DateTimeKind.Utc)),
            MakeObject("data/after_00000001_20260101010101.csv", 1, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, "2026-01-02", 1, 20, "lastModified", "desc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal("within_00000001_20260101010101.csv", result.Files.Single().Name);
    }

    [Theory]
    [InlineData("name", "asc", new[] { "a_00000001_20260101010101.csv", "b_00000001_20260101010101.csv", "c_00000001_20260101010101.csv" })]
    [InlineData("name", "desc", new[] { "c_00000001_20260101010101.csv", "b_00000001_20260101010101.csv", "a_00000001_20260101010101.csv" })]
    public async Task ListFilesAsync_SortByName_RespectsSortOrder(string sortBy, string sortOrder, string[] expectedOrder)
    {
        QueueSinglePage(
            MakeObject("data/b_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/c_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/a_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, sortBy, sortOrder);

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(expectedOrder, result.Files.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task ListFilesAsync_SortBySize_Ascending()
    {
        QueueSinglePage(
            MakeObject("data/big_00000001_20260101010101.csv", 300, DateTime.UtcNow),
            MakeObject("data/small_00000001_20260101010101.csv", 10, DateTime.UtcNow),
            MakeObject("data/mid_00000001_20260101010101.csv", 100, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "size", "asc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(new[] { 10L, 100L, 300L }, result.Files.Select(f => f.Size).ToArray());
    }

    [Fact]
    public async Task ListFilesAsync_UnrecognizedSortOrder_DefaultsToDescending()
    {
        // Only the literal 'asc' flips to ascending order; any other value (including a
        // typo, or the missing-value default) sorts descending, mirroring the source's
        // `sortOrder === 'asc' ? cmp : -cmp`.
        QueueSinglePage(
            MakeObject("data/a_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/b_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "name", "not-a-real-value");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(new[] { "b_00000001_20260101010101.csv", "a_00000001_20260101010101.csv" }, result.Files.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task ListFilesAsync_Pagination_SlicesSortedResultsAndComputesTotalPages()
    {
        QueueSinglePage(
            MakeObject("data/1_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/2_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/3_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/4_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/5_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 2, 2, "name", "asc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(new[] { "3_00000001_20260101010101.csv", "4_00000001_20260101010101.csv" }, result.Files.Select(f => f.Name).ToArray());
        Assert.Equal(5, result.Total);
        Assert.Equal(3, result.TotalPages); // ceil(5/2)
        Assert.Equal(2, result.Page);
        Assert.Equal(2, result.Limit);
    }

    [Fact]
    public async Task ListFilesAsync_PageBeyondLastPage_ReturnsEmptyFilesButKeepsTotals()
    {
        QueueSinglePage(MakeObject("data/only_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 5, 20, "name", "asc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task ListFilesAsync_MultiPageContinuation_AggregatesAcrossPages()
    {
        _s3.ListObjectsV2Responses.Enqueue(_ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/p1_00000001_20260101010101.csv", 1, DateTime.UtcNow) },
            IsTruncated = true,
            NextContinuationToken = "token-1",
        });
        _s3.ListObjectsV2Responses.Enqueue(_ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/p2_00000001_20260101010101.csv", 1, DateTime.UtcNow) },
            IsTruncated = false,
        });

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "name", "asc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal(2, result.Total);
        Assert.Equal(2, _s3.ListObjectsV2Calls.Count);
        Assert.Null(_s3.ListObjectsV2Calls[0].ContinuationToken);
        Assert.Equal("token-1", _s3.ListObjectsV2Calls[1].ContinuationToken);
    }

    [Fact]
    public async Task ListFilesAsync_ItemFields_ReflectKeyNamePrefixEtagAndPipelineName()
    {
        QueueSinglePage(MakeObject("data/sub/enrolment_00000001_20260101010101.csv", 42, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "the-etag"));

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "name", "asc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);
        var file = result.Files.Single();

        Assert.Equal("data/sub/enrolment_00000001_20260101010101.csv", file.Key);
        Assert.Equal("enrolment_00000001_20260101010101.csv", file.Name);
        Assert.Equal("data/sub/", file.Prefix);
        Assert.Equal(42, file.Size);
        Assert.Equal("the-etag", file.ETag); // surrounding quotes stripped
        Assert.Equal("enrolment", file.PipelineName);
        Assert.Equal("2026-01-01T00:00:00.000Z", file.LastModified);
    }

    [Fact]
    public async Task ListFilesAsync_DefaultLastModified_FallsBackToUnixEpoch()
    {
        // obj.LastModified left at its CLR default(DateTime) simulates the source's
        // `obj.LastModified?.toISOString() ?? new Date(0).toISOString()` fallback.
        QueueSinglePage(new S3Object { Key = "data/nodate_00000001_20260101010101.csv", Size = 1, ETag = "\"x\"" });

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "name", "asc");

        var result = await service.ListFilesAsync(query, CancellationToken.None);

        Assert.Equal("1970-01-01T00:00:00.000Z", result.Files.Single().LastModified);
    }

    [Fact]
    public async Task ListFilesAsync_S3NoSuchBucket_MapsTo404WithBucketNameInMessage()
    {
        _s3.ListObjectsV2Exception = new AmazonS3Exception("nope", ErrorType.Receiver, "NoSuchBucket", "req-1", HttpStatusCode.NotFound);

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc");

        var ex = await Assert.ThrowsAsync<AppException>(() => service.ListFilesAsync(query, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains(Bucket, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFilesAsync_S3AccessDenied_MapsTo403()
    {
        _s3.ListObjectsV2Exception = new AmazonS3Exception("denied", ErrorType.Receiver, "AccessDenied", "req-1", HttpStatusCode.Forbidden);

        var service = CreateService();
        var query = new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc");

        var ex = await Assert.ThrowsAsync<AppException>(() => service.ListFilesAsync(query, CancellationToken.None));

        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task ListFilesAsync_HttpStatus403WithUnrecognizedErrorCode_StillMapsTo403()
    {
        _s3.ListObjectsV2Exception = new AmazonS3Exception("denied", ErrorType.Receiver, "SomeOtherCode", "req-1", HttpStatusCode.Forbidden);

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            service.ListFilesAsync(new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc"), CancellationToken.None));

        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task ListFilesAsync_ConnectivityFailure_MapsTo503()
    {
        _s3.ListObjectsV2Exception = new AmazonServiceException("boom", new SocketException());

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            service.ListFilesAsync(new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc"), CancellationToken.None));

        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public async Task ListFilesAsync_UnknownError_MapsToItsHttpStatusOr500_AndIncludesErrorCodeInMessage()
    {
        _s3.ListObjectsV2Exception = new AmazonS3Exception("weird failure", ErrorType.Unknown, "SomeWeirdCode", "req-1", HttpStatusCode.BadGateway);

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            service.ListFilesAsync(new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc"), CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("SomeWeirdCode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFilesAsync_PlainExceptionWithNoStatusCode_MapsTo500()
    {
        _s3.ListObjectsV2Exception = new InvalidOperationException("totally unexpected");

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            service.ListFilesAsync(new ListFilesQuery(null, null, null, null, 1, 20, "lastModified", "desc"), CancellationToken.None));

        Assert.Equal(500, ex.StatusCode);
        Assert.Contains("totally unexpected", ex.Message, StringComparison.Ordinal);
    }

    // ── StreamFileAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamFileAsync_HappyPath_ReturnsBodyContentTypeLengthAndFileName()
    {
        var bodyStream = new MemoryStream(new byte[] { 1, 2, 3 });
        _s3.GetObjectResponseFactory = req =>
        {
            var response = new GetObjectResponse { ResponseStream = bodyStream, ContentLength = 3 };
            response.Headers.ContentType = "text/csv";
            return response;
        };

        var service = CreateService();
        var result = await service.StreamFileAsync("data/sub/enrolment_00000001_20260101010101.csv", CancellationToken.None);

        Assert.Same(bodyStream, result.Body);
        Assert.Equal("text/csv", result.ContentType);
        Assert.Equal(3, result.ContentLength);
        Assert.Equal("enrolment_00000001_20260101010101.csv", result.FileName);
    }

    [Fact]
    public async Task StreamFileAsync_KeyWithNoSlashes_UsesWholeKeyAsFileName()
    {
        _s3.GetObjectResponseFactory = _ => new GetObjectResponse { ResponseStream = new MemoryStream() };

        var service = CreateService();
        var result = await service.StreamFileAsync("top-level-file.csv", CancellationToken.None);

        Assert.Equal("top-level-file.csv", result.FileName);
    }

    [Fact]
    public async Task StreamFileAsync_MissingContentType_FallsBackToOctetStream()
    {
        _s3.GetObjectResponseFactory = _ => new GetObjectResponse { ResponseStream = new MemoryStream() };

        var service = CreateService();
        var result = await service.StreamFileAsync("data/file.bin", CancellationToken.None);

        Assert.Equal("application/octet-stream", result.ContentType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StreamFileAsync_NonPositiveContentLength_MapsToNull(long contentLength)
    {
        _s3.GetObjectResponseFactory = _ => new GetObjectResponse { ResponseStream = new MemoryStream(), ContentLength = contentLength };

        var service = CreateService();
        var result = await service.StreamFileAsync("data/file.bin", CancellationToken.None);

        Assert.Null(result.ContentLength);
    }

    [Fact]
    public async Task StreamFileAsync_NullResponseStream_ThrowsAppException404()
    {
        _s3.GetObjectResponseFactory = _ => new GetObjectResponse { ResponseStream = null! };

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<AppException>(() => service.StreamFileAsync("data/file.bin", CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("File body is empty", ex.Message);
    }

    [Fact]
    public async Task StreamFileAsync_NoSuchKey_MapsTo404WithFileNotFoundMessage()
    {
        _s3.GetObjectException = new AmazonS3Exception("missing", ErrorType.Receiver, "NoSuchKey", "req-1", HttpStatusCode.NotFound);

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<AppException>(() => service.StreamFileAsync("data/missing.csv", CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("File not found in S3", ex.Message);
    }

    [Fact]
    public async Task StreamFileAsync_RequestExpired_MapsTo403WithCredentialsMessage()
    {
        // Status 400 (not 403) so the earlier "httpStatus == 403" access-denied branch
        // doesn't shadow this one - mirrors the source's `toAppError`, which checks the
        // AccessDenied/403 condition strictly before the RequestExpired condition too.
        _s3.GetObjectException = new AmazonS3Exception("expired", ErrorType.Receiver, "RequestExpired", "req-1", HttpStatusCode.BadRequest);

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<AppException>(() => service.StreamFileAsync("data/file.csv", CancellationToken.None));

        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── GetPipelineNamesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPipelineNamesAsync_DedupesAndSortsOrdinally()
    {
        QueueSinglePage(
            MakeObject("data/beta_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/alpha_00000001_20260101010101.csv", 1, DateTime.UtcNow),
            MakeObject("data/alpha_00000002_20260102010101.csv", 1, DateTime.UtcNow), // same pipeline name again
            MakeObject("data/no-pattern-here.csv", 1, DateTime.UtcNow), // never derives a name
            MakeObject("data/folder/", 0, DateTime.UtcNow)); // folder marker skipped entirely

        var service = CreateService();
        var names = await service.GetPipelineNamesAsync(CancellationToken.None);

        Assert.Equal(new[] { "alpha", "beta" }, names);
    }

    [Fact]
    public async Task GetPipelineNamesAsync_MultiPageContinuation_AggregatesAcrossPages()
    {
        _s3.ListObjectsV2Responses.Enqueue(_ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/alpha_00000001_20260101010101.csv", 1, DateTime.UtcNow) },
            IsTruncated = true,
            NextContinuationToken = "next",
        });
        _s3.ListObjectsV2Responses.Enqueue(_ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/beta_00000001_20260101010101.csv", 1, DateTime.UtcNow) },
            IsTruncated = false,
        });

        var service = CreateService();
        var names = await service.GetPipelineNamesAsync(CancellationToken.None);

        Assert.Equal(new[] { "alpha", "beta" }, names);
        Assert.Equal("next", _s3.ListObjectsV2Calls[1].ContinuationToken);
    }

    [Fact]
    public async Task GetPipelineNamesAsync_S3Failure_PropagatesAsMappedAppException()
    {
        _s3.ListObjectsV2Exception = new AmazonS3Exception("nope", ErrorType.Receiver, "NoSuchBucket", "req-1", HttpStatusCode.NotFound);

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<AppException>(() => service.GetPipelineNamesAsync(CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
    }

    // ── RetriggerFileAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RetriggerFileAsync_DerivesPipelineNameAndDate_InsertsRecoveryJobAndPipelineRows()
    {
        var service = CreateService();

        var result = await service.RetriggerFileAsync(
            "enrolment_000000009718_20260629010224.csv", "data/enrolment_000000009718_20260629010224.csv", null, CancellationToken.None);

        Assert.Equal("enrolment", result.PipelineName);
        Assert.Equal("queued", result.Status);
        Assert.True(Guid.TryParse(result.JobId, out _));

        Assert.Equal(2, _db.Queries.Count);

        var recoveryJobQuery = _db.Queries[0];
        Assert.Contains("INSERT INTO recovery_jobs", recoveryJobQuery.Sql, StringComparison.Ordinal);
        Assert.Equal("enrolment", FakePrimaryDb.GetParam(recoveryJobQuery.Parameters, "PipelineName"));
        Assert.Equal("2026-06-29", FakePrimaryDb.GetParam(recoveryJobQuery.Parameters, "DateStr"));
        Assert.Equal(result.JobId, FakePrimaryDb.GetParam(recoveryJobQuery.Parameters, "JobId")!.ToString());

        var pipelineQuery = _db.Queries[1];
        Assert.Contains("INSERT INTO pipelines", pipelineQuery.Sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (name) DO NOTHING", pipelineQuery.Sql, StringComparison.Ordinal);
        Assert.Equal("enrolment", FakePrimaryDb.GetParam(pipelineQuery.Parameters, "Name"));
        Assert.Equal("Enrolment", FakePrimaryDb.GetParam(pipelineQuery.Parameters, "DisplayName"));
    }

    [Fact]
    public async Task RetriggerFileAsync_PipelineNameOverride_TakesPrecedenceOverDerivedName()
    {
        var service = CreateService();

        var result = await service.RetriggerFileAsync(
            "enrolment_000000009718_20260629010224.csv", "some/key.csv", "custom_pipeline", CancellationToken.None);

        Assert.Equal("custom_pipeline", result.PipelineName);
        Assert.Equal("custom_pipeline", FakePrimaryDb.GetParam(_db.Queries[0].Parameters, "PipelineName"));
    }

    [Fact]
    public async Task RetriggerFileAsync_DisplayName_ReplacesUnderscoresAndTitleCasesEachWord()
    {
        var service = CreateService();

        await service.RetriggerFileAsync("x_20260101010101.csv", "key", "claims_intake_batch", CancellationToken.None);

        Assert.Equal("Claims Intake Batch", FakePrimaryDb.GetParam(_db.Queries[1].Parameters, "DisplayName"));
    }

    [Fact]
    public async Task RetriggerFileAsync_NoDateInFilename_FallsBackToTodayUtc()
    {
        var service = CreateService();
        var before = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var result = await service.RetriggerFileAsync("plainfile.csv", "key", "some_pipeline", CancellationToken.None);

        var after = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var dateStr = (string)FakePrimaryDb.GetParam(_db.Queries[0].Parameters, "DateStr")!;

        Assert.True(dateStr == before || dateStr == after);
        Assert.Equal("some_pipeline", result.PipelineName);
    }

    [Fact]
    public async Task RetriggerFileAsync_CannotDerivePipelineName_ThrowsAppException400_AndNeverTouchesDb()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            service.RetriggerFileAsync("no-pattern-here.csv", "key", null, CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("no-pattern-here.csv", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_db.Queries);
    }

    [Fact]
    public async Task RetriggerFileAsync_EmptyStringOverride_IsTreatedAsNoOverride_AndStillThrowsIfUnderivable()
    {
        // pipelineNameOverride ?? derivePipelineName(fileName) only short-circuits on a
        // non-null override; an empty string is a non-null override (so it "wins") but is
        // itself empty, so the subsequent IsNullOrEmpty guard must still fire.
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            service.RetriggerFileAsync("enrolment_00000001_20260101010101.csv", "key", string.Empty, CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
    }

    // ── GetFileNamesForDateRangeAsync (raw-listing cache) ──────────────────────

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_MapsNameAndLastModifiedOnly()
    {
        QueueSinglePage(MakeObject("data/f_00000001_20260101010101.csv", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var service = CreateService();
        var entries = await service.GetFileNamesForDateRangeAsync(null, null, CancellationToken.None);

        var entry = entries.Single();
        Assert.Equal("f_00000001_20260101010101.csv", entry.Name);
        Assert.Equal("2026-01-01T00:00:00.000Z", entry.LastModified);
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_FiltersByStartAndEndDate()
    {
        QueueSinglePage(
            MakeObject("data/before_00000001_20260101010101.csv", 1, new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc)),
            MakeObject("data/within_00000001_20260101010101.csv", 1, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)),
            MakeObject("data/after_00000001_20260101010101.csv", 1, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

        var service = CreateService();
        var entries = await service.GetFileNamesForDateRangeAsync("2026-01-01", "2026-01-31", CancellationToken.None);

        Assert.Equal(new[] { "within_00000001_20260101010101.csv" }, entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_SecondCallWithinTtl_ServesFromCache_NoExtraS3Call()
    {
        QueueSinglePage(MakeObject("data/f_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();

        await service.GetFileNamesForDateRangeAsync(null, null, CancellationToken.None);
        await service.GetFileNamesForDateRangeAsync(null, null, CancellationToken.None);

        Assert.Single(_s3.ListObjectsV2Calls);
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_ConcurrentColdCallers_OnlyTriggerOneS3RoundTrip()
    {
        // AsyncLazy's whole purpose: several requests racing on the same cold cache entry
        // must still only cause one ListObjectsV2 call, mirroring the source's
        // rawS3Inflight promise de-duplication.
        QueueSinglePage(MakeObject("data/f_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var service = CreateService();

        var tasks = Enumerable.Range(0, 5).Select(_ => service.GetFileNamesForDateRangeAsync(null, null, CancellationToken.None));
        await Task.WhenAll(tasks);

        Assert.Single(_s3.ListObjectsV2Calls);
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_FailedFetch_IsNotCached_NextCallRetries()
    {
        _s3.ListObjectsV2Exception = new AmazonS3Exception("boom", ErrorType.Receiver, "NoSuchBucket", "req-1", HttpStatusCode.NotFound);

        var service = CreateService();

        await Assert.ThrowsAsync<AppException>(() => service.GetFileNamesForDateRangeAsync(null, null, CancellationToken.None));

        // Fix the underlying failure and retry - a faulted fetch must not have poisoned the cache entry.
        _s3.ListObjectsV2Exception = null;
        QueueSinglePage(MakeObject("data/f_00000001_20260101010101.csv", 1, DateTime.UtcNow));

        var entries = await service.GetFileNamesForDateRangeAsync(null, null, CancellationToken.None);

        Assert.Single(entries);
    }
}
