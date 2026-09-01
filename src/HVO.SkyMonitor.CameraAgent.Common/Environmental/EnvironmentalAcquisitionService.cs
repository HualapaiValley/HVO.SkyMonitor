using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed partial class EnvironmentalAcquisitionService(
    EnvironmentalAcquisitionCoordinator coordinator,
    IEnvironmentalAcquisitionStateStore stateStore,
    ILocalEnvironmentalRetentionStore retentionStore,
    EnvironmentalAcquisitionTelemetry telemetry,
    TimeProvider timeProvider,
    IOptions<CameraAgentHostOptions> options,
    ILogger<EnvironmentalAcquisitionService> logger) : BackgroundService
{
    private long _queueDepth;
    private readonly Channel<EnvironmentalTriggerRequest> _requests = Channel.CreateBounded<EnvironmentalTriggerRequest>(
        new BoundedChannelOptions(options.Value.EnvironmentalAcquisition.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    public bool TryEnqueue(EnvironmentalTriggerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var written = _requests.Writer.TryWrite(request);
        if (written)
        {
            telemetry.SetQueueDepth(Interlocked.Increment(ref _queueDepth));
        }
        else
        {
            QueueRejected(logger, request.Trigger.ToString(), "queue-capacity");
        }
        return written;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AcquisitionInitialized(
            logger,
            coordinator.Sources.Count,
            coordinator.Sources.Count(static source => source.Required),
            options.Value.EnvironmentalAcquisition.MaximumConcurrency,
            options.Value.EnvironmentalAcquisition.QueueCapacity);
        var periodic = coordinator.Sources
            .Where(static source => source.Triggers.Contains(EnvironmentalAcquisitionTrigger.Periodic))
            .Select(source => RunPeriodicAsync(source, stoppingToken));
        return Task.WhenAll(periodic
            .Append(DrainRequestsAsync(stoppingToken))
            .Append(RunRetentionAsync(stoppingToken)));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "One queued trigger failure must not stop independent environmental acquisition.")]
    private async Task DrainRequestsAsync(CancellationToken cancellationToken)
    {
        await foreach (var request in _requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            telemetry.SetQueueDepth(Interlocked.Decrement(ref _queueDepth));
            try
            {
                _ = await coordinator.AcquireTriggerAsync(
                    request.Trigger,
                    request.ObservedAtUtc,
                    request.CaptureSequence,
                    request.CaptureId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                AcquisitionFailed(logger, "Multiple", request.Trigger.ToString(), "Failed", "unexpected");
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A transient schedule-store failure must remain bounded and retry without terminating other sources.")]
    private async Task RunPeriodicAsync(
        EnvironmentalSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var due = CurrentDue(source, now);
        if (due <= now)
        {
            await AcquireAsync(source, due, cancellationToken).ConfigureAwait(false);
            due = NextDue(source, timeProvider.GetUtcNow());
        }
        var journalUnavailable = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await stateStore.UpdateSourceScheduleAsync(
                    options.Value.RawIngressRoot, source, due, cancellationToken).ConfigureAwait(false);
                if (journalUnavailable)
                {
                    JournalRecovered(logger, "source-schedule");
                    journalUnavailable = false;
                }
                var delay = due - timeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                }
                await AcquireAsync(source, due, cancellationToken).ConfigureAwait(false);
                due = NextDue(source, timeProvider.GetUtcNow());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                if (!journalUnavailable)
                {
                    JournalUnavailable(logger, "source-schedule");
                    journalUnavailable = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed bounded retention pass must not terminate environmental acquisition.")]
    private async Task RunRetentionAsync(CancellationToken cancellationToken)
    {
        var journalUnavailable = false;
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var configured = options.Value.EnvironmentalAcquisition;
            var nextDelay = TimeSpan.FromHours(1);
            try
            {
                var result = await retentionStore.RetainLocalAsync(
                    options.Value.RawIngressRoot,
                    timeProvider.GetUtcNow().AddDays(-configured.RetentionDays),
                    configured.RetentionBatchSize,
                    cancellationToken).ConfigureAwait(false);
                if (journalUnavailable)
                {
                    JournalRecovered(logger, "retention");
                    journalUnavailable = false;
                }
                consecutiveFailures = 0;
                RetentionCompleted(logger, result.RemovedCount, result.RemovedBytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 2 && !journalUnavailable)
                {
                    JournalUnavailable(logger, "retention");
                    journalUnavailable = true;
                }
                nextDelay = TimeSpan.FromSeconds(1);
            }
            await Task.Delay(nextDelay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A single configured source failure must not stop independent periodic sources.")]
    private async Task AcquireAsync(
        EnvironmentalSourceDescriptor source,
        DateTimeOffset due,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await coordinator.AcquireSourceAsync(
                source.Id,
                EnvironmentalAcquisitionTrigger.Periodic,
                due,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            AcquisitionFailed(
                logger,
                source.Kind.ToString(),
                EnvironmentalAcquisitionTrigger.Periodic.ToString(),
                EnvironmentalAcquisitionDisposition.Failed.ToString(),
                "unexpected");
        }
    }

    private static DateTimeOffset CurrentDue(EnvironmentalSourceDescriptor source, DateTimeOffset now)
    {
        if (now < source.ScheduleEpochUtc)
        {
            return source.ScheduleEpochUtc;
        }
        var periodTicks = TimeSpan.FromSeconds(source.PeriodSeconds).Ticks;
        var elapsedPeriods = (now - source.ScheduleEpochUtc).Ticks / periodTicks;
        return source.ScheduleEpochUtc.AddTicks(elapsedPeriods * periodTicks);
    }

    private static DateTimeOffset NextDue(EnvironmentalSourceDescriptor source, DateTimeOffset now)
    {
        if (now < source.ScheduleEpochUtc)
        {
            return source.ScheduleEpochUtc;
        }
        var periodTicks = TimeSpan.FromSeconds(source.PeriodSeconds).Ticks;
        var elapsedPeriods = (now - source.ScheduleEpochUtc).Ticks / periodTicks;
        return source.ScheduleEpochUtc.AddTicks((elapsedPeriods + 1) * periodTicks);
    }

    [LoggerMessage(2520, LogLevel.Information,
        "Environmental acquisition initialized with {SourceCount} sources, {RequiredSourceCount} required sources, concurrency {MaximumConcurrency}, and queue capacity {QueueCapacity}.")]
    private static partial void AcquisitionInitialized(
        ILogger logger, int sourceCount, int requiredSourceCount, int maximumConcurrency, int queueCapacity);

    [LoggerMessage(2522, LogLevel.Error,
        "Environmental acquisition failed for kind {Kind}, trigger {Trigger}, outcome {Outcome}, and reason {Reason}.")]
    private static partial void AcquisitionFailed(
        ILogger logger, string kind, string trigger, string outcome, string reason);

    [LoggerMessage(2525, LogLevel.Warning,
        "Environmental trigger {Trigger} was rejected for reason {Reason}.")]
    private static partial void QueueRejected(ILogger logger, string trigger, string reason);

    [LoggerMessage(2526, LogLevel.Error,
        "Environmental journal operation {Operation} is unavailable.")]
    private static partial void JournalUnavailable(ILogger logger, string operation);

    [LoggerMessage(2527, LogLevel.Information,
        "Environmental journal operation {Operation} recovered.")]
    private static partial void JournalRecovered(ILogger logger, string operation);

    [LoggerMessage(2528, LogLevel.Information,
        "Environmental retention completed with {RemovedCount} records and {RemovedBytes} bytes removed.")]
    private static partial void RetentionCompleted(ILogger logger, int removedCount, long removedBytes);

}

public sealed record EnvironmentalTriggerRequest(
    EnvironmentalAcquisitionTrigger Trigger,
    DateTimeOffset ObservedAtUtc,
    long? CaptureSequence = null,
    Guid? CaptureId = null);
