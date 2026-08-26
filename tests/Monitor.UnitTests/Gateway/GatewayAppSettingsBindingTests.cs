using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;
using Monitor.Gateway.Options;

namespace Monitor.UnitTests.Gateway;

/// <summary>
/// Binds the actual appsettings.json / appsettings.Development.json shipped with
/// Monitor.Gateway (linked into TestData/Gateway/ via the csproj), the same way
/// Program.cs's builder.Configuration would layer them, and inspects the raw
/// "ReverseProxy" tree the way <c>AddReverseProxy().LoadFromConfig</c> would read it.
/// This guards against the checked-in JSON silently drifting away from the
/// "every route currently targets the legacy cluster" strangler-fig invariant the
/// architecture brief calls out, and from GatewayOptions.Port disagreeing with the
/// Dockerfile's EXPOSE 8080 / docker-compose's Gateway__Port env var.
/// </summary>
public class GatewayAppSettingsBindingTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Gateway", fileName);

    private static IConfiguration BuildConfiguration(bool includeDevelopmentOverlay)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(TestDataPath("appsettings.json"), optional: false);

        if (includeDevelopmentOverlay)
        {
            builder.AddJsonFile(TestDataPath("appsettings.Development.json"), optional: false);
        }

        return builder.Build();
    }

    [Fact]
    public void BaseAppSettingsJson_GatewayPort_Is8080()
    {
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);
        var services = new ServiceCollection();
        services.AddValidatedOptions<GatewayOptions>(configuration, GatewayOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GatewayOptions>>().Value;

        Assert.Equal(8080, options.Port);
    }

    [Fact]
    public void DevelopmentOverlay_GatewayPort_StaysAt8080()
    {
        var configuration = BuildConfiguration(includeDevelopmentOverlay: true);
        var services = new ServiceCollection();
        services.AddValidatedOptions<GatewayOptions>(configuration, GatewayOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GatewayOptions>>().Value;

        Assert.Equal(8080, options.Port);
    }

    [Theory]
    [InlineData("health", "1", "legacy", "/health")]
    [InlineData("api-auth", "2", "legacy", "/api/auth/{**catch-all}")]
    [InlineData("api-s3", "3", "legacy", "/api/s3/{**catch-all}")]
    [InlineData("api-monitor", "4", "legacy", "/api/monitor/{**catch-all}")]
    [InlineData("api-catch-all", "100", "legacy", "/api/{**catch-all}")]
    public void BaseAppSettingsJson_EveryRoute_TargetsLegacyCluster_WithExpectedOrderAndPath(
        string routeId, string expectedOrder, string expectedCluster, string expectedPath)
    {
        // Every route currently targets "legacy" because, as of this slice, no bounded
        // context has mapped endpoints on Monitor.Api yet -- cutting a route over is meant
        // to be a config-only change (ClusterId flips to "dotnet"), never a code change.
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);

        var section = configuration.GetSection($"ReverseProxy:Routes:{routeId}");

        Assert.True(section.Exists());
        Assert.Equal(expectedOrder, section["Order"]);
        Assert.Equal(expectedCluster, section["ClusterId"]);
        Assert.Equal(expectedPath, section["Match:Path"]);
    }

    [Fact]
    public void BaseAppSettingsJson_RouteOrders_AreUnique_AndCatchAllSortsLast()
    {
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);
        var routeIds = new[] { "health", "api-auth", "api-s3", "api-monitor", "api-catch-all" };

        var orders = routeIds
            .Select(id => int.Parse(configuration[$"ReverseProxy:Routes:{id}:Order"]!))
            .ToList();

        Assert.Equal(orders.Count, orders.Distinct().Count());
        Assert.Equal(orders.Max(), orders.Single(o => o == 100));
        Assert.True(
            orders.Where(o => o != 100).All(o => o < 100),
            "every specific route must sort before the generic /api/{**catch-all} fallback");
    }

    [Theory]
    [InlineData("api-auth", "/api/auth/{**catch-all}")]
    [InlineData("api-s3", "/api/s3/{**catch-all}")]
    [InlineData("api-monitor", "/api/monitor/{**catch-all}")]
    public void BaseAppSettingsJson_BoundedContextRoutes_ScopeExactlyToTheirOwnPrefix(
        string routeId, string expectedPath)
    {
        // Matches the scope-reality-check in the architecture brief: only
        // /api/auth, /api/s3 and /api/monitor are live route groups.
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);

        Assert.Equal(expectedPath, configuration[$"ReverseProxy:Routes:{routeId}:Match:Path"]);
    }

    [Fact]
    public void BaseAppSettingsJson_LegacyCluster_PointsAtLegacyBackendServiceName()
    {
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);

        Assert.Equal(
            "http://legacy-backend:4001/",
            configuration["ReverseProxy:Clusters:legacy:Destinations:destination1:Address"]);
    }

    [Fact]
    public void BaseAppSettingsJson_DotnetCluster_PointsAtMonitorApiServiceName()
    {
        // Not referenced by any route yet (no ClusterId is "dotnet" in appsettings.json),
        // but must already be declared and correctly addressed so future cutover slices
        // only need to flip a route's ClusterId, not add a whole new cluster.
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);

        Assert.Equal(
            "http://monitor-api:4000/",
            configuration["ReverseProxy:Clusters:dotnet:Destinations:destination1:Address"]);

        var routeIds = new[] { "health", "api-auth", "api-s3", "api-monitor", "api-catch-all" };
        Assert.DoesNotContain(routeIds, id =>
            configuration[$"ReverseProxy:Routes:{id}:ClusterId"] == "dotnet");
    }

    [Fact]
    public void DevelopmentOverlay_ClusterDestinations_PointAtLocalhostPorts()
    {
        // appsettings.Development.json overrides only the Clusters (host-container names
        // don't resolve outside docker-compose); Routes are inherited unchanged from the
        // base file via ASP.NET Core's configuration layering.
        var configuration = BuildConfiguration(includeDevelopmentOverlay: true);

        Assert.Equal(
            "http://localhost:4001/",
            configuration["ReverseProxy:Clusters:legacy:Destinations:destination1:Address"]);
        Assert.Equal(
            "http://localhost:4000/",
            configuration["ReverseProxy:Clusters:dotnet:Destinations:destination1:Address"]);

        // Routes are unaffected by the Development overlay.
        Assert.Equal("legacy", configuration["ReverseProxy:Routes:api-auth:ClusterId"]);
        Assert.Equal("/api/auth/{**catch-all}", configuration["ReverseProxy:Routes:api-auth:Match:Path"]);
    }

    [Fact]
    public void BaseAppSettingsJson_SerilogAndAllowedHosts_ArePresentButNotBoundByGatewayOptions()
    {
        var configuration = BuildConfiguration(includeDevelopmentOverlay: false);

        Assert.Equal("Information", configuration["Serilog:MinimumLevel"]);
        Assert.Equal("*", configuration["AllowedHosts"]);
    }
}
