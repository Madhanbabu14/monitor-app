using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Monitor.Api.Middleware;
using Monitor.Core.Errors;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Monitor.UnitTests.Middleware;

public class ExceptionHandlingMiddlewareTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (ILogger logger, CollectingSink sink) MakeTestLogger()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private static DefaultHttpContext MakeContext(string method = "GET", string path = "/api/whatever")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Minimal IHttpResponseFeature whose HasStarted is hardcoded true, standing in for
    /// "headers already sent by a real transport" without needing a live Kestrel connection.
    /// </summary>
    private sealed class AlreadyStartedResponseFeature : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    [Fact]
    public async Task InvokeAsync_NoException_PassesThroughUntouched()
    {
        var context = MakeContext();
        var (logger, sink) = MakeTestLogger();
        var middleware = new ExceptionHandlingMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode); // default, untouched
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task InvokeAsync_AppException_WritesStatusCodeAndLegacyEnvelope()
    {
        var context = MakeContext();
        var (logger, sink) = MakeTestLogger();
        RequestDelegate next = _ => throw new AppException(401, "Invalid email or password");
        var middleware = new ExceptionHandlingMiddleware(next, logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);

        var body = await ReadBodyAsync(context);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Invalid email or password", doc.RootElement.GetProperty("message").GetString());

        // AppException path never logs through the unhandled-error branch.
        Assert.Empty(sink.Events);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public async Task InvokeAsync_AppException_PreservesArbitraryStatusCode(int statusCode)
    {
        var context = MakeContext();
        var (logger, _) = MakeTestLogger();
        RequestDelegate next = _ => throw new AppException(statusCode, "some message");
        var middleware = new ExceptionHandlingMiddleware(next, logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(statusCode, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_Returns500WithGenericMessage_NeverLeaksDetails()
    {
        var context = MakeContext();
        var (logger, sink) = MakeTestLogger();
        RequestDelegate next = _ => throw new InvalidOperationException("some internal secret detail");
        var middleware = new ExceptionHandlingMiddleware(next, logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);

        var body = await ReadBodyAsync(context);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("An unexpected error occurred", doc.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("some internal secret detail", body);
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_LogsErrorWithCorrelationPathAndMethod()
    {
        var context = MakeContext(method: "POST", path: "/api/monitor/status");
        var (logger, sink) = MakeTestLogger();
        var thrown = new InvalidOperationException("boom");
        RequestDelegate next = _ => throw thrown;
        var middleware = new ExceptionHandlingMiddleware(next, logger);

        await middleware.InvokeAsync(context);

        var evt = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Error, evt.Level);
        Assert.Equal("Unhandled error", evt.MessageTemplate.Text);
        Assert.Same(thrown, evt.Exception);

        Assert.True(evt.Properties.ContainsKey("CorrelationId"));
        Assert.Equal($"\"{context.TraceIdentifier}\"", evt.Properties["CorrelationId"].ToString());
        Assert.Equal("\"/api/monitor/status\"", evt.Properties["Path"].ToString());
        Assert.Equal("\"POST\"", evt.Properties["Method"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_AfterResponseStarted_LogsThenRethrows()
    {
        var context = MakeContext();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new AlreadyStartedResponseFeature());
        var (logger, sink) = MakeTestLogger();
        var thrown = new InvalidOperationException("too late");
        RequestDelegate next = _ => throw thrown;
        var middleware = new ExceptionHandlingMiddleware(next, logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.Same(thrown, ex);
        // It still logs before deciding it cannot write a fresh envelope.
        Assert.Single(sink.Events);
    }

    [Fact]
    public void Constructor_NullLogger_FallsBackToStaticLogLogger()
    {
        // Should not throw even though nothing configured Log.Logger for this test run;
        // Serilog's default Log.Logger is a safe no-op SilentLogger.
        var middleware = new ExceptionHandlingMiddleware(_ => Task.CompletedTask);

        Assert.NotNull(middleware);
    }

    [Fact]
    public async Task UseAppExceptionHandling_RegistersMiddlewareInPipeline()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var builder = new ApplicationBuilder(provider);

        builder.UseAppExceptionHandling();
        var pipeline = builder.Build();

        var context = MakeContext();
        // No exception thrown downstream (pipeline terminates with a 404 default handler);
        // this proves UseAppExceptionHandling wired a RequestDelegate without throwing.
        await pipeline(context);

        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task UseAppExceptionHandling_CatchesDownstreamAppException()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var builder = new ApplicationBuilder(provider);

        builder.UseAppExceptionHandling();
        builder.Run(_ => throw new AppException(422, "Unprocessable"));
        var pipeline = builder.Build();

        var context = MakeContext();
        await pipeline(context);

        Assert.Equal(422, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("Unprocessable", body);
    }
}
