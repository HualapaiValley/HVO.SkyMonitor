using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class HorizonCaptureObserver
{
    internal static ObserverLocation Resolve(CaptureProcessingContext context, IServiceProvider? services)
    {
        ObservatoryLocation site;
        if (context.ReconstructionDescriptor is { } descriptor)
        {
            var provenance = descriptor.Location ?? throw new InvalidDataException("Capture location provenance is unavailable.");
            var store = services?.GetService<IDeploymentLocationStore>() ??
                throw new InvalidDataException("Capture location history is unavailable.");
            site = store.Resolve(provenance, descriptor.Timing.ExposureStartedUtc).ToObservatoryLocation();
        }
        else
        {
            if (context.Config.DeploymentLocationRedacted)
                throw new InvalidDataException("Capture location provenance is unavailable.");
            site = context.Config.ResolveObservatory();
        }
        return new(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters);
    }
}
