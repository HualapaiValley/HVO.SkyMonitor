using System.Collections.Immutable;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed record CenteredWindowSlotSnapshot(int Offset, DateTimeOffset ExpectedStartUtc,
    ExposureAttemptKey? Attempt, ExposureKey? Exposure, CenteredWindowSlotState? State, string? Reason);

public sealed record CenteredWindowSnapshot(Guid ReferenceCaptureId, string WindowIdentity, CameraKey Camera, Guid RunId,
    DateTimeOffset DeadlineUtc, DateTimeOffset? EvidenceCutoffUtc, string? ClosureIdentity,
    bool IsTerminal, string? FailureReason, Guid? EvidenceArtifactId, string? PlanSha256,
    CenteredStackDisposition? StackDisposition, ImmutableArray<Guid> ContributingCaptureIds,
    ImmutableArray<CenteredWindowSlotSnapshot> Slots);

/// <summary>Reads the original live scheduling and registration evidence for an exact admitted center.</summary>
public interface ICenteredWindowReader
{
    ValueTask<CenteredWindowSnapshot?> ReadAsync(Guid referenceCaptureId, CancellationToken cancellationToken);
}

internal sealed class CenteredWindowReader(SqliteCaptureProcessingStore store) : ICenteredWindowReader
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
        var slots = closed is null
            ? checkpoint.ProjectedTimes.Select((time, index) =>
                new CenteredWindowSlotSnapshot(index - CenteredWindowScheduler.CenterIndex, time.ExpectedStartUtc,
                    index == CenteredWindowScheduler.CenterIndex ? checkpoint.ReferenceAttempt : null,
                    index == CenteredWindowScheduler.CenterIndex ? new ExposureKey(schedule.Camera, schedule.RunId, referenceCaptureId) : null,
                    time.BoundaryReason is null ? null : CenteredWindowSlotState.OutsideRun, time.BoundaryReason)).ToImmutableArray()
            : closed.Closure.Slots.Select(slot => new CenteredWindowSlotSnapshot(slot.Offset, slot.Time.ExpectedStartUtc,
                slot.Attempt, slot.Input?.Key, slot.State, slot.Reason)).ToImmutableArray();

        return new(referenceCaptureId, schedule.Identity, schedule.Camera, schedule.RunId, schedule.DeadlineUtc,
            closed?.Closure.EvidenceCutoffUtc, closed?.Closure.Identity, closed?.IsTerminal ?? false, closed?.FailureReason,
            publication?.EvidenceArtifactId, publication?.PlanSha256, publication?.StackDisposition,
            publication?.ContributingCaptureIds ?? [], slots);
    }
}
