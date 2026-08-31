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
using Monitor.Identity.Authorization;
using Monitor.Operations;
using Monitor.Operations.Domain;

namespace Monitor.UnitTests.Endpoints;

/// <summary>
/// Exercises <see cref="MonitorEndpoints.MapMonitorEndpoints"/> — the .NET collapse of
/// monitor.routes.ts (route wiring) + monitor.controller.ts (query parsing, page/limit
/// defaulting and clamping) around <see cref="IMonitorService"/> (business logic covered
/// separately by <c>MonitorServiceTests</c>). Boots a minimal, DB-free
/// <see cref="WebApplication"/> with a fake <see cref="IMonitorService"/> and a
/// controllable test authentication handler, using the same request-dispatch technique
/// as <c>FilesEndpointsTests</c> (driving the built pipeline directly against a
/// <see cref="DefaultHttpContext"/> rather than <c>TestServer</c>/<c>HttpClient</c>, which
/// cannot serialize <c>Results.Json</c> responses under this SDK's <c>System.Text.Json</c>).
///
/// Per MonitorEndpoints.cs's own doc comment: monitor.routes.ts applies only the bare
/// `authenticate` middleware to every route — `authorize(...)` is never called for
/// `/api/monitor/*`. All six routes below are exercised against the same
/// `AuthenticatedUser` policy, not a role-restricted one.
/// </summary>
public class MonitorEndpointsTests
{
    private sealed class FakeMonitorService : IMonitorService
    {
        public Func<MonitorParams, FileMonitorResult>? OnGetFileMonitor;
        public MonitorParams? LastFileMonitorParams { get; private set; }

        public Func<IReadOnlyList<string>>? OnGetPipelineNames;

        public Func<bool>? OnIsDbAvailable;

        public Func<string?, string?, DashboardData>? OnGetDashboard;
        public (string? StartDate, string? EndDate)? LastDashboardArgs { get; private set; }

        public Func<string?, string?, NotProcessedCount>? OnGetNotProcessedCount;
        public (string? StartDate, string? EndDate)? LastNotProcessedArgs { get; private set; }

        public Func<MonitorParams, FileMonitorResult>? OnGetReconciled;
        public MonitorParams? LastReconciledParams { get; private set; }

        public Task<FileMonitorResult> GetFileMonitorAsync(MonitorParams parameters, CancellationToken cancellationToken = default)
        {
            LastFileMonitorParams = parameters;
            return Task.FromResult(OnGetFileMonitor!(parameters));
        }

        public Task<IReadOnlyList<string>> GetPipelineNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OnGetPipelineNames!());

