using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Monitor.Data.Repositories;
using Monitor.Files;
using Monitor.Files.Domain;
using Monitor.Operations.Domain;

namespace Monitor.Operations;

/// <inheritdoc cref="IMonitorService"/>
public sealed class MonitorService : IMonitorService
{
    // Pipeline files follow the pattern: <name>_<8+digit id>...
    // Files that DON'T match (_SUCCESS, manifest.json, part-*.csv, etc.) are
    // not pipeline data files and must never be counted as "Not Processed".
    // NOTE: this is monitor.service.ts's OWN PIPELINE_FILE_RE — anchored to a
    // leading letter — and is deliberately NOT the same regex as
    // Monitor.Files.PipelineNameDeriver (s3.service.ts's non-greedy `^(.+?)_\d{8,}`).
    // The two source files defined independent patterns for independent purposes.
    private static readonly Regex PipelineFileRegex = new(@"^([a-zA-Z][a-zA-Z0-9_]*)_\d{8,}", RegexOptions.Compiled);

    private readonly IOperationalDb _operationalDb;
    private readonly IS3FilesService _s3FilesService;
    private readonly ILogger<MonitorService> _logger;

    public MonitorService(IOperationalDb operationalDb, IS3FilesService s3FilesService, ILogger<MonitorService> logger)
    {
        _operationalDb = operationalDb;
        _s3FilesService = s3FilesService;
        _logger = logger;
    }

    private static string MapStatus(int code) => code switch
    {
        1 => MonitorStatuses.Processed,
        -1 => MonitorStatuses.Failed,
        0 => MonitorStatuses.InProgress,
        _ => MonitorStatuses.NotProcessed,
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? FormatIsoOrNull(DateTime? value) =>
        value is null ? null : value.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// Mirrors `new Date(x).getTime()` for sort comparisons: a missing value is
    /// 0 (falsy check, matches source exactly); an unparseable-but-present
    /// value is also treated as 0 here, whereas the source would get NaN —
    /// V8's Array#sort with a NaN-returning comparator has unspecified
    /// ordering for those entries, so this is a deliberate approximation of a
    /// pathological edge case (a malformed timestamp string in the DB).
    /// </summary>
    private static long ParseTimestampMillis(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero).ToUnixTimeMilliseconds()
            : 0;
    }

    private static string? DerivePipelineFileName(string fileName)
    {
        var match = PipelineFileRegex.Match(fileName);
        return match.Success ? match.Groups[1].Value : null;
    }

    private sealed record NotProcessedS3File(string Name, string LastModified, string PipelineName);

    private sealed class FileNameRow
    {
        public string FileName { get; init; } = string.Empty;
    }

    /// <summary>
    /// Direct translation of the free function <c>s3FilesNotInDb</c>
    /// (monitor.service.ts): given a list of S3 file names, returns those that
    /// have NO record in the operational DB. Returns nothing (rather than
    /// treating every S3 file as "Not Processed") when the operational DB is
    /// unreachable, since we cannot determine which files are unprocessed
    /// without it.
    /// </summary>
    private async Task<IReadOnlyList<NotProcessedS3File>> GetS3FilesNotInDbAsync(
        IReadOnlyList<FileNameDateEntry> s3Files, string? pipelineFilter, CancellationToken cancellationToken)
    {
        var eligible = new List<(string Name, string LastModified, string PipelineName)>();
        foreach (var f in s3Files)
        {
            var derived = DerivePipelineFileName(f.Name);
            if (derived is null) continue;
            if (!string.IsNullOrEmpty(pipelineFilter) && derived != pipelineFilter) continue;
            eligible.Add((f.Name, f.LastModified, derived));
        }

        if (eligible.Count == 0) return Array.Empty<NotProcessedS3File>();

        var names = eligible.Select(e => e.Name).ToArray();
        var result = await _operationalDb.QueryAsync<FileNameRow>(
            @"SELECT DISTINCT file_name AS ""FileName"" FROM rt.pipeline_operational_logs WHERE file_name = ANY(@Names::text[])",
            new { Names = names },
            cancellationToken);

        if (!result.Available) return Array.Empty<NotProcessedS3File>();

        var dbSet = new HashSet<string>(result.Value!.Select(r => r.FileName), StringComparer.Ordinal);

        return eligible
            .Where(e => !dbSet.Contains(e.Name))
            .Select(e => new NotProcessedS3File(e.Name, e.LastModified, e.PipelineName))
            .ToList();
    }

