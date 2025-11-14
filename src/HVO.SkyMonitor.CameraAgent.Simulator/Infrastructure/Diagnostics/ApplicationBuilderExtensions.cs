using System;
using Microsoft.AspNetCore.Builder;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Infrastructure.Diagnostics;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}
