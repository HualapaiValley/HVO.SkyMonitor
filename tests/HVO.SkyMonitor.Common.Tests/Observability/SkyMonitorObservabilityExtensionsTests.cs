using System.Globalization;
using HVO.SkyMonitor.Common.Observability;
using HVO.Enterprise.Telemetry.Configuration;
using HVO.Enterprise.Telemetry.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
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

    [TestMethod]
    [TestCategory("Unit")]
    public void AddSkyMonitorObservability_RegistersReplayRunnerMeter()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddSkyMonitorObservability();
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<OtlpExportOptions>>().Value;

        Assert.IsTrue(options.AdditionalMeterNames.Contains(
            "HVO.SkyMonitor.CameraAgent.ReplayRunner",
            StringComparer.Ordinal));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddSkyMonitorObservability_ReportsTheBuildVersionInTelemetryAndTheResource()
    {
        const string buildVersion = "0.0.0-dev+e5eb865e66b0e7343653b210f5841e029e99cf54";
        using var host = BuildHost(buildVersion, configuredServiceVersion: null);

        Assert.AreEqual(buildVersion, host.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value.ServiceVersion);
        Assert.AreEqual(buildVersion, ResourceServiceVersion(host.Services.GetRequiredService<TracerProvider>()));
        Assert.AreEqual(buildVersion, ResourceServiceVersion(host.Services.GetRequiredService<MeterProvider>()));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddSkyMonitorObservability_ConfiguredServiceVersionTakesPrecedence()
    {
        using var host = BuildHost("0.0.0-dev+unknown", configuredServiceVersion: "2.0.0-site");

        Assert.AreEqual("2.0.0-site", host.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value.ServiceVersion);
        Assert.AreEqual("2.0.0-site", ResourceServiceVersion(host.Services.GetRequiredService<TracerProvider>()));
        Assert.AreEqual("2.0.0-site", ResourceServiceVersion(host.Services.GetRequiredService<MeterProvider>()));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddSkyMonitorObservability_WithoutAVersionLeavesTheServiceVersionUnset()
    {
        using var host = BuildHost(buildVersion: " ", configuredServiceVersion: null);

        Assert.IsNull(host.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value.ServiceVersion);
        Assert.IsNull(ResourceServiceVersion(host.Services.GetRequiredService<TracerProvider>()));
    }

    private static IHost BuildHost(string? buildVersion, string? configuredServiceVersion)
    {
        var builder = Host.CreateApplicationBuilder();
        if (configuredServiceVersion is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:ServiceVersion"] = configuredServiceVersion
            });
        }
        builder.AddSkyMonitorObservability(buildVersion);
        // The hosts register their own trace and metric providers (the CameraAgent Prometheus endpoint, for example);
        // the shared resource configuration applies to whichever providers exist.
        builder.Services.AddOpenTelemetry().WithTracing(static _ => { }).WithMetrics(static _ => { });
        return builder.Build();
    }

    private static string? ResourceServiceVersion(BaseProvider provider)
        => provider.GetResource().Attributes
            .Where(static attribute => attribute.Key == "service.version")
            .Select(static attribute => attribute.Value as string)
            .SingleOrDefault();

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
