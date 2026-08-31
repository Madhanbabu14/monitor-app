namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>FileMonitorRow</c> interface (monitor.service.ts).</summary>
public sealed record FileMonitorRow(
    string FileName,
    string? PipelineName,
    string? FileReceivedDate,
    int RecordsInserted,
    int RecordsUpdated,
    int TotalRecords,
    string ProcessStatus,
    bool FileProcessed,
    string? ErrorMessage);
