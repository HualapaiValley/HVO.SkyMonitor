using System.Threading.Channels;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

public enum TransientCandidateTransportDisposition
{
    Acknowledged,
    DependencyWaiting,
    Retry,
    AuthenticationBlocked,
    Rejected
}

public sealed record TransientCandidateTransportResult(
    TransientCandidateTransportDisposition Disposition,
    string Reason,
    TransientCandidateSubmissionAcknowledgementV1? Acknowledgement = null,
    TimeSpan? RetryAfter = null);

public interface ITransientCandidateTransport
{
    ValueTask<TransientCandidateTransportResult> SendAsync(
        TransientCandidateSubmissionEnvelopeV1 submission,
        CancellationToken cancellationToken);
}

public sealed class TransientCandidateDeliveryWakeup
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public void Signal() => _channel.Writer.TryWrite(true);

    internal async ValueTask WaitAsync(
        TimeSpan maximumDelay,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(maximumDelay, timeProvider, timeout.Token);
        var wake = _channel.Reader.ReadAsync(timeout.Token).AsTask();
        var completed = await Task.WhenAny(delay, wake).ConfigureAwait(false);
        await timeout.CancelAsync().ConfigureAwait(false);
        if (completed == wake)
        {
            await wake.ConfigureAwait(false);
        }
    }
}

public enum TransientCandidateDeliveryAvailability
{
    Disabled,
    Starting,
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record TransientCandidateDeliverySnapshot(
    TransientCandidateDeliveryAvailability Availability,
    string Reason,
    long PendingCount,
    int RetryingCount,
    int AuthenticationBlockedCount,
    long QuarantinedCount,
    DateTimeOffset? OldestPendingUtc,
    DateTimeOffset? LastAcknowledgedUtc,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastScanUtc,
    DateTimeOffset UpdatedUtc);

public sealed class TransientCandidateDeliveryState(TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private TransientCandidateDeliverySnapshot _snapshot = new(
        TransientCandidateDeliveryAvailability.Starting,
        "starting",
        0,
        0,
        0,
        0,
        null,
        null,
        null,
        null,
        timeProvider.GetUtcNow());

    public TransientCandidateDeliverySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    internal void SetDisabled()
        => Set(TransientCandidateDeliveryAvailability.Disabled, "disabled", new(0, 0, null), 0, 0, null, null, null);

    internal void Set(
        TransientCandidateDeliveryAvailability availability,
        string reason,
        TransientCandidateDeliveryAggregate aggregate,
        int retryingCount,
        int authenticationBlockedCount,
        DateTimeOffset? lastAcknowledgedUtc,
        DateTimeOffset? lastAttemptUtc,
        DateTimeOffset? lastScanUtc)
    {
        lock (_gate)
        {
            _snapshot = new(
                availability,
                Bound(reason),
                aggregate.PendingCount,
                retryingCount,
                authenticationBlockedCount,
                aggregate.QuarantinedCount,
                aggregate.OldestPendingUtc,
                lastAcknowledgedUtc ?? _snapshot.LastAcknowledgedUtc,
                lastAttemptUtc ?? _snapshot.LastAttemptUtc,
                lastScanUtc ?? _snapshot.LastScanUtc,
                timeProvider.GetUtcNow());
        }
    }

    internal void Fail(string reason, DateTimeOffset scanUtc)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Availability = TransientCandidateDeliveryAvailability.Unhealthy,
                Reason = Bound(reason),
                LastScanUtc = scanUtc,
                UpdatedUtc = timeProvider.GetUtcNow()
            };
        }
    }

    private static string Bound(string reason)
        => reason.Length <= 128 ? reason : reason[..128];
}

internal sealed class NullTransientCandidateTransport : ITransientCandidateTransport
{
    internal static readonly NullTransientCandidateTransport Instance = new();

    public ValueTask<TransientCandidateTransportResult> SendAsync(
        TransientCandidateSubmissionEnvelopeV1 submission,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new TransientCandidateTransportResult(
            TransientCandidateTransportDisposition.AuthenticationBlocked,
            "transport-unavailable"));
}

