using Monitor.Core.Errors;
using Monitor.Files;
using Monitor.Files.Domain;
using Monitor.Identity.Authorization;

namespace Monitor.Api.Endpoints;

/// <summary>
/// One endpoint module for the whole `/api/s3` surface — the .NET collapse of
/// s3.routes.ts (route wiring) + s3.controller.ts (query/body parsing, status
/// codes) per the Architect's "route + policy + validation + DTO mapping live
/// together" layering. Business logic lives in <see cref="IS3FilesService"/>
/// (the translation of s3.service.ts). Response envelope matches the source's
/// `res.json({ status: 'success', data: ... })` exactly for the strangler window.
///
/// NOTE: despite this slice's title ("... retrigger (role-gated)"), the
/// observed source (s3.routes.ts) only applies the bare `authenticate`
/// middleware to every route in this router — `authorize(...)` is never
/// called for `/api/s3/*` (confirmed: no `authorize(` call site references
/// s3Routes, and `authorize` itself is exported-but-unused per
/// AuthorizationPolicyNames.cs's own comment). All four routes below use the
/// same `AuthenticatedUser` policy as the source, not a role-restricted one.
/// </summary>
public static class FilesEndpoints
{
    public static IEndpointRouteBuilder MapFilesEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/s3/files (s3.routes.ts, s3.controller.ts#listFiles)
        app.MapGet("/api/s3/files", async (
            string? prefix,
            string? search,
            string? startDate,
            string? endDate,
            int? page,
            int? limit,
            string? sortBy,
            string? sortOrder,
            IS3FilesService filesService,
            CancellationToken cancellationToken) =>
        {
            var parameters = new ListFilesParams(
                Prefix: prefix,
                Search: search,
                StartDate: startDate,
                EndDate: endDate,
                Page: page ?? 1,
                Limit: limit.HasValue ? Math.Min(limit.Value, 100) : 20,
                SortBy: sortBy is "name" or "size" or "lastModified" ? sortBy : "lastModified",
                SortOrder: sortOrder is "asc" or "desc" ? sortOrder : "desc");

            var result = await filesService.ListFilesAsync(parameters, cancellationToken);
            return Results.Json(new { status = "success", data = result });
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        // GET /api/s3/pipeline-names (s3.routes.ts, s3.controller.ts#pipelineNames)
        app.MapGet("/api/s3/pipeline-names", async (IS3FilesService filesService, CancellationToken cancellationToken) =>
        {
            var names = await filesService.GetPipelineNamesAsync(cancellationToken);
            return Results.Json(new { status = "success", data = names });
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        // GET /api/s3/download (s3.routes.ts, s3.controller.ts#downloadFile)
        app.MapGet("/api/s3/download", async (
            string? key,
            IS3FilesService filesService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new AppException(400, "File key is required");
            }

            await using var download = await filesService.GetFileDownloadAsync(key, cancellationToken);

            httpContext.Response.Headers["Content-Disposition"] =
                $"attachment; filename=\"{Uri.EscapeDataString(download.FileName)}\"";
            httpContext.Response.ContentType = download.ContentType ?? "application/octet-stream";
            if (download.ContentLength is { } contentLength)
            {
                httpContext.Response.ContentLength = contentLength;
            }

            await download.Content.CopyToAsync(httpContext.Response.Body, cancellationToken);
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        // POST /api/s3/retrigger (s3.routes.ts, s3.controller.ts#retriggerFile)
        app.MapPost("/api/s3/retrigger", async (RetriggerRequest? request, IS3FilesService filesService, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request?.FileName)) throw new AppException(400, "fileName is required");
            if (string.IsNullOrEmpty(request.S3Key)) throw new AppException(400, "s3Key is required");

            var result = await filesService.RetriggerFileAsync(request.FileName, request.S3Key, request.PipelineName, cancellationToken);
            return Results.Json(new { status = "success", data = result }, statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        return app;
    }
}

// Top-level record (not nested) so System.Text.Json's reflection-based body
// binding always has an unambiguous public constructor — matches
// s3.controller.ts#retriggerFile's `const { fileName, s3Key, pipelineName } = req.body`.
internal sealed record RetriggerRequest(string? FileName, string? S3Key, string? PipelineName);
