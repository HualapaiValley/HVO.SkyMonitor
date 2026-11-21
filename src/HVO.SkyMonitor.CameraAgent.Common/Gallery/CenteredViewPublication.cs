using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>A camera's publication scope within one admitted run and immutable processing generation.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The key identifies a durably ordered stream of camera publications.")]
public sealed record CenteredViewStream(CameraKey Camera, Guid RunId, string GenerationId);

public enum CenteredViewKind
{
    [SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Single denotes one exposure.")]
    Single,
    Stack
}
public enum CenteredViewLayerScope { Reference, Contributor, Stack }
public enum CenteredViewReadiness { Waiting, Ready, Partial, AssessedEmpty, Incompatible, NotConfigured }
public enum CenteredViewImageSource { QualifiedLinear, RawFallback, RegisteredLinear }

/// <summary>Exact retained artifact identity and payload checksum.</summary>
public sealed record CenteredViewArtifact(Guid ArtifactId, string IdentitySha256, string PayloadSha256);

public sealed record CenteredViewExposure(
    ExposureInputV1 Input,
    Guid ExecutionId,
    CenteredViewArtifact? Linear)
{
    [JsonIgnore] public CaptureIdentityDescriptor Capture => Input.Raw.Capture;
    [JsonIgnore] public DateTimeOffset ExposureStartedUtc => Input.Raw.Timing.ExposureStartedUtc;
    [JsonIgnore] public DateTimeOffset ExposureEndedUtc => Input.Raw.Timing.ExposureEndedUtc;

    internal CenteredViewArtifact RawArtifact() => new(Input.Raw.Artifact.ArtifactId,
        CaptureContractJson.ComputeDescriptorSha256(Input.Raw), Input.Raw.Artifact.ChecksumSha256);
}

/// <summary>Versioned direct mapping from the contributor's delivered raster to the reference raster.</summary>
public sealed record CenteredViewContributor(
    CenteredViewExposure Exposure,
    string RegistrationMethod,
    JsonElement SourceToReference);

/// <summary>Crop and resize in pixel-edge coordinates, with pixel centers at +0.5.</summary>
public sealed record CenteredViewPreviewTransform(
    int ReferenceWidth, int ReferenceHeight,
    int CropX, int CropY, int CropWidth, int CropHeight,
    int PreviewWidth, int PreviewHeight)
{
    public PixelPoint ToPreview(PixelPoint reference) => new(
        (reference.X - CropX) * PreviewWidth / CropWidth,
        (reference.Y - CropY) * PreviewHeight / CropHeight);
}

public sealed record CenteredViewPreview(
    PresentationProductReference Product,
    string PayloadSha256,
    RecipeIdentityDescriptor Recipe,
    string EncoderName,
    string EncoderVersion,
    CenteredViewPreviewTransform Transform)
{
    public CenteredViewImageSource Source { get; init; }
    public LinearExposureDisposition? LinearDisposition { get; init; }
    public string? QualificationReason { get; init; }
}

public sealed record CenteredViewBaseDefinition(
    CenteredViewStream Stream,
    CenteredViewExposure Reference,
    // Monotonic reference position within the run, from the admission/window contract.
    long ReferenceOrder,
    CenteredViewKind Kind,
    CenteredViewArtifact Science,
    CenteredViewPreview Preview,
    IReadOnlyList<CenteredViewContributor> Contributors,
    CenteredViewArtifact? ClosedWindow,
    string? RecomputedFromBaseId = null)
{
    public CenteredViewWindowBinding? Window { get; init; }
}

public sealed record CenteredViewWindowBinding(
    string ScheduleIdentitySha256, string ClosureIdentitySha256, string RegistrationPlanIdentitySha256)
{
    public DateTimeOffset DeadlineUtc { get; init; }
    public DateTimeOffset EvidenceCutoffUtc { get; init; }
    public IReadOnlyList<CenteredViewWindowSlot> Slots { get; init; } = [];
}

public sealed record CenteredViewWindowSlot(
    int Offset, DateTimeOffset ExpectedStartUtc, Guid? CaptureId,
    DateTimeOffset? StartedUtc, DateTimeOffset? EndedUtc,
    string ClosureState, string RegistrationOutcome, string? ClosureReason);

public sealed record CenteredViewBase(string BaseId, CenteredViewBaseDefinition Definition, DateTimeOffset RegisteredUtc)
{
    public const string SchemaVersion = "cameraagent-centered-view-base-v1";
}

/// <summary>One independently updated branch or contributor slot. The source capture is null for stack-wide facts.</summary>
public sealed record CenteredViewLayerKey(string Kind, CenteredViewLayerScope Scope, Guid? SourceCaptureId);

/// <summary>Order comes from the evidence producer's durable revision, independent of arrival order.</summary>
public sealed record CenteredViewLayer(
    CenteredViewLayerKey Key,
    long EvidenceOrder,
    CenteredViewArtifact? Evidence,
    DateTimeOffset? ObservedUtc,
    CenteredViewReadiness Readiness,
    string? Reason,
    PresentationLayerV1? Drawable);

public sealed record CenteredViewRevision(
    string RevisionId,
    string BaseId,
    long Order,
    DateTimeOffset PublishedUtc,
    OverlayManifestV1 Manifest,
    IReadOnlyList<CenteredViewLayer> Layers)
{
    public const string SchemaVersion = "cameraagent-centered-view-revision-v1";
}

public sealed record CenteredView(CenteredViewBase Base, CenteredViewRevision Revision);

/// <summary>A durable interval for a revision issued as the authoritative live selection.</summary>
public sealed record CenteredViewLiveInterval(
    string BaseId, string RevisionId,
    long StartedOrder, DateTimeOffset StartedUtc,
    long? EndedOrder, DateTimeOffset? EndedUtc);

public sealed record CenteredViewCurrent(CenteredView View, CenteredViewLiveInterval Interval);

/// <summary>The first committed revision, latest enrichment, and last live-issued revision are independent.</summary>
public sealed record CenteredViewArchiveBase(
    CenteredViewBase Base, string InitialRevisionId, string LatestRevisionId, string? LastLiveRevisionId)
{
    public string DefaultRevisionId => LastLiveRevisionId ?? InitialRevisionId;
}

public sealed record CenteredViewChange(
    long Order, DateTimeOffset CommittedUtc, string BaseId, string RevisionId, bool SelectedLive);

public sealed record CenteredViewSavedSelection(
    Guid SelectionId, string BaseId, string RevisionId, IReadOnlyList<string> EnabledLayerIds, DateTimeOffset SavedUtc);

public interface ICenteredViewReadIndex
{
    ValueTask<CenteredViewCurrent?> ReadCurrentAsync(CenteredViewStream stream, CancellationToken cancellationToken = default);

