using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public enum GalleryEvidenceOrigin
{
    Unknown,
    Simulated,
    DeveloperFixture
}

public sealed record CameraAgentGalleryQuery(
    int? PageSize = null,
    string? Cursor = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    long? MinimumSequence = null,
    long? MaximumSequence = null,
    string? RawState = null,
    GalleryEvidenceOrigin? EvidenceOrigin = null,
    FrameArtifactRole? ProcessingRole = null,
    string? Recipe = null,
    string? ProcessingStatus = null);

public sealed record CameraAgentGalleryPage(
    IReadOnlyList<CameraAgentGalleryCapture> Items,
    string? NextCursor);

public sealed record CameraAgentGalleryCapture(
    Guid CaptureId,
    string AgentId,
    string? RigId,
    long CaptureSequence,
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset DurableIngressUtc,
    string RawState,
    GalleryEvidenceOrigin EvidenceOrigin,
    IReadOnlyList<CameraAgentGalleryArtifact> Artifacts,
    IReadOnlyList<CameraAgentGalleryProcessingNode> ProcessingNodes,
    CameraAgentGalleryCaptureDetail? Detail = null,
    bool ProcessingNodesTruncated = false,
    bool ArtifactsTruncated = false,
    bool ProcessingProjectionUnavailable = false,
    string CanonicalSceneAvailability = "Unavailable");

public sealed record CameraAgentGalleryCaptureDetail(
    string EvidenceAvailability,
    string? ManifestSchemaVersion,
    CameraAgentGalleryLayout? Layout,
    CameraAgentGalleryTiming? Timing,
    CameraAgentGalleryControls? Controls,
    bool? RawRetentionHold,
    IReadOnlyList<CameraAgentGalleryArtifactState> ArtifactStates,
    IReadOnlyList<CameraAgentGalleryProcessingNodeDetail> ProcessingNodes,
    CameraAgentGalleryCloudAssessment CloudAssessment,
    ProfileIdentityDescriptor? ProcessingProfile = null);

public sealed record CameraAgentGalleryLayout(
    int Width,
    int Height,
    int StrideBytes,
    string PixelFormat,
    string ByteOrder,
    int SampleDepthBits,
    int ContainerDepthBits,
    string Packing,
    string CfaPattern,
    double? BlackLevel,
    double? WhiteLevel,
    long ByteLength);

public sealed record CameraAgentGalleryTiming(
    DateTimeOffset RequestedStartUtc,
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset ExposureEndedUtc,
    DateTimeOffset ReadoutCompletedUtc,
    DateTimeOffset DurableIngressUtc,
    DateTimeOffset? SetpointAppliedUtc);

public sealed record CameraAgentGalleryControls(
    double RequestedExposureMilliseconds,
    double EffectiveExposureMilliseconds,
    double RequestedGain,
    double EffectiveGain,
    double? RequestedOffset,
    double? EffectiveOffset,
    double? TemperatureSetpointC,
    double? EffectiveTemperatureC);

public sealed record CameraAgentGalleryArtifactState(
    Guid ArtifactId,
    string RetentionState,
    string DeliveryAvailability,
    IReadOnlyList<CameraAgentGalleryArtifactDelivery> DeliveryStatuses);

public sealed record CameraAgentGalleryArtifactDelivery(string StorageAlias, string Status);

public sealed record CameraAgentGalleryProcessingNodeDetail(
    string NodeId,
    IReadOnlyList<string> Dependencies,
    int Attempt,
    DateTimeOffset CompletedUtc,
    string? FailureCategory,
    string? ProcessingProfileIdentitySha256 = null,
    DateTimeOffset? StartedUtc = null,
    double? DurationMilliseconds = null,
    string? Outcome = null,
    IReadOnlyList<CameraAgentGalleryProcessingNodeInput>? Inputs = null,
    bool InputsTruncated = false);

