using System.Globalization;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Core.Errors;
using Monitor.Core.Options;
using Monitor.Data.Repositories;
using Monitor.Files.Domain;

namespace Monitor.Files;

/// <summary>Direct translation of <c>S3Service</c> (features/s3/s3.service.ts).</summary>
public sealed class FilesService : IFilesService
{
    private const string RawListingCacheKeyPrefix = "Monitor.Files.RawS3Listing:";
    private static readonly TimeSpan RawListingCacheTtl = TimeSpan.FromSeconds(60);

    private readonly IAmazonS3 _s3Client;
    private readonly IPrimaryDb _primaryDb;
    private readonly IMemoryCache _cache;
    private readonly AwsOptions _awsOptions;
    private readonly ILogger<FilesService> _logger;

    public FilesService(
        IAmazonS3 s3Client,
        IPrimaryDb primaryDb,
        IMemoryCache cache,
        IOptions<AwsOptions> awsOptions,
        ILogger<FilesService> logger)
    {
        _s3Client = s3Client;
        _primaryDb = primaryDb;
        _cache = cache;
        _awsOptions = awsOptions.Value;
        _logger = logger;
    }

    public async Task<ListFilesResult> ListFilesAsync(ListFilesQuery query, CancellationToken cancellationToken)
    {
        var rootPrefix = _awsOptions.S3Prefix ?? string.Empty;
        var fetchPrefix = !string.IsNullOrEmpty(query.Prefix) && query.Prefix != rootPrefix ? query.Prefix : rootPrefix;

        _logger.LogInformation(
            "S3 list files: fetchPrefix={FetchPrefix} search={Search} startDate={StartDate} endDate={EndDate}",
            fetchPrefix, query.Search, query.StartDate, query.EndDate);

        var allItems = await FetchAllObjectsAsync(fetchPrefix, cancellationToken);

        var folders = ExtractFolders(allItems, rootPrefix);
        var suggestions = ExtractSuggestions(allItems);

        IEnumerable<S3FileItem> filtered = allItems;

        if (!string.IsNullOrEmpty(query.Prefix) && query.Prefix != rootPrefix)
        {
            filtered = filtered.Where(f => f.Key.StartsWith(query.Prefix, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var q = query.Search.Trim().ToLowerInvariant();
            filtered = filtered.Where(f => f.Name.ToLowerInvariant().Contains(q, StringComparison.Ordinal));
        }

        if (query.StartDate is not null)
        {
            var from = ParseIsoOrMinValue(query.StartDate);
            filtered = filtered.Where(f => ParseIsoOrMinValue(f.LastModified) >= from);
        }

        if (query.EndDate is not null)
        {
            // Source: `to.setHours(23, 59, 59, 999)` — end-of-day in the server's local
            // time zone, which in this deployment is UTC (see server.ts's container
            // runtime), so end-of-day-UTC reproduces the same instant.
            var to = ParseIsoOrMinValue(query.EndDate).Date.AddDays(1).AddMilliseconds(-1);
            filtered = filtered.Where(f => ParseIsoOrMinValue(f.LastModified) <= to);
        }

        Comparison<S3FileItem> comparison = query.SortBy switch
        {
            "name" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal),
            "size" => (a, b) => a.Size.CompareTo(b.Size),
            _ => (a, b) => ParseIsoOrMinValue(a.LastModified).CompareTo(ParseIsoOrMinValue(b.LastModified)),
        };
        // Source: `sortOrder === 'asc' ? cmp : -cmp` — only the literal 'asc' flips to
        // ascending; any other value (including the 'desc' default) sorts descending.
        var ascending = query.SortOrder == "asc";

        // LINQ OrderBy is a stable sort, matching Array.prototype.sort's spec-guaranteed
        // stability (relied on implicitly by the source for equal-key ordering).
        var comparer = Comparer<S3FileItem>.Create((a, b) => ascending ? comparison(a, b) : -comparison(a, b));
        var sorted = filtered.OrderBy(x => x, comparer).ToList();

        var total = sorted.Count;
        var totalSize = sorted.Sum(f => f.Size);
        var totalPages = (int)Math.Ceiling(total / (double)query.Limit);
        var offset = (query.Page - 1) * query.Limit;
        var paged = sorted.Skip(Math.Max(offset, 0)).Take(query.Limit).ToList();

        return new ListFilesResult(
            paged,
            total,
            totalSize,
            query.Page,
            query.Limit,
            totalPages,
            folders,
            suggestions,
            _awsOptions.S3Bucket,
            rootPrefix);
    }

    public async Task<S3DownloadResult> StreamFileAsync(string key, CancellationToken cancellationToken)
    {
        GetObjectResponse response;
        try
        {
            response = await _s3Client.GetObjectAsync(new GetObjectRequest { BucketName = _awsOptions.S3Bucket, Key = key }, cancellationToken);
        }
        catch (Exception err)
        {
            throw S3ErrorMapper.ToAppException(err, _logger, _awsOptions.S3Bucket);
        }

        if (response.ResponseStream is null)
        {
            throw new AppException(404, "File body is empty");
        }

        var fileName = key.Split('/').LastOrDefault();
        fileName = string.IsNullOrEmpty(fileName) ? "download" : fileName;

        var contentLength = response.ContentLength > 0 ? response.ContentLength : (long?)null;
        return new S3DownloadResult(
            response.ResponseStream,
            response.Headers.ContentType ?? "application/octet-stream",
            contentLength,
            fileName);
    }

    public async Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;

        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = _awsOptions.S3Bucket,
                Prefix = _awsOptions.S3Prefix ?? string.Empty,
                MaxKeys = 1000,
                ContinuationToken = continuationToken,
            };

