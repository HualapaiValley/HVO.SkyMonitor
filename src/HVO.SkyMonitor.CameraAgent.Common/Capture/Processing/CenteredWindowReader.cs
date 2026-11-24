using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed record CenteredWindowSlotSnapshot(int Offset, DateTimeOffset ExpectedStartUtc,
    ExposureAttemptKey? Attempt, ExposureKey? Exposure, CenteredWindowSlotState? State, string? Reason)
{
    public long? CaptureSequence { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public CenteredRegistrationReason? RegistrationOutcome { get; init; }
}

public sealed record CenteredWindowSnapshot(Guid ReferenceCaptureId, string WindowIdentity, CameraKey Camera, Guid RunId,
    DateTimeOffset DeadlineUtc, DateTimeOffset? EvidenceCutoffUtc, string? ClosureIdentity,
    bool IsTerminal, string? FailureReason, Guid? EvidenceArtifactId, string? PlanSha256,
    CenteredStackDisposition? StackDisposition, ImmutableArray<Guid> ContributingCaptureIds,
    ImmutableArray<CenteredWindowSlotSnapshot> Slots)
{
    public string? GenerationId { get; init; }
}

/// <summary>
/// Reads the original live scheduling and registration evidence for an exact admitted center.
/// A capture admitted after its missing-center window closed resolves to that same closure, with no center exposure.
/// </summary>
public interface ICenteredWindowReader
{
    ValueTask<CenteredWindowSnapshot?> ReadAsync(Guid referenceCaptureId, CancellationToken cancellationToken);
}

internal sealed class CenteredWindowReader(SqliteCaptureProcessingStore store, CaptureProcessingPersistence? persistence = null)
    : ICenteredWindowReader
{
    public async ValueTask<CenteredWindowSnapshot?> ReadAsync(Guid referenceCaptureId, CancellationToken cancellationToken)
    {
        var row = await store.ReadCenteredWindowStateAsync(referenceCaptureId, cancellationToken).ConfigureAwait(false);
        if (row is not { } tracked)
            return null;

        if (tracked.Refusal is { } refusal)
            return new(referenceCaptureId, refusal.Identity, refusal.Camera, refusal.RunId, refusal.DeadlineUtc, null, null,
                true, refusal.Reason, null, null, null, [], []);

        var checkpoint = tracked.Checkpoint!;
        var schedule = checkpoint.Schedule;
        var closed = tracked.Closed;
        var publication = closed?.Publication;
        var registration = publication?.StackDisposition == CenteredStackDisposition.SingleExposure
            ? await ReadSingleRegistrationAsync(closed!.Closure, publication, cancellationToken).ConfigureAwait(false)
            : null;
        var slots = closed is null
            ? checkpoint.ProjectedTimes.Select((time, index) =>
                new CenteredWindowSlotSnapshot(index - CenteredWindowScheduler.CenterIndex, time.ExpectedStartUtc,
                    index == CenteredWindowScheduler.CenterIndex ? checkpoint.ReferenceAttempt : null,
                    index == CenteredWindowScheduler.CenterIndex ? new ExposureKey(schedule.Camera, schedule.RunId, referenceCaptureId) : null,
                    time.BoundaryReason is null ? null : CenteredWindowSlotState.OutsideRun, time.BoundaryReason)).ToImmutableArray()
            : closed.Closure.Slots.Select((slot, index) => new CenteredWindowSlotSnapshot(slot.Offset,
                slot.Time.ExpectedStartUtc, slot.Attempt, slot.Input?.Key, slot.State, slot.Reason)
            {
                CaptureSequence = slot.Input?.CaptureSequence,
                StartedUtc = slot.Input?.Timing.ExposureStartedUtc,
                EndedUtc = slot.Input?.Timing.ExposureEndedUtc,
                RegistrationOutcome = registration?.Sources[index].Reason
            }).ToImmutableArray();

        return new(referenceCaptureId, schedule.Identity, schedule.Camera, schedule.RunId, schedule.DeadlineUtc,
            closed?.Closure.EvidenceCutoffUtc, closed?.Closure.Identity, closed?.IsTerminal ?? false, closed?.FailureReason,
            publication?.EvidenceArtifactId, publication?.PlanSha256, publication?.StackDisposition,
            publication?.ContributingCaptureIds ?? [], slots)
        {
            GenerationId = schedule.GenerationId
        };
    }

    private async ValueTask<CenteredRegisteredStackEvidence?> ReadSingleRegistrationAsync(CenteredWindowClosure closure,
        CenteredWindowPublication receipt, CancellationToken cancellationToken)
    {
        if (persistence is null || !receipt.RegistrationArtifactIds.Contains(receipt.EvidenceArtifactId))
            return null;
        try
        {
            var output = await store.ReadOutputByArtifactIdAsync(receipt.EvidenceArtifactId, cancellationToken).ConfigureAwait(false);
            if (output is not { AvailabilityState: "Available", ProductSchemaVersion: CenteredRegisteredStackEvidence.CurrentSchemaVersion })
                return null;
            var artifact = await persistence.ReadCenteredViewRetainedProductAsync(receipt.ReferenceCaptureId,
                new(output.ArtifactId, output.OutputIdentitySha256, output.Artifact.ChecksumSha256), cancellationToken).ConfigureAwait(false);
            var evidence = CenteredRegisteredStackEvidence.Read(artifact);
            Gallery.CenteredViewProducer.RequireWindowBinding(closure, receipt, evidence);
            return evidence;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            // The retained slot closure remains readable independently of the registration artifact.
            return null;
        }
    }
}