internal sealed class TransientCandidateDeliveryService(
    ITransientCandidateJournal journal,
    ArtifactOutboxState artifactOutboxState,
    IArtifactOutbox artifactOutbox,
    ITransientCandidateTransport transport,
    TransientCandidateDeliveryWakeup wakeup,
    TransientCandidateDeliveryState state,
    TransientWorkerTelemetry telemetry,
    IOptions<CameraAgentHostOptions> hostOptions,
    TimeProvider timeProvider,
    ILogger<TransientCandidateDeliveryService> logger) : BackgroundService
{
    internal const string ModeDisabledReason = "hybrid-submission.mode-disabled";
    internal const string EvidenceMissingReason = "hybrid-submission.evidence-missing";
    internal const string EvidenceUnavailableReason = "hybrid-submission.evidence-unavailable";
    internal const int MaximumBatchCount = 64;
    internal const int MaximumConcurrentSends = 4;
    internal const int MaximumRetryBatchCount = 16;
    private readonly Dictionary<Guid, RetryState> _retries = [];
    private readonly TransientDetectionOptions _options = hostOptions.Value.TransientDetection;
    private readonly bool _enabled = hostOptions.Value.CentralIntegration.Mode == CentralIntegrationMode.Enabled &&
        hostOptions.Value.TransientDetection.Mode == TransientOperatingMode.Hybrid;
    private TransientCandidateDeliveryCursor? _cursor;
    private DateTimeOffset? _lastAcknowledgedUtc;
    private DateTimeOffset? _lastAttemptUtc;
    private DateTimeOffset? _lastScanUtc;
    private DependencyCircuit? _dependencyCircuit;
    private bool _drainImmediately;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The hosted delivery loop must remain available to retry durable submissions after an iteration failure.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            state.SetDisabled();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await DeliverBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _lastScanUtc = timeProvider.GetUtcNow();
                state.Fail("scan-failed", _lastScanUtc.Value);
                telemetry.Record("delivery", "scan-failed", TimeSpan.Zero);
                TransientCandidateDeliveryLog.ScanFailed(logger, exception.GetType().Name, exception);
            }

            var delay = GetWaitDelay(timeProvider.GetUtcNow());
            if (_dependencyCircuit is null)
            {
                await wakeup.WaitAsync(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Transport implementations are isolated so one failed candidate cannot block the durable delivery batch.")]
    internal async ValueTask<int> DeliverBatchAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        if (_dependencyCircuit is { } circuit)
        {
            if (circuit.NextAttemptUtc > now)
            {
                await UpdateStateAsync(dependencyWaiting: false, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            try
            {
                var retained = await journal.ReadAsync(circuit.CandidateId, cancellationToken).ConfigureAwait(false);
                if (retained is not { Phase: TransientCandidateWorkflowPhase.HandoffPending, Submission: not null } ||
                    retained.SourceHoldReleased)
                {
                    var replacement = await FindReplacementCanaryAsync(
                        circuit.CandidateId, cancellationToken).ConfigureAwait(false);
                    var retainedCircuit = replacement is null
                        ? circuit
                        : circuit with { CandidateId = replacement.CandidateId };
                    AdvanceDependencyCircuit(
                        retainedCircuit,
                        timeProvider.GetUtcNow(),
                        requestedDelay: null,
                        "canary-retained-candidate-unavailable");
                    await UpdateStateAsync(dependencyWaiting: false, cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                var canary = await SendAsync(retained, cancellationToken).ConfigureAwait(false);
                RecordAttempt(canary);
                if (ProvesModeAvailable(canary))
                {
                    await SettleCandidateSafelyAsync(canary, cancellationToken).ConfigureAwait(false);
                    CloseDependencyCircuit();
                }
                else
                {
                    if (IsTerminal(canary))
                    {
                        await SettleCandidateSafelyAsync(canary, cancellationToken).ConfigureAwait(false);
                        var replacement = await FindReplacementCanaryAsync(
                            circuit.CandidateId, cancellationToken).ConfigureAwait(false);
                        if (replacement is not null)
                        {
                            circuit = circuit with { CandidateId = replacement.CandidateId };
                        }
                    }
                    RecordSharedDependencyWait(canary);
                    AdvanceDependencyCircuit(circuit, canary.CompletedUtc, canary.Result.RetryAfter, canary.Result.Reason);
                }
                await UpdateStateAsync(dependencyWaiting: false, cancellationToken).ConfigureAwait(false);
                return 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (_dependencyCircuit is null || _dependencyCircuit.NextAttemptUtc <= now)
                {
                    AdvanceDependencyCircuit(
                        circuit,
                        timeProvider.GetUtcNow(),
                        requestedDelay: null,
                        $"canary-{exception.GetType().Name}");
                }
                throw;
            }
        }

        var artifactOutboxSnapshot = artifactOutboxState.Snapshot;
        var dependencyWaiting = artifactOutboxSnapshot.Availability is
            ArtifactOutboxAvailability.Initializing or ArtifactOutboxAvailability.Unavailable;
        var trackedArtifactIds = new HashSet<Guid>();
        var pendingPage = dependencyWaiting
            ? null
            : await journal.ReadPendingDeliveryPageAsync(
                _cursor, MaximumBatchCount, cancellationToken).ConfigureAwait(false);
        if (pendingPage is { Entries.Count: 0 } && _cursor is not null)
        {
            _cursor = null;
            pendingPage = await journal.ReadPendingDeliveryPageAsync(
                after: null, MaximumBatchCount, cancellationToken).ConfigureAwait(false);
        }
        if (pendingPage is not null)
        {
            trackedArtifactIds.UnionWith(pendingPage.Entries.SelectMany(entry =>
                entry.Submission!.Candidate.ContextSources.Select(source => source.Locator.Artifact.ArtifactId)));
        }
        var acknowledgedArtifactIds = new HashSet<Guid>();
        foreach (var root in artifactOutboxState.Roots)
        {
            try
            {
                var acknowledged = await artifactOutbox.GetAcknowledgedArtifactIdsAsync(
                    root, trackedArtifactIds, cancellationToken).ConfigureAwait(false);
                acknowledgedArtifactIds.UnionWith(acknowledged);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                dependencyWaiting = true;
            }
        }
        var entries = new List<TransientCandidateJournalEntry>(MaximumBatchCount);
        var candidateIds = new HashSet<Guid>();
        var discoveryCursors = new Dictionary<Guid, TransientCandidateDeliveryCursor>();
        var dueRetryIds = _retries
            .Where(value => value.Value.NextAttemptUtc <= now)
            .OrderBy(value => value.Value.NextAttemptUtc)
            .ThenBy(value => value.Key)
            .Take(MaximumRetryBatchCount)
            .Select(value => value.Key)
            .ToArray();
        foreach (var candidateId in dueRetryIds)
        {
            var entry = await journal.ReadAsync(candidateId, cancellationToken).ConfigureAwait(false);
            if (entry is { Phase: TransientCandidateWorkflowPhase.HandoffPending, Submission: not null } &&
                !entry.SourceHoldReleased)
            {
                entries.Add(entry);
                candidateIds.Add(entry.CandidateId);
            }
            else
            {
                _retries.Remove(candidateId);
            }
        }

        if (!dependencyWaiting && pendingPage is not null)
        {
            var publicationTracked = artifactOutboxState.Roots.Count > 0;
            var freshCandidateSeen = false;
            foreach (var entry in pendingPage.Entries.Take(MaximumBatchCount - entries.Count))
            {
                if (publicationTracked && entry.Submission!.Candidate.ContextSources.Any(source =>
                        !acknowledgedArtifactIds.Contains(source.Locator.Artifact.ArtifactId)))
                {
                    dependencyWaiting = true;
                    break;
                }
                if (candidateIds.Add(entry.CandidateId) &&
                    (!_retries.TryGetValue(entry.CandidateId, out var retry) || retry.NextAttemptUtc <= now))
                {
                    entries.Add(entry);
                    discoveryCursors[entry.CandidateId] = new(entry.CreatedUtc, entry.CandidateId);
                    freshCandidateSeen = true;
                }
                else if (!freshCandidateSeen && _retries.ContainsKey(entry.CandidateId))
                {
                    _cursor = new(entry.CreatedUtc, entry.CandidateId);
                }
            }
        }

        var waiting = new Queue<TransientCandidateJournalEntry>(entries);
        var attemptedCount = 0;
        while (waiting.Count > 0)
        {
            var active = new List<Task<SendOutcome>>(MaximumConcurrentSends);
            while (active.Count < MaximumConcurrentSends && waiting.TryDequeue(out var entry))
            {
                active.Add(SendAsync(entry, cancellationToken));
                if (discoveryCursors.TryGetValue(entry.CandidateId, out var dispatchedCursor))
                {
                    _cursor = dispatchedCursor;
                }
                attemptedCount++;
            }

            var modeDisabled = false;
            Exception? waveFailure = null;
            while (active.Count > 0)
            {
                var completed = await Task.WhenAny(active).ConfigureAwait(false);
                active.Remove(completed);
                SendOutcome outcome;
                try
                {
                    outcome = await completed.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    waveFailure ??= exception;
                    continue;
                }
                RecordAttempt(outcome);
                if (IsModeDisabled(outcome.Result))
                {
                    RetainModeDisabledCandidate(outcome);
                    if (!modeDisabled)
                    {
                        OpenDependencyCircuit(outcome);
                        modeDisabled = true;
                    }
                    else
                    {
                        telemetry.Record("delivery", "dependency-wait", outcome.Duration);
                    }
                }
                else
                {
                    try
                    {
                        await SettleCandidateAsync(outcome, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        ScheduleSettlementRetry(outcome, exception);
                        waveFailure ??= exception;
                    }
                }
            }
            if (waveFailure is not null)
            {
                throw waveFailure;
            }
            if (modeDisabled)
            {
                break;
            }
        }

        await UpdateStateAsync(dependencyWaiting, cancellationToken).ConfigureAwait(false);
        return attemptedCount;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Each transport attempt is isolated so a failed request cannot cancel sibling durable deliveries.")]
    private async Task<SendOutcome> SendAsync(
        TransientCandidateJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        TransientCandidateTransportResult result;
        try
        {
            result = await transport.SendAsync(entry.Submission!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = new(TransientCandidateTransportDisposition.Retry, $"transport-{exception.GetType().Name}");
        }
        return new(entry, result, timeProvider.GetUtcNow(), timeProvider.GetElapsedTime(started));
    }

    private async Task SettleCandidateAsync(SendOutcome outcome, CancellationToken cancellationToken)
    {
        var entry = outcome.Entry;
        var submission = entry.Submission!;
        var result = outcome.Result;
        if (result.Disposition == TransientCandidateTransportDisposition.Rejected)
        {
            await journal.QuarantineDeliveryAsync(
                entry.CandidateId, entry.EventId, result.Reason, cancellationToken).ConfigureAwait(false);
            _retries.Remove(entry.CandidateId);
            telemetry.Record("delivery", "rejected", outcome.Duration);
            TransientCandidateDeliveryLog.Quarantined(logger, result.Reason);
            return;
        }
        if (result is
            {
                Disposition: TransientCandidateTransportDisposition.Acknowledged,
                Acknowledgement: { } acknowledgement
            } && TransientCandidateDeliveryJson.Matches(acknowledgement, submission))
        {
            await journal.AcknowledgeAsync(
                entry.CandidateId, entry.EventId, acknowledgement, cancellationToken).ConfigureAwait(false);
            _retries.Remove(entry.CandidateId);
            _lastAcknowledgedUtc = Later(_lastAcknowledgedUtc, outcome.CompletedUtc);
            telemetry.Record(
                "delivery",
                acknowledgement.Disposition == TransientCandidateSubmissionDisposition.Accepted
                    ? "accepted"
                    : acknowledgement.Disposition == TransientCandidateSubmissionDisposition.Duplicate
                        ? "duplicate"
                        : "retired",
                outcome.Duration);
            TransientCandidateDeliveryLog.Acknowledged(logger, result.Reason);
            return;
        }

        _retries.TryGetValue(entry.CandidateId, out var retry);
        var attempts = retry?.Attempts + 1 ?? 1;
        var delay = RetryDelay(attempts, result.RetryAfter);
        var disposition = result.Disposition switch
        {
            TransientCandidateTransportDisposition.AuthenticationBlocked =>
                TransientCandidateTransportDisposition.AuthenticationBlocked,
            TransientCandidateTransportDisposition.DependencyWaiting =>
                TransientCandidateTransportDisposition.DependencyWaiting,
            _ => TransientCandidateTransportDisposition.Retry
        };
        _retries[entry.CandidateId] = new RetryState(attempts, outcome.CompletedUtc + delay, disposition);
        telemetry.Record(
            "delivery",
            disposition switch
            {
                TransientCandidateTransportDisposition.AuthenticationBlocked => "authentication-blocked",
                TransientCandidateTransportDisposition.DependencyWaiting => "dependency-wait",
                _ => "retry"
            },
            outcome.Duration);
        if (disposition == TransientCandidateTransportDisposition.DependencyWaiting)
        {
            TransientCandidateDeliveryLog.DependencyWaiting(logger, result.Reason, (long)delay.TotalMilliseconds);
        }
        else
        {
            TransientCandidateDeliveryLog.Retrying(logger, result.Reason, (long)delay.TotalMilliseconds);
        }
    }

    private async Task SettleCandidateSafelyAsync(SendOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await SettleCandidateAsync(outcome, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ScheduleSettlementRetry(outcome, exception);
            throw;
        }
    }

    private void ScheduleSettlementRetry(SendOutcome outcome, Exception exception)
    {
        _retries.TryGetValue(outcome.Entry.CandidateId, out var retry);
        var attempts = retry?.Attempts + 1 ?? 1;
        var delay = RetryDelay(attempts, requestedDelay: null);
        _retries[outcome.Entry.CandidateId] = new(
            attempts,
            outcome.CompletedUtc + delay,
            TransientCandidateTransportDisposition.Retry);
        TransientCandidateDeliveryLog.Retrying(
            logger,
            $"settlement-{exception.GetType().Name}",
            (long)delay.TotalMilliseconds);
    }

    private void RecordAttempt(SendOutcome outcome)
        => _lastAttemptUtc = Later(_lastAttemptUtc, outcome.CompletedUtc);

    internal TimeSpan GetWaitDelay(DateTimeOffset now)
    {
        if (_drainImmediately)
        {
            _drainImmediately = false;
            return TimeSpan.Zero;
        }
        if (_dependencyCircuit is { } circuit)
        {
            var circuitDelay = circuit.NextAttemptUtc - now;
            return circuitDelay <= TimeSpan.Zero ? TimeSpan.Zero : circuitDelay;
        }

        var poll = TimeSpan.FromMilliseconds(_options.WorkerPollIntervalMilliseconds);
        DateTimeOffset? earliest = _retries.Count == 0
            ? null
            : _retries.Min(value => value.Value.NextAttemptUtc);
        if (earliest is null)
        {
            return poll;
        }
        var retryDelay = earliest.Value - now;
        return retryDelay <= TimeSpan.Zero ? TimeSpan.Zero : retryDelay < poll ? retryDelay : poll;
    }

    private async ValueTask UpdateStateAsync(bool dependencyWaiting, CancellationToken cancellationToken)
    {
        _lastScanUtc = timeProvider.GetUtcNow();
        var aggregate = await journal.ReadDeliveryAggregateAsync(cancellationToken).ConfigureAwait(false);
        UpdateState(aggregate, dependencyWaiting);
    }

    private void UpdateState(TransientCandidateDeliveryAggregate aggregate, bool dependencyWaiting = false)
    {
        var authenticationBlocked = _retries.Count(value =>
            value.Value.Disposition == TransientCandidateTransportDisposition.AuthenticationBlocked);
        var centralWaiting = _retries.Count(value =>
            value.Value.Disposition == TransientCandidateTransportDisposition.DependencyWaiting &&
            !value.Value.RetainedModeDisabled);
        var retainedModeDisabled = _retries.Count(value => value.Value.RetainedModeDisabled);
        var retrying = _retries.Count - authenticationBlocked - centralWaiting - retainedModeDisabled;
        var availability = aggregate.QuarantinedCount > 0
            ? TransientCandidateDeliveryAvailability.Unhealthy
            : authenticationBlocked > 0 || retrying > 0
                ? TransientCandidateDeliveryAvailability.Degraded
                : TransientCandidateDeliveryAvailability.Healthy;
        var reason = aggregate.QuarantinedCount > 0
            ? "quarantined"
            : _dependencyCircuit is not null
                ? "waiting-central-mode"
                : authenticationBlocked > 0
                    ? "authentication-blocked"
                    : retrying > 0
                        ? "retrying"
                        : centralWaiting > 0
                            ? "waiting-central-evidence"
                            : dependencyWaiting ? "waiting-artifact-upload" : "ready";
        state.Set(
            availability,
            reason,
            aggregate,
            retrying,
            authenticationBlocked,
            _lastAcknowledgedUtc,
            _lastAttemptUtc,
            _lastScanUtc);
    }

    private void OpenDependencyCircuit(SendOutcome outcome)
    {
        var delay = RetryDelay(attempts: 1, outcome.Result.RetryAfter);
        _dependencyCircuit = new(outcome.Entry.CandidateId, Attempts: 1, outcome.CompletedUtc + delay);
        _drainImmediately = false;
        telemetry.Record("delivery", "dependency-wait", outcome.Duration);
        TransientCandidateDeliveryLog.DependencyWaiting(
            logger, outcome.Result.Reason, (long)delay.TotalMilliseconds);
    }

    private void RetainModeDisabledCandidate(SendOutcome outcome)
    {
        _retries[outcome.Entry.CandidateId] = new(
            Attempts: 1,
            NextAttemptUtc: outcome.CompletedUtc,
            TransientCandidateTransportDisposition.DependencyWaiting,
            RetainedModeDisabled: true);
    }

    private void AdvanceDependencyCircuit(
        DependencyCircuit circuit,
        DateTimeOffset completedUtc,
        TimeSpan? requestedDelay,
        string reason)
    {
        var attempts = circuit.Attempts + 1;
        var delay = RetryDelay(attempts, requestedDelay);
        _dependencyCircuit = circuit with { Attempts = attempts, NextAttemptUtc = completedUtc + delay };
        _drainImmediately = false;
        TransientCandidateDeliveryLog.DependencyWaiting(logger, reason, (long)delay.TotalMilliseconds);
    }

    private void RecordSharedDependencyWait(SendOutcome outcome)
    {
        telemetry.Record(
            "delivery",
            IsModeDisabled(outcome.Result) ? "dependency-wait" : "retry",
            outcome.Duration);
    }

    private void CloseDependencyCircuit()
    {
        if (_dependencyCircuit is null)
        {
            return;
        }
        _dependencyCircuit = null;
        _drainImmediately = true;
    }

    private static bool IsModeDisabled(TransientCandidateTransportResult result)
        => result.Disposition == TransientCandidateTransportDisposition.DependencyWaiting &&
            string.Equals(result.Reason, ModeDisabledReason, StringComparison.Ordinal);

    private static bool ProvesModeAvailable(SendOutcome outcome)
    {
        var result = outcome.Result;
        return result.Disposition == TransientCandidateTransportDisposition.DependencyWaiting &&
                result.Reason is EvidenceMissingReason or EvidenceUnavailableReason ||
            result is
            {
                Disposition: TransientCandidateTransportDisposition.Acknowledged,
                Acknowledgement: { } acknowledgement
            } && acknowledgement.Disposition == TransientCandidateSubmissionDisposition.Accepted &&
                TransientCandidateDeliveryJson.Matches(acknowledgement, outcome.Entry.Submission!);
    }

    private static bool IsTerminal(SendOutcome outcome)
        => outcome.Result.Disposition == TransientCandidateTransportDisposition.Rejected ||
            outcome.Result is
            {
                Disposition: TransientCandidateTransportDisposition.Acknowledged,
                Acknowledgement: { } acknowledgement
            } && TransientCandidateDeliveryJson.Matches(acknowledgement, outcome.Entry.Submission!);

    private async ValueTask<TransientCandidateJournalEntry?> FindReplacementCanaryAsync(
        Guid excludedCandidateId,
        CancellationToken cancellationToken)
    {
        var retainedCandidateIds = _retries
            .Where(value => value.Key != excludedCandidateId && value.Value.RetainedModeDisabled)
            .OrderBy(value => value.Key)
            .Select(value => value.Key)
            .ToArray();
        foreach (var candidateId in retainedCandidateIds)
        {
            var retained = await journal.ReadAsync(candidateId, cancellationToken).ConfigureAwait(false);
            if (retained is { Phase: TransientCandidateWorkflowPhase.HandoffPending, Submission: not null } &&
                !retained.SourceHoldReleased)
            {
                return retained;
            }
            _retries.Remove(candidateId);
        }

        var page = await journal.ReadPendingDeliveryPageAsync(
            after: null, maximumCount: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
        return page.Entries.FirstOrDefault(entry => entry.CandidateId != excludedCandidateId);
    }

    private static DateTimeOffset Later(DateTimeOffset? current, DateTimeOffset candidate)
        => current is null || candidate > current.Value ? candidate : current.Value;

    private TimeSpan RetryDelay(int attempts, TimeSpan? requestedDelay)
    {
        var exponent = Math.Min(Math.Max(0, attempts - 1), _options.MaximumAttempts - 1);
        var seconds = _options.RetryInitialDelaySeconds * Math.Pow(2, exponent);
        var bounded = TimeSpan.FromSeconds(Math.Min(seconds, _options.RetryMaximumDelaySeconds));
        if (requestedDelay is { } requested && requested > bounded)
        {
            bounded = requested > TimeSpan.FromSeconds(_options.RetryMaximumDelaySeconds)
                ? TimeSpan.FromSeconds(_options.RetryMaximumDelaySeconds)
                : requested;
        }
        return bounded;
    }

    private sealed record RetryState(
        int Attempts,
        DateTimeOffset NextAttemptUtc,
        TransientCandidateTransportDisposition Disposition,
        bool RetainedModeDisabled = false);
    private sealed record SendOutcome(
        TransientCandidateJournalEntry Entry,
        TransientCandidateTransportResult Result,
        DateTimeOffset CompletedUtc,
        TimeSpan Duration);
    private sealed record DependencyCircuit(Guid CandidateId, int Attempts, DateTimeOffset NextAttemptUtc);
}

internal static partial class TransientCandidateDeliveryLog
{
    [LoggerMessage(2252, LogLevel.Debug, "Transient candidate delivery acknowledged with outcome {Outcome}")]
    internal static partial void Acknowledged(ILogger logger, string outcome);

    [LoggerMessage(2253, LogLevel.Warning, "Transient candidate delivery retained evidence because {Reason}; retrying after {DelayMilliseconds} ms")]
    internal static partial void Retrying(ILogger logger, string reason, long delayMilliseconds);

    [LoggerMessage(2254, LogLevel.Error, "Transient candidate delivery scan failed because {ExceptionType}")]
    internal static partial void ScanFailed(ILogger logger, string exceptionType, Exception exception);

    [LoggerMessage(2255, LogLevel.Error, "Transient candidate delivery quarantined retained evidence because {Reason}")]
    internal static partial void Quarantined(ILogger logger, string reason);

    [LoggerMessage(2256, LogLevel.Information, "Transient candidate delivery is waiting for central dependency {Reason}; checking again after {DelayMilliseconds} ms")]
    internal static partial void DependencyWaiting(ILogger logger, string reason, long delayMilliseconds);
}