            ListObjectsV2Response response;
            try
            {
                response = await _s3Client.ListObjectsV2Async(request, cancellationToken);
            }
            catch (Exception err)
            {
                throw S3ErrorMapper.ToAppException(err, _logger, _awsOptions.S3Bucket);
            }

            foreach (var obj in response.S3Objects ?? new List<S3Object>())
            {
                if (obj.Key is null || obj.Key.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                var slashIdx = obj.Key.LastIndexOf('/');
                var name = slashIdx >= 0 ? obj.Key[(slashIdx + 1)..] : obj.Key;
                var derived = PipelineNaming.DerivePipelineName(name);
                if (derived is not null)
                {
                    seen.Add(derived);
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);

        return seen.ToList();
    }

    public async Task<RetriggerResult> RetriggerFileAsync(
        string fileName,
        string s3Key,
        string? pipelineNameOverride,
        CancellationToken cancellationToken)
    {
        var pipelineName = pipelineNameOverride ?? PipelineNaming.DerivePipelineName(fileName);
        if (string.IsNullOrEmpty(pipelineName))
        {
            throw new AppException(400, $"Cannot derive pipeline name from file: {fileName}");
        }

        var fileDate = PipelineNaming.ExtractDateFromFilename(fileName);
        var dateStr = fileDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var jobId = Guid.NewGuid();

        await _primaryDb.QueryAsync<int>(
            @"INSERT INTO recovery_jobs
                 (id, pipeline_name, start_date, end_date, files_found,
                  files_processed, files_failed, status, triggered_by)
               VALUES (@JobId, @PipelineName, @DateStr, @DateStr, 1, 0, 0, 'Pending', 'S3 Files Page')",
            new { JobId = jobId, PipelineName = pipelineName, DateStr = dateStr },
            cancellationToken);

        // Auto-create pipeline definition if it doesn't exist.
        await _primaryDb.QueryAsync<int>(
            @"INSERT INTO pipelines (id, name, display_name, is_active)
               VALUES (@Id, @Name, @DisplayName, true)
               ON CONFLICT (name) DO NOTHING",
            new { Id = Guid.NewGuid(), Name = pipelineName, DisplayName = ToDisplayName(pipelineName) },
            cancellationToken);

        _logger.LogInformation(
            "Retrigger queued from S3 Files page: jobId={JobId} pipelineName={PipelineName} fileName={FileName} s3Key={S3Key}",
            jobId, pipelineName, fileName, s3Key);

        return new RetriggerResult(jobId.ToString(), pipelineName, "queued");
    }

    public async Task<IReadOnlyList<FileDateEntry>> GetFileNamesForDateRangeAsync(
        string? startDate,
        string? endDate,
        CancellationToken cancellationToken)
    {
        var items = await FetchAllObjectsCachedAsync(_awsOptions.S3Prefix ?? string.Empty);

        IEnumerable<S3FileItem> filtered = items;

        if (startDate is not null)
        {
            var from = ParseIsoOrMinValue(startDate);
            filtered = filtered.Where(f => ParseIsoOrMinValue(f.LastModified) >= from);
        }

        if (endDate is not null)
        {
            var to = ParseIsoOrMinValue(endDate).Date.AddDays(1).AddMilliseconds(-1);
            filtered = filtered.Where(f => ParseIsoOrMinValue(f.LastModified) <= to);
        }

        return filtered.Select(f => new FileDateEntry(f.Name, f.LastModified)).ToList();
    }

    // ── raw-listing cache: Task.WhenAll-safe de-duplication via IMemoryCache + AsyncLazy ──
    // Only GetFileNamesForDateRangeAsync (reconciliation) reads from this cache.
    // ListFilesAsync always calls FetchAllObjectsAsync directly so the S3 Files page
    // always shows up-to-date content.
    private async Task<IReadOnlyList<S3FileItem>> FetchAllObjectsCachedAsync(string prefix)
    {
        var cacheKey = RawListingCacheKeyPrefix + prefix;
        var lazy = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = RawListingCacheTtl;
            return new AsyncLazy<IReadOnlyList<S3FileItem>>(() => FetchAllObjectsAsync(prefix, CancellationToken.None));
        })!;

