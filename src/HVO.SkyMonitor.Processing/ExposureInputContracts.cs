using System.Collections.Immutable;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>Output layout and its binding to native photosite and optical geometry.</summary>
/// <remarks>Coordinates use delivered pixel edges, with sample centers at index plus one half.</remarks>
public sealed record ExposureGeometryV1(
    FrameLayoutDescriptor Layout,
    RigOrientation Orientation,
    string RigIdentitySha256,
    string OpticalCalibrationVersion,
    double? PixelScaleArcseconds);

/// <summary>Immutable raw identity, actual acquisition facts, and geometry.</summary>
public sealed record ExposureInputV1(
    ExposureKey Key,
    ReconstructionDescriptor Raw,
    ExposureGeometryV1 Geometry);

public enum LinearExposureDisposition
{
    Pending,
    Calibrated,
    QualifiedNormalizationOnly,
    Unsupported,
    Unavailable,
    Failed,
    Expired
}

/// <summary>White threshold in the named source's stored container sample space.</summary>
public sealed record ExposureWhiteThreshold(Guid ArtifactId, double StoredWhiteLevel);

/// <summary>Verified relationship between an exact linear artifact and its original raw input.</summary>
public sealed record LinearInputProvenanceV1(
    LinearExposureDisposition Disposition,
    Guid RawArtifactId,
    string RawDescriptorSha256,
    string RawPayloadSha256,
    Guid LinearArtifactId,
    string LinearDescriptorSha256,
    string LinearPayloadSha256,
    string OutputIdentitySha256,
    ProcessingRecipeIdentity Recipe,
    ImmutableArray<Guid> CalibrationArtifactIds,
    ExposureWhiteThreshold RawWhiteThreshold,
    ExposureWhiteThreshold LinearWhiteThreshold)
{
    public string? CalibrationBundleIdentitySha256 { get; init; }

    public ImmutableArray<string> CalibrationPayloadHashes { get; init; } = [];

    public string? Reason { get; init; }
}

/// <summary>Borrowed pixel data from an exact qualified product, with its original exposure evidence.</summary>
public sealed record LinearExposureInputV1(
    ExposureInputV1 Exposure,
    ProcessingArtifact Linear,
    LinearInputProvenanceV1 Provenance);
