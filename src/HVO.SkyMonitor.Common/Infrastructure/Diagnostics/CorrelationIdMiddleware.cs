using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HVO.SkyMonitor.Common.Infrastructure.Diagnostics;

/// <summary>
/// Middleware that ensures every request has a correlation ID for distributed tracing.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private const string ContextKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Get or generate correlation ID
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        // Store in context
        context.Items[ContextKey] = correlationId;

        // Add to response headers
        context.Response.Headers.Append(HeaderName, correlationId);

        await _next(context);
    }

    /// <summary>
    /// Gets the correlation ID from the HTTP context.
    /// </summary>
    public static string? GetCorrelationId(HttpContext? context)
    {
        return context?.Items[ContextKey] as string;
    }
}

/// <summary>
/// Extension methods for correlation ID middleware.
/// </summary>
public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CorrelationIdMiddleware>();
    }
}
