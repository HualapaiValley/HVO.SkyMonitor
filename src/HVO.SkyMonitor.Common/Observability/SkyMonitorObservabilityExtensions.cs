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
        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
        {
            app.MapHealthChecks(HealthEndpointPath);
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("live")
            });
        }

        return app;
    }

    private static bool HasOtlpEndpointConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    private static bool IsHealthRequest(PathString path)
    {
        return path.StartsWithSegments(HealthEndpointPath) || path.StartsWithSegments(AlivenessEndpointPath);
    }
}
