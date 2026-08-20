using Monitor.Api.Middleware;
using Monitor.Core.Errors;
using Monitor.Core.Logging;
using Monitor.Core.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog bootstrap (utils/logger.ts) ────────────────────────────────────
Log.Logger = SerilogBootstrap
    .Configure(new LoggerConfiguration(), builder.Environment.IsDevelopment())
    .CreateLogger();
builder.Host.UseSerilog();

// ── Validated options (config/index.ts) — fail fast at boot if a required
//    value is missing/invalid, instead of throwing the first time a route
//    touches config.* ─────────────────────────────────────────────────────
builder.Services.AddValidatedOptions<AppOptions>(builder.Configuration, AppOptions.SectionName);
builder.Services.AddValidatedOptions<DatabaseOptions>(builder.Configuration, DatabaseOptions.SectionName);
builder.Services.AddValidatedOptions<AzureAdOptions>(builder.Configuration, AzureAdOptions.SectionName);
builder.Services.AddValidatedOptions<AwsOptions>(builder.Configuration, AwsOptions.SectionName);
builder.Services.AddValidatedOptions<JwtOptions>(builder.Configuration, JwtOptions.SectionName);
builder.Services.AddValidatedOptions<CorsOptions>(builder.Configuration, CorsOptions.SectionName);
builder.Services.AddValidatedOptions<RateLimitOptions>(builder.Configuration, RateLimitOptions.SectionName);
builder.Services.AddValidatedOptions<WebhookOptions>(builder.Configuration, WebhookOptions.SectionName);

// Bounded-context service registrations (Monitor.Identity / Files / Operations
// / Data) are added by their own slices via extension methods on
// builder.Services — intentionally not referenced here yet.

var app = builder.Build();

// ── Terminal exception-handling middleware (middleware/error.middleware.ts:errorHandler) ──
app.UseAppExceptionHandling();

// Feature endpoint modules (AuthEndpoints, FilesEndpoints, MonitorEndpoints, ...)
// are mapped here by their own slices, e.g. `app.MapAuthEndpoints();`.

// ── notFoundHandler equivalent — fallback for anything no endpoint matched ──
app.MapFallback(async context =>
{
    context.Response.ContentType = "application/json";
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    var message = $"Route {context.Request.Method} {context.Request.Path} not found";
    await context.Response.WriteAsJsonAsync(new ErrorEnvelope(message));
});

app.Run();
