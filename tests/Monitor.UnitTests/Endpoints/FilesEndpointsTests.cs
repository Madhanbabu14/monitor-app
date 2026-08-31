using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Api.Endpoints;
using Monitor.Core.Errors;
using Monitor.Files;
using Monitor.Files.Domain;
using Monitor.Identity.Authorization;

namespace Monitor.UnitTests.Endpoints;

/// <summary>
/// Exercises <see cref="FilesEndpoints.MapFilesEndpoints"/> — the .NET collapse of
/// s3.routes.ts (route wiring) + s3.controller.ts (query/body parsing, status codes,
/// response envelope) around <see cref="IS3FilesService"/> (business logic covered
/// separately by <c>S3FilesServiceTests</c>). Boots a minimal, DB-free
/// <see cref="WebApplication"/> with a fake <see cref="IS3FilesService"/> and a
/// controllable test authentication handler, using the same request-dispatch
/// technique as <c>AuthEndpointsTests</c> (driving the built pipeline directly against
/// a <see cref="DefaultHttpContext"/> rather than <c>TestServer</c>/<c>HttpClient</c>,
/// which cannot serialize <c>Results.Json</c> responses under this SDK's
/// <c>System.Text.Json</c>).
///
/// Per FilesEndpoints.cs's own doc comment: despite this slice's title mentioning
/// "role-gated" retrigger, the source only ever applies the bare `authenticate`
/// middleware to every `/api/s3/*` route — there is no role restriction. All four
/// routes below are exercised against the same <c>AuthenticatedUser</c> policy.
/// </summary>
public class FilesEndpointsTests
{
    private sealed class FakeS3FilesService : IS3FilesService
    {
        public Func<ListFilesParams, ListFilesResult>? OnListFiles;
        public ListFilesParams? LastListFilesParams { get; private set; }

        public Func<string, S3FileDownload>? OnGetFileDownload;
        public string? LastDownloadKey { get; private set; }

        public Func<IReadOnlyList<string>>? OnGetPipelineNames;

        public Func<string, string, string?, RetriggerResult>? OnRetrigger;
        public (string FileName, string S3Key, string? PipelineName)? LastRetriggerArgs { get; private set; }

        public Task<ListFilesResult> ListFilesAsync(ListFilesParams parameters, CancellationToken cancellationToken = default)
        {
            LastListFilesParams = parameters;
            return Task.FromResult(OnListFiles!(parameters));
        }

        public Task<S3FileDownload> GetFileDownloadAsync(string key, CancellationToken cancellationToken = default)
        {
            LastDownloadKey = key;
            return Task.FromResult(OnGetFileDownload!(key));
        }

        public Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OnGetPipelineNames!());

        public Task<RetriggerResult> RetriggerFileAsync(string fileName, string s3Key, string? pipelineNameOverride, CancellationToken cancellationToken = default)
        {
            LastRetriggerArgs = (fileName, s3Key, pipelineNameOverride);
            return Task.FromResult(OnRetrigger!(fileName, s3Key, pipelineNameOverride));
        }