public sealed record CameraAgentGalleryProcessingNodeInput(
    int Ordinal,
    string Kind,
    string? Name,
    Guid? ArtifactId,
    FrameArtifactRole? Role,
    string? Variant,
    string? RecipeIdentitySha256,
    string? SchemaVersion,
    string? IdentitySha256,
    bool Selected);

public sealed record CameraAgentGalleryCloudAssessment(
    string Availability,
    string? Status,
    string? Quality,
    int? CoverageMillionths,
    int? ConfidenceMillionths,
    IReadOnlyList<string> ReasonCodes,
    bool? MaskPresent,
    int? MaskWidth,
    int? MaskHeight,
    string? MaskChecksumSha256);

public sealed record CameraAgentGalleryArtifact(
    Guid ArtifactId,
    FrameArtifactRole Role,
    string? SourceId,
    string? Variant,
    DateTimeOffset? CreatedUtc,
    string? MediaType,
    string ChecksumSha256,
    long? ByteLength,
    CameraAgentGalleryRecipe? Recipe,
    IReadOnlyList<Guid> SourceArtifactIds,
    string? ProcessingNodeId,
    IReadOnlyList<CameraAgentGalleryAlgorithm>? Algorithms = null,
    string? ProductKind = null,
    string? ProductSchemaVersion = null,
    string? ContentIdentitySha256 = null,
    string Availability = "Available",
    string? AvailabilityReason = null,
    int? EncodedWidth = null,
    int? EncodedHeight = null,
    CameraPixelFormat? PixelFormat = null,
    bool PreviewReconstructionSupported = false);

public sealed record CameraAgentGalleryAlgorithm(string Name, string Version);

public sealed record CameraAgentGalleryRecipe(
    string Name,
    string SemanticVersion,
    string ImplementationVersion,
    string OptionsSha256,
    string IdentitySha256);

public sealed record CameraAgentGalleryProcessingNode(
    string NodeId,
    bool Required,
    string Status,
    string? RecipeName,
    FrameArtifactRole? OutputRole,
    string? OutputVariant,
    IReadOnlyList<Guid> ArtifactIds);

public interface ICameraAgentGallery
{
    ValueTask<CameraAgentGalleryPage> GetPageAsync(
        CameraAgentGalleryQuery query,
        CancellationToken cancellationToken);

    ValueTask<CameraAgentGalleryCapture?> GetCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken);
}

public sealed class CameraAgentGalleryQueryException : Exception
{
    public CameraAgentGalleryQueryException()
    {
    }

    public CameraAgentGalleryQueryException(string message)
        : base(message)
    {
    }

    public CameraAgentGalleryQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

// Archive read models added for the Current Sky and Archive workflows. They
// are separate from ICameraAgentGallery so existing gallery consumers and
// test doubles keep their narrow surface.
public sealed record CameraAgentGalleryCalendarQuery(
    DateOnly FromDate,
    DateOnly ToDate,
    CameraAgentGalleryQuery? Filters = null);

public sealed record CameraAgentGalleryCalendar(
    string TimeZoneId,
    bool TimeZoneFallback,
    IReadOnlyList<CameraAgentGalleryCalendarDay> Days);

public sealed record CameraAgentGalleryCalendarDay(
    ObservingDay Day,
    long CaptureCount,
    long CandidateCount,
    DateTimeOffset? FirstExposureUtc,
    DateTimeOffset? LastExposureUtc);

// Neighbours follow the gallery order under the same filters: the newer
// capture precedes and the older capture follows the current one.
public sealed record CameraAgentGalleryNeighbours(
    Guid CaptureId,
    Guid? NewerCaptureId,
    Guid? OlderCaptureId);

public interface ICameraAgentArchive
{
    ObservingDayCalendar ObservingDays { get; }

    ValueTask<CameraAgentGalleryCalendar> GetCalendarAsync(
        CameraAgentGalleryCalendarQuery query,
        CancellationToken cancellationToken);

    ValueTask<CameraAgentGalleryNeighbours?> GetNeighboursAsync(
        Guid captureId,
        CameraAgentGalleryQuery filters,
        CancellationToken cancellationToken);
}
