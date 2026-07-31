using Microsoft.Net.Http.Headers;

namespace HVO.SkyMonitor.LogicHost.Middleware;

internal sealed class DynamicPageCachePolicyMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var isPublicNetworkPage = path == "/"
            || path.StartsWithSegments("/observatories", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/events", StringComparison.OrdinalIgnoreCase);
        var isProtectedPage = path.StartsWithSegments("/app", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/devices", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase);
        if (!isPublicNetworkPage && !isProtectedPage)
        {
            return next(context);
        }

        context.Response.OnStarting(() =>
        {
            var isPrivate = isProtectedPage
                || context.User.Identity?.IsAuthenticated == true
                || context.Request.Headers.ContainsKey(HeaderNames.Authorization)
                || context.Request.Headers.ContainsKey(HeaderNames.Cookie);
            context.Response.Headers[HeaderNames.CacheControl] = isPrivate
                ? "private, no-store"
                : "public, no-cache, must-revalidate";
            context.Response.Headers[HeaderNames.Pragma] = "no-cache";
            context.Response.Headers[HeaderNames.Vary] = isPrivate
                ? "Cookie, Authorization, X-API-Key"
                : "Cookie";
            return Task.CompletedTask;
        });
        return next(context);
    }
}
