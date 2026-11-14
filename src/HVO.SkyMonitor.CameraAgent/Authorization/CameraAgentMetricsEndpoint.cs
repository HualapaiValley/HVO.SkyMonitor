using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenTelemetry.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Authorization;

/// <summary>
/// Prometheus scraping at <see cref="Path"/>. The response is operational data, so it uses the same
/// ready-owner operations-read policy as the operations API. Callers receive 401 or 403 directly:
/// a login redirect would hide that status from a scraper and could copy the request query into the URL.
/// </summary>
internal static class CameraAgentMetricsEndpoint
{
    internal const string Path = "/metrics";

    internal static bool IsScrapePath(PathString path)
        => string.Equals(path.Value, Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>API routes and the metrics scrape answer with status codes instead of browser redirects.</summary>
    internal static bool PrefersAuthenticationStatusCode(PathString path)
        => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || IsScrapePath(path);

    internal static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var mapped = endpoints.MapPrometheusScrapingEndpoint(Path)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        if (mapped is RouteHandlerBuilder route)
        {
            route.AddEndpointFilter(static (context, next) =>
            {
                context.HttpContext.Response.OnStarting(() =>
                {
                    context.HttpContext.Response.Headers.CacheControl = "no-store";
                    return Task.CompletedTask;
                });
                return next(context);
            });
        }

        return mapped;
    }
}
