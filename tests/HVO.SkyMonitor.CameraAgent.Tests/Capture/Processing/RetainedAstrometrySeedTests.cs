using System.Collections.Concurrent;
using System.Diagnostics;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetainedAstrometry_ColdSeedSurvivesFiveSecondRefresh(bool pauseAtPublication)
    {
        var root = CreateTestRoot();
        try
        {
            var old = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            using var pause = new AstrometrySeedPause(pauseAtPublication
                ? CaptureProcessingFaultPoint.BeforeAstrometrySeedPublished
                : CaptureProcessingFaultPoint.AfterAstrometrySeedRowMaterialized);
            using var store = new SqliteCaptureProcessingStore(old.Fixture.Options, faultInjector: pause);
            var persistence = CreatePersistence(old.Fixture.Options, store, storage, telemetry);
            var accepted = await ProduceLatestReadFixtureAsync(old, store, persistence, telemetry, gate).ConfigureAwait(false);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
            var newer = await CreateAstrometryDurabilityFixtureAsync(root, "rejected", hostCompatible: true,
                captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
            _ = await ProduceLatestReadFixtureAsync(newer, store, persistence, telemetry, gate).ConfigureAwait(false);

            using var artifacts = new CameraAgentArtifactService(old.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var reader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var service = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(reader);
            var clock = Stopwatch.StartNew();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = Task.Run(async () => await service.GetLatestAcceptedAstrometryAsync(deadline.Token).ConfigureAwait(false));
            bool callerCanceledWhileSeedPaused;
            CameraAgentLatestAstrometry? expected;
            using var retryDeadline = new CancellationTokenSource();
            Task<OperatorUiResult<CameraAgentLatestAstrometryRead>>? retry = null;
            try
            {
                await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.HasCount(pauseAtPublication ? 2 : 1, pause.Rows);
                using var selectedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var selected = await service.GetRetainedAstrometryAsync(accepted.Capture.CaptureId, selectedDeadline.Token).ConfigureAwait(false);
                Assert.IsTrue(selected.IsSuccess, selected.Message);
                Assert.AreEqual(AstrometricAssessmentStatus.Accepted, selected.Value?.Solution?.Assessment.Status);
                expected = CameraAgentLatestAstrometry.FromVerified(selected.Value!);
                Assert.IsNotNull(expected);
                Console.WriteLine($"Selected read verified the fit at {clock.Elapsed.TotalMilliseconds:F0} ms during the cold seed.");

                callerCanceledWhileSeedPaused = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(7))).ConfigureAwait(false) == first;
                Console.WriteLine($"First refresh returned while the seed was paused: {callerCanceledWhileSeedPaused} at {clock.Elapsed.TotalMilliseconds:F0} ms.");
                if (callerCanceledWhileSeedPaused)
                {
                    clock.Restart();
                    retryDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                    retry = service.GetLatestAcceptedAstrometryAsync(retryDeadline.Token).AsTask();
                    Assert.IsFalse(retry.IsCompleted, "The retry must wait for the same seed while no snapshot has been published.");
                }
            }
            finally
            {
                pause.Resume();
                await Assert.ThrowsAsync<OperationCanceledException>(() => first).ConfigureAwait(false);
            }

            if (retry is null)
            {
                clock.Restart();
                retryDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                retry = service.GetLatestAcceptedAstrometryAsync(retryDeadline.Token).AsTask();
            }
            var next = await retry.ConfigureAwait(false);
            Assert.IsTrue(next.IsSuccess, next.Message);
            Assert.AreEqual(expected, next.Value?.Summary);
            Console.WriteLine($"Retry verified the same solution after {clock.Elapsed.TotalMilliseconds:F0} ms; materialized captures: {string.Join(", ", pause.Rows)}.");
            Assert.HasCount(2, pause.Rows, "The original seed must finish without reading any row twice.");
            Assert.IsTrue(callerCanceledWhileSeedPaused, "A page deadline must release the caller even if SQLite has not returned.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetainedAstrometry_ColdSeedAppliesConcurrentChangesAndRestarts()
    {
        var root = CreateTestRoot();
        try
        {
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            using var pause = new AstrometrySeedPause(CaptureProcessingFaultPoint.AfterAstrometrySeedRowMaterialized);
            using var store = new SqliteCaptureProcessingStore(first.Fixture.Options, faultInjector: pause);
            var persistence = CreatePersistence(first.Fixture.Options, store, storage, telemetry);
            var original = await ProduceLatestReadFixtureAsync(first, store, persistence, telemetry, gate).ConfigureAwait(false);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
            var newer = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
            using var artifacts = new CameraAgentArtifactService(first.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var reader = new CameraAgentRetainedAstrometryService(store, artifacts);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pending = reader.ReadLatestAcceptedAsync(deadline.Token).AsTask();
            DurableProcessingOutput published;
            CameraAgentLatestAstrometryRead latest;
            try
            {
                await pause.Entered.WaitAsync(deadline.Token).ConfigureAwait(false);
                await store.SetOutputAvailabilityAsync(original.OutputIdentitySha256, "Missing", "fixture-reconciliation",
                    deadline.Token).ConfigureAwait(false);
                published = await ProduceLatestReadFixtureAsync(newer, store, persistence, telemetry, gate).ConfigureAwait(false);
                await EvictLatestReadFixtureAsync(store, original).ConfigureAwait(false);
            }
            finally
            {
                pause.Resume();
                latest = await pending.ConfigureAwait(false);
            }

            Assert.IsTrue(latest.Completed);
            Assert.AreEqual(published.ArtifactId, latest.Summary?.ArtifactId);
            Assert.HasCount(1, pause.Rows, "The pending seed sees only the original SQLite read snapshot.");
            var current = await store.ReadAstrometryCandidatesAsync(deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(published.Capture.CaptureId, current.Single().CaptureId);

            using var restarted = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var restartedArtifacts = new CameraAgentArtifactService(first.Fixture.Options, restarted, new CameraAgentPreviewEncoder());
            using var restartedReader = new CameraAgentRetainedAstrometryService(restarted, restartedArtifacts);
            Assert.AreEqual(latest, await restartedReader.ReadLatestAcceptedAsync(deadline.Token).ConfigureAwait(false),
                "A fresh store must rebuild the same accepted fit, identity and midpoint from the retained journal.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RetainedAstrometry_FailedColdSeedRemainsIncompleteAndRetries()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: new AstrometrySeedFailure());
            using var artifacts = new CameraAgentArtifactService(fixture.Options, store, new CameraAgentPreviewEncoder());
            using var reader = new CameraAgentRetainedAstrometryService(store, artifacts);

            Assert.AreEqual(new CameraAgentLatestAstrometryRead(false),
                await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(true),
                await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RetainedAstrometry_DisposingStoreJoinsItsColdSeed()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var pause = new AstrometrySeedPause(CaptureProcessingFaultPoint.BeforeAstrometrySeedPublished);
            using var store = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: pause);
            var pending = store.ReadAstrometryCandidatesAsync(CancellationToken.None).AsTask();
            Task? stopping = null;
            try
            {
                await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                stopping = Task.Run(store.Dispose);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // Calls before disposal would wait for the same paused seed. Calls after it began refuse immediately.
                while (true)
                {
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    check.CancelAfter(TimeSpan.FromMilliseconds(20));
                    try { _ = await store.ReadAstrometryCandidatesAsync(check.Token).ConfigureAwait(false); }
                    catch (ObjectDisposedException) { break; }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                }
                Assert.IsFalse(stopping.IsCompleted, "Disposal must wait for its outstanding SQLite read.");
            }
            finally
            {
                pause.Resume();
                if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class AstrometrySeedFailure : ICaptureProcessingFaultInjector
    {
        private bool failed;

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (failed || point != CaptureProcessingFaultPoint.BeforeAstrometrySeedPublished) return;
            failed = true;
            throw new IOException("Injected astrometry seed failure before publication.");
        }
    }

    private sealed class AstrometrySeedPause(CaptureProcessingFaultPoint pausePoint) : ICaptureProcessingFaultInjector, IDisposable
    {
        private readonly ManualResetEventSlim resume = new(false);
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pauses;

        public Task Entered => entered.Task;
        public ConcurrentQueue<string> Rows { get; } = new();

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == CaptureProcessingFaultPoint.AfterAstrometrySeedRowMaterialized) Rows.Enqueue(nodeId);
            if (point != pausePoint || Interlocked.Increment(ref pauses) != 1) return;
            entered.SetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The test did not resume the astrometry seed.");
        }

        public void Resume() => resume.Set();

        public void Dispose() => resume.Dispose();
    }
}
