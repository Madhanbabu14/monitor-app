using Monitor.Core.Errors;
using Serilog;

namespace Monitor.Api.Middleware;

/// <summary>
/// Terminal exception-handling middleware — the .NET 6 analogue of
/// middleware/error.middleware.ts's `errorHandler` (IExceptionHandler is a
/// .NET 8 feature, so this is hand-rolled middleware for the strangler
/// window). Always emits the legacy envelope:
///   {"status":"error","message":"..."}
/// `AppException` -> its own StatusCode + message, verbatim (never a stack
/// trace). Anything else -> logged at Error with the correlation id, and a
/// generic 500 message is returned to the client.
/// `express-async-errors` has no analogue here: ASP.NET Core middleware
/// already awaits `next()`, so async exceptions from downstream endpoints
/// are caught here without any extra wiring.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Serilog.ILogger _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, Serilog.ILogger? logger = null)
    {
        _next = next;
        _logger = logger ?? Log.Logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException appEx)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = appEx.StatusCode;
            await context.Response.WriteAsJsonAsync(new ErrorEnvelope(appEx.Message));
        }
        catch (Exception ex)
        {
            var correlationId = context.TraceIdentifier;

            _logger
                .ForContext("CorrelationId", correlationId)
                .ForContext("Path", context.Request.Path.Value)
                .ForContext("Method", context.Request.Method)
                .Error(ex, "Unhandled error");

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ErrorEnvelope("An unexpected error occurred"));
        }
    }
}

public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseAppExceptionHandling(this IApplicationBuilder app)
        => app.UseMiddleware<ExceptionHandlingMiddleware>();
}
