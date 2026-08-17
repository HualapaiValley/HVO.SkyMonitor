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

            await wakeup.WaitAsync(
                GetWaitDelay(timeProvider.GetUtcNow()),
                timeProvider,
                stoppingToken).ConfigureAwait(false);
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
                }
                _cursor = new TransientCandidateDeliveryCursor(entry.CreatedUtc, entry.CandidateId);
            }
        }

        var waiting = new Queue<TransientCandidateJournalEntry>(entries);
        var active = new List<Task<SendOutcome>>(MaximumConcurrentSends);
        while (active.Count < MaximumConcurrentSends && waiting.TryDequeue(out var entry))
        {
            active.Add(SendAsync(entry, cancellationToken));
        }
        while (active.Count > 0)
        {
            var completed = await Task.WhenAny(active).ConfigureAwait(false);
            active.Remove(completed);
            await SettleAsync(await completed.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            if (waiting.TryDequeue(out var entry))
            {
                active.Add(SendAsync(entry, cancellationToken));
            }
        }

        _lastScanUtc = timeProvider.GetUtcNow();
        var aggregate = await journal.ReadDeliveryAggregateAsync(cancellationToken).ConfigureAwait(false);
        UpdateState(aggregate, dependencyWaiting);
        return entries.Count;
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

    private async Task SettleAsync(SendOutcome outcome, CancellationToken cancellationToken)
    {
        var entry = outcome.Entry;
        var submission = entry.Submission!;
        var result = outcome.Result;
        _lastAttemptUtc = Later(_lastAttemptUtc, outcome.CompletedUtc);
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
                    : "duplicate",
                outcome.Duration);
            TransientCandidateDeliveryLog.Acknowledged(logger, result.Reason);
            return;
        }

        _retries.TryGetValue(entry.CandidateId, out var retry);
        var attempts = retry?.Attempts + 1 ?? 1;
        var delay = RetryDelay(attempts, result.RetryAfter);
        var disposition = result.Disposition == TransientCandidateTransportDisposition.AuthenticationBlocked
            ? TransientCandidateTransportDisposition.AuthenticationBlocked
            : TransientCandidateTransportDisposition.Retry;
        _retries[entry.CandidateId] = new RetryState(attempts, outcome.CompletedUtc + delay, disposition);
        telemetry.Record(
            "delivery",
            disposition == TransientCandidateTransportDisposition.AuthenticationBlocked
                ? "authentication-blocked"
                : "retry",
            outcome.Duration);
        TransientCandidateDeliveryLog.Retrying(logger, result.Reason, (long)delay.TotalMilliseconds);
    }

    internal TimeSpan GetWaitDelay(DateTimeOffset now)
    {
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

    private void UpdateState(TransientCandidateDeliveryAggregate aggregate, bool dependencyWaiting = false)
    {
        var authenticationBlocked = _retries.Count(value =>
            value.Value.Disposition == TransientCandidateTransportDisposition.AuthenticationBlocked);
        var retrying = _retries.Count - authenticationBlocked;
        var availability = aggregate.QuarantinedCount > 0
            ? TransientCandidateDeliveryAvailability.Unhealthy
            : authenticationBlocked > 0 || retrying > 0
                ? TransientCandidateDeliveryAvailability.Degraded
                : TransientCandidateDeliveryAvailability.Healthy;
        var reason = aggregate.QuarantinedCount > 0
            ? "quarantined"
            : authenticationBlocked > 0
                ? "authentication-blocked"
                : retrying > 0
                    ? "retrying"
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
        TransientCandidateTransportDisposition Disposition);
    private sealed record SendOutcome(
        TransientCandidateJournalEntry Entry,
        TransientCandidateTransportResult Result,
        DateTimeOffset CompletedUtc,
        TimeSpan Duration);
}

internal static partial class TransientCandidateDeliveryLog
{
    [LoggerMessage(2520, LogLevel.Debug, "Transient candidate delivery acknowledged with outcome {Outcome}")]
    internal static partial void Acknowledged(ILogger logger, string outcome);

    [LoggerMessage(2521, LogLevel.Warning, "Transient candidate delivery retained evidence because {Reason}; retrying after {DelayMilliseconds} ms")]
    internal static partial void Retrying(ILogger logger, string reason, long delayMilliseconds);

    [LoggerMessage(2522, LogLevel.Error, "Transient candidate delivery scan failed because {ExceptionType}")]
    internal static partial void ScanFailed(ILogger logger, string exceptionType, Exception exception);

    [LoggerMessage(2523, LogLevel.Error, "Transient candidate delivery quarantined retained evidence because {Reason}")]
    internal static partial void Quarantined(ILogger logger, string reason);
}
