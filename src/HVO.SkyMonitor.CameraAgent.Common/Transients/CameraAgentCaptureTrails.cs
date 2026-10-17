using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

/// <summary>A bounded immutable projection of measured evidence belonging to one exact raw capture.</summary>
public sealed record CameraAgentCaptureTrails(
    Guid CaptureId,
    string State,
    IReadOnlyList<CameraAgentCaptureTrail> Trails,
    bool Incomplete,
    string? Reason = null)
{
    public const string SchemaVersion = "cameraagent-capture-trails-v1";
    public const int MaximumTrails = 32;
}

/// <summary>Geometry and conversion are authenticated by their retained extraction/source identities.</summary>
public sealed record CameraAgentCaptureTrail(
    Guid CandidateId,
    Guid EventId,
    string State,
    string CandidateIdentitySha256,
    string ExtractionIdentitySha256,
    string? AssessmentIdentitySha256,
    string? FinalizationIdentitySha256,
    ArtifactManifestV2 SourceManifest,
    TransientDetectorInputDescriptorV1 DetectorInput,
    TransientGeometryV1 Geometry);
