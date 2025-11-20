using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
using OpenTelemetry.Resources;
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
    /// Well-known infrastructure ports that should be excluded from telemetry.
    /// Includes MinIO console (9001), typical admin/monitoring interfaces.
    /// </summary>
    private static readonly HashSet<int> InfrastructureConsolePorts = [9001];

    private static readonly Dictionary<string, string> PeerServiceMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logichost"] = "LogicHost",
        ["minio"] = "Minio"
    };

    /// <summary>
    /// Configures OpenTelemetry logging, metrics, tracing, and optional OTLP export if configured.
    /// </summary>
    public static IHostApplicationBuilder AddSkyMonitorObservability(this IHostApplicationBuilder builder, Action<IOpenTelemetryBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var openTelemetryBuilder = builder.Services.AddOpenTelemetry();

        openTelemetryBuilder.ConfigureResource(resourceBuilder =>
        {
            var serviceName = ResolveServiceName(builder);
            var serviceInstanceId = ResolveServiceInstanceId(builder);
            var serviceVersion = ResolveServiceVersion();

            resourceBuilder.AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion,
                serviceInstanceId: serviceInstanceId);

            resourceBuilder.AddAttributes([
                new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)
            ]);
        });

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
                    .AddSource(DependencyTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context => !IsHealthRequest(context.Request.Path);
                    })
                        .AddHttpClientInstrumentation(options =>
                        {
                            options.FilterHttpRequestMessage = ShouldCaptureDependency;
                            options.EnrichWithHttpRequestMessage = EnrichDependencyWithServiceMetadata;
                        })
                    .AddProcessor(new HealthActivityFilterProcessor());
            });

        configure?.Invoke(openTelemetryBuilder);

        if (builder.Configuration.GetValue("Observability:AzureMonitor:EnableDiagnostics", false))
        {
            builder.Services.AddHostedService<AzureMonitorExporterDiagnosticsService>();
        }

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

    private static bool IsHealthRequest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return IsHealthRequest(new PathString(path));
    }

    private static bool ShouldCaptureDependency(HttpRequestMessage request)
    {
        if (request.RequestUri is null)
        {
            return true;
        }

        var path = request.RequestUri.AbsolutePath;
        if (IsHealthRequest(path))
        {
            return false;
        }

        if (IsMinioHealthProbe(request.RequestUri))
        {
            return false;
        }

        return true;
    }

    private static void EnrichDependencyWithServiceMetadata(Activity activity, HttpRequestMessage request)
    {
        if (activity is null || request.RequestUri is null)
        {
            return;
        }

        if (PeerServiceMappings.TryGetValue(request.RequestUri.Host, out var peerService))
        {
            activity.SetTag("peer.service", peerService);
        }
    }

    private static bool IsMinioHealthProbe(Uri requestUri)
    {
        var path = requestUri.AbsolutePath;

        // Filter MinIO health endpoints regardless of hostname/IP
        if (path.StartsWith("/minio/health", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Filter infrastructure console/admin ports (MinIO console, monitoring UIs, etc.)
        if (InfrastructureConsolePorts.Contains(requestUri.Port))
        {
            return true;
        }

        // Filter based on known MinIO hostnames
        var host = requestUri.Host;
        if (host.Equals("minio", StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith("/minio/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string ResolveServiceName(IHostApplicationBuilder builder)
    {
        return builder.Configuration["Observability:Service:Name"]
               ?? builder.Environment.ApplicationName
               ?? "unknown_service";
    }

    private static string ResolveServiceInstanceId(IHostApplicationBuilder builder)
    {
        return builder.Configuration["Observability:Service:InstanceId"]
               ?? Environment.GetEnvironmentVariable("HOSTNAME")
               ?? Environment.MachineName
               ?? Guid.NewGuid().ToString("N");
    }

    private static string? ResolveServiceVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly?.GetName().Version?.ToString();
    }

    private static bool ShouldExportActivity(Activity activity)
    {
        if (activity is null)
        {
            return false;
        }

        if (activity.Kind == ActivityKind.Server || activity.Kind == ActivityKind.Consumer)
        {
            var route = activity.GetTagItem("http.route") as string;
            var target = activity.GetTagItem("http.target") as string;
            if (IsHealthRequest(route) || IsHealthRequest(target))
            {
                return false;
            }
        }

        if (activity.Kind == ActivityKind.Client || activity.Kind == ActivityKind.Producer)
        {
            var url = activity.GetTagItem("http.url") as string;
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (IsHealthRequest(uri.AbsolutePath) || IsMinioHealthProbe(uri))
                {
                    return false;
                }
            }

            var target = activity.GetTagItem("http.target") as string;
            if (IsHealthRequest(target))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class HealthActivityFilterProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            if (ShouldExportActivity(data))
            {
                base.OnEnd(data);
            }
        }
    }
}
