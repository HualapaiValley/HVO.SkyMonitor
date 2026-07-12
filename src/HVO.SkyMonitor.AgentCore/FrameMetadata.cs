using System;
using System.Collections.Generic;

namespace HVO.SkyMonitor.AgentCore;

public sealed record FrameMetadata(
    TimeSpan Exposure,
    double Gain,
    double TemperatureC,
    string? SourceId = null,
    IReadOnlyDictionary<string, string>? Extra = null,
    double? Offset = null,
    SceneProvenance? Scene = null);

/// <summary>
/// Immutable identifiers needed to reproduce a catalog-backed frame or derivative.
/// Values identify algorithms and inputs; this contract owns no external resources and is thread-safe.
/// </summary>
public sealed record SceneProvenance(
    string SceneId,
    string RigProfileVersion,
    string CatalogName,
    string CatalogVersion,
    string CatalogChecksumSha256,
    string ProjectionModel,
    string ProjectionAlgorithmVersion,
    string AstronomyAlgorithmVersion,
    string SensorRecipeVersion,
    Uri? CatalogSourceUrl = null,
    string? CatalogLicense = null,
    string? CatalogSchemaVersion = null,
    string? CatalogPreprocessingVersion = null,
    IReadOnlyList<ProjectedObjectProvenance>? Objects = null,
    IReadOnlyList<ProjectedSegmentProvenance>? Segments = null,
    string? EphemerisModelVersion = null,
    string? ConstellationTopologyVersion = null,
    Uri? ConstellationTopologySourceUrl = null,
    string? ConstellationTopologySha256 = null,
    string? ConstellationTopologyLicense = null,
    string? ConstellationTopologyPreprocessingVersion = null);

/// <summary>
/// A projected scene object in continuous sensor pixel-edge coordinates.
/// This immutable transport record owns no resources and is thread-safe.
/// </summary>
public sealed record ProjectedObjectProvenance(
    string Id,
    string DisplayName,
    double PixelX,
    double PixelY,
    double Magnitude);

/// <summary>A projected derivative segment using stable object identifiers and continuous sensor pixels.</summary>
public sealed record ProjectedSegmentProvenance(
    string ConstellationId,
    string FromObjectId,
    string ToObjectId,
    double FromPixelX,
    double FromPixelY,
    double ToPixelX,
    double ToPixelY);
