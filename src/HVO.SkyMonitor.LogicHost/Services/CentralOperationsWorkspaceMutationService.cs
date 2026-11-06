using System.Security.Claims;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum CentralWorkspaceAction
{
    CancelJob, RetryJob, ReprocessJob, CancelExecution, ReplayExecution, ConfirmEvent, RejectEvent,
    ReleaseArtifact, WithdrawArtifact, SavePolicy, PublishRevision, RetireRevision
}

internal sealed record CentralWorkspaceCommand(CentralWorkspaceAction Action, CentralOperationsQuery Selection,
    string IdempotencyKey, string? ETag = null, Guid? Assessment = null, Guid? Artifact = null,
    CentralProcessingPolicyRequest? Policy = null);

internal interface ICentralOperationsWorkspaceMutationService
{
    Task<string> ExecuteAsync(ClaimsPrincipal principal, Guid observatory, Guid camera, CentralWorkspaceCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>Scope adapter only. Existing services own auditing, state checks, locks, idempotency and writes.</summary>
internal sealed class CentralOperationsWorkspaceMutationService(ICentralOperationsWorkspaceService workspace,
    INetworkOperationsMutationService jobs, ICentralProcessingGraphExecutionService executions,
    ICentralTransientReviewService reviews, IPublicRecordPublicationService publication,
    ICentralProcessingPolicyService policy, IProcessingGraphCatalogService catalog, TimeProvider clock)
    : ICentralOperationsWorkspaceMutationService
{
    internal const string Denied = "Not found or access was removed. Refresh access before trying again.";

    public async Task<string> ExecuteAsync(ClaimsPrincipal principal, Guid observatory, Guid camera, CentralWorkspaceCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (actor is null || !CentralArtifactCredentialAccess.HasOwnerWriteCredential(principal) ||
            !CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, observatory)) return Denied;
        var snapshot = await workspace.ReadAsync(principal, observatory, camera, command.Selection, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return Denied;
        var manage = snapshot.Role is ObservatoryMembershipRole.Manager or ObservatoryMembershipRole.Owner;
        switch (command.Action)
        {
            case CentralWorkspaceAction.CancelJob or CentralWorkspaceAction.RetryJob or CentralWorkspaceAction.ReprocessJob:
                if (!manage || snapshot.Job is not { } job) return Denied;
                var outcome = command.Action switch
                {
                    CentralWorkspaceAction.CancelJob => await jobs.CancelJobAsync(actor, job.Job.JobId, cancellationToken).ConfigureAwait(false),
                    CentralWorkspaceAction.RetryJob => await jobs.RequeueJobAsync(actor, job.Job.JobId, cancellationToken).ConfigureAwait(false),
                    _ => await jobs.ReprocessJobAsync(actor, job.Job.JobId, cancellationToken).ConfigureAwait(false)
                };
                return outcome switch
                {
                    NetworkOperationsMutationOutcome.Applied => command.Action == CentralWorkspaceAction.RetryJob
                        ? "Requeued the same job for a new attempt; retained attempt history is unchanged."
                        : command.Action == CentralWorkspaceAction.ReprocessJob
                            ? "Successor reprocessing was requested using the existing supersession rules; prior history is retained."
                            : "Central job cancellation was recorded; worker convergence may still be pending.",
                    NetworkOperationsMutationOutcome.InvalidState => "The job changed state or is ineligible. Refresh and inspect its current state.",
                    _ => Denied
                };
            case CentralWorkspaceAction.CancelExecution:
                if (snapshot.Execution is not { } cancel) return Denied;
                var cancellation = await executions.CancelAsync(cancel.Execution.Id, actor, observatory, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                return cancellation switch
                {
                    CentralProcessingGraphCancellationOutcome.Applied => "Central execution cancellation requested; retained sources and history are unchanged.",
                    CentralProcessingGraphCancellationOutcome.Unchanged => "The central execution is already terminal or cancellation was already requested.",
                    CentralProcessingGraphCancellationOutcome.Forbidden => "Forbidden: management authority or ownership of this replay is required to cancel it.",
                    _ => Denied
                };
            case CentralWorkspaceAction.ReplayExecution:
                if (!manage || snapshot.Execution is not { } replay) return Denied;
                if (command.IdempotencyKey.Length is < 1 or > 128) return "A bounded idempotency key is required.";
                var scheduled = await executions.ScheduleReplayAsync(replay.Execution.RevisionId,
                    replay.Sources.OrderBy(s => s.Ordinal).Select(s => s.ArtifactId).ToArray(), actor, observatory,
                    command.IdempotencyKey, "central-workspace-exact-replay", clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                return scheduled.Outcome switch
                {
                    CentralProcessingGraphScheduleOutcome.Created => $"Created exact-source central replay {scheduled.Execution?.Id:D}. The inspected execution is unchanged.",
                    CentralProcessingGraphScheduleOutcome.Existing => $"Existing exact-source central replay {scheduled.Execution?.Id:D}; this request was already recorded.",
                    CentralProcessingGraphScheduleOutcome.Conflict => "Replay idempotency conflict. Keep this request key and inspect the recorded request.",
                    _ => $"Replay was not scheduled: {scheduled.ReasonCode ?? scheduled.Outcome.ToString()}.",
                };
            case CentralWorkspaceAction.ConfirmEvent or CentralWorkspaceAction.RejectEvent:
                if (!snapshot.CanReviewEvent || snapshot.Event is not { } eventDetail) return Denied;
                if (command.Assessment is not { } assessment || !CentralTransientEventEtag.TryParse(command.ETag ?? "", out var rowVersion))
                    return "A current strong event ETag and exact assessment are required. Refresh the event.";
                var review = await reviews.ReviewAsync(principal, eventDetail.Summary.CentralTransientEventId, rowVersion,
                    command.IdempotencyKey, new(assessment,
                        command.Action == CentralWorkspaceAction.ConfirmEvent ? TransientReviewDisposition.Confirmed : TransientReviewDisposition.Rejected,
                        null, ["central-workspace-review"]), cancellationToken).ConfigureAwait(false);
                return review.Status switch
                {
                    CentralTransientReviewMutationStatus.Applied => "The event review was recorded against the exact assessment.",
                    CentralTransientReviewMutationStatus.PreconditionFailed => "The event changed (stale If-Match). Refresh and review the current assessment.",
                    CentralTransientReviewMutationStatus.NotFound => Denied,
                    _ => $"Event review was not applied: {review.Status}. Refresh before another decision."
                };
            case CentralWorkspaceAction.ReleaseArtifact or CentralWorkspaceAction.WithdrawArtifact:
                if (snapshot.Role != ObservatoryMembershipRole.Owner || snapshot.Artifacts is null) return Denied;
                var artifact = snapshot.Artifacts.Items.SingleOrDefault(a => a.ArtifactId == command.Artifact);
                if (artifact is null) return Denied;
                var released = await publication.DecideAsync(observatory, actor,
                    new(PublicRecordSubjectKind.Artifact, artifact.PublicationSubjectId),
                    command.Action == CentralWorkspaceAction.ReleaseArtifact ? PublicationDecisionState.Released : PublicationDecisionState.Withdrawn,
                    "public-image-v1", "central-workspace-exact-artifact", cancellationToken).ConfigureAwait(false);
                return released.Outcome switch
                {
                    PublicRecordPublicationOutcome.Applied => "The publication decision for this exact artifact was recorded. Other artifacts are unchanged.",
                    PublicRecordPublicationOutcome.Unchanged => "This exact artifact publication decision is already current.",
                    PublicRecordPublicationOutcome.Conflict => "Publication conflict. Refresh eligibility and release state.",
                    _ => Denied
                };
            case CentralWorkspaceAction.SavePolicy:
                if (snapshot.Role != ObservatoryMembershipRole.Owner || snapshot.Policy is null || command.Policy is null) return Denied;
                var changed = await policy.SetAsync(observatory, actor, command.Policy with { ReasonCode = "central-workspace-policy" }, cancellationToken).ConfigureAwait(false);
                return changed switch
                {
                    CentralProcessingPolicyMutationOutcome.Applied => "Activated an observatory policy successor for future central jobs. Historical execution plans are unchanged.",
                    CentralProcessingPolicyMutationOutcome.Unchanged => "The observatory policy is already current.",
                    CentralProcessingPolicyMutationOutcome.Invalid => "The override is invalid for the current global configuration.",
                    _ => Denied
                };
            case CentralWorkspaceAction.PublishRevision or CentralWorkspaceAction.RetireRevision:
                if (!principal.IsInRole(AuthorizationRoleNames.PlatformEditor) || snapshot.Revision is not { } revision) return Denied;
                var catalogResult = command.Action == CentralWorkspaceAction.PublishRevision
                    ? await catalog.PublishRevisionAsync(revision.Id, actor, true, cancellationToken).ConfigureAwait(false)
                    : await catalog.RetireRevisionAsync(revision.Id, actor, "central-workspace-retirement", true, cancellationToken).ConfigureAwait(false);
                return $"Global catalog operation: {catalogResult.Outcome}. {catalogResult.ReasonCode}";
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
}
