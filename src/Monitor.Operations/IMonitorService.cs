using Monitor.Operations.Domain;

namespace Monitor.Operations;

/// <summary>
/// Direct translation of <c>MonitorService</c> (monitor.service.ts). Backs the
/// `/api/monitor` endpoints (Monitor.Api's MonitorEndpoints): dashboard
/// aggregates, S3&lt;-&gt;operational-log reconciliation (the "Not Processed"
/// band), the legacy DB-only file monitor, and a DB health probe.
/// </summary>
public interface IMonitorService
{
    /// <summary>DB-only legacy file monitor (monitor.service.ts#getFileMonitor) — backs GET /api/monitor/files.</summary>
    Task<FileMonitorResult> GetFileMonitorAsync(MonitorParams parameters, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken = default);

    Task<bool> IsDbAvailableAsync(CancellationToken cancellationToken = default);

    Task<DashboardData> GetDashboardAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default);

    /// <summary>S3-only "Not Processed" count, fetched asynchronously by the dashboard (monitor.service.ts#getNotProcessedCount).</summary>
    Task<NotProcessedCount> GetNotProcessedCountAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default);

    /// <summary>DB rows + S3 "Not Processed" rows merged, filtered, sorted, and paginated in memory (monitor.service.ts#getReconciled).</summary>
    Task<FileMonitorResult> GetReconciledAsync(MonitorParams parameters, CancellationToken cancellationToken = default);
}
