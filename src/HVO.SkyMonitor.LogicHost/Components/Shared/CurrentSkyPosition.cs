using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

/// <summary>Page-lifetime navigation identity only; every display still needs fresh authorized reads.</summary>
internal sealed class CurrentSkyPosition
{
    internal (Guid Observatory, Guid Camera)? Scope { get; set; }
    internal CameraCurrentSelection? Selection { get; set; }
    internal Guid? CaptureId { get; set; }
}
