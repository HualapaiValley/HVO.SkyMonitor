using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// A live execution is created when its own raw capture is accepted, before the earlier captures of a
/// trailing window have finished processing, so these tests drive a real ingress and standard lane with a
/// processing backlog - the normal state of a paced agent - and prove the combined artifact still spans the
/// configured window while replay keeps consuming its own frozen pins.
/// </summary>
[TestClass]
public sealed class RollingCombinationWindowLineageTests
{
    private const int WindowSize = 5;
    private const int CaptureCount = WindowSize + 1;
    private const string RollingNodeId = "rolling";
    private static readonly DateTimeOffset FixtureUtc = new(2026, 2, 1, 3, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LiveExecutionSpansTheConfiguredWindowAndRecordsWhatItConsumed()
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            var (receipt, _) = await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            using var store = CreateStore(root);
            var rolling = await store.ReadNodeAsync(captureId, RollingNodeId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            var sources = rolling.Outputs[0].Descriptor!.Artifact.SourceArtifactIds;
            Assert.HasCount(WindowSize, sources);

            // The combined frame must actually accumulate the window, not merely name it.
            Assert.AreEqual(TimeSpan.FromSeconds(WindowSize), rolling.Outputs[0].TotalIntegration);

            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var execution = (await operations.ReadExecutionsAsync(
                    ProcessingGraphExecutionClass.Live, 256, CancellationToken.None).ConfigureAwait(false))
                .Single(state => state.CaptureId == captureId);
            var detail = await operations.ReadExecutionDetailAsync(execution.ExecutionId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(detail);
            var recorded = detail.Nodes.Single(static node => node.NodeId == RollingNodeId).Inputs
                .Where(static input => input.Kind == ProcessingGraphExecutionInputKind.ProcessingOutput)
                .OrderBy(static input => input.WindowPosition)
                .ToArray();

            // The recorded window evidence must be the earlier captures the attempt actually combined.
            CollectionAssert.AreEqual(
                Enumerable.Range(-(WindowSize - 1), WindowSize - 1).ToArray(),
                recorded.Select(static input => input.WindowPosition).ToArray());
            CollectionAssert.AreEqual(
                sources.Take(WindowSize - 1).ToArray(),
                recorded.Select(static input => input.ArtifactId).ToArray());
            Assert.IsFalse(recorded.Any(input => input.CaptureId == captureId));
            Assert.AreEqual(0L, await CountUnreleasedPinsAsync(root, execution.ExecutionId).ConfigureAwait(false));
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LiveRetryReplacesItsUnreleasedWindowPinsWithoutStaleOrDuplicateRows()
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            var fault = new ArmableNodeFaultInjector(RollingNodeId);
            using var provider = CreateProvider(root, fault);
            var configuration = CreateConfiguration(syntheticReferences: false);
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            _ = await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
            var receipt = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(
                configuration,
                await CreateSubmissionAsync(module, CaptureCount, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);

            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static lane => lane.Name == "standard");
            var handler = provider.GetServices<ICaptureLaneHandler>().Single(
                static candidate => candidate.Lane == "standard");
            var firstLease = await laneStore.ClaimAsync(
                standard, "rolling-window-retry-1", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(firstLease);
            Assert.AreEqual(1, firstLease.Attempt);
            Assert.IsNotNull(firstLease.Context.Execution);
            fault.Arm();
            var firstResult = await handler.HandleAsync(firstLease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, firstResult.Outcome);
            var firstPins = await ReadPinsAsync(root, firstLease.Context.Execution.ExecutionId).ConfigureAwait(false);
            Assert.HasCount(WindowSize - 1, firstPins);
            var staleIdentity = firstPins[^1].Split('|')[^1];

            await laneStore.ReleaseAsync(firstLease, CancellationToken.None).ConfigureAwait(false);
            using var store = CreateStore(root);
            await store.SetOutputAvailabilityAsync(
                staleIdentity, "Missing", "retry-pin-test", CancellationToken.None).ConfigureAwait(false);

            var secondLease = await laneStore.ClaimAsync(
                standard, "rolling-window-retry-2", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(secondLease);
            Assert.AreEqual(2, secondLease.Attempt);
            Assert.IsNotNull(secondLease.Context.Execution);
            var secondResult = await handler.HandleAsync(secondLease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, secondResult.Outcome, secondResult.Reason);
            var secondPins = await ReadPinsAsync(root, secondLease.Context.Execution.ExecutionId).ConfigureAwait(false);

            Assert.HasCount(WindowSize - 1, secondPins);
            Assert.IsFalse(secondPins.Any(pin => pin.EndsWith(staleIdentity, StringComparison.Ordinal)));
            Assert.AreEqual(
                WindowSize - 1,
                secondPins.Select(static pin => pin.Split('|')[^1]).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(
                WindowSize - 1,
                await CountUnreleasedPinsAsync(root, secondLease.Context.Execution.ExecutionId).ConfigureAwait(false));
            await laneStore.CompleteAsync(secondLease, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LiveRetryUsesItsOwnRevisionCompatibilityAfterShiftedReplayCompletes()
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            var fault = new ArmableNodeFaultInjector("revision-barrier");
            using var provider = CreateProvider(root, fault);
            var configuration = CreateRevisionBarrierConfiguration(SyntheticCalibration);
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            _ = await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            var receipt = await ingress.AcceptAsync(
                configuration,
                await CreateSubmissionAsync(module, CaptureCount, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);

            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static lane => lane.Name == "standard");
            var handler = provider.GetServices<ICaptureLaneHandler>().Single(
                static candidate => candidate.Lane == "standard");
            var firstLease = await laneStore.ClaimAsync(
                standard, "revision-reference-live-1", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(firstLease);
            fault.Arm();
            var firstResult = await handler.HandleAsync(firstLease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, firstResult.Outcome);

            using (var store = CreateStore(root))
            {
                var calibration = await store.ReadNodeAsync(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    "calibration",
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(calibration);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, calibration.Status);
                Assert.IsNull(await store.ReadNodeAsync(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    RollingNodeId,
                    CancellationToken.None).ConfigureAwait(false));
            }

            var shiftedConfiguration = CreateRevisionBarrierConfiguration(SyntheticCalibration);
            var shiftedPipeline = shiftedConfiguration.Pipeline with
            {
                Steps = shiftedConfiguration.Pipeline.Steps.Select(static step =>
                    string.Equals(step.Id, "calibration", StringComparison.Ordinal)
                        ? step with
                        {
                            Options = JsonSerializer.SerializeToElement(new
                            {
                                strategy = "None",
                                outputVariant = "synthetic-corrected"
                            })
                        }
                        : step).ToArray()
            };
            var shiftedRevision = await operations.CreateRevisionAsync(
                "shifted-calibration",
                "v1",
                shiftedPipeline,
                "shifted-create-key",
                "revision-reference-test",
                null,
                CancellationToken.None).ConfigureAwait(false);
            _ = await operations.ValidateRevisionAsync(
                shiftedRevision.RevisionId,
                "shifted-validate-key",
                "revision-reference-test",
                null,
                CancellationToken.None).ConfigureAwait(false);
            var replay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    shiftedRevision.RevisionId,
                    receipt.Manifest.Descriptor.Artifact.ArtifactId),
                "shifted-replay-key",
                "revision-reference-test",
                CancellationToken.None).ConfigureAwait(false);

            // Production correctly gives every standard-lane row priority over replay. Temporarily hide this
            // already-leased row so the fixture can construct the defensive cross-revision interleaving, then
            // restore its exact lease before asking the lane store to retry it.
            await SetStandardLaneStateAsync(root, firstLease.WorkId, "completed").ConfigureAwait(false);
            var replayWorker = provider.GetRequiredService<ProcessingReplayWorker>();
            await replayWorker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var detail = await operations.ReadExecutionDetailAsync(
                        replay.Execution.ExecutionId, timeout.Token).ConfigureAwait(false);
                    Assert.IsNotNull(detail);
                    if (detail.Execution.Status == ProcessingGraphExecutionStatus.Completed)
                    {
                        break;
                    }
                    Assert.AreNotEqual(
                        ProcessingGraphExecutionStatus.Failed,
                        detail.Execution.Status,
                        detail.Execution.FailureReason);
                    await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                await replayWorker.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await SetStandardLaneStateAsync(root, firstLease.WorkId, "leased").ConfigureAwait(false);
            }

            Guid replayCalibrationArtifactId;
            using (var referenceStore = CreateStore(root))
            {
                var replayReference = await referenceStore.ReadExecutionNodeAsync(
                    replay.Execution.ExecutionId,
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    "calibration",
                    CancellationToken.None).ConfigureAwait(false);
                var liveReference = await referenceStore.ReadExecutionNodeAsync(
                    firstLease.Context.Execution!.ExecutionId,
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    "calibration",
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(replayReference);
                Assert.IsNotNull(liveReference);
                Assert.HasCount(1, replayReference.Outputs);
                Assert.HasCount(1, liveReference.Outputs);
                Assert.AreEqual(liveReference.Outputs[0].Artifact.Role, replayReference.Outputs[0].Artifact.Role);
                Assert.AreEqual(liveReference.Outputs[0].Artifact.Variant, replayReference.Outputs[0].Artifact.Variant);
                Assert.AreNotEqual(liveReference.Outputs[0].Compatibility, replayReference.Outputs[0].Compatibility);
                replayCalibrationArtifactId = replayReference.Outputs[0].ArtifactId;
            }

            await laneStore.ReleaseAsync(firstLease, CancellationToken.None).ConfigureAwait(false);
            var secondLease = await laneStore.ClaimAsync(
                standard, "revision-reference-live-2", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(secondLease);
            Assert.AreEqual(2, secondLease.Attempt);
            var secondResult = await handler.HandleAsync(secondLease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, secondResult.Outcome, secondResult.Reason);
            await laneStore.CompleteAsync(secondLease, CancellationToken.None).ConfigureAwait(false);

            var live = (await operations.ReadExecutionsAsync(
                    ProcessingGraphExecutionClass.Live, 256, CancellationToken.None).ConfigureAwait(false))
                .Single(execution => execution.CaptureId == receipt.Manifest.Descriptor.Capture.CaptureId);
            var liveDetail = await operations.ReadExecutionDetailAsync(
                live.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(liveDetail);
            var historicalInputs = liveDetail.Nodes.Single(static node => node.NodeId == RollingNodeId).Inputs
                .Where(static input => input.Kind == ProcessingGraphExecutionInputKind.ProcessingOutput)
                .OrderBy(static input => input.WindowPosition)
                .ToArray();
            Assert.HasCount(WindowSize - 1, historicalInputs);
            CollectionAssert.AreEqual(
                Enumerable.Range(-(WindowSize - 1), WindowSize - 1).ToArray(),
                historicalInputs.Select(static input => input.WindowPosition).ToArray());
            using var finalStore = CreateStore(root);
            var rolling = await finalStore.ReadNodeAsync(
                receipt.Manifest.Descriptor.Capture.CaptureId,
                RollingNodeId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            var sourceArtifactIds = rolling.Outputs.Single().Descriptor!.Artifact.SourceArtifactIds;
            Assert.HasCount(WindowSize, sourceArtifactIds);
            Assert.DoesNotContain(replayCalibrationArtifactId, sourceArtifactIds);
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ReplayExecutionKeepsItsFrozenWindowPins()
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            var (receipt, activeRevisionId) = await RunBacklogAsync(provider, configuration, module)
                .ConfigureAwait(false);
            var descriptor = receipt.Manifest.Descriptor;
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var replay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    descriptor.Capture.CaptureId,
                    activeRevisionId,
                    descriptor.Artifact.ArtifactId),
                "rolling-window-replay-key",
                "rolling-window-test",
                CancellationToken.None).ConfigureAwait(false);
            var frozen = await ReadPinsAsync(root, replay.Execution.ExecutionId).ConfigureAwait(false);
            Assert.HasCount(WindowSize - 1, frozen.Where(static pin => pin.StartsWith(
                RollingNodeId + "|", StringComparison.Ordinal)).ToArray());

            // The newest member of the frozen window stops qualifying after submission. A replay that
            // reselected would drop it and reach one capture further back; one that honours its frozen pins
            // consumes the original members unchanged, because a pinned output is read by identity.
            using var store = CreateStore(root);
            var pinnedArtifactIds = (await store.ReadFrozenExecutionOutputsAsync(
                    replay.Execution.ExecutionId, RollingNodeId, CancellationToken.None).ConfigureAwait(false))
                .Select(static output => output.ArtifactId)
                .ToArray();
            Assert.HasCount(WindowSize - 1, pinnedArtifactIds);
            var newestPinned = (await store.ReadFrozenExecutionOutputsAsync(
                    replay.Execution.ExecutionId, RollingNodeId, CancellationToken.None).ConfigureAwait(false))[^1];
            await store.SetOutputAvailabilityAsync(
                newestPinned.OutputIdentitySha256,
                "Missing",
                "rolling-window-test",
                CancellationToken.None).ConfigureAwait(false);

            var worker = provider.GetRequiredService<ProcessingReplayWorker>();
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var detail = await operations.ReadExecutionDetailAsync(
                        replay.Execution.ExecutionId, timeout.Token).ConfigureAwait(false);
                    Assert.IsNotNull(detail);
                    if (detail.Execution.Status == ProcessingGraphExecutionStatus.Completed) break;
                    Assert.AreNotEqual(ProcessingGraphExecutionStatus.Failed, detail.Execution.Status,
                        detail.Execution.FailureReason);
                    await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // The replay consumed exactly the window frozen when it was submitted, and left it unchanged.
            CollectionAssert.AreEqual(
                frozen, await ReadPinsAsync(root, replay.Execution.ExecutionId).ConfigureAwait(false));
            var replayed = await store.ReadExecutionNodeAsync(
                replay.Execution.ExecutionId,
                descriptor.Capture.CaptureId,
                RollingNodeId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(replayed);
            Assert.HasCount(1, replayed.Outputs);
            var replayedSources = replayed.Outputs[0].Descriptor!.Artifact.SourceArtifactIds;
            Assert.HasCount(WindowSize, replayedSources);
            CollectionAssert.AreEqual(pinnedArtifactIds, replayedSources.Take(WindowSize - 1).ToArray());
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LiveExecutionSkipsAnIneligibleEarlierCaptureAndUsesAnOlderOne()
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            var (excludedReceipt, _) = await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
            using var store = CreateStore(root);
            var excludedCalibration = await store.ReadNodeAsync(
                excludedReceipt.Manifest.Descriptor.Capture.CaptureId,
                "calibration",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(excludedCalibration);
            Assert.HasCount(1, excludedCalibration.Outputs);
            var excludedArtifactId = excludedCalibration.Outputs[0].ArtifactId;

            // The next capture is accepted while its predecessor is still eligible, and the predecessor stops
            // qualifying before the consuming node runs, as an evicted, failed, or still-running peer would.
            var receipt = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(
                configuration,
                await CreateSubmissionAsync(module, CaptureCount, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            await store.SetOutputAvailabilityAsync(
                excludedCalibration.Outputs[0].OutputIdentitySha256,
                "Missing",
                "rolling-window-test",
                CancellationToken.None).ConfigureAwait(false);
            await DrainOneAsync(provider, configuration).ConfigureAwait(false);

            var rolling = await store.ReadNodeAsync(
                receipt.Manifest.Descriptor.Capture.CaptureId,
                RollingNodeId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            var sources = rolling.Outputs[0].Descriptor!.Artifact.SourceArtifactIds;

            // The ineligible capture is skipped over and an older eligible one takes its place.
            Assert.HasCount(WindowSize, sources);
            Assert.DoesNotContain(excludedArtifactId, sources);
            Assert.AreEqual(TimeSpan.FromSeconds(WindowSize), rolling.Outputs[0].TotalIntegration);
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LiveExecutionRetentionProtectsEligibleCandidateUntilWindowPins()
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            var fault = new ArmableNodeFaultInjector("revision-barrier");
            using var provider = CreateProvider(root, fault, maximumWindowInputs: 128);
            var configuration = CreateRevisionBarrierConfiguration(SyntheticCalibration);
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            _ = await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
            using var store = CreateStore(root, maximumWindowInputs: 128);
            var calibrations = await ReadOutputsAsync(root, "calibration").ConfigureAwait(false);
            Assert.HasCount(CaptureCount, calibrations);
            var oldestCandidate = calibrations[0];
            foreach (var output in calibrations.Skip(WindowSize - 1))
            {
                await store.SetOutputAvailabilityAsync(
                    output.OutputIdentitySha256, "Missing", "candidate-gap-test", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            foreach (var output in await ReadOutputsAsync(root, RollingNodeId).ConfigureAwait(false))
            {
                await store.SetOutputAvailabilityAsync(
                    output.OutputIdentitySha256, "Missing", "candidate-gap-test", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            var expiredControl = await InsertIneligibleOutputsAsync(root, 512).ConfigureAwait(false);

            var receipt = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(
                configuration,
                await CreateSubmissionAsync(module, CaptureCount, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static lane => lane.Name == "standard");
            var handler = provider.GetServices<ICaptureLaneHandler>().Single(
                static candidate => candidate.Lane == "standard");
            var firstLease = await laneStore.ClaimAsync(
                standard, "candidate-retention-1", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(firstLease);
            fault.Arm();
            var firstResult = await handler.HandleAsync(firstLease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, firstResult.Outcome);
            Assert.IsEmpty(await ReadPinsAsync(root, firstLease.Context.Execution!.ExecutionId).ConfigureAwait(false));

            var holds = await store.ReadRetentionHoldsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(holds.Any(hold => hold.ArtifactId == oldestCandidate.ArtifactId));
            Assert.IsFalse(holds.Any(hold => hold.ArtifactId == expiredControl.ArtifactId));
            var heldPaths = holds.SelectMany(static hold => new[]
                {
                    hold.PayloadRelativePath,
                    hold.SidecarRelativePath
                })
                .Select(path => Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))))
                .ToHashSet(StringComparer.Ordinal);
            _ = await provider.GetRequiredService<CaptureProcessingPersistence>().ExpireOutputsAsync(
                root, DateTimeOffset.MaxValue, heldPaths, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(Path.Combine(
                root, oldestCandidate.PayloadRelativePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.IsFalse(File.Exists(expiredControl.PayloadPath));

            await laneStore.ReleaseAsync(firstLease, CancellationToken.None).ConfigureAwait(false);
            var secondLease = await laneStore.ClaimAsync(
                standard, "candidate-retention-2", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(secondLease);
            var secondResult = await handler.HandleAsync(secondLease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, secondResult.Outcome, secondResult.Reason);
            await laneStore.CompleteAsync(secondLease, CancellationToken.None).ConfigureAwait(false);

            var rolling = await store.ReadNodeAsync(
                receipt.Manifest.Descriptor.Capture.CaptureId,
                RollingNodeId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            var sources = rolling.Outputs[0].Descriptor!.Artifact.SourceArtifactIds;
            Assert.HasCount(WindowSize, sources);
            Assert.Contains(oldestCandidate.ArtifactId, sources);

            var live = (await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().ReadExecutionsAsync(
                    ProcessingGraphExecutionClass.Live, 256, CancellationToken.None).ConfigureAwait(false))
                .Single(execution => execution.CaptureId == receipt.Manifest.Descriptor.Capture.CaptureId);
            var detail = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                .ReadExecutionDetailAsync(live.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            var historicalInputs = detail.Nodes.Single(static node => node.NodeId == RollingNodeId).Inputs
                .Where(static input => input.Kind == ProcessingGraphExecutionInputKind.ProcessingOutput)
                .OrderBy(static input => input.WindowPosition)
                .ToArray();
            Assert.HasCount(WindowSize - 1, historicalInputs);
            Assert.Contains(oldestCandidate.ArtifactId, historicalInputs.Select(static input => input.ArtifactId));
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    private static async Task<ICameraModule> CreateModuleAsync(
        ServiceProvider provider,
        CameraModuleConfig configuration)
    {
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            provider.GetRequiredService<ICelestialCatalog>(),
            provider.GetRequiredService<IProjectedSceneStore>(),
            provider.GetRequiredService<IConstellationTopology>());
        await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        return module;
    }

    private static async Task<(RawCaptureReceipt Receipt, string ActiveRevisionId)> RunBacklogAsync(
        ServiceProvider provider,
        CameraModuleConfig configuration,
        ICameraModule module)
    {
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
            .ConfigureAwait(false);
        RawCaptureReceipt? receipt = null;
        for (var index = 0; index < CaptureCount; index++)
        {
            receipt = await ingress.AcceptAsync(
                configuration,
                await CreateSubmissionAsync(module, index, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
        }
        for (var index = 0; index < CaptureCount; index++)
        {
            await DrainOneAsync(provider, configuration).ConfigureAwait(false);
        }
        return (receipt!, registry.ActiveRevisionId);
    }

    private static async Task DrainOneAsync(ServiceProvider provider, CameraModuleConfig configuration)
    {
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static lane => lane.Name == "standard");
        var lease = await laneStore.ClaimAsync(
            standard, "rolling-window-test", configuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(
            static candidate => candidate.Lane == "standard");
        var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().NotifyLiveWorkChanged();
    }

    private sealed record OutputReference(
        string OutputIdentitySha256,
        Guid ArtifactId,
        string PayloadRelativePath,
        long CaptureSequence);

    private sealed record IneligibleOutputControl(Guid ArtifactId, string PayloadPath);

    private static async Task<OutputReference[]> ReadOutputsAsync(string root, string nodeId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output_identity_sha256, artifact_id, payload_relative_path, capture_sequence
            FROM processing_outputs
            WHERE node_id = $node AND availability_state = 'Available'
            ORDER BY capture_sequence, output_identity_sha256;
            """;
        command.Parameters.AddWithValue("$node", nodeId);
        var outputs = new List<OutputReference>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            outputs.Add(new OutputReference(
                reader.GetString(0),
                Guid.ParseExact(reader.GetString(1), "N"),
                reader.GetString(2),
                reader.GetInt64(3)));
        }
        return outputs.ToArray();
    }

    private static async Task<IneligibleOutputControl> InsertIneligibleOutputsAsync(string root, int count)
    {
        var directory = Path.Combine(root, "ineligible");
        Directory.CreateDirectory(directory);
        var controlPayload = Path.Combine(directory, "0001.bin");
        var controlSidecar = Path.Combine(directory, "0001.json");
        await File.WriteAllBytesAsync(controlPayload, [1]).ConfigureAwait(false);
        await File.WriteAllTextAsync(controlSidecar, "{}").ConfigureAwait(false);
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        for (var ordinal = 1; ordinal <= count; ordinal++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO processing_outputs(
                    output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                    payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                    algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                    committed_unix_ms)
                VALUES ($output, $capture, 'rolling-window-agent', 'calibration', $artifact,
                        'Calibrated', 'ineligible', $payload, $sidecar, X'7B7D', $recipe,
                        X'5B5D', X'7B7D', 1, $sequence, $committed);
                """;
            command.Parameters.AddWithValue(
                "$output", string.Concat("F", ordinal.ToString("D63", CultureInfo.InvariantCulture)));
            command.Parameters.AddWithValue(
                "$capture", Guid.Parse($"90000000-0000-0000-0000-{ordinal:D12}").ToString("N"));
            command.Parameters.AddWithValue(
                "$artifact", Guid.Parse($"80000000-0000-0000-0000-{ordinal:D12}").ToString("N"));
            command.Parameters.AddWithValue("$payload", $"ineligible/{ordinal:D4}.bin");
            command.Parameters.AddWithValue("$sidecar", $"ineligible/{ordinal:D4}.json");
            command.Parameters.AddWithValue("$recipe", new string('F', 64));
            command.Parameters.AddWithValue("$sequence", CaptureCount);
            command.Parameters.AddWithValue("$committed", FixtureUtc.ToUnixTimeMilliseconds() + ordinal);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await transaction.CommitAsync().ConfigureAwait(false);
        return new IneligibleOutputControl(
            Guid.Parse("80000000-0000-0000-0000-000000000001"), controlPayload);
    }

    private static async Task<string[]> ReadPinsAsync(string root, Guid executionId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, input_ordinal, window_position, output_identity_sha256
            FROM processing_execution_output_input_pins
            WHERE execution_id = $execution
            ORDER BY node_id, input_ordinal;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        var pins = new List<string>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            pins.Add(string.Create(CultureInfo.InvariantCulture,
                $"{reader.GetString(0)}|{reader.GetInt32(1)}|{reader.GetInt32(2)}|{reader.GetString(3)}"));
        }
        return pins.ToArray();
    }

    private static async Task<long> CountUnreleasedPinsAsync(string root, Guid executionId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM processing_execution_output_input_pins
            WHERE execution_id = $execution AND released_flag = 0;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task SetStandardLaneStateAsync(string root, long workId, string state)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE capture_lane_work SET state = $state
            WHERE work_id = $work AND lane_name = 'standard';
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$work", workId);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteCaptureProcessingStore CreateStore(string root, int maximumWindowInputs = 32)
        => new(Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressReserveBytes = 0,
            ProcessingGraphs = new ProcessingGraphExecutionOptions
            {
                MaximumWindowInputs = maximumWindowInputs
            }
        }));

    private static ServiceProvider CreateProvider(
        string root,
        ICaptureProcessingFaultInjector? faultInjector = null,
        int maximumWindowInputs = 32)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog(
            [new CelestialCatalogObject("star", "Star", 2.5, 20, 1)]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
                ["CameraAgent:ProcessingGraphs:MaximumWindowInputs"] = maximumWindowInputs.ToString(
                    CultureInfo.InvariantCulture),
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            }).Build());
        if (faultInjector is not null)
        {
            services.AddSingleton<ICaptureProcessingFaultInjector>(faultInjector);
        }
        return services.BuildServiceProvider();
    }

    private static CameraModuleConfig CreateRevisionBarrierConfiguration(
        SyntheticCalibrationModelV1 syntheticCalibration)
    {
        var configuration = CreateConfiguration();
        return configuration with
        {
            Pipeline = configuration.Pipeline with
            {
                Steps =
                [
                    configuration.Pipeline.Steps[0] with
                    {
                        Options = JsonSerializer.SerializeToElement(new
                        {
                            strategy = "SyntheticReferences",
                            outputVariant = "synthetic-corrected",
                            syntheticCalibration
                        })
                    },
                    new CaptureProcessingStepConfig(
                        "Telemetry",
                        "revision-barrier",
                        Order: 22,
                        DependsOn: ["calibration"]),
                    configuration.Pipeline.Steps[1]
                ]
            }
        };
    }

    private static CameraModuleConfig CreateConfiguration(bool syntheticReferences = true)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(
                new VirtualSkyCameraModuleOptions
                {
                    MaximumResults = 10,
                    ShotNoiseEnabled = false,
                    FixedSceneUtc = FixtureUtc,
                    SyntheticCalibration = SyntheticCalibration
                })),
            new CameraRigConfig(
                new SensorProfile("rolling-window", 64, 48, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                    SensorResponseMode.Monochrome),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    PrincipalPointX: 32, PrincipalPointY: 24, ImageCircleRadiusPixels: 23),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig(
                [
                    // Synthetic references are what the production smoke uses, and they deliberately change the
                    // calibrated artifact's calibration and mask compatibility axes, which is the case a
                    // pass-through calibration would not cover.
                    new CaptureProcessingStepConfig(
                        "Calibration",
                        "calibration",
                        Options: syntheticReferences
                            ? JsonSerializer.SerializeToElement(new
                            {
                                strategy = "SyntheticReferences",
                                outputVariant = "synthetic-corrected",
                                syntheticCalibration = SyntheticCalibration
                            })
                            : JsonSerializer.SerializeToElement(new { }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig(
                        "RollingCombination",
                        RollingNodeId,
                        Options: JsonSerializer.SerializeToElement(new { windowSize = WindowSize }),
                        DependsOn: ["calibration"])
                ],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent),
            "rolling-window-agent");

    private static readonly SyntheticCalibrationModelV1 SyntheticCalibration = new()
    {
        Seed = 195,
        DarkExposure = TimeSpan.FromSeconds(1),
        FlatExposure = TimeSpan.FromSeconds(1),
        Gain = 1,
        TemperatureC = -10
    };

    private sealed class ArmableNodeFaultInjector(string nodeId) : ICaptureProcessingFaultInjector
    {
        private int _armed;

        internal void Arm() => Volatile.Write(ref _armed, 1);

        public void Inject(CaptureProcessingFaultPoint point, string candidateNodeId)
        {
            if (point == CaptureProcessingFaultPoint.BeforeNodeExecution &&
                string.Equals(candidateNodeId, nodeId, StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new InvalidOperationException("Injected one-shot revision barrier failure.");
            }
        }
    }

    private static async Task<CaptureLoopSubmission> CreateSubmissionAsync(
        ICameraModule module,
        int index,
        CancellationToken cancellationToken)
    {
        var startedUtc = FixtureUtc.AddSeconds(index * 5L);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, -10);
        var request = new CaptureRequest(startedUtc, TimeSpan.FromSeconds(2), CaptureMode.Still, setpoint);
        var result = await module.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
        return new CaptureLoopSubmission(
            request,
            result with
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(1), startedUtc.AddSeconds(1.1))
            },
            startedUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));
    }
}
