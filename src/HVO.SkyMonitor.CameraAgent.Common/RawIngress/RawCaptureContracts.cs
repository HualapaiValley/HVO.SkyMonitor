using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

public enum RawIngressOutcome
{
    Committed,
    Existing
}

public sealed record RawCaptureReceipt(
    RawIngressOutcome Outcome,
    ArtifactManifestV2 Manifest,
    StoredFrameReference StoredFrame,
    string CommittedManifestSha256);

public interface IRawCaptureIngress
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask<RawCaptureReceipt?> AcceptAsync(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission,
        CancellationToken cancellationToken);

    internal ValueTask<RawCapturePublicationState> GetPublicationStateAsync(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(RawCapturePublicationState.Unknown);

    internal ValueTask BindRecoveredLiveExecutionsAsync(
        CameraModuleConfig configuration,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal enum RawCapturePublicationState
{
    Unknown,
    DefinitelyNotCommitted,
    Committed
}

internal interface IProjectedSceneStageOwnerProvider
{
    ValueTask<IReadOnlySet<string>> GetOwnedStageKeysAsync(CancellationToken cancellationToken);
}

public sealed record RawIngressRetentionHold(
    Guid ArtifactId,
    string PayloadRelativePath,
    string SidecarRelativePath);

public interface IRawIngressRetentionHolds
{
    ValueTask<IReadOnlyList<RawIngressRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken);
}

internal sealed record RawCaptureIdentity(
    string AgentId,
    long CaptureSequence,
    Guid CaptureId,
    Guid ArtifactId);

internal sealed record RawIngressPaths(
    string PayloadRelativePath,
    string SidecarRelativePath,
    string PayloadAbsolutePath,
    string SidecarAbsolutePath);

internal sealed record RawIngressJournalEntry(
    string AgentId,
    long CaptureSequence,
    Guid CaptureId,
    Guid ArtifactId,
    string DescriptorSha256,
    string ManifestSha256,
    string PayloadSha256,
    long PayloadLength,
    string PayloadRelativePath,
    string SidecarRelativePath,
    byte[] ManifestJson,
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset DurableIngressUtc,
    string State = "committed",
    bool RetentionHold = true,
    GalleryEvidenceOrigin EvidenceOrigin = GalleryEvidenceOrigin.Unknown);

internal sealed record RawIngressReconciliationSummary(
    int Inspected,
    int Recovered,
    int Cleaned,
    int Quarantined,
    int MissingEvidence,
    long QuarantineBytes,
    int IndexProjectionFailures = 0,
    int ProjectedSceneStageBacklog = 0);

internal sealed record RawIngressPlannedQuarantine(
    string EvidenceKey,
    string SourceRelativePath,
    string? CompanionRelativePath,
    string QuarantineRelativePath,
    string Reason,
    long ObservedBytes);
