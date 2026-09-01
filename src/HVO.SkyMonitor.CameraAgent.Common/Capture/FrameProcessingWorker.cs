using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class FrameProcessingWorker
{
    private readonly FrameProcessingChannel _channel;
    private readonly IReadOnlyList<ICaptureProcessingStep> _steps;
    private readonly ILogger _logger;
    private readonly IRawIngressRecoveryControl? _rawIngressControl;

    public FrameProcessingWorker(
        FrameProcessingChannel channel,
        IEnumerable<ICaptureProcessingStep> steps,
        ILogger logger,
        IRawIngressRecoveryControl? rawIngressControl = null)
    {
        _channel = channel;
        _steps = steps?
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.Name, StringComparer.Ordinal)
            .ToList() ?? throw new ArgumentNullException(nameof(steps));
        _logger = logger;
        _rawIngressControl = rawIngressControl;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in _channel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessItemAsync(item, _steps, _logger, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _rawIngressControl?.InvalidateEvidence();
                _channel.Complete();
                throw;
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Processing must continue even if individual steps fail.")]
    internal static async ValueTask<CaptureLaneHandlerResult> ProcessItemAsync(
        FrameProcessingItem item,
        IReadOnlyList<ICaptureProcessingStep> steps,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var context = await CreateContextAsync(item, cancellationToken).ConfigureAwait(false);
        var failed = false;
        foreach (var step in steps)
        {
            if (step is not IDescriptorOnlyCaptureProcessingStep)
            {
                await context.EnsureRawFrameAsync(cancellationToken).ConfigureAwait(false);
            }
            var stopwatch = Stopwatch.StartNew();
            var succeeded = false;
            string? errorMessage = null;
            try
            {
                await step.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
                succeeded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.CaptureProcessingStepFailed(step.Name, context.Submission.CaptureStartedUtc, ex);
                errorMessage = ex.Message;
                failed = true;
            }
            finally
            {
                stopwatch.Stop();
                context.AddStepTelemetry(new CaptureProcessingStepTelemetry(
                    step.Name,
                    stopwatch.Elapsed,
                    succeeded,
                    errorMessage));
            }
        }
        return failed
            ? CaptureLaneHandlerResult.Retry("processing-step")
            : CaptureLaneHandlerResult.Success;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The graph boundary persists a stable failure and applies required/optional policy.")]
    internal static async ValueTask<CaptureLaneHandlerResult> ProcessGraphItemAsync(
        FrameProcessingItem item,
        CaptureProcessingGraph graph,
        CaptureProcessingPersistence? persistence,
        CaptureProcessingTelemetry telemetry,
        int attempt,
        ILogger logger,
        CancellationToken cancellationToken,
        int maximumAttempts = int.MaxValue,
        TimeProvider? timeProvider = null,
        ICaptureProcessingFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(telemetry);
        timeProvider ??= TimeProvider.System;
        var graphStopwatch = Stopwatch.StartNew();
        using var graphActivity = CaptureProcessingTelemetry.ActivitySource.StartActivity("processing-graph.execute");
        var executionClass = item.Execution?.ExecutionClass.ToString() ?? "Ephemeral";
        telemetry.GraphStarted(executionClass);
        var graphFinished = false;
        CaptureLaneHandlerResult Finish(CaptureLaneHandlerResult result)
        {
            graphFinished = true;
            graphStopwatch.Stop();
            var outcome = result.Outcome switch
            {
                CaptureLaneHandlerOutcome.Completed => "completed",
                CaptureLaneHandlerOutcome.Deferred => "waiting",
                CaptureLaneHandlerOutcome.RetryableFailure => "retry",
                _ => "terminal"
            };
            telemetry.RecordGraph(outcome, graphStopwatch.Elapsed, executionClass);
            graphActivity?.SetStatus(
                result.Outcome is CaptureLaneHandlerOutcome.Completed or CaptureLaneHandlerOutcome.Deferred
                    ? ActivityStatusCode.Ok
                    : ActivityStatusCode.Error,
                result.Reason);
            logger.CaptureProcessingGraphCompleted(outcome);
            return result;
        }
        try
        {
            var context = await CreateContextAsync(item, cancellationToken).ConfigureAwait(false);
            var rawCapture = context.RawCapture;
            var captureId = rawCapture?.Manifest.Descriptor.Capture.CaptureId;
            if (persistence is not null)
            {
                await persistence.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            if (persistence is not null && captureId is { } persistedCaptureId)
            {
                foreach (var node in graph.Nodes)
                {
                    var persistedNode = item.Execution is null
                        ? await persistence.ReadNodeAsync(
                            persistedCaptureId, node.Id, cancellationToken).ConfigureAwait(false)
                        : await persistence.ReadExecutionNodeAsync(
                            item.Execution, persistedCaptureId, node.Id, cancellationToken).ConfigureAwait(false);
                    if (persistedNode is not null && !string.Equals(
                        persistedNode.PlanSha256, node.PlanSha256, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Committed processing node '{node.Id}' does not match the current graph plan.");
                    }
                }
            }
            var statuses = new Dictionary<string, DurableProcessingNodeStatus>(StringComparer.OrdinalIgnoreCase);
            string? deferredRetryReason = null;
            foreach (var node in graph.Nodes)
            {
                context.BeginNode(node.Id, node.Dependencies, node.DeclaredDependencies);
                var dependencyStopwatch = Stopwatch.StartNew();
                var retryableDependency = node.Dependencies.FirstOrDefault(dependency =>
                    statuses.TryGetValue(dependency, out var status) &&
                    status == DurableProcessingNodeStatus.RetryableFailure);
                if (retryableDependency is not null)
                {
                    statuses[node.Id] = DurableProcessingNodeStatus.RetryableFailure;
                    deferredRetryReason ??= "processing.dependency-retry";
                    dependencyStopwatch.Stop();
                    telemetry.RecordDependencyWait(node, dependencyStopwatch.Elapsed);
                    continue;
                }
                var blockedDependency = node.Dependencies.FirstOrDefault(dependency =>
                    node.OptionalDependencies?.Contains(dependency) != true &&
                    statuses.TryGetValue(dependency, out var status) && status != DurableProcessingNodeStatus.Completed);
                if (blockedDependency is not null)
                {
                    const string dependencyReason = "processing.dependency-unavailable";
                    if (persistence is not null && rawCapture is not null)
                    {
                        if (item.Execution is not null)
                        {
                            await persistence.BeginExecutionNodeAttemptAsync(
                                item.Execution, node, attempt, timeProvider.GetUtcNow(), cancellationToken)
                                .ConfigureAwait(false);
                        }
                        await persistence.WriteNodeAsync(
                            rawCapture, node, DurableProcessingNodeStatus.Skipped, dependencyReason, attempt,
                            null, timeProvider.GetUtcNow(), null, ProcessingOutcomeStatus.Skipped,
                            item.WorkId, item.LeaseToken,
                            [], context, item.Execution, cancellationToken).ConfigureAwait(false);
                    }
                    statuses[node.Id] = DurableProcessingNodeStatus.Skipped;
                    dependencyStopwatch.Stop();
                    telemetry.RecordDependencyWait(node, dependencyStopwatch.Elapsed);
                    if (node.Required)
                    {
                        return Finish(CaptureLaneHandlerResult.Terminal(dependencyReason));
                    }
                    continue;
                }

                if (persistence is not null && captureId is { } durableCaptureId)
                {
                    if (item.Execution is null)
                    {
                        _ = await persistence.ResolveUnavailableNodeAsync(
                            durableCaptureId, node.Id, node.PlanSha256, cancellationToken).ConfigureAwait(false);
                    }
                    var durable = item.Execution is null
                        ? await persistence.ReadNodeAsync(
                            durableCaptureId, node.Id, cancellationToken).ConfigureAwait(false)
                        : await persistence.ReadExecutionNodeAsync(
                            item.Execution, durableCaptureId, node.Id, cancellationToken).ConfigureAwait(false);
                    var memoryOnly = node.Publication?.Persistence == CaptureProcessingPersistenceMode.MemoryOnly;
                    if (!memoryOnly && durable?.Status is
                        (DurableProcessingNodeStatus.Completed or DurableProcessingNodeStatus.Skipped))
                    {
                        if (durable.Status == DurableProcessingNodeStatus.Completed)
                        {
                            await persistence.RestoreNodeAsync(durable, context, cancellationToken).ConfigureAwait(false);
                            if (item.Execution?.AllowAutomaticPublication != false &&
                                node.Step is IDurableCaptureProcessingPostCommit restoredCommit)
                            {
                                await RunPostCommitCleanupAsync(
                                    restoredCommit, node, context, telemetry, logger, cancellationToken).ConfigureAwait(false);
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                            logger.CaptureProcessingOutputExisting(node.Id);
                        }
                        statuses[node.Id] = durable.Status;
                        context.AddStepTelemetry(new CaptureProcessingStepTelemetry(
                            node.Id,
                            durable.Duration ?? TimeSpan.Zero,
                            durable.Status == DurableProcessingNodeStatus.Completed,
                            durable.Reason));
                        if (node.Required && durable.Status == DurableProcessingNodeStatus.Skipped)
                        {
                            return Finish(CaptureLaneHandlerResult.Terminal(durable.Reason ?? "processing.skipped"));
                        }
                        continue;
                    }
                    if (!memoryOnly && durable?.Status == DurableProcessingNodeStatus.TerminalFailure)
                    {
                        statuses[node.Id] = durable.Status;
                        context.AddStepTelemetry(new CaptureProcessingStepTelemetry(
                            node.Id,
                            durable.Duration ?? TimeSpan.Zero,
                            false,
                            durable.Reason));
                        if (node.Required)
                        {
                            return Finish(CaptureLaneHandlerResult.Terminal(durable.Reason ?? "processing.terminal"));
                        }
                        continue;
                    }
                }

                if (node.Step is not IDescriptorOnlyCaptureProcessingStep)
                {
                    await context.EnsureRawFrameAsync(cancellationToken).ConfigureAwait(false);
                }
                if (persistence is not null && node.Step is IWindowCaptureProcessingGraphStep window &&
                    node.Dependencies.Count > 0 && graph.Nodes.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, node.Dependencies[0], StringComparison.OrdinalIgnoreCase)) is { OutputRole: { } sourceRole })
                {
                    context.SetHistoricalInputs(item.Execution is not null
                        ? await persistence.ReadFrozenExecutionInputsAsync(
                            item.Execution.ExecutionId, node.Id, cancellationToken).ConfigureAwait(false)
                        : await persistence.ReadRecentInputsAsync(
                            rawCapture!.Manifest.Descriptor,
                            node.Dependencies[0], sourceRole, window.MaximumInputCount, cancellationToken).ConfigureAwait(false));
                }
                else if (persistence is not null && rawCapture is not null &&
                    node.Step is IWindowCaptureProcessingGraphStep rawWindow &&
                    context.Artifacts?.Raw is { } rawArtifact)
                {
                    context.SetHistoricalInputs(item.Execution is not null
                        ? await persistence.ReadFrozenExecutionRawInputsAsync(
                            item.Execution.ExecutionId, node.Id, cancellationToken).ConfigureAwait(false)
                        : await persistence.ReadRecentRawInputsAsync(
                            rawCapture.Manifest.Descriptor,
                            CameraAgentRecipeExecutionAdapter.CreateArtifact(
                                context.Config, rawArtifact, "source", context.AcquisitionTiming,
                                context.ReconstructionDescriptor),
                            rawWindow.MaximumInputCount,
                            cancellationToken).ConfigureAwait(false));
                }
                else
                {
                    context.SetHistoricalInputs([]);
                }

                var outcomeStart = context.ProcessingOutcomes.Count;
                Exception? exception = null;
                var startedUtc = timeProvider.GetUtcNow();
                var startedTimestamp = timeProvider.GetTimestamp();
                dependencyStopwatch.Stop();
                telemetry.RecordDependencyWait(node, dependencyStopwatch.Elapsed);
                using var nodeActivity = CaptureProcessingTelemetry.ActivitySource.StartActivity("processing-step.execute");
                logger.CaptureProcessingNodeStarted(node.Id, attempt);
                try
                {
                    if (persistence is not null && item.Execution is not null)
                    {
                        await persistence.BeginExecutionNodeAttemptAsync(
                            item.Execution, node, attempt, startedUtc, cancellationToken).ConfigureAwait(false);
                    }
                    faultInjector?.Inject(CaptureProcessingFaultPoint.BeforeNodeExecution, node.Id);
                    await node.Step.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception caught)
                {
                    exception = caught;
                    logger.CaptureProcessingStepFailed(node.Id, context.Submission.CaptureStartedUtc, caught);
                }
                var duration = timeProvider.GetElapsedTime(startedTimestamp);
                var completedUtc = timeProvider.GetUtcNow();

                var outcomes = context.ProcessingOutcomes.Skip(outcomeStart).ToArray();
                var outcome = outcomes.LastOrDefault();
                var (status, reason) = ResolveStatus(node, outcome, exception);
                if (!node.Required && status == DurableProcessingNodeStatus.RetryableFailure && attempt >= maximumAttempts)
                {
                    status = DurableProcessingNodeStatus.TerminalFailure;
                    reason = "processing.optional-exhausted";
                }
                if (!node.Required && status == DurableProcessingNodeStatus.RetryableFailure &&
                    item.Execution?.ExecutionClass == ProcessingGraphExecutionClass.Live)
                {
                    status = DurableProcessingNodeStatus.TerminalFailure;
                    reason = "processing.optional-degraded";
                }
                var products = exception is null
                    ? outcomes.SelectMany(static value => value.Products).ToArray()
                    : [];
                foreach (var product in products)
                {
                    context.RegisterProcessingProduct(product);
                }
                telemetry.RecordNode(node, status, reason, duration, executionClass);
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.CaptureProcessingNodeOutcome(node.Id, status.ToString());
                }
                nodeActivity?.SetStatus(
                    status == DurableProcessingNodeStatus.Completed ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                    reason);
                context.AddStepTelemetry(new CaptureProcessingStepTelemetry(
                    node.Id,
                    duration,
                    status == DurableProcessingNodeStatus.Completed,
                    reason));
                if (persistence is not null && rawCapture is not null)
                {
                    if (status == DurableProcessingNodeStatus.Completed &&
                        node.Publication?.Persistence == CaptureProcessingPersistenceMode.MemoryOnly)
                    {
                        if (item.Execution?.AllowAutomaticPublication != false)
                        {
                            await persistence.DeleteOutputlessNodeAsync(
                                rawCapture.Manifest.Descriptor.Capture.CaptureId,
                                node.Id,
                                item.WorkId,
                                item.LeaseToken,
                                cancellationToken).ConfigureAwait(false);
                        }
                        if (item.Execution is not null)
                        {
                            await persistence.CompleteOutputlessExecutionNodeAsync(
                                item.Execution,
                                node,
                                status,
                                reason,
                                attempt,
                                completedUtc,
                                duration,
                                outcome?.Status,
                                cancellationToken).ConfigureAwait(false);
                        }
                        if (item.Execution?.AllowAutomaticPublication != false &&
                            node.Step is IDurableCaptureProcessingPostCommit memoryOnlyCleanup)
                        {
                            await RunPostCommitCleanupAsync(
                                memoryOnlyCleanup, node, context, telemetry, logger, cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    else
                    {
                        await persistence.WriteNodeAsync(
                            rawCapture, node, status, reason, attempt,
                            startedUtc, completedUtc, duration,
                            outcome?.Status,
                            item.WorkId, item.LeaseToken,
                            products, context, item.Execution, cancellationToken).ConfigureAwait(false);
                        if (item.Execution?.AllowAutomaticPublication != false &&
                            status == DurableProcessingNodeStatus.Completed &&
                            node.Step is IDurableCaptureProcessingPostCommit committed)
                        {
                            await RunPostCommitCleanupAsync(
                                committed, node, context, telemetry, logger, cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                }
                statuses[node.Id] = status;
                if (node.Required)
                {
                    if (status == DurableProcessingNodeStatus.RetryableFailure)
                    {
                        logger.CaptureProcessingNodeRetry(node.Id, reason ?? "processing.retryable");
                        return Finish(RetryOrWait(reason ?? "processing.retryable"));
                    }
                    if (status is DurableProcessingNodeStatus.TerminalFailure or DurableProcessingNodeStatus.Skipped)
                    {
                        logger.CaptureProcessingNodeTerminal(node.Id, reason ?? "processing.terminal");
                        return Finish(CaptureLaneHandlerResult.Terminal(reason ?? "processing.terminal"));
                    }
                }
                else if (status == DurableProcessingNodeStatus.RetryableFailure)
                {
                    deferredRetryReason ??= reason ?? "processing.retryable";
                }
            }
            return Finish(deferredRetryReason is null
                ? CaptureLaneHandlerResult.Success
                : RetryOrWait(deferredRetryReason));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!graphFinished)
            {
                Finish(CaptureLaneHandlerResult.Retry("processing.cancelled"));
            }
            throw;
        }
        catch
        {
            if (!graphFinished)
            {
                Finish(CaptureLaneHandlerResult.Terminal("processing.unhandled"));
            }
            throw;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Post-commit cleanup cannot invalidate a durable node or block its dependents.")]
    private static async ValueTask RunPostCommitCleanupAsync(
        IDurableCaptureProcessingPostCommit cleanup,
        CaptureProcessingGraphNode node,
        CaptureProcessingContext context,
        CaptureProcessingTelemetry telemetry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await cleanup.OnCommittedAsync(
                new CaptureDescriptorProcessingContext(context), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var reason = exception is OperationCanceledException ? "cancelled" : "failed";
            telemetry.RecordPostCommitCleanupFailure(node, reason);
            logger.CaptureProcessingPostCommitCleanupFailed(node.Id, exception);
        }
    }

    private static (DurableProcessingNodeStatus Status, string? Reason) ResolveStatus(
        CaptureProcessingGraphNode node,
        ProcessingOutcome? outcome,
        Exception? exception)
    {
        if (exception is not null)
        {
            return (DurableProcessingNodeStatus.RetryableFailure, "processing.step-exception");
        }
        if (outcome is not null)
        {
            return outcome.Status switch
            {
                ProcessingOutcomeStatus.Produced => (DurableProcessingNodeStatus.Completed, null),
                ProcessingOutcomeStatus.Skipped => (DurableProcessingNodeStatus.Skipped, outcome.ReasonCode),
                ProcessingOutcomeStatus.RetryableFailure => (DurableProcessingNodeStatus.RetryableFailure, outcome.ReasonCode),
                ProcessingOutcomeStatus.TerminalFailure => (DurableProcessingNodeStatus.TerminalFailure, outcome.ReasonCode),
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            };
        }
        return node.RecipeName is null
            ? (DurableProcessingNodeStatus.Completed, null)
            : (DurableProcessingNodeStatus.Skipped, ProcessingReasonCodes.MissingInput);
    }

    private static CaptureLaneHandlerResult RetryOrWait(string reason)
        => string.Equals(reason, ProcessingReasonCodes.EnvironmentAssociationPending, StringComparison.Ordinal)
            ? CaptureLaneHandlerResult.Wait(reason)
            : CaptureLaneHandlerResult.Retry(reason);

    private static async ValueTask<CaptureProcessingContext> CreateContextAsync(
        FrameProcessingItem item,
        CancellationToken cancellationToken)
    {
        var submission = item.Submission;
        var rawCapture = item.RawCapture;
        if (rawCapture is { } receipt)
        {
            var sidecar = await File.ReadAllBytesAsync(
                Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json"), cancellationToken).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(sidecar);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } persistedManifest)
            {
                throw new InvalidDataException($"Committed raw sidecar could not be parsed ({parsed.Validation.ReasonCode}).");
            }
            if (!string.Equals(
                    CaptureContractJson.ComputeManifestSha256(sidecar),
                    receipt.CommittedManifestSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Committed raw sidecar differs from the journaled manifest.");
            }
            receipt = receipt with { Manifest = persistedManifest };
            rawCapture = receipt;
            async ValueTask<CaptureResult> LoadRawFrameAsync(CancellationToken token)
            {
                var payload = await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath, token).ConfigureAwait(false);
                var reconstruction = FrameReconstructor.TryReconstruct(receipt.Manifest.Descriptor, payload, out var frame);
                if (!reconstruction.IsValid || frame is null)
                {
                    throw new InvalidDataException($"Committed raw evidence could not be reconstructed ({reconstruction.ReasonCode}).");
                }
                if (receipt.Manifest.Scene is not null)
                {
                    frame = frame with { Metadata = frame.Metadata with { Scene = receipt.Manifest.Scene } };
                }
                var artifact = new FrameArtifact(
                    receipt.Manifest.Descriptor.Artifact.ArtifactId,
                    FrameArtifactRole.Raw,
                    frame,
                    recipeVersion: ProcessingIdentity.CreateRecipeIdentity(
                        receipt.Manifest.Descriptor.Artifact.Recipe).IdentitySha256);
                return submission.Result with { Frame = frame, Artifacts = new FrameArtifactSet(artifact) };
            }
            submission = submission with { Result = submission.Result with { Frame = null, Artifacts = null } };
            return new CaptureProcessingContext(
                item.Config, submission, rawCapture, LoadRawFrameAsync, item.Execution);
        }
        return new CaptureProcessingContext(item.Config, submission, rawCapture, null, item.Execution);
    }
}