    private sealed class FileMonitorRawRow
    {
        public string FileName { get; init; } = string.Empty;
        public string? PipelineName { get; init; }
        public string? ProcessDateTime { get; init; }
        public int ProcessStatus { get; init; }
        public string? ProcessRemark { get; init; }
        public int RowsInserted { get; init; }
        public int RowsUpdated { get; init; }
        public long TotalCount { get; init; }
    }

    // ── File Monitor — DB-only, legacy /monitor/files endpoint ────────────────
    public async Task<FileMonitorResult> GetFileMonitorAsync(MonitorParams parameters, CancellationToken cancellationToken = default)
    {
        var startDate = NullIfEmpty(parameters.StartDate);
        var endDate = NullIfEmpty(parameters.EndDate);
        var status = NullIfEmpty(parameters.Status);
        var pipeline = NullIfEmpty(parameters.Pipeline);
        var search = NullIfEmpty(parameters.Search);
        var page = parameters.Page;
        var limit = parameters.Limit;
        var sortBy = string.IsNullOrEmpty(parameters.SortBy) ? "fileReceivedDate" : parameters.SortBy;
        var sortOrder = string.IsNullOrEmpty(parameters.SortOrder) ? "desc" : parameters.SortOrder;

        var orderCol = sortBy switch
        {
            "fileName" => "file_name",
            "pipelineName" => "pipeline_name",
            "recordsInserted" => "rows_inserted",
            "recordsUpdated" => "rows_updated",
            _ => "process_date_time",
        };
        var orderDir = sortOrder == "asc" ? "ASC" : "DESC";

        // orderCol/orderDir are drawn from the fixed allowlists above (never raw
        // caller input), so splicing them into ORDER BY is safe — matches the
        // source's own ternary-built `orderCol`/`orderDir` spliced into its SQL template literal.
        var sql = $@"
            WITH latest AS (
              SELECT DISTINCT ON (file_name)
                file_name, pipeline_name, process_date_time, process_status, process_remark,
                GREATEST(COALESCE(rows_inserted, 0), 0) AS rows_inserted,
                GREATEST(COALESCE(rows_updated,  0), 0) AS rows_updated
              FROM rt.pipeline_operational_logs
              WHERE
                (@StartDate::date IS NULL OR (process_date_time::timestamptz)::date >= @StartDate::date)
                AND (@EndDate::date IS NULL OR (process_date_time::timestamptz)::date <= @EndDate::date)
                AND (@Pipeline::text IS NULL OR pipeline_name = @Pipeline)
                AND (@Search::text IS NULL OR file_name ILIKE '%' || @Search || '%')
              ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST
            ),
            filtered AS (
              SELECT * FROM latest
              WHERE (
                @Status::text IS NULL
                OR (@Status = 'Processed'   AND process_status = 1)
                OR (@Status = 'Failed'      AND process_status = -1)
                OR (@Status = 'In Progress' AND process_status = 0)
              )
            )
            SELECT file_name AS ""FileName"", pipeline_name AS ""PipelineName"",
                   process_date_time AS ""ProcessDateTime"", process_status AS ""ProcessStatus"",
                   process_remark AS ""ProcessRemark"", rows_inserted AS ""RowsInserted"",
                   rows_updated AS ""RowsUpdated"", COUNT(*) OVER() AS ""TotalCount""
            FROM filtered
            ORDER BY {orderCol} {orderDir} NULLS LAST
            LIMIT @Limit OFFSET @Offset";

        var offset = (page - 1) * limit;
        var result = await _operationalDb.QueryAsync<FileMonitorRawRow>(
            sql,
            new { StartDate = startDate, EndDate = endDate, Pipeline = pipeline, Status = status, Search = search, Limit = limit, Offset = offset },
            cancellationToken);

        var safeRows = result.Available ? result.Value! : Array.Empty<FileMonitorRawRow>();
        var total = safeRows.Count > 0 ? (int)safeRows[0].TotalCount : 0;

        return new FileMonitorResult(
            Data: safeRows.Select(r => new FileMonitorRow(
                FileName: r.FileName,
                PipelineName: r.PipelineName,
                FileReceivedDate: r.ProcessDateTime,
                RecordsInserted: r.RowsInserted,
                RecordsUpdated: r.RowsUpdated,
                TotalRecords: r.RowsInserted + r.RowsUpdated,
                ProcessStatus: MapStatus(r.ProcessStatus),
                FileProcessed: r.ProcessStatus == 1,
                ErrorMessage: r.ProcessRemark)).ToList(),
            Total: total,
            Page: page,
            Limit: limit,
            TotalPages: limit > 0 ? (int)Math.Ceiling(total / (double)limit) : 0,
            DbAvailable: result.Available,
            StatusBreakdown: StatusBreakdown.Empty);
    }

