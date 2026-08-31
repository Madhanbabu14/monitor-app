namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>TopFailureItem</c> interface (monitor.service.ts).</summary>
public sealed record TopFailureItem(string PipelineName, string FileName, string? FailureReason, string FailedTime);
