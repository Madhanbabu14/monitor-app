using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Monitor.Api.Endpoints;
using Monitor.Core.Errors;
using Monitor.Files;
using Monitor.Files.Domain;
using Monitor.Identity.Authorization;
using Monitor.UnitTests.Files.Fakes;

namespace Monitor.UnitTests.Files;

/// <summary>
/// Unit tests for <c>Monitor.Api.Endpoints.FilesEndpoints</c> (the merged translation of
/// features/s3/s3.routes.ts + s3.controller.ts). <see cref="IFilesService"/> is replaced
/// with <see cref="FakeFilesService"/> so these tests are scoped to the endpoint module's
/// own responsibilities: route/auth wiring, query-string defaulting, request-body
/// validation, and JSON envelope shape - never <see cref="Monitor.Files.FilesService"/>'s
/// business logic (covered separately by <c>FilesServiceTests</c>).
///
/// The handlers under test (<c>ListFilesAsync</c>, <c>GetPipelineNamesAsync</c>,
/// <c>DownloadFileAsync</c>, <c>RetriggerFileAsync</c>) are all <c>private static</c>
/// members of <c>FilesEndpoints</c>, invoked here via reflection against manually built
/// <see cref="DefaultHttpContext"/>/<see cref="HttpRequest"/> instances rather than via a
/// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>: the sandbox this suite runs in
/// only has the .NET 10 runtime installed (this project targets net6.0 and is executed via
/// <c>DOTNET_ROLL_FORWARD=LatestMajor</c>), and under that combination
/// <c>TestServer</c>'s <c>ResponseBodyPipeWriter</c> throws
/// <c>PipeWriter.UnflushedBytes not implemented</c> for every request - an environment
/// artifact, not a bug in the migrated code. Executing the handlers directly (and, for
/// error paths, letting the real <see cref="AppException"/> propagate out of the awaited
/// task instead of relying on the terminal exception-handling middleware, which belongs to
/// a different slice) keeps these tests focused on this module while sidestepping that
/// incompatibility. Route wiring and the authorization-policy gate are verified separately,
/// purely from endpoint metadata, without executing any request at all.
/// </summary>
public class FilesEndpointsTests
{
    private static readonly Type EndpointsType = typeof(FilesEndpoints);