    private sealed class PipelineNameRow
    {
        public string PipelineName { get; init; } = string.Empty;
    }

    // ── Pipeline names ─────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _operationalDb.QueryAsync<PipelineNameRow>(
            @"SELECT DISTINCT pipeline_name AS ""PipelineName"" FROM rt.pipeline_operational_logs ORDER BY pipeline_name",
            cancellationToken: cancellationToken);
        return result.Available ? result.Value!.Select(r => r.PipelineName).ToList() : Array.Empty<string>();
    }

    private sealed class OkRow
    {
        public int Ok { get; init; }
    }

    // ── DB health check ────────────────────────────────────────────────────────
    public async Task<bool> IsDbAvailableAsync(CancellationToken cancellationToken = default)
    {
        var result = await _operationalDb.QueryAsync<OkRow>(@"SELECT 1 AS ""Ok""", cancellationToken: cancellationToken);
        return result.Available && result.Value!.Count > 0;
    }

    private sealed class KpiRawRow
    {
        public int TotalFiles { get; init; }
        public int Processed { get; init; }
        public int Failed { get; init; }
        public int InProgress { get; init; }
        public long RowsInserted { get; init; }
        public long RowsUpdated { get; init; }
    }

    private sealed class PipelineRawRow
    {
        public string PipelineName { get; init; } = string.Empty;
        public int TotalFiles { get; init; }
        public int Processed { get; init; }
        public int Failed { get; init; }
        public DateTime? LastRunTime { get; init; }
    }

    private sealed class TrendRawRow
    {
        public string Period { get; init; } = string.Empty;
        public int Processed { get; init; }
        public int Failed { get; init; }
        public int InProgress { get; init; }
    }

    private sealed class FailureRawRow
    {
        public string PipelineName { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string? FailureReason { get; init; }
        public string? FailedTime { get; init; }
    }

    private sealed class ActivityRawRow
    {
        public string? Time { get; init; }
        public string FileName { get; init; } = string.Empty;
        public string PipelineName { get; init; } = string.Empty;
        public int ProcessStatus { get; init; }
        public string? Remark { get; init; }
    }

    // ── Dashboard ──────────────────────────────────────────────────────────────
    public async Task<DashboardData> GetDashboardAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default)
    {
        var dateParams = new { StartDate = NullIfEmpty(startDate), EndDate = NullIfEmpty(endDate) };

        // ── WHY DB IS AUTHORITATIVE FOR KPIs ─────────────────────────────────────
        // The pipeline's process_date_time determines "when something was processed".
        // S3 lastModified is "when the file arrived in S3" — a different dimension.
        // A file uploaded to S3 three days ago can be processed by the pipeline today.
        // So Total/Processed/Failed/Rows are always DB-based (matches what user saw before).
        // Only "Not Processed" is derived from S3 (files in S3 with no DB record at all).
        const string kpiSql = @"
            WITH latest AS (
              SELECT DISTINCT ON (file_name)
                file_name, process_status,
                GREATEST(COALESCE(rows_inserted, 0), 0) AS rows_inserted,
                GREATEST(COALESCE(rows_updated,  0), 0) AS rows_updated
              FROM rt.pipeline_operational_logs
              WHERE (@StartDate::date IS NULL OR (process_date_time::timestamptz)::date >= @StartDate)
                AND (@EndDate::date IS NULL OR (process_date_time::timestamptz)::date <= @EndDate)
              ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST
            )
            SELECT
              COUNT(*)::int AS ""TotalFiles"",
              COUNT(CASE WHEN process_status = 1  THEN 1 END)::int AS ""Processed"",
              COUNT(CASE WHEN process_status = -1 THEN 1 END)::int AS ""Failed"",
              COUNT(CASE WHEN process_status = 0  THEN 1 END)::int AS ""InProgress"",
              COALESCE(SUM(rows_inserted), 0)::bigint AS ""RowsInserted"",
              COALESCE(SUM(rows_updated),  0)::bigint AS ""RowsUpdated""
            FROM latest";

        const string pipelineSql = @"
            WITH latest AS (
              SELECT DISTINCT ON (pipeline_name, file_name)
                pipeline_name, file_name, process_status, process_date_time
              FROM rt.pipeline_operational_logs
              WHERE (@StartDate::date IS NULL OR (process_date_time::timestamptz)::date >= @StartDate)
                AND (@EndDate::date IS NULL OR (process_date_time::timestamptz)::date <= @EndDate)
              ORDER BY pipeline_name, file_name, process_date_time::timestamptz DESC NULLS LAST
            )
            SELECT
              pipeline_name AS ""PipelineName"",
              COUNT(*)::int AS ""TotalFiles"",
              COUNT(CASE WHEN process_status = 1  THEN 1 END)::int AS ""Processed"",
              COUNT(CASE WHEN process_status = -1 THEN 1 END)::int AS ""Failed"",
              MAX(process_date_time::timestamptz) AS ""LastRunTime""
            FROM latest
            GROUP BY pipeline_name
            ORDER BY ""TotalFiles"" DESC";

        const string trendSql = @"
            WITH latest_per_day AS (
              SELECT DISTINCT ON ((process_date_time::timestamptz)::date, file_name)
                (process_date_time::timestamptz)::date AS period, process_status
              FROM rt.pipeline_operational_logs
              WHERE (@StartDate::date IS NULL OR (process_date_time::timestamptz)::date >= @StartDate)
                AND (@EndDate::date IS NULL OR (process_date_time::timestamptz)::date <= @EndDate)
              ORDER BY (process_date_time::timestamptz)::date,
                       file_name, process_date_time::timestamptz DESC NULLS LAST
            )
            SELECT period::text AS ""Period"",
              COUNT(CASE WHEN process_status = 1  THEN 1 END)::int AS ""Processed"",
              COUNT(CASE WHEN process_status = -1 THEN 1 END)::int AS ""Failed"",
              COUNT(CASE WHEN process_status = 0  THEN 1 END)::int AS ""InProgress""
            FROM latest_per_day GROUP BY period ORDER BY period ASC";

        const string failureSql = @"
            WITH latest AS (
              SELECT DISTINCT ON (file_name)
                pipeline_name, file_name, process_status, process_remark, process_date_time
              FROM rt.pipeline_operational_logs
              WHERE (@StartDate::date IS NULL OR (process_date_time::timestamptz)::date >= @StartDate)
                AND (@EndDate::date IS NULL OR (process_date_time::timestamptz)::date <= @EndDate)
              ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST
            )
            SELECT pipeline_name AS ""PipelineName"", file_name AS ""FileName"",
                   process_remark AS ""FailureReason"", process_date_time AS ""FailedTime""
            FROM latest WHERE process_status = -1
            ORDER BY process_date_time::timestamptz DESC LIMIT 10";

        const string activitySql = @"
            SELECT DISTINCT ON (file_name)
              process_date_time AS ""Time"", file_name AS ""FileName"", pipeline_name AS ""PipelineName"",
              process_status AS ""ProcessStatus"", process_remark AS ""Remark""
            FROM rt.pipeline_operational_logs
            WHERE (@StartDate::date IS NULL OR (process_date_time::timestamptz)::date >= @StartDate)
              AND (@EndDate::date IS NULL OR (process_date_time::timestamptz)::date <= @EndDate)
            ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST
            LIMIT 15";

        // All DB queries run in parallel. S3 is NOT called here — the frontend
        // fetches Not Processed count via /monitor/not-processed-count separately
        // so DB metrics render immediately without waiting for AWS.
        var kpiTask = _operationalDb.QueryAsync<KpiRawRow>(kpiSql, dateParams, cancellationToken);
        var pipelineTask = _operationalDb.QueryAsync<PipelineRawRow>(pipelineSql, dateParams, cancellationToken);
        var trendTask = _operationalDb.QueryAsync<TrendRawRow>(trendSql, dateParams, cancellationToken);
        var failureTask = _operationalDb.QueryAsync<FailureRawRow>(failureSql, dateParams, cancellationToken);
        var activityTask = _operationalDb.QueryAsync<ActivityRawRow>(activitySql, dateParams, cancellationToken);

        await Task.WhenAll(kpiTask, pipelineTask, trendTask, failureTask, activityTask);

        var kpiResult = kpiTask.Result;
        var pipelineResult = pipelineTask.Result;
        var trendResult = trendTask.Result;
        var failureResult = failureTask.Result;
        var activityResult = activityTask.Result;

        // DB-based KPIs — identical to pre-S3 implementation (Total = 1100+ restored).
        var kpiRow = kpiResult.Available && kpiResult.Value!.Count > 0 ? kpiResult.Value[0] : null;
        var total = kpiRow?.TotalFiles ?? 0;
        var proc = kpiRow?.Processed ?? 0;
        var fail = kpiRow?.Failed ?? 0;
        var inProg = kpiRow?.InProgress ?? 0;
        var rowsIns = kpiRow?.RowsInserted ?? 0;
        var rowsUpd = kpiRow?.RowsUpdated ?? 0;

        var pipelineStatus = (pipelineResult.Available ? pipelineResult.Value! : Array.Empty<PipelineRawRow>())
            .Select(r => new PipelineStatusItem(
                PipelineName: r.PipelineName,
                TotalFiles: r.TotalFiles,
                ProcessedFiles: r.Processed,
                FailedFiles: r.Failed,
                LastRunTime: FormatIsoOrNull(r.LastRunTime),
                Status: r.Failed == 0 ? "healthy" : r.Failed >= r.TotalFiles * 0.5 ? "failed" : "warning"))
            .ToList();

        var sorted = pipelineStatus
            .OrderByDescending(p => (double)p.FailedFiles / (p.TotalFiles == 0 ? 1 : p.TotalFiles))
            .ToList();

        _logger.LogInformation(
            "Dashboard DB metrics {StartDate} {EndDate} {Total} {Processed} {Failed} {InProgress}",
            startDate, endDate, total, proc, fail, inProg);

        return new DashboardData(
            Kpis: new DashboardKpis(
                TotalFiles: total,
                Processed: proc,
                Failed: fail,
                InProgress: inProg,
                NotProcessed: 0, // fetched async via /not-processed-count
                RowsInserted: rowsIns,
                RowsUpdated: rowsUpd,
                SuccessRate: total > 0 ? Math.Round(proc / (double)total * 1000, MidpointRounding.AwayFromZero) / 10 : 0,
                FailureRate: total > 0 ? Math.Round(fail / (double)total * 1000, MidpointRounding.AwayFromZero) / 10 : 0),
            PipelineStatus: pipelineStatus,
            Trend: (trendResult.Available ? trendResult.Value! : Array.Empty<TrendRawRow>())
                .Select(r => new TrendPoint(r.Period, r.Processed, r.Failed, r.InProgress)).ToList(),
            TopFailures: (failureResult.Available ? failureResult.Value! : Array.Empty<FailureRawRow>())
                .Select(r => new TopFailureItem(r.PipelineName, r.FileName, r.FailureReason, r.FailedTime ?? string.Empty)).ToList(),
            RecentActivity: (activityResult.Available ? activityResult.Value! : Array.Empty<ActivityRawRow>())
                .Select(r => new ActivityItem(r.Time ?? string.Empty, r.FileName, r.PipelineName, MapStatus(r.ProcessStatus), r.Remark)).ToList(),
            MostActivePipeline: pipelineStatus.Count > 0 ? pipelineStatus[0].PipelineName : null,
            HighestFailurePipeline: sorted.Count > 0 && sorted[0].FailedFiles > 0 ? sorted[0].PipelineName : null,
            DbAvailable: kpiResult.Available);
    }

    // ── Not Processed count — S3-only, called asynchronously by the dashboard ──
    public async Task<NotProcessedCount> GetNotProcessedCountAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default)
    {
        var s3Files = await _s3FilesService.GetFileNamesForDateRangeAsync(startDate, endDate, cancellationToken);
        var notProcFiles = await GetS3FilesNotInDbAsync(s3Files, null, cancellationToken);
        _logger.LogInformation(
            "Not Processed count {StartDate} {EndDate} {S3Total} {Count}",
            startDate, endDate, s3Files.Count, notProcFiles.Count);
        return new NotProcessedCount(notProcFiles.Count);
    }

    // ── Reconciled File Monitor — DB rows + S3 "Not Processed" rows ───────────
    //
    // Design:
    //   DB  (date filter = process_date_time) → Processed / Failed / In Progress rows
    //   S3  (date filter = lastModified)      → "Not Processed" rows (no DB record)
    //   Merge → apply search/status filter → sort → paginate in memory
    //
    // Why separate date axes:
    //   The pipeline runs daily and processes files that arrived in S3 over many days.
    //   "Yesterday" in DB means "processed yesterday" (could be files from any S3 date).
    //   "Yesterday" in S3 means "arrived in S3 yesterday" (may not be processed yet).
    public async Task<FileMonitorResult> GetReconciledAsync(MonitorParams parameters, CancellationToken cancellationToken = default)
    {
        var startDate = NullIfEmpty(parameters.StartDate);
        var endDate = NullIfEmpty(parameters.EndDate);
        var status = NullIfEmpty(parameters.Status);
        var pipeline = NullIfEmpty(parameters.Pipeline);
        var search = NullIfEmpty(parameters.Search);
        var page = parameters.Page;
        var limit = parameters.Limit;
        var sortBy = string.IsNullOrEmpty(parameters.SortBy) ? "fileReceivedDate" : parameters.SortBy;
        var sortOrder = string.IsNullOrEmpty(parameters.SortOrder) ? "desc" : parameters.SortOrder;

        var needsDbFiles = status is null || status != MonitorStatuses.NotProcessed;
        var needsS3Check = status is null || status == MonitorStatuses.NotProcessed;

        // DB query: ALL files for date range (no SQL pagination — merging in memory).
        // No status filter in SQL; we filter in memory after merge so the total is correct.
        const string dbSql = @"
            SELECT DISTINCT ON (file_name)
              file_name AS ""FileName"", pipeline_name AS ""PipelineName"", process_date_time AS ""ProcessDateTime"",
              process_status AS ""ProcessStatus"", process_remark AS ""ProcessRemark"",
              GREATEST(COALESCE(rows_inserted, 0), 0) AS ""RowsInserted"",
              GREATEST(COALESCE(rows_updated,  0), 0) AS ""RowsUpdated""
            FROM rt.pipeline_operational_logs
            WHERE
              (@StartDate::date IS NULL OR (process_date_time::timestamptz)::date >= @StartDate::date)
              AND (@EndDate::date IS NULL OR (process_date_time::timestamptz)::date <= @EndDate::date)
              AND (@Pipeline::text IS NULL OR pipeline_name = @Pipeline)
              AND (@Search::text IS NULL OR file_name ILIKE '%' || @Search || '%')
            ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST";

        // DB and S3 run in parallel where both are needed.
        var dbTask = needsDbFiles
            ? _operationalDb.QueryAsync<FileMonitorRawRow>(
                dbSql,
                new { StartDate = startDate, EndDate = endDate, Pipeline = pipeline, Search = search },
                cancellationToken)
            : Task.FromResult(OperationalResult<IReadOnlyList<FileMonitorRawRow>>.Ok(Array.Empty<FileMonitorRawRow>()));

        var s3Task = needsS3Check
            ? _s3FilesService.GetFileNamesForDateRangeAsync(startDate, endDate, cancellationToken)
            : Task.FromResult<IReadOnlyList<FileNameDateEntry>>(Array.Empty<FileNameDateEntry>());

        await Task.WhenAll(dbTask, s3Task);

        var rawDbResult = dbTask.Result;
        var s3Files = s3Task.Result;

        var safeDbRows = rawDbResult.Available ? rawDbResult.Value! : Array.Empty<FileMonitorRawRow>();

        // Convert DB rows → FileMonitorRow.
        var dbRows = safeDbRows.Select(r => new FileMonitorRow(
            FileName: r.FileName,
            PipelineName: r.PipelineName,
            FileReceivedDate: r.ProcessDateTime,
            RecordsInserted: r.RowsInserted,
            RecordsUpdated: r.RowsUpdated,
            TotalRecords: r.RowsInserted + r.RowsUpdated,
            ProcessStatus: MapStatus(r.ProcessStatus),
            FileProcessed: r.ProcessStatus == 1,
            ErrorMessage: r.ProcessRemark)).ToList();

        // S3 Not Processed rows — only if status allows it.
        var notProcRows = new List<FileMonitorRow>();
        if (needsS3Check && s3Files.Count > 0)
        {
            var notProcFiles = await GetS3FilesNotInDbAsync(s3Files, pipeline, cancellationToken);
            notProcRows = notProcFiles.Select(f => new FileMonitorRow(
                FileName: f.Name,
                PipelineName: f.PipelineName,
                FileReceivedDate: f.LastModified,
                RecordsInserted: 0,
                RecordsUpdated: 0,
                TotalRecords: 0,
                ProcessStatus: MonitorStatuses.NotProcessed,
                FileProcessed: false,
                ErrorMessage: null)).ToList();
        }

        // Merge. DB rows already have pipeline+search applied in SQL.
        // Not Processed rows have pipeline applied in GetS3FilesNotInDbAsync().
        var merged = new List<FileMonitorRow>(dbRows.Count + notProcRows.Count);
        merged.AddRange(dbRows);
        merged.AddRange(notProcRows);

        // Apply search to Not Processed rows (DB search was already in SQL).
        // Re-apply to merged to cover both (cheaper than maintaining two filters).
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim().ToLowerInvariant();
            merged = merged.Where(r => r.FileName.ToLowerInvariant().Contains(q)).ToList();
        }

        // Status filter — 'Not Processed' only matches S3-only rows.
        // A Failed file has a DB record so it is NEVER "Not Processed".
        if (status is not null)
        {
            merged = merged.Where(r => r.ProcessStatus == status).ToList();
        }

        // Sort in memory — O(N log N).
        merged.Sort((a, b) =>
        {
            int cmp;
            if (sortBy == "fileName") cmp = string.CompareOrdinal(a.FileName, b.FileName);
            else if (sortBy == "pipelineName") cmp = string.CompareOrdinal(a.PipelineName ?? string.Empty, b.PipelineName ?? string.Empty);
            else
            {
                var aT = ParseTimestampMillis(a.FileReceivedDate);
                var bT = ParseTimestampMillis(b.FileReceivedDate);
                cmp = aT.CompareTo(bT);
            }
            return sortOrder == "asc" ? cmp : -cmp;
        });

        var total = merged.Count;
        var offset = (page - 1) * limit;
        var data = merged.Skip(Math.Max(offset, 0)).Take(limit).ToList();

        // Single-pass breakdown over the full post-filter, pre-pagination result set.
        int processedCount = 0, failedCount = 0, inProgressCount = 0, notProcessedCount = 0;
        foreach (var r in merged)
        {
            if (r.ProcessStatus == MonitorStatuses.Processed) processedCount++;
            else if (r.ProcessStatus == MonitorStatuses.Failed) failedCount++;
            else if (r.ProcessStatus == MonitorStatuses.InProgress) inProgressCount++;
            else if (r.ProcessStatus == MonitorStatuses.NotProcessed) notProcessedCount++;
        }
        var statusBreakdown = new StatusBreakdown(processedCount, failedCount, inProgressCount, notProcessedCount);

        _logger.LogInformation(
            "Reconcile S3+DB {StartDate} {EndDate} {PipelineFilter} {StatusFilter} {Processed} {Failed} {InProgress} {NotProcessed} {Total}",
            startDate, endDate, pipeline ?? "(all)", status ?? "(all)",
            statusBreakdown.Processed, statusBreakdown.Failed, statusBreakdown.InProgress, statusBreakdown.NotProcessed, total);

        return new FileMonitorResult(
            Data: data,
            Total: total,
            Page: page,
            Limit: limit,
            TotalPages: limit > 0 ? (int)Math.Ceiling(total / (double)limit) : 0,
            DbAvailable: rawDbResult.Available,
            StatusBreakdown: statusBreakdown);
    }
}
