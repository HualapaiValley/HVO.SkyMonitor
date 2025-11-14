using Microsoft.AspNetCore.Http;

namespace HVO.SkyMonitor.Common.Infrastructure.Diagnostics;

public sealed class HttpContextCorrelationIdAccessor : ICorrelationIdAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCorrelationIdAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? GetCorrelationId()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        return CorrelationIdMiddleware.GetCorrelationId(httpContext);
    }
}
