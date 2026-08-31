namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>{ count: number }</c> return shape (monitor.service.ts#getNotProcessedCount).</summary>
public sealed record NotProcessedCount(int Count);
