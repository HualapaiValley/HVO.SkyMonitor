using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Components;

/// <summary>Site-clock presentation for components; unavailable deployment state uses a labeled UTC fallback.</summary>
public abstract class SiteTimeComponent : ComponentBase
{
    [Inject] protected IServiceProvider SiteServices { get; set; } = default!;

    protected CameraAgentSiteTime SiteTime => new(SiteServices.GetService<IObservingDayCalendarProvider>()?.Current
        ?? ObservingDayCalendar.ForDeployment(null));
}
