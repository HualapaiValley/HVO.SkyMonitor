using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.ZWO.Components.Shared;

public sealed class AppErrorBoundary : ErrorBoundary
{
    [Inject]
    public ILogger<AppErrorBoundary> Logger { get; set; } = default!;

    [Inject]
    public ICorrelationIdAccessor CorrelationIdAccessor { get; set; } = default!;

    [CascadingParameter]
    public HttpContext? HttpContext { get; set; }

    protected override Task OnErrorAsync(Exception exception)
    {
        var correlationId = CorrelationIdAccessor.GetCorrelationId();
        using (Logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId ?? string.Empty,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? HttpContext?.TraceIdentifier ?? string.Empty
        }))
        {
            Logger.LogError(exception, "Unhandled exception bubbled to the application error boundary.");
        }

        return base.OnErrorAsync(exception);
    }
}
