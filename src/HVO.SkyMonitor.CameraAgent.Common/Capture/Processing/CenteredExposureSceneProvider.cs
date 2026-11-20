using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record CenteredSceneResources(
    CatalogSnapshotIdentity? Stars,
    CatalogSnapshotIdentity? DeepSky,
    string? TopologySha256,
    string? EphemerisVersion,
    string ProjectionVersion);

/// <summary>Allowlisted capture facts for independent catalog prediction.</summary>
internal sealed record CenteredPredictionFacts(
    ProjectedSceneSource Source,
    CaptureTimingDescriptor Timing,
    CaptureLocationProvenance Location,
    CameraRigConfig Rig,
    FrameLayoutDescriptor Layout,
    CenteredSceneResources Resources);

internal sealed class CenteredExposureSceneProvider(
    RawCaptureIngress ingress,
    CaptureProjectedSceneStager stager,
    ICelestialCatalog catalog,
    Func<IDeploymentLocationStore?> resolveLocations,
    IConstellationTopology? topology = null,
    IPlanetEphemeris? ephemeris = null)
{
    internal async Task<CenteredPredictionFacts?> ReadPredictionFactsAsync(
        ExposureKey key, CancellationToken cancellationToken)
    {
        var raw = await ingress.ReadCenteredRawAsync(key, cancellationToken).ConfigureAwait(false);
        if (raw is null) return null;
        var descriptor = raw.Manifest.Descriptor;
        return new(new(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor)),
            descriptor.Timing, raw.Run.Location, raw.Run.Configuration.Rig, descriptor.Layout, raw.Run.SceneResources);
    }

    internal Task<ProjectedSceneV1?> ResolvePredictionsAsync(
        CenteredPredictionFacts facts, CancellationToken cancellationToken)
        => BuildPredictedSceneAsync(facts, new ProjectedSceneCaptureProcessingStepOptions(), cancellationToken);

    internal async Task<ProjectedSceneV1?> ResolvePresentationAsync(
        CenteredExposureBindingV1 binding,
        ReconstructionDescriptor descriptor,
        ProjectedSceneCaptureProcessingStepOptions options,
        DateTimeOffset? virtualPresentationEpoch,
        CancellationToken cancellationToken)
    {
        var key = new ExposureKey(binding.Source.Camera, binding.Source.RunId, descriptor.Capture.CaptureId);
        var facts = await ReadPredictionFactsAsync(key, cancellationToken).ConfigureAwait(false);
        return facts is null ? null : await BuildPredictedSceneAsync(facts, options, cancellationToken,
            virtualPresentationEpoch).ConfigureAwait(false);
    }

    private async Task<ProjectedSceneV1?> BuildPredictedSceneAsync(
        CenteredPredictionFacts facts,
        ProjectedSceneCaptureProcessingStepOptions options,
        CancellationToken cancellationToken,
        DateTimeOffset? virtualPresentationEpoch = null)
    {
        if (facts.Resources.Stars is null || stager.FrozenResources != facts.Resources ||
            catalog is not ICelestialCatalogMetadataSource metadata)
            return null;

        var timing = facts.Timing;
        var effectiveUtc = timing.ExposureStartedUtc +
            TimeSpan.FromTicks((timing.ExposureEndedUtc - timing.ExposureStartedUtc).Ticks / 2);
        var locations = resolveLocations();
        if (locations is null) return null;
        var site = locations.Resolve(facts.Location, timing.ExposureStartedUtc);
        if (!site.IsEffectiveAt(effectiveUtc)) return null;
        var projection = RigProjectionContextFactory.Create(facts.Rig);
        if (projection.WidthPixels != facts.Layout.Width || projection.HeightPixels != facts.Layout.Height)
            return null;

        var request = new VisibleSceneRequest(virtualPresentationEpoch ?? effectiveUtc,
            new ObserverLocation(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters), projection,
            new CatalogQuery(options.MaximumMagnitude, options.MaximumResults), metadata.Metadata,
            horizonPolicy: HorizonPolicy.GeometricHorizon, projectionVersion: facts.Rig.Optics.CalibrationVersion,
            algorithmVersion: options.AstronomyAlgorithmVersion, constellationIds: options.ConstellationIds,
            solarSystemBodies: options.SolarSystemBodies,
            includeConstellationEndpointStars: options.IncludeConstellationEndpointStars);
        var scene = await new VisibleSceneBuilder(catalog, topology, ephemeris)
            .BuildAsync(request, cancellationToken).ConfigureAwait(false);

        if (options.DeepSkyEnabled || options.CuratedAnnotations is { Enabled: true })
        {
            if ((catalog as IDeepSkyCatalogSource)?.DeepSky is not { } deepSky || facts.Resources.DeepSky is null)
                return null;
            var selection = options.DeepSkyEnabled
                ? options.DeepSkySelection().ToSelection()
                : options.CuratedAnnotations!.ToSelection();
            scene = scene.WithCuratedDeepSky(deepSky, selection);
        }

        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, scene,
            ProjectedSceneImageTransformV1.Identity(facts.Layout.Width, facts.Layout.Height),
            facts.Source, facts.Rig.Optics.CalibrationVersion, facts.Rig.Optics.CalibrationVersion);
    }
}
