using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetainedAstrometry_QuarantinedRawLineageKeepsProgressAcrossDeadlines()
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(fixture.Fixture.Options, store, storage, telemetry);
            var oldest = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
            var solutions = new List<DurableProcessingOutput> { oldest };
            var rawPaths = new List<RawIngressPaths>();
            for (var index = 1; index <= 3; index++)
            {
                var sequence = 1 + index * 60;
                var reference = await MeasuredAstrometryFixture.CreateAtAsync(index * 20).ConfigureAwait(false);
                var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                    captureSequence: sequence, reference: reference, prior: (sequence - 60, index)).ConfigureAwait(false);
                var output = await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false);
                solutions.Add(output);
                rawPaths.Add(RawIngressFileStore.GetPaths(root, next.Fixture.Manifest.Descriptor.Timing.ExposureStartedUtc,
                    output.Artifact.SourceArtifactIds[0]));
            }

            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var selected = new CameraAgentRetainedAstrometryService(store, artifacts);
            var expected = CameraAgentLatestAstrometry.FromVerified(await selected.GetAsync(oldest.Capture.CaptureId, token).ConfigureAwait(false));
            Assert.IsNotNull(expected);
            var newest = solutions[^1];
            var expectedNew = CameraAgentLatestAstrometry.FromVerified(await selected.GetAsync(newest.Capture.CaptureId, token).ConfigureAwait(false));
            Assert.IsNotNull(expectedNew);
            var restoredPaths = rawPaths[^1];
            var restoredPayload = await File.ReadAllBytesAsync(restoredPaths.PayloadAbsolutePath, token).ConfigureAwait(false);
            var restoredSidecar = await File.ReadAllBytesAsync(restoredPaths.SidecarAbsolutePath, token).ConfigureAwait(false);

            foreach (var paths in rawPaths)
            {
                var bytes = await File.ReadAllBytesAsync(paths.PayloadAbsolutePath, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(paths.PayloadAbsolutePath, [.. bytes, 0], token).ConfigureAwait(false);
            }
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            var reconciliation = await new RawIngressReconciler(root, journal)
                .RunAsync(_ => ValueTask.FromResult(default(CaptureMaintenanceEntry)), token).ConfigureAwait(false);
            Assert.AreEqual(3, reconciliation.Quarantined);
            foreach (var solution in solutions.Skip(1))
            {
                var raw = await artifacts.OpenContentAsync(solution.Artifact.SourceArtifactIds[0], token).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict, raw.Status);
                Assert.IsNull(raw.Content);
            }

            var probeOrder = new List<Guid>();
            var assessmentIds = solutions.Select(static solution => solution.ArtifactId).ToHashSet();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            delayed.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, CancellationToken cancellation) =>
                {
                    if (cancellation.CanBeCanceled && assessmentIds.Contains(id))
                    {
                        probeOrder.Add(id);
                        if (probeOrder.Count == 2)
                        {
                            entered.TrySetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation).ConfigureAwait(false);
                        }
                    }
                    return await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                });

            using var reader = CreateUnpooledAstrometryReader(store, delayed.Object);
            ICameraAgentRetainedAstrometryService service = reader;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                probeOrder.Clear();
                entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = service.ReadLatestAcceptedAsync(deadline.Token).AsTask();
                try
                {
                    await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                    var exact = await service.GetAsync(oldest.Capture.CaptureId, token).ConfigureAwait(false);
                    Assert.AreEqual(expected, CameraAgentLatestAstrometry.FromVerified(exact));
                }
                finally { await deadline.CancelAsync().ConfigureAwait(false); }
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => pending).ConfigureAwait(false);
                CollectionAssert.AreEqual(new[] { solutions[3 - attempt].ArtifactId, solutions[2 - attempt].ArtifactId },
                    probeOrder, $"Deadline {attempt + 1} must resume the unread raw lineage.");
            }

            var completed = await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
            Assert.IsTrue(completed.Completed);
            Assert.AreEqual(expected, completed.Summary);

            // Resolve the same store through the raw ingress constructor as the host does.
            using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
            var ingressState = new RawIngressState(TimeProvider.System);
            using var ingressTelemetry = new RawIngressTelemetry(ingressState);
            using var rawIngress = ActivatorUtilities.CreateInstance<RawCaptureIngress>(services, fixture.Fixture.Options,
                new FileSystemStorageCapacityProvider(), ingressState, TimeProvider.System, ingressTelemetry,
                NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector());
            var evidence = await rawIngress.ReadEvidencePageAsync(10, null, token).ConfigureAwait(false);
            var repair = evidence.Items.Single(item => item.Target.CaptureId == newest.Capture.CaptureId);
            Assert.AreEqual("quarantined", repair.Target.State);
            await File.WriteAllBytesAsync(restoredPaths.PayloadAbsolutePath, restoredPayload, token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(restoredPaths.SidecarAbsolutePath, restoredSidecar, token).ConfigureAwait(false);
            Assert.AreEqual(expected, (await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary,
                "Restored bytes alone do not release the durable quarantine.");
            var receipt = await rawIngress.ReleaseEvidenceAsync(repair.Target, RawEvidenceReleaseAction.Revalidate,
                "verify-restored-raw", "owner", "evidence-restored", token).ConfigureAwait(false);
            Assert.IsFalse(receipt.Duplicate);
            Assert.AreEqual(RawEvidenceReleaseAction.Revalidate, receipt.Audit.Action);
            Assert.AreEqual(expectedNew, (await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary,
                "The audited release must reopen this candidate while the reader and older fallback are warm.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(1, 3)]
    [DataRow(3, 1)]
    [DataRow(4, 3)]
    [DataRow(3, 0)]
    public async Task RetainedAstrometry_QuarantineWinsOverOtherMissingLineage(int missingOrdinal, int quarantineOrdinal)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(fixture.Fixture.Options, store, storage, telemetry);
            var oldest = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
            var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true, captureSequence: 61,
                reference: await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false), prior: (1, 1)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false);
            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var reader = new CameraAgentRetainedAstrometryService(store, artifacts);
            ICameraAgentRetainedAstrometryService service = reader;
            var expectedOld = CameraAgentLatestAstrometry.FromVerified(await service.GetAsync(oldest.Capture.CaptureId, token).ConfigureAwait(false));
            var expectedNew = (await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary;
            Assert.IsNotNull(expectedOld);
            Assert.AreEqual(newest.ArtifactId, expectedNew?.ArtifactId);

            var sources = newest.Artifact.SourceArtifactIds;
            var missing = missingOrdinal == sources.Count ? newest :
                (await store.ReadOutputByArtifactIdAsync(sources[missingOrdinal], token).ConfigureAwait(false))!;
            var missingPath = Path.Combine(root, missing.PayloadRelativePath);
            var displaced = Path.Combine(root, "displaced");
            File.Move(missingPath, displaced);
            var reconciler = new DerivedProductReconciler(root, store);
            var missingReceipt = await reconciler.RunAsync(token).ConfigureAwait(false);
            Assert.AreEqual(1, missingReceipt.Missing);
            Assert.AreEqual("Missing", (await store.ReadOutputByArtifactIdAsync(missing.ArtifactId, token).ConfigureAwait(false))?.AvailabilityState);
            var selectedMissing = await service.GetAsync(newest.Capture.CaptureId, token).ConfigureAwait(false);
            Assert.IsTrue(selectedMissing.HasMissingEvidence);
            Assert.IsNull(selectedMissing.Mapping);
            var pending = await service.ReadLatestAcceptedAsync(expectedNew, token).ConfigureAwait(false);
            Assert.IsFalse(pending.Completed);
            Assert.IsFalse(pending.PreviousSummaryQuarantined);

            DurableProcessingOutput? damaged = null;
            byte[]? originalPayload = null;
            byte[]? originalSidecar = null;
            if (quarantineOrdinal == 0)
            {
                var raw = RawIngressFileStore.GetPaths(root, next.Fixture.Manifest.Descriptor.Timing.ExposureStartedUtc, sources[0]);
                var bytes = await File.ReadAllBytesAsync(raw.PayloadAbsolutePath, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(raw.PayloadAbsolutePath, [.. bytes, 0], token).ConfigureAwait(false);
                var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
                var rawReceipt = await new RawIngressReconciler(root, journal)
                    .RunAsync(_ => ValueTask.FromResult(default(CaptureMaintenanceEntry)), token).ConfigureAwait(false);
                Assert.AreEqual(1, rawReceipt.Quarantined);
            }
            else
            {
                damaged = (await store.ReadOutputByArtifactIdAsync(sources[quarantineOrdinal], token).ConfigureAwait(false))!;
                var payloadPath = Path.Combine(root, damaged.PayloadRelativePath);
                originalPayload = await File.ReadAllBytesAsync(payloadPath, token).ConfigureAwait(false);
                originalSidecar = await File.ReadAllBytesAsync(Path.Combine(root, damaged.SidecarRelativePath), token).ConfigureAwait(false);
                var corrupt = (byte[])originalPayload.Clone();
                corrupt[0] ^= 1;
                await File.WriteAllBytesAsync(payloadPath, corrupt, token).ConfigureAwait(false);
                await reconciler.RunAsync(token).ConfigureAwait(false);
                Assert.AreEqual("Quarantined", (await store.ReadOutputByArtifactIdAsync(damaged.ArtifactId, token).ConfigureAwait(false))?.AvailabilityState);
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var latest = await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
                Assert.IsTrue(latest.Completed, $"A confirmed quarantined input must outrank recoverable Missing on attempt {attempt + 1}.");
                Assert.AreEqual(expectedOld, latest.Summary);
            }

            if (damaged is not null)
            {
                await File.WriteAllBytesAsync(Path.Combine(root, damaged.PayloadRelativePath), originalPayload!, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(root, damaged.SidecarRelativePath), originalSidecar!, token).ConfigureAwait(false);
                await store.SetOutputAvailabilityAsync(damaged.OutputIdentitySha256, "Available", null, token).ConfigureAwait(false);
                Assert.IsFalse((await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Completed);
                File.Move(displaced, missingPath);
                await reconciler.RunAsync(token).ConfigureAwait(false);
                Assert.AreEqual("Available", (await store.ReadOutputByArtifactIdAsync(missing.ArtifactId, token).ConfigureAwait(false))?.AvailabilityState);
                Assert.IsFalse((await service.GetAsync(newest.Capture.CaptureId, token).ConfigureAwait(false)).HasMissingEvidence);
                Assert.AreEqual(expectedNew, (await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
