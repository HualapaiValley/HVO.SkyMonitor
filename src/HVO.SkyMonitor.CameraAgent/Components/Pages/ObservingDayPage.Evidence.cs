namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ObservingDayPage
{
    // Retain failures by capture identity across day navigation; another day's image never substitutes for it.
    private readonly HashSet<Guid> _failedRepresentativePreviews = [];
}
