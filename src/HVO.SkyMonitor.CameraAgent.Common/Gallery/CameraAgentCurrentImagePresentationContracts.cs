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

/// <summary>How a stage's displayed pixels relate to the stage artifact named by <c>ArtifactId</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentPresentationDisplayBasis>))]
public enum CameraAgentPresentationDisplayBasis
{
    /// <summary>The preview is the stage artifact's own encoded bytes, or is derived on demand from its own pixels
    /// under the explicit reference policy, or global defaults when no reference is selected.</summary>
    OwnArtifact,

    /// <summary>The preview is a retained, lineage-matched display derivative whose only source is the stage
    /// artifact; the stage artifact itself remains the linear identity and download target.</summary>
    RetainedDerivative
}

/// <summary>
/// A slot names two things that are usually the same artifact. <c>ArtifactId</c> is the stage identity: the
/// artifact whose bytes the stage represents and whose content is downloaded. <c>DisplayArtifactId</c> is the
/// artifact whose preview is rendered. They differ only for <see cref="CameraAgentPresentationDisplayBasis.RetainedDerivative"/>,
/// where the linear frame stays the identity and its retained display derivative supplies the pixels.
/// <c>DisplayPolicy</c> names the display transfer that produced the shown pixels; it is never a calibration.
/// <c>DisplayReferenceId</c> identifies the retained recipe supplying settings, never replacement source pixels.
/// </summary>
public sealed record CameraAgentPresentationSlot(
    CameraAgentPresentationStage Stage,
    string Label,
    CameraAgentPresentationSlotAvailability Availability,
    string Reason,
    Guid? ArtifactId = null,
    FrameArtifactRole? ArtifactRole = null,
    string? Variant = null,
    string? MediaType = null,
    Uri? PreviewUrl = null,
    Guid? DisplayArtifactId = null,
    CameraAgentPresentationDisplayBasis DisplayBasis = CameraAgentPresentationDisplayBasis.OwnArtifact,
    string? DisplayPolicy = null,
    Guid? DisplayReferenceId = null,
    CameraAgentPreviewOperation DisplayOperation = CameraAgentPreviewOperation.Unknown);

public sealed record CameraAgentCapturePresentation(
    CameraAgentPresentationStage? SelectedStage,
    IReadOnlyList<CameraAgentPresentationSlot> Stages);

public interface ICameraAgentCapturePresentationProjector
{
    /// <summary>Projects every stage onto its own artifact's preview. Safe for consumers that do not validate previews.</summary>
    CameraAgentCapturePresentation Project(CameraAgentGalleryCapture? capture);

    /// <summary>
    /// Projects stages whose Combined display may be a lineage-matched retained derivative instead of the linear
    /// frame's on-demand preview. Only a consumer that validates each slot's <c>DisplayArtifactId</c> and
    /// re-projects on failure may use this; a corrupt or missing derivative must fall back to the stage artifact
    /// rather than surface as a broken stage.
    /// </summary>
    CameraAgentCapturePresentation ProjectWithRetainedDisplay(CameraAgentGalleryCapture? capture);
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

    /// <summary>Validates the supplied capture's display stages with at most 12 preview checks,
    /// without reading current capture history or runtime state.</summary>
    ValueTask<CameraAgentCapturePresentation> ProjectCaptureAsync(
        CameraAgentGalleryCapture capture,
        CancellationToken cancellationToken);
}
