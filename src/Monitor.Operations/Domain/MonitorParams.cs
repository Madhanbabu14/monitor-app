namespace Monitor.Operations.Domain;

/// <summary>Direct translation of the source's <c>MonitorParams</c> interface (monitor.service.ts).</summary>
public sealed record MonitorParams(
    string? StartDate = null,
    string? EndDate = null,
    string? Status = null,
    string? Pipeline = null,
    string? Search = null,
    int Page = 1,
    int Limit = 20,
    string SortBy = "fileReceivedDate",
    string SortOrder = "desc");
