using Microsoft.AspNetCore.Http;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Infrastructure.Diagnostics;

public interface ICorrelationIdAccessor
{
    string? GetCorrelationId();
}

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
