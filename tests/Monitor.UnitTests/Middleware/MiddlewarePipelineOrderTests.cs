using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monitor.Api.Middleware;
using Monitor.Core.Options;

namespace Monitor.UnitTests.Middleware;

/// <summary>
/// Composition-level tests for the ordering Program.cs wires up:
///   UseAppExceptionHandling -> (body-size bump) -> UseSecurityHeaders ->
///   UseCors -> UseResponseCompression -> UseSerilogRequestLogging ->
///   UseApiRateLimiting -> endpoints -> MapFallback.
///
/// Booting the *actual* Program.cs host is out of scope for unit tests: it
/// requires a reachable Postgres (PrimaryDatabaseStartupCheck) and fully
/// populated secrets (Jwt/Aws/AzureAd options), which belong to
/// Monitor.IntegrationTests with Testcontainers, not here. Instead, this
/// wires the two host-agnostic, DB-free middlewares this slice owns
/// (SecurityHeadersMiddleware, RateLimitingMiddleware) together through a
/// real <see cref="ApplicationBuilder"/>, in the exact relative order
/// Program.cs uses, to verify their interaction: security headers must be
/// registered "outside" rate limiting, so they still apply even when the
/// rate limiter short-circuits the pipeline with a 429.
/// </summary>
public class MiddlewarePipelineOrderTests
{
    private sealed class StaticOptionsMonitor : IOptionsMonitor<RateLimitOptions>
    {
        public StaticOptionsMonitor(RateLimitOptions value) => CurrentValue = value;
        public RateLimitOptions CurrentValue { get; }
        public RateLimitOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RateLimitOptions, string?> listener) => null;
    }

    /// <summary>
    /// Minimal IHttpResponseFeature that actually stores and can fire
    /// OnStarting callbacks, standing in for a live Kestrel connection.
    /// </summary>
    private sealed class OnStartingCapableResponseFeature : IHttpResponseFeature
    {
        private readonly List<Func<object, Task>> _callbacks = new();
        private readonly List<object> _states = new();

        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state)
        {
            _callbacks.Add(callback);
            _states.Add(state);
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public async Task FireOnStartingAsync()
        {
            HasStarted = true;
            for (var i = 0; i < _callbacks.Count; i++)
            {
                await _callbacks[i](_states[i]);
            }
        }
    }

    private static RequestDelegate BuildPipeline(RateLimitOptions rateLimitOptions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<RateLimitOptions>>(new StaticOptionsMonitor(rateLimitOptions));
        using var provider = services.BuildServiceProvider();
        var builder = new ApplicationBuilder(provider);

        // Same relative order as Program.cs: security headers registered
        // before the API rate limiter.
        builder.UseSecurityHeaders();
        builder.UseApiRateLimiting();
        builder.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        return builder.Build();
    }

    private static (DefaultHttpContext context, OnStartingCapableResponseFeature feature) MakeContext(string path, string ip = "10.0.0.1")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        var feature = new OnStartingCapableResponseFeature { Body = context.Response.Body };
        context.Features.Set<IHttpResponseFeature>(feature);
        return (context, feature);
    }

    [Fact]
    public async Task Pipeline_ApiRequestUnderLimit_HasBothSecurityAndRateLimitHeaders()
    {
        var pipeline = BuildPipeline(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 5 });
        var (context, feature) = MakeContext("/api/monitor/status");

        await pipeline(context);
        await feature.FireOnStartingAsync();

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("SAMEORIGIN", feature.Headers["X-Frame-Options"]);
        Assert.Equal("5", feature.Headers["RateLimit-Limit"]);
        Assert.Equal("4", feature.Headers["RateLimit-Remaining"]);
    }

    [Fact]
    public async Task Pipeline_NonApiRequest_HasSecurityHeadersButNoRateLimitHeaders()
    {
        var pipeline = BuildPipeline(new RateLimitOptions { WindowMs = 60_000, MaxRequests = 5 });
        var (context, feature) = MakeContext("/health");

        await pipeline(context);
        await feature.FireOnStartingAsync();

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("SAMEORIGIN", feature.Headers["X-Frame-Options"]);
        Assert.False(feature.Headers.ContainsKey("RateLimit-Limit"));
    }

    [Fact]
    public async Task Pipeline_ApiRequestOverLimit_StillCarriesSecurityHeadersOnThe429()
    {
        // Because UseSecurityHeaders is registered before UseApiRateLimiting
        // in Program.cs, its OnStarting registration happens on every
        // request regardless of whether the rate limiter short-circuits the
        // pipeline with a 429 - this is the behavior that ordering exists to
        // guarantee.
        var options = new RateLimitOptions { WindowMs = 60_000, MaxRequests = 1 };
        var pipeline = BuildPipeline(options);

        var (first, firstFeature) = MakeContext("/api/monitor/status", ip: "10.0.0.9");
        await pipeline(first);
        await firstFeature.FireOnStartingAsync();
        Assert.Equal(200, first.Response.StatusCode);

        var (second, secondFeature) = MakeContext("/api/monitor/status", ip: "10.0.0.9");
        await pipeline(second);
        await secondFeature.FireOnStartingAsync();

        Assert.Equal(429, second.Response.StatusCode);
        // Security headers still present on the rejected response.
        Assert.Equal("SAMEORIGIN", secondFeature.Headers["X-Frame-Options"]);
        Assert.Equal("nosniff", secondFeature.Headers["X-Content-Type-Options"]);
        // Rate limit headers still describe the exhausted window.
        Assert.Equal("0", secondFeature.Headers["RateLimit-Remaining"]);

        second.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(second.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("\"status\":\"error\"", body);
        Assert.Contains("Too many requests, please try again later", body);
    }
}