    private static readonly MethodInfo ListFilesMethod =
        EndpointsType.GetMethod("ListFilesAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetPipelineNamesMethod =
        EndpointsType.GetMethod("GetPipelineNamesAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo DownloadFileMethod =
        EndpointsType.GetMethod("DownloadFileAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo RetriggerFileMethod =
        EndpointsType.GetMethod("RetriggerFileAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly Type RetriggerRequestType =
        EndpointsType.GetNestedType("RetriggerRequest", BindingFlags.NonPublic)!;

    private readonly FakeFilesService _filesService = new();

    // ── infrastructure helpers ──────────────────────────────────────────────────

    private static DefaultHttpContext CreateContext(IDictionary<string, string>? query = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<JsonOptions>(_ => { });
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();

        if (query is not null)
        {
            var values = query.ToDictionary(kv => kv.Key, kv => new StringValues(kv.Value));
            context.Request.Query = new QueryCollection(values);
        }

        return context;
    }

    private static async Task<JsonDocument> ExecuteAndReadJsonAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        var json = await reader.ReadToEndAsync();
        return JsonDocument.Parse(json);
    }

    private async Task<IResult> InvokeListFilesAsync(HttpRequest request, CancellationToken ct = default)
    {
        var task = (Task<IResult>)ListFilesMethod.Invoke(null, new object?[] { request, _filesService, ct })!;
        return await task;
    }

    private async Task<IResult> InvokeGetPipelineNamesAsync(CancellationToken ct = default)
    {
        var task = (Task<IResult>)GetPipelineNamesMethod.Invoke(null, new object?[] { _filesService, ct })!;
        return await task;
    }

    private async Task InvokeDownloadFileAsync(HttpContext context, CancellationToken ct = default)
    {
        var task = (Task)DownloadFileMethod.Invoke(null, new object?[] { context, _filesService, ct })!;
        await task;
    }

    private async Task<IResult> InvokeRetriggerAsync(string? fileName, string? s3Key, string? pipelineName, CancellationToken ct = default)
    {
        var body = Activator.CreateInstance(RetriggerRequestType, fileName, s3Key, pipelineName);
        var task = (Task<IResult>)RetriggerFileMethod.Invoke(null, new object?[] { body, _filesService, ct })!;
        return await task;
    }

    private async Task<IResult> InvokeRetriggerAsync(object? body, CancellationToken ct = default)
    {
        var task = (Task<IResult>)RetriggerFileMethod.Invoke(null, new object?[] { body, _filesService, ct })!;
        return await task;
    }

    // ── route wiring / authorization gate (metadata only, no request executed) ─

    [Fact]
    public void MapFilesEndpoints_RegistersAllFourRoutes_GatedOnAuthenticatedUserPolicy()
    {
        var builder = WebApplication.CreateBuilder();
        // RequestDelegateFactory needs to know IFilesService is a DI service (rather than
        // inferring it as a [FromBody] parameter) to build endpoint metadata at all - it
        // doesn't affect anything these tests assert on, since no request is ever sent.
        builder.Services.AddSingleton<IFilesService>(_filesService);
        var app = builder.Build();
        app.MapFilesEndpoints();

        IEndpointRouteBuilder erb = app;
        var routes = erb.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(re => new
            {
                Path = re.RoutePattern.RawText,
                Methods = re.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>(),
                Policies = re.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToArray(),
            })
            .ToList();

        Assert.Contains(routes, r =>
            r.Path == "/api/s3/files" && r.Methods.Contains("GET") && r.Policies.Contains(AuthorizationPolicyNames.AuthenticatedUser));
        Assert.Contains(routes, r =>
            r.Path == "/api/s3/pipeline-names" && r.Methods.Contains("GET") && r.Policies.Contains(AuthorizationPolicyNames.AuthenticatedUser));
        Assert.Contains(routes, r =>
            r.Path == "/api/s3/download" && r.Methods.Contains("GET") && r.Policies.Contains(AuthorizationPolicyNames.AuthenticatedUser));
        Assert.Contains(routes, r =>
            r.Path == "/api/s3/retrigger" && r.Methods.Contains("POST") && r.Policies.Contains(AuthorizationPolicyNames.AuthenticatedUser));
    }

    // ── GET /api/s3/files ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_NoQueryString_AppliesSourceDefaults()
    {
        var context = CreateContext();

        await InvokeListFilesAsync(context.Request);

        var query = _filesService.LastListFilesQuery!;
        Assert.Equal(1, query.Page);
        Assert.Equal(20, query.Limit);
        Assert.Equal("lastModified", query.SortBy);
        Assert.Equal("desc", query.SortOrder);
        Assert.Null(query.Prefix);
        Assert.Null(query.Search);
        Assert.Null(query.StartDate);
        Assert.Null(query.EndDate);
    }

    [Fact]
    public async Task ListFiles_LimitAboveOneHundred_IsCappedAtOneHundred()
    {
        var context = CreateContext(new Dictionary<string, string> { ["limit"] = "500" });

        await InvokeListFilesAsync(context.Request);

        Assert.Equal(100, _filesService.LastListFilesQuery!.Limit);
    }

    [Fact]
    public async Task ListFiles_LimitBelowCap_PassesThroughUnchanged()
    {
        var context = CreateContext(new Dictionary<string, string> { ["limit"] = "45" });

        await InvokeListFilesAsync(context.Request);

        Assert.Equal(45, _filesService.LastListFilesQuery!.Limit);
    }

    [Fact]
    public async Task ListFiles_LimitExactlyOneHundred_PassesThroughUnchanged()
    {
        var context = CreateContext(new Dictionary<string, string> { ["limit"] = "100" });

        await InvokeListFilesAsync(context.Request);

        Assert.Equal(100, _filesService.LastListFilesQuery!.Limit);
    }

    [Fact]
    public async Task ListFiles_NonNumericPageAndLimit_FallBackToDefaults()
    {
        var context = CreateContext(new Dictionary<string, string> { ["page"] = "notanumber", ["limit"] = "notanumber" });

        await InvokeListFilesAsync(context.Request);

        Assert.Equal(1, _filesService.LastListFilesQuery!.Page);
        Assert.Equal(20, _filesService.LastListFilesQuery!.Limit);
    }

    [Fact]
    public async Task ListFiles_EmptySortByAndSortOrder_FallBackToDefaults()
    {
        var context = CreateContext(new Dictionary<string, string> { ["sortBy"] = "", ["sortOrder"] = "" });

        await InvokeListFilesAsync(context.Request);

        Assert.Equal("lastModified", _filesService.LastListFilesQuery!.SortBy);
        Assert.Equal("desc", _filesService.LastListFilesQuery!.SortOrder);
    }

    [Fact]
    public async Task ListFiles_AllQueryParametersProvided_ArePassedThroughVerbatim()
    {
        var context = CreateContext(new Dictionary<string, string>
        {
            ["prefix"] = "data/sub/",
            ["search"] = "enrol",
            ["startDate"] = "2026-01-01",
            ["endDate"] = "2026-01-31",
            ["page"] = "3",
            ["limit"] = "10",
            ["sortBy"] = "name",
            ["sortOrder"] = "asc",
        });

        await InvokeListFilesAsync(context.Request);

        var query = _filesService.LastListFilesQuery!;
        Assert.Equal("data/sub/", query.Prefix);
        Assert.Equal("enrol", query.Search);
        Assert.Equal("2026-01-01", query.StartDate);
        Assert.Equal("2026-01-31", query.EndDate);
        Assert.Equal(3, query.Page);
        Assert.Equal(10, query.Limit);
        Assert.Equal("name", query.SortBy);
        Assert.Equal("asc", query.SortOrder);
    }

    [Fact]
    public async Task ListFiles_HappyPath_ReturnsSuccessEnvelopeWithMappedFileFields()
    {
        _filesService.ListFilesResultFactory = _ => new ListFilesResult(
            new[] { new S3FileItem("data/f.csv", "f.csv", "data/", 123, "2026-01-01T00:00:00.000Z", "etag1", "pipeline-a") },
            1, 123, 1, 20, 1, new[] { "data/sub/" }, new[] { "pipeline-a" }, "the-bucket", "data/");

        var context = CreateContext();
        var result = await InvokeListFilesAsync(context.Request);
        using var doc = await ExecuteAndReadJsonAsync(result, context);

        var root = doc.RootElement;
        Assert.Equal("success", root.GetProperty("status").GetString());

        var data = root.GetProperty("data");
        Assert.Equal(1, data.GetProperty("total").GetInt32());
        Assert.Equal(123, data.GetProperty("totalSize").GetInt32());
        Assert.Equal(1, data.GetProperty("page").GetInt32());
        Assert.Equal(20, data.GetProperty("limit").GetInt32());
        Assert.Equal(1, data.GetProperty("totalPages").GetInt32());
        Assert.Equal("the-bucket", data.GetProperty("bucket").GetString());
        Assert.Equal("data/", data.GetProperty("rootPrefix").GetString());
        Assert.Equal(new[] { "data/sub/" }, data.GetProperty("folders").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "pipeline-a" }, data.GetProperty("suggestions").EnumerateArray().Select(e => e.GetString()).ToArray());

        var file = data.GetProperty("files")[0];
        Assert.Equal("data/f.csv", file.GetProperty("key").GetString());
        Assert.Equal("f.csv", file.GetProperty("name").GetString());
        Assert.Equal("data/", file.GetProperty("prefix").GetString());
        Assert.Equal(123, file.GetProperty("size").GetInt32());
        Assert.Equal("2026-01-01T00:00:00.000Z", file.GetProperty("lastModified").GetString());
        Assert.Equal("etag1", file.GetProperty("etag").GetString());
        Assert.Equal("pipeline-a", file.GetProperty("pipelineName").GetString());
    }

    [Fact]
    public async Task ListFiles_ItemWithNullPipelineName_SerializesAsJsonNull()
    {
        _filesService.ListFilesResultFactory = _ => new ListFilesResult(
            new[] { new S3FileItem("data/f.csv", "f.csv", "data/", 1, "2026-01-01T00:00:00.000Z", "etag1", null) },
            1, 1, 1, 20, 1, Array.Empty<string>(), Array.Empty<string>(), "b", "data/");

        var context = CreateContext();
        var result = await InvokeListFilesAsync(context.Request);
        using var doc = await ExecuteAndReadJsonAsync(result, context);

        var pipelineName = doc.RootElement.GetProperty("data").GetProperty("files")[0].GetProperty("pipelineName");
        Assert.Equal(JsonValueKind.Null, pipelineName.ValueKind);
    }

    [Fact]
    public async Task ListFiles_NoFiles_ReturnsEmptyFilesArray()
    {
        _filesService.ListFilesResultFactory = q => new ListFilesResult(
            Array.Empty<S3FileItem>(), 0, 0, q.Page, q.Limit, 0, Array.Empty<string>(), Array.Empty<string>(), "b", "root/");

        var context = CreateContext();
        var result = await InvokeListFilesAsync(context.Request);
        using var doc = await ExecuteAndReadJsonAsync(result, context);

        Assert.Empty(doc.RootElement.GetProperty("data").GetProperty("files").EnumerateArray());
        Assert.Equal(0, doc.RootElement.GetProperty("data").GetProperty("total").GetInt32());
    }

    // ── GET /api/s3/pipeline-names ──────────────────────────────────────────────

    [Fact]
    public async Task PipelineNames_HappyPath_ReturnsSuccessEnvelopeWithNamesArray()
    {
        _filesService.PipelineNamesFactory = () => new[] { "alpha", "beta" };

        var context = CreateContext();
        var result = await InvokeGetPipelineNamesAsync();
        using var doc = await ExecuteAndReadJsonAsync(result, context);

        Assert.Equal("success", doc.RootElement.GetProperty("status").GetString());
        var names = doc.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "alpha", "beta" }, names);
    }

