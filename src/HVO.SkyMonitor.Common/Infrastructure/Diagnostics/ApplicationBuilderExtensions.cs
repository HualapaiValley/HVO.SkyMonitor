using Microsoft.AspNetCore.Builder;

namespace HVO.SkyMonitor.Common.Infrastructure.Diagnostics;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}
