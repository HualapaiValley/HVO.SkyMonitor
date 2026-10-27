using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

/// <summary>Page-lifetime navigation identity only; every display still needs fresh authorized reads.</summary>
internal sealed class CurrentSkyPosition
{
    internal (Guid Observatory, Guid Camera)? Scope { get; private set; }
    internal long ScopeGeneration { get; private set; }
    internal CameraCurrentSelection? Selection { get; set; }
    internal Guid? CaptureId { get; set; }

    internal void SetScope(Guid observatory, Guid camera)
    {
        if (Scope == (observatory, camera)) return;
        Scope = (observatory, camera);
        ScopeGeneration++;
        Selection = null;
        CaptureId = null;
    }
}
