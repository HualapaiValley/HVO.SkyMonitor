using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Frames;

namespace HVO.SkyMonitor.CameraAgent.Common.SkyMap;

/// <summary>
/// Projects the installed catalog, the authoritative observer location, and the
/// active rig geometry into one bounded read-only view for a UTC instant. The
/// projection is offline and deterministic for the same instant, location, rig,
/// and catalog; it never reads LogicHost, the network, or a storage path.
/// </summary>
public interface ICameraAgentSkyMapProjection
{
    /// <summary>Projects the sky map for <paramref name="atUtc"/>, or for the current instant when it is null.</summary>
    ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(
        DateTimeOffset? atUtc,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class CameraAgentSkyMapProjection(
    ICameraAgentConfigurationAccessor configurationAccessor,
    ICelestialCatalog catalog,
    TimeProvider timeProvider,
    IConstellationTopology? constellationTopology = null,
    IDeploymentLocationStore? deploymentLocationStore = null,
    ILatestFrameAccessor? latestFrameAccessor = null) : ICameraAgentSkyMapProjection
{
    /// <summary>The hard upper bound on projected objects returned by one query.</summary>
    public const int MaximumObjects = 200;

    /// <summary>The fixed limiting magnitude applied before exact horizon and projection rejection.</summary>
    public const double MaximumMagnitude = 6.5;

    /// <summary>The coordinate and selection algorithm version recorded with every projection.</summary>
    public const string AstronomyAlgorithmVersion = "visible-scene-iau1976-constellation-v2";

    /// <inheritdoc />
    public async ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(
        DateTimeOffset? atUtc,
        CancellationToken cancellationToken)
    {
        var instant = (atUtc ?? timeProvider.GetUtcNow()).ToUniversalTime();
        var config = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (catalog is not ICelestialCatalogMetadataSource metadataSource)
        {
            throw new InvalidOperationException(
                "The sky map requires an installed catalog that publishes validated provenance metadata.");
        }

        var location = deploymentLocationStore?.Active ?? config.DeploymentLocation
            ?? throw new InvalidOperationException(
                "The sky map requires authoritative deployment coordinates from the local configuration owner.");
        var sourceKind = deploymentLocationStore?.ResolveSourceKind(location) ?? DeploymentLocationSourceKind.Unspecified;
        var observer = new CameraAgentSkyMapObserver(
            location.LocationId,
            location.Version,
            location.CanonicalSha256,
            location.Source,
            sourceKind,
            location.Version <= 1
                ? CameraAgentSkyMapLocationOrigin.StartupSeed
                : CameraAgentSkyMapLocationOrigin.SucceededVersion,
            deploymentLocationStore?.Staged is not null,
            location.LatitudeDegrees,
            location.LongitudeDegrees,
            location.ElevationMeters,
            location.TimeZoneId,
            location.HorizontalAccuracyMeters,
            location.EffectiveFromUtc,
            location.EffectiveUntilUtc,
            location.IsEffectiveAt(instant));

        var metadata = metadataSource.Metadata;
        var projection = RigProjectionContextFactory.Create(config.Rig);
        var constellationIds = ResolveConstellationIds();
        var request = new VisibleSceneRequest(
            instant,
            new ObserverLocation(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters),
            projection,
            new CatalogQuery(MaximumMagnitude, MaximumObjects),
            metadata,
            horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: config.Rig.Optics.CalibrationVersion,
            algorithmVersion: AstronomyAlgorithmVersion,
            constellationIds: constellationIds);
        var scene = await new VisibleSceneBuilder(catalog, constellationTopology)
            .BuildAsync(request, cancellationToken).ConfigureAwait(false);

        var objects = scene.Objects
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .Take(MaximumObjects)
            .Select(static item => new CameraAgentSkyMapObject(
                item.Id,
                item.DisplayName,
                item.Kind.ToString(),
                item.Magnitude,
                item.ApparentHorizontal.AltitudeDegrees,
                item.ApparentHorizontal.AzimuthDegrees,
                item.Pixel.X,
                item.Pixel.Y,
                item.HipparcosId))
            .ToArray();
        var constellations = scene.Segments
            .GroupBy(static segment => segment.ConstellationId, StringComparer.Ordinal)
            .Select(static group => new CameraAgentSkyMapConstellation(group.Key, group.Count()))
            .OrderBy(static item => item.ConstellationId, StringComparer.Ordinal)
            .ToArray();

        var latestScene = ResolveLatestCaptureScene();
        return new CameraAgentSkyMapProjectionResult(
            instant,
            new CameraAgentSkyMapCatalogIdentity(
                metadata.Name,
                metadata.Version,
                metadata.Checksum,
                metadata.License,
                metadata.SchemaVersion,
                metadataSource.PreprocessingVersion),
            observer,
            CreateGeometry(config, projection),
            objects,
            constellations,
            MaximumObjects,
            objects.Length >= MaximumObjects,
            MaximumMagnitude,
            AstronomyAlgorithmVersion,
            CreateSummary(objects.Length, constellations.Length, constellationIds.Length, latestScene),
            latestScene);
    }

    private string[] ResolveConstellationIds()
        => constellationTopology is InMemoryConstellationTopology topology && catalog is IHipparcosCatalog
            ? topology.AllSegments
                .Select(static segment => segment.ConstellationId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static CameraAgentSkyMapGeometry CreateGeometry(CameraModuleConfig config, ProjectionContext projection)
    {
        var landmarks = RigProjectionContextFactory.CreateAnnotationLandmarks(projection);
        return new CameraAgentSkyMapGeometry(
            projection.Model.ToString(),
            projection.Aperture.ToString(),
            projection.BoresightAltitudeDegrees,
            projection.BoresightAzimuthDegrees,
            projection.RollDegrees,
            projection.HorizontalFlip,
            config.Rig.Optics.FieldOfViewDegrees,
            config.Rig.Optics.VerticalFieldOfViewDegrees,
            projection.PrincipalPointX,
            projection.PrincipalPointY,
            projection.FocalLengthXPixels,
            projection.FocalLengthYPixels,
            projection.WidthPixels,
            projection.HeightPixels,
            projection.ImageCircleRadiusPixels,
            config.Rig.ProfileVersion,
            RigProjectionContextFactory.CreateProfileHashSha256(config.Rig),
            config.Rig.Optics.CalibrationVersion,
            RigProjectionContextFactory.AlgorithmVersion,
            [
                new CameraAgentSkyMapCardinal("North", 0, landmarks?.North.X, landmarks?.North.Y),
                new CameraAgentSkyMapCardinal("East", 90, landmarks?.East.X, landmarks?.East.Y),
                new CameraAgentSkyMapCardinal("South", 180, landmarks?.South.X, landmarks?.South.Y),
                new CameraAgentSkyMapCardinal("West", 270, landmarks?.West.X, landmarks?.West.Y)
            ]);
    }

    private CameraAgentSkyMapCaptureProvenance? ResolveLatestCaptureScene()
    {
        if (latestFrameAccessor is null ||
            !latestFrameAccessor.TryGetSnapshot(out var snapshot) ||
            snapshot.Metadata?.Scene is not { } scene)
        {
            return null;
        }

        return new CameraAgentSkyMapCaptureProvenance(
            scene.SceneId,
            scene.SceneUtc,
            scene.CatalogName,
            scene.CatalogVersion,
            scene.CatalogChecksumSha256,
            scene.ProjectionModel,
            scene.RigProfileVersion,
            scene.RigProfileHashSha256,
            scene.ProjectionCalibrationVersion,
            scene.ProjectedSceneStageIdentitySha256);
    }

    private static string CreateSummary(
        int objectCount,
        int constellationCount,
        int requestedConstellationCount,
        CameraAgentSkyMapCaptureProvenance? latestScene)
    {
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{objectCount} of at most {MaximumObjects} catalog objects brighter than magnitude {MaximumMagnitude} fall inside the calibrated image, with {constellationCount} of {requestedConstellationCount} installed constellation figures partly visible.");
        if (requestedConstellationCount == 0)
        {
            summary += " Constellation topology is unavailable to this catalog, so no figures were resolved.";
        }
        return latestScene is null
            ? summary + " No retained capture currently carries scene provenance, so capture-time scene identity is omitted."
            : summary;
    }
}
