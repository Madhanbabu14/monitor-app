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

    [Fact]
    public async Task InvokeAsync_ContentSecurityPolicy_MatchesHelmetDefaultDirectivesExactly()
    {
        // Locks the exact helmet v6/v7 default CSP directive string (order and
        // punctuation matter for parity with the source's `app.use(helmet())`
        // with no options passed).
        var (_, feature) = await InvokeAsync(_ => Task.CompletedTask);

        const string expectedCsp =
            "default-src 'self';base-uri 'self';font-src 'self' https: data:;" +
            "form-action 'self';frame-ancestors 'self';img-src 'self' data:;" +
            "object-src 'none';script-src 'self';script-src-attr 'none';" +
            "style-src 'self' https: 'unsafe-inline';upgrade-insecure-requests";

        Assert.Equal(expectedCsp, feature.Headers["Content-Security-Policy"]);
    }

    [Fact]
    public async Task InvokeAsync_SetsExactlyTheDocumentedHeaderSet_NoMoreNoLess()
    {
        var (_, feature) = await InvokeAsync(_ => Task.CompletedTask);

        var expectedHeaderNames = new[]
        {
            "Content-Security-Policy",
            "Cross-Origin-Opener-Policy",
            "Cross-Origin-Resource-Policy",
            "Origin-Agent-Cluster",
            "Referrer-Policy",
            "Strict-Transport-Security",
            "X-Content-Type-Options",
            "X-DNS-Prefetch-Control",
            "X-Download-Options",
            "X-Frame-Options",
            "X-Permitted-Cross-Domain-Policies",
            "X-XSS-Protection",
        };

        Assert.Equal(expectedHeaderNames.Length, feature.Headers.Count);
        foreach (var name in expectedHeaderNames)
        {
            Assert.True(feature.Headers.ContainsKey(name), $"Expected header '{name}' to be set.");
        }
    }

    [Fact]
    public async Task InvokeAsync_NextThrows_PropagatesExceptionAndStillRegistersHeaderCallback()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var feature = new OnStartingCapableResponseFeature { Body = context.Response.Body };
        context.Features.Set<IHttpResponseFeature>(feature);

        var thrown = new InvalidOperationException("downstream failure");
        var middleware = new SecurityHeadersMiddleware(_ => throw thrown);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.Same(thrown, ex);

        // The OnStarting callback was registered (SecurityHeadersMiddleware
        // registers it before calling next), even though this particular
        // request never reached the point of actually starting the response.
        await feature.FireOnStartingAsync();
        Assert.Equal("nosniff", feature.Headers["X-Content-Type-Options"]);
    }

    [Fact]
    public async Task InvokeAsync_HeadersNotAppliedUntilResponseActuallyStarts()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var feature = new OnStartingCapableResponseFeature { Body = context.Response.Body };
        context.Features.Set<IHttpResponseFeature>(feature);

        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        // Before the hosting layer fires OnStarting, no headers should be visible yet.
        Assert.False(feature.Headers.ContainsKey("X-Frame-Options"));

        await feature.FireOnStartingAsync();

        Assert.Equal("SAMEORIGIN", feature.Headers["X-Frame-Options"]);
    }
}
