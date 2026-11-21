using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
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
            CenteredWindowClosure? closure = null;
            try
            {
                var workState = await store.ReadCenteredWindowDispositionAsync(work.WindowIdentity, cancellationToken).ConfigureAwait(false);
                if (workState is { IsTerminal: false })
                {
                    closure = workState.Closure;
                    await RegisterAsync(closure, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or KeyNotFoundException)
            {
                ReportFailure(logger, work.WindowIdentity, exception);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or System.Text.Json.JsonException)
            {
                if (closure is not null)
                    await store.FailCenteredRegistrationAsync(closure, "registration.invalid-retained-evidence", journal,
                        cancellationToken).ConfigureAwait(false);
                ReportFailure(logger, work.WindowIdentity, exception);
            }
        }
        _lastVisited = batch.Count == BatchSize ? batch[^1] : null;
    }

    private async ValueTask RegisterAsync(CenteredWindowClosure closure, CancellationToken cancellationToken)
    {
        var remaining = closure.Schedule.RegistrationDeadlineUtc - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return;

        using var deadline = new CancellationTokenSource(remaining, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var attempt = await CenteredWindowRegistrationResult.ExecuteAsync(closure, source, linked.Token).ConfigureAwait(false);
            if (attempt.Result is { } result)
            {
                var center = closure.Center.Input!;
                var reference = await source.ReadExposureAsync(center.Key, linked.Token).ConfigureAwait(false);
                if (reference is null || reference.Key != center.Key ||
                    CaptureContractJson.ComputeDescriptorSha256(reference.Raw) != center.RawDescriptorSha256)
                    throw new InvalidDataException("The registration reference differs from the admitted capture.");

                await persistence.PersistCenteredRegistrationAsync(result, reference.Raw, journal, linked.Token)
                    .ConfigureAwait(false);
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ingress is not null)
            await ingress.WaitForCenteredWindowEvidenceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        do
        {
            try
            {
                await RegisterPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
            {
                ReportFailure(logger, "registration-queue", exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 6192, Level = LogLevel.Warning, Message = "Centered registration for {WindowIdentity} did not publish.")]
    private static partial void ReportFailure(ILogger logger, string windowIdentity, Exception exception);
}
