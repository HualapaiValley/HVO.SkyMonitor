using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// The agent clock in the deployment site time zone, ticking only this component once a second.
/// </summary>
public sealed partial class AgentClock : ComponentBase, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private ITimer? _timer;
    private DateTimeOffset _now;
    private bool _disposed;

    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;

    /// <summary>The site's time zone, or null when no deployment location is active.</summary>
    [Parameter] public string? SiteTimeZoneId { get; set; }

    protected override void OnInitialized()
    {
        _now = TimeProvider.GetUtcNow();
        // The first tick lands on the next whole second, so the seconds change together with the agent's clock.
        var untilNextSecond = TickInterval - TimeSpan.FromTicks(_now.UtcTicks % TickInterval.Ticks);
        _timer = TimeProvider.CreateTimer(
            static state => ((AgentClock)state!).OnTick(), this, untilNextSecond, TickInterval);
    }

    private void OnTick()
    {
        if (_disposed)
        {
            return;
        }
        _ = InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }
            _now = TimeProvider.GetUtcNow();
            StateHasChanged();
        });
    }

    private CameraAgentSiteTime SiteClock => new(ObservingDayCalendar.Create(SiteTimeZoneId));

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
