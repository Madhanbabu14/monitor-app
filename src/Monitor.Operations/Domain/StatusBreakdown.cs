namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>StatusBreakdown</c> interface (monitor.service.ts).</summary>
public sealed record StatusBreakdown(int Processed, int Failed, int InProgress, int NotProcessed)
{
    public static readonly StatusBreakdown Empty = new(0, 0, 0, 0);
}
