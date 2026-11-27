using System.Collections.Concurrent;
using System.Threading.Channels;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LayeredPresentation_RetiresExpiredFreshReadsAndPreservesOrdinaryCoalescing(bool fresh)
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Fixture.Options);
            var persistence = CreatePersistence(fixture.Fixture.Options, store, telemetry);
            var capture = fixture.Fixture.Manifest.Descriptor.Capture.CaptureId;
            var preview = await ProduceRetainedDisplayBaseAsync(fixture, persistence, telemetry).ConfigureAwait(false);
            var manifest = (await store.ReadCaptureProductsAsync(capture, OverlayManifestV1.CurrentSchemaVersion, 2, CancellationToken.None)
                .ConfigureAwait(false)).Single();
            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            var observed = Channel.CreateUnbounded<Task<CameraAgentArtifactContentResult>>();
            var opens = new ConcurrentQueue<Task<CameraAgentArtifactContentResult>>();
            var decoratingArtifacts = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            decoratingArtifacts.Setup(api => api.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellation) =>
                {
                    var read = artifacts.OpenContentAsync(id, cancellation).AsTask();
                    if (id == manifest.ArtifactId)
                    {
                        opens.Enqueue(read);
                        observed.Writer.TryWrite(read);
                    }
                    return new ValueTask<CameraAgentArtifactContentResult>(read);
                });
            var service = new CameraAgentLayeredPresentationService(store, decoratingArtifacts.Object);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var lifecycle = StorageLifecycleLock.ForRoot(root);
            await lifecycle.WaitAsync(deadline.Token).ConfigureAwait(false);
            var observedOutstanding = new List<int>();
            int afterViewerCloses;
            Task<CameraAgentLayeredPresentationResult> survivor;
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    using var waiter = new CancellationTokenSource();
                    var request = ReadAsync(waiter.Token);
                    if (fresh || attempt == 0)
                    {
                        var blocked = await observed.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                        Assert.IsFalse(blocked.IsCompleted, "The real artifact open must wait on the lifecycle gate.");
                    }
                    waiter.CancelAfter(TimeSpan.FromMilliseconds(10));
                    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                        await request.WaitAsync(deadline.Token).ConfigureAwait(false)).ConfigureAwait(false);
                    observedOutstanding.Add(opens.Count(task => !task.IsCompleted));
                }

                using var closingViewer = new CancellationTokenSource();
                var departing = ReadAsync(closingViewer.Token);
                if (fresh) _ = await observed.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                survivor = ReadAsync(deadline.Token);
                var liveOpen = fresh ? await observed.Reader.ReadAsync(deadline.Token).ConfigureAwait(false) : opens.Single();
                Assert.IsFalse(liveOpen.IsCompleted);
                await closingViewer.CancelAsync().ConfigureAwait(false);
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await departing.WaitAsync(deadline.Token).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.IsFalse(liveOpen.IsCompleted, "A different viewer still owns a live read.");
                Assert.IsFalse(survivor.IsCompleted);
                afterViewerCloses = opens.Count(task => !task.IsCompleted);
                Console.WriteLine($"Fresh={fresh}, outstanding after three expiries [{string.Join(", ", observedOutstanding)}], " +
                    $"manifest opens={opens.Count}, outstanding after viewer closes={afterViewerCloses}.");
            }
            finally { lifecycle.Release(); }

            var recovered = await survivor.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, recovered.Status, recovered.Reason);
            Assert.AreEqual(preview, recovered.Presentation?.BaseArtifactId);
            // Drain the real opens even when testing the baseline, where expired builds survive the hold.
            foreach (var open in opens)
            {
                try
                {
                    var result = await open.WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (result.Content is { } stream)
                    {
                        while (stream.CanRead) await Task.Delay(10, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (open.IsCanceled) { }
            }
            var lastContent = (await opens.Last().WaitAsync(deadline.Token).ConfigureAwait(false)).Content;
            Assert.IsNotNull(lastContent);
            Assert.IsFalse(lastContent.CanRead, "The manifest lease is disposed before a viewer receives its projection.");
            Assert.IsTrue(observedOutstanding.All(count => count == (fresh ? 0 : 1)),
                $"Outstanding artifact opens after each expiry: {string.Join(", ", observedOutstanding)}.");
            Assert.AreEqual(1, afterViewerCloses);
            Assert.AreEqual(fresh ? 5 : 1, opens.Count);

            Task<CameraAgentLayeredPresentationResult> ReadAsync(CancellationToken cancellation)
                => fresh ? service.GetCurrentAsync(capture, cancellation).AsTask() : service.GetAsync(capture, cancellation).AsTask();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LayeredPresentation_FreshReadBypassesAnOlderMeasuredProjectionAndRecovers()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Fixture.Options);
            using var numericalGate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(fixture.Fixture.Options, store, telemetry);
            var fit = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, numericalGate).ConfigureAwait(false);
            var preview = await ProduceRetainedDisplayBaseAsync(fixture, persistence, telemetry).ConfigureAwait(false);
            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var astrometry = new CameraAgentRetainedAstrometryService(store, artifacts);
            var reachedBase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holdFirst = 1;
            var decoratingArtifacts = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            decoratingArtifacts.Setup(api => api.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, CancellationToken cancellation) =>
                {
                    if (id == preview && Interlocked.Exchange(ref holdFirst, 0) != 0)
                    {
                        reachedBase.SetResult();
                        await releaseBase.Task.WaitAsync(cancellation).ConfigureAwait(false);
                    }
                    return await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                });
            var service = new CameraAgentLayeredPresentationService(store, decoratingArtifacts.Object,
                persistence: persistence, astrometry: astrometry);
            var capture = fit.Capture.CaptureId;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var old = service.GetAsync(capture, deadline.Token).AsTask();
            var sourcePath = Path.Combine(root, fixture.Fixture.Manifest.RelativeArtifactPath);
            var savedPath = sourcePath + ".held";
            try
            {
                await reachedBase.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                // The old request has already projected the fit. Remove its real raw evidence before the re-read.
                File.Move(sourcePath, savedPath);
                var invalidated = await service.GetCurrentAsync(capture, deadline.Token).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, invalidated.Status, invalidated.Reason);
                Assert.AreEqual(preview, invalidated.Presentation?.BaseArtifactId);
                Assert.IsEmpty(invalidated.Presentation!.Layers);
                Assert.IsNull(invalidated.Presentation.Astrometry?.Mapping);
                Assert.IsFalse(old.IsCompleted);

                File.Move(savedPath, sourcePath);
                var repaired = await service.GetCurrentAsync(capture, deadline.Token).ConfigureAwait(false);
                AssertMeasured(repaired);
                Assert.IsFalse(old.IsCompleted);
            }
            finally
            {
                releaseBase.TrySetResult();
                await old.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (File.Exists(savedPath)) File.Move(savedPath, sourcePath);
            }
            AssertMeasured(await old.ConfigureAwait(false));

            void AssertMeasured(CameraAgentLayeredPresentationResult result)
            {
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, result.Status, result.Reason);
                Assert.AreEqual(preview, result.Presentation?.Astrometry?.CompatibleBaseArtifactId);
                Assert.HasCount(2, result.Presentation!.Layers);
                Assert.IsNotNull(result.Presentation.Astrometry!.Mapping);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
