namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>DashboardData</c> interface (monitor.service.ts).</summary>
public sealed record DashboardData(
    DashboardKpis Kpis,
    IReadOnlyList<PipelineStatusItem> PipelineStatus,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<TopFailureItem> TopFailures,
    IReadOnlyList<ActivityItem> RecentActivity,
    string? MostActivePipeline,
    string? HighestFailurePipeline,
    bool DbAvailable);
