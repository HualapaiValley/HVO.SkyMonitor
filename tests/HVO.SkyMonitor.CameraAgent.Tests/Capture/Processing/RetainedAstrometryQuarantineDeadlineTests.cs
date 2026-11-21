using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetainedAstrometry_ContextQuarantineReportsDisplayedIdentityAcrossDeadlinesAndFallbackRecovery()
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            using var artifacts = new CameraAgentArtifactService(first.Fixture.Options, store, new CameraAgentPreviewEncoder());
            var persistence = CreatePersistence(first.Fixture.Options, store, storage, telemetry);
            var older = await ProduceLatestReadFixtureAsync(first, store, persistence, telemetry, gate).ConfigureAwait(false);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
            var second = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(second, store, persistence, telemetry, gate).ConfigureAwait(false);
            var context = await store.ReadOutputByArtifactIdAsync(newest.Artifact.SourceArtifactIds[3], token).ConfigureAwait(false);
            Assert.IsNotNull(context);

            var fallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pauseNextFallback = false;
            var transientFallback = false;
            var transientNewest = false;
            var newestAssessmentOpens = 0;
            var interruptible = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            interruptible.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, CancellationToken cancellation) =>
                {
                    if (id == newest.ArtifactId)
                    {
                        Interlocked.Increment(ref newestAssessmentOpens);
                        if (transientNewest)
                            return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Unavailable);
                    }
                    if (id == older.ArtifactId && pauseNextFallback)
                    {
                        pauseNextFallback = false;
                        fallbackEntered.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation).ConfigureAwait(false);
                    }
                    if (id == older.ArtifactId && transientFallback)
                        return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Unavailable);
                    return await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                });

            using var reader = new CameraAgentRetainedAstrometryService(store, interruptible.Object);
            var firstRead = await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
            Assert.IsTrue(firstRead.Completed);
            var displayed = firstRead.Summary;
            Assert.IsNotNull(displayed);
            Assert.AreEqual(newest.ArtifactId, displayed.ArtifactId);

            transientNewest = true;
            var failedOpen = await reader.ReadLatestAcceptedAsync(displayed, token).ConfigureAwait(false);
            Assert.IsFalse(failedOpen.Completed);
            Assert.IsFalse(failedOpen.PreviousSummaryQuarantined);
            transientNewest = false;

            var reconciler = new DerivedProductReconciler(root, store);
            var contextPath = Path.Combine(root, context.PayloadRelativePath);
            var bytes = await File.ReadAllBytesAsync(contextPath, token).ConfigureAwait(false);
            bytes[0] ^= 1;
            await File.WriteAllBytesAsync(contextPath, bytes, token).ConfigureAwait(false);
            Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Quarantined);
            Assert.AreEqual("Quarantined", (await store.ReadOutputByArtifactIdAsync(context.ArtifactId, token)
                .ConfigureAwait(false))?.AvailabilityState);

            pauseNextFallback = true;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var interrupted = reader.ReadLatestAcceptedAsync(displayed, deadline.Token).AsTask();
            CameraAgentLatestAstrometry exactOlder;
            try
            {
                await fallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var frame = await reader.GetAsync(older.Capture.CaptureId, token).ConfigureAwait(false);
                var verified = CameraAgentLatestAstrometry.FromVerified(frame);
                Assert.IsNotNull(verified, "The latest lookup must leave exact-frame reads available.");
                exactOlder = verified;
                Assert.AreEqual(older.ArtifactId, exactOlder.ArtifactId);

                var partial = await interrupted.WaitAsync(TimeSpan.FromSeconds(7)).ConfigureAwait(false);
                AssertInvalidationWhilePending(partial);
                Assert.IsTrue(deadline.IsCancellationRequested);
            }
            finally
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                try { _ = await interrupted.ConfigureAwait(false); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }

            var probesBeforeRetry = Volatile.Read(ref newestAssessmentOpens);
            transientFallback = true;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var unrelated = await reader.ReadLatestAcceptedAsync(exactOlder, token).ConfigureAwait(false);
                Assert.IsFalse(unrelated.Completed);
                Assert.IsFalse(unrelated.PreviousSummaryQuarantined);
            }
            var differentIdentity = displayed with { SolutionIdentitySha256 = new string('0', 64) };
            var mismatched = await reader.ReadLatestAcceptedAsync(differentIdentity, token).ConfigureAwait(false);
            Assert.IsFalse(mismatched.Completed);
            Assert.IsFalse(mismatched.PreviousSummaryQuarantined);
            Assert.AreEqual(probesBeforeRetry, Volatile.Read(ref newestAssessmentOpens),
                "A later lookup must skip the confirmed quarantined candidate.");
            transientFallback = false;

            var fallbackPath = Path.Combine(root, older.PayloadRelativePath);
            var displaced = Path.Combine(root, "displaced-fallback");
            File.Move(fallbackPath, displaced);
            Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Missing);
            AssertInvalidationWhilePending(await reader.ReadLatestAcceptedAsync(displayed, token).ConfigureAwait(false));

            var invalidSelected = await reader.GetAsync(newest.Capture.CaptureId, token).ConfigureAwait(false);
            Assert.IsNull(invalidSelected.Solution);
            Assert.IsNull(invalidSelected.Mapping);
            File.Move(displaced, fallbackPath);
            _ = await reconciler.RunAsync(token).ConfigureAwait(false);
            var recovered = await reader.ReadLatestAcceptedAsync(displayed, token).ConfigureAwait(false);
            Assert.IsTrue(recovered.Completed);
            Assert.AreEqual(exactOlder, recovered.Summary,
                "The recovered fallback must retain its verified artifact, solution identity and midpoint.");

            static void AssertInvalidationWhilePending(CameraAgentLatestAstrometryRead read)
            {
                Assert.IsFalse(read.Completed);
                Assert.IsNull(read.Summary);
                Assert.IsTrue(read.PreviousSummaryQuarantined);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
