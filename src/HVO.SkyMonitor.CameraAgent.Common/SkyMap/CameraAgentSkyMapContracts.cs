using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.SkyMap;

/// <summary>How the active deployment coordinates entered the protected local history.</summary>
public enum CameraAgentSkyMapLocationOrigin
{
    /// <summary>Version one: the coordinates this agent was seeded with by its startup configuration.</summary>
    StartupSeed,

    /// <summary>A later version that superseded the seed, from an updated startup seed or a central acknowledgement.</summary>
    SucceededVersion
}

/// <summary>
/// Identity of the installed read-only catalog snapshot. Only provenance the
/// catalog already publishes appears here; no install root, database file, or
/// any other local storage path is carried.
/// </summary>
public sealed record CameraAgentSkyMapCatalogIdentity(
    string Name,
    string Version,
    string ChecksumSha256,
    string License,
    string SchemaVersion,
    string PreprocessingVersion);

/// <summary>
/// The authoritative observer coordinates for this agent. They are read-only in
/// this projection: the versioned local configuration owner is the only writer.
/// </summary>
public sealed record CameraAgentSkyMapObserver(
    string LocationId,
    long Version,
    string CanonicalSha256,
    string Source,
    DeploymentLocationSourceKind SourceKind,
    CameraAgentSkyMapLocationOrigin Origin,
    bool StagedAcknowledgementPending,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationMeters,
    string TimeZoneId,
    double? HorizontalAccuracyMeters,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    bool EffectiveAtInstant);

/// <summary>One cardinal horizon direction and where the calibrated optics place it in the image.</summary>
public sealed record CameraAgentSkyMapCardinal(
    string Name,
    double AzimuthDegrees,
    double? PixelX,
    double? PixelY);

/// <summary>One point in the active rig's output image, in pixels from the top-left corner.</summary>
public sealed record CameraAgentSkyMapPoint(double X, double Y);

/// <summary>
/// One connected run of image points. Points are rounded to a tenth of a pixel and simplified to within one
/// pixel of the exact projected curve, so a figure stays small enough to send with every projection.
/// </summary>
public sealed record CameraAgentSkyMapPolyline(IReadOnlyList<CameraAgentSkyMapPoint> Points);

/// <summary>
/// One circle of constant altitude as the calibrated optics place it in the image. A tilted or cropped rig
/// can split a circle into several runs, and an altitude the image never reaches has no lines.
/// </summary>
public sealed record CameraAgentSkyMapAltitudeRing(
    double AltitudeDegrees,
    IReadOnlyList<CameraAgentSkyMapPolyline> Lines);

/// <summary>Numeric image geometry of the active rig after its configured readout transform.</summary>
/// <param name="Cardinals">
/// Where each horizon compass direction meets the image-circle edge, or null pixels when the aperture is
/// rectangular and the edge is not a circle.
/// </param>
/// <param name="AltitudeRings">The horizon and the 30 and 60 degree altitude circles projected into the image.</param>
public sealed record CameraAgentSkyMapGeometry(
    string ProjectionModel,
    string Aperture,
    double BoresightAltitudeDegrees,
    double BoresightAzimuthDegrees,
    double RollDegrees,
    bool HorizontalFlip,
    double FieldOfViewDegrees,
    double? VerticalFieldOfViewDegrees,
    double PrincipalPointX,
    double PrincipalPointY,
    double FocalLengthXPixels,
    double FocalLengthYPixels,
    int WidthPixels,
    int HeightPixels,
    double? ImageCircleRadiusPixels,
    string RigProfileVersion,
    string RigProfileHashSha256,
    string ProjectionCalibrationVersion,
    string ProjectionAlgorithmVersion,
    IReadOnlyList<CameraAgentSkyMapCardinal> Cardinals,
    IReadOnlyList<CameraAgentSkyMapAltitudeRing> AltitudeRings,
    double RadialDistortionK1 = 0);

/// <summary>One visible object with its horizontal direction and projected pixel.</summary>
/// <param name="Kind">
/// <c>Star</c> for a catalog object, or <c>Sun</c>, <c>Moon</c> or <c>Planet</c> for a solar-system body placed
/// by the planet ephemeris.
/// </param>
public sealed record CameraAgentSkyMapObject(
    string Id,
    string DisplayName,
    string Kind,
    double Magnitude,
    double AltitudeDegrees,
    double AzimuthDegrees,
    double PixelX,
    double PixelY,
    string? HipparcosId);

/// <summary>One constellation with at least one chord inside the calibrated image.</summary>
/// <param name="VisibleSegmentCount">The number of clipped projected chords inside the image.</param>
/// <param name="Lines">
/// The visible parts of the figure's stick lines in image pixels: consecutive chords of one figure segment are
/// joined into a single run and simplified, so the payload grows with the figure rather than with the chord
/// subdivision.
/// </param>
public sealed record CameraAgentSkyMapConstellation(
    string ConstellationId,
    int VisibleSegmentCount,
    IReadOnlyList<CameraAgentSkyMapPolyline> Lines);

/// <summary>
/// Capture-time scene provenance identity of the newest retained frame, when the
/// in-memory latest-frame evidence carries one. Identity only; never a path.
/// </summary>
public sealed record CameraAgentSkyMapCaptureProvenance(
    string SceneId,
    DateTimeOffset? SceneUtc,
    string CatalogName,
    string CatalogVersion,
    string CatalogChecksumSha256,
    string ProjectionModel,
    string RigProfileVersion,
    string? RigProfileHashSha256,
    string? ProjectionCalibrationVersion,
    string? ProjectedSceneStageIdentitySha256);

/// <summary>
/// A bounded, offline, deterministic map/catalog projection for one UTC instant,
/// the active deployment location, and the active rig.
/// </summary>
/// <param name="EphemerisModelVersion">
/// The planet ephemeris model that placed the solar-system bodies, or null when no ephemeris is installed and
/// the scene holds catalog stars only.
/// </param>
/// <param name="Objects">
/// The visible catalog stars, at most <paramref name="MaximumObjects"/> of them, plus every solar-system body
/// above the horizon and inside the image. Bodies are exempt from the object bound and the limiting magnitude.
/// </param>
/// <param name="Constellations">The installed constellation figures with at least one chord inside the image.</param>
/// <param name="InstalledConstellationIds">Every constellation figure the installed topology defines.</param>
/// <param name="ObjectsAtBound">True when more catalog stars were visible than the bound admits.</param>
public sealed record CameraAgentSkyMapProjectionResult(
    DateTimeOffset AtUtc,
    CameraAgentSkyMapCatalogIdentity Catalog,
    string? EphemerisModelVersion,
    CameraAgentSkyMapObserver Observer,
    CameraAgentSkyMapGeometry Geometry,
    IReadOnlyList<CameraAgentSkyMapObject> Objects,
    IReadOnlyList<CameraAgentSkyMapConstellation> Constellations,
    IReadOnlyList<string> InstalledConstellationIds,
    int MaximumObjects,
    bool ObjectsAtBound,
    double MaximumMagnitude,
    string AstronomyAlgorithmVersion,
    string Summary,
    CameraAgentSkyMapCaptureProvenance? LatestCaptureScene);
