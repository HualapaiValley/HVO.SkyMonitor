using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record AnnotationSceneResult(VisibleScene Scene, SceneProvenance Provenance);

internal interface IAnnotationSceneProvider
{
    ValueTask<AnnotationSceneResult> BuildAsync(
        CameraModuleConfig config,
        CameraFrame rawFrame,
        IReadOnlyList<string> constellationIds,
        CancellationToken cancellationToken = default);
}

internal sealed class AnnotationSceneProvider(
    Func<ICelestialCatalog?> catalogAccessor,
    IConstellationTopology constellationTopology) : IAnnotationSceneProvider
{
    private const string AstronomyAlgorithmVersion = "visible-scene-iau1976-constellation-v2";

    public async ValueTask<AnnotationSceneResult> BuildAsync(
        CameraModuleConfig config,
        CameraFrame rawFrame,
        IReadOnlyList<string> constellationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rawFrame);
        ArgumentNullException.ThrowIfNull(constellationIds);
        var catalog = catalogAccessor() ?? throw new InvalidOperationException(
            "Real-frame constellation annotation requires a configured celestial catalog.");
        if (catalog is not ICelestialCatalogMetadataSource metadataSource)
        {
            throw new InvalidOperationException(
                "Real-frame constellation annotation requires a catalog with validated provenance metadata.");
        }
        if (catalog is not IHipparcosCatalog)
        {
            throw new InvalidOperationException(
                "Real-frame constellation annotation requires stable Hipparcos catalog lookup.");
        }
        if (rawFrame.Width != config.Rig.Sensor.WidthPixels || rawFrame.Height != config.Rig.Sensor.HeightPixels ||
            rawFrame.PixelFormat != config.Rig.Sensor.PixelFormat)
        {
            throw new InvalidOperationException(
                "Real-frame constellation annotation requires the captured frame layout to match the configured rig.");
        }

        var projection = RigProjectionContextFactory.Create(config.Rig);
        var metadata = metadataSource.Metadata;
        var request = new VisibleSceneRequest(
            rawFrame.TimestampUtc,
            new ObserverLocation(
                config.Observatory.LatitudeDegrees,
                config.Observatory.LongitudeDegrees,
                config.Observatory.ElevationMeters),
            projection,
            new CatalogQuery(-30, 1),
            metadata,
            horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: config.Rig.Optics.CalibrationVersion,
            algorithmVersion: AstronomyAlgorithmVersion,
            constellationIds: constellationIds,
            includeConstellationEndpointStars: false);
        var scene = await new VisibleSceneBuilder(catalog, constellationTopology)
            .BuildAsync(request, cancellationToken).ConfigureAwait(false);
        var sceneId = CreateHash(new
        {
            Kind = "real-constellation-annotation-v1",
            request.Utc,
            request.Observer,
            request.Projection,
            request.ProjectionVersion,
            request.AlgorithmVersion,
            request.ConstellationIds,
            Catalog = new { metadata.Version, metadata.Checksum },
            Topology = new { constellationTopology.Metadata.Version, constellationTopology.Metadata.SourceSha256 }
        });
        var rigVersion = string.Concat("sha256:", CreateHash(config.Rig));
        var topologyMetadata = constellationTopology.Metadata;
        var provenance = new SceneProvenance(
            sceneId,
            rigVersion,
            metadata.Name,
            metadata.Version,
            metadata.Checksum,
            projection.Model.ToString(),
            config.Rig.Optics.CalibrationVersion,
            AstronomyAlgorithmVersion,
            config.Rig.Sensor.SensorRecipeVersion,
            metadata.SourceUrl,
            metadata.License,
            metadata.SchemaVersion,
            metadataSource.PreprocessingVersion,
            Objects: Array.Empty<ProjectedObjectProvenance>(),
            Segments: scene.Segments.Select(static item => new ProjectedSegmentProvenance(
                item.ConstellationId, item.FromObjectId, item.ToObjectId,
                item.FromPixel.X, item.FromPixel.Y, item.ToPixel.X, item.ToPixel.Y, item.PartIndex)).ToArray(),
            ConstellationTopologyVersion: topologyMetadata.Version,
            ConstellationTopologySourceUrl: topologyMetadata.SourceUrl,
            ConstellationTopologySha256: topologyMetadata.SourceSha256,
            ConstellationTopologyLicense: topologyMetadata.License,
            ConstellationTopologyPreprocessingVersion: topologyMetadata.PreprocessingVersion,
            ConstellationIds: request.ConstellationIds,
            IncludeConstellationEndpointStars: false);
        return new AnnotationSceneResult(scene, provenance);
    }

    private static string CreateHash<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
