using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Describes how strongly a projected scene is tied to image evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProjectedSceneKind>))]
public enum ProjectedSceneKind
{
    Predicted,
    VirtualRenderAuthoritative,
    ImageRegistered
}

/// <summary>The image coordinate convention used by every projected scene v1 point.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProjectedSceneCoordinateConvention>))]
public enum ProjectedSceneCoordinateConvention
{
    ContinuousTopLeftPixelEdge
}

public sealed record ProjectedSceneCatalog(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Version,
    [property: JsonRequired] Uri SourceUrl,
    [property: JsonRequired] string ChecksumSha256,
    [property: JsonRequired] string License,
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string PreprocessingVersion);

/// <summary>Canonical catalog and topology selections used to construct the visible scene.</summary>
public sealed record ProjectedSceneSelection(
    [property: JsonRequired] double MaximumMagnitude,
    [property: JsonRequired] int MaximumResults,
    [property: JsonRequired] IReadOnlyList<string> ConstellationIds,
    [property: JsonRequired] IReadOnlyList<SolarSystemBody> SolarSystemBodies,
    [property: JsonRequired] bool IncludeConstellationEndpointStars);

/// <summary>Constellation topology source and checksum bound by the visible-scene builder.</summary>
public sealed record ProjectedSceneTopologyProvenance(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Version,
    [property: JsonRequired] Uri SourceUrl,
    [property: JsonRequired] string SourceSha256,
    [property: JsonRequired] string? ArtifactSha256,
    [property: JsonRequired] string License,
    [property: JsonRequired] string PreprocessingVersion);

public sealed record ProjectedSceneProjection(
    [property: JsonRequired] ProjectionModel Model,
    [property: JsonRequired] ProjectionAperture Aperture,
    [property: JsonRequired] string CalibrationVersion,
    [property: JsonRequired] string AlgorithmVersion,
    [property: JsonRequired] int WidthPixels,
    [property: JsonRequired] int HeightPixels,
    [property: JsonRequired] double PrincipalPointX,
    [property: JsonRequired] double PrincipalPointY,
    [property: JsonRequired] double FocalLengthXPixels,
    [property: JsonRequired] double FocalLengthYPixels,
    [property: JsonRequired] double? ImageCircleRadiusPixels,
    [property: JsonRequired] double BoresightAltitudeDegrees,
    [property: JsonRequired] double BoresightAzimuthDegrees,
    [property: JsonRequired] double RollDegrees,
    [property: JsonRequired] bool HorizontalFlip,
    [property: JsonRequired] bool EnforceSensorBounds);

/// <summary>Supported clockwise post-readout rotations in image coordinates.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProjectedSceneQuarterRotation>))]
public enum ProjectedSceneQuarterRotation
{
    Degrees0,
    Degrees90,
    Degrees180,
    Degrees270
}

/// <summary>Versioned source-projection to emitted-image transform in continuous pixel-edge coordinates.</summary>
public sealed record ProjectedSceneImageTransformV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] int SourceWidthPixels,
    [property: JsonRequired] int SourceHeightPixels,
    [property: JsonRequired] int CropX,
    [property: JsonRequired] int CropY,
    [property: JsonRequired] int CropWidth,
    [property: JsonRequired] int CropHeight,
    [property: JsonRequired] int BinX,
    [property: JsonRequired] int BinY,
    [property: JsonRequired] bool HorizontalMirror,
    [property: JsonRequired] bool VerticalMirror,
    [property: JsonRequired] ProjectedSceneQuarterRotation Rotation,
    [property: JsonRequired] int OutputWidthPixels,
    [property: JsonRequired] int OutputHeightPixels)
{
    public const string CurrentSchemaVersion = "projected-scene-image-transform-v1";
    public const string OperationOrder = "crop-bin-horizontal-mirror-vertical-mirror-quarter-rotation-v1";

    /// <summary>Creates an emitted-image identity transform for one optical projection.</summary>
    public static ProjectedSceneImageTransformV1 Identity(int widthPixels, int heightPixels) => new(
        CurrentSchemaVersion, widthPixels, heightPixels, 0, 0, widthPixels, heightPixels,
        1, 1, false, false, ProjectedSceneQuarterRotation.Degrees0, widthPixels, heightPixels);
}

/// <summary>Identifies the capture evidence associated with a scene without embedding it.</summary>
public sealed record ProjectedSceneSource(
    [property: JsonRequired] Guid CaptureId,
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] string ArtifactIdentitySha256);

