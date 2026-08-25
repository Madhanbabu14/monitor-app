using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Monitor.Core.Errors;
using Monitor.Core.Options;

namespace Monitor.Api.Middleware;

/// <summary>
/// .NET 6 analogue of the source's <c>express-rate-limit</c> instance, which
/// is mounted on <c>/api</c> only (app.ts: <c>app.use('/api', limiter)</c>).
/// .NET 6 predates the built-in `Microsoft.AspNetCore.RateLimiting`
/// middleware (that's a .NET 7 feature), so this hand-rolls the same
/// fixed-window behaviour: per-client-IP counter, reset every
/// <see cref="RateLimitOptions.WindowMs"/>, capped at
/// <see cref="RateLimitOptions.MaxRequests"/>. Mirrors
/// `standardHeaders: true, legacyHeaders: false` by emitting the draft
/// `RateLimit-*` headers (never the legacy `X-RateLimit-*` ones), and the
/// exact 429 envelope the source returned via its `message` option.
/// </summary>
public sealed class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<RateLimitOptions> _options;
    private readonly ConcurrentDictionary<string, Window> _windows = new();

    public RateLimitingMiddleware(RequestDelegate next, IOptionsMonitor<RateLimitOptions> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var options = _options.CurrentValue;
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTimeOffset.UtcNow;

        int count;
        DateTimeOffset resetAt;
        var window = _windows.GetOrAdd(key, _ => new Window());
        lock (window)
        {
            if (window.Count == 0 || now >= window.ResetAt)
            {
                window.ResetAt = now.AddMilliseconds(options.WindowMs);
                window.Count = 1;
            }
            else
            {
                window.Count++;
            }

            count = window.Count;
            resetAt = window.ResetAt;
        }

        var remaining = Math.Max(0, options.MaxRequests - count);
        context.Response.Headers["RateLimit-Limit"] = options.MaxRequests.ToString();
        context.Response.Headers["RateLimit-Remaining"] = remaining.ToString();
        context.Response.Headers["RateLimit-Reset"] = resetAt.ToUnixTimeSeconds().ToString();

        if (count > options.MaxRequests)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new ErrorEnvelope("Too many requests, please try again later"));
            return;
        }

        await _next(context);
    }

    private sealed class Window
    {
        public DateTimeOffset ResetAt;
        public int Count;
    }
}

public static class RateLimitingMiddlewareExtensions
{
    public static IApplicationBuilder UseApiRateLimiting(this IApplicationBuilder app)
        => app.UseMiddleware<RateLimitingMiddleware>();
}