        try
        {
            return await lazy.Value;
        }
        catch
        {
            // Don't leave a faulted fetch cached — the next caller should retry, mirroring
            // the source clearing `rawS3Inflight` in its `.catch` before rethrowing.
            _cache.Remove(cacheKey);
            throw;
        }
    }

    private async Task<IReadOnlyList<S3FileItem>> FetchAllObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        var items = new List<S3FileItem>();
        string? continuationToken = null;

        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = _awsOptions.S3Bucket,
                Prefix = prefix,
                MaxKeys = 1000,
                ContinuationToken = continuationToken,
            };

            ListObjectsV2Response response;
            try
            {
                response = await _s3Client.ListObjectsV2Async(request, cancellationToken);
            }
            catch (Exception err)
            {
                throw S3ErrorMapper.ToAppException(err, _logger, _awsOptions.S3Bucket);
            }

            foreach (var obj in response.S3Objects ?? new List<S3Object>())
            {
                if (obj.Key is null || obj.Key.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                var key = obj.Key;
                var slashIdx = key.LastIndexOf('/');
                var name = slashIdx >= 0 ? key[(slashIdx + 1)..] : key;
                var objPrefix = slashIdx >= 0 ? key[..(slashIdx + 1)] : string.Empty;

                var lastModifiedUtc = obj.LastModified == default
                    ? DateTime.UnixEpoch
                    : DateTime.SpecifyKind(obj.LastModified, DateTimeKind.Utc);

                items.Add(new S3FileItem(
                    key,
                    name,
                    objPrefix,
                    obj.Size,
                    lastModifiedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    (obj.ETag ?? string.Empty).Replace("\"", string.Empty),
                    PipelineNaming.DerivePipelineName(name)));
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);

        return items;
    }

    private static IReadOnlyList<string> ExtractFolders(IReadOnlyList<S3FileItem> items, string rootPrefix)
    {
        var folderSet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Prefix) || item.Prefix == rootPrefix)
            {
                continue;
            }

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

    private static IReadOnlyList<string> ExtractSuggestions(IReadOnlyList<S3FileItem> items)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var suggestion = PipelineNaming.SuggestionFor(item.Name);
            if (!string.IsNullOrEmpty(suggestion))
            {
                seen.Add(suggestion);
            }
        }

        return seen.ToList();
    }

    // Source: pipelineName.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase())
    private static string ToDisplayName(string pipelineName)
    {
        var withSpaces = pipelineName.Replace('_', ' ');
        return Regex.Replace(withSpaces, @"\b\w", m => m.Value.ToUpperInvariant());
    }

    private static DateTime ParseIsoOrMinValue(string value)
    {
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTime.MinValue;
    }
}
