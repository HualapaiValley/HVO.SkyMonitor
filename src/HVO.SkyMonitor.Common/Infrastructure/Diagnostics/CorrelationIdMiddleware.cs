using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Common.Infrastructure.Diagnostics;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    internal const string CorrelationItemKey = "__CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = GetOrCreateCorrelationId(context);

        context.Items[CorrelationItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        if (Activity.Current is not null)
        {
            Activity.Current.SetTag("correlation.id", correlationId);
        }

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    public static string? GetCorrelationId(HttpContext? context)
    {
        if (context is null)
        {
            return null;
        }

        if (context.Items.TryGetValue(CorrelationItemKey, out var value) && value is string correlationId)
        {
            return correlationId;
        }

        return context.TraceIdentifier;
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var headerValue) && !string.IsNullOrWhiteSpace(headerValue))
        {
            return headerValue.ToString();
        }

        if (Activity.Current is not null && Activity.Current.TraceId != default)
        {
            return Activity.Current.TraceId.ToString();
        }

        return context.TraceIdentifier;
    }
}
