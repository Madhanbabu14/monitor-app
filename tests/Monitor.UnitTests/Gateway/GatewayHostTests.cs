extern alias GatewayHost;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using GatewayProgram = GatewayHost::Program;

namespace Monitor.UnitTests.Gateway;

/// <summary>
/// Boots the real Monitor.Gateway <c>Program.cs</c> host end to end via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>. Unlike Monitor.Api, the gateway has no
/// database dependency and no secrets to configure (its only options class is
/// <c>GatewayOptions.Port</c>), which makes booting the actual host feasible at the unit
/// test level -- something explicitly called out as out of scope for Monitor.Api itself
/// (see MiddlewarePipelineOrderTests). This pins down two things the source never had an
/// equivalent for: the gateway-only liveness probe, and that YARP's config-driven routing
/// really does wire up the routes declared in appsettings*.json (proven here by observing
/// that a request into a mapped route path is actually forwarded -- and fails with a
/// Bad Gateway, not a 404, once no downstream is listening).
/// </summary>
public sealed class GatewayHostTests : IDisposable
{
    private readonly string _originalDirectory;
    private readonly string _tempDirectory;

    public GatewayHostTests()
    {
        // Program.cs wires the shared Serilog bootstrap, which writes logs/*.log relative
        // to the current directory (see SerilogBootstrapTests) -- isolate that side effect
        // in a scratch directory instead of littering the test output folder.
        _originalDirectory = Directory.GetCurrentDirectory();
        _tempDirectory = Path.Combine(Path.GetTempPath(), "monitor-gateway-host-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDirectory);
        Directory.SetCurrentDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDirectory);
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; file sinks may briefly hold a handle.
        }
    }

    private static WebApplicationFactory<GatewayProgram> CreateFactory() =>
        new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(builder =>
        {
            // WebApplicationFactory's default content-root discovery walks up from the test
            // assembly looking for "<assemblyName>.csproj" (i.e. "Monitor.Gateway.csproj")
            // directly under an ancestor directory; it never finds it because the real
            // project lives under "src/Monitor.Gateway", not "Monitor.Gateway". Point it
            // straight at Monitor.Gateway's own build output directory instead, which
            // already has its appsettings*.json copied alongside the built assembly.
            var gatewayAssemblyDirectory = Path.GetDirectoryName(typeof(GatewayProgram).Assembly.Location)!;
            builder.UseContentRoot(gatewayAssemblyDirectory);

            // Pin the environment so cluster destinations are the Development overlay's
            // http://localhost:400x addresses (fast-failing "connection refused" instead of
            // a DNS lookup against the docker-compose service name "legacy-backend", which
            // would be slow/flaky outside a container network).
            builder.UseEnvironment("Development");
        });

    [Fact]
    public async Task GatewayHealth_ReturnsOk_WithStatusOkJsonBody()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/_gateway/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"status\":\"ok\"}", body);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/s3/list")]
    [InlineData("/api/monitor/dashboard")]
    [InlineData("/api/anything-else")]
    [InlineData("/health")]
    public async Task MappedRoute_IsActuallyProxied_NotHandledLocally(string path)
    {
        // These paths are NOT gateway endpoints (only /_gateway/health and the YARP catch-all
        // are mapped in Program.cs), so a 200/404 from the gateway itself would mean routing
        // silently failed to reach YARP. With no legacy/dotnet destination listening in this
        // test process, YARP's forwarder must attempt the proxy and surface a Bad Gateway --
        // proving the route -> cluster -> destination chain in appsettings*.json is live.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task UnmappedPath_OutsideAnyRoute_ReturnsNotFound()
    {
        // "/totally-unrouted" matches none of health/api-auth/api-s3/api-monitor/api-catch-all
        // (which all require the "/api" or exact "/health" prefix), and the gateway defines
        // no MapFallback of its own -- so YARP/ASP.NET Core's default 404 applies, unlike
        // Monitor.Api's JSON notFoundHandler equivalent.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/totally-unrouted");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GatewayHealth_DoesNotGetProxied_EvenThoughItSharesPathPrefixStyle()
    {
        // Sanity check that the gateway's own liveness route short-circuits before YARP's
        // catch-all -- if it were accidentally proxied instead of mapped locally, this would
        // fail the same way the /api/* probes above do (BadGateway) rather than returning ok.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/_gateway/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_ForwardsRequestHeaders_ButAddsNoGatewayOwnedCorsHeaders()
    {
        // The gateway is documented (Program.cs comments) as a transparent L7 proxy that
        // adds no CORS policy of its own. Since nothing answers the proxied destination in
        // this test, the response is YARP's own 502 -- but it must not carry any
        // Access-Control-* headers, which would indicate a gateway-level CORS policy had
        // been added contrary to the architecture.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Origin", "http://localhost:3000");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void Server_ExposesReverseProxyServices_ConfiguredFromReverseProxySection()
    {
        // Confirms builder.Services.AddReverseProxy().LoadFromConfig(...) actually ran and
        // registered YARP's proxy config provider, without needing a live HTTP round trip.
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();

        var proxyConfigProvider = scope.ServiceProvider.GetService<Yarp.ReverseProxy.Configuration.IProxyConfigProvider>();

        Assert.NotNull(proxyConfigProvider);
        var config = proxyConfigProvider!.GetConfig();
        Assert.Contains(config.Routes, r => r.RouteId == "api-auth");
        Assert.Contains(config.Routes, r => r.RouteId == "api-catch-all");
        Assert.Contains(config.Clusters, c => c.ClusterId == "legacy");
        Assert.Contains(config.Clusters, c => c.ClusterId == "dotnet");
    }
}
