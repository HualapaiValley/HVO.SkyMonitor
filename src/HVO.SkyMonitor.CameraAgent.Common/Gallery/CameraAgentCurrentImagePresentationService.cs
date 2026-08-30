using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed record CameraAgentPresentationRuntimeSnapshot(
    CameraAgentPresentationSystemStatus System,
    TimeSpan? ExpectedCaptureInterval);

internal interface ICameraAgentPresentationRuntime
{
    CameraAgentPresentationRuntimeSnapshot GetSnapshot(DateTimeOffset observedUtc);
}

internal sealed class CameraAgentPresentationRuntime(
    CaptureAdmissionCoordinator captureAdmission,
    CaptureScheduleRuntimeCoordinator captureSchedule,
    FleetRuntimeState fleetRuntime) : ICameraAgentPresentationRuntime
{
    public CameraAgentPresentationRuntimeSnapshot GetSnapshot(DateTimeOffset observedUtc)
    {
        var admission = captureAdmission.Snapshot;
        var schedule = captureSchedule.Snapshot;
        return Project(
            admission,
            schedule?.CurrentDecision,
            schedule?.Revision.Definition,
            schedule?.Configuration.Rig.Pipeline.CaptureInterval,
            fleetRuntime.Snapshot.Capture.Availability,
            observedUtc);
    }

    internal static CameraAgentPresentationRuntimeSnapshot Project(
        CaptureAdmissionSnapshot admission,
        CaptureScheduleDecision? decision,
        CaptureScheduleDefinition? schedule,
        TimeSpan? expectedInterval,
        FleetAvailability captureAvailability,
        DateTimeOffset observedUtc)
    {
        var (state, message) = admission.State switch
        {
            CaptureAdmissionState.Unavailable =>
                (CameraAgentPresentationSystemState.Unavailable, "Capture is unavailable."),
            _ when captureAvailability == FleetAvailability.Unavailable =>
                (CameraAgentPresentationSystemState.Unavailable, "The camera is currently unavailable."),
            CaptureAdmissionState.Initializing =>
                (CameraAgentPresentationSystemState.Starting, "CameraAgent is preparing capture."),
            CaptureAdmissionState.PauseRequested or CaptureAdmissionState.Paused =>
                (CameraAgentPresentationSystemState.Paused, "Capture is paused."),
            _ when captureAvailability == FleetAvailability.Initializing =>
                (CameraAgentPresentationSystemState.Starting, "The camera is preparing capture."),
            _ when decision is { Admitted: false, Reason: CaptureScheduleAdmissionReason.SafetyUnavailable } =>
                (CameraAgentPresentationSystemState.Unavailable, "Capture is waiting for a required local dependency."),
            _ when decision is { Admitted: false } =>
                (CameraAgentPresentationSystemState.Standby, "Capture is waiting for its next scheduled window."),
            _ => (CameraAgentPresentationSystemState.Capturing, "CameraAgent is capturing normally.")
        };
        var activeInterval = decision is { Admitted: true, SetpointProfileId: { } profileId }
            ? schedule?.SetpointProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, profileId, StringComparison.Ordinal))?.CaptureInterval
            : null;
        return new(
            new CameraAgentPresentationSystemStatus(state, message, observedUtc, decision?.NextTransitionUtc),
            (activeInterval ?? expectedInterval) is { } interval && interval > TimeSpan.Zero ? interval : null);
    }
}

internal interface ICameraAgentStructuredLayerAvailability
{
    ValueTask<bool> IsAvailableAsync(Guid captureId, CancellationToken cancellationToken);
}

internal sealed class CameraAgentStructuredLayerAvailability(
    ICameraAgentLayeredPresentationService presentations) : ICameraAgentStructuredLayerAvailability
{
    public async ValueTask<bool> IsAvailableAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var result = await presentations.GetAsync(captureId, cancellationToken).ConfigureAwait(false);
        return result.Status == CameraAgentLayeredPresentationStatus.Found;
    }
}

