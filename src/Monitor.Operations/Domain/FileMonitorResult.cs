namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>FileMonitorResult</c> interface (monitor.service.ts).</summary>
public sealed record FileMonitorResult(
    IReadOnlyList<FileMonitorRow> Data,
    int Total,
    int Page,
    int Limit,
    int TotalPages,
    bool DbAvailable,
    StatusBreakdown StatusBreakdown);
