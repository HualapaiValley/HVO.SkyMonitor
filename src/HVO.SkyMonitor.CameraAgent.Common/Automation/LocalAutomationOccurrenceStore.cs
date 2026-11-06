namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>Explicit commands prepare one fixed window/attempt; the existing runner remains execution authority.</summary>
public sealed record LocalAutomationBackfillRequest(
    string DefinitionId,
    DateOnly ReportDate,
    DateTimeOffset? HourStartUtc,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string Reason);

public sealed record LocalAutomationRetryRequest(
    string DefinitionId,
    string PreviousRunKey,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string Reason);

/// <summary>
/// One explicit operator request to run a periodic definition now. It queues a single occurrence for the
/// existing runner and never moves the definition's periodic schedule.
/// </summary>
public sealed record LocalAutomationRunNowRequest(
    string DefinitionId,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string Reason);

public interface ILocalAutomationOccurrenceStore
{
    /// <summary>Retained original identities, including prepared backfills and retries, for this exact revision.</summary>
    ValueTask<IReadOnlySet<string>> GetRecordedOccurrenceIdentitiesAsync(
        LocalAutomationRunnerEntry entry, CancellationToken cancellationToken);

    ValueTask<bool> TryBeginOccurrenceAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken);

    ValueTask<LocalAutomationCommandResult> BackfillAsync(
        LocalAutomationBackfillRequest request, CancellationToken cancellationToken);

    ValueTask<LocalAutomationCommandResult> RetryAsync(
        LocalAutomationRetryRequest request, CancellationToken cancellationToken);

    ValueTask<LocalAutomationRun?> TryClaimQueuedAsync(
        LocalAutomationRunnerEntry currentEntry, CancellationToken cancellationToken);

    /// <summary>
    /// Idempotently queues one run-now occurrence of an enabled periodic definition whose registered task
    /// accepts it. At most one run-now per definition is queued or running at a time, and the periodic
    /// progress is not touched, so the scheduled cadence and missed-occurrence accounting are unchanged.
    /// </summary>
    ValueTask<LocalAutomationCommandResult> RunNowAsync(
        LocalAutomationRunNowRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This automation store does not support an explicit run-now.");

    /// <summary>
    /// Looks up a recorded run-now idempotency key without writing anything. It returns
    /// <see cref="LocalAutomationCommandStatus.Replayed"/> when the key was recorded for this exact request,
    /// <see cref="LocalAutomationCommandStatus.Conflict"/> with
    /// <see cref="LocalAutomationContract.IdempotencyKeyConflictReasonCode"/> when it was recorded for a different
    /// command, and null when the key is unknown or the request is not a valid run-now command. The answer is exactly
    /// the one <see cref="RunNowAsync"/> would give first, so a caller that applies its own policy before queuing can
    /// answer a retry of an already executed request as a replay instead of meeting that policy again.
    /// </summary>
    ValueTask<LocalAutomationCommandResult?> FindRunNowReplayAsync(
        LocalAutomationRunNowRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult<LocalAutomationCommandResult?>(null);
}
