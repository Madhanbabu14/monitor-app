using Monitor.Identity.Authorization;
using Monitor.Operations;
using Monitor.Operations.Domain;

namespace Monitor.Api.Endpoints;

/// <summary>
/// One endpoint module for the whole `/api/monitor` surface — the .NET
/// collapse of monitor.routes.ts (route wiring) + monitor.controller.ts
/// (query parsing, page/limit defaulting and clamping) per the Architect's
/// "route + policy + validation + DTO mapping live together" layering.
/// Business logic lives in <see cref="IMonitorService"/> (the translation of
/// monitor.service.ts). Response envelope matches the source's
/// `res.json({ status: 'success', data: ... })` exactly for the strangler
/// window.
///
/// NOTE: monitor.routes.ts applies only the bare `authenticate` middleware
/// (router.use(authenticate)) to every route — `authorize(...)` is never
/// called for `/api/monitor/*`. All six routes below use the same
/// `AuthenticatedUser` policy as the source, not a role-restricted one.
/// </summary>
public static class MonitorEndpoints
{
    public static IEndpointRouteBuilder MapMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/monitor/dashboard (monitor.routes.ts, monitor.controller.ts#getDashboard)
        app.MapGet("/api/monitor/dashboard", async (
            string? startDate,
            string? endDate,
            IMonitorService monitorService,
            CancellationToken cancellationToken) =>
        {
            var result = await monitorService.GetDashboardAsync(startDate, endDate, cancellationToken);
            return Results.Json(new { status = "success", data = result });
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        // GET /api/monitor/not-processed-count (monitor.routes.ts, monitor.controller.ts#getNotProcessedCount)
        app.MapGet("/api/monitor/not-processed-count", async (
            string? startDate,
            string? endDate,
            IMonitorService monitorService,
            CancellationToken cancellationToken) =>
        {
            var result = await monitorService.GetNotProcessedCountAsync(startDate, endDate, cancellationToken);
            return Results.Json(new { status = "success", data = result });
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        // GET /api/monitor/reconcile (monitor.routes.ts, monitor.controller.ts#getReconciled)
        // NOTE: limit cap is 200 here, vs. 100 on /files below — matches the
        // source's two separate Math.min(...) call sites exactly.
        app.MapGet("/api/monitor/reconcile", async (
            string? startDate,
            string? endDate,
            string? status,
            string? pipeline,
            string? search,
            int? page,
            int? limit,
            string? sortBy,
            string? sortOrder,
            IMonitorService monitorService,
            CancellationToken cancellationToken) =>
        {
            var parameters = new MonitorParams(
                StartDate: startDate,
                EndDate: endDate,
                Status: status,
                Pipeline: pipeline,
                Search: search,
                Page: page ?? 1,
                Limit: limit.HasValue ? Math.Min(limit.Value, 200) : 20,
                SortBy: sortBy ?? "fileReceivedDate",
                SortOrder: sortOrder ?? "desc");

            var result = await monitorService.GetReconciledAsync(parameters, cancellationToken);
            return Results.Json(new { status = "success", data = result });
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        // GET /api/monitor/files (monitor.routes.ts, monitor.controller.ts#getFiles)
        // NOTE: limit cap is 100 here, vs. 200 on /reconcile above.
        app.MapGet("/api/monitor/files", async (
            string? startDate,
            string? endDate,
            string? status,
            string? pipeline,
            string? search,
            int? page,
            int? limit,
            string? sortBy,
            string? sortOrder,
            IMonitorService monitorService,
            CancellationToken cancellationToken) =>
        {
            var parameters = new MonitorParams(
                StartDate: startDate,
                EndDate: endDate,
                Status: status,
                Pipeline: pipeline,
                Search: search,
                Page: page ?? 1,
                Limit: limit.HasValue ? Math.Min(limit.Value, 100) : 20,
                SortBy: sortBy ?? "fileReceivedDate",
                SortOrder: sortOrder ?? "desc");

            var result = await monitorService.GetFileMonitorAsync(parameters, cancellationToken);
            return Results.Json(new { status = "success", data = result });
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        // GET /api/monitor/pipeline-names (monitor.routes.ts, monitor.controller.ts#getPipelineNames)
        app.MapGet("/api/monitor/pipeline-names", async (IMonitorService monitorService, CancellationToken cancellationToken) =>
        {
            var names = await monitorService.GetPipelineNamesAsync(cancellationToken);
            return Results.Json(new { status = "success", data = names });
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        // GET /api/monitor/db-status (monitor.routes.ts, monitor.controller.ts#getDbStatus)
        app.MapGet("/api/monitor/db-status", async (IMonitorService monitorService, CancellationToken cancellationToken) =>
        {
            var available = await monitorService.IsDbAvailableAsync(cancellationToken);
            return Results.Json(new { status = "success", data = new { available } });
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        return app;
    }
}