    /// <summary>Last live-issued revision for the camera, even while its successor run warms up.</summary>
    ValueTask<CenteredViewCurrent?> ReadLastLiveAsync(CameraKey camera, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<CenteredViewCurrent?>(null);

    ValueTask<CenteredView?> ReadLatestSingleAsync(CenteredViewStream stream, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CenteredViewArchiveBase>> ReadArchiveAsync(
        CenteredViewStream stream, Guid referenceCaptureId, CancellationToken cancellationToken = default);

    ValueTask<CenteredViewStream?> ReadReferenceStreamAsync(
        CameraKey camera, Guid referenceCaptureId, CancellationToken cancellationToken = default);

    ValueTask<CenteredView?> ReadRevisionAsync(string baseId, string revisionId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CenteredViewRevision>> ReadRevisionsAsync(
        string baseId, long afterOrder, int limit, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CenteredViewLiveInterval>> ReadLiveHistoryAsync(
        string baseId, long afterOrder, int limit, CancellationToken cancellationToken = default);

    /// <summary>Replay committed changes on reconnect, resuming strictly after the last observed order.</summary>
    ValueTask<IReadOnlyList<CenteredViewChange>> ReadChangesAsync(
        CenteredViewStream stream, long afterOrder, int limit, CancellationToken cancellationToken = default);

    ValueTask<CenteredViewSavedSelection?> ReadSavedAsync(
        string owner, Guid selectionId, CancellationToken cancellationToken = default);
}

public interface ICenteredViewPublisher
{
    /// <summary>Raised after commit. The durable change feed is authoritative for ordering and recovery.</summary>
    [SuppressMessage("Design", "CA1003:Use generic event handler instances", Justification = "Only the stream key is needed to wake the reader.")]
    event Action<CenteredViewStream>? Changed;

    ValueTask<CenteredView> RegisterBaseAsync(
        CenteredViewBaseDefinition definition, IReadOnlyList<CenteredViewLayer> initialLayers,
        CancellationToken cancellationToken = default);

    /// <summary>Update one evidence key without overwriting a concurrently committed, independent key.</summary>
    ValueTask<CenteredView?> PublishLayerAsync(
        string baseId, CenteredViewLayer layer, CancellationToken cancellationToken = default);

    /// <summary>Issue the latest revision of a ready base if it advances the authoritative live selection.</summary>
    ValueTask<CenteredViewCurrent?> SelectLiveAsync(
        CenteredViewStream stream, string baseId, CancellationToken cancellationToken = default);

    ValueTask<CenteredViewSavedSelection?> SaveAsync(
        string owner, Guid selectionId, string baseId, string revisionId, IReadOnlyList<string> enabledLayerIds,
        CancellationToken cancellationToken = default);
}
