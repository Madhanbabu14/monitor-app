namespace Monitor.Operations.Domain;

/// <summary>
/// Direct translation of the source's <c>MonitorStatus</c> string-literal union
/// (<c>'Processed' | 'Failed' | 'In Progress' | 'Not Processed'</c>, monitor.service.ts).
/// Kept as plain wire strings (not an enum) because these values are compared
/// directly against the raw `status` query-string filter and round-tripped
/// verbatim into JSON — an enum would need a custom converter to reproduce
/// "In Progress" / "Not Processed" exactly.
/// </summary>
public static class MonitorStatuses
{
    public const string Processed = "Processed";
    public const string Failed = "Failed";
    public const string InProgress = "In Progress";
    public const string NotProcessed = "Not Processed";
}
