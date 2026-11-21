using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

/// <summary>The capture facts needed for measurement and independent catalog/mask geometry.</summary>
internal sealed record TransientMeasurementSource(
    ProcessingArtifact Artifact,
    TransientSourceEvidenceReferenceV1 Source,
    CameraRigConfig Rig,
    ObservatoryLocation Observatory,
    CaptureLocationProvenance? Location,
    DateTimeOffset CelestialStartUtc,
    TimeSpan EffectiveExposure);

internal sealed class TransientDetectorSourcePreparation(Func<IDeploymentLocationStore?>? deploymentLocationStoreAccessor = null)
{
    /// <summary>Project authenticated capture evidence without carrying the scene or module's simulator options.</summary>
    internal IReadOnlyDictionary<int, TransientMeasurementSource> Prepare(
        IReadOnlyDictionary<int, CameraModuleConfig> configurations,
        IReadOnlyDictionary<int, TransientLoadedFrame> frames,
        bool integrateStarlight)
    {
        var sources = new Dictionary<int, TransientMeasurementSource>(frames.Count);
        foreach (var (offset, frame) in frames)
        {
            var descriptor = frame.Manifest.Descriptor;
            var config = configurations[offset];
            var site = ResolveObservatory(descriptor, config);
            var celestialStart = frame.Source.ObservationStartedUtc;
            if (integrateStarlight)
            {
                try
                {
                    celestialStart = VirtualExposureProvenance.ResolveCelestialStartUtc(descriptor, frame.Manifest.Scene?.VirtualExposure);
                }
                catch (ArgumentException)
                {
                    throw new TransientWorkerExecutionException("transient-runtime.exposure-star-mask-unsupported", retryable: false);
                }
            }

            sources.Add(offset, new(frame.Artifact, frame.Source, config.Rig, site, descriptor.Location,
                celestialStart, descriptor.Controls.EffectiveExposure));
        }
        return sources;
    }

    internal ObservatoryLocation ResolveObservatory(ReconstructionDescriptor descriptor, CameraModuleConfig configuration)
    {
        if (descriptor.Location is { } provenance)
        {
            var store = deploymentLocationStoreAccessor?.Invoke() ?? throw new TransientWorkerExecutionException(
                "transient-runtime.location-history-unavailable", retryable: false);
            return store.Resolve(provenance, descriptor.Timing.ExposureStartedUtc).ToObservatoryLocation();
        }
        if (configuration.DeploymentLocation is not null || configuration.DeploymentLocationRedacted)
        {
            throw new TransientWorkerExecutionException(
                "transient-runtime.capture-location-missing", retryable: false);
        }
        return configuration.Observatory;
    }
}
