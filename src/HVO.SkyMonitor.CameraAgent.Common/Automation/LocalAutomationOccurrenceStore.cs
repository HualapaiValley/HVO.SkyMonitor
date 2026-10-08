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
}
