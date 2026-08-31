using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Core.Errors;
using Monitor.Core.Options;
using Monitor.Data.Repositories;
using Monitor.Files.Caching;
using Monitor.Files.Domain;

namespace Monitor.Files;

/// <inheritdoc cref="IS3FilesService"/>
public sealed class S3FilesService : IS3FilesService
{
    // Caches the full bucket listing for 60s so the dashboard and file monitor
    // never trigger more than one ListObjectsV2 round-trip per minute. Only
    // GetFileNamesForDateRangeAsync (used by reconciliation) reads from this
    // cache — ListFilesAsync (the S3 Files page) always calls FetchAllObjectsAsync
    // directly so the page always shows up-to-date content. Mirrors the source's
    // rawS3Cache/rawS3Inflight module-level variables (s3.service.ts).
    private static readonly TimeSpan RawListingCacheTtl = TimeSpan.FromSeconds(60);
    private const string RawListingCacheKeyPrefix = "s3:raw-listing:";

    private readonly IAmazonS3 _s3Client;
    private readonly IPrimaryDb _primaryDb;
    private readonly IMemoryCache _cache;
    private readonly S3ErrorMapper _errorMapper;
    private readonly ILogger<S3FilesService> _logger;
    private readonly AwsOptions _awsOptions;

    public S3FilesService(
        IAmazonS3 s3Client,
        IPrimaryDb primaryDb,
        IMemoryCache cache,
        S3ErrorMapper errorMapper,
        ILogger<S3FilesService> logger,
        IOptions<AwsOptions> awsOptions)
    {
        _s3Client = s3Client;
        _primaryDb = primaryDb;
        _cache = cache;
        _errorMapper = errorMapper;
        _logger = logger;
        _awsOptions = awsOptions.Value;
    }

    public async Task<ListFilesResult> ListFilesAsync(ListFilesParams parameters, CancellationToken cancellationToken = default)
    {
        var rootPrefix = _awsOptions.S3Prefix ?? string.Empty;
        var prefix = parameters.Prefix;
        var fetchPrefix = !string.IsNullOrEmpty(prefix) && prefix != rootPrefix ? prefix : rootPrefix;

        _logger.LogInformation(
            "S3 list files {FetchPrefix} {Search} {StartDate} {EndDate}",
            fetchPrefix, parameters.Search, parameters.StartDate, parameters.EndDate);

        var items = await FetchAllObjectsAsync(fetchPrefix, cancellationToken);

        var folders = ExtractFolders(items, rootPrefix);
        var suggestions = PipelineNameDeriver.ExtractSuggestions(items);

        IEnumerable<S3FileItem> filtered = items;

        if (!string.IsNullOrEmpty(prefix) && prefix != rootPrefix)
        {
            filtered = filtered.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(parameters.Search))
        {
            var q = parameters.Search.Trim().ToLowerInvariant();
            filtered = filtered.Where(f => f.Name.ToLowerInvariant().Contains(q));
        }

        if (!string.IsNullOrEmpty(parameters.StartDate))
        {
            // Source: `new Date(startDate).getTime()` yields NaN for an unparseable
            // date, and every NaN comparison is false, so a malformed value filters
            // out ALL items rather than being ignored. TryParseDate returning null
            // reproduces that by making the predicate unconditionally false.
            var from = TryParseDate(parameters.StartDate);
            filtered = filtered.Where(f => from.HasValue && ParseIso(f.LastModified) >= from.Value);
        }
        if (!string.IsNullOrEmpty(parameters.EndDate))
        {
            var to = TryParseDate(parameters.EndDate)?.Date.AddHours(23).AddMinutes(59).AddSeconds(59).AddMilliseconds(999);
            filtered = filtered.Where(f => to.HasValue && ParseIso(f.LastModified) <= to.Value);
        }

        var sorted = SortItems(filtered.ToList(), parameters.SortBy, parameters.SortOrder);

        var total = sorted.Count;
        var totalSize = sorted.Sum(f => f.Size);
        var totalPages = parameters.Limit > 0 ? (int)Math.Ceiling(total / (double)parameters.Limit) : 0;
        var offset = (parameters.Page - 1) * parameters.Limit;
        var paged = sorted.Skip(Math.Max(offset, 0)).Take(parameters.Limit).ToList();

        return new ListFilesResult(
            Files: paged,
            Total: total,
            TotalSize: totalSize,
            Page: parameters.Page,
            Limit: parameters.Limit,
            TotalPages: totalPages,
            Folders: folders,
            Suggestions: suggestions,
            Bucket: _awsOptions.S3Bucket,
            RootPrefix: rootPrefix);
    }

