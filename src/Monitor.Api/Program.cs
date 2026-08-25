using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Monitor.Api.Middleware;
using Monitor.Core.Errors;
using Monitor.Core.Logging;
using Monitor.Core.Options;
using Monitor.Data.DependencyInjection;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog bootstrap (utils/logger.ts) ────────────────────────────────────
Log.Logger = SerilogBootstrap
    .Configure(new LoggerConfiguration(), builder.Environment.IsDevelopment())
    .CreateLogger();
builder.Host.UseSerilog();

// ── process.on('uncaughtException') / process.on('unhandledRejection')
//    (server.ts) — log at the same "fatal, about to die" severity the
//    source used before calling process.exit(1). The generic host already
//    tears the process down on an unhandled exception; this only adds the
//    logging side of the source's handlers. ──────────────────────────────
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Fatal(e.ExceptionObject as Exception, "Uncaught exception");
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Error(e.Exception, "Unhandled rejection");
    e.SetObserved();
};

// ── Listen port (config.port / PORT) — read straight off IConfiguration
//    before the host is built so it can drive Kestrel's bound address,
//    same as the source's `app.listen(config.port)`. The validated
//    AppOptions registered below is still the source of truth for the
//    rest of the app; this is just early enough to configure the listener.
var appPort = builder.Configuration.GetValue($"{AppOptions.SectionName}:Port", 4000);
builder.WebHost.UseUrls($"http://0.0.0.0:{appPort}");

// ── express.json({ limit: '10mb' }) / express.urlencoded(...) (app.ts) —
//    body-size ceiling lives on Kestrel in ASP.NET Core; model binding
//    handles the parsing itself. ──────────────────────────────────────────
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10mb
});

// ── Graceful shutdown forced-exit timeout (server.ts's 10s setTimeout after
//    SIGTERM/SIGINT) — the generic host's own shutdown budget replaces the
//    hand-rolled timer + process.exit(1). ──────────────────────────────────
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

// ── app.use(cors(...)) / app.use(compression()) (app.ts) ──────────────────
builder.Services.AddCors();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

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

// ── Primary + operational NpgsqlDataSources, Dapper repositories, and the
//    fail-fast startup connectivity check (infrastructure/database/connection.ts) ──
builder.Services.AddMonitorData();

// Bounded-context service registrations (Monitor.Identity / Files / Operations)
// are added by their own slices via extension methods on
// builder.Services — intentionally not referenced here yet.

var app = builder.Build();

// ── Terminal exception-handling middleware (middleware/error.middleware.ts:errorHandler) ──
// Registered first so it wraps every middleware/endpoint below it.
app.UseAppExceptionHandling();

// ── app.use(helmet()) (app.ts) ─────────────────────────────────────────────
app.UseSecurityHeaders();

// ── app.use(cors({ origin: config.cors.origin, credentials: true })) (app.ts) ──
var corsOrigin = app.Services.GetRequiredService<IOptions<CorsOptions>>().Value.Origin;
app.UseCors(policy => policy
    .WithOrigins(corsOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials());

// ── app.use(compression()) (app.ts) ────────────────────────────────────────
app.UseResponseCompression();

// ── morgan('combined', { stream: { write: (m) => logger.http(m) } }) (app.ts) ──
app.UseSerilogRequestLogging();

// ── const limiter = rateLimit(...); app.use('/api', limiter) (app.ts) ─────
app.UseApiRateLimiting();

// ── app.get('/health', ...) (app.ts) ───────────────────────────────────────
app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
    version = "1.0.0",
}));

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

// ── SIGTERM/SIGINT graceful shutdown (server.ts's shutdown() handler) —
//    IHostApplicationLifetime + HostOptions.ShutdownTimeout (configured
//    above) replace the hand-rolled process.on(...) + setTimeout handler. ──
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
    Log.Information("Received shutdown signal, shutting down gracefully"));
lifetime.ApplicationStopped.Register(() =>
    Log.Information("Server closed"));

app.Run();
