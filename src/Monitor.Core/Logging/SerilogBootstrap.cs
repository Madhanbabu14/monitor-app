using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Monitor.Core.Logging;

/// <summary>
/// Direct translation of utils/logger.ts (winston):
///   - level: debug in dev, info otherwise
///   - JSON-formatted file sinks: logs/error.log (Error+ only), logs/combined.log (everything)
///   - defaultMeta.service = "pipeline-control-center"
///   - a colorized, human-readable console sink, but ONLY in dev
/// Used by Monitor.Api's Program.cs via `Log.Logger = SerilogBootstrap.Configure(...)`.
/// </summary>
public static class SerilogBootstrap
{
    public const string ServiceName = "pipeline-control-center";

    public static LoggerConfiguration Configure(LoggerConfiguration configuration, bool isDevelopment)
    {
        configuration
            .MinimumLevel.Is(isDevelopment ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.WithProperty("service", ServiceName)
            .WriteTo.File(
                new CompactJsonFormatter(),
                path: "logs/combined.log")
            .WriteTo.File(
                new CompactJsonFormatter(),
                path: "logs/error.log",
                restrictedToMinimumLevel: LogEventLevel.Error);

        if (isDevelopment)
        {
            configuration.WriteTo.Console();
        }

        return configuration;
    }
}
