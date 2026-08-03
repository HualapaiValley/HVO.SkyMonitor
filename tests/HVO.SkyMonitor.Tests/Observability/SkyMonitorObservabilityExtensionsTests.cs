using System.Globalization;
using HVO.SkyMonitor.Common.Observability;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace HVO.SkyMonitor.Tests.Observability;

[TestClass]
public sealed class SkyMonitorObservabilityExtensionsTests
{
    private const string Category = "HVO.SkyMonitor.CameraAgent.Common";

    [TestMethod]
    [TestCategory("Unit")]
    public void ApplyCategoryLogLevel_NoneExcludesCategoryIncludingFatalEvents()
    {
        var sink = new CollectingSink();
        var configuration = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink);
        SkyMonitorObservabilityExtensions.ApplyCategoryLogLevel(configuration, Category, LogLevel.None);
        using var logger = configuration.CreateLogger();

        logger.ForContext("SourceContext", Category + ".Capture").Fatal("excluded");
        logger.ForContext("SourceContext", "HVO.SkyMonitor.Other").Information("retained");

        Assert.HasCount(1, sink.Events);
        Assert.AreEqual("retained", sink.Events[0].RenderMessage(CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ApplyCategoryLogLevel_DebugOverridesInformationDefault()
    {
        var sink = new CollectingSink();
        var configuration = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink);
        SkyMonitorObservabilityExtensions.ApplyCategoryLogLevel(configuration, Category, LogLevel.Debug);
        using var logger = configuration.CreateLogger();
        var categoryLogger = logger.ForContext("SourceContext", Category + ".Capture");

        categoryLogger.Verbose("excluded");
        categoryLogger.Debug("retained");

        Assert.HasCount(1, sink.Events);
        Assert.AreEqual(LogEventLevel.Debug, sink.Events[0].Level);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