    public async Task<S3FileDownload> GetFileDownloadAsync(string key, CancellationToken cancellationToken = default)
    {
        GetObjectResponse response;
        try
        {
            response = await _s3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _awsOptions.S3Bucket,
                Key = key,
            }, cancellationToken);
        }
        catch (Exception err)
        {
            throw _errorMapper.ToAppException(err);
        }

        if (response.ResponseStream is null)
        {
            throw new AppException(404, "File body is empty");
        }

        var fileName = key.Split('/').LastOrDefault() ?? "download";
        long? contentLength = response.Headers.ContentLength > 0 ? response.Headers.ContentLength : null;

        return new S3FileDownload(response.ResponseStream, response.Headers.ContentType, contentLength, fileName);
    }

    public async Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken = default)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;

        // Scan the entire bucket with no size cap — only pipeline names are kept in memory.
        do
        {
            ListObjectsV2Response output;
            try
            {
                output = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _awsOptions.S3Bucket,
                    Prefix = _awsOptions.S3Prefix ?? string.Empty,
                    MaxKeys = 1000,
                    ContinuationToken = continuationToken,
                }, cancellationToken);
            }
            catch (Exception err)
            {
                throw _errorMapper.ToAppException(err);
            }

            foreach (var obj in output.S3Objects ?? new List<S3Object>())
            {
                if (string.IsNullOrEmpty(obj.Key) || obj.Key.EndsWith('/')) continue;
                var slashIdx = obj.Key.LastIndexOf('/');
                var name = slashIdx >= 0 ? obj.Key[(slashIdx + 1)..] : obj.Key;
                var pipelineName = PipelineNameDeriver.DerivePipelineName(name);
                if (pipelineName is not null) seen.Add(pipelineName);
            }

            continuationToken = output.IsTruncated ? output.NextContinuationToken : null;
        } while (continuationToken is not null);

        return seen.ToList();
    }

    public async Task<RetriggerResult> RetriggerFileAsync(string fileName, string s3Key, string? pipelineNameOverride, CancellationToken cancellationToken = default)
    {
        var pipelineName = !string.IsNullOrEmpty(pipelineNameOverride) ? pipelineNameOverride : PipelineNameDeriver.DerivePipelineName(fileName);
        if (string.IsNullOrEmpty(pipelineName))
        {
            throw new AppException(400, $"Cannot derive pipeline name from file: {fileName}");
        }

        var fileDate = PipelineNameDeriver.ExtractDateFromFilename(fileName);
        var dateStr = fileDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var jobId = Guid.NewGuid();

        await _primaryDb.QueryAsync<int>(
            @"INSERT INTO recovery_jobs
                (id, pipeline_name, start_date, end_date, files_found,
                 files_processed, files_failed, status, triggered_by)
              VALUES (@Id, @PipelineName, @StartDate, @EndDate, 1, 0, 0, 'Pending', 'S3 Files Page')",
            new { Id = jobId, PipelineName = pipelineName, StartDate = dateStr, EndDate = dateStr },
            cancellationToken);

        // Auto-create pipeline definition if it doesn't exist.
        await _primaryDb.QuerySingleAsync<int>(
            @"INSERT INTO pipelines (id, name, display_name, is_active)
              VALUES (@Id, @Name, @DisplayName, true)
              ON CONFLICT (name) DO NOTHING",
            new { Id = Guid.NewGuid(), Name = pipelineName, DisplayName = PipelineNameDeriver.ToDisplayName(pipelineName) },
            cancellationToken);

        _logger.LogInformation(
            "Retrigger queued from S3 Files page {JobId} {PipelineName} {FileName} {S3Key}",
            jobId, pipelineName, fileName, s3Key);

        return new RetriggerResult(jobId.ToString(), pipelineName, "queued");
    }

    public async Task<IReadOnlyList<FileNameDateEntry>> GetFileNamesForDateRangeAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default)
    {
        // Uses the 60-second cache — safe to call from dashboard + file monitor in the
        // same session without triggering duplicate S3 API round-trips.
        var items = await FetchAllObjectsCachedAsync(_awsOptions.S3Prefix ?? string.Empty, cancellationToken);

        IEnumerable<S3FileItem> filtered = items;
        if (!string.IsNullOrEmpty(startDate))
        {
            var from = TryParseDate(startDate);
            filtered = filtered.Where(f => from.HasValue && ParseIso(f.LastModified) >= from.Value);
        }
        if (!string.IsNullOrEmpty(endDate))
        {
            var to = TryParseDate(endDate)?.Date.AddHours(23).AddMinutes(59).AddSeconds(59).AddMilliseconds(999);
            filtered = filtered.Where(f => to.HasValue && ParseIso(f.LastModified) <= to.Value);
        }

        return filtered.Select(f => new FileNameDateEntry(f.Name, f.LastModified)).ToList();
    }

    private Task<IReadOnlyList<S3FileItem>> FetchAllObjectsCachedAsync(string prefix, CancellationToken cancellationToken)
    {
        var key = RawListingCacheKeyPrefix + prefix;
        var lazy = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = RawListingCacheTtl;
            _logger.LogInformation("S3 cache refreshed, TTL {TtlSeconds}s", RawListingCacheTtl.TotalSeconds);
            // Cancellation isn't tied to any one caller: the fetched result is
            // shared by every concurrent request that hits this cache key.
            return new AsyncLazy<IReadOnlyList<S3FileItem>>(() => FetchAllObjectsAsync(prefix, CancellationToken.None));
        })!;

        return AwaitAndEvictOnFailure(key, lazy);
    }

    private async Task<IReadOnlyList<S3FileItem>> AwaitAndEvictOnFailure(string key, AsyncLazy<IReadOnlyList<S3FileItem>> lazy)
    {
        try
        {
            return await lazy.Value;
        }
        catch
        {
            // Mirrors the source's rawS3Inflight = null on failure: don't let a
            // failed fetch poison the cache for the rest of the TTL window.
            _cache.Remove(key);
            throw;
        }
    }

    private async Task<IReadOnlyList<S3FileItem>> FetchAllObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        var items = new List<S3FileItem>();
        string? continuationToken = null;

        do
        {
            ListObjectsV2Response output;
            try
            {
                output = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _awsOptions.S3Bucket,
                    Prefix = prefix,
                    MaxKeys = 1000,
                    ContinuationToken = continuationToken,
                }, cancellationToken);
            }
            catch (Exception err)
            {
                throw _errorMapper.ToAppException(err);
            }

            foreach (var obj in output.S3Objects ?? new List<S3Object>())
            {
                if (string.IsNullOrEmpty(obj.Key) || obj.Key.EndsWith('/')) continue;

                var key = obj.Key;
                var slashIdx = key.LastIndexOf('/');
                var name = slashIdx >= 0 ? key[(slashIdx + 1)..] : key;
                var objPrefix = slashIdx >= 0 ? key[..(slashIdx + 1)] : string.Empty;

                items.Add(new S3FileItem(
                    Key: key,
                    Name: name,
                    Prefix: objPrefix,
                    Size: obj.Size,
                    LastModified: FormatIso(obj.LastModified),
                    ETag: (obj.ETag ?? string.Empty).Replace("\"", string.Empty),
                    PipelineName: PipelineNameDeriver.DerivePipelineName(name)));
            }

            continuationToken = output.IsTruncated ? output.NextContinuationToken : null;
        } while (continuationToken is not null);

        return items;
    }

    private static IReadOnlyList<string> ExtractFolders(IReadOnlyList<S3FileItem> items, string rootPrefix)
    {
        var folderSet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Prefix) || item.Prefix == rootPrefix) continue;

            var relative = item.Prefix.StartsWith(rootPrefix, StringComparison.Ordinal)
                ? item.Prefix[rootPrefix.Length..]
                : item.Prefix;

            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var accumulated = rootPrefix;
            foreach (var part in parts)
            {
                accumulated += part + "/";
                folderSet.Add(accumulated);
            }
        }
        return folderSet.ToList();
    }

    private static List<S3FileItem> SortItems(List<S3FileItem> items, string sortBy, string sortOrder)
    {
        Comparison<S3FileItem> cmp = sortBy switch
        {
            "name" => (a, b) => string.CompareOrdinal(a.Name, b.Name),
            "size" => (a, b) => a.Size.CompareTo(b.Size),
            _ => (a, b) => ParseIso(a.LastModified).CompareTo(ParseIso(b.LastModified)),
        };

        items.Sort((a, b) => sortOrder == "asc" ? cmp(a, b) : -cmp(a, b));
        return items;
    }

    private static DateTime FromUnixEpoch() => new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string FormatIso(DateTime? value)
    {
        var dt = (value ?? FromUnixEpoch()).ToUniversalTime();
        return dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static DateTime ParseIso(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static DateTime? TryParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
