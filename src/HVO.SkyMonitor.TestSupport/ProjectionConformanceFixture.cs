using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.TestSupport;

/// <summary>Shared projection fixture used by host test projects to detect consumer drift.</summary>
public static class ProjectionConformanceFixture
{
    /// <summary>Projects a stable cardinal direction through the shared perspective projector.</summary>
    public static PixelPoint ProjectReferenceDirection()
    {
        var projector = ProjectorFactory.CreatePerspective(new PerspectiveProjectionContext(100, 100, 100, 100, 200, 200));
        return projector.Project(new AltAzPoint(60, 90))
            ?? throw new InvalidOperationException("Reference direction should be visible.");
    }
}
