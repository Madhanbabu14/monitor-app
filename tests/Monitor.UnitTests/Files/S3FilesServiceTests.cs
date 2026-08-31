using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monitor.Core.Errors;
using Monitor.Core.Options;
using Monitor.Data.Repositories;
using Monitor.Files;
using Monitor.Files.Domain;
using Npgsql;

namespace Monitor.UnitTests.Files;

/// <summary>
/// Exercises <see cref="S3FilesService"/> — the direct translation of
/// s3.service.ts's exported functions (listFiles, streamFile,
/// getAllPipelineNames, retriggerFile, getFileNamesForDateRange). Uses a
/// hand-written <see cref="AmazonS3Client"/> subclass that overrides only the
/// two virtual SDK methods the service calls (no mocking library referenced
/// by this test project, matching <c>UserRepositoryTests</c>'s
/// <c>FakePrimaryDb</c> pattern) plus the same hand-written <see cref="IPrimaryDb"/>
/// fake used there.
/// </summary>
public class S3FilesServiceTests
{
    private sealed class FakeS3Client : AmazonS3Client
    {
        public FakeS3Client()
            : base(new BasicAWSCredentials("test", "test"), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
        {
        }

        public Queue<ListObjectsV2Response> ListResponses { get; } = new();
        public List<ListObjectsV2Request> ListRequests { get; } = new();
        public Func<GetObjectRequest, GetObjectResponse>? GetObjectHandler { get; set; }
        public Exception? GetObjectException { get; set; }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            ListRequests.Add(request);
            var response = ListResponses.Count > 0
                ? ListResponses.Dequeue()
                : new ListObjectsV2Response { S3Objects = new List<S3Object>(), IsTruncated = false };
            return Task.FromResult(response);
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            if (GetObjectException is not null) throw GetObjectException;
            return Task.FromResult(GetObjectHandler!(request));
        }
    }

    private sealed class FakePrimaryDb : IPrimaryDb
    {
        public List<(string Sql, object? Parameters)> QueryCalls { get; } = new();
        public List<(string Sql, object? Parameters)> QuerySingleCalls { get; } = new();

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            QueryCalls.Add((sql, parameters));
            return Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
        }

