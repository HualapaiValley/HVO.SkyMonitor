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

/// <summary>
/// Canonical catalog and topology selections used to construct the visible scene. The deep-sky selection is present
/// exactly when the scene carries a deep-sky collection, so v1 and v2 selections serialize unchanged.
/// </summary>
public sealed record ProjectedSceneSelection(
    [property: JsonRequired] double MaximumMagnitude,
    [property: JsonRequired] int MaximumResults,
    [property: JsonRequired] IReadOnlyList<string> ConstellationIds,
    [property: JsonRequired] IReadOnlyList<SolarSystemBody> SolarSystemBodies,
    [property: JsonRequired] bool IncludeConstellationEndpointStars,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ProjectedSceneDeepSkySelection? DeepSky = null);

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
    [property: JsonRequired] bool EnforceSensorBounds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double RadialDistortionK1 = 0);

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

/// <summary>
/// A canonical immutable snapshot of existing visible-scene geometry; it does not claim physical detection.
/// Scenes with a deep-sky collection use <see cref="DeepSkySchemaVersion"/>; other scenes with resolved footprints
/// use <see cref="ResolvedFootprintSchemaVersion"/>; all others keep <see cref="CurrentSchemaVersion"/> and serialize
/// byte-identically to the original v1 contract. Each version adds only members the previous one omits.
/// </summary>
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
    [property: JsonRequired] IReadOnlyList<ProjectedConstellationSegment> Segments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ProjectedResolvedFootprint>? ResolvedFootprints = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ProjectedDeepSky? DeepSky = null)
{
    public const string CurrentSchemaVersion = SceneProvenance.RetainedProjectedSceneSchemaVersion;
    public const string ResolvedFootprintSchemaVersion = SceneProvenance.ResolvedFootprintProjectedSceneSchemaVersion;
    public const string DeepSkySchemaVersion = SceneProvenance.DeepSkyProjectedSceneSchemaVersion;

    /// <summary>Returns whether a stored product schema is a projected scene this contract parses.</summary>
    public static bool IsSupportedSchemaVersion(string? schemaVersion) =>
        SceneProvenance.IsRetainedProjectedSceneSchemaVersion(schemaVersion);

    /// <summary>Every projected-scene schema version this contract parses, oldest first.</summary>
    public static IReadOnlyList<string> SupportedSchemaVersions { get; } =
        Array.AsReadOnly([CurrentSchemaVersion, ResolvedFootprintSchemaVersion, DeepSkySchemaVersion]);
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
    public const int MaximumResolvedFootprintCount = 64;
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
        var footprints = (geometry.ResolvedFootprints ?? [])
            .Select(NormalizeGeneratedGeometry)
            .OfType<ProjectedResolvedFootprint>()
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (footprints.Length > MaximumResolvedFootprintCount)
            throw new ArgumentException("Visible scene exceeds projected-scene structural bounds.", nameof(visibleScene));
        var deepSky = geometry.DeepSky is { } projectedDeepSky
            ? NormalizeGeneratedGeometry(projectedDeepSky, footprints, visibleScene.DeepSkySelection!)
            : null;
        var schemaVersion = deepSky is not null
            ? ProjectedSceneV1.DeepSkySchemaVersion
            : footprints.Length == 0 ? ProjectedSceneV1.CurrentSchemaVersion : ProjectedSceneV1.ResolvedFootprintSchemaVersion;
        var scene = new ProjectedSceneV1(
            schemaVersion,
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
                NormalizeConstellationIds(request.ConstellationIds),
                Freeze(request.SolarSystemBodies.Distinct().Order()),
                request.IncludeConstellationEndpointStars,
                deepSky is null ? null : visibleScene.DeepSkySelection),
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
                projection.EnforceSensorBounds,
                projection.RadialDistortionK1),
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
                .ThenBy(static item => item.PartIndex)),
            footprints.Length == 0 ? null : Freeze(footprints),
            deepSky);
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

    public static ProjectedSceneParseResult Parse(ReadOnlyMemory<byte> utf8Json) =>
        Parse(utf8Json, ProjectedSceneV1.SupportedSchemaVersions);

    /// <summary>
    /// Parses a scene that must carry one of <paramref name="acceptedSchemaVersions"/>. Any other schema fails closed
    /// with the error path <c>$schemaVersion</c> before its content is read, which is how a reader that predates a
    /// schema refuses it.
    /// </summary>
    internal static ProjectedSceneParseResult Parse(ReadOnlyMemory<byte> utf8Json, IReadOnlyList<string> acceptedSchemaVersions)
    {
        ArgumentNullException.ThrowIfNull(acceptedSchemaVersions);
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
            if (document.RootElement.GetProperty("schemaVersion") is not { ValueKind: JsonValueKind.String } schema ||
                !acceptedSchemaVersions.Contains(schema.GetString(), StringComparer.Ordinal))
            {
                return new(null, "$schemaVersion");
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

    /// <summary>
    /// Returns a constellation selection as a scene records it: each identifier is converted with
    /// <see cref="string.ToUpperInvariant()"/>, then duplicates are removed by ordinal comparison, then the result is
    /// sorted by ordinal comparison. Producers declare the selection in configuration order, so a declared selection is
    /// normalized this way before it is compared with a scene.
    /// </summary>
    public static IReadOnlyList<string> NormalizeConstellationIds(IEnumerable<string> constellationIds)
    {
        ArgumentNullException.ThrowIfNull(constellationIds);
        return Freeze(constellationIds.Select(static id => id.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
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
        var hasDeepSky = string.Equals(scene.SchemaVersion, ProjectedSceneV1.DeepSkySchemaVersion, StringComparison.Ordinal);
        var hasFootprints = string.Equals(
            scene.SchemaVersion, ProjectedSceneV1.ResolvedFootprintSchemaVersion, StringComparison.Ordinal);
        if (!hasDeepSky && !hasFootprints &&
            !string.Equals(scene.SchemaVersion, ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException("Unsupported schema.", nameof(scene));
        // v2 requires resolved footprints and v1 forbids them; v3 carries them only when it has some.
        if (!hasDeepSky && hasFootprints != scene.ResolvedFootprints is not null ||
            scene.ResolvedFootprints is { Count: 0 or > MaximumResolvedFootprintCount } ||
            scene.ResolvedFootprints?.Any(static item => item is null) == true)
            throw new ArgumentException("Resolved footprints require projected-scene-v2 or v3 and are absent from v1.", nameof(scene));
        if (!Enum.IsDefined(scene.Kind) || !Enum.IsDefined(scene.CoordinateConvention) ||
            !Enum.IsDefined(scene.HorizonPolicy) || scene.EffectiveUtc == default || scene.EffectiveUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Scene kind, coordinate convention, horizon policy, and effective UTC must be explicit.", nameof(scene));
        scene.Observer.Validate();
        scene.Refraction.Validate();
        if (scene.Catalog is null || scene.Selection is null || scene.Projection is null ||
            scene.ImageTransform is null || scene.Source is null)
            throw new ArgumentException("Nested scene contracts are required.", nameof(scene));
        if (hasDeepSky != scene.DeepSky is not null || hasDeepSky != scene.Selection.DeepSky is not null)
            throw new ArgumentException(
                "A deep-sky collection and its selection require projected-scene-v3 and are absent from v1 and v2.", nameof(scene));
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
            scene.Objects.Any(static item => item.Kind == CelestialObjectKind.SolarSystemBody) ||
            scene.ResolvedFootprints?.Any(static item => item.SourceKind == ResolvedFootprintSourceKind.SolarSystemBody) == true;
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
        if (scene.ResolvedFootprints is not null)
            ValidateFootprints(scene, projector);
        if (scene.DeepSky is not null)
            ValidateDeepSky(scene, projector);
        ValidateSha256(scene.SceneIdentitySha256, nameof(scene.SceneIdentitySha256));
        if (!string.Equals(scene.SceneIdentitySha256, ComputeIdentity(scene), StringComparison.Ordinal))
            throw new ArgumentException("Scene identity does not match its canonical content.", nameof(scene));
    }

    private static void ValidateFootprints(ProjectedSceneV1 scene, IImageProjector projector)
    {
        var objectsById = scene.Objects.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var expectedRefractionModel = scene.Refraction.Enabled ? AtmosphericRefraction.ModelVersion : null;
        for (var index = 0; index < scene.ResolvedFootprints!.Count; index++)
        {
            var item = scene.ResolvedFootprints[index];
            var path = $"resolvedFootprints[{index}]";
            ValidateText(item.Id, $"{path}.id");
            ValidateText(item.DisplayName, $"{path}.displayName");
            ValidateText(item.SamplingAlgorithmVersion, $"{path}.samplingAlgorithmVersion");
            if (index > 0 && StringComparer.Ordinal.Compare(scene.ResolvedFootprints[index - 1].Id, item.Id) >= 0)
                throw new ArgumentException("Resolved footprints must be unique and in ID order.", nameof(scene));
            if (!Enum.IsDefined(item.SourceKind) ||
                !string.Equals(item.ContractVersion, ProjectedResolvedFootprint.CurrentContractVersion, StringComparison.Ordinal) ||
                !string.Equals(item.RefractionModel, expectedRefractionModel, StringComparison.Ordinal))
                throw new ArgumentException("Resolved footprint contract or refraction model is invalid.", path);
            ValidateExtent(item.Extent, $"{path}.extent");
            if (!Finite(item.GeometricCenter.AltitudeDegrees, item.GeometricCenter.AzimuthDegrees,
                    item.ApparentCenter.AltitudeDegrees, item.ApparentCenter.AzimuthDegrees) ||
                item.GeometricCenter.AltitudeDegrees is < -90 or > 90 || item.GeometricCenter.AzimuthDegrees is < 0 or >= 360 ||
                item.ApparentCenter.AltitudeDegrees is < -90 or > 90 || item.ApparentCenter.AzimuthDegrees is < 0 or >= 360 ||
                item.ApparentCenter.AzimuthDegrees != item.GeometricCenter.AzimuthDegrees ||
                Math.Abs(item.ApparentCenter.AltitudeDegrees -
                    AtmosphericRefraction.Apply(item.GeometricCenter.AltitudeDegrees, scene.Refraction)) > DirectionTolerance)
                throw new ArgumentException("Resolved footprint centres must agree with the scene refraction.", path);
            var sourceCenter = scene.HorizonPolicy == HorizonPolicy.GeometricHorizon && item.GeometricCenter.AltitudeDegrees < 0
                ? null
                : projector.Project(item.ApparentCenter);
            PixelPoint? expectedCenter = sourceCenter is { } projected &&
                ProjectedSceneImageTransform.ContainsCrop(scene.ImageTransform, projected)
                    ? ProjectedSceneImageTransform.Apply(scene.ImageTransform, projected)
                    : null;
            if (expectedCenter.HasValue != item.CenterPixel.HasValue ||
                item.CenterPixel is { } center && Distance(center, expectedCenter!.Value) > PixelTolerance)
                throw new ArgumentException("Resolved footprint centre pixel must agree with the scene projection.", path);
            if (item.Parts is null || item.Parts.Count is 0 or > ProjectedResolvedFootprint.MaximumPartCount ||
                item.Parts.Any(static part => part?.Points is null) ||
                item.Parts.Sum(static part => part.Points.Count) > ProjectedResolvedFootprint.MaximumPointCount ||
                item.Bounds is null)
                throw new ArgumentException("Resolved footprint parts exceed their bounds.", path);
            ValidateParts(scene, item.Parts, path);
            if (item.Clipped == ResolvedFootprintSampler.IsSingleClosed(item.Parts) ||
                item.Bounds != ResolvedFootprintSampler.ComputeBounds(item.Parts))
                throw new ArgumentException("Resolved footprint clipping and bounds must match its parts.", path);
            if (objectsById.TryGetValue(item.Id, out var match) &&
                (match.GeometricHorizontal != item.GeometricCenter || match.ApparentHorizontal != item.ApparentCenter))
                throw new ArgumentException("A resolved footprint and its point object must share one centre.", path);
            if (item.SourceKind == ResolvedFootprintSourceKind.SolarSystemBody)
            {
                if (item.Appearance is null ||
                    !scene.Selection.SolarSystemBodies.Any(body => string.Equals(item.Id, $"solar-system:{body}", StringComparison.Ordinal)))
                    throw new ArgumentException("Solar-system footprints require a selected body and its appearance.", path);
                ValidateText(item.Appearance.EphemerisAlgorithmVersion, $"{path}.appearance.ephemerisAlgorithmVersion");
                if (!Finite(item.Appearance.DistanceKilometers, item.Appearance.IlluminatedFraction,
                        item.Appearance.BrightLimbAngleDegrees, item.Appearance.VisualMagnitude) ||
                    item.Appearance.DistanceKilometers <= 0 || item.Appearance.IlluminatedFraction is < 0 or > 1 ||
                    item.Appearance.BrightLimbAngleDegrees is <= -180 or > 180)
                    throw new ArgumentException("Resolved body appearance is invalid.", $"{path}.appearance");
            }
            else if (item.Appearance is not null)
            {
                throw new ArgumentException("Only solar-system footprints carry a body appearance.", path);
            }
        }
    }

    /// <summary>
    /// Validates the deep-sky collection: its attribution and bounds, each outline's geometry, and each object's
    /// position and representation. Every outline and deep-sky footprint belongs to exactly one placed object.
    /// </summary>
    private static void ValidateDeepSky(ProjectedSceneV1 scene, IImageProjector projector)
    {
        const string path = "deepSky";
        var section = scene.DeepSky!;
        var selection = scene.Selection.DeepSky!;
        if (!string.Equals(section.ContractVersion, ProjectedDeepSky.CurrentContractVersion, StringComparison.Ordinal))
            throw new ArgumentException("Deep-sky contract version is unsupported.", nameof(scene));
        ValidateText(section.AlgorithmVersion, $"{path}.algorithmVersion");
        ValidateText(section.SourceName, $"{path}.sourceName");
        ValidateText(section.SourceVersion, $"{path}.sourceVersion");
        ValidateText(section.SourceCommit, $"{path}.sourceCommit");
        ValidateText(section.License, $"{path}.license");
        if (section.SourceUrl is null || !section.SourceUrl.IsAbsoluteUri)
            throw new ArgumentException("Deep-sky source URL must be absolute.", nameof(scene));
        if (section.OmittedCandidateCount < 0 ||
            section.Objects is null || section.Objects.Count == 0 || section.Objects.Count > selection.MaximumObjects ||
            section.Objects.Any(static item => item is null) ||
            section.Outlines is null || section.Outlines.Count > selection.MaximumOutlines ||
            section.Outlines.Any(static item => item is null))
            throw new ArgumentException("Deep-sky collection counts exceed their bounds.", nameof(scene));

        var expectedRefractionModel = scene.Refraction.Enabled ? AtmosphericRefraction.ModelVersion : null;
        var outlinesById = new Dictionary<string, ProjectedDeepSkyOutline>(StringComparer.Ordinal);
        var outlinePoints = 0;
        for (var index = 0; index < section.Outlines.Count; index++)
        {
            var item = section.Outlines[index];
            var itemPath = $"{path}.outlines[{index}]";
            ValidateText(item.Id, $"{itemPath}.id");
            ValidateText(item.SamplingAlgorithmVersion, $"{itemPath}.samplingAlgorithmVersion");
            if (index > 0 && StringComparer.Ordinal.Compare(section.Outlines[index - 1].Id, item.Id) >= 0)
                throw new ArgumentException("Deep-sky outlines must be unique and in ID order.", nameof(scene));
            if (!string.Equals(item.ContractVersion, ProjectedDeepSkyOutline.CurrentContractVersion, StringComparison.Ordinal) ||
                !string.Equals(item.RefractionModel, expectedRefractionModel, StringComparison.Ordinal) ||
                item.Level is < DeepSkyOutline.WidestLevel or > DeepSkyOutline.NarrowestLevel ||
                item.RingCount is < 1 or > ProjectedDeepSkyOutline.MaximumPartCount)
                throw new ArgumentException("Deep-sky outline contract, refraction model, level or ring count is invalid.", itemPath);
            if (item.Parts is null || item.Parts.Count is 0 or > ProjectedDeepSkyOutline.MaximumPartCount ||
                item.Parts.Any(static part => part?.Points is null) ||
                item.Parts.Sum(static part => part.Points.Count) > ProjectedDeepSkyOutline.MaximumPointCount ||
                item.Bounds is null)
                throw new ArgumentException("Deep-sky outline parts exceed their bounds.", itemPath);
            ValidateParts(scene, item.Parts, itemPath);
            if (item.Clipped == ProjectedDeepSkyOutline.IsComplete(item.Parts, item.RingCount) ||
                item.Bounds != ResolvedFootprintSampler.ComputeBounds(item.Parts))
                throw new ArgumentException("Deep-sky outline clipping and bounds must match its parts.", itemPath);
            outlinePoints += item.Parts.Sum(static part => part.Points.Count);
            outlinesById.Add(item.Id, item);
        }
        if (outlinePoints > ProjectedDeepSky.MaximumTotalOutlinePointCount)
            throw new ArgumentException("Deep-sky outlines exceed the scene point budget.", nameof(scene));

        var footprintsById = (scene.ResolvedFootprints ?? [])
            .Where(static item => item.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject)
            .ToDictionary(static item => item.Id, StringComparer.Ordinal);
        if (footprintsById.Count > selection.MaximumFootprints)
            throw new ArgumentException("Deep-sky footprints exceed the selected bound.", nameof(scene));
        var extentSource = $"{section.SourceName} {section.SourceVersion}";
        var linkedOutlines = 0;
        var linkedFootprints = 0;
        for (var index = 0; index < section.Objects.Count; index++)
        {
            var item = section.Objects[index];
            var itemPath = $"{path}.objects[{index}]";
            ValidateText(item.Id, $"{itemPath}.id");
            ValidateText(item.Designation, $"{itemPath}.designation");
            ValidateText(item.DisplayName, $"{itemPath}.displayName");
            ValidateText(item.ObjectType, $"{itemPath}.objectType");
            if (item.CommonName is not null) ValidateText(item.CommonName, $"{itemPath}.commonName");
            if (index > 0 && StringComparer.Ordinal.Compare(section.Objects[index - 1].Id, item.Id) >= 0)
                throw new ArgumentException("Deep-sky objects must be unique and in ID order.", nameof(scene));
            if (!item.Id.StartsWith(ProjectedDeepSkyObject.IdPrefix, StringComparison.Ordinal) ||
                item.Id.Length == ProjectedDeepSkyObject.IdPrefix.Length ||
                !DeepSkyObjectTypes.IsSupported(item.ObjectType) ||
                item.MessierNumber is < 1 or > 110 || item.CaldwellNumber is < 1 or > 109 ||
                item.Featured != ProjectedDeepSkyObject.IsFeatured(item.MessierNumber, item.CaldwellNumber, item.CommonName) ||
                !Enum.IsDefined(item.Representation) || item.Degradation is { } degradation && !Enum.IsDefined(degradation))
                throw new ArgumentException("Deep-sky object identity is invalid.", itemPath);
            if (!Finite(item.J2000Equatorial.RightAscensionHours, item.J2000Equatorial.DeclinationDegrees,
                    item.GeometricHorizontal.AltitudeDegrees, item.GeometricHorizontal.AzimuthDegrees,
                    item.ApparentHorizontal.AltitudeDegrees, item.ApparentHorizontal.AzimuthDegrees) ||
                item.J2000Equatorial.RightAscensionHours is < 0 or >= 24 ||
                item.J2000Equatorial.DeclinationDegrees is < -90 or > 90 ||
                item.GeometricHorizontal.AltitudeDegrees is < -90 or > 90 || item.GeometricHorizontal.AzimuthDegrees is < 0 or >= 360 ||
                item.ApparentHorizontal.AltitudeDegrees is < -90 or > 90 || item.ApparentHorizontal.AzimuthDegrees is < 0 or >= 360 ||
                item.ApparentHorizontal.AzimuthDegrees != item.GeometricHorizontal.AzimuthDegrees ||
                Math.Abs(item.ApparentHorizontal.AltitudeDegrees -
                    AtmosphericRefraction.Apply(item.GeometricHorizontal.AltitudeDegrees, scene.Refraction)) > DirectionTolerance)
                throw new ArgumentException("Deep-sky object position must agree with the scene refraction.", itemPath);
            // The pixel follows the resolved-footprint centre rule.
            var sourcePixel = scene.HorizonPolicy == HorizonPolicy.GeometricHorizon && item.GeometricHorizontal.AltitudeDegrees < 0
                ? null
                : projector.Project(item.ApparentHorizontal);
            PixelPoint? expectedPixel = sourcePixel is { } projected &&
                ProjectedSceneImageTransform.ContainsCrop(scene.ImageTransform, projected)
                    ? ProjectedSceneImageTransform.Apply(scene.ImageTransform, projected)
                    : null;
            if (expectedPixel.HasValue != item.Pixel.HasValue ||
                item.Pixel is { } pixel && Distance(pixel, expectedPixel!.Value) > PixelTolerance)
                throw new ArgumentException("Deep-sky object pixel must agree with the scene projection.", itemPath);
            if (item.VisualMagnitude is { } visual && !double.IsFinite(visual) ||
                item.BlueMagnitude is { } blue && !double.IsFinite(blue) ||
                item.MajorAxisArcminutes is { } major && (!double.IsFinite(major) || major <= 0) ||
                item.MinorAxisArcminutes is { } minor &&
                    (item.MajorAxisArcminutes is not { } majorAxis || !double.IsFinite(minor) || minor <= 0 || minor > majorAxis) ||
                item.PositionAngleDegrees is { } angle && (!double.IsFinite(angle) || angle is < 0 or >= 180) ||
                item.MajorAxisPixels is { } size && (item.MajorAxisArcminutes is null || !double.IsFinite(size) || size <= 0))
                throw new ArgumentException("Deep-sky object magnitudes and extent are invalid.", itemPath);

            var stellar = DeepSkyObjectTypes.IsStellar(item.ObjectType);
            var glyph = item.Representation is not (DeepSkyRepresentation.Outline or DeepSkyRepresentation.Footprint);
            var valid = item.Representation switch
            {
                DeepSkyRepresentation.Outline => !stellar && item.Degradation is null &&
                    outlinesById.TryGetValue(item.Id, out var outline) && outline.Level == item.OutlineLevel,
                DeepSkyRepresentation.Footprint => !stellar && item.OutlineLevel is null &&
                    item.Degradation is null or DeepSkyDegradation.OutlineLimit &&
                    footprintsById.TryGetValue(item.Id, out var footprint) &&
                    FootprintMatches(item, footprint, extentSource, scene.EffectiveUtc),
                DeepSkyRepresentation.StellarGlyph => stellar && item.MessierNumber is not null && item.Degradation is null,
                DeepSkyRepresentation.UnknownExtentGlyph => !stellar && item.MajorAxisArcminutes is null,
                DeepSkyRepresentation.SizedGlyph => !stellar && item.MajorAxisPixels is not null,
                DeepSkyRepresentation.MinimumGlyph => !stellar && item.MajorAxisArcminutes is not null,
                _ => false
            };
            if (!valid || glyph && (item.Pixel is null || item.OutlineLevel is not null))
                throw new ArgumentException("Deep-sky object representation does not match its geometry.", itemPath);
            if (item.Representation == DeepSkyRepresentation.Outline) linkedOutlines++;
            if (item.Representation == DeepSkyRepresentation.Footprint) linkedFootprints++;
        }
        if (linkedOutlines != outlinesById.Count || linkedFootprints != footprintsById.Count)
            throw new ArgumentException("Every deep-sky outline and footprint must belong to one placed object.", nameof(scene));

        // The footprint is the catalog's extent: its source, its axes, and its shape. Equal axes are a circle, which
        // has no orientation. An ellipse's position angle is the catalog's, turned from J2000 north to north of date
        // at the scene time.
        static bool FootprintMatches(
            ProjectedDeepSkyObject item, ProjectedResolvedFootprint footprint, string source, DateTimeOffset utc) =>
            item.MajorAxisArcminutes is { } major && item.MinorAxisArcminutes is { } minor &&
            string.Equals(footprint.DisplayName, item.DisplayName, StringComparison.Ordinal) &&
            string.Equals(footprint.Extent.Source, source, StringComparison.Ordinal) &&
            footprint.GeometricCenter == item.GeometricHorizontal && footprint.ApparentCenter == item.ApparentHorizontal &&
            Math.Abs(footprint.Extent.SemiMajorAxisDegrees - major / 120d) <= DirectionTolerance &&
            Math.Abs(footprint.Extent.SemiMinorAxisDegrees - minor / 120d) <= DirectionTolerance &&
            (major == minor
                ? footprint.Extent.Shape == ResolvedFootprintShape.Circle
                : footprint.Extent.Shape == ResolvedFootprintShape.Ellipse && item.PositionAngleDegrees is { } angle &&
                    AxialDifference(footprint.Extent.PositionAngleDegrees, DeepSkySceneProjector.PositionAngleOfDate(
                        item.J2000Equatorial.RightAscensionHours, item.J2000Equatorial.DeclinationDegrees, angle, utc)) <=
                    DirectionTolerance);
    }

    /// <summary>Returns the difference in degrees between two axis orientations, which repeat every 180 degrees.</summary>
    private static double AxialDifference(double left, double right)
    {
        var difference = Math.Abs(left - right) % 180d;
        return Math.Min(difference, 180d - difference);
    }

    private static void ValidateParts(ProjectedSceneV1 scene, IReadOnlyList<ResolvedFootprintPart> parts, string path)
    {
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var part = parts[partIndex];
            if (part.Points.Count < (part.Closed ? 3 : 2) ||
                part.Points.Any(point => !Finite(point.X, point.Y) ||
                    !ContainsOutput(scene.Projection, scene.ImageTransform, point) ||
                    !Contains(scene.Projection, ProjectedSceneImageTransform.Inverse(scene.ImageTransform, point))))
                throw new ArgumentException("Resolved footprint part contains an invalid point.", $"{path}.parts[{partIndex}]");
        }
    }

    private static void ValidateExtent(ResolvedFootprintExtent value, string path)
    {
        if (value is null || !Enum.IsDefined(value.Shape) ||
            !Finite(value.SemiMajorAxisDegrees, value.SemiMinorAxisDegrees, value.PositionAngleDegrees) ||
            value.SemiMinorAxisDegrees <= 0 || value.SemiMajorAxisDegrees < value.SemiMinorAxisDegrees ||
            value.SemiMajorAxisDegrees > 90 || value.PositionAngleDegrees is < 0 or >= 180 ||
            value.Shape == ResolvedFootprintShape.Circle &&
                (value.SemiMajorAxisDegrees != value.SemiMinorAxisDegrees || value.PositionAngleDegrees != 0) ||
            new[] { value.SemiMajorAxisUncertaintyDegrees, value.SemiMinorAxisUncertaintyDegrees, value.PositionAngleUncertaintyDegrees }
                .Any(static uncertainty => uncertainty is { } quantified && (!double.IsFinite(quantified) || quantified < 0)))
            throw new ArgumentException("Resolved footprint extent is invalid.", path);
        ValidateText(value.Source, $"{path}.source");
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
                value.FocalLengthYPixels, value.BoresightAltitudeDegrees, value.BoresightAzimuthDegrees, value.RollDegrees,
                value.RadialDistortionK1) ||
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
        value.DeepSky?.Validate();
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
        value.RollDegrees, value.HorizontalFlip, value.EnforceSensorBounds, value.RadialDistortionK1);

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
            selection.TryGetProperty("deepSky", out var deepSkySelection) && !HasProperties(deepSkySelection,
                "maximumObjects", "maximumFootprints", "maximumOutlines", "preferredOutlineLevel", "minimumGlyphPixels") ||
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
        if (root.TryGetProperty("resolvedFootprints", out var footprints))
        {
            if (footprints.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in footprints.EnumerateArray())
            {
                if (!HasProperties(item, "id", "displayName", "sourceKind", "contractVersion", "samplingAlgorithmVersion",
                        "extent", "geometricCenter", "apparentCenter", "refractionModel", "centerPixel", "clipped", "bounds",
                        "parts", "appearance") ||
                    !Point(item, "extent", "shape", "semiMajorAxisDegrees", "semiMinorAxisDegrees", "positionAngleDegrees",
                        "semiMajorAxisUncertaintyDegrees", "semiMinorAxisUncertaintyDegrees", "positionAngleUncertaintyDegrees",
                        "source") ||
                    !Point(item, "geometricCenter", "altitudeDegrees", "azimuthDegrees") ||
                    !Point(item, "apparentCenter", "altitudeDegrees", "azimuthDegrees") ||
                    !Point(item, "bounds", "minX", "minY", "maxX", "maxY") ||
                    item.GetProperty("centerPixel") is { ValueKind: not JsonValueKind.Null } centerPixel &&
                        !HasProperties(centerPixel, "x", "y") ||
                    item.GetProperty("appearance") is { ValueKind: not JsonValueKind.Null } appearance &&
                        !HasProperties(appearance, "ephemerisAlgorithmVersion", "distanceKilometers", "illuminatedFraction",
                            "brightLimbAngleDegrees", "visualMagnitude") ||
                    !Array(item, "parts", out var parts)) return false;
                if (!HasParts(parts)) return false;
            }
        }
        if (root.TryGetProperty("deepSky", out var deepSky))
        {
            if (!HasProperties(deepSky, "contractVersion", "algorithmVersion", "sourceName", "sourceVersion", "sourceCommit",
                    "sourceUrl", "license", "omittedCandidateCount", "objects", "outlines") ||
                !Array(deepSky, "objects", out var deepSkyObjects) || !Array(deepSky, "outlines", out var outlines)) return false;
            foreach (var item in deepSkyObjects.EnumerateArray())
            {
                if (!HasProperties(item, "id", "designation", "displayName", "objectType", "commonName", "messierNumber",
                        "caldwellNumber", "featured", "j2000Equatorial", "geometricHorizontal", "apparentHorizontal", "pixel",
                        "visualMagnitude", "blueMagnitude", "majorAxisArcminutes", "minorAxisArcminutes", "positionAngleDegrees",
                        "majorAxisPixels", "representation", "outlineLevel", "degradation") ||
                    !Point(item, "j2000Equatorial", "rightAscensionHours", "declinationDegrees") ||
                    !Point(item, "geometricHorizontal", "altitudeDegrees", "azimuthDegrees") ||
                    !Point(item, "apparentHorizontal", "altitudeDegrees", "azimuthDegrees") ||
                    item.GetProperty("pixel") is { ValueKind: not JsonValueKind.Null } pixel && !HasProperties(pixel, "x", "y"))
                    return false;
            }
            foreach (var item in outlines.EnumerateArray())
            {
                if (!HasProperties(item, "id", "contractVersion", "samplingAlgorithmVersion", "level", "ringCount",
                        "refractionModel", "clipped", "bounds", "parts") ||
                    !Point(item, "bounds", "minX", "minY", "maxX", "maxY") ||
                    !Array(item, "parts", out var parts) || !HasParts(parts)) return false;
            }
        }
        return true;

        static bool Point(JsonElement parent, string name, params string[] properties) =>
            Object(parent, name, out var point) && HasProperties(point, properties);

        static bool HasParts(JsonElement parts)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (!HasProperties(part, "closed", "points") || !Array(part, "points", out var points) ||
                    points.EnumerateArray().Any(static point => !HasProperties(point, "x", "y"))) return false;
            }
            return true;
        }
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

    /// <summary>Rounds generated footprint geometry, removing points that rounding makes coincident.</summary>
    private static ProjectedResolvedFootprint? NormalizeGeneratedGeometry(ProjectedResolvedFootprint value)
    {
        var parts = NormalizeParts(value.Parts);
        if (parts.Count == 0) return null;
        var extent = value.Extent;
        return value with
        {
            Extent = extent with
            {
                SemiMajorAxisDegrees = Normalize(extent.SemiMajorAxisDegrees),
                SemiMinorAxisDegrees = Normalize(extent.SemiMinorAxisDegrees),
                PositionAngleDegrees = NormalizePeriodic(extent.PositionAngleDegrees, 180),
                SemiMajorAxisUncertaintyDegrees = extent.SemiMajorAxisUncertaintyDegrees is { } major ? Normalize(major) : null,
                SemiMinorAxisUncertaintyDegrees = extent.SemiMinorAxisUncertaintyDegrees is { } minor ? Normalize(minor) : null,
                PositionAngleUncertaintyDegrees = extent.PositionAngleUncertaintyDegrees is { } angle ? Normalize(angle) : null
            },
            GeometricCenter = Normalize(value.GeometricCenter),
            ApparentCenter = Normalize(value.ApparentCenter),
            CenterPixel = value.CenterPixel is { } center ? Normalize(center) : null,
            Clipped = !ResolvedFootprintSampler.IsSingleClosed(parts),
            Bounds = ResolvedFootprintSampler.ComputeBounds(parts),
            Parts = Freeze(parts),
            Appearance = value.Appearance is { } appearance ? appearance with
            {
                DistanceKilometers = Normalize(appearance.DistanceKilometers),
                IlluminatedFraction = Normalize(appearance.IlluminatedFraction),
                BrightLimbAngleDegrees = Normalize(appearance.BrightLimbAngleDegrees),
                VisualMagnitude = Normalize(appearance.VisualMagnitude)
            } : null
        };
    }

    /// <summary>
    /// Rounds a generated deep-sky collection and reconciles it with the normalized footprints. An object whose
    /// outline or footprint the image transform or rounding removed keeps a glyph at its pixel, or is dropped when it
    /// has none. Returns null when no object remains, so the scene stays v1 or v2.
    /// </summary>
    private static ProjectedDeepSky? NormalizeGeneratedGeometry(
        ProjectedDeepSky value,
        IReadOnlyList<ProjectedResolvedFootprint> footprints,
        ProjectedSceneDeepSkySelection selection)
    {
        var outlines = value.Outlines.Select(NormalizeGeneratedGeometry).OfType<ProjectedDeepSkyOutline>()
            .ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var footprintIds = footprints.Where(static item => item.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject)
            .Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        var objects = new List<ProjectedDeepSkyObject>();
        foreach (var source in value.Objects)
        {
            var item = NormalizeGeneratedGeometry(source);
            var drawn = item.Representation switch
            {
                DeepSkyRepresentation.Outline => outlines.ContainsKey(item.Id),
                DeepSkyRepresentation.Footprint => footprintIds.Contains(item.Id),
                _ => true
            };
            if (!drawn)
            {
                item = item with
                {
                    Representation = DeepSkySceneProjector.GlyphRepresentation(
                        item.ObjectType, item.MajorAxisArcminutes, item.MajorAxisPixels, selection.MinimumGlyphPixels),
                    OutlineLevel = null
                };
            }
            if (item.Pixel is null && item.Representation is not (DeepSkyRepresentation.Outline or DeepSkyRepresentation.Footprint))
                continue;
            objects.Add(item);
        }
        if (objects.Count == 0) return null;
        return value with
        {
            Objects = Freeze(objects.OrderBy(static item => item.Id, StringComparer.Ordinal)),
            Outlines = Freeze(outlines.Values.OrderBy(static item => item.Id, StringComparer.Ordinal))
        };
    }

    private static ProjectedDeepSkyObject NormalizeGeneratedGeometry(ProjectedDeepSkyObject value) => value with
    {
        J2000Equatorial = Normalize(value.J2000Equatorial),
        GeometricHorizontal = Normalize(value.GeometricHorizontal),
        ApparentHorizontal = Normalize(value.ApparentHorizontal),
        Pixel = value.Pixel is { } pixel ? Normalize(pixel) : null,
        MajorAxisPixels = value.MajorAxisPixels is { } size ? Normalize(size) : null
    };

    /// <summary>Rounds a generated outline; it stays unclipped only when every ring survives as one closed part.</summary>
    private static ProjectedDeepSkyOutline? NormalizeGeneratedGeometry(ProjectedDeepSkyOutline value)
    {
        var parts = NormalizeParts(value.Parts);
        if (parts.Count == 0) return null;
        return value with
        {
            Clipped = !ProjectedDeepSkyOutline.IsComplete(parts, value.RingCount),
            Bounds = ResolvedFootprintSampler.ComputeBounds(parts),
            Parts = Freeze(parts)
        };
    }

    private static List<ResolvedFootprintPart> NormalizeParts(IEnumerable<ResolvedFootprintPart> source)
    {
        var parts = new List<ResolvedFootprintPart>();
        foreach (var part in source)
        {
            var points = new List<PixelPoint>();
            foreach (var point in part.Points.Select(Normalize))
            {
                if (points.Count == 0 || points[^1] != point) points.Add(point);
            }
            var closed = part.Closed;
            if (closed && points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
            if (closed && points.Count < 3) closed = false;
            if (points.Count >= 2) parts.Add(new ResolvedFootprintPart(closed, Freeze(points)));
        }
        return parts;
    }

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
        Segments = Freeze(scene.Segments),
        ResolvedFootprints = scene.ResolvedFootprints is null ? null : Freeze(scene.ResolvedFootprints.Select(static item => item with
        {
            Parts = Freeze(item.Parts.Select(static part => part with { Points = Freeze(part.Points) }))
        })),
        DeepSky = scene.DeepSky is null ? null : scene.DeepSky with
        {
            Objects = Freeze(scene.DeepSky.Objects),
            Outlines = Freeze(scene.DeepSky.Outlines.Select(static item => item with
            {
                Parts = Freeze(item.Parts.Select(static part => part with { Points = Freeze(part.Points) }))
            }))
        }
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
