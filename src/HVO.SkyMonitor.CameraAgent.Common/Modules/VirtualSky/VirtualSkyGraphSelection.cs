using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

/// <summary>The validated immutable graph selected once before a virtual capture's scene is rendered.</summary>
public sealed record VirtualSkyGraphSelection(string RevisionId, CapturePipelineConfig Pipeline);

/// <summary>Optional acquisition-time binding for independently configured deep-sky annotations.</summary>
public interface IVirtualSkyGraphSelection
{
    ValueTask<VirtualSkyGraphSelection?> ReadAsync(CameraModuleConfig configuration, CancellationToken cancellationToken);
}

internal sealed class VirtualSkyGraphSelectionProvider(ProcessingGraphOperationsCoordinator operations, bool enabled) : IVirtualSkyGraphSelection
{
    public async ValueTask<VirtualSkyGraphSelection?> ReadAsync(CameraModuleConfig configuration, CancellationToken cancellationToken)
    {
        if (!enabled) return null;
        var revision = await operations.SelectLiveRevisionAsync(configuration, retainedCapture: false, cancellationToken).ConfigureAwait(false);
        return new(revision.State.RevisionId, revision.Pipeline);
    }
}