        public Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            QuerySingleCalls.Add((sql, parameters));
            return Task.FromResult(default(T));
        }

        public Task<TResult> WithTransactionAsync<TResult>(Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by S3FilesService.");

        public Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by S3FilesService.");
    }

    private static object? GetParam(object? parameters, string name) =>
        parameters?.GetType().GetProperty(name)?.GetValue(parameters);

    private static S3FilesService CreateService(FakeS3Client s3Client, FakePrimaryDb? primaryDb = null, string bucket = "test-bucket", string prefix = "data/") =>
        new(
            s3Client,
            primaryDb ?? new FakePrimaryDb(),
            new MemoryCache(new MemoryCacheOptions()),
            new S3ErrorMapper(NullLogger<S3ErrorMapper>.Instance, Microsoft.Extensions.Options.Options.Create(new AwsOptions { S3Bucket = bucket, S3Prefix = prefix })),
            NullLogger<S3FilesService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new AwsOptions { S3Bucket = bucket, S3Prefix = prefix }));

    private static S3Object MakeObject(string key, long size, DateTime lastModified, string etag = "abc123") =>
        new() { Key = key, Size = size, LastModified = lastModified, ETag = etag };

    [Fact]
    public async Task ListFilesAsync_PaginatesAcrossContinuationTokens_AndSortsByLastModifiedDescByDefault()
    {
        var t1 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);

        var s3 = new FakeS3Client();
        s3.ListResponses.Enqueue(new ListObjectsV2Response
        {
            S3Objects = new List<S3Object>
            {
                MakeObject("data/enrolment_000000009718_20260610010203.csv", 100, t1),
                MakeObject("data/reports/2026/summary.csv", 200, t2),
            },
            IsTruncated = true,
            NextContinuationToken = "tok1",
        });
        s3.ListResponses.Enqueue(new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/notes.txt", 50, t3) },
            IsTruncated = false,
        });

        var service = CreateService(s3);

        var result = await service.ListFilesAsync(new ListFilesParams(Page: 1, Limit: 2));

        Assert.Equal(2, s3.ListRequests.Count);
        Assert.Null(s3.ListRequests[0].ContinuationToken);
        Assert.Equal("tok1", s3.ListRequests[1].ContinuationToken);

        Assert.Equal(3, result.Total);
        Assert.Equal(350, result.TotalSize);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal(2, result.Files.Count);
        // Default sort is lastModified desc: newest (notes.txt, t3) first.
        Assert.Equal("notes.txt", result.Files[0].Name);
        Assert.Equal("summary.csv", result.Files[1].Name);

        Assert.Contains("data/reports/", result.Folders);
        Assert.Contains("data/reports/2026/", result.Folders);
        Assert.Contains("enrolment", result.Suggestions);
    }

    [Fact]
    public async Task ListFilesAsync_FiltersBySearch_CaseInsensitiveSubstringOnName()
    {
        var s3 = new FakeS3Client();
        s3.ListResponses.Enqueue(new ListObjectsV2Response
        {
            S3Objects = new List<S3Object>
            {
                MakeObject("data/Report_January.csv", 10, DateTime.UtcNow),
                MakeObject("data/invoice.csv", 10, DateTime.UtcNow),
            },
            IsTruncated = false,
        });

        var result = await CreateService(s3).ListFilesAsync(new ListFilesParams(Search: "report"));

        Assert.Equal(1, result.Total);
        Assert.Equal("Report_January.csv", result.Files[0].Name);
    }

    [Fact]
    public async Task ListFilesAsync_FiltersByValidDateRange_InclusiveOfEndOfDay()
    {
        var inRange = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var beforeRange = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var afterRange = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        var s3 = new FakeS3Client();
        s3.ListResponses.Enqueue(new ListObjectsV2Response
        {
            S3Objects = new List<S3Object>
            {
                MakeObject("data/in.csv", 1, inRange),
                MakeObject("data/before.csv", 1, beforeRange),
                MakeObject("data/after.csv", 1, afterRange),
            },
            IsTruncated = false,
        });

        var result = await CreateService(s3).ListFilesAsync(new ListFilesParams(StartDate: "2026-03-10", EndDate: "2026-03-20"));

        Assert.Equal(1, result.Total);
        Assert.Equal("in.csv", result.Files[0].Name);
    }

    [Fact]
    public async Task ListFilesAsync_UnparseableStartDate_ReturnsNoMatches()
    {
        // Source: `new Date(startDate).getTime()` is NaN for a malformed date, and every
        // NaN comparison is false, so a bad startDate filters out every item rather than
        // being ignored.
        var s3 = new FakeS3Client();
        s3.ListResponses.Enqueue(new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/file.csv", 1, DateTime.UtcNow) },
            IsTruncated = false,
        });

        var result = await CreateService(s3).ListFilesAsync(new ListFilesParams(StartDate: "not-a-date"));

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task ListFilesAsync_SortByNameAscending()
    {
        var s3 = new FakeS3Client();
        s3.ListResponses.Enqueue(new ListObjectsV2Response
        {
            S3Objects = new List<S3Object>
            {
                MakeObject("data/banana.csv", 1, DateTime.UtcNow),
                MakeObject("data/apple.csv", 1, DateTime.UtcNow),
            },
            IsTruncated = false,
        });

        var result = await CreateService(s3).ListFilesAsync(new ListFilesParams(SortBy: "name", SortOrder: "asc"));

        Assert.Equal("apple.csv", result.Files[0].Name);
        Assert.Equal("banana.csv", result.Files[1].Name);
    }

    [Fact]
    public async Task GetFileDownloadAsync_ReturnsStreamContentTypeAndFileName()
    {
        var s3 = new FakeS3Client();
        var body = new MemoryStream(new byte[] { 1, 2, 3 });
        s3.GetObjectHandler = request =>
        {
            Assert.Equal("data/reports/summary.csv", request.Key);
            var response = new GetObjectResponse { ResponseStream = body };
            response.Headers.ContentType = "text/csv";
            response.Headers.ContentLength = 3;
            return response;
        };

        await using var download = await CreateService(s3).GetFileDownloadAsync("data/reports/summary.csv");

        Assert.Same(body, download.Content);
        Assert.Equal("text/csv", download.ContentType);
        Assert.Equal(3, download.ContentLength);
        Assert.Equal("summary.csv", download.FileName);
    }

    [Fact]
    public async Task GetFileDownloadAsync_S3NoSuchKey_MapsToAppException404()
    {
        var s3 = new FakeS3Client { GetObjectException = new AmazonS3Exception("missing") { ErrorCode = "NoSuchKey" } };

        var ex = await Assert.ThrowsAsync<AppException>(() => CreateService(s3).GetFileDownloadAsync("data/missing.csv"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("File not found in S3", ex.Message);
    }

    [Fact]
    public async Task GetPipelineNamesAsync_DedupesAcrossPages_AndSortsOrdinally()
    {
        var s3 = new FakeS3Client();
        s3.ListResponses.Enqueue(new ListObjectsV2Response
        {
            S3Objects = new List<S3Object>
            {
                MakeObject("data/beta_000000000001_20260101000000.csv", 1, DateTime.UtcNow),
                MakeObject("data/alpha_000000000002_20260102000000.csv", 1, DateTime.UtcNow),
            },
            IsTruncated = true,
            NextContinuationToken = "tok1",
        });
        s3.ListResponses.Enqueue(new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/alpha_000000000003_20260103000000.csv", 1, DateTime.UtcNow) },
            IsTruncated = false,
        });

        var names = await CreateService(s3).GetPipelineNamesAsync();

        Assert.Equal(new[] { "alpha", "beta" }, names);
    }

    [Fact]
    public async Task RetriggerFileAsync_InsertsRecoveryJobAndUpsertsPipeline_WithDerivedNameAndDate()
    {
        var db = new FakePrimaryDb();
        var service = CreateService(new FakeS3Client(), db);

        var result = await service.RetriggerFileAsync("enrolment_000000009718_20260629010224.csv", "data/enrolment_000000009718_20260629010224.csv", null);

        Assert.Equal("enrolment", result.PipelineName);
        Assert.Equal("queued", result.Status);
        Assert.True(Guid.TryParse(result.JobId, out _));

        Assert.Single(db.QueryCalls);
        Assert.Contains("INSERT INTO recovery_jobs", db.QueryCalls[0].Sql);
        Assert.Equal("enrolment", GetParam(db.QueryCalls[0].Parameters, "PipelineName"));
        Assert.Equal("2026-06-29", GetParam(db.QueryCalls[0].Parameters, "StartDate"));
        Assert.Equal("2026-06-29", GetParam(db.QueryCalls[0].Parameters, "EndDate"));

        Assert.Single(db.QuerySingleCalls);
        Assert.Contains("INSERT INTO pipelines", db.QuerySingleCalls[0].Sql);
        Assert.Contains("ON CONFLICT (name) DO NOTHING", db.QuerySingleCalls[0].Sql);
        Assert.Equal("enrolment", GetParam(db.QuerySingleCalls[0].Parameters, "Name"));
        Assert.Equal("Enrolment", GetParam(db.QuerySingleCalls[0].Parameters, "DisplayName"));
    }

    [Fact]
    public async Task RetriggerFileAsync_UsesPipelineNameOverride_WhenProvided()
    {
        var db = new FakePrimaryDb();
        var service = CreateService(new FakeS3Client(), db);

        var result = await service.RetriggerFileAsync("unrelated.csv", "data/unrelated.csv", "override_pipeline");

        Assert.Equal("override_pipeline", result.PipelineName);
        Assert.Equal("override_pipeline", GetParam(db.QueryCalls[0].Parameters, "PipelineName"));
    }

    [Fact]
    public async Task RetriggerFileAsync_FallsBackToTodayUtc_WhenFilenameHasNoDate()
    {
        var db = new FakePrimaryDb();
        var service = CreateService(new FakeS3Client(), db);
        var expectedDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await service.RetriggerFileAsync("nodatehere.csv", "data/nodatehere.csv", "manual_pipeline");

        Assert.Equal(expectedDate, GetParam(db.QueryCalls[0].Parameters, "StartDate"));
        Assert.Equal(expectedDate, GetParam(db.QueryCalls[0].Parameters, "EndDate"));
    }

    [Fact]
    public async Task RetriggerFileAsync_CannotDeriveOrOverridePipelineName_Throws400()
    {
        var service = CreateService(new FakeS3Client());

        var ex = await Assert.ThrowsAsync<AppException>(() => service.RetriggerFileAsync("plainfile.csv", "data/plainfile.csv", null));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("plainfile.csv", ex.Message);
    }
}
