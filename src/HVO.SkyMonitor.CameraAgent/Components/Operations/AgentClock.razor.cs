using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// Two facts for an <c>ops-facts</c> list: this agent's clock in UTC and in the site's time zone. It reads the agent's
/// clock, not the browser's, and re-renders only itself once a second so the panel around it does not.
/// </summary>
public sealed partial class AgentClock : ComponentBase, IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private ITimer? _timer;
    private TimeZoneInfo? _zone;
    private string? _zoneId;
    private DateTimeOffset _now;
    private bool _zoneResolved;
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

    protected override void OnParametersSet()
    {
        if (_zoneResolved && string.Equals(_zoneId, SiteTimeZoneId, StringComparison.Ordinal))
        {
            return;
        }
        _zoneResolved = true;
        _zoneId = SiteTimeZoneId;
        _zone = SiteTimeZoneId is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
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

    private static string Iso(DateTimeOffset value) => value.ToString("yyyy-MM-ddTHH:mm:sszzz", Invariant);

    private static string Offset(TimeSpan offset)
        => string.Concat(offset < TimeSpan.Zero ? "−" : "+", offset.Duration().ToString(@"hh\:mm", Invariant));

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
