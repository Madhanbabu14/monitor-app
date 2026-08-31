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
/// Exercises <see cref="S3FilesService.GetFileNamesForDateRangeAsync"/> and the
/// 60-second raw-listing cache it shares with the (not-yet-translated) dashboard
/// reconciliation path — the direct translation of s3.service.ts's
/// <c>getFileNamesForDateRange</c> / <c>fetchAllObjectsCached</c> /
/// <c>rawS3Cache</c> / <c>rawS3Inflight</c>. Kept separate from
/// <c>S3FilesServiceTests</c> since it focuses specifically on cache-hit,
/// cache-dedup, and cache-eviction-on-failure behavior rather than listing/paging.
/// </summary>
public class S3FilesServiceCachingTests
{
    private sealed class FakeS3Client : AmazonS3Client
    {
        public FakeS3Client()
            : base(new BasicAWSCredentials("test", "test"), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
        {
        }

        public int ListCallCount { get; private set; }
        public Func<ListObjectsV2Request, ListObjectsV2Response>? Handler { get; set; }
        public Exception? Exception { get; set; }
        private readonly TaskCompletionSource _gate = new();
        public bool GateEnabled { get; set; }

        public void Release() => _gate.TrySetResult();

        public override async Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            if (GateEnabled) await _gate.Task;
            if (Exception is not null) throw Exception;
            return Handler!(request);
        }

        private int _callCount;
        public int GetCallCount() => _callCount;
    }

    private sealed class FakePrimaryDb : IPrimaryDb
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());

        public Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(default(T));

        public Task<TResult> WithTransactionAsync<TResult>(Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static S3Object MakeObject(string key, DateTime lastModified, long size = 10) =>
        new() { Key = key, Size = size, LastModified = lastModified, ETag = "etag" };

    private static (S3FilesService Service, FakeS3Client S3, IMemoryCache Cache) CreateService(string bucket = "test-bucket", string prefix = "data/")
    {
        var s3 = new FakeS3Client();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = Microsoft.Extensions.Options.Options.Create(new AwsOptions { S3Bucket = bucket, S3Prefix = prefix });
        var service = new S3FilesService(
            s3,
            new FakePrimaryDb(),
            cache,
            new S3ErrorMapper(NullLogger<S3ErrorMapper>.Instance, options),
            NullLogger<S3FilesService>.Instance,
            options);
        return (service, s3, cache);
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_NoFilters_ReturnsNameAndLastModifiedForEveryItem()
    {
        var (service, s3, _) = CreateService();
        s3.Handler = _ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object>
            {
                MakeObject("data/a.csv", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                MakeObject("data/b.csv", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            },
            IsTruncated = false,
        };

        var result = await service.GetFileNamesForDateRangeAsync(null, null);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Name == "a.csv");
        Assert.Contains(result, e => e.Name == "b.csv");
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_FiltersByStartAndEndDate_InclusiveOfEndOfDay()
    {
        var (service, s3, _) = CreateService();
        s3.Handler = _ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object>
            {
                MakeObject("data/in.csv", new DateTime(2026, 3, 15, 23, 0, 0, DateTimeKind.Utc)),
                MakeObject("data/before.csv", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
                MakeObject("data/after.csv", new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)),
            },
            IsTruncated = false,
        };

        var result = await service.GetFileNamesForDateRangeAsync("2026-03-10", "2026-03-20");

        var name = Assert.Single(result);
        Assert.Equal("in.csv", name.Name);
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_UnparseableStartDate_ReturnsNoMatches()
    {
        var (service, s3, _) = CreateService();
        s3.Handler = _ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/x.csv", DateTime.UtcNow) },
            IsTruncated = false,
        };

        var result = await service.GetFileNamesForDateRangeAsync("not-a-date", null);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_SecondCallWithinTtl_ServesFromCache_NoSecondS3RoundTrip()
    {
        var (service, s3, _) = CreateService();
        s3.Handler = _ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/x.csv", DateTime.UtcNow) },
            IsTruncated = false,
        };

        await service.GetFileNamesForDateRangeAsync(null, null);
        await service.GetFileNamesForDateRangeAsync(null, null);
        await service.GetFileNamesForDateRangeAsync("2026-01-01", null);

        // All three calls share the same 60s cache entry keyed off the configured S3
        // prefix (independent of the date-range filter args, which are applied
        // in-memory after the cached fetch) — only one ListObjectsV2 round-trip total.
        Assert.Equal(1, s3.GetCallCount());
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_ConcurrentColdCacheCallers_DedupToOneS3RoundTrip()
    {
        var (service, s3, _) = CreateService();
        s3.GateEnabled = true;
        s3.Handler = _ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/x.csv", DateTime.UtcNow) },
            IsTruncated = false,
        };

        var call1 = service.GetFileNamesForDateRangeAsync(null, null);
        var call2 = service.GetFileNamesForDateRangeAsync(null, null);
        var call3 = service.GetFileNamesForDateRangeAsync(null, null);

        s3.Release();
        var results = await Task.WhenAll(call1, call2, call3);

        Assert.Equal(1, s3.GetCallCount());
        Assert.All(results, r => Assert.Single(r));
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_FetchFails_DoesNotPoisonCacheForSubsequentCalls()
    {
        var (service, s3, _) = CreateService();
        s3.Exception = new AmazonS3Exception("down") { ErrorCode = "InternalError" };

        var ex = await Assert.ThrowsAsync<AppException>(() => service.GetFileNamesForDateRangeAsync(null, null));
        Assert.Equal(500, ex.StatusCode);

        // A failed fetch must evict itself from the cache (mirrors the source's
        // `rawS3Inflight = null` on catch) so a transient S3 outage doesn't lock every
        // caller out of a good listing for the rest of the 60s TTL window.
        s3.Exception = null;
        s3.Handler = _ => new ListObjectsV2Response
        {
            S3Objects = new List<S3Object> { MakeObject("data/recovered.csv", DateTime.UtcNow) },
            IsTruncated = false,
        };

        var result = await service.GetFileNamesForDateRangeAsync(null, null);

        Assert.Single(result);
        Assert.Equal("recovered.csv", result[0].Name);
        Assert.Equal(2, s3.GetCallCount());
    }

    [Fact]
    public async Task GetFileNamesForDateRangeAsync_DifferentPrefixes_UseIndependentCacheEntries()
    {
        // ListFilesAsync always bypasses the cache, but GetFileNamesForDateRangeAsync's
        // cache key is derived from AwsOptions.S3Prefix, so two services configured with
        // different prefixes must never share a cache entry.
        var (serviceA, s3A, _) = CreateService(prefix: "data/");
        var (serviceB, s3B, _) = CreateService(prefix: "other/");
        s3A.Handler = _ => new ListObjectsV2Response { S3Objects = new List<S3Object> { MakeObject("data/a.csv", DateTime.UtcNow) }, IsTruncated = false };
        s3B.Handler = _ => new ListObjectsV2Response { S3Objects = new List<S3Object> { MakeObject("other/b.csv", DateTime.UtcNow) }, IsTruncated = false };

        var resultA = await serviceA.GetFileNamesForDateRangeAsync(null, null);
        var resultB = await serviceB.GetFileNamesForDateRangeAsync(null, null);

        Assert.Equal("a.csv", Assert.Single(resultA).Name);
        Assert.Equal("b.csv", Assert.Single(resultB).Name);
    }
}
