namespace Monitor.Api.Middleware;

/// <summary>
/// .NET 6 analogue of the source's <c>app.use(helmet())</c> (app.ts). Helmet
/// itself has no .NET port, so this hand-rolls the same set of response
/// headers helmet applies with its default configuration (helmet v6/v7,
/// no options passed) — the exact set the source relies on.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers["Content-Security-Policy"] =
                "default-src 'self';base-uri 'self';font-src 'self' https: data:;" +
                "form-action 'self';frame-ancestors 'self';img-src 'self' data:;" +
                "object-src 'none';script-src 'self';script-src-attr 'none';" +
                "style-src 'self' https: 'unsafe-inline';upgrade-insecure-requests";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Origin-Agent-Cluster"] = "?1";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-DNS-Prefetch-Control"] = "off";
            headers["X-Download-Options"] = "noopen";
            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["X-Permitted-Cross-Domain-Policies"] = "none";
            headers["X-XSS-Protection"] = "0";

            return Task.CompletedTask;
        });

        return _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
