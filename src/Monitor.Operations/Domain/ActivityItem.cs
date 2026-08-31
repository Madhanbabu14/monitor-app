namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>ActivityItem</c> interface (monitor.service.ts).</summary>
public sealed record ActivityItem(string Time, string FileName, string PipelineName, string Status, string? Remark);
