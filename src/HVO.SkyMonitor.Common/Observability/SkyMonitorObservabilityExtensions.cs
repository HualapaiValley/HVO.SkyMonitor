using System.Linq;
using System.Globalization;
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
        "Kind", "CatalogVersion", "SchemaVersion", "PreprocessingVersion", "DatabaseSha256", "RowCount"
    };
    private static readonly HashSet<string> RawIngressHealthDataKeys = new(StringComparer.Ordinal)
    {
        "Availability", "PendingCount", "PendingBytes", "QuarantineCount", "QuarantineBytes"
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
            options.AdditionalMeterNames.Add("HVO.SkyMonitor.CameraAgent.RawIngress");
            options.AdditionalActivitySources.Add("HVO.SkyMonitor.CameraAgent.RawIngress");
            options.AdditionalActivitySources.Add(builder.Environment.ApplicationName);
        });

        if (HasOtlpEndpointConfigured(builder.Configuration))
        {
            builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddOtlpExporter())
                .WithMetrics(metrics => metrics
                    .AddMeter("HVO.SkyMonitor.Authentication")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.Capture")
                    .AddMeter("HVO.SkyMonitor.CameraAgent.RawIngress")
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
                error = entry.Value.Exception?.Message,
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

    private static void ConfigureSerilog(IHostApplicationBuilder builder)
    {
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithTelemetry()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);

        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? builder.Environment.ApplicationName;
            loggerConfiguration.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = endpoint.TrimEnd('/') + "/v1/logs";
                options.Protocol = OtlpProtocol.HttpProtobuf;
                options.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = serviceName
                };
            });
        }

        Log.Logger = loggerConfiguration.CreateLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);
    }

    private static bool IsHealthRequest(PathString path)
    {
        return path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments(AlivenessEndpointPath, StringComparison.OrdinalIgnoreCase);
    }
}
