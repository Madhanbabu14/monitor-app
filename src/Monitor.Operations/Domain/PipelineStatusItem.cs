namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>PipelineStatusItem</c> interface (monitor.service.ts).</summary>
public sealed record PipelineStatusItem(
    string PipelineName,
    int TotalFiles,
    int ProcessedFiles,
    int FailedFiles,
    string? LastRunTime,
    string Status);
