using System.Linq;
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

namespace HVO.SkyMonitor.Common.Observability;

/// <summary>
/// Provides shared observability helpers so we can remove Aspire service defaults.
/// </summary>
public static class SkyMonitorObservabilityExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// Configures OpenTelemetry logging, metrics, tracing, and optional OTLP export if configured.
    /// </summary>
    public static IHostApplicationBuilder AddSkyMonitorObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var openTelemetryBuilder = builder.Services.AddOpenTelemetry();

        openTelemetryBuilder
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context => !IsHealthRequest(context.Request.Path);
                    })
                    .AddHttpClientInstrumentation();
            });

        if (HasOtlpEndpointConfigured(builder.Configuration))
        {
            openTelemetryBuilder.UseOtlpExporter();
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
                tags = entry.Value.Tags
            })
        };

        return context.Response.WriteAsJsonAsync(payload);
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

    private static bool IsHealthRequest(PathString path)
    {
        return path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments(AlivenessEndpointPath, StringComparison.OrdinalIgnoreCase);
    }
}
