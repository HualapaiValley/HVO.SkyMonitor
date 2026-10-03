using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public interface IObservingDayCalendarProvider
{
    ObservingDayCalendar Current { get; }
}

// Resolves the observing-day calendar from the active deployment location.
// Before the location initializes, no sunrise period is qualified. UTC is used only for navigation
// and for an explicitly requested historical noon interpretation.
public sealed class DeploymentObservingDayCalendarProvider(IDeploymentLocationStore? deploymentLocation = null)
    : IObservingDayCalendarProvider
{
    private readonly object _gate = new();
    private ObservingDayCalendar _calendar = ObservingDayCalendar.ForDeployment(null);
    private DeploymentLocationSnapshot? _site;
    private bool _resolved;

    public ObservingDayCalendar Current
    {
        get
        {
            var site = deploymentLocation?.Active;
            lock (_gate)
            {
                // Coordinates/version matter even when the zone is unchanged. Previously resolved periods retain
                // their immutable site and endpoints; only new reads use a changed active snapshot.
                if (!_resolved || site != _site)
                {
                    _resolved = true;
                    _calendar = ObservingDayCalendar.ForDeployment(site);
                    _site = site;
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
