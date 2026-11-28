using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetainedAstrometry_InterruptedRefreshesKeepVerifiedRejectionProgress()
    {
        var root = CreateTestRoot();
        try
        {
            var old = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var writer = SiloInfrastructureFixture.OpenProcessingStore(old.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(old.Fixture.Options, writer, telemetry);
            var acceptedOutput = await ProduceLatestReadFixtureAsync(old, writer, persistence, telemetry, gate).ConfigureAwait(false);
            var rejectedOutputs = new Stack<DurableProcessingOutput>();
            for (var index = 1; index <= 3; index++)
            {
                var reference = await MeasuredAstrometryFixture.CreateAtAsync(index * 20).ConfigureAwait(false);
                var sequence = 1 + index * 60;
                var rejected = await CreateAstrometryDurabilityFixtureAsync(root, "rejected", hostCompatible: true,
                    captureSequence: sequence, reference: reference, prior: (sequence - 60, index)).ConfigureAwait(false);
                rejectedOutputs.Push(await ProduceLatestReadFixtureAsync(rejected, writer, persistence, telemetry, gate).ConfigureAwait(false));
            }

            using var store = SiloInfrastructureFixture.OpenProcessingStore(old.Fixture.Options);
            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(old.Fixture.Options, store);
            var delayed = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = new List<Guid>();
            delayed.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid artifactId, CancellationToken token) =>
                {
                    if (token.CanBeCanceled)
                    {
                        reads.Add(artifactId);
                        if (reads.Count == 2)
                        {
                            entered.TrySetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                        }
                    }
                    return await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false);
                });

            using var reader = CreateUnpooledAstrometryReader(store, delayed.Object);
            var oldCapture = old.Fixture.Manifest.Descriptor.Capture.CaptureId;
            var ordered = rejectedOutputs.Select(output => output.ArtifactId).ToArray();
            for (var request = 0; request < 2; request++)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                reads.Clear();
                var pending = reader.ReadLatestAcceptedAsync(cancellation.Token).AsTask();
                await entered.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
                CollectionAssert.AreEqual(ordered[request..(request + 2)], reads);
                Assert.IsFalse((await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false)).Completed);
                var exact = await reader.GetAsync(oldCapture, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(oldCapture, exact.CaptureId);
                Assert.AreEqual(AstrometricAssessmentStatus.Accepted, exact.Solution?.Assessment.Status);
                await cancellation.CancelAsync().ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => pending).ConfigureAwait(false);
            }

            var latest = await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(latest.Completed);
            Assert.AreEqual(oldCapture, latest.Summary?.CaptureId);
            var priorReads = artifacts.PayloadValidationReads;
            Assert.AreEqual(latest, await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(5, artifacts.PayloadValidationReads - priorReads, "Only the accepted fit is re-verified.");

            var accepted = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, acceptedOutput, CancellationToken.None)
                .ConfigureAwait(false);
            accepted.FlipByte();
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(true),
                await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false),
                "A payload with a confirmed checksum conflict cannot supply an accepted fit.");
            accepted.FlipByte();
            Assert.AreEqual(latest, await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));

            await EvictLatestReadFixtureAsync(store, acceptedOutput).ConfigureAwait(false);
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(true),
                await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));

            var unavailableOutput = rejectedOutputs.Peek().OutputIdentitySha256;
            await store.SetOutputAvailabilityAsync(unavailableOutput, "Missing", "fixture-missing", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(false),
                await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            await store.RestoreMissingOutputAvailableAsync(unavailableOutput, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(true),
                await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetainedAstrometry_TransientArtifactFailureKeepsNewestFitPendingAcrossRestart()
    {
        var root = CreateTestRoot();
        try
        {
            var old = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = SiloInfrastructureFixture.OpenProcessingStore(old.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(old.Fixture.Options, store, telemetry);
            var olderOutput = await ProduceLatestReadFixtureAsync(old, store, persistence, telemetry, gate).ConfigureAwait(false);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
            var newer = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true, captureSequence: 61,
                reference: reference, prior: (1, 1)).ConfigureAwait(false);
            var newerOutput = await ProduceLatestReadFixtureAsync(newer, store, persistence, telemetry, gate).ConfigureAwait(false);
            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(old.Fixture.Options, store);
            var interrupted = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            var interruptNextOpen = false;
            var observedUnavailable = false;
            interrupted.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid artifactId, CancellationToken token) =>
                {
                    var interrupt = interruptNextOpen && artifactId == newerOutput.ArtifactId;
                    if (interrupt)
                    {
                        interruptNextOpen = false;
                        // The reader has already selected an Available row. Its real artifact-service open sees the
                        // persisted availability change, as it would during concurrent reconciliation.
                        await store.SetOutputAvailabilityAsync(newerOutput.OutputIdentitySha256, "Missing",
                            "fixture-interrupted-open", token).ConfigureAwait(false);
                    }
                    var result = await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false);
                    if (interrupt)
                    {
                        Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, result.Status);
                        Assert.IsNull(result.Content);
                        observedUnavailable = true;
                    }
                    return result;
                });

            using var reader = CreateUnpooledAstrometryReader(store, interrupted.Object);
            var baseline = await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(baseline.Completed);
            Assert.AreEqual(newerOutput.Capture.CaptureId, baseline.Summary?.CaptureId);
            interruptNextOpen = true;
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(false),
                await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(observedUnavailable);
            var selected = await reader.GetAsync(olderOutput.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(AstrometricAssessmentStatus.Accepted, selected.Solution?.Assessment.Status);

            using var restarted = SiloInfrastructureFixture.OpenProcessingStore(old.Fixture.Options);
            using var restartedArtifacts = SiloInfrastructureFixture.OpenArtifactReader(old.Fixture.Options, restarted);
            using var restartedReader = new CameraAgentRetainedAstrometryService(restarted, restartedArtifacts);
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(false),
                await restartedReader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            await EvictLatestReadFixtureAsync(restarted, olderOutput).ConfigureAwait(false);
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(false),
                await restartedReader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            await restarted.RestoreMissingOutputAvailableAsync(newerOutput.OutputIdentitySha256, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(baseline, await restartedReader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false),
                "A successful retry recovers the same solution identity and midpoint.");
            await EvictLatestReadFixtureAsync(restarted, newerOutput).ConfigureAwait(false);
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(true),
                await restartedReader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<DurableProcessingOutput> ProduceLatestReadFixtureAsync(AstrometryDurabilityFixture fixture,
        SqliteCaptureProcessingStore store, CaptureProcessingPersistence persistence, CaptureProcessingTelemetry telemetry,
        CaptureAstrometryExecutionGate gate)
    {
        var graph = AstrometryGraph(fixture, new(new ProcessingRecipeExecutor()), gate, persistence: persistence);
        var result = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Fixture.Item, graph, persistence,
            telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        var captureId = fixture.Fixture.Manifest.Descriptor.Capture.CaptureId;
        var node = await store.ReadNodeAsync(captureId, "astrometry", CancellationToken.None).ConfigureAwait(false);
        return node!.Outputs.Single(output => output.ProductSchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
    }

    private static ValueTask EvictLatestReadFixtureAsync(SqliteCaptureProcessingStore store, DurableProcessingOutput output)
        => store.CompleteLifecycleOperationAsync(new(
            Guid.NewGuid().ToString("N"), "delete", output.OutputIdentitySha256,
            output.PayloadRelativePath, output.SidecarRelativePath, "unused", "fixture-eviction", 0),
            CancellationToken.None);
}