/// <summary>A canonical immutable snapshot of existing visible-scene geometry; it does not claim physical detection.</summary>
public sealed record ProjectedSceneV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string SceneIdentitySha256,
    [property: JsonRequired] ProjectedSceneKind Kind,
    [property: JsonRequired] DateTimeOffset EffectiveUtc,
    [property: JsonRequired] ObserverLocation Observer,
    [property: JsonRequired] ProjectedSceneCatalog Catalog,
    [property: JsonRequired] ProjectedSceneSelection Selection,
    [property: JsonRequired] ProjectedSceneProjection Projection,
    [property: JsonRequired] ProjectedSceneImageTransformV1 ImageTransform,
    [property: JsonRequired] ProjectedSceneCoordinateConvention CoordinateConvention,
    [property: JsonRequired] HorizonPolicy HorizonPolicy,
    [property: JsonRequired] RefractionOptions Refraction,
    [property: JsonRequired] string AstronomyAlgorithmVersion,
    [property: JsonRequired] ProjectedSceneTopologyProvenance? ConstellationTopology,
    [property: JsonRequired] string? EphemerisModelVersion,
    [property: JsonRequired] ProjectedSceneSource Source,
    [property: JsonRequired] IReadOnlyList<ProjectedCelestialObject> Objects,
    [property: JsonRequired] IReadOnlyList<ProjectedConstellationSegment> Segments)
{
    public const string CurrentSchemaVersion = "projected-scene-v1";
}

public sealed record ProjectedSceneParseResult(ProjectedSceneV1? Scene, string? ErrorPath)
{
    public bool IsValid => Scene is not null;
}

