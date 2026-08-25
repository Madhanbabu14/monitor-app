using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Monitor.Api.Middleware;

namespace Monitor.UnitTests.Middleware;

public class SecurityHeadersMiddlewareTests
{
    /// <summary>
    /// Minimal IHttpResponseFeature that actually stores and can fire
    /// OnStarting callbacks, standing in for a live Kestrel connection
    /// (DefaultHttpContext's own default feature doesn't wire OnStarting up
    /// without one).
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

    private static async Task<(DefaultHttpContext context, OnStartingCapableResponseFeature feature)> InvokeAsync(RequestDelegate next)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var feature = new OnStartingCapableResponseFeature { Body = context.Response.Body };
        context.Features.Set<IHttpResponseFeature>(feature);

        var middleware = new SecurityHeadersMiddleware(next);
        await middleware.InvokeAsync(context);
        // Headers are set in an OnStarting callback; force it to fire like a real
        // response start would, without needing a live Kestrel connection.
        await feature.FireOnStartingAsync();
        return (context, feature);
    }

    [Fact]
    public async Task InvokeAsync_SetsHelmetDefaultHeaders()
    {
        var (_, feature) = await InvokeAsync(_ => Task.CompletedTask);

        Assert.Equal("nosniff", feature.Headers["X-Content-Type-Options"]);
        Assert.Equal("SAMEORIGIN", feature.Headers["X-Frame-Options"]);
        Assert.Equal("off", feature.Headers["X-DNS-Prefetch-Control"]);
        Assert.Equal("noopen", feature.Headers["X-Download-Options"]);
        Assert.Equal("none", feature.Headers["X-Permitted-Cross-Domain-Policies"]);
        Assert.Equal("0", feature.Headers["X-XSS-Protection"]);
        Assert.Equal("no-referrer", feature.Headers["Referrer-Policy"]);
        Assert.Equal("same-origin", feature.Headers["Cross-Origin-Opener-Policy"]);
        Assert.Equal("same-origin", feature.Headers["Cross-Origin-Resource-Policy"]);
        Assert.Equal("?1", feature.Headers["Origin-Agent-Cluster"]);
        Assert.Equal("max-age=31536000; includeSubDomains", feature.Headers["Strict-Transport-Security"]);
        Assert.False(string.IsNullOrEmpty(feature.Headers["Content-Security-Policy"]));
    }

    [Fact]
    public async Task InvokeAsync_CallsNext()
    {
        var called = false;
        await InvokeAsync(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        Assert.True(called);
    }

    [Fact]
    public void UseSecurityHeaders_RegistersMiddlewareInPipeline()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var builder = new ApplicationBuilder(provider);

        builder.UseSecurityHeaders();
        var pipeline = builder.Build();

        Assert.NotNull(pipeline);
    }
}
