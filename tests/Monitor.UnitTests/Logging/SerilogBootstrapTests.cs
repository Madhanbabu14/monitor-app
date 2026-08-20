using Monitor.Core.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Monitor.UnitTests.Logging;

/// <summary>
/// Serilog file sinks are wired unconditionally by Configure(), so these tests run in a
/// temp working directory and clean up the "logs/" folder they create, mirroring
/// utils/logger.ts's File transports without leaving artifacts behind in the repo.
/// </summary>
public sealed class SerilogBootstrapTests : IDisposable
{
    private readonly string _originalDirectory;
    private readonly string _tempDirectory;
    private readonly TextWriter _originalConsoleOut;

    public SerilogBootstrapTests()
    {
        _originalDirectory = Directory.GetCurrentDirectory();
        _tempDirectory = Path.Combine(Path.GetTempPath(), "monitor-serilog-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDirectory);
        Directory.SetCurrentDirectory(_tempDirectory);
        _originalConsoleOut = Console.Out;
    }

    public void Dispose()
    {
        Console.SetOut(_originalConsoleOut);
        Directory.SetCurrentDirectory(_originalDirectory);
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; file sinks may briefly hold a handle.
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public void Configure_Development_MinimumLevelIsDebug()
    {
        var sink = new CollectingSink();
        var config = SerilogBootstrap.Configure(new LoggerConfiguration(), isDevelopment: true)
            .WriteTo.Sink(sink);
        using var logger = config.CreateLogger();

        logger.Debug("debug message");
        logger.Information("info message");

        Assert.Equal(2, sink.Events.Count);
        Assert.Contains(sink.Events, e => e.Level == LogEventLevel.Debug);
    }

    [Fact]
    public void Configure_NonDevelopment_MinimumLevelIsInformation_DebugIsFilteredOut()
    {
        var sink = new CollectingSink();
        var config = SerilogBootstrap.Configure(new LoggerConfiguration(), isDevelopment: false)
            .WriteTo.Sink(sink);
        using var logger = config.CreateLogger();

        logger.Debug("should be filtered");
        logger.Information("should pass");

        Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Information, sink.Events[0].Level);
        Assert.Equal("should pass", sink.Events[0].MessageTemplate.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configure_EnrichesEveryEvent_WithServiceName(bool isDevelopment)
    {
        var sink = new CollectingSink();
        var config = SerilogBootstrap.Configure(new LoggerConfiguration(), isDevelopment)
            .WriteTo.Sink(sink);
        using var logger = config.CreateLogger();

        logger.Information("hello");

        var evt = Assert.Single(sink.Events);
        Assert.True(evt.Properties.ContainsKey("service"));
        var value = evt.Properties["service"].ToString().Trim('"');
        Assert.Equal("pipeline-control-center", value);
        Assert.Equal("pipeline-control-center", SerilogBootstrap.ServiceName);
    }

    [Fact]
    public void Configure_Development_WritesToConsole()
    {
        using var writer = new StringWriter();
        Console.SetOut(writer);

        using (var logger = SerilogBootstrap.Configure(new LoggerConfiguration(), isDevelopment: true).CreateLogger())
        {
            logger.Information("dev-console-marker-{Id}", 42);
        }

        Assert.Contains("dev-console-marker-42", writer.ToString());
    }

    [Fact]
    public void Configure_NonDevelopment_DoesNotWriteToConsole()
    {
        using var writer = new StringWriter();
        Console.SetOut(writer);

        using (var logger = SerilogBootstrap.Configure(new LoggerConfiguration(), isDevelopment: false).CreateLogger())
        {
            logger.Information("prod-console-marker-should-not-appear");
        }

        Assert.DoesNotContain("prod-console-marker-should-not-appear", writer.ToString());
    }

    [Fact]
    public void Configure_ReturnsSameConfigurationInstance_ForFluentChaining()
    {
        var original = new LoggerConfiguration();

        var result = SerilogBootstrap.Configure(original, isDevelopment: true);

        Assert.Same(original, result);
    }

    [Fact]
    public void Configure_CreatesLogFiles_ForCombinedAndErrorLogs()
    {
        using (var logger = SerilogBootstrap.Configure(new LoggerConfiguration(), isDevelopment: true).CreateLogger())
        {
            logger.Information("combined-only");
            logger.Error("goes-to-both-files");
        }

        Assert.True(File.Exists(Path.Combine(_tempDirectory, "logs", "combined.log")));
        Assert.True(File.Exists(Path.Combine(_tempDirectory, "logs", "error.log")));

        var errorLogContent = File.ReadAllText(Path.Combine(_tempDirectory, "logs", "error.log"));
        Assert.Contains("goes-to-both-files", errorLogContent);
        Assert.DoesNotContain("combined-only", errorLogContent);

        var combinedLogContent = File.ReadAllText(Path.Combine(_tempDirectory, "logs", "combined.log"));
        Assert.Contains("combined-only", combinedLogContent);
        Assert.Contains("goes-to-both-files", combinedLogContent);
    }
}
