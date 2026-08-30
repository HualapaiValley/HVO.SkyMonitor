using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentPresentationStage>))]
public enum CameraAgentPresentationStage
{
    Raw,
    Calibrated,
    Combined,
    Preview,
    Annotated
}

[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentPresentationSlotAvailability>))]
public enum CameraAgentPresentationSlotAvailability
{
    Available,
    Missing,
    Unsupported,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentPresentationImageFreshness>))]
public enum CameraAgentPresentationImageFreshness
{
    Empty,
    Current,
    Delayed,
    Stale,
    Historical
}

[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentPresentationSystemState>))]
public enum CameraAgentPresentationSystemState
{
    Starting,
    Capturing,
    Standby,
    Paused,
    Unavailable
}

public sealed record CameraAgentPresentationSystemStatus(
    CameraAgentPresentationSystemState State,
    string Message,
    DateTimeOffset ObservedUtc,
    DateTimeOffset? NextTransitionUtc = null);

public sealed record CameraAgentPresentationCapture(
    Guid CaptureId,
    long CaptureSequence,
    DateTimeOffset ExposureStartedUtc,
    long AgeSeconds,
    GalleryEvidenceOrigin EvidenceOrigin);

public sealed record CameraAgentPresentationSlot(
    CameraAgentPresentationStage Stage,
    string Label,
    CameraAgentPresentationSlotAvailability Availability,
    string Reason,
    Guid? ArtifactId = null,
    FrameArtifactRole? ArtifactRole = null,
    string? Variant = null,
    string? MediaType = null,
    Uri? PreviewUrl = null);

public sealed record CameraAgentCapturePresentation(
    CameraAgentPresentationStage? SelectedStage,
    IReadOnlyList<CameraAgentPresentationSlot> Stages);

public interface ICameraAgentCapturePresentationProjector
{
    CameraAgentCapturePresentation Project(CameraAgentGalleryCapture? capture);
}

public sealed record CameraAgentCurrentImagePresentation(
    DateTimeOffset ObservedUtc,
    CameraAgentPresentationImageFreshness ImageFreshness,
    CameraAgentPresentationSystemStatus System,
    CameraAgentPresentationCapture? LatestCapture,
    CameraAgentPresentationCapture? DisplayCapture,
    bool IsHistoricalFallback,
    CameraAgentPresentationStage? SelectedStage,
    IReadOnlyList<CameraAgentPresentationSlot> Stages,
    bool StructuredLayersAvailable,
    bool HistoryBoundReached);

public interface ICameraAgentCurrentImagePresentationService
{
    ValueTask<CameraAgentCurrentImagePresentation> GetAsync(CancellationToken cancellationToken);
}
