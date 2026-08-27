using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed class CaptureProjectedSceneStager(
    IProjectedSceneStagingStore stagingStore,
    ICelestialCatalog catalog,
    IConstellationTopology? topology = null,
    IPlanetEphemeris? ephemeris = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly ProjectedSceneSource IdentitySource = new(
        new Guid("11111111-1111-1111-1111-111111111111"),
        new Guid("22222222-2222-2222-2222-222222222222"),
        new string('A', 64));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort cleanup must not replace the optional staging failure; startup reconciliation owns any retained stage.")]
    internal async ValueTask<CaptureLoopSubmission> StageAsync(
        CameraModuleConfig config,
        CaptureLoopSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Result.Frame is not { } frame ||
            string.Equals(config.ModuleType, "VirtualSky", StringComparison.OrdinalIgnoreCase) ||
            ResolveOptions(config) is not { } options ||
            frame.Metadata.Scene?.ProjectedSceneStageKey is not null ||
            frame.Layout is not { } layout ||
            submission.Result.AcquisitionTiming is not { } timing ||
            config.DeploymentLocation is not { } location ||
            catalog is not ICelestialCatalogMetadataSource metadataSource ||
            options.ConstellationIds.Count > 0 && topology is null ||
            options.SolarSystemBodies.Count > 0 && ephemeris is null)
        {
            return submission;
        }

        var exposureStartedUtc = timing.ExposureStartedUtc.ToUniversalTime();
        var exposureEndedUtc = timing.ExposureEndedUtc.ToUniversalTime();
        if (exposureStartedUtc == default || exposureEndedUtc != default && exposureEndedUtc < exposureStartedUtc)
            return submission;
        var effectiveUtc = exposureEndedUtc > exposureStartedUtc
            ? exposureStartedUtc + TimeSpan.FromTicks((exposureEndedUtc - exposureStartedUtc).Ticks / 2)
            : exposureStartedUtc + TimeSpan.FromTicks(frame.Metadata.Exposure.Ticks / 2);
        if (!location.IsEffectiveAt(effectiveUtc)) return submission;

        var projection = RigProjectionContextFactory.Create(config.Rig);
        if (projection.WidthPixels != frame.Width || projection.HeightPixels != frame.Height ||
            layout.Width != frame.Width || layout.Height != frame.Height || layout.PixelFormat != frame.PixelFormat)
        {
            return submission;
        }

        var metadata = metadataSource.Metadata;
        var request = new VisibleSceneRequest(
            effectiveUtc,
            new ObserverLocation(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters),
            projection,
            new CatalogQuery(options.MaximumMagnitude, options.MaximumResults),
            metadata,
            horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: config.Rig.Optics.CalibrationVersion,
            algorithmVersion: options.AstronomyAlgorithmVersion,
            constellationIds: options.ConstellationIds,
            solarSystemBodies: options.SolarSystemBodies,
            includeConstellationEndpointStars: options.IncludeConstellationEndpointStars);
        var scene = await new VisibleSceneBuilder(catalog, topology, ephemeris)
            .BuildAsync(request, cancellationToken).ConfigureAwait(false);
        var identityDocument = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted,
            scene,
            ProjectedSceneImageTransformV1.Identity(frame.Width, frame.Height),
            IdentitySource,
            config.Rig.Optics.CalibrationVersion,
            config.Rig.Optics.CalibrationVersion);
        var captureId = RawCaptureDescriptorFactory.CreateStableIds(config, submission).CaptureId;
        var stageKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hvo.skymonitor.projected-scene-stage-key.v1\0{captureId:N}")));
        var topologyMetadata = options.ConstellationIds.Count > 0 ? topology?.Metadata : null;
        var provenance = new SceneProvenance(
            stageKey,
            config.Rig.ProfileVersion,
            metadata.Name,
            metadata.Version,
            metadata.Checksum,
            projection.Model.ToString(),
            RigProjectionContextFactory.AlgorithmVersion,
            options.AstronomyAlgorithmVersion,
            config.Rig.Sensor.SensorRecipeVersion,
            metadata.SourceUrl,
            metadata.License,
            metadata.SchemaVersion,
            metadataSource.PreprocessingVersion,
            scene.Objects.Select(static item => new ProjectedObjectProvenance(
                item.Id, item.DisplayName, item.Pixel.X, item.Pixel.Y, item.Magnitude)).ToArray(),
            scene.Segments.Select(static item => new ProjectedSegmentProvenance(
                item.ConstellationId, item.FromObjectId, item.ToObjectId,
                item.FromPixel.X, item.FromPixel.Y, item.ToPixel.X, item.ToPixel.Y, item.PartIndex)).ToArray(),
            options.SolarSystemBodies.Count > 0 ? ephemeris?.ModelVersion : null,
            topologyMetadata?.Version,
            topologyMetadata?.SourceUrl,
            topologyMetadata?.SourceSha256,
            topologyMetadata?.License,
            topologyMetadata?.PreprocessingVersion,
            options.ConstellationIds,
            options.IncludeConstellationEndpointStars,
            RigProjectionContextFactory.CreateProfileHashSha256(config.Rig),
            config.Rig.Optics.CalibrationVersion,
            SceneUtc: effectiveUtc,
            ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
            ProjectedSceneStageKey: stageKey,
            ProjectedSceneStageIdentitySha256: identityDocument.SceneIdentitySha256);
        await stagingStore.StageAsync(
            stageKey,
            stageKey,
            scene,
            new CaptureProjectedSceneStageFacts(
                CameraRigProfileIdentity.ComputeSha256(config.Rig),
                layout,
                ProjectedSceneKind.Predicted,
                identityDocument.SceneIdentitySha256),
            cancellationToken).ConfigureAwait(false);
        return submission with
        {
            Result = submission.Result with
            {
                Frame = frame with { Metadata = frame.Metadata with { Scene = provenance } }
            }
        };
    }

    private static ProjectedSceneCaptureProcessingStepOptions? ResolveOptions(CameraModuleConfig config)
    {
        var step = config.Pipeline.Steps.FirstOrDefault(static candidate =>
            candidate.Enabled is not false && string.Equals(candidate.Type, "ProjectedScene", StringComparison.OrdinalIgnoreCase));
        if (step is null) return null;
        return step.Options is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value
            ? value.Deserialize<ProjectedSceneCaptureProcessingStepOptions>(SerializerOptions)
            : new ProjectedSceneCaptureProcessingStepOptions();
    }
}
