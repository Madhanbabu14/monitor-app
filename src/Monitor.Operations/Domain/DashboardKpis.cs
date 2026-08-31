namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>DashboardKpis</c> interface (monitor.service.ts).</summary>
public sealed record DashboardKpis(
    int TotalFiles,
    int Processed,
    int Failed,
    int InProgress,
    int NotProcessed,
    long RowsInserted,
    long RowsUpdated,
    double SuccessRate,
    double FailureRate);