        public Task<bool> IsDbAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OnIsDbAvailable!());

        public Task<DashboardData> GetDashboardAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default)
        {
            LastDashboardArgs = (startDate, endDate);
            return Task.FromResult(OnGetDashboard!(startDate, endDate));
        }

        public Task<NotProcessedCount> GetNotProcessedCountAsync(string? startDate, string? endDate, CancellationToken cancellationToken = default)
        {
            LastNotProcessedArgs = (startDate, endDate);
            return Task.FromResult(OnGetNotProcessedCount!(startDate, endDate));
        }

        public Task<FileMonitorResult> GetReconciledAsync(MonitorParams parameters, CancellationToken cancellationToken = default)
        {
            LastReconciledParams = parameters;
            return Task.FromResult(OnGetReconciled!(parameters));
        }
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

    private sealed record HttpCallResult(int StatusCode, byte[] RawBody)
    {
        public JsonElement? Json => RawBody.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(RawBody) : null;
    }

    private sealed class BodyDetectionFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => false;
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

        public async Task<HttpCallResult> SendAsync(string path, IDictionary<string, string?>? query = null)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = App.Services,
            };
            context.Request.Method = "GET";
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

            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());

            var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            await Pipeline(context);

            responseBody.Position = 0;
            return new HttpCallResult(context.Response.StatusCode, responseBody.ToArray());
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static TestApp CreateApp(IMonitorService monitorService)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var holder = new PrincipalHolder();
        builder.Services.AddSingleton(holder);
        builder.Services.AddSingleton(monitorService);
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthorizationPolicyNames.AuthenticatedUser, policy => policy.RequireAuthenticatedUser()));

        var app = builder.Build();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMonitorEndpoints();
        app.UseEndpoints(_ => { });

        var pipeline = ((IApplicationBuilder)app).Build();
        return new TestApp(app, pipeline, holder);
    }

    private static void Authenticate(TestApp testApp) =>
        testApp.Principal.Principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "user-1") }, "Test"));

    private static FileMonitorResult MakeFileMonitorResult() => new(
        Data: new[] { new FileMonitorRow("f1_20260101000000.csv", "pipe", "2026-01-01T00:00:00Z", 1, 0, 1, MonitorStatuses.Processed, true, null) },
        Total: 1,
        Page: 1,
        Limit: 20,
        TotalPages: 1,
        DbAvailable: true,
        StatusBreakdown: new StatusBreakdown(1, 0, 0, 0));

    private static DashboardData MakeDashboardData() => new(
        Kpis: new DashboardKpis(TotalFiles: 10, Processed: 8, Failed: 1, InProgress: 1, NotProcessed: 0, RowsInserted: 100, RowsUpdated: 50, SuccessRate: 80, FailureRate: 10),
        PipelineStatus: new[] { new PipelineStatusItem("p1", 10, 8, 1, "2026-01-01T00:00:00.000Z", "warning") },
        Trend: new[] { new TrendPoint("2026-01-01", 8, 1, 1) },
        TopFailures: Array.Empty<TopFailureItem>(),
        RecentActivity: Array.Empty<ActivityItem>(),
        MostActivePipeline: "p1",
        HighestFailurePipeline: "p1",
        DbAvailable: true);

    // ── GET /api/monitor/dashboard ───────────────────────────────────────────

    [Fact]
    public async Task Dashboard_Unauthenticated_Returns401()
    {
        var service = new FakeMonitorService { OnGetDashboard = (_, _) => MakeDashboardData() };
        await using var testApp = CreateApp(service);

        var response = await testApp.SendAsync("/api/monitor/dashboard");

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_PassesStartAndEndDateThrough()
    {
        var service = new FakeMonitorService { OnGetDashboard = (_, _) => MakeDashboardData() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/dashboard", new Dictionary<string, string?>
        {
            ["startDate"] = "2026-01-01",
            ["endDate"] = "2026-01-31",
        });

        Assert.Equal(("2026-01-01", "2026-01-31"), service.LastDashboardArgs);
    }

    [Fact]
    public async Task Dashboard_NoQueryParams_PassesNulls()
    {
        var service = new FakeMonitorService { OnGetDashboard = (_, _) => MakeDashboardData() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/dashboard");

        Assert.Equal((null, null), service.LastDashboardArgs);
    }

    [Fact]
    public async Task Dashboard_Success_ReturnsSuccessEnvelopeWithMappedKpis()
    {
        var service = new FakeMonitorService { OnGetDashboard = (_, _) => MakeDashboardData() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("/api/monitor/dashboard");

        Assert.Equal(200, response.StatusCode);
        var body = response.Json!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var data = body.GetProperty("data");
        var kpis = data.GetProperty("kpis");
        Assert.Equal(10, kpis.GetProperty("totalFiles").GetInt32());
        Assert.Equal(80, kpis.GetProperty("successRate").GetDouble());
        Assert.Equal("p1", data.GetProperty("mostActivePipeline").GetString());
        Assert.Equal("p1", data.GetProperty("highestFailurePipeline").GetString());
        Assert.True(data.GetProperty("dbAvailable").GetBoolean());
        Assert.Equal("warning", data.GetProperty("pipelineStatus")[0].GetProperty("status").GetString());
    }

    // ── GET /api/monitor/not-processed-count ─────────────────────────────────

    [Fact]
    public async Task NotProcessedCount_Unauthenticated_Returns401()
    {
        var service = new FakeMonitorService { OnGetNotProcessedCount = (_, _) => new NotProcessedCount(0) };
        await using var testApp = CreateApp(service);

        var response = await testApp.SendAsync("/api/monitor/not-processed-count");

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task NotProcessedCount_PassesDateRangeThroughAndReturnsCount()
    {
        var service = new FakeMonitorService { OnGetNotProcessedCount = (_, _) => new NotProcessedCount(7) };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("/api/monitor/not-processed-count", new Dictionary<string, string?>
        {
            ["startDate"] = "2026-02-01",
            ["endDate"] = "2026-02-28",
        });

        Assert.Equal(("2026-02-01", "2026-02-28"), service.LastNotProcessedArgs);
        Assert.Equal(200, response.StatusCode);
        var body = response.Json!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.Equal(7, body.GetProperty("data").GetProperty("count").GetInt32());
    }

    // ── GET /api/monitor/reconcile ────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_Unauthenticated_Returns401()
    {
        var service = new FakeMonitorService { OnGetReconciled = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);

        var response = await testApp.SendAsync("/api/monitor/reconcile");

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task Reconcile_NoQueryParams_UsesControllerDefaults()
    {
        var service = new FakeMonitorService { OnGetReconciled = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/reconcile");

        var used = service.LastReconciledParams!;
        Assert.Null(used.StartDate);
        Assert.Null(used.Status);
        Assert.Null(used.Pipeline);
        Assert.Null(used.Search);
        Assert.Equal(1, used.Page);
        Assert.Equal(20, used.Limit);
        Assert.Equal("fileReceivedDate", used.SortBy);
        Assert.Equal("desc", used.SortOrder);
    }

    [Fact]
    public async Task Reconcile_LimitAboveTwoHundred_ClampsToTwoHundred()
    {
        var service = new FakeMonitorService { OnGetReconciled = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/reconcile", new Dictionary<string, string?> { ["limit"] = "500" });

        Assert.Equal(200, service.LastReconciledParams!.Limit);
    }

    [Fact]
    public async Task Reconcile_LimitBelowTwoHundred_PassesThroughUnclamped()
    {
        var service = new FakeMonitorService { OnGetReconciled = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/reconcile", new Dictionary<string, string?> { ["limit"] = "150" });

        Assert.Equal(150, service.LastReconciledParams!.Limit);
    }

    [Fact]
    public async Task Reconcile_PassesThroughAllFilterAndSortParams()
    {
        var service = new FakeMonitorService { OnGetReconciled = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/reconcile", new Dictionary<string, string?>
        {
            ["startDate"] = "2026-01-01",
            ["endDate"] = "2026-01-31",
            ["status"] = "Not Processed",
            ["pipeline"] = "enrolment",
            ["search"] = "abc",
            ["page"] = "4",
            ["limit"] = "50",
            ["sortBy"] = "fileName",
            ["sortOrder"] = "asc",
        });

        var used = service.LastReconciledParams!;
        Assert.Equal("2026-01-01", used.StartDate);
        Assert.Equal("2026-01-31", used.EndDate);
        Assert.Equal("Not Processed", used.Status);
        Assert.Equal("enrolment", used.Pipeline);
        Assert.Equal("abc", used.Search);
        Assert.Equal(4, used.Page);
        Assert.Equal(50, used.Limit);
        Assert.Equal("fileName", used.SortBy);
        Assert.Equal("asc", used.SortOrder);
    }

    [Fact]
    public async Task Reconcile_Success_ReturnsSuccessEnvelopeWithMappedResult()
    {
        var service = new FakeMonitorService { OnGetReconciled = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("/api/monitor/reconcile");

        Assert.Equal(200, response.StatusCode);
        var body = response.Json!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var data = body.GetProperty("data");
        Assert.Equal(1, data.GetProperty("total").GetInt32());
        Assert.True(data.GetProperty("dbAvailable").GetBoolean());
        Assert.Equal(1, data.GetProperty("statusBreakdown").GetProperty("processed").GetInt32());
        Assert.Equal("f1_20260101000000.csv", data.GetProperty("data")[0].GetProperty("fileName").GetString());
    }

    // ── GET /api/monitor/files ────────────────────────────────────────────────

    [Fact]
    public async Task Files_Unauthenticated_Returns401()
    {
        var service = new FakeMonitorService { OnGetFileMonitor = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);

        var response = await testApp.SendAsync("/api/monitor/files");

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task Files_NoQueryParams_UsesControllerDefaults()
    {
        var service = new FakeMonitorService { OnGetFileMonitor = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/files");

        var used = service.LastFileMonitorParams!;
        Assert.Equal(1, used.Page);
        Assert.Equal(20, used.Limit);
        Assert.Equal("fileReceivedDate", used.SortBy);
        Assert.Equal("desc", used.SortOrder);
    }

    [Fact]
    public async Task Files_LimitAboveOneHundred_ClampsToOneHundred()
    {
        var service = new FakeMonitorService { OnGetFileMonitor = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/files", new Dictionary<string, string?> { ["limit"] = "500" });

        Assert.Equal(100, service.LastFileMonitorParams!.Limit);
    }

    [Fact]
    public async Task Files_LimitOfOneHundredFiftyIsAboveTheFilesCapButBelowReconcilesCap()
    {
        // Documents the two distinct caps (100 for /files vs 200 for /reconcile) side by side.
        var service = new FakeMonitorService { OnGetFileMonitor = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/files", new Dictionary<string, string?> { ["limit"] = "150" });

        Assert.Equal(100, service.LastFileMonitorParams!.Limit);
    }

    [Fact]
    public async Task Files_LimitBelowOneHundred_PassesThroughUnclamped()
    {
        var service = new FakeMonitorService { OnGetFileMonitor = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/files", new Dictionary<string, string?> { ["limit"] = "5" });

        Assert.Equal(5, service.LastFileMonitorParams!.Limit);
    }

    [Fact]
    public async Task Files_PassesThroughAllFilterAndSortParams()
    {
        var service = new FakeMonitorService { OnGetFileMonitor = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        await testApp.SendAsync("/api/monitor/files", new Dictionary<string, string?>
        {
            ["startDate"] = "2026-01-01",
            ["endDate"] = "2026-01-31",
            ["status"] = "Failed",
            ["pipeline"] = "enrolment",
            ["search"] = "abc",
            ["page"] = "2",
            ["sortBy"] = "recordsInserted",
            ["sortOrder"] = "asc",
        });

        var used = service.LastFileMonitorParams!;
        Assert.Equal("2026-01-01", used.StartDate);
        Assert.Equal("2026-01-31", used.EndDate);
        Assert.Equal("Failed", used.Status);
        Assert.Equal("enrolment", used.Pipeline);
        Assert.Equal("abc", used.Search);
        Assert.Equal(2, used.Page);
        Assert.Equal("recordsInserted", used.SortBy);
        Assert.Equal("asc", used.SortOrder);
    }

    [Fact]
    public async Task Files_Success_ReturnsSuccessEnvelopeWithMappedResult()
    {
        var service = new FakeMonitorService { OnGetFileMonitor = _ => MakeFileMonitorResult() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("/api/monitor/files");

        Assert.Equal(200, response.StatusCode);
        var body = response.Json!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var data = body.GetProperty("data");
        Assert.Equal(1, data.GetProperty("total").GetInt32());
        Assert.Equal(1, data.GetProperty("page").GetInt32());
        Assert.Equal(20, data.GetProperty("limit").GetInt32());
    }

    // ── GET /api/monitor/pipeline-names ───────────────────────────────────────

    [Fact]
    public async Task PipelineNames_Unauthenticated_Returns401()
    {
        var service = new FakeMonitorService { OnGetPipelineNames = () => Array.Empty<string>() };
        await using var testApp = CreateApp(service);

        var response = await testApp.SendAsync("/api/monitor/pipeline-names");

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task PipelineNames_Success_ReturnsSuccessEnvelopeWithNamesArray()
    {
        var service = new FakeMonitorService { OnGetPipelineNames = () => new[] { "alpha", "beta" } };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("/api/monitor/pipeline-names");

        Assert.Equal(200, response.StatusCode);
        var body = response.Json!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var names = body.GetProperty("data").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "alpha", "beta" }, names);
    }

    [Fact]
    public async Task PipelineNames_Empty_ReturnsSuccessEnvelopeWithEmptyArray()
    {
        var service = new FakeMonitorService { OnGetPipelineNames = () => Array.Empty<string>() };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("/api/monitor/pipeline-names");

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(response.Json!.Value.GetProperty("data").EnumerateArray());
    }

    // ── GET /api/monitor/db-status ────────────────────────────────────────────

    [Fact]
    public async Task DbStatus_Unauthenticated_Returns401()
    {
        var service = new FakeMonitorService { OnIsDbAvailable = () => true };
        await using var testApp = CreateApp(service);

        var response = await testApp.SendAsync("/api/monitor/db-status");

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task DbStatus_Available_ReturnsTrue()
    {
        var service = new FakeMonitorService { OnIsDbAvailable = () => true };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("/api/monitor/db-status");

        Assert.Equal(200, response.StatusCode);
        var body = response.Json!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("data").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task DbStatus_Unavailable_ReturnsFalse()
    {
        var service = new FakeMonitorService { OnIsDbAvailable = () => false };
        await using var testApp = CreateApp(service);
        Authenticate(testApp);

        var response = await testApp.SendAsync("/api/monitor/db-status");

        Assert.Equal(200, response.StatusCode);
        Assert.False(response.Json!.Value.GetProperty("data").GetProperty("available").GetBoolean());
    }
}
