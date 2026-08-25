using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Monitor.Api.Middleware;
using Monitor.Core.Options;

namespace Monitor.UnitTests.Middleware;

public class RateLimitingMiddlewareTests
{
    private sealed class StaticOptionsMonitor : IOptionsMonitor<RateLimitOptions>
    {
        public StaticOptionsMonitor(RateLimitOptions value) => CurrentValue = value;
        public RateLimitOptions CurrentValue { get; }
        public RateLimitOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RateLimitOptions, string?> listener) => null;
    }

    private static DefaultHttpContext MakeContext(string path = "/api/monitor/status", string ip = "10.0.0.1")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        return context;
    }

    [Fact]
    public async Task InvokeAsync_PathOutsideApi_PassesThroughWithoutCounting()
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 1 });
        var called = 0;
        var middleware = new RateLimitingMiddleware(_ =>
        {
            called++;
            return Task.CompletedTask;
        }, options);

        var context = MakeContext(path: "/health");
        await middleware.InvokeAsync(context);
        await middleware.InvokeAsync(context);

        Assert.Equal(2, called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_UnderLimit_PassesThroughAndSetsHeaders()
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 5 });
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, options);

        var context = MakeContext();
        await middleware.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers["RateLimit-Limit"]);
        Assert.Equal("4", context.Response.Headers["RateLimit-Remaining"]);
    }

    [Fact]
    public async Task InvokeAsync_OverLimit_Returns429WithLegacyEnvelope()
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 1 });
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, options);

        var first = MakeContext();
        await middleware.InvokeAsync(first);
        Assert.Equal(200, first.Response.StatusCode);

        var second = MakeContext();
        await middleware.InvokeAsync(second);

        Assert.Equal(429, second.Response.StatusCode);
        second.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(second.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("\"status\":\"error\"", body);
        Assert.Contains("Too many requests, please try again later", body);
    }

    [Fact]
    public async Task InvokeAsync_DifferentClients_CountedSeparately()
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 1 });
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, options);

        var clientA = MakeContext(ip: "10.0.0.1");
        var clientB = MakeContext(ip: "10.0.0.2");

        await middleware.InvokeAsync(clientA);
        await middleware.InvokeAsync(clientB);

        Assert.Equal(200, clientA.Response.StatusCode);
        Assert.Equal(200, clientB.Response.StatusCode);
    }
}
