using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public interface IObservingDayCalendarProvider
{
    ObservingDayCalendar Current { get; }
}

// Resolves the observing-day calendar from the active deployment location.
// Before the location initializes, or when no store is registered, the
// calendar falls back to UTC and every resolved day says so.
public sealed class DeploymentObservingDayCalendarProvider(IDeploymentLocationStore? deploymentLocation = null)
    : IObservingDayCalendarProvider
{
    private readonly object _gate = new();
    private ObservingDayCalendar _calendar = ObservingDayCalendar.Create(null);
    private string? _timeZoneId;
    private bool _resolved;

    public ObservingDayCalendar Current
    {
        get
        {
            var timeZoneId = deploymentLocation?.Active?.TimeZoneId;
            lock (_gate)
            {
                // Recompute only when the configured identifier changes; an
                // identifier this host cannot resolve stays cached as a fallback.
                if (!_resolved || !string.Equals(timeZoneId, _timeZoneId, StringComparison.Ordinal))
                {
                    _resolved = true;
                    _calendar = ObservingDayCalendar.Create(timeZoneId);
                    _timeZoneId = timeZoneId;
                }
                return _calendar;
            }
        }
    }
}

public sealed class FixedObservingDayCalendarProvider(ObservingDayCalendar calendar) : IObservingDayCalendarProvider
{
    public ObservingDayCalendar Current { get; } = calendar ?? throw new ArgumentNullException(nameof(calendar));
}