    [Fact]
    public async Task PipelineNames_NoNames_ReturnsEmptyArray()
    {
        var context = CreateContext();
        var result = await InvokeGetPipelineNamesAsync();
        using var doc = await ExecuteAndReadJsonAsync(result, context);

        Assert.Empty(doc.RootElement.GetProperty("data").EnumerateArray());
    }

    // ── GET /api/s3/download ────────────────────────────────────────────────────

    [Fact]
    public async Task Download_MissingKey_ThrowsAppExceptionWith400()
    {
        var context = CreateContext();

        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeDownloadFileAsync(context));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("File key is required", ex.Message);
        Assert.Null(_filesService.LastStreamedKey);
    }

    [Fact]
    public async Task Download_EmptyKey_ThrowsAppExceptionWith400()
    {
        var context = CreateContext(new Dictionary<string, string> { ["key"] = "" });

        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeDownloadFileAsync(context));

        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Download_HappyPath_SetsContentDispositionContentTypeAndBody()
    {
        var bytes = new byte[] { 10, 20, 30 };
        _filesService.StreamFileResultFactory = key => new S3DownloadResult(new MemoryStream(bytes), "text/csv", bytes.Length, "report.csv");

        var context = CreateContext(new Dictionary<string, string> { ["key"] = "data/sub/report.csv" });

        await InvokeDownloadFileAsync(context);

        Assert.Equal("data/sub/report.csv", _filesService.LastStreamedKey);
        Assert.Equal("text/csv", context.Response.ContentType);
        Assert.Equal(bytes.Length, context.Response.ContentLength);
        Assert.Equal("attachment; filename=\"report.csv\"", context.Response.Headers["Content-Disposition"].ToString());

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        await context.Response.Body.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task Download_NoContentLength_DoesNotSetContentLengthHeader()
    {
        _filesService.StreamFileResultFactory = _ => new S3DownloadResult(new MemoryStream(new byte[] { 1 }), "text/csv", null, "report.csv");

        var context = CreateContext(new Dictionary<string, string> { ["key"] = "data/report.csv" });

        await InvokeDownloadFileAsync(context);

        Assert.Null(context.Response.ContentLength);
    }

    [Fact]
    public async Task Download_FileNameWithSpecialCharacters_IsPercentEncodedInContentDisposition()
    {
        _filesService.StreamFileResultFactory = _ => new S3DownloadResult(new MemoryStream(), "text/csv", null, "report with spaces & stuff.csv");

        var context = CreateContext(new Dictionary<string, string> { ["key"] = "data/report with spaces & stuff.csv" });

        await InvokeDownloadFileAsync(context);

        var disposition = context.Response.Headers["Content-Disposition"].ToString();
        Assert.Contains(Uri.EscapeDataString("report with spaces & stuff.csv"), disposition, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", disposition.Split('"')[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_ServiceThrowsAppException_PropagatesWithMappedStatusAndMessage()
    {
        _filesService.StreamFileException = new AppException(404, "File not found in S3");

        var context = CreateContext(new Dictionary<string, string> { ["key"] = "data/missing.csv" });

        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeDownloadFileAsync(context));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("File not found in S3", ex.Message);
    }

    // ── POST /api/s3/retrigger ──────────────────────────────────────────────────

    [Fact]
    public async Task Retrigger_MissingFileName_ThrowsAppExceptionWith400()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeRetriggerAsync(null, "data/f.csv", null));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("fileName is required", ex.Message);
    }

    [Fact]
    public async Task Retrigger_EmptyFileName_ThrowsAppExceptionWith400()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeRetriggerAsync(string.Empty, "data/f.csv", null));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("fileName is required", ex.Message);
    }

    [Fact]
    public async Task Retrigger_MissingS3Key_ThrowsAppExceptionWith400()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeRetriggerAsync("f.csv", null, null));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("s3Key is required", ex.Message);
    }

    [Fact]
    public async Task Retrigger_EmptyS3Key_ThrowsAppExceptionWith400()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeRetriggerAsync("f.csv", string.Empty, null));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("s3Key is required", ex.Message);
    }

    [Fact]
    public async Task Retrigger_NullBody_ThrowsAppExceptionForMissingFileNameFirst()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeRetriggerAsync((object?)null));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("fileName is required", ex.Message);
    }

    [Fact]
    public async Task Retrigger_MissingBothFields_ReportsFileNameFirst()
    {
        // Mirrors the source controller's validation order: fileName is checked before s3Key.
        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeRetriggerAsync(null, null, null));

        Assert.Equal("fileName is required", ex.Message);
    }

    [Fact]
    public async Task Retrigger_HappyPath_Returns201WithSuccessEnvelope_AndForwardsArgsToService()
    {
        _filesService.RetriggerResultFactory = () => new RetriggerResult("job-123", "enrolment", "queued");

        var context = CreateContext();
        var result = await InvokeRetriggerAsync("enrolment_1_20260101.csv", "data/enrolment_1_20260101.csv", "custom");
        using var doc = await ExecuteAndReadJsonAsync(result, context);

        Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);

        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("success", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("job-123", data.GetProperty("jobId").GetString());
        Assert.Equal("enrolment", data.GetProperty("pipelineName").GetString());
        Assert.Equal("queued", data.GetProperty("status").GetString());

        Assert.Equal(("enrolment_1_20260101.csv", "data/enrolment_1_20260101.csv", "custom"), _filesService.LastRetrigger);
    }

    [Fact]
    public async Task Retrigger_NoPipelineNameInBody_ForwardsNullOverride()
    {
        await InvokeRetriggerAsync("f.csv", "data/f.csv", null);

        Assert.Equal(("f.csv", "data/f.csv", (string?)null), _filesService.LastRetrigger);
    }

    [Fact]
    public async Task Retrigger_ServiceThrows400_PropagatesAppException()
    {
        _filesService.RetriggerException = new AppException(400, "Cannot derive pipeline name from file: f.csv");

        var ex = await Assert.ThrowsAsync<AppException>(() => InvokeRetriggerAsync("f.csv", "data/f.csv", null));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Cannot derive pipeline name from file: f.csv", ex.Message);
    }

    [Fact]
    public async Task Retrigger_ServiceThrowsUnhandledException_PropagatesUnwrapped()
    {
        _filesService.RetriggerException = new InvalidOperationException("boom");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRetriggerAsync("f.csv", "data/f.csv", null));

        Assert.Equal("boom", ex.Message);
    }
}
