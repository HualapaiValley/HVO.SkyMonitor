using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetainedAstrometry_EvictedContextDoesNotHideReadableFallback(bool reopen)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(first.Fixture.Options, store, telemetry);
            var older = await ProduceLatestReadFixtureAsync(first, store, persistence, telemetry, gate).ConfigureAwait(false);
            var second = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true, captureSequence: 61,
                reference: await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false), prior: (1, 1)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(second, store, persistence, telemetry, gate).ConfigureAwait(false);
            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(first.Fixture.Options, store);
            using var reader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var expectedOlder = CameraAgentLatestAstrometry.FromVerified(await reader.GetAsync(older.Capture.CaptureId, token).ConfigureAwait(false));
            var expectedNewest = (await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary;
            Assert.IsNotNull(expectedOlder);
            Assert.AreEqual(newest.ArtifactId, expectedNewest?.ArtifactId);
            var measured = (await store.ReadOutputByArtifactIdAsync(newest.Artifact.SourceArtifactIds[1], token).ConfigureAwait(false))!;
            var context = (await store.ReadOutputByArtifactIdAsync(newest.Artifact.SourceArtifactIds[3], token).ConfigureAwait(false))!;
            var measuredPath = Path.Combine(root, measured.PayloadRelativePath);
            var displaced = Path.Combine(root, "displaced-measurement");
            File.Move(measuredPath, displaced);
            var reconciler = new DerivedProductReconciler(root, store);
            Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Missing);
            var temporary = await reader.ReadLatestAcceptedAsync(expectedNewest, token).ConfigureAwait(false);
            Assert.IsFalse(temporary.Completed);
            Assert.IsFalse(temporary.PreviousSummaryInvalidated);
            File.Move(displaced, measuredPath);
            await reconciler.RunAsync(token).ConfigureAwait(false);
            Assert.AreEqual(expectedNewest, (await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);

            File.Move(measuredPath, displaced);
            var contextPath = Path.Combine(root, context.PayloadRelativePath);
            var payload = await File.ReadAllBytesAsync(contextPath, token).ConfigureAwait(false);
            var corrupt = (byte[])payload.Clone();
            corrupt[0] ^= 1;
            await File.WriteAllBytesAsync(contextPath, corrupt, token).ConfigureAwait(false);
            Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Quarantined);
            Assert.AreEqual("Missing", (await store.ReadOutputByArtifactIdAsync(measured.ArtifactId, token).ConfigureAwait(false))?.AvailabilityState);
            Assert.AreEqual(expectedOlder, (await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);

            var diagnostic = (await store.ReadDiagnosticExpirationPageAsync(DateTimeOffset.MaxValue, null, null, 10, token)
                .ConfigureAwait(false)).Items.Single(item => item.QuarantineRelativePath is not null);
            var quarantinedFiles = Directory.GetFiles(Path.Combine(root, diagnostic.QuarantineRelativePath!)).Order(StringComparer.Ordinal).ToArray();
            Assert.HasCount(2, quarantinedFiles);
            var destination = $"processing-deletion-tombstones/{Guid.NewGuid():N}";
            var eviction = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", context.OutputIdentitySha256,
                Path.GetRelativePath(root, quarantinedFiles[0]), Path.GetRelativePath(root, quarantinedFiles[1]),
                destination, "retention-expired", quarantinedFiles.Sum(static path => new FileInfo(path).Length));
            await store.PlanLifecycleOperationAsync(eviction, token).ConfigureAwait(false);
            await reconciler.ResumeOperationAsync(eviction, token).ConfigureAwait(false);
            Assert.IsNull(await store.ReadOutputByArtifactIdAsync(context.ArtifactId, token).ConfigureAwait(false));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, destination)));
            Assert.IsTrue(quarantinedFiles.All(path => !File.Exists(path)));

            using var reopenedStore = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var reopenedArtifacts = SiloInfrastructureFixture.OpenArtifactReader(first.Fixture.Options, reopenedStore);
            using var reopenedReader = new CameraAgentRetainedAstrometryService(reopenedStore, reopenedArtifacts);
            ICameraAgentRetainedAstrometryService service = reopen ? reopenedReader : reader;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var read = await service.ReadLatestAcceptedAsync(expectedNewest, token).ConfigureAwait(false);
                Assert.IsTrue(read.PreviousSummaryEvicted);
                Assert.IsTrue(read.Completed, "The eviction must take precedence over a missing sibling.");
                Assert.AreEqual(expectedOlder, read.Summary);
            }

            // Republish the exact evicted context through the production output transaction. The
            // unchanged assessment identity must become eligible even on this warmed reader.
            var repairStore = reopen ? reopenedStore : store;
            await File.WriteAllBytesAsync(contextPath, payload, token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(root, context.SidecarRelativePath), context.EvidenceJson, token).ConfigureAwait(false);
            var node = AstrometryGraph(second, new(new ProcessingRecipeExecutor()), gate).Nodes.Single(item => item.Id == "astrometry");
            await repairStore.WriteNodeAsync(newest.Capture.CaptureId, node, DurableProcessingNodeStatus.Completed,
                null, 2, null, null, DateTimeOffset.UtcNow, null, ProcessingOutcomeStatus.Produced, [], 0, null, [context], token)
                .ConfigureAwait(false);
            var pendingRepair = await service.ReadLatestAcceptedAsync(expectedNewest, token).ConfigureAwait(false);
            Assert.IsFalse(pendingRepair.Completed);
            Assert.IsFalse(pendingRepair.PreviousSummaryInvalidated);
            File.Move(displaced, measuredPath);
            await new DerivedProductReconciler(root, repairStore).RunAsync(token).ConfigureAwait(false);
            Assert.AreEqual(expectedNewest, (await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("first", "before")]
    [DataRow("same-older", "before")]
    [DataRow("different-older", "after")]
    [DataRow("warm", "after")]
    [DataRow("first", "raw")]
    [DataRow("warm", "missing")]
    [DataRow("selected", "after")]
    public async Task RetainedAstrometry_ReturnedFitRequiresCurrentLineage(string identityMode, string change)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(first.Fixture.Options, store, telemetry);
            var oldest = await ProduceLatestReadFixtureAsync(first, store, persistence, telemetry, gate).ConfigureAwait(false);
            var second = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true, captureSequence: 61,
                reference: await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false), prior: (1, 1)).ConfigureAwait(false);
            var older = await ProduceLatestReadFixtureAsync(second, store, persistence, telemetry, gate).ConfigureAwait(false);
            var third = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true, captureSequence: 121,
                reference: await MeasuredAstrometryFixture.CreateAtAsync(40).ConfigureAwait(false), prior: (61, 2)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(third, store, persistence, telemetry, gate).ConfigureAwait(false);
            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(first.Fixture.Options, store);
            using var referenceReader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var expectedNewest = CameraAgentLatestAstrometry.FromVerified(await referenceReader.GetAsync(newest.Capture.CaptureId, token).ConfigureAwait(false));
            var expectedOlder = CameraAgentLatestAstrometry.FromVerified(await referenceReader.GetAsync(older.Capture.CaptureId, token).ConfigureAwait(false));
            var expectedOldest = CameraAgentLatestAstrometry.FromVerified(await referenceReader.GetAsync(oldest.Capture.CaptureId, token).ConfigureAwait(false));
            Assert.IsNotNull(expectedNewest);
            Assert.IsNotNull(expectedOlder);
            Assert.IsNotNull(expectedOldest);

            var measured = (await store.ReadOutputByArtifactIdAsync(newest.Artifact.SourceArtifactIds[1], token).ConfigureAwait(false))!;
            var contextId = newest.Artifact.SourceArtifactIds[3];
            var context = (await store.ReadOutputByArtifactIdAsync(contextId, token).ConfigureAwait(false))!;
            var rawPaths = RawIngressFileStore.GetPaths(root, third.Fixture.Manifest.Descriptor.Timing.ExposureStartedUtc,
                newest.Artifact.SourceArtifactIds[0]);
            var payloadPath = change == "raw" ? rawPaths.PayloadAbsolutePath : Path.Combine(root, measured.PayloadRelativePath);
            var sidecarPath = change == "raw" ? rawPaths.SidecarAbsolutePath : Path.Combine(root, measured.SidecarRelativePath);
            var payload = await File.ReadAllBytesAsync(payloadPath, token).ConfigureAwait(false);
            var sidecar = await File.ReadAllBytesAsync(sidecarPath, token).ConfigureAwait(false);
            var contextPath = Path.Combine(root, context.PayloadRelativePath);
            var displacedContext = Path.Combine(root, "displaced-context");
            var displacedMeasurement = Path.Combine(root, "displaced-measurement");
            var reconciler = new DerivedProductReconciler(root, store);
            var checkpoints = 0;
            var armed = false;
            var intercepted = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            intercepted.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, CancellationToken cancellation) =>
                {
                    if (!armed || id != contextId)
                        return await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                    armed = false;
                    checkpoints++;
                    if (change == "before")
                    {
                        await ChangeLineageAsync().ConfigureAwait(false);
                        return await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                    }
                    var opened = await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentArtifactReadStatus.Found, opened.Status);
                    try
                    {
                        await ChangeLineageAsync().ConfigureAwait(false);
                        return opened;
                    }
                    catch
                    {
                        await opened.Content!.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                });

            using var reader = CreateUnpooledAstrometryReader(store, intercepted.Object);
            ICameraAgentRetainedAstrometryService service = reader;
            if (identityMode == "warm")
                Assert.AreEqual(expectedNewest, (await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);
            if (change == "before")
            {
                File.Move(contextPath, displacedContext);
                Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Missing);
            }
            var previous = identityMode switch
            {
                "same-older" or "different-older" => expectedOldest,
                "warm" => expectedNewest,
                _ => null
            };
            var selected = identityMode == "different-older" ? expectedOlder : previous;
            var selectedIdentity = selected is null ? null :
                new CameraAgentAstrometryAssessmentIdentity(selected.CaptureId, selected.ArtifactId, selected.SolutionIdentitySha256);
            armed = true;
            if (identityMode == "selected")
            {
                var exact = await service.GetAsync(expectedNewest.CaptureId, token).ConfigureAwait(false);
                Assert.IsNull(exact.Solution);
                Assert.IsNull(exact.Mapping);
            }
            else
            {
                var firstResult = await service.ReadLatestAcceptedAsync(previous, selectedIdentity, token).ConfigureAwait(false);
                Assert.AreNotEqual(expectedNewest, firstResult.Summary, "A buffered fit cannot outlive its current journal lineage.");
                if (identityMode == "warm" && change == "missing")
                {
                    Assert.IsFalse(firstResult.Completed);
                    Assert.IsFalse(firstResult.PreviousSummaryInvalidated);
                    Assert.IsFalse(firstResult.SelectedAssessmentInvalidated);
                }
                else if (identityMode == "warm")
                {
                    Assert.IsTrue(firstResult.PreviousSummaryQuarantined);
                    Assert.IsTrue(firstResult.SelectedAssessmentInvalidated);
                }
                else
                {
                    Assert.IsFalse(firstResult.PreviousSummaryInvalidated);
                    Assert.IsFalse(firstResult.SelectedAssessmentInvalidated);
                }
            }
            Assert.AreEqual(1, checkpoints);

            if (change == "missing")
            {
                var stillPending = await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
                Assert.IsFalse(stillPending.Completed);
                File.Move(displacedMeasurement, payloadPath);
                await reconciler.RunAsync(token).ConfigureAwait(false);
            }
            else
            {
                var fallback = await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
                Assert.IsTrue(fallback.Completed);
                Assert.AreEqual(expectedOlder, fallback.Summary);
                await File.WriteAllBytesAsync(payloadPath, payload, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(sidecarPath, sidecar, token).ConfigureAwait(false);
                if (change == "raw")
                {
                    using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
                    var ingressState = new RawIngressState(TimeProvider.System);
                    using var ingressTelemetry = new RawIngressTelemetry(ingressState);
                    using var ingress = ActivatorUtilities.CreateInstance<RawCaptureIngress>(services, first.Fixture.Options,
                        new FileSystemStorageCapacityProvider(), ingressState, TimeProvider.System, ingressTelemetry,
                        NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector());
                    var quarantined = (await ingress.ReadEvidencePageAsync(10, null, token).ConfigureAwait(false)).Items
                        .Single(item => item.Target.CaptureId == newest.Capture.CaptureId);
                    await ingress.ReleaseEvidenceAsync(quarantined.Target, RawEvidenceReleaseAction.Revalidate,
                        "restore-lineage", "owner", "evidence-restored", token).ConfigureAwait(false);
                }
                else
                {
                    await store.SetOutputAvailabilityAsync(measured.OutputIdentitySha256, "Available", null, token).ConfigureAwait(false);
                }
            }
            Assert.AreEqual(expectedNewest, (await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary,
                "A legitimate recovery must make the exact newer fit readable again.");

            async Task ChangeLineageAsync()
            {
                if (change == "missing")
                {
                    File.Move(payloadPath, displacedMeasurement);
                    Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Missing);
                    return;
                }
                if (change == "raw")
                {
                    var replacement = Path.Combine(root, "corrupt-raw");
                    await File.WriteAllBytesAsync(replacement, [.. payload, 0], token).ConfigureAwait(false);
                    File.Move(replacement, payloadPath, overwrite: true);
                    var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
                    var receipt = await new RawIngressReconciler(root, journal)
                        .RunAsync(_ => ValueTask.FromResult(default(CaptureMaintenanceEntry)), token).ConfigureAwait(false);
                    Assert.AreEqual(1, receipt.Quarantined);
                    return;
                }
                var bytes = (byte[])payload.Clone();
                bytes[0] ^= 1;
                await File.WriteAllBytesAsync(payloadPath, bytes, token).ConfigureAwait(false);
                if (change == "before") File.Move(displacedContext, contextPath);
                Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Quarantined);
                if (change == "before")
                    Assert.AreEqual("Available", (await store.ReadOutputByArtifactIdAsync(contextId, token).ConfigureAwait(false))?.AvailabilityState);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
