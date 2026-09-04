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

/// <summary>Numeric image geometry of the active rig after its configured readout transform.</summary>
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
    IReadOnlyList<CameraAgentSkyMapCardinal> Cardinals);

/// <summary>One visible catalog object with its horizontal direction and projected pixel.</summary>
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
public sealed record CameraAgentSkyMapConstellation(
    string ConstellationId,
    int VisibleSegmentCount);

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
public sealed record CameraAgentSkyMapProjectionResult(
    DateTimeOffset AtUtc,
    CameraAgentSkyMapCatalogIdentity Catalog,
    CameraAgentSkyMapObserver Observer,
    CameraAgentSkyMapGeometry Geometry,
    IReadOnlyList<CameraAgentSkyMapObject> Objects,
    IReadOnlyList<CameraAgentSkyMapConstellation> Constellations,
    int MaximumObjects,
    bool ObjectsAtBound,
    double MaximumMagnitude,
    string AstronomyAlgorithmVersion,
    string Summary,
    CameraAgentSkyMapCaptureProvenance? LatestCaptureScene);
