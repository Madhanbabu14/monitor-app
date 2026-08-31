using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Monitor.Data.Repositories;
using Monitor.Files;
using Monitor.Files.Domain;
using Monitor.Operations;
using Monitor.Operations.Domain;

namespace Monitor.UnitTests.Operations;

/// <summary>
/// Exercises <see cref="MonitorService"/> — the direct translation of
/// monitor.service.ts. Covers the DB-only file monitor, pipeline names, the
/// DB health probe, the dashboard aggregate (including the "DB is
/// authoritative for KPIs" split and the healthy/warning/failed pipeline
/// classification), the S3-only "Not Processed" count, and the reconciled
/// file monitor (DB rows + S3 "Not Processed" rows merged, filtered,
/// sorted, and paginated in memory).
///
/// <see cref="MonitorService"/> binds its Dapper query results into private
/// nested row types (e.g. <c>KpiRawRow</c>, <c>FileMonitorRawRow</c>) that
/// this test project cannot reference by name. <see cref="FakeOperationalDb"/>
/// works around that without a mocking library (matching this codebase's
/// established no-mock-library convention, e.g. <c>UserRepositoryTests</c>'s
/// <c>FakePrimaryDb</c>): its <c>QueryAsync&lt;T&gt;</c> is generic, so at the
/// call site inside <see cref="MonitorService"/>, T is reified to the real
/// (if inaccessible-by-name) private row type. The fake accepts plain
/// anonymous objects shaped like the SQL's own column aliases and copies
/// their properties onto a freshly reflection-constructed T by name — the
/// same "bind columns onto a POCO by name" contract Dapper itself provides
/// in production.
/// </summary>
public class MonitorServiceTests
{
    private sealed class QueuedResponse
    {
        public bool Available { get; init; }
        public object[] Rows { get; init; } = Array.Empty<object>();
    }

    private sealed class FakeOperationalDb : IOperationalDb
    {
        public bool IsConfigured { get; set; } = true;

        private readonly Queue<QueuedResponse> _responses = new();
        public List<(string Sql, object? Parameters)> Calls { get; } = new();

        public void EnqueueUnavailable() => _responses.Enqueue(new QueuedResponse { Available = false });

        public void EnqueueRows(params object[] rows) => _responses.Enqueue(new QueuedResponse { Available = true, Rows = rows });

        public Task<OperationalResult<IReadOnlyList<T>>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((sql, parameters));

            if (_responses.Count == 0)
            {
                return Task.FromResult(OperationalResult<IReadOnlyList<T>>.Ok(Array.Empty<T>()));
            }

            var next = _responses.Dequeue();
            if (!next.Available)
            {
                return Task.FromResult(OperationalResult<IReadOnlyList<T>>.Unavailable());
            }

            var mapped = (IReadOnlyList<T>)next.Rows.Select(MapRow<T>).ToList();
            return Task.FromResult(OperationalResult<IReadOnlyList<T>>.Ok(mapped));
        }

