using Monitor.Core.Logging;
using Monitor.Core.Options;
using Monitor.Gateway.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog bootstrap, shared with Monitor.Api for consistent log shape
//    across every process in the solution (utils/logger.ts had no gateway
//    equivalent — this component is new, added purely for the strangler
//    migration strategy). ───────────────────────────────────────────────
Log.Logger = SerilogBootstrap
    .Configure(new LoggerConfiguration(), builder.Environment.IsDevelopment())
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddValidatedOptions<GatewayOptions>(builder.Configuration, GatewayOptions.SectionName);

// ── Listen port — this is the single origin the retained React/Vite
//    frontend's VITE_API_URL now points at, instead of hitting the legacy
//    Express service (or, post-cutover, Monitor.Api) directly. ───────────
var gatewayPort = builder.Configuration.GetValue($"{GatewayOptions.SectionName}:Port", 8080);
builder.WebHost.UseUrls($"http://0.0.0.0:{gatewayPort}");

// ── YARP: routes + clusters are entirely config-driven (see
//    appsettings*.json's "ReverseProxy" section). Today every route's
//    ClusterId is "legacy" because no bounded-context slice has mapped its
//    endpoints on Monitor.Api yet; as each one does, its route(s) flip to
//    the "dotnet" cluster — a config change, not a code change. ──────────
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseSerilogRequestLogging();

// ── The gateway is a transparent L7 proxy: it does not add its own CORS
//    policy or auth handling. YARP forwards the browser's Origin header
//    (and every other request header) straight through to whichever
//    cluster handles the route, so the destination's own CORS response
//    headers (Monitor.Api's `app.UseCors(...)` or the legacy Express
//    `cors()` middleware) flow back to the browser unmodified. Adding a
//    second, possibly-divergent CORS policy here would risk masking or
//    conflicting with the origin server's answer. ─────────────────────────

// Liveness probe for the gateway process itself — distinct from the
// proxied `/health`, which is forwarded downstream like any other route.
app.MapGet("/_gateway/health", () => Results.Json(new { status = "ok" }));

app.MapReverseProxy();

app.Run();

// Exposed for WebApplicationFactory-style integration tests.
public partial class Program { }
