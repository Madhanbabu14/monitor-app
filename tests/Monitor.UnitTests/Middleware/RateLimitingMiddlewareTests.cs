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

    private static DefaultHttpContext MakeContext(string path = "/api/monitor/status", string? ip = "10.0.0.1")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (ip is not null)
        {
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        }
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
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

        // draft-6 standardHeaders: RateLimit-Reset is seconds-until-reset
        // (a small delta), never an absolute Unix timestamp.
        var resetSeconds = int.Parse(context.Response.Headers["RateLimit-Reset"]!);
        Assert.InRange(resetSeconds, 0, 60);
        Assert.Equal("5;w=60", context.Response.Headers["RateLimit-Policy"]);
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

    [Fact]
    public async Task InvokeAsync_AtExactBoundary_LastAllowedRequestHasZeroRemaining_NextIsRejected()
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 3 });
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, options);

        var first = MakeContext();
        var second = MakeContext();
        var third = MakeContext();
        var fourth = MakeContext();

        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);
        await middleware.InvokeAsync(third);
        await middleware.InvokeAsync(fourth);

        Assert.Equal(200, first.Response.StatusCode);
        Assert.Equal("2", first.Response.Headers["RateLimit-Remaining"]);

        Assert.Equal(200, second.Response.StatusCode);
        Assert.Equal("1", second.Response.Headers["RateLimit-Remaining"]);

        Assert.Equal(200, third.Response.StatusCode);
        Assert.Equal("0", third.Response.Headers["RateLimit-Remaining"]);

        // Fourth request in the same window exceeds MaxRequests=3.
        Assert.Equal(429, fourth.Response.StatusCode);
        // Remaining must clamp at zero, never go negative.
        Assert.Equal("0", fourth.Response.Headers["RateLimit-Remaining"]);
    }

    [Fact]
    public async Task InvokeAsync_WindowExpires_CounterResetsAndRequestIsAllowedAgain()
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 30, MaxRequests = 1 });
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, options);
        var key = "192.168.1.50";

        var first = MakeContext(ip: key);
        await middleware.InvokeAsync(first);
        Assert.Equal(200, first.Response.StatusCode);

        var second = MakeContext(ip: key);
        await middleware.InvokeAsync(second);
        Assert.Equal(429, second.Response.StatusCode);

        // Wait for the (very short) window to expire.
        await Task.Delay(150);

        var third = MakeContext(ip: key);
        await middleware.InvokeAsync(third);

        Assert.Equal(200, third.Response.StatusCode);
        Assert.Equal("0", third.Response.Headers["RateLimit-Remaining"]);
    }

    [Fact]
    public async Task InvokeAsync_NoRemoteIpAddress_FallsBackToSharedUnknownBucket()
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 1 });
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, options);

        var first = MakeContext(ip: null);
        var second = MakeContext(ip: null);

        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        Assert.Equal(200, first.Response.StatusCode);
        // Both requests share the same "unknown" bucket, so the second is
        // counted against the same limit and rejected.
        Assert.Equal(429, second.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/")]
    [InlineData("/api/monitor")]
    [InlineData("/API/monitor")]
    public async Task InvokeAsync_PathsUnderOrEqualToApiSegment_AreRateLimited(string path)
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 1 });
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, options);

        var first = MakeContext(path: path);
        var second = MakeContext(path: path);

        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        Assert.Equal(200, first.Response.StatusCode);
        Assert.Equal(429, second.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_PathThatOnlyPrefixMatches_IsNotTreatedAsUnderApiSegment()
    {
        // "/apiextra" is NOT the "/api" segment (StartsWithSegments is
        // segment-aware), so it must bypass the limiter entirely, same as
        // any other non-/api route.
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 1 });
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, options);

        var first = MakeContext(path: "/apiextra");
        var second = MakeContext(path: "/apiextra");

        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        Assert.Equal(200, first.Response.StatusCode);
        Assert.Equal(200, second.Response.StatusCode);
        Assert.False(first.Response.Headers.ContainsKey("RateLimit-Limit"));
    }

    [Theory]
    [InlineData(60_000, 60)]
    [InlineData(30_000, 30)]
    [InlineData(900_000, 900)]
    [InlineData(1_500, 2)]
    public async Task InvokeAsync_RateLimitPolicyHeader_ReflectsConfiguredWindowInSeconds(int windowMs, int expectedWindowSeconds)
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = windowMs, MaxRequests = 100 });
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, options);

        var context = MakeContext();
        await middleware.InvokeAsync(context);

        Assert.Equal($"100;w={expectedWindowSeconds}", context.Response.Headers["RateLimit-Policy"]);
    }

    [Fact]
    public async Task InvokeAsync_OverLimit_SetsJsonContentTypeAndDoesNotCallNext()
    {
        var options = new StaticOptionsMonitor(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 1 });
        var called = 0;
        var middleware = new RateLimitingMiddleware(_ =>
        {
            called++;
            return Task.CompletedTask;
        }, options);

        var first = MakeContext();
        await middleware.InvokeAsync(first);

        var second = MakeContext();
        await middleware.InvokeAsync(second);

        Assert.Equal(1, called); // next() only invoked for the allowed first request
        Assert.StartsWith("application/json", second.Response.ContentType);

        var body = await ReadBodyAsync(second);
        Assert.Equal("{\"status\":\"error\",\"message\":\"Too many requests, please try again later\"}", body);
    }
}
