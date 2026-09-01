using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class ProcessingReplayWorker(
    SqliteCaptureProcessingStore store,
    ICaptureProcessingPipelineFactory pipelineFactory,
    CaptureProcessingPersistence persistence,
    CaptureProcessingTelemetry telemetry,
    ProcessingReplayWakeup wakeup,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<ProcessingReplayWorker> logger) : BackgroundService
{
    private readonly ProcessingGraphExecutionOptions _options = options.Value.ProcessingGraphs;
    private readonly string _ownerPrefix = $"replay-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private static readonly Action<ILogger, Exception?> ClaimFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2180, "ProcessingReplayClaimFailed"),
        "Processing replay claim failed.");
    private static readonly Action<ILogger, Guid, Exception?> ExecutionFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2181, "ProcessingReplayExecutionFailed"),
        "Processing replay execution {ExecutionId} failed.");
    private static readonly Action<ILogger, Guid, Exception?> LeaseLost = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2182, "ProcessingReplayLeaseLost"),
        "Processing replay {ExecutionId} lost its lease.");

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(Enumerable.Range(0, _options.ReplayMaximumConcurrency)
            .Select(index => RunWorkerAsync($"{_ownerPrefix}-{index}", stoppingToken)));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Replay failures are converted to fenced durable retry state so the worker remains available.")]
    private async Task RunWorkerAsync(string owner, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var livePreemption = new CancellationTokenSource();
            using var preemptionRegistration = wakeup.RegisterLivePreemption(livePreemption);
            ProcessingReplayLease? lease;
            try
            {
                lease = await store.ClaimReplayAsync(owner, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ClaimFailed(logger, exception);
                await WaitAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }
            if (lease is null)
            {
                await WaitAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }
            telemetry.RecordReplayClaim(lease.Execution.AcceptedUtc, timeProvider.GetUtcNow());
            try
            {
                await ProcessLeaseAsync(lease, livePreemption.Token, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ExecutionFailed(logger, lease.Execution.ExecutionId, exception);
                await WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The replay boundary durably records a retry instead of terminating the worker.")]
    private async Task ProcessLeaseAsync(
        ProcessingReplayLease lease,
        CancellationToken livePreemptionToken,
        CancellationToken stoppingToken)
    {
        var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, livePreemptionToken);
        var renewalCancellation = new CancellationTokenSource();
        var renewal = RenewLeaseAsync(lease, processingCancellation, renewalCancellation.Token);
        CaptureLaneHandlerResult result;
        try
        {
            using var graph = new ReplayGraph(pipelineFactory.CreateGraph(lease.Configuration));
            VerifyFrozenPlan(graph.Value, lease.Revision);
            var execution = new ProcessingExecutionContext(
                lease.Execution.ExecutionId,
                ProcessingGraphExecutionClass.Replay,
                lease.Execution.GraphRevisionId,
                lease.Execution.LocalPlanIdentitySha256,
                false,
                lease.WorkId,
                lease.LeaseToken,
                lease.LeaseOwner,
                lease.Execution.DeadlineUtc);
            result = await FrameProcessingWorker.ProcessGraphItemAsync(
                new FrameProcessingItem(
                    lease.Configuration,
                    lease.Submission,
                    lease.RawCapture,
                    lease.WorkId,
                    lease.LeaseToken,
                    execution),
                graph.Value,
                persistence,
                telemetry,
                lease.Execution.AttemptCount,
                logger,
                processingCancellation.Token,
                _options.ReplayMaximumAttempts,
                timeProvider,
                durableAttempt: lease.ClaimCount,
                cancellationDisposition: () => ReplayCancellationResult(livePreemptionToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
        {
            result = ReplayCancellationResult(livePreemptionToken);
        }
        catch (Exception exception)
        {
            ExecutionFailed(logger, lease.Execution.ExecutionId, exception);
            result = CaptureLaneHandlerResult.Retry("processing.replay-exception");
        }
        finally
        {
            await renewalCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                renewalCancellation.Dispose();
                processingCancellation.Dispose();
            }
        }
        try
        {
            var completed = await store.CompleteReplayAsync(lease, result, CancellationToken.None).ConfigureAwait(false);
            telemetry.RecordReplayDisposition(completed);
        }
        catch (CaptureLaneLeaseLostException exception)
        {
            LeaseLost(logger, lease.Execution.ExecutionId, exception);
        }
        catch (Exception exception)
        {
            ExecutionFailed(logger, lease.Execution.ExecutionId, exception);
        }
    }

    private async Task RenewLeaseAsync(
        ProcessingReplayLease lease,
        CancellationTokenSource processingCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, _options.ReplayLeaseSeconds / 3)),
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
                if (!await store.RenewReplayAsync(lease, cancellationToken).ConfigureAwait(false))
                {
                    await processingCancellation.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await processingCancellation.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask WaitAsync(CancellationToken cancellationToken)
        => wakeup.WaitAsync(
            TimeSpan.FromSeconds(_options.ReplayRecoveryPollSeconds), timeProvider, cancellationToken);

    private static CaptureLaneHandlerResult ReplayCancellationResult(CancellationToken livePreemptionToken)
        => livePreemptionToken.IsCancellationRequested
            ? CaptureLaneHandlerResult.Wait("processing.replay-live-priority")
            : CaptureLaneHandlerResult.Retry("processing.replay-interrupted");

    private static void VerifyFrozenPlan(
        CaptureProcessingGraph graph,
        ProcessingGraphRevisionSnapshot revision)
    {
        if (graph.Nodes.Count != revision.Nodes.Length ||
            graph.Nodes.Where((node, index) =>
                    !string.Equals(node.Id, revision.Nodes[index].NodeId, StringComparison.Ordinal) ||
                    !string.Equals(node.PlanSha256, revision.Nodes[index].PlanSha256, StringComparison.Ordinal) ||
                    !string.Equals(
                        node.SharedPlanNodeIdentitySha256,
                        revision.Nodes[index].SharedPlanNodeIdentitySha256,
                        StringComparison.Ordinal))
                .Any())
        {
            throw new InvalidDataException("The replay runtime graph differs from its frozen revision.");
        }
    }

    private sealed class ReplayGraph(CaptureProcessingGraph value) : IDisposable
    {
        internal CaptureProcessingGraph Value { get; } = value;

        public void Dispose() => Value.DisposeSteps();
    }
}
