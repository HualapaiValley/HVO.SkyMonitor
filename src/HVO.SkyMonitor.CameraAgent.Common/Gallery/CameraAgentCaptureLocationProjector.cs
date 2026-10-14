using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal static class CameraAgentCaptureLocationProjector
{
    internal static CameraAgentCaptureLocationFacts Project(
        ReconstructionDescriptor? descriptor, IDeploymentLocationStore? history)
    {
        if (descriptor is null) return new("Unavailable");
        if (descriptor.Location is not { } provenance) return new("NotRetained");
        if (history is null) return new("Unavailable");
        try
        {
            var captured = history.Resolve(provenance, descriptor.Timing.ExposureStartedUtc);
            if (!captured.Validate().IsValid || captured.ToProvenance() != provenance ||
                !captured.IsEffectiveAt(descriptor.Timing.ExposureStartedUtc))
                return new("Unavailable");
            return new("Available", captured.LatitudeDegrees, captured.LongitudeDegrees,
                captured.ElevationMeters, captured.TimeZoneId, captured.Version);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
            ArgumentException or IOException or UnauthorizedAccessException)
        {
            // An unavailable optional history must not hide the image or other retained capture facts.
            return new("Unavailable");
        }
    }
}
