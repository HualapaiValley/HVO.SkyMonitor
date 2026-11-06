using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Google.Protobuf;
using HVO.SkyMonitor.Common.Observability;
using HVO.Enterprise.Telemetry.Configuration;
using HVO.Enterprise.Telemetry.OpenTelemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
    private const string BuildVersion = "0.0.0-dev+e5eb865e66b0e7343653b210f5841e029e99cf54";

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

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(null, BuildVersion, BuildVersion, DisplayName = "The build version replaces the environment's")]
    [DataRow("2.0.0-site", BuildVersion, "2.0.0-site", DisplayName = "A configured version replaces the environment's")]
    [DataRow(null, null, "stale", DisplayName = "Without a version the environment's is kept")]
    public async Task AddSkyMonitorObservability_ExportsTheResolvedVersionOnTheLogResource(
        string? configuredServiceVersion,
        string? buildVersion,
        string expectedServiceVersion)
    {
        // The log sink applies the OTLP environment variables after its options, so a stale service.version there must
        // not replace the version that startup, traces and metrics report. The other variables still apply.
        using var collector = new OtlpLogCollector();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Endpoint,
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-hvo-collector=site",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.version=stale,deployment.environment=site",
            ["OTEL_SERVICE_NAME"] = "hvo-camera"
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Nothing listens here, so an export reaching the collector shows that the environment endpoint still applies.
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://127.0.0.1:{FreeLoopbackPort()}",
            ["Telemetry:ServiceVersion"] = configuredServiceVersion
        });
        SkyMonitorObservabilityExtensions.AddSkyMonitorObservability(
            builder, buildVersion, name => environment.GetValueOrDefault(name));
        using (var host = builder.Build())
        {
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HVO.SkyMonitor.Tests")
                .Log(LogLevel.Information, default, "Exported", null, static (state, _) => state);
        }

        // Disposing the host flushes the batched export.
        var (body, header) = await collector.Export.WaitAsync(TimeSpan.FromSeconds(30));
        var resource = ReadLogResourceAttributes(body);

        Assert.AreEqual(expectedServiceVersion, resource["service.version"]);
        Assert.AreEqual("site", resource["deployment.environment"]);
        Assert.AreEqual("hvo-camera", resource["service.name"]);
        Assert.AreEqual("site", header);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task WriteDetailedHealthResponse_ProjectsOnlyTheAllowedDataOfAKnownCheck()
    {
        // The space-weather evaluator emits only the two allowed keys, so the extra key shows that the writer filters
        // rather than passes data through, and the unregistered check shows that unknown data is withheld.
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
            {
                ["space-weather"] = new(
                    HealthStatus.Healthy,
                    "Space weather refresh is disabled.",
                    TimeSpan.Zero,
                    null,
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["Availability"] = "Disabled",
                        ["Reason"] = "space-weather.disabled",
                        ["Unlisted"] = "withheld"
                    }),
                ["unregistered"] = new(
                    HealthStatus.Healthy,
                    null,
                    TimeSpan.Zero,
                    null,
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["Availability"] = "Disabled" })
            },
            TimeSpan.Zero);
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await SkyMonitorObservabilityExtensions.WriteDetailedHealthResponse(context, report);

        using var document = JsonDocument.Parse(body.ToArray());
        var checks = document.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(static check => check.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var data = checks["space-weather"].GetProperty("data");
        Assert.AreEqual("Disabled", data.GetProperty("Availability").GetString());
        Assert.AreEqual("space-weather.disabled", data.GetProperty("Reason").GetString());
        Assert.AreEqual(2, data.EnumerateObject().Count());
        Assert.AreEqual(JsonValueKind.Null, checks["unregistered"].GetProperty("data").ValueKind);
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

    /// <summary>
    /// Reads the string attributes of the first resource in an OTLP <c>ExportLogsServiceRequest</c>. Field 1 of the
    /// request holds the resource logs, their field 1 the resource, and its field 1 the attributes. Each attribute is
    /// a key (1) and a value (2) whose field 1 holds a string value.
    /// </summary>
    private static Dictionary<string, string> ReadLogResourceAttributes(byte[] request)
    {
        var resourceLogs = LengthDelimitedFields(ByteString.CopyFrom(request), 1)[0];
        var resource = LengthDelimitedFields(resourceLogs, 1)[0];
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in LengthDelimitedFields(resource, 1))
        {
            var stringValue = LengthDelimitedFields(LengthDelimitedFields(attribute, 2)[0], 1);
            if (stringValue.Count == 1)
            {
                attributes[LengthDelimitedFields(attribute, 1)[0].ToStringUtf8()] = stringValue[0].ToStringUtf8();
            }
        }
        return attributes;
    }

    private static List<ByteString> LengthDelimitedFields(ByteString message, int fieldNumber)
    {
        var input = message.CreateCodedInput();
        var fields = new List<ByteString>();
        for (var tag = input.ReadTag(); tag != 0; tag = input.ReadTag())
        {
            if (WireFormat.GetTagFieldNumber(tag) == fieldNumber &&
                WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                fields.Add(input.ReadBytes());
            }
            else
            {
                input.SkipLastField();
            }
        }
        return fields;
    }

    private static int FreeLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    /// <summary>Accepts OTLP/HTTP log exports on a loopback port and keeps the first one.</summary>
    private sealed class OtlpLogCollector : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly TaskCompletionSource<(byte[] Body, string? Header)> export =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OtlpLogCollector()
        {
            Endpoint = $"http://127.0.0.1:{FreeLoopbackPort()}";
            listener.Prefixes.Add(Endpoint + "/");
            listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public string Endpoint { get; }

        public Task<(byte[] Body, string? Header)> Export => export.Task;

        public void Dispose() => listener.Close();

        private async Task AcceptAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (context.Request.Url!.AbsolutePath == "/v1/logs")
                {
                    using var body = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(body);
                    export.TrySetResult((body.ToArray(), context.Request.Headers["x-hvo-collector"]));
                }
                context.Response.Close();
            }
        }
    }
}