/// <summary>Canonical JSON, strict parsing, validation, and SHA-256 identity operations for projected scenes.</summary>
public static class ProjectedSceneJson
{
    private const double DirectionTolerance = 1e-9;
    private const double PixelTolerance = 1e-8;
    private const int GeneratedGeometryDecimalPlaces = 12;
    /// <summary>Maximum accepted width or height for a projected scene.</summary>
    public const int MaximumDimensionPixels = 65_536;
    /// <summary>Maximum checked scene pixel area, independent of payload byte size.</summary>
    public const long MaximumPixelArea = 268_435_456;
    /// <summary>Maximum canonical uncompressed UTF-8 payload size.</summary>
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    public const int MaximumObjectCount = 10_000;
    public const int MaximumSegmentCount = 50_000;
    /// <summary>Recommendation for durable storage; identity always covers uncompressed canonical JSON.</summary>
    public const string PersistenceRecommendation = "canonical-json-utf8-compress-at-rest-v1";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>Snapshots a visible scene with separately identified calibration and projection algorithm facts.</summary>
    public static ProjectedSceneV1 Create(
        ProjectedSceneKind kind,
        VisibleScene visibleScene,
        ProjectedSceneImageTransformV1 imageTransform,
        ProjectedSceneSource source,
        string projectionCalibrationVersion,
        string projectionAlgorithmVersion)
    {
        ArgumentNullException.ThrowIfNull(visibleScene);
        ArgumentNullException.ThrowIfNull(imageTransform);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionCalibrationVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionAlgorithmVersion);
        if (!string.Equals(projectionAlgorithmVersion, visibleScene.Request.ProjectionVersion, StringComparison.Ordinal))
            throw new ArgumentException("Projection algorithm identity must match the visible-scene geometry.", nameof(projectionAlgorithmVersion));
        if (visibleScene.Objects.Count > MaximumObjectCount || visibleScene.Segments.Count > MaximumSegmentCount)
            throw new ArgumentException("Visible scene exceeds projected-scene structural bounds.", nameof(visibleScene));
        var request = visibleScene.Request;
        var computation = visibleScene.ComputationProvenance;
        if ((request.SolarSystemBodies.Count > 0 || visibleScene.Objects.Any(static item => item.Kind == CelestialObjectKind.SolarSystemBody)) &&
            string.IsNullOrWhiteSpace(computation.EphemerisModelVersion))
            throw new ArgumentException("Solar-system selections and objects require ephemeris provenance.", nameof(visibleScene));
        if ((request.ConstellationIds.Count > 0 || visibleScene.Segments.Count > 0) && computation.ConstellationTopology is null)
            throw new ArgumentException("Constellation selections and segments require topology provenance.", nameof(visibleScene));
        var projection = request.Projection;
        var geometry = ProjectedSceneImageTransform.CreateGeometrySnapshot(visibleScene, imageTransform);
        var scene = new ProjectedSceneV1(
            ProjectedSceneV1.CurrentSchemaVersion,
            string.Empty,
            kind,
            request.Utc,
            request.Observer,
            new ProjectedSceneCatalog(
                request.CatalogMetadata.Name,
                request.CatalogMetadata.Version,
                request.CatalogMetadata.SourceUrl,
                NormalizeSha256(request.CatalogMetadata.Checksum),
                request.CatalogMetadata.License,
                request.CatalogMetadata.SchemaVersion,
                computation.CatalogPreprocessingVersion),
            new ProjectedSceneSelection(
                request.CatalogQuery.MaximumMagnitude,
                request.CatalogQuery.MaximumResults,
                Freeze(request.ConstellationIds.Select(static id => id.ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                Freeze(request.SolarSystemBodies.Distinct().Order()),
                request.IncludeConstellationEndpointStars),
            new ProjectedSceneProjection(
                projection.Model,
                projection.Aperture,
                projectionCalibrationVersion,
                projectionAlgorithmVersion,
                projection.WidthPixels,
                projection.HeightPixels,
                projection.PrincipalPointX,
                projection.PrincipalPointY,
                projection.FocalLengthXPixels,
                projection.FocalLengthYPixels,
                projection.ImageCircleRadiusPixels,
                projection.BoresightAltitudeDegrees,
                projection.BoresightAzimuthDegrees,
                projection.RollDegrees,
                projection.HorizontalFlip,
                projection.EnforceSensorBounds),
            imageTransform,
            ProjectedSceneCoordinateConvention.ContinuousTopLeftPixelEdge,
            request.HorizonPolicy,
            request.Refraction,
            request.AlgorithmVersion,
            computation.ConstellationTopology is null ? null : new ProjectedSceneTopologyProvenance(
                computation.ConstellationTopology.Name,
                computation.ConstellationTopology.Version,
                computation.ConstellationTopology.SourceUrl,
                NormalizeSha256(computation.ConstellationTopology.SourceSha256),
                computation.ConstellationTopologyArtifactSha256 is null
                    ? null
                    : NormalizeSha256(computation.ConstellationTopologyArtifactSha256),
                computation.ConstellationTopology.License,
                computation.ConstellationTopology.PreprocessingVersion),
            computation.EphemerisModelVersion,
            source with { ArtifactIdentitySha256 = NormalizeSha256(source.ArtifactIdentitySha256) },
            Freeze(geometry.Objects.Select(NormalizeGeneratedGeometry).OrderBy(static item => item.Magnitude)
                .ThenBy(static item => item.Id, StringComparer.Ordinal)),
            Freeze(geometry.Segments.Select(NormalizeGeneratedGeometry)
                .OrderBy(static item => item.ConstellationId, StringComparer.Ordinal)
                .ThenBy(static item => item.FromObjectId, StringComparer.Ordinal)
                .ThenBy(static item => item.ToObjectId, StringComparer.Ordinal)
                .ThenBy(static item => item.PartIndex)));
        scene = scene with { SceneIdentitySha256 = ComputeIdentity(scene) };
        Validate(scene);
        return scene;
    }

    public static byte[] Serialize(ProjectedSceneV1 scene)
    {
        Validate(scene);
        var bytes = SerializeCanonical(scene);
        if (bytes.Length > MaximumPayloadBytes)
        {
            throw new ArgumentException("Projected scene exceeds the 4 MiB uncompressed payload limit.", nameof(scene));
        }
        return bytes;
    }

    public static ProjectedSceneParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumPayloadBytes)
        {
            return new(null, "$payload");
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (HasDuplicateProperties(document.RootElement) || !HasRequiredShape(document.RootElement))
            {
                return new(null, "$json");
            }
            var scene = JsonSerializer.Deserialize<ProjectedSceneV1>(utf8Json.Span, SerializerOptions);
            if (scene is null)
            {
                return new(null, "$scene");
            }
            Validate(scene);
            var normalized = Freeze(scene);
            if (!utf8Json.Span.SequenceEqual(SerializeCanonical(normalized)))
            {
                return new(null, "$canonical");
            }
            return new(normalized, null);
        }
        catch (JsonException)
        {
            return new(null, "$json");
        }
        catch (ArgumentException exception)
        {
            return new(null, exception.ParamName ?? "$scene");
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException or FormatException or OverflowException)
        {
            return new(null, "$scene");
        }
    }

    public static string ComputeIdentity(ProjectedSceneV1 scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var identityDocument = scene with { SceneIdentitySha256 = string.Empty };
        return CaptureContractJson.ComputeCanonicalJsonSha256(
            JsonSerializer.SerializeToElement(identityDocument, SerializerOptions));
    }

    public static void Validate(ProjectedSceneV1 scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!string.Equals(scene.SchemaVersion, ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException("Unsupported schema.", nameof(scene));
        if (!Enum.IsDefined(scene.Kind) || !Enum.IsDefined(scene.CoordinateConvention) ||
            !Enum.IsDefined(scene.HorizonPolicy) || scene.EffectiveUtc == default || scene.EffectiveUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Scene kind, coordinate convention, horizon policy, and effective UTC must be explicit.", nameof(scene));
        scene.Observer.Validate();
        scene.Refraction.Validate();
        if (scene.Catalog is null || scene.Selection is null || scene.Projection is null ||
            scene.ImageTransform is null || scene.Source is null)
            throw new ArgumentException("Nested scene contracts are required.", nameof(scene));
        ValidateText(scene.Catalog.Name, nameof(scene.Catalog.Name));
        ValidateText(scene.Catalog.Version, nameof(scene.Catalog.Version));
        ValidateText(scene.Catalog.SchemaVersion, nameof(scene.Catalog.SchemaVersion));
        ValidateText(scene.Catalog.License, nameof(scene.Catalog.License));
        ValidateText(scene.Catalog.PreprocessingVersion, nameof(scene.Catalog.PreprocessingVersion));
        ValidateSha256(scene.Catalog.ChecksumSha256, nameof(scene.Catalog.ChecksumSha256));
        if (scene.Catalog.SourceUrl is null || !scene.Catalog.SourceUrl.IsAbsoluteUri)
            throw new ArgumentException("Catalog source URL must be absolute.", nameof(scene));
        ValidateSelection(scene.Selection);
        if (scene.ConstellationTopology is not null) ValidateTopology(scene.ConstellationTopology);
        ValidateText(scene.Projection.CalibrationVersion, nameof(scene.Projection.CalibrationVersion));
        ValidateText(scene.Projection.AlgorithmVersion, nameof(scene.Projection.AlgorithmVersion));
        ValidateText(scene.AstronomyAlgorithmVersion, nameof(scene.AstronomyAlgorithmVersion));
        if (scene.EphemerisModelVersion is not null) ValidateText(scene.EphemerisModelVersion, nameof(scene.EphemerisModelVersion));
        ValidateProjection(scene.Projection);
        ProjectedSceneImageTransform.Validate(scene.ImageTransform, scene.Projection.WidthPixels, scene.Projection.HeightPixels);
        if (scene.Source.CaptureId == Guid.Empty || scene.Source.ArtifactId == Guid.Empty)
            throw new ArgumentException("Source identities cannot be empty.", nameof(scene));
        ValidateSha256(scene.Source.ArtifactIdentitySha256, nameof(scene.Source.ArtifactIdentitySha256));
        if (scene.Objects is null || scene.Objects.Count > MaximumObjectCount || scene.Objects.Any(static item => item is null) ||
            scene.Segments is null || scene.Segments.Count > MaximumSegmentCount)
            throw new ArgumentException("Scene structural count exceeds its bound.", nameof(scene));
        if (scene.Segments.Any(static item => item is null))
            throw new ArgumentException("Scene lists cannot contain null elements.", nameof(scene));
        var needsEphemeris = scene.Selection.SolarSystemBodies.Count > 0 ||
            scene.Objects.Any(static item => item.Kind == CelestialObjectKind.SolarSystemBody);
        if (needsEphemeris && string.IsNullOrWhiteSpace(scene.EphemerisModelVersion))
            throw new ArgumentException("Solar-system selections and objects require ephemeris provenance.", nameof(scene));
        var needsTopology = scene.Selection.ConstellationIds.Count > 0 || scene.Segments.Count > 0;
        if (needsTopology && scene.ConstellationTopology is null)
            throw new ArgumentException("Constellation selections and segments require topology provenance.", nameof(scene));

        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        var projectionContext = ToProjectionContext(scene.Projection);
        var projector = ProjectorFactory.Create(projectionContext);
        var basis = CameraBasis.Create(
            scene.Projection.BoresightAltitudeDegrees, scene.Projection.BoresightAzimuthDegrees,
            scene.Projection.RollDegrees, scene.Projection.HorizontalFlip);
        for (var index = 0; index < scene.Objects.Count; index++)
        {
            var item = scene.Objects[index];
            ValidateText(item.Id, $"objects[{index}].id");
            ValidateText(item.DisplayName, $"objects[{index}].displayName");
            if (!Enum.IsDefined(item.Kind) ||
                !Finite(item.J2000Equatorial.RightAscensionHours, item.J2000Equatorial.DeclinationDegrees,
                    item.EquatorialOfDate.RightAscensionHours, item.EquatorialOfDate.DeclinationDegrees,
                    item.GeometricHorizontal.AltitudeDegrees, item.GeometricHorizontal.AzimuthDegrees,
                    item.ApparentHorizontal.AltitudeDegrees, item.ApparentHorizontal.AzimuthDegrees,
                    item.CameraDirection.East, item.CameraDirection.North, item.CameraDirection.Up,
                    item.Pixel.X, item.Pixel.Y, item.Magnitude) ||
                item.J2000Equatorial.RightAscensionHours is < 0 or >= 24 ||
                item.EquatorialOfDate.RightAscensionHours is < 0 or >= 24 ||
                item.J2000Equatorial.DeclinationDegrees is < -90 or > 90 ||
                item.EquatorialOfDate.DeclinationDegrees is < -90 or > 90 ||
                item.GeometricHorizontal.AltitudeDegrees is < -90 or > 90 ||
                item.ApparentHorizontal.AltitudeDegrees is < -90 or > 90 ||
                item.GeometricHorizontal.AzimuthDegrees is < 0 or >= 360 ||
                item.ApparentHorizontal.AzimuthDegrees is < 0 or >= 360 ||
                item.CameraDirection.East is < -1 or > 1 || item.CameraDirection.North is < -1 or > 1 ||
                item.CameraDirection.Up is < -1 or > 1 || Math.Abs(item.CameraDirection.Length - 1) > 1e-9 ||
                !ContainsOutput(scene.Projection, scene.ImageTransform, item.Pixel) ||
                scene.HorizonPolicy == HorizonPolicy.GeometricHorizon && item.GeometricHorizontal.AltitudeDegrees < 0 ||
                item.ColorIndex is { } color && !double.IsFinite(color))
                throw new ArgumentException("Projected object contains an invalid value.", $"objects[{index}]");
            ValidateText(item.CatalogVersion, $"objects[{index}].catalogVersion");
            ValidateText(item.ProjectionVersion, $"objects[{index}].projectionVersion");
            ValidateText(item.AlgorithmVersion, $"objects[{index}].algorithmVersion");
            if (!string.Equals(item.CatalogVersion, scene.Catalog.Version, StringComparison.Ordinal) ||
                !string.Equals(item.ProjectionVersion, scene.Projection.AlgorithmVersion, StringComparison.Ordinal) ||
                !string.Equals(item.AlgorithmVersion, scene.AstronomyAlgorithmVersion, StringComparison.Ordinal))
                throw new ArgumentException("Object algorithm and source versions must match scene-level facts.", nameof(scene));
            var expectedDirection = basis.ToCamera(CameraBasis.FromHorizontal(item.ApparentHorizontal));
            var expectedSourcePixel = projector.Project(item.ApparentHorizontal);
            PixelPoint? expectedPixel = expectedSourcePixel is null
                ? null
                : ProjectedSceneImageTransform.Apply(scene.ImageTransform, expectedSourcePixel.Value);
            if (Distance(item.CameraDirection, expectedDirection) > DirectionTolerance || expectedPixel is null ||
                Distance(item.Pixel, expectedPixel.Value) > PixelTolerance)
                throw new ArgumentException("Object direction and pixel must agree with the scene projection.", nameof(scene));
            if (item.HipparcosId is not null) ValidateText(item.HipparcosId, $"objects[{index}].hipparcosId");
            if (!objectIds.Add(item.Id))
                throw new ArgumentException("Projected object identifiers must be unique.", nameof(scene));
            if (index > 0 && CompareObjects(scene.Objects[index - 1], item) >= 0)
                throw new ArgumentException("Projected objects must be unique and in magnitude-then-ID order.", nameof(scene));
        }
        ProjectedConstellationSegment? previousSegment = null;
        for (var index = 0; index < scene.Segments.Count; index++)
        {
            var item = scene.Segments[index];
            ValidateText(item.ConstellationId, $"segments[{index}].constellationId");
            ValidateText(item.FromObjectId, $"segments[{index}].fromObjectId");
            ValidateText(item.ToObjectId, $"segments[{index}].toObjectId");
            if (!scene.Selection.ConstellationIds.Contains(item.ConstellationId, StringComparer.Ordinal))
                throw new ArgumentException("Projected segment constellation must be requested by the scene selection.", nameof(scene));
            if (item.PartIndex < 0 || !Finite(item.FromPixel.X, item.FromPixel.Y, item.ToPixel.X, item.ToPixel.Y) ||
                !ContainsOutput(scene.Projection, scene.ImageTransform, item.FromPixel) ||
                !ContainsOutput(scene.Projection, scene.ImageTransform, item.ToPixel) ||
                !Contains(scene.Projection, ProjectedSceneImageTransform.Inverse(scene.ImageTransform, item.FromPixel)) ||
                !Contains(scene.Projection, ProjectedSceneImageTransform.Inverse(scene.ImageTransform, item.ToPixel)))
                throw new ArgumentException("Projected segment contains an invalid value.", $"segments[{index}]");
            if (previousSegment is not null && CompareSegments(previousSegment, item) >= 0)
                throw new ArgumentException("Projected segments must be unique and in canonical order.", nameof(scene));
            var sameLogicalSegment = previousSegment is not null &&
                string.Equals(previousSegment.ConstellationId, item.ConstellationId, StringComparison.Ordinal) &&
                string.Equals(previousSegment.FromObjectId, item.FromObjectId, StringComparison.Ordinal) &&
                string.Equals(previousSegment.ToObjectId, item.ToObjectId, StringComparison.Ordinal);
            var expectedPartIndex = sameLogicalSegment ? previousSegment!.PartIndex + 1 : 0;
            if (item.PartIndex != expectedPartIndex)
                throw new ArgumentException("Segment parts must be contiguous and zero-based per logical segment.", nameof(scene));
            previousSegment = item;
        }
        ValidateSha256(scene.SceneIdentitySha256, nameof(scene.SceneIdentitySha256));
        if (!string.Equals(scene.SceneIdentitySha256, ComputeIdentity(scene), StringComparison.Ordinal))
            throw new ArgumentException("Scene identity does not match its canonical content.", nameof(scene));
    }

    private static byte[] SerializeCanonical(ProjectedSceneV1 scene) =>
        JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            JsonSerializer.SerializeToElement(scene, SerializerOptions)));

    private static void ValidateProjection(ProjectedSceneProjection value)
    {
        if (!Enum.IsDefined(value.Model) || !Enum.IsDefined(value.Aperture) ||
            value.WidthPixels is < 1 or > MaximumDimensionPixels || value.HeightPixels is < 1 or > MaximumDimensionPixels ||
            (long)value.WidthPixels * value.HeightPixels > MaximumPixelArea ||
            !Finite(value.PrincipalPointX, value.PrincipalPointY, value.FocalLengthXPixels,
                value.FocalLengthYPixels, value.BoresightAltitudeDegrees, value.BoresightAzimuthDegrees, value.RollDegrees) ||
            value.FocalLengthXPixels <= 0 || value.FocalLengthYPixels <= 0 ||
            value.BoresightAltitudeDegrees is < -90 or > 90 ||
            value.Aperture == ProjectionAperture.Circular && value.ImageCircleRadiusPixels is not { } ||
            value.Aperture == ProjectionAperture.Rectangular && value.ImageCircleRadiusPixels is not null ||
            value.Model == ProjectionModel.Perspective != (value.Aperture == ProjectionAperture.Rectangular) ||
            value.ImageCircleRadiusPixels is { } radius && (!double.IsFinite(radius) || radius <= 0))
            throw new ArgumentException("Projection is invalid.", nameof(value));
        try
        {
            ToProjectionContext(value).Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Projection is invalid.", nameof(value), exception);
        }
    }

    private static void ValidateSelection(ProjectedSceneSelection value)
    {
        if (!double.IsFinite(value.MaximumMagnitude) || value.MaximumResults is < 1 or > 100_000 ||
            value.ConstellationIds is null || value.SolarSystemBodies is null ||
            value.ConstellationIds.Count > 256 || value.SolarSystemBodies.Count > Enum.GetValues<SolarSystemBody>().Length ||
            value.ConstellationIds.Any(string.IsNullOrWhiteSpace) ||
            value.ConstellationIds.Any(static id => !string.Equals(id, id.ToUpperInvariant(), StringComparison.Ordinal)) ||
            value.ConstellationIds.Any(static id => id.Length > 256) ||
            value.SolarSystemBodies.Any(static body => !Enum.IsDefined(body)) ||
            !value.ConstellationIds.SequenceEqual(value.ConstellationIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) ||
            !value.SolarSystemBodies.SequenceEqual(value.SolarSystemBodies.Distinct().Order()))
            throw new ArgumentException("Scene selection is invalid or not canonically ordered.", nameof(value));
    }

    private static void ValidateTopology(ProjectedSceneTopologyProvenance value)
    {
        ValidateText(value.Name, nameof(value));
        ValidateText(value.Version, nameof(value));
        ValidateText(value.License, nameof(value));
        ValidateText(value.PreprocessingVersion, nameof(value));
        ValidateSha256(value.SourceSha256, nameof(value));
        if (value.ArtifactSha256 is not null) ValidateSha256(value.ArtifactSha256, nameof(value));
        if (value.SourceUrl is null || !value.SourceUrl.IsAbsoluteUri)
            throw new ArgumentException("Topology source URL must be absolute.", nameof(value));
    }

    private static bool Contains(ProjectedSceneProjection projection, PixelPoint point)
    {
        if (projection.EnforceSensorBounds &&
            (point.X < 0 || point.X > projection.WidthPixels || point.Y < 0 || point.Y > projection.HeightPixels))
            return false;
        if (projection.Aperture == ProjectionAperture.Rectangular) return true;
        var dx = point.X - projection.PrincipalPointX;
        var dy = point.Y - projection.PrincipalPointY;
        return dx * dx + dy * dy <= Math.Pow(projection.ImageCircleRadiusPixels!.Value, 2) + 1e-8;
    }

    private static bool ContainsOutput(
        ProjectedSceneProjection projection,
        ProjectedSceneImageTransformV1 transform,
        PixelPoint point) =>
        !projection.EnforceSensorBounds && ProjectedSceneImageTransform.IsIdentity(transform) ||
        point.X >= 0 && point.X <= transform.OutputWidthPixels &&
        point.Y >= 0 && point.Y <= transform.OutputHeightPixels;

    private static ProjectionContext ToProjectionContext(ProjectedSceneProjection value) => new(
        value.Model, value.PrincipalPointX, value.PrincipalPointY, value.FocalLengthXPixels,
        value.FocalLengthYPixels, value.WidthPixels, value.HeightPixels, value.Aperture,
        value.ImageCircleRadiusPixels, value.BoresightAltitudeDegrees, value.BoresightAzimuthDegrees,
        value.RollDegrees, value.HorizontalFlip, value.EnforceSensorBounds);

    private static double Distance(EnuVector left, EnuVector right) => Math.Sqrt(
        Math.Pow(left.East - right.East, 2) + Math.Pow(left.North - right.North, 2) + Math.Pow(left.Up - right.Up, 2));

    private static double Distance(PixelPoint left, PixelPoint right) => Math.Sqrt(
        Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static int CompareObjects(ProjectedCelestialObject left, ProjectedCelestialObject right)
    {
        var magnitude = left.Magnitude.CompareTo(right.Magnitude);
        return magnitude != 0 ? magnitude : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private static int CompareSegments(ProjectedConstellationSegment left, ProjectedConstellationSegment right)
    {
        var result = StringComparer.Ordinal.Compare(left.ConstellationId, right.ConstellationId);
        if (result == 0) result = StringComparer.Ordinal.Compare(left.FromObjectId, right.FromObjectId);
        if (result == 0) result = StringComparer.Ordinal.Compare(left.ToObjectId, right.ToObjectId);
        return result == 0 ? left.PartIndex.CompareTo(right.PartIndex) : result;
    }

    private static bool Finite(params double[] values) => values.All(double.IsFinite);

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (HasDuplicateProperties(item)) return true;
            }
        }
        return false;
    }

    private static bool HasRequiredShape(JsonElement root)
    {
        if (!HasProperties(root, "schemaVersion", "sceneIdentitySha256", "kind", "effectiveUtc", "observer", "catalog", "selection",
            "projection", "imageTransform", "coordinateConvention", "horizonPolicy", "refraction",
            "astronomyAlgorithmVersion", "constellationTopology", "ephemerisModelVersion", "source", "objects", "segments")) return false;
        if (!Object(root, "observer", out var observer) || !HasProperties(observer, "latitudeDegrees", "longitudeDegrees", "elevationMeters") ||
            !Object(root, "catalog", out var catalog) || !HasProperties(catalog, "name", "version", "sourceUrl",
                "checksumSha256", "license", "schemaVersion", "preprocessingVersion") ||
            !Object(root, "selection", out var selection) || !HasProperties(selection, "maximumMagnitude", "maximumResults",
                "constellationIds", "solarSystemBodies", "includeConstellationEndpointStars") ||
            !Array(selection, "constellationIds", out _) || !Array(selection, "solarSystemBodies", out _) ||
            !Object(root, "projection", out var projection) || !HasProperties(projection, "model", "aperture", "calibrationVersion",
                "algorithmVersion", "widthPixels", "heightPixels", "principalPointX", "principalPointY", "focalLengthXPixels",
                "focalLengthYPixels", "imageCircleRadiusPixels", "boresightAltitudeDegrees", "boresightAzimuthDegrees", "rollDegrees",
                "horizontalFlip", "enforceSensorBounds") ||
            !Object(root, "imageTransform", out var imageTransform) || !HasProperties(imageTransform, "schemaVersion", "sourceWidthPixels",
                "sourceHeightPixels", "cropX", "cropY", "cropWidth", "cropHeight", "binX", "binY", "horizontalMirror",
                "verticalMirror", "rotation", "outputWidthPixels", "outputHeightPixels") ||
            !Object(root, "refraction", out var refraction) || !HasProperties(refraction, "enabled", "minimumAltitudeDegrees") ||
            !Object(root, "source", out var source) || !HasProperties(source, "captureId", "artifactId", "artifactIdentitySha256") ||
            !Array(root, "objects", out var objects) || !Array(root, "segments", out var segments)) return false;
        if (root.GetProperty("constellationTopology") is { ValueKind: not JsonValueKind.Null } topology &&
            !HasProperties(topology, "name", "version", "sourceUrl", "sourceSha256", "artifactSha256", "license", "preprocessingVersion")) return false;
        foreach (var item in objects.EnumerateArray())
        {
            if (!HasProperties(item, "id", "displayName", "kind", "j2000Equatorial", "equatorialOfDate", "geometricHorizontal",
                "apparentHorizontal", "cameraDirection", "pixel", "magnitude", "colorIndex", "catalogVersion", "projectionVersion",
                "algorithmVersion", "hipparcosId") ||
                !Point(item, "j2000Equatorial", "rightAscensionHours", "declinationDegrees") ||
                !Point(item, "equatorialOfDate", "rightAscensionHours", "declinationDegrees") ||
                !Point(item, "geometricHorizontal", "altitudeDegrees", "azimuthDegrees") ||
                !Point(item, "apparentHorizontal", "altitudeDegrees", "azimuthDegrees") ||
                !Point(item, "cameraDirection", "east", "north", "up") ||
                !Point(item, "pixel", "x", "y")) return false;
        }
        foreach (var item in segments.EnumerateArray())
        {
            if (!HasProperties(item, "constellationId", "fromObjectId", "toObjectId", "fromPixel", "toPixel", "partIndex") ||
                !Point(item, "fromPixel", "x", "y") || !Point(item, "toPixel", "x", "y")) return false;
        }
        return true;

        static bool Point(JsonElement parent, string name, params string[] properties) =>
            Object(parent, name, out var point) && HasProperties(point, properties);
    }

    private static bool HasProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var properties = value.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        return names.All(properties.Contains);
    }

    private static bool Object(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool Array(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array;

    private static void ValidateText(string value, string path)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new ArgumentException("Text is blank or too long.", path);
    }

    private static void ValidateSha256(string value, string path)
    {
        if (value is null || value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character) || char.IsLetter(character) && !char.IsUpper(character)))
            throw new ArgumentException("A SHA-256 value is required.", path);
    }

    private static string NormalizeSha256(string value)
    {
        if (value is null || value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 value is required.", nameof(value));
        return value.ToUpperInvariant();
    }

    private static ProjectedCelestialObject NormalizeGeneratedGeometry(ProjectedCelestialObject value) => value with
    {
        J2000Equatorial = Normalize(value.J2000Equatorial),
        EquatorialOfDate = Normalize(value.EquatorialOfDate),
        GeometricHorizontal = Normalize(value.GeometricHorizontal),
        ApparentHorizontal = Normalize(value.ApparentHorizontal),
        CameraDirection = Normalize(value.CameraDirection),
        Pixel = Normalize(value.Pixel),
        Magnitude = Normalize(value.Magnitude),
        ColorIndex = value.ColorIndex is { } colorIndex ? Normalize(colorIndex) : null
    };

    private static ProjectedConstellationSegment NormalizeGeneratedGeometry(ProjectedConstellationSegment value) =>
        value with { FromPixel = Normalize(value.FromPixel), ToPixel = Normalize(value.ToPixel) };

    private static EquatorialPoint Normalize(EquatorialPoint value) => new(
        NormalizePeriodic(value.RightAscensionHours, 24), Normalize(value.DeclinationDegrees));

    private static AltAzPoint Normalize(AltAzPoint value) => new(
        Normalize(value.AltitudeDegrees), NormalizePeriodic(value.AzimuthDegrees, 360));

    private static EnuVector Normalize(EnuVector value) => new(
        Normalize(value.East), Normalize(value.North), Normalize(value.Up));

    private static PixelPoint Normalize(PixelPoint value) => new(Normalize(value.X), Normalize(value.Y));

    private static double Normalize(double value)
    {
        var rounded = Math.Round(value, GeneratedGeometryDecimalPlaces, MidpointRounding.ToEven);
        return rounded == 0 ? 0 : rounded;
    }

    private static double NormalizePeriodic(double value, double period)
    {
        if (value < 0 || value >= period)
        {
            return value;
        }
        var rounded = Normalize(value);
        return rounded >= period ? 0 : rounded;
    }

    private static ProjectedSceneV1 Freeze(ProjectedSceneV1 scene) => scene with
    {
        Selection = scene.Selection with
        {
            ConstellationIds = Freeze(scene.Selection.ConstellationIds),
            SolarSystemBodies = Freeze(scene.Selection.SolarSystemBodies)
        },
        Objects = Freeze(scene.Objects),
        Segments = Freeze(scene.Segments)
    };

    private static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
