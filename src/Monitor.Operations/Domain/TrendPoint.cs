namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>TrendPoint</c> interface (monitor.service.ts).</summary>
public sealed record TrendPoint(string Period, int Processed, int Failed, int InProgress);
