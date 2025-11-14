using Microsoft.AspNetCore.Http;

namespace HVO.SkyMonitor.Common.Infrastructure.Diagnostics;

/// <summary>
/// Accesses the correlation ID from the current HTTP context.
/// </summary>
public class HttpContextCorrelationIdAccessor : ICorrelationIdAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCorrelationIdAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? CorrelationId =>
        CorrelationIdMiddleware.GetCorrelationId(_httpContextAccessor.HttpContext);
}