internal sealed class CameraAgentCurrentImagePresentationService(
    ICameraAgentGallery gallery,
    ICameraAgentCapturePresentationProjector capturePresentation,
    ICameraAgentArtifactService artifacts,
    ICameraAgentPresentationRuntime runtime,
    ICameraAgentStructuredLayerAvailability structuredLayers,
    TimeProvider timeProvider) : ICameraAgentCurrentImagePresentationService
{
    internal const int MaximumCandidateCaptures = 12;
    internal const int MaximumCandidatePages = 4;
    internal const int MaximumPreviewValidationAttempts = 12;

    public async ValueTask<CameraAgentCurrentImagePresentation> GetAsync(CancellationToken cancellationToken)
    {
        var observedUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var runtimeSnapshot = runtime.GetSnapshot(observedUtc);
        CameraAgentGalleryCapture? latest = null;
        CameraAgentGalleryCapture? display = null;
        CameraAgentCapturePresentation? latestPresentation = null;
        CameraAgentCapturePresentation? displayPresentation = null;
        string? cursor = null;
        var historyBoundReached = false;
        var validationAttempts = 0;
        for (var pageIndex = 0; pageIndex < MaximumCandidatePages; pageIndex++)
        {
            var page = await gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: MaximumCandidateCaptures, Cursor: cursor), cancellationToken)
                .ConfigureAwait(false);
            if (latest is null && page.Items.Count > 0)
            {
                latest = page.Items[0];
            }
            foreach (var candidate in page.Items)
            {
                var validation = await ValidatePresentationAsync(
                    candidate,
                    MaximumPreviewValidationAttempts - validationAttempts,
                    cancellationToken)
                    .ConfigureAwait(false);
                validationAttempts += validation.Attempts;
                var candidatePresentation = validation.Presentation;
                if (candidate.CaptureId == latest?.CaptureId)
                {
                    latestPresentation = candidatePresentation;
                }
                if (candidatePresentation.SelectedStage is not null)
                {
                    display = candidate;
                    displayPresentation = candidatePresentation;
                    break;
                }
                if (validation.BoundReached)
                {
                    historyBoundReached = true;
                    break;
                }
            }
            if (display is not null || historyBoundReached || page.NextCursor is null)
            {
                break;
            }
            cursor = page.NextCursor;
            historyBoundReached = pageIndex == MaximumCandidatePages - 1;
        }
        var projectedCapture = displayPresentation ?? latestPresentation ?? capturePresentation.Project(null);
        var selectedStage = display is null ? null : projectedCapture.SelectedStage;
        var historicalFallback = latest is not null && display is not null && latest.CaptureId != display.CaptureId;
        var freshness = GetFreshness(
            display,
            historicalFallback,
            observedUtc,
            runtimeSnapshot.ExpectedCaptureInterval);
        var structuredLayersAvailable = false;
        if (display is not null)
        {
            try
            {
                structuredLayersAvailable = await structuredLayers.IsAvailableAsync(
                    display.CaptureId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is SqliteException or InvalidDataException or
                JsonException or ArgumentException or InvalidOperationException or OverflowException)
            {
                structuredLayersAvailable = false;
            }
        }

        return new CameraAgentCurrentImagePresentation(
            observedUtc,
            freshness,
            runtimeSnapshot.System,
            ProjectCapture(latest, observedUtc),
            ProjectCapture(display, observedUtc),
            historicalFallback,
            selectedStage,
            projectedCapture.Stages,
            structuredLayersAvailable,
            display is null && historyBoundReached);
    }

    private async ValueTask<ValidatedPresentation> ValidatePresentationAsync(
        CameraAgentGalleryCapture capture,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        var projection = capturePresentation.Project(capture);
        CameraAgentPresentationStage? selectedStage = null;
        var stages = new CameraAgentPresentationSlot[projection.Stages.Count];
        var attempts = 0;
        var boundReached = false;
        for (var index = 0; index < projection.Stages.Count; index++)
        {
            var slot = projection.Stages[index];
            if (slot.Availability != CameraAgentPresentationSlotAvailability.Available)
            {
                stages[index] = slot;
                continue;
            }
            var excluded = new HashSet<Guid>();
            while (slot.ArtifactId is { } artifactId)
            {
                if (attempts >= maximumAttempts)
                {
                    stages[index] = Unavailable(slot, "ValidationBoundReached");
                    boundReached = true;
                    break;
                }
                attempts++;
                var preview = await artifacts.GetPreviewAsync(artifactId, cancellationToken).ConfigureAwait(false);
                if (preview.Status == CameraAgentArtifactReadStatus.Found)
                {
                    selectedStage ??= slot.Stage;
                    stages[index] = slot;
                    break;
                }
                var failed = Unavailable(slot, preview.Status.ToString());
                excluded.Add(artifactId);
                var remaining = capture with
                {
                    Artifacts = capture.Artifacts.Where(artifact => !excluded.Contains(artifact.ArtifactId)).ToArray()
                };
                slot = capturePresentation.Project(remaining).Stages.Single(candidate => candidate.Stage == slot.Stage);
                if (slot.Availability != CameraAgentPresentationSlotAvailability.Available)
                {
                    stages[index] = failed;
                    break;
                }
            }
            if (stages[index] is null)
            {
                stages[index] = slot;
            }
        }
        return new(new CameraAgentCapturePresentation(selectedStage, stages), attempts, boundReached);
    }

    private static CameraAgentPresentationSlot Unavailable(CameraAgentPresentationSlot slot, string reason)
        => slot with
        {
            Availability = CameraAgentPresentationSlotAvailability.Unavailable,
            Reason = reason,
            ArtifactId = null,
            ArtifactRole = null,
            Variant = null,
            MediaType = null,
            PreviewUrl = null
        };

    private static CameraAgentPresentationImageFreshness GetFreshness(
        CameraAgentGalleryCapture? display,
        bool historicalFallback,
        DateTimeOffset observedUtc,
        TimeSpan? expectedCaptureInterval)
    {
        if (display is null)
        {
            return CameraAgentPresentationImageFreshness.Empty;
        }
        if (historicalFallback)
        {
            return CameraAgentPresentationImageFreshness.Historical;
        }
        if (expectedCaptureInterval is not { } interval)
        {
            return CameraAgentPresentationImageFreshness.Current;
        }
        var age = observedUtc - display.ExposureStartedUtc;
        if (age > interval * 6)
        {
            return CameraAgentPresentationImageFreshness.Stale;
        }
        return age > interval * 2
            ? CameraAgentPresentationImageFreshness.Delayed
            : CameraAgentPresentationImageFreshness.Current;
    }

    private static CameraAgentPresentationCapture? ProjectCapture(
        CameraAgentGalleryCapture? capture,
        DateTimeOffset observedUtc)
        => capture is null
            ? null
            : new CameraAgentPresentationCapture(
                capture.CaptureId,
                capture.CaptureSequence,
                capture.ExposureStartedUtc,
                Math.Max(0, (long)(observedUtc - capture.ExposureStartedUtc).TotalSeconds),
                capture.EvidenceOrigin);

    private sealed record ValidatedPresentation(
        CameraAgentCapturePresentation Presentation,
        int Attempts,
        bool BoundReached);
}

