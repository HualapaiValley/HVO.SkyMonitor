using System.Linq;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using HVO.Enterprise.Telemetry;
using HVO.Enterprise.Telemetry.OpenTelemetry;
using HVO.Enterprise.Telemetry.Serilog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

namespace HVO.SkyMonitor.Common.Observability;

/// <summary>
/// Provides shared observability helpers so we can remove Aspire service defaults.
/// </summary>
public static class SkyMonitorObservabilityExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private static readonly HashSet<string> CatalogHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Kind", "CatalogId", "CatalogIdentitySource", "CatalogVersion", "SchemaVersion", "PreprocessingVersion", "RowCount"
    };
    private static readonly HashSet<string> RawIngressHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Availability", "PendingCount", "PendingBytes", "QuarantineCount", "QuarantineBytes"
    };
    private static readonly HashSet<string> CaptureLaneHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Availability", "LaneCount", "PendingCount", "PendingBytes", "LeasedCount", "QuarantineCount"
    };
    private static readonly HashSet<string> CaptureProcessingHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Availability", "PendingCount", "RetryCount", "TerminalCount", "OldestPendingAgeSeconds", "Reason"
    };
    private static readonly HashSet<string> ArtifactOutboxHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Availability", "PendingCount", "PendingBytes", "LeasedCount", "RetryCount", "QuarantineCount", "OldestAgeSeconds"
    };
    private static readonly HashSet<string> ArtifactConsistencyHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Condition", "ReconciliationWindowSeconds"
    };
    private static readonly HashSet<string> ArtifactRetentionHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Condition", "PendingCount", "PendingBytes", "PendingOldestAgeSeconds"
    };
    private static readonly HashSet<string> CentralDerivativeWorkerHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Status", "ActiveSlots", "PendingCount", "OldestAgeSeconds", "LastSuccessAgeSeconds"
    };
    private static readonly HashSet<string> ObjectStoreHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Reason"
    };
    private static readonly HashSet<string> EnvironmentalObservationHealthDataKeys = new(StringComparer.Ordinal)
    {
        "SourceCount", "ObservationCount", "NewestReceivedAgeSeconds", "RetentionEligibleCount",
        "RetentionOldestAgeSeconds", "RetentionLastSucceededUtc"
    };
    private static readonly HashSet<string> DeploymentLocationHealthDataKeys = new(StringComparer.Ordinal)
    {
        "PendingCount", "OldestAgeSeconds", "PendingWorkCount", "PendingCaptureCount",
        "OldestWorkAgeSeconds", "OldestWorkProgressAgeSeconds", "UndiscoveredWorkCount", "RetryCount",
        "ExpiredLeaseCount"
    };
    private static readonly HashSet<string> EnvironmentalDeliveryHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Availability", "PendingCount", "PendingBytes", "LeasedCount", "RetryCount", "QuarantineCount",
        "TerminalCount", "OverflowCount", "OldestAgeSeconds"
    };
    private static readonly HashSet<string> EnvironmentalAcquisitionHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Availability", "ConfiguredSourceCount", "RequiredSourceCount", "InitializedSourceCount",
        "FreshSourceCount", "StaleSourceCount", "MissingSourceCount", "FailingSourceCount",
        "OverduePollCount", "MaximumConsecutiveFailures", "StoredCount", "StoredBytes"
    };
    private static readonly HashSet<string> CalibrationLibraryHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Required", "Active", "ActiveState", "StateVersion", "PendingAcquisitionState",
        "LastSelectionReason", "LastSelectionUtc", "BundleCount", "QuarantineCount",
        "LastActivationResult", "LastActivationUtc", "LastReconciliationResult", "LastReconciliationUtc"
    };

    /// <summary>
    /// Configures the shared HVO telemetry stack and OTLP export.
    /// </summary>
    public static IHostApplicationBuilder AddSkyMonitorObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigureSerilog(builder);
        builder.Services.AddTelemetry(builder.Configuration.GetSection("Telemetry"));
        builder.Services.AddOpenTelemetryExport(options =>
        {
            // Preserve OTLP/HTTP signal paths using the collector's standard SDK environment settings.
            options.EnableTraceExport = false;
            options.EnableMetricsExport = false;
            options.EnableLogExport = false;
            options.EnableStandardMeters = true;
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.Authentication");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.Capture");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.CaptureControl");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.RawIngress");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.CaptureLanes");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.DeploymentLocation");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.ProcessingGraph");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.ReplayRunner");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.Outbox");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.LogicHost.Ingest");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.LogicHost.Retrieval");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.LogicHost.DerivativeWorker");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.LogicHost.EnvironmentalObservations");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.LogicHost.DeploymentLocation");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.EnvironmentalDelivery");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.Transients");
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.Calibration");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.RawIngress");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.CaptureControl");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.CaptureLanes");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.DeploymentLocation");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.ProcessingGraph");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.Outbox");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.LogicHost");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.LogicHost.DerivativeWorker");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.LogicHost.EnvironmentalObservations");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.LogicHost.DeploymentLocation");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.EnvironmentalDelivery");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.Transients");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.Calibration");
            options.AdditionalActivitySources.Add(builder.Environment.ApplicationName);
        });

        if (HasOtlpEndpointConfigured(builder.Configuration))
        {
            builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddOtlpExporter())
                .WithMetrics(metrics => metrics
                    .AddMeter("HVO.SkyMonitor.Authentication")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.Capture")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.CaptureControl")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.RawIngress")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.CaptureLanes")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.DeploymentLocation")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.ProcessingGraph")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.ReplayRunner")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.Outbox")
                    .AddMeter("HVO.SkyMonitor.LogicHost.Ingest")
                    .AddMeter("HVO.SkyMonitor.LogicHost.Retrieval")
                    .AddMeter("HVO.SkyMonitor.LogicHost.DerivativeWorker")
                    .AddMeter("HVO.SkyMonitor.LogicHost.EnvironmentalObservations")
                    .AddMeter("HVO.SkyMonitor.LogicHost.DeploymentLocation")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.EnvironmentalDelivery")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.Transients")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.Calibration")
                    .AddOtlpExporter());
        }

        return builder;
    }

    /// <summary>
    /// Adds the default SkyMonitor liveness health check and returns the builder for further customization.
    /// </summary>
    public static IHealthChecksBuilder AddSkyMonitorHealthChecks(this IServiceCollection services)
    {
        return services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
    }

    /// <summary>
    /// Maps the default health endpoints when running in Development or Testing environments.
    /// </summary>
    public static WebApplication MapSkyMonitorHealthEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapHealthChecks(HealthEndpointPath, new HealthCheckOptions
        {
            ResponseWriter = WriteDetailedHealthResponse
        })
            .AllowAnonymous();

        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
            ResponseWriter = WriteLivenessResponse
        })
            .AllowAnonymous();

        return app;
    }

    private static Task WriteDetailedHealthResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            duration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                duration = entry.Value.Duration.TotalMilliseconds,
                error = entry.Value.Exception is null ? null : "Health check failed.",
                tags = entry.Value.Tags,
                data = SelectHealthData(entry.Key, entry.Value.Data)
            })
        };

        return context.Response.WriteAsJsonAsync(payload);
    }

    private static Dictionary<string, object>? SelectHealthData(
        string checkName,
        IReadOnlyDictionary<string, object> data)
    {
        var allowedKeys = checkName switch
        {
            "catalog" => CatalogHealthDataKeys,
            "raw-ingress" => RawIngressHealthDataKeys,
            "capture-lanes" => CaptureLaneHealthDataKeys,
            "capture-processing" => CaptureProcessingHealthDataKeys,
            "artifact-outbox" => ArtifactOutboxHealthDataKeys,
            "artifact-consistency" => ArtifactConsistencyHealthDataKeys,
            "artifact-retention" => ArtifactRetentionHealthDataKeys,
            "central-derivative-worker" => CentralDerivativeWorkerHealthDataKeys,
            "s3-object-store" => ObjectStoreHealthDataKeys,
            "environmental-observations" => EnvironmentalObservationHealthDataKeys,
            "environmental-delivery" => EnvironmentalDeliveryHealthDataKeys,
            "environmental-acquisition" => EnvironmentalAcquisitionHealthDataKeys,
            "deployment-location" => DeploymentLocationHealthDataKeys,
            "calibration-library" => CalibrationLibraryHealthDataKeys,
            _ => null
        };
        return allowedKeys is null
            ? null
            : data.Where(pair => allowedKeys.Contains(pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    }

    private static Task WriteLivenessResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new { status = report.Status.ToString() };
        return context.Response.WriteAsJsonAsync(payload);
    }

    private static bool HasOtlpEndpointConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The created Serilog root logger owns and disposes its registered telemetry sink.")]
    private static void ConfigureSerilog(IHostApplicationBuilder builder)
    {
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithTelemetry()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);

        const string cameraAgentCategory = "HVO.SkyMonitor.CameraAgent.Common";
        var configuredCategoryLevel = builder.Configuration[$"Logging:LogLevel:{cameraAgentCategory}"];
        if (Enum.TryParse<LogLevel>(configuredCategoryLevel, ignoreCase: true, out var categoryLevel))
        {
            ApplyCategoryLogLevel(loggerConfiguration, cameraAgentCategory, categoryLevel);
        }

        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? builder.Environment.ApplicationName;
            var telemetryLogger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = endpoint.TrimEnd('/') + "/v1/logs";
                    options.Protocol = OtlpProtocol.HttpProtobuf;
                    options.IncludedData = IncludedData.TraceIdField |
                        IncludedData.SpanIdField |
                        IncludedData.SourceContextAttribute;
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = serviceName
                    };
                })
                .CreateLogger();
            loggerConfiguration.WriteTo.Sink(new SanitizedOpenTelemetryLogSink(telemetryLogger));
        }

        Log.Logger = loggerConfiguration.CreateLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);
    }

    internal static void ApplyCategoryLogLevel(
        LoggerConfiguration loggerConfiguration,
        string category,
        LogLevel level)
    {
        if (level == LogLevel.None)
        {
            loggerConfiguration.Filter.ByExcluding(logEvent =>
                logEvent.Properties.TryGetValue("SourceContext", out var source) &&
                source is ScalarValue { Value: string value } &&
                (string.Equals(value, category, StringComparison.Ordinal) ||
                 value.StartsWith(category + ".", StringComparison.Ordinal)));
            return;
        }

        loggerConfiguration.MinimumLevel.Override(category, level switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        });
    }

    private static bool IsHealthRequest(PathString path)
    {
        return path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments(AlivenessEndpointPath, StringComparison.OrdinalIgnoreCase);
    }
}
