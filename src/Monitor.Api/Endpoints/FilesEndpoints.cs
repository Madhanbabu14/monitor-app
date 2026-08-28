using Microsoft.Extensions.Primitives;
using Monitor.Core.Errors;
using Monitor.Files;
using Monitor.Files.Domain;
using Monitor.Identity.Authorization;

namespace Monitor.Api.Endpoints;

/// <summary>
/// Direct translation of features/s3/s3.routes.ts + s3.controller.ts. The source only
/// applies the bare `authenticate` middleware to this router (no `authorize(...)` call
/// on any route, even the mutating /retrigger), so every route here is gated on
/// <see cref="AuthorizationPolicyNames.AuthenticatedUser"/> — any authenticated caller,
/// no specific role required.
/// </summary>
public static class FilesEndpoints
{
    public static IEndpointRouteBuilder MapFilesEndpoints(this IEndpointRouteBuilder app)
    {
        // MapGroup is a .NET 7+ API; on .NET 6 each route chains its own
        // .RequireAuthorization(...) instead of sharing one group-level policy.
        app.MapGet("/api/s3/files", ListFilesAsync).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);
        app.MapGet("/api/s3/pipeline-names", GetPipelineNamesAsync).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);
        app.MapGet("/api/s3/download", DownloadFileAsync).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);
        app.MapPost("/api/s3/retrigger", RetriggerFileAsync).RequireAuthorization(AuthorizationPolicyNames.AuthenticatedUser);

        return app;
    }

    // GET /api/s3/files — S3Controller.listFiles
    private static async Task<IResult> ListFilesAsync(HttpRequest request, IFilesService filesService, CancellationToken cancellationToken)
    {
        var q = request.Query;

        var page = ParseIntOrDefault(q["page"], 1);
        var limit = Math.Min(ParseIntOrDefault(q["limit"], 20), 100);
        var sortByRaw = FirstOrDefault(q["sortBy"]);
        var sortOrderRaw = FirstOrDefault(q["sortOrder"]);

        var query = new ListFilesQuery(
            FirstOrDefault(q["prefix"]),
            FirstOrDefault(q["search"]),
            FirstOrDefault(q["startDate"]),
            FirstOrDefault(q["endDate"]),
            page,
            limit,
            string.IsNullOrEmpty(sortByRaw) ? "lastModified" : sortByRaw,
            string.IsNullOrEmpty(sortOrderRaw) ? "desc" : sortOrderRaw);

        var result = await filesService.ListFilesAsync(query, cancellationToken);

        return Results.Json(new
        {
            status = "success",
            data = new
            {
                files = result.Files.Select(ToJson),
                total = result.Total,
                totalSize = result.TotalSize,
                page = result.Page,
                limit = result.Limit,
                totalPages = result.TotalPages,
                folders = result.Folders,
                suggestions = result.Suggestions,
                bucket = result.Bucket,
                rootPrefix = result.RootPrefix,
            },
        });
    }

    // GET /api/s3/pipeline-names — S3Controller.pipelineNames
    private static async Task<IResult> GetPipelineNamesAsync(IFilesService filesService, CancellationToken cancellationToken)
    {
        var names = await filesService.GetPipelineNamesAsync(cancellationToken);
        return Results.Json(new { status = "success", data = names });
    }

    // GET /api/s3/download — S3Controller.downloadFile / S3Service.streamFile
    private static async Task DownloadFileAsync(HttpContext context, IFilesService filesService, CancellationToken cancellationToken)
    {
        var key = context.Request.Query["key"].ToString();
        if (string.IsNullOrEmpty(key))
        {
            throw new AppException(400, "File key is required");
        }

        var result = await filesService.StreamFileAsync(key, cancellationToken);
        await using var body = result.Body;

        context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(result.FileName)}\"";
        context.Response.ContentType = result.ContentType;
        if (result.ContentLength.HasValue)
        {
            context.Response.ContentLength = result.ContentLength;
        }

        await body.CopyToAsync(context.Response.Body, cancellationToken);
    }

    // POST /api/s3/retrigger — S3Controller.retriggerFile
    private static async Task<IResult> RetriggerFileAsync(RetriggerRequest? body, IFilesService filesService, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(body?.FileName))
        {
            throw new AppException(400, "fileName is required");
        }

        if (string.IsNullOrEmpty(body.S3Key))
        {
            throw new AppException(400, "s3Key is required");
        }

        var result = await filesService.RetriggerFileAsync(body.FileName, body.S3Key, body.PipelineName, cancellationToken);

        return Results.Json(
            new { status = "success", data = new { jobId = result.JobId, pipelineName = result.PipelineName, status = result.Status } },
            statusCode: StatusCodes.Status201Created);
    }

    private static object ToJson(S3FileItem item) => new
    {
        key = item.Key,
        name = item.Name,
        prefix = item.Prefix,
        size = item.Size,
        lastModified = item.LastModified,
        etag = item.ETag,
        pipelineName = item.PipelineName,
    };

    private static string? FirstOrDefault(StringValues values) => values.Count > 0 ? values[0] : null;

    private static int ParseIntOrDefault(StringValues values, int fallback)
    {
        var raw = FirstOrDefault(values);
        return !string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    // Matches the source controller's `{ fileName?, s3Key?, pipelineName? }` body shape
    // (features/s3/s3.controller.ts's retriggerFile). Minimal APIs bind this from the
    // JSON request body automatically since it's the handler's only complex parameter;
    // the framework's default case-insensitive property matching maps the client's
    // camelCase JSON keys onto these PascalCase members.
    private sealed record RetriggerRequest(string? FileName, string? S3Key, string? PipelineName);
}