internal sealed class CameraAgentCapturePresentationProjector(
    IOptions<CameraAgentHostOptions> options) : ICameraAgentCapturePresentationProjector
{
    private readonly ArtifactReadOptions _artifactRead = options.Value.ArtifactRead;

    private static readonly CameraAgentPresentationStage[] StageOrder =
    [
        CameraAgentPresentationStage.Annotated,
        CameraAgentPresentationStage.Combined,
        CameraAgentPresentationStage.Calibrated,
        CameraAgentPresentationStage.Raw
    ];

    private static readonly CameraAgentPresentationStage[] SelectionOrder =
    [
        CameraAgentPresentationStage.Annotated,
        CameraAgentPresentationStage.Combined,
        CameraAgentPresentationStage.Calibrated,
        CameraAgentPresentationStage.Raw
    ];

    public CameraAgentCapturePresentation Project(CameraAgentGalleryCapture? capture)
    {
        var stages = StageOrder.Select(stage => capture is null
            ? Unavailable(stage, CameraAgentPresentationSlotAvailability.Missing, "NoCapture")
            : ProjectSlot(capture, stage)).ToArray();
        var selected = SelectionOrder
            .Select(stage => stages.Single(slot => slot.Stage == stage))
            .FirstOrDefault(static slot => slot.Availability == CameraAgentPresentationSlotAvailability.Available);
        return new(selected?.Stage, stages);
    }

    private CameraAgentPresentationSlot ProjectSlot(
        CameraAgentGalleryCapture capture,
        CameraAgentPresentationStage stage)
    {
        var roles = RolesFor(stage);
        var matching = capture.Artifacts.Where(artifact => roles.Contains(artifact.Role)).ToArray();
        if (stage == CameraAgentPresentationStage.Raw &&
            !string.Equals(capture.RawState, "committed", StringComparison.Ordinal))
        {
            return Unavailable(stage, CameraAgentPresentationSlotAvailability.Unavailable, "RawEvidenceUnavailable");
        }
        if (stage != CameraAgentPresentationStage.Raw && capture.ProcessingProjectionUnavailable)
        {
            return Unavailable(stage, CameraAgentPresentationSlotAvailability.Unavailable, "ProcessingProjectionUnavailable");
        }

        var availableArtifacts = matching
            .Where(static artifact => string.Equals(artifact.Availability, "Available", StringComparison.Ordinal))
            .Select(artifact => new
            {
                Artifact = artifact,
                Eligibility = CameraAgentPreviewEligibilityPolicy.Evaluate(
                    artifact.Role,
                    artifact.MediaType,
                    artifact.ByteLength,
                    artifact.PixelFormat,
                    artifact.PreviewReconstructionSupported,
                    artifact.EncodedWidth,
                    artifact.EncodedHeight,
                    _artifactRead)
            })
            .ToArray();
        var available = availableArtifacts
            .Where(static candidate => candidate.Eligibility == CameraAgentPreviewEligibility.Available)
            .Select(static candidate => candidate.Artifact)
            .OrderBy(artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview ? 0 : 1)
            .ThenBy(MediaRank)
            .ThenBy(artifact => roles.Contains(FrameArtifactRole.Raw) || artifact.SourceArtifactIds.Count > 0 ? 0 : 1)
            .ThenBy(static artifact => artifact.Recipe?.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(static artifact => artifact.Variant, StringComparer.Ordinal)
            .ThenByDescending(static artifact => artifact.CreatedUtc)
            .ThenBy(static artifact => artifact.ArtifactId)
            .FirstOrDefault();
        if (available is not null)
        {
            return new CameraAgentPresentationSlot(
                stage,
                LabelFor(stage),
                CameraAgentPresentationSlotAvailability.Available,
                "Available",
                available.ArtifactId,
                available.Role,
                available.Variant,
                available.MediaType,
                new Uri(FormattableString.Invariant(
                    $"/api/v1/operations/artifacts/{available.ArtifactId:D}/preview"), UriKind.Relative));
        }
        var producingNodes = capture.ProcessingNodes.Where(node =>
            node.OutputRole is { } role && roles.Contains(role)).ToArray();
        var processingReason = producingNodes.Any(static node => node.Status is "RetryableFailure" or "TerminalFailure")
            ? "ProcessingFailed"
            : producingNodes.Any(static node => string.Equals(node.Status, "Skipped", StringComparison.Ordinal))
                ? "ProcessingSkipped"
                : null;
        if (stage == CameraAgentPresentationStage.Annotated && processingReason is not null)
        {
            return Unavailable(stage, CameraAgentPresentationSlotAvailability.Unavailable, processingReason);
        }
        if (availableArtifacts.Any(static candidate => candidate.Eligibility == CameraAgentPreviewEligibility.TooLarge))
        {
            return Unavailable(stage, CameraAgentPresentationSlotAvailability.Unavailable, "PreviewBoundsExceeded");
        }
        if (availableArtifacts.Any(static candidate => candidate.Eligibility == CameraAgentPreviewEligibility.Invalid))
        {
            return Unavailable(stage, CameraAgentPresentationSlotAvailability.Unavailable, "ArtifactInvalid");
        }
        if (availableArtifacts.Length > 0)
        {
            return Unavailable(stage, CameraAgentPresentationSlotAvailability.Unsupported, "UnsupportedMediaType");
        }
        if (matching.Length > 0)
        {
            return Unavailable(stage, CameraAgentPresentationSlotAvailability.Unavailable, "ArtifactUnavailable");
        }
        if (processingReason is not null)
        {
            return Unavailable(stage, CameraAgentPresentationSlotAvailability.Unavailable, processingReason);
        }
        if (capture.ArtifactsTruncated)
        {
            return Unavailable(stage, CameraAgentPresentationSlotAvailability.Unavailable, "ProjectionBoundReached");
        }
        return Unavailable(stage, CameraAgentPresentationSlotAvailability.Missing, "NotProduced");
    }

    private static int MediaRank(CameraAgentGalleryArtifact artifact)
    {
        if (!string.Equals(artifact.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (string.Equals(artifact.Variant, "annotated-thumbnail-1024-jpeg", StringComparison.Ordinal))
        {
            return 0;
        }
        return artifact.Variant?.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) == true ? 1 : 3;
    }

    private static FrameArtifactRole[] RolesFor(CameraAgentPresentationStage stage) => stage switch
    {
        CameraAgentPresentationStage.Raw => [FrameArtifactRole.Raw],
        CameraAgentPresentationStage.Calibrated => [FrameArtifactRole.Calibrated],
        CameraAgentPresentationStage.Combined => [FrameArtifactRole.Combined],
        CameraAgentPresentationStage.Annotated => [FrameArtifactRole.AnnotatedPreview, FrameArtifactRole.Preview],
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private static string LabelFor(CameraAgentPresentationStage stage) => stage switch
    {
        CameraAgentPresentationStage.Raw => "Raw",
        CameraAgentPresentationStage.Calibrated => "Calibrated",
        CameraAgentPresentationStage.Combined => "Combined",
        CameraAgentPresentationStage.Annotated => "Processed",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private static CameraAgentPresentationSlot Unavailable(
        CameraAgentPresentationStage stage,
        CameraAgentPresentationSlotAvailability availability,
        string reason)
        => new(stage, LabelFor(stage), availability, reason);
}