        private static T MapRow<T>(object source)
        {
            // nonPublic: true — the target row types are private nested classes;
            // their implicit default constructors are reachable via reflection
            // regardless of the C#-level accessibility rule.
            var instance = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
            foreach (var sourceProp in source.GetType().GetProperties())
            {
                var targetProp = typeof(T).GetProperty(sourceProp.Name, BindingFlags.Public | BindingFlags.Instance);
                targetProp?.SetValue(instance, sourceProp.GetValue(source));
            }
            return instance;
        }
    }

    private sealed class FakeS3FilesService : IS3FilesService
    {
        public IReadOnlyList<FileNameDateEntry> FilesForDateRange { get; set; } = Array.Empty<FileNameDateEntry>();
        public (string? StartDate, string? EndDate)? LastDateRangeArgs { get; private set; }

        public Task<IReadOnlyList<FileNameDateEntry>> GetFileNamesForDateRangeAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default)
        {
            LastDateRangeArgs = (startDate, endDate);
            return Task.FromResult(FilesForDateRange);
        }

        public Task<ListFilesResult> ListFilesAsync(ListFilesParams parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by MonitorService.");

        public Task<S3FileDownload> GetFileDownloadAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by MonitorService.");

        public Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by MonitorService.");

        public Task<RetriggerResult> RetriggerFileAsync(string fileName, string s3Key, string? pipelineNameOverride, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by MonitorService.");
    }

    private static MonitorService CreateService(FakeOperationalDb? db = null, FakeS3FilesService? s3 = null) =>
        new(db ?? new FakeOperationalDb(), s3 ?? new FakeS3FilesService(), NullLogger<MonitorService>.Instance);

    private static object? GetParam(object? parameters, string name) =>
        parameters?.GetType().GetProperty(name)?.GetValue(parameters);

    private static object FileMonitorRawRow(string fileName, string? pipeline, string? received, int status, int inserted = 0, int updated = 0, string? remark = null, long totalCount = 0) => new
    {
        FileName = fileName,
        PipelineName = pipeline,
        ProcessDateTime = received,
        ProcessStatus = status,
        ProcessRemark = remark,
        RowsInserted = inserted,
        RowsUpdated = updated,
        TotalCount = totalCount,
    };

    // ── GetFileMonitorAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetFileMonitorAsync_MapsStatusCodesAndComputesDerivedFields()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(
            FileMonitorRawRow("processed.csv", "pipe", "2026-01-01T00:00:00Z", 1, inserted: 7, updated: 3, totalCount: 3),
            FileMonitorRawRow("failed.csv", "pipe", "2026-01-02T00:00:00Z", -1, remark: "boom", totalCount: 3),
            FileMonitorRawRow("inprogress.csv", "pipe", "2026-01-03T00:00:00Z", 0, totalCount: 3));
        var service = CreateService(db);

        var result = await service.GetFileMonitorAsync(new MonitorParams());

        Assert.True(result.DbAvailable);
        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Data.Count);

        var processed = result.Data[0];
        Assert.Equal(MonitorStatuses.Processed, processed.ProcessStatus);
        Assert.True(processed.FileProcessed);
        Assert.Equal(10, processed.TotalRecords);
        Assert.Null(processed.ErrorMessage);

        var failed = result.Data[1];
        Assert.Equal(MonitorStatuses.Failed, failed.ProcessStatus);
        Assert.False(failed.FileProcessed);
        Assert.Equal("boom", failed.ErrorMessage);

        var inProgress = result.Data[2];
        Assert.Equal(MonitorStatuses.InProgress, inProgress.ProcessStatus);
        Assert.False(inProgress.FileProcessed);

        Assert.Equal(StatusBreakdown.Empty, result.StatusBreakdown);
    }

    [Fact]
    public async Task GetFileMonitorAsync_UnknownStatusCode_MapsToNotProcessed()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(FileMonitorRawRow("odd.csv", "pipe", "2026-01-01T00:00:00Z", 99, totalCount: 1));
        var service = CreateService(db);

        var result = await service.GetFileMonitorAsync(new MonitorParams());

        Assert.Equal(MonitorStatuses.NotProcessed, result.Data[0].ProcessStatus);
    }

    [Fact]
    public async Task GetFileMonitorAsync_DbUnavailable_ReturnsEmptyDataAndDbAvailableFalse()
    {
        var db = new FakeOperationalDb();
        db.EnqueueUnavailable();
        var service = CreateService(db);

        var result = await service.GetFileMonitorAsync(new MonitorParams());

        Assert.False(result.DbAvailable);
        Assert.Empty(result.Data);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.TotalPages);
    }

    [Fact]
    public async Task GetFileMonitorAsync_NoQueuedRows_DefaultsToEmptyAvailableResult()
    {
        var service = CreateService();

        var result = await service.GetFileMonitorAsync(new MonitorParams());

        Assert.True(result.DbAvailable);
        Assert.Empty(result.Data);
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.Limit);
    }

    [Fact]
    public async Task GetFileMonitorAsync_EmptyStringFilters_AreNormalizedToNull()
    {
        var db = new FakeOperationalDb();
        var service = CreateService(db);

        await service.GetFileMonitorAsync(new MonitorParams(StartDate: "", EndDate: "", Status: "", Pipeline: "", Search: ""));

        var (_, parameters) = db.Calls.Single();
        Assert.Null(GetParam(parameters, "StartDate"));
        Assert.Null(GetParam(parameters, "EndDate"));
        Assert.Null(GetParam(parameters, "Status"));
        Assert.Null(GetParam(parameters, "Pipeline"));
        Assert.Null(GetParam(parameters, "Search"));
    }

    [Theory]
    [InlineData("fileName", "file_name")]
    [InlineData("pipelineName", "pipeline_name")]
    [InlineData("recordsInserted", "rows_inserted")]
    [InlineData("recordsUpdated", "rows_updated")]
    [InlineData("somethingElse", "process_date_time")]
    public async Task GetFileMonitorAsync_SortByAllowlist_MapsToExpectedOrderColumn(string sortBy, string expectedColumn)
    {
        var db = new FakeOperationalDb();
        var service = CreateService(db);

        await service.GetFileMonitorAsync(new MonitorParams(SortBy: sortBy));

        var (sql, _) = db.Calls.Single();
        Assert.Contains($"ORDER BY {expectedColumn}", sql);
    }

    [Theory]
    [InlineData("asc", "ASC")]
    [InlineData("desc", "DESC")]
    [InlineData("bogus", "DESC")]
    public async Task GetFileMonitorAsync_SortOrder_DefaultsToDescUnlessExactlyAsc(string sortOrder, string expectedDir)
    {
        var db = new FakeOperationalDb();
        var service = CreateService(db);

        await service.GetFileMonitorAsync(new MonitorParams(SortOrder: sortOrder));

        var (sql, _) = db.Calls.Single();
        Assert.Contains($"{expectedDir} NULLS LAST", sql);
    }

    [Fact]
    public async Task GetFileMonitorAsync_PageAndLimit_ComputeOffsetAndAreEchoedBack()
    {
        var db = new FakeOperationalDb();
        var service = CreateService(db);

        var result = await service.GetFileMonitorAsync(new MonitorParams(Page: 3, Limit: 10));

        var (_, parameters) = db.Calls.Single();
        Assert.Equal(10, GetParam(parameters, "Limit"));
        Assert.Equal(20, GetParam(parameters, "Offset")); // (3-1)*10
        Assert.Equal(3, result.Page);
        Assert.Equal(10, result.Limit);
    }

    // ── GetPipelineNamesAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetPipelineNamesAsync_DbAvailable_ReturnsNamesInOrder()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(new { PipelineName = "alpha" }, new { PipelineName = "beta" });
        var service = CreateService(db);

        var names = await service.GetPipelineNamesAsync();

        Assert.Equal(new[] { "alpha", "beta" }, names);
    }

    [Fact]
    public async Task GetPipelineNamesAsync_DbUnavailable_ReturnsEmptyList()
    {
        var db = new FakeOperationalDb();
        db.EnqueueUnavailable();
        var service = CreateService(db);

        var result = await service.GetPipelineNamesAsync();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ── IsDbAvailableAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task IsDbAvailableAsync_QueryUnavailable_ReturnsFalse()
    {
        var db = new FakeOperationalDb();
        db.EnqueueUnavailable();
        var service = CreateService(db);

        Assert.False(await service.IsDbAvailableAsync());
    }

    [Fact]
    public async Task IsDbAvailableAsync_QueryAvailableWithNoRows_ReturnsFalse()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(); // Ok, empty
        var service = CreateService(db);

        Assert.False(await service.IsDbAvailableAsync());
    }

    [Fact]
    public async Task IsDbAvailableAsync_QueryAvailableWithRows_ReturnsTrue()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(new { Ok = 1 });
        var service = CreateService(db);

        Assert.True(await service.IsDbAvailableAsync());
    }

    // ── GetDashboardAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetDashboardAsync_AllQueriesUnavailable_ReturnsZeroedKpisAndDbAvailableFalse()
    {
        var db = new FakeOperationalDb();
        for (var i = 0; i < 5; i++) db.EnqueueUnavailable(); // kpi, pipeline, trend, failure, activity
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.False(result.DbAvailable);
        Assert.Equal(0, result.Kpis.TotalFiles);
        Assert.Equal(0, result.Kpis.Processed);
        Assert.Equal(0, result.Kpis.Failed);
        Assert.Equal(0, result.Kpis.InProgress);
        Assert.Equal(0, result.Kpis.SuccessRate);
        Assert.Equal(0, result.Kpis.FailureRate);
        Assert.Empty(result.PipelineStatus);
        Assert.Empty(result.Trend);
        Assert.Empty(result.TopFailures);
        Assert.Empty(result.RecentActivity);
        Assert.Null(result.MostActivePipeline);
        Assert.Null(result.HighestFailurePipeline);
    }

    [Fact]
    public async Task GetDashboardAsync_TotalFilesZero_RatesAreZeroNotDivideByZero()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(new { TotalFiles = 0, Processed = 0, Failed = 0, InProgress = 0, RowsInserted = 0L, RowsUpdated = 0L });
        for (var i = 0; i < 4; i++) db.EnqueueRows();
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.Equal(0, result.Kpis.SuccessRate);
        Assert.Equal(0, result.Kpis.FailureRate);
    }

    [Fact]
    public async Task GetDashboardAsync_ComputesRoundedSuccessAndFailureRates()
    {
        var db = new FakeOperationalDb();
        // total=3, processed=2 (66.666...% -> 66.7), failed=1 (33.333...% -> 33.3)
        db.EnqueueRows(new { TotalFiles = 3, Processed = 2, Failed = 1, InProgress = 0, RowsInserted = 100L, RowsUpdated = 50L });
        for (var i = 0; i < 4; i++) db.EnqueueRows();
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.Equal(3, result.Kpis.TotalFiles);
        Assert.Equal(2, result.Kpis.Processed);
        Assert.Equal(1, result.Kpis.Failed);
        Assert.Equal(100, result.Kpis.RowsInserted);
        Assert.Equal(50, result.Kpis.RowsUpdated);
        Assert.Equal(66.7, result.Kpis.SuccessRate);
        Assert.Equal(33.3, result.Kpis.FailureRate);
        // Not Processed is never computed here — always 0, fetched separately.
        Assert.Equal(0, result.Kpis.NotProcessed);
    }

    [Theory]
    [InlineData(0, 10, "healthy")]
    [InlineData(5, 10, "failed")] // 5 >= 10*0.5
    [InlineData(2, 10, "warning")] // 0 < 2 < 5
    [InlineData(1, 1, "failed")] // 1 >= 1*0.5
    public async Task GetDashboardAsync_PipelineStatus_ClassifiesHealthByFailureRatio(int failed, int total, string expectedStatus)
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows();
        db.EnqueueRows(new { PipelineName = "p1", TotalFiles = total, Processed = total - failed, Failed = failed, LastRunTime = (DateTime?)null });
        for (var i = 0; i < 3; i++) db.EnqueueRows();
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.Single(result.PipelineStatus);
        Assert.Equal(expectedStatus, result.PipelineStatus[0].Status);
    }

    [Fact]
    public async Task GetDashboardAsync_MostActivePipeline_IsFirstRowAsReturnedByQuery_NotResorted()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows();
        db.EnqueueRows(
            new { PipelineName = "big-pipeline", TotalFiles = 100, Processed = 100, Failed = 0, LastRunTime = (DateTime?)null },
            new { PipelineName = "small-failing-pipeline", TotalFiles = 2, Processed = 0, Failed = 2, LastRunTime = (DateTime?)null });
        for (var i = 0; i < 3; i++) db.EnqueueRows();
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.Equal("big-pipeline", result.MostActivePipeline);
        // Highest failure ratio (100%) belongs to the second row despite it not being first.
        Assert.Equal("small-failing-pipeline", result.HighestFailurePipeline);
    }

    [Fact]
    public async Task GetDashboardAsync_NoPipelineHasFailures_HighestFailurePipelineIsNull()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows();
        db.EnqueueRows(new { PipelineName = "p1", TotalFiles = 10, Processed = 10, Failed = 0, LastRunTime = (DateTime?)null });
        for (var i = 0; i < 3; i++) db.EnqueueRows();
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.Null(result.HighestFailurePipeline);
    }

    [Fact]
    public async Task GetDashboardAsync_NoPipelines_MostActiveAndHighestFailureAreNull()
    {
        var db = new FakeOperationalDb();
        for (var i = 0; i < 5; i++) db.EnqueueRows();
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.Null(result.MostActivePipeline);
        Assert.Null(result.HighestFailurePipeline);
    }

    [Fact]
    public async Task GetDashboardAsync_LastRunTime_FormattedAsUtcIsoWithMilliseconds()
    {
        var lastRun = new DateTime(2026, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);
        var db = new FakeOperationalDb();
        db.EnqueueRows();
        db.EnqueueRows(new { PipelineName = "p1", TotalFiles = 1, Processed = 1, Failed = 0, LastRunTime = (DateTime?)lastRun });
        for (var i = 0; i < 3; i++) db.EnqueueRows();
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.Equal("2026-03-04T05:06:07.890Z", result.PipelineStatus[0].LastRunTime);
    }

    [Fact]
    public async Task GetDashboardAsync_LastRunTimeNull_MapsToNull()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows();
        db.EnqueueRows(new { PipelineName = "p1", TotalFiles = 1, Processed = 1, Failed = 0, LastRunTime = (DateTime?)null });
        for (var i = 0; i < 3; i++) db.EnqueueRows();
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.Null(result.PipelineStatus[0].LastRunTime);
    }

    [Fact]
    public async Task GetDashboardAsync_MapsTrendTopFailuresAndRecentActivity()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows();
        db.EnqueueRows();
        db.EnqueueRows(new { Period = "2026-01-01", Processed = 5, Failed = 1, InProgress = 2 });
        db.EnqueueRows(new { PipelineName = "p1", FileName = "f1.csv", FailureReason = "boom", FailedTime = "2026-01-01T00:00:00Z" });
        db.EnqueueRows(new { Time = "2026-01-01T01:00:00Z", FileName = "f2.csv", PipelineName = "p2", ProcessStatus = 1, Remark = "ok" });
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        var trend = Assert.Single(result.Trend);
        Assert.Equal("2026-01-01", trend.Period);
        Assert.Equal(5, trend.Processed);
        Assert.Equal(1, trend.Failed);
        Assert.Equal(2, trend.InProgress);

        var failure = Assert.Single(result.TopFailures);
        Assert.Equal("p1", failure.PipelineName);
        Assert.Equal("f1.csv", failure.FileName);
        Assert.Equal("boom", failure.FailureReason);
        Assert.Equal("2026-01-01T00:00:00Z", failure.FailedTime);

        var activity = Assert.Single(result.RecentActivity);
        Assert.Equal("f2.csv", activity.FileName);
        Assert.Equal("p2", activity.PipelineName);
        Assert.Equal(MonitorStatuses.Processed, activity.Status);
        Assert.Equal("ok", activity.Remark);
    }

    [Fact]
    public async Task GetDashboardAsync_FailedTimeAndActivityTimeNull_DefaultToEmptyString()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows();
        db.EnqueueRows();
        db.EnqueueRows();
        db.EnqueueRows(new { PipelineName = "p1", FileName = "f1.csv", FailureReason = (string?)null, FailedTime = (string?)null });
        db.EnqueueRows(new { Time = (string?)null, FileName = "f2.csv", PipelineName = "p2", ProcessStatus = -1, Remark = (string?)null });
        var service = CreateService(db);

        var result = await service.GetDashboardAsync(null, null);

        Assert.Equal(string.Empty, result.TopFailures[0].FailedTime);
        Assert.Equal(string.Empty, result.RecentActivity[0].Time);
        Assert.Equal(MonitorStatuses.Failed, result.RecentActivity[0].Status);
    }

    [Fact]
    public async Task GetDashboardAsync_EmptyStringDates_AreNormalizedToNullBeforeQuerying()
    {
        var db = new FakeOperationalDb();
        for (var i = 0; i < 5; i++) db.EnqueueRows();
        var service = CreateService(db);

        await service.GetDashboardAsync("", "");

        var (_, parameters) = db.Calls[0];
        Assert.Null(GetParam(parameters, "StartDate"));
        Assert.Null(GetParam(parameters, "EndDate"));
    }

    // ── GetNotProcessedCountAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetNotProcessedCountAsync_NoS3Files_ReturnsZero()
    {
        var s3 = new FakeS3FilesService { FilesForDateRange = Array.Empty<FileNameDateEntry>() };
        var service = CreateService(s3: s3);

        var result = await service.GetNotProcessedCountAsync("2026-01-01", "2026-01-31");

        Assert.Equal(0, result.Count);
        Assert.Equal(("2026-01-01", "2026-01-31"), s3.LastDateRangeArgs);
    }

    [Fact]
    public async Task GetNotProcessedCountAsync_NonPipelineFilesAreExcludedEvenWithoutDbRecord()
    {
        var s3 = new FakeS3FilesService
        {
            FilesForDateRange = new[]
            {
                new FileNameDateEntry("_SUCCESS", "2026-01-01T00:00:00Z"),
                new FileNameDateEntry("manifest.json", "2026-01-01T00:00:00Z"),
            },
        };
        var db = new FakeOperationalDb();
        var service = CreateService(db, s3);

        var result = await service.GetNotProcessedCountAsync(null, null);

        Assert.Equal(0, result.Count);
        // Neither file matches the pipeline-file pattern, so the DB is never even queried.
        Assert.Empty(db.Calls);
    }

    [Fact]
    public async Task GetNotProcessedCountAsync_FilesNotInDb_AreCounted()
    {
        var s3 = new FakeS3FilesService
        {
            FilesForDateRange = new[]
            {
                new FileNameDateEntry("enrolment_20260101000000.csv", "2026-01-01T00:00:00Z"),
                new FileNameDateEntry("claims_20260102000000.csv", "2026-01-02T00:00:00Z"),
            },
        };
        var db = new FakeOperationalDb();
        db.EnqueueRows(new { FileName = "enrolment_20260101000000.csv" });
        var service = CreateService(db, s3);

        var result = await service.GetNotProcessedCountAsync(null, null);

        // Only claims_... has no DB record.
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task GetNotProcessedCountAsync_DbUnavailable_ReturnsZeroRatherThanCountingEverything()
    {
        var s3 = new FakeS3FilesService
        {
            FilesForDateRange = new[] { new FileNameDateEntry("enrolment_20260101000000.csv", "2026-01-01T00:00:00Z") },
        };
        var db = new FakeOperationalDb();
        db.EnqueueUnavailable();
        var service = CreateService(db, s3);

        var result = await service.GetNotProcessedCountAsync(null, null);

        Assert.Equal(0, result.Count);
    }

    // ── GetReconciledAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetReconciledAsync_NoStatusFilter_MergesDbAndS3NotProcessedRows()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(FileMonitorRawRow("processed_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1, inserted: 10, updated: 5));
        db.EnqueueRows(); // s3FilesNotInDb lookup: none in DB
        var s3 = new FakeS3FilesService
        {
            FilesForDateRange = new[] { new FileNameDateEntry("orphan_20260102000000.csv", "2026-01-02T00:00:00Z") },
        };

        var service = CreateService(db, s3);

        var result = await service.GetReconciledAsync(new MonitorParams());

        Assert.Equal(2, result.Total);
        Assert.Contains(result.Data, r => r.FileName == "processed_20260101000000.csv" && r.ProcessStatus == MonitorStatuses.Processed);
        Assert.Contains(result.Data, r => r.FileName == "orphan_20260102000000.csv" && r.ProcessStatus == MonitorStatuses.NotProcessed);
        Assert.Equal(1, result.StatusBreakdown.Processed);
        Assert.Equal(1, result.StatusBreakdown.NotProcessed);
        Assert.True(result.DbAvailable);
    }

    [Fact]
    public async Task GetReconciledAsync_StatusNotProcessed_SkipsPrimaryDbQueryButStillProbesS3()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(); // only consumed by the s3FilesNotInDb lookup
        var s3 = new FakeS3FilesService
        {
            FilesForDateRange = new[] { new FileNameDateEntry("orphan_20260102000000.csv", "2026-01-02T00:00:00Z") },
        };

        var service = CreateService(db, s3);

        var result = await service.GetReconciledAsync(new MonitorParams(Status: "Not Processed"));

        Assert.Single(result.Data);
        Assert.Equal("orphan_20260102000000.csv", result.Data[0].FileName);
        // The only DB call made is the s3FilesNotInDb lookup (a DISTINCT file_name probe),
        // never the primary "all files for date range" query.
        Assert.Single(db.Calls);
        Assert.Contains("DISTINCT file_name", db.Calls[0].Sql);
        // DbAvailable still reports true because the skipped primary-query task resolves
        // to a pre-built Ok(empty) rather than actually calling into IOperationalDb.
        Assert.True(result.DbAvailable);
    }

    [Theory]
    [InlineData("Processed")]
    [InlineData("Failed")]
    [InlineData("In Progress")]
    public async Task GetReconciledAsync_NonNotProcessedStatus_SkipsS3EntirelyAndNeverProducesNotProcessedRows(string status)
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(
            FileMonitorRawRow("f1_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1),
            FileMonitorRawRow("f2_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", -1),
            FileMonitorRawRow("f3_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 0));
        var s3 = new FakeS3FilesService
        {
            FilesForDateRange = new[] { new FileNameDateEntry("shouldnotappear_20260101000000.csv", "2026-01-01T00:00:00Z") },
        };
        var service = CreateService(db, s3);

        var result = await service.GetReconciledAsync(new MonitorParams(Status: status));

        Assert.Single(result.Data);
        Assert.Equal(status, result.Data[0].ProcessStatus);
        Assert.DoesNotContain(result.Data, r => r.FileName == "shouldnotappear_20260101000000.csv");
        Assert.Null(s3.LastDateRangeArgs); // S3 never even called
    }

    [Fact]
    public async Task GetReconciledAsync_FailedFileNeverClassifiedAsNotProcessed()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(FileMonitorRawRow("failed_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", -1, remark: "bad data"));
        var s3 = new FakeS3FilesService { FilesForDateRange = Array.Empty<FileNameDateEntry>() };
        var service = CreateService(db, s3);

        var result = await service.GetReconciledAsync(new MonitorParams(Status: "Not Processed"));

        // A Failed file has a DB record, so with status=Not Processed it must not appear.
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task GetReconciledAsync_SearchFilterAppliesToMergedResultsCaseInsensitiveTrimmed()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(
            FileMonitorRawRow("Invoice_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1),
            FileMonitorRawRow("Report_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1));
        var service = CreateService(db);

        var result = await service.GetReconciledAsync(new MonitorParams(Search: "  INVOICE  "));

        Assert.Single(result.Data);
        Assert.Equal("Invoice_20260101000000.csv", result.Data[0].FileName);
    }

    [Fact]
    public async Task GetReconciledAsync_PipelineFilterNarrowsS3NotProcessedRows()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(); // s3FilesNotInDb lookup
        var s3 = new FakeS3FilesService
        {
            FilesForDateRange = new[]
            {
                new FileNameDateEntry("alpha_20260101000000.csv", "2026-01-01T00:00:00Z"),
                new FileNameDateEntry("beta_20260101000000.csv", "2026-01-01T00:00:00Z"),
            },
        };
        var service = CreateService(db, s3);

        var result = await service.GetReconciledAsync(new MonitorParams(Status: "Not Processed", Pipeline: "alpha"));

        Assert.Single(result.Data);
        Assert.Equal("alpha_20260101000000.csv", result.Data[0].FileName);
    }

    [Theory]
    [InlineData("asc", new[] { "a_20260101000000.csv", "b_20260101000000.csv" })]
    [InlineData("desc", new[] { "b_20260101000000.csv", "a_20260101000000.csv" })]
    public async Task GetReconciledAsync_SortByFileName_OrdinalComparison(string sortOrder, string[] expectedOrder)
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(
            FileMonitorRawRow("b_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1),
            FileMonitorRawRow("a_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1));
        var service = CreateService(db);

        var result = await service.GetReconciledAsync(new MonitorParams(SortBy: "fileName", SortOrder: sortOrder));

        Assert.Equal(expectedOrder, result.Data.Select(r => r.FileName).ToArray());
    }

    [Fact]
    public async Task GetReconciledAsync_SortByPipelineName_NullPipelineTreatedAsEmptyString()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(
            FileMonitorRawRow("f1_20260101000000.csv", "zeta", "2026-01-01T00:00:00Z", 1),
            FileMonitorRawRow("f2_20260101000000.csv", null, "2026-01-01T00:00:00Z", 1));
        var service = CreateService(db);

        var result = await service.GetReconciledAsync(new MonitorParams(SortBy: "pipelineName", SortOrder: "asc"));

        // null (-> "") sorts before "zeta".
        Assert.Equal("f2_20260101000000.csv", result.Data[0].FileName);
        Assert.Equal("f1_20260101000000.csv", result.Data[1].FileName);
    }

    [Fact]
    public async Task GetReconciledAsync_DefaultSortByDate_MalformedTimestampTreatedAsZero()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(
            FileMonitorRawRow("valid_20260101000000.csv", "pipe", "2026-06-01T00:00:00Z", 1),
            FileMonitorRawRow("malformed_20260101000000.csv", "pipe", "not-a-date", 1));
        var service = CreateService(db);

        var result = await service.GetReconciledAsync(new MonitorParams(SortOrder: "asc"));

        // Malformed timestamp -> treated as epoch 0 -> sorts first ascending.
        Assert.Equal("malformed_20260101000000.csv", result.Data[0].FileName);
        Assert.Equal("valid_20260101000000.csv", result.Data[1].FileName);
    }

    [Fact]
    public async Task GetReconciledAsync_Pagination_SlicesMergedResultsAndComputesTotalPages()
    {
        var rows = Enumerable.Range(1, 5)
            .Select(i => FileMonitorRawRow($"f{i}_2026010100000{i}.csv", "pipe", $"2026-01-0{i}T00:00:00Z", 1))
            .ToArray();
        var db = new FakeOperationalDb();
        db.EnqueueRows(rows);
        var service = CreateService(db);

        var result = await service.GetReconciledAsync(new MonitorParams(Page: 2, Limit: 2, SortBy: "fileName", SortOrder: "asc"));

        Assert.Equal(5, result.Total);
        Assert.Equal(3, result.TotalPages); // ceil(5/2)
        Assert.Equal(2, result.Data.Count);
        Assert.Equal("f3_20260101000003.csv", result.Data[0].FileName);
        Assert.Equal("f4_20260101000004.csv", result.Data[1].FileName);
    }

    [Fact]
    public async Task GetReconciledAsync_LimitZero_ReturnsNoDataAndZeroTotalPages()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(FileMonitorRawRow("f1_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1));
        var service = CreateService(db);

        var result = await service.GetReconciledAsync(new MonitorParams(Limit: 0));

        Assert.Empty(result.Data);
        Assert.Equal(0, result.TotalPages);
        Assert.Equal(1, result.Total); // total still reflects the full merged set
    }

    [Fact]
    public async Task GetReconciledAsync_DbUnavailable_DbRowsEmptyAndDbAvailableFalse()
    {
        var db = new FakeOperationalDb();
        db.EnqueueUnavailable();
        var service = CreateService(db);

        var result = await service.GetReconciledAsync(new MonitorParams());

        Assert.False(result.DbAvailable);
        Assert.Empty(result.Data);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task GetReconciledAsync_StatusBreakdown_CountsAcrossAllFourBuckets()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(
            FileMonitorRawRow("proc_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1),
            FileMonitorRawRow("fail_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", -1),
            FileMonitorRawRow("prog_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 0));
        db.EnqueueRows(); // s3FilesNotInDb lookup
        var s3 = new FakeS3FilesService
        {
            FilesForDateRange = new[] { new FileNameDateEntry("orphan_20260101000000.csv", "2026-01-01T00:00:00Z") },
        };
        var service = CreateService(db, s3);

        var result = await service.GetReconciledAsync(new MonitorParams());

        Assert.Equal(1, result.StatusBreakdown.Processed);
        Assert.Equal(1, result.StatusBreakdown.Failed);
        Assert.Equal(1, result.StatusBreakdown.InProgress);
        Assert.Equal(1, result.StatusBreakdown.NotProcessed);
    }

    [Fact]
    public async Task GetReconciledAsync_TotalRecordsIsSumOfInsertedAndUpdated()
    {
        var db = new FakeOperationalDb();
        db.EnqueueRows(FileMonitorRawRow("f1_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1, inserted: 7, updated: 3));
        var service = CreateService(db);

        var result = await service.GetReconciledAsync(new MonitorParams());

        Assert.Equal(10, result.Data[0].TotalRecords);
        Assert.True(result.Data[0].FileProcessed);
    }

    [Fact]
    public async Task GetReconciledAsync_EmptyStringFilters_AreNormalizedToNullOnPrimaryQuery()
    {
        var db = new FakeOperationalDb();
        var service = CreateService(db);

        await service.GetReconciledAsync(new MonitorParams(StartDate: "", EndDate: "", Pipeline: "", Search: ""));

        var (_, parameters) = db.Calls.Single();
        Assert.Null(GetParam(parameters, "StartDate"));
        Assert.Null(GetParam(parameters, "EndDate"));
        Assert.Null(GetParam(parameters, "Pipeline"));
        Assert.Null(GetParam(parameters, "Search"));
    }
}
