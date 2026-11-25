using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

// Full-frame registration has a separate worker from closure and acquisition.
internal sealed partial class CenteredWindowRegistrationWorker(
    SqliteCaptureProcessingStore store,
    CaptureProcessingPersistence persistence,
    ICenteredWindowJournal journal,
    ICenteredWindowRegistrationSource source,
    TimeProvider timeProvider,
    ILogger<CenteredWindowRegistrationWorker> logger,
    RawCaptureIngress? ingress = null) : BackgroundService
{
    private const int BatchSize = 4;
    private CenteredWindowQueueEntry? _lastVisited;

    internal async ValueTask RegisterPendingAsync(CancellationToken cancellationToken)
    {
        var batch = await store.ReadPendingCenteredRegistrationsAsync(_lastVisited, BatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var work in batch)
        {
            try
            {
                var workState = await store.ReadCenteredWindowDispositionAsync(work.WindowIdentity, cancellationToken).ConfigureAwait(false);
                if (workState is { IsTerminal: false })
                    await RegisterAsync(workState, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The durable queue retains interrupted work for a retry or expiry.
                ReportFailure(logger, work.WindowIdentity, exception);
            }
        }
        _lastVisited = batch.Count == BatchSize ? batch[^1] : null;
    }

    private async ValueTask RegisterAsync(CenteredWindowDisposition state, CancellationToken cancellationToken)
    {
        var closure = state.Closure;
        var remaining = closure.Schedule.RegistrationDeadlineUtc - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return;

        using var deadline = new CancellationTokenSource(remaining, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var attempt = await CenteredWindowRegistrationResult.ExecuteAsync(closure, source, linked.Token).ConfigureAwait(false);
            if (attempt.InputFailure is not null)
                await RecordInputFailureAsync(closure, attempt, cancellationToken).ConfigureAwait(false);
            else if (attempt.Result is { } result)
            {
                await PublishAsync(state, result, linked.Token, cancellationToken).ConfigureAwait(false);
            }
            else if (attempt.Outcome.Status != ProcessingOutcomeStatus.RetryableFailure)
            {
                await store.FailCenteredRegistrationAsync(closure, attempt.Outcome.ReasonCode ?? "registration.failed",
                    journal, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Closure owns the durable expiry and releases the run's holds.
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or JsonException)
        {
            ReportFailure(logger, closure.Schedule.Identity, exception);
            await store.FailCenteredRegistrationAsync(closure, "registration.invalid-retained-evidence", journal,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(CenteredWindowDisposition state, CenteredWindowRegistrationResult result,
        CancellationToken attemptToken, CancellationToken cancellationToken)
    {
        var closure = state.Closure;
        var center = closure.Center.Input!;
        var rawPin = closure.Center.Products!.RegistrationPin!.Raw;
        ExposureInputV1? reference;
        try
        {
            reference = await source.ReadExposureAsync(center.Key, attemptToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (CenteredWindowRegistrationResult.IsRetryableInputRead(exception))
        {
            attemptToken.ThrowIfCancellationRequested();
            var failure = CenteredWindowRegistrationResult.UnreadableInput(center.Key, rawPin, exception);
            await RecordInputFailureAsync(closure, failure, cancellationToken).ConfigureAwait(false);
            return;
        }

        attemptToken.ThrowIfCancellationRequested();
        if (reference is null)
        {
            var failure = CenteredWindowRegistrationResult.UnreadableInput(center.Key, rawPin);
            await RecordInputFailureAsync(closure, failure, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (reference.Key != center.Key || CaptureContractJson.ComputeDescriptorSha256(reference.Raw) != center.RawDescriptorSha256)
            throw new InvalidDataException("The registration reference differs from the admitted capture.");

        // An output write failure must not inherit a previous input failure once every pin is readable again.
        if (state.InputFailure is not null)
            await store.UpdateCenteredRegistrationInputFailureAsync(closure, null, cancellationToken).ConfigureAwait(false);
        await persistence.PersistCenteredRegistrationAsync(result, reference.Raw, journal, attemptToken).ConfigureAwait(false);
    }

    private async Task RecordInputFailureAsync(CenteredWindowClosure closure, CenteredWindowRegistrationAttempt attempt,
        CancellationToken cancellationToken)
    {
        var failure = attempt.InputFailure!;
        if (await store.UpdateCenteredRegistrationInputFailureAsync(closure, failure, cancellationToken).ConfigureAwait(false))
            ReportUnreadableInput(logger, closure.Schedule.Identity, failure.Exposure.CaptureId,
                failure.Artifact.ArtifactId, failure.Kind, attempt.InputReadException);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        var evidenceReady = ingress is null;
        do
        {
            try
            {
                if (!evidenceReady)
                {
                    await ingress!.WaitForCenteredWindowEvidenceAsync(stoppingToken).ConfigureAwait(false);
                    evidenceReady = true;
                }
                await RegisterPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReportFailure(logger, "registration-queue", exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 6192, Level = LogLevel.Warning, Message = "Centered registration for {WindowIdentity} did not publish.")]
    private static partial void ReportFailure(ILogger logger, string windowIdentity, Exception exception);

    [LoggerMessage(EventId = 6193, Level = LogLevel.Warning,
        Message = "Centered registration for {WindowIdentity} could not read pinned artifact {ArtifactId} from capture {CaptureId} ({FailureKind}).")]
    private static partial void ReportUnreadableInput(ILogger logger, string windowIdentity, Guid captureId,
        Guid artifactId, CenteredRegistrationInputFailureKind failureKind, Exception? exception);
}