        public Task<IReadOnlyList<FileNameDateEntry>> GetFileNamesForDateRangeAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by FilesEndpoints.");
    }

    private sealed class PrincipalHolder
    {
        public ClaimsPrincipal? Principal;
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly PrincipalHolder _holder;

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PrincipalHolder holder)
            : base(options, logger, encoder)
        {
            _holder = holder;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (_holder.Principal is null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var ticket = new AuthenticationTicket(_holder.Principal, "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed record HttpCallResult(int StatusCode, byte[] RawBody, HttpResponse Response)
    {
        public JsonElement? Json => RawBody.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(RawBody) : null;
        public string Text => Encoding.UTF8.GetString(RawBody);
    }

    private sealed class BodyDetectionFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public BodyDetectionFeature(bool canHaveBody) => CanHaveBody = canHaveBody;

        public bool CanHaveBody { get; }
    }

    private sealed class TestApp : IAsyncDisposable
    {
        public TestApp(WebApplication app, RequestDelegate pipeline, PrincipalHolder principal)
        {
            App = app;
            Pipeline = pipeline;
            Principal = principal;
        }

        public WebApplication App { get; }
        public RequestDelegate Pipeline { get; }
        public PrincipalHolder Principal { get; }

        public async Task<HttpCallResult> SendAsync(string method, string path, object? jsonBody = null, IDictionary<string, string?>? query = null, bool sendBody = true)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = App.Services,
            };
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");

            if (query is { Count: > 0 })
            {
                var parts = query.Select(kv => kv.Value is null
                    ? Uri.EscapeDataString(kv.Key)
                    : $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
                context.Request.QueryString = new QueryString("?" + string.Join('&', parts));
            }

            var hasBody = sendBody && jsonBody is not null;
            if (hasBody)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(jsonBody);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
            }

            // See AuthEndpointsTests: DefaultHttpContext never populates this feature the
            // way Kestrel does, but minimal APIs' generated request delegates consult it to
            // decide whether a complex parameter may be bound from the request body.
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(
                new BodyDetectionFeature(hasBody));

            var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            await Pipeline(context);

            responseBody.Position = 0;
            return new HttpCallResult(context.Response.StatusCode, responseBody.ToArray(), context.Response);
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static TestApp CreateApp(IS3FilesService filesService)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var holder = new PrincipalHolder();
        builder.Services.AddSingleton(holder);
        builder.Services.AddSingleton(filesService);
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthorizationPolicyNames.AuthenticatedUser, policy => policy.RequireAuthenticatedUser()));

        var app = builder.Build();

        // Test-only stand-in for the real ExceptionHandlingMiddleware (a different
        // slice): renders AppException per its documented envelope so the endpoint's
        // validation/error paths are assertable.
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (AppException ex)
            {
                context.Response.StatusCode = ex.StatusCode;
                await context.Response.WriteAsJsonAsync(new { status = "error", message = ex.Message });
            }
        });

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapFilesEndpoints();
        app.UseEndpoints(_ => { });

        var pipeline = ((IApplicationBuilder)app).Build();
        return new TestApp(app, pipeline, holder);
    }

    private static void Authenticate(TestApp testApp) =>
        testApp.Principal.Principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "user-1") }, "Test"));

    private static S3FileItem MakeItem(string key = "data/enrolment_1_20260101000000.csv") =>
        new(key, "enrolment_1_20260101000000.csv", "data/", 42, "2026-01-01T00:00:00.000Z", "abc123", "enrolment");

    private static ListFilesResult MakeListResult() => new(
        Files: new[] { MakeItem() },
        Total: 1,
        TotalSize: 42,
        Page: 1,
        Limit: 20,
        TotalPages: 1,
        Folders: new[] { "data/" },
        Suggestions: new[] { "enrolment" },
        Bucket: "test-bucket",
        RootPrefix: "data/");

    // ── GET /api/s3/files ────────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_Unauthenticated_Returns401()
    {
        var service = new FakeS3FilesService { OnListFiles = _ => MakeListResult() };
        await using var testApp = CreateApp(service);

        var response = await testApp.SendAsync("GET", "/api/s3/files");

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task ListFiles_NoQueryParams_UsesSourceDefaults()
    {
        var service = new FakeS3FilesService { OnListFiles = _ => MakeListResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("GET", "/api/s3/files");

        var used = service.LastListFilesParams!;
        Assert.Null(used.Prefix);
        Assert.Null(used.Search);
        Assert.Equal(1, used.Page);
        Assert.Equal(20, used.Limit);
        Assert.Equal("lastModified", used.SortBy);
        Assert.Equal("desc", used.SortOrder);
    }

    [Fact]
    public async Task ListFiles_LimitAboveOneHundred_ClampsToOneHundred()
    {
        var service = new FakeS3FilesService { OnListFiles = _ => MakeListResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("GET", "/api/s3/files", query: new Dictionary<string, string?> { ["limit"] = "500" });

        Assert.Equal(100, service.LastListFilesParams!.Limit);
    }

    [Fact]
    public async Task ListFiles_LimitBelowOneHundred_PassesThroughUnclamped()
    {
        var service = new FakeS3FilesService { OnListFiles = _ => MakeListResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("GET", "/api/s3/files", query: new Dictionary<string, string?> { ["limit"] = "5" });

        Assert.Equal(5, service.LastListFilesParams!.Limit);
    }

    [Theory]
    [InlineData("name", "name")]
    [InlineData("size", "size")]
    [InlineData("lastModified", "lastModified")]
    [InlineData("bogus", "lastModified")]
    public async Task ListFiles_SortByOnlyAcceptsAllowlistedValues_OtherwiseDefaults(string requested, string expected)
    {
        var service = new FakeS3FilesService { OnListFiles = _ => MakeListResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("GET", "/api/s3/files", query: new Dictionary<string, string?> { ["sortBy"] = requested });

        Assert.Equal(expected, service.LastListFilesParams!.SortBy);
    }

    [Theory]
    [InlineData("asc", "asc")]
    [InlineData("desc", "desc")]
    [InlineData("nonsense", "desc")]
    public async Task ListFiles_SortOrderOnlyAcceptsAscOrDesc_OtherwiseDefaultsToDesc(string requested, string expected)
    {
        var service = new FakeS3FilesService { OnListFiles = _ => MakeListResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("GET", "/api/s3/files", query: new Dictionary<string, string?> { ["sortOrder"] = requested });

        Assert.Equal(expected, service.LastListFilesParams!.SortOrder);
    }

    [Fact]
    public async Task ListFiles_PassesThroughPrefixSearchAndDateRange()
    {
        var service = new FakeS3FilesService { OnListFiles = _ => MakeListResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("GET", "/api/s3/files", query: new Dictionary<string, string?>
        {
            ["prefix"] = "data/reports/",
            ["search"] = "enrolment",
            ["startDate"] = "2026-01-01",
            ["endDate"] = "2026-01-31",
            ["page"] = "3",
        });

        var used = service.LastListFilesParams!;
        Assert.Equal("data/reports/", used.Prefix);
        Assert.Equal("enrolment", used.Search);
        Assert.Equal("2026-01-01", used.StartDate);
        Assert.Equal("2026-01-31", used.EndDate);
        Assert.Equal(3, used.Page);
    }

    [Fact]
    public async Task ListFiles_Success_ReturnsSuccessEnvelopeWithMappedResult()
    {
        var service = new FakeS3FilesService { OnListFiles = _ => MakeListResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("GET", "/api/s3/files");

        Assert.Equal(200, response.StatusCode);
        var body = response.Json!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var data = body.GetProperty("data");
        Assert.Equal(1, data.GetProperty("total").GetInt32());
        Assert.Equal(42, data.GetProperty("totalSize").GetInt64());
        Assert.Equal("test-bucket", data.GetProperty("bucket").GetString());
        Assert.Equal("data/", data.GetProperty("rootPrefix").GetString());

        var file = data.GetProperty("files")[0];
        Assert.Equal("enrolment_1_20260101000000.csv", file.GetProperty("name").GetString());
        // S3FileItem.ETag carries an explicit [JsonPropertyName("etag")] to match the
        // source's lowercase wire field even though every other property is camelCased
        // by ASP.NET Core's default naming policy.
        Assert.Equal("abc123", file.GetProperty("etag").GetString());
        Assert.Equal("enrolment", file.GetProperty("pipelineName").GetString());
    }

    // ── GET /api/s3/pipeline-names ───────────────────────────────────────────

    [Fact]
    public async Task PipelineNames_Unauthenticated_Returns401()
    {
        var service = new FakeS3FilesService { OnGetPipelineNames = () => Array.Empty<string>() };
        await using var testApp = CreateApp(service);

        var response = await testApp.SendAsync("GET", "/api/s3/pipeline-names");

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task PipelineNames_Success_ReturnsSuccessEnvelopeWithNamesArray()
    {
        var service = new FakeS3FilesService { OnGetPipelineNames = () => new[] { "alpha", "beta" } };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("GET", "/api/s3/pipeline-names");

        Assert.Equal(200, response.StatusCode);
        var body = response.Json!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var names = body.GetProperty("data").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "alpha", "beta" }, names);
    }

    // ── GET /api/s3/download ─────────────────────────────────────────────────

    [Fact]
    public async Task Download_MissingKey_Returns400WithExpectedMessage()
    {
        var service = new FakeS3FilesService();
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("GET", "/api/s3/download");

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("File key is required", response.Json!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Download_EmptyKey_Returns400()
    {
        var service = new FakeS3FilesService();
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("GET", "/api/s3/download", query: new Dictionary<string, string?> { ["key"] = "" });

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task Download_Success_SetsHeadersAndStreamsBody()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var service = new FakeS3FilesService
        {
            OnGetFileDownload = key =>
            {
                Assert.Equal("data/reports/summary.csv", key);
                return new S3FileDownload(new MemoryStream(content), "text/csv", content.Length, "summary.csv");
            },
        };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("GET", "/api/s3/download", query: new Dictionary<string, string?> { ["key"] = "data/reports/summary.csv" });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(content, response.RawBody);
        Assert.Equal("text/csv", response.Response.ContentType);
        Assert.Equal(content.Length, response.Response.ContentLength);
        Assert.Equal("attachment; filename=\"summary.csv\"", response.Response.Headers["Content-Disposition"]);
    }

    [Fact]
    public async Task Download_ContentTypeNull_DefaultsToOctetStream()
    {
        var service = new FakeS3FilesService
        {
            OnGetFileDownload = _ => new S3FileDownload(new MemoryStream(new byte[] { 9 }), null, null, "file.bin"),
        };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("GET", "/api/s3/download", query: new Dictionary<string, string?> { ["key"] = "data/file.bin" });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Response.ContentType);
    }

    [Fact]
    public async Task Download_FileNameWithSpecialCharacters_IsPercentEncodedInContentDisposition()
    {
        var service = new FakeS3FilesService
        {
            OnGetFileDownload = _ => new S3FileDownload(new MemoryStream(new byte[] { 1 }), "text/plain", 1, "report with spaces.csv"),
        };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("GET", "/api/s3/download", query: new Dictionary<string, string?> { ["key"] = "data/report with spaces.csv" });

        Assert.Equal("attachment; filename=\"report%20with%20spaces.csv\"", response.Response.Headers["Content-Disposition"]);
    }

    [Fact]
    public async Task Download_ServiceThrowsAppException_PropagatesStatusCodeAndMessage()
    {
        var service = new FakeS3FilesService
        {
            OnGetFileDownload = _ => throw new AppException(404, "File not found in S3"),
        };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("GET", "/api/s3/download", query: new Dictionary<string, string?> { ["key"] = "data/missing.csv" });

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("File not found in S3", response.Json!.Value.GetProperty("message").GetString());
    }

    // ── POST /api/s3/retrigger ───────────────────────────────────────────────

    [Fact]
    public async Task Retrigger_Unauthenticated_Returns401()
    {
        var service = new FakeS3FilesService();
        await using var testApp = CreateApp(service);

        var response = await testApp.SendAsync("POST", "/api/s3/retrigger", new { fileName = "f.csv", s3Key = "data/f.csv" });

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task Retrigger_NoBodyAtAll_Returns400ForMissingFileName()
    {
        var service = new FakeS3FilesService();
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("POST", "/api/s3/retrigger", jsonBody: null);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("fileName is required", response.Json!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Retrigger_MissingFileName_Returns400WithExpectedMessage()
    {
        var service = new FakeS3FilesService();
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("POST", "/api/s3/retrigger", new { s3Key = "data/f.csv" });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("fileName is required", response.Json!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Retrigger_EmptyFileName_Returns400()
    {
        var service = new FakeS3FilesService();
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("POST", "/api/s3/retrigger", new { fileName = "", s3Key = "data/f.csv" });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("fileName is required", response.Json!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Retrigger_MissingS3Key_Returns400WithExpectedMessage()
    {
        var service = new FakeS3FilesService();
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("POST", "/api/s3/retrigger", new { fileName = "enrolment_1_20260101000000.csv" });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("s3Key is required", response.Json!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Retrigger_EmptyS3Key_Returns400()
    {
        var service = new FakeS3FilesService();
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("POST", "/api/s3/retrigger", new { fileName = "f.csv", s3Key = "" });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("s3Key is required", response.Json!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Retrigger_Success_PassesArgumentsAndReturns201WithSuccessEnvelope()
    {
        var service = new FakeS3FilesService
        {
            OnRetrigger = (_, _, _) => new RetriggerResult("job-123", "enrolment", "queued"),
        };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("POST", "/api/s3/retrigger", new
        {
            fileName = "enrolment_1_20260101000000.csv",
            s3Key = "data/enrolment_1_20260101000000.csv",
            pipelineName = "override",
        });

        Assert.Equal(201, response.StatusCode);
        Assert.Equal(("enrolment_1_20260101000000.csv", "data/enrolment_1_20260101000000.csv", "override"), service.LastRetriggerArgs);

        var body = response.Json!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var data = body.GetProperty("data");
        Assert.Equal("job-123", data.GetProperty("jobId").GetString());
        Assert.Equal("enrolment", data.GetProperty("pipelineName").GetString());
        Assert.Equal("queued", data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Retrigger_NoPipelineNameOverride_PassesNullThrough()
    {
        var service = new FakeS3FilesService
        {
            OnRetrigger = (_, _, _) => new RetriggerResult("job-1", "enrolment", "queued"),
        };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("POST", "/api/s3/retrigger", new { fileName = "enrolment_1_20260101000000.csv", s3Key = "data/f.csv" });

        Assert.Null(service.LastRetriggerArgs!.Value.PipelineName);
    }

    [Fact]
    public async Task Retrigger_ServiceThrowsAppException_PropagatesStatusCodeAndMessage()
    {
        var service = new FakeS3FilesService
        {
            OnRetrigger = (fileName, _, _) => throw new AppException(400, $"Cannot derive pipeline name from file: {fileName}"),
        };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("POST", "/api/s3/retrigger", new { fileName = "plainfile.csv", s3Key = "data/plainfile.csv" });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("Cannot derive pipeline name from file: plainfile.csv", response.Json!.Value.GetProperty("message").GetString());
    }
}
