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
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task RetainedAstrometry_ReconciledQuarantineCompletesLatestRead(bool hasOlder, bool quarantineInput)
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
            var latestOutput = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
            DurableProcessingOutput? olderOutput = null;
            if (hasOlder)
            {
                olderOutput = latestOutput;
                var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
                var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                    captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
                latestOutput = await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false);
            }

            var captureId = latestOutput.Capture.CaptureId;
            var target = latestOutput;
            if (quarantineInput)
            {
                var contextId = latestOutput.Artifact.SourceArtifactIds[3];
                target = (await store.ReadOutputByArtifactIdAsync(contextId, token).ConfigureAwait(false))!;
            }

            var path = Path.Combine(root, target.PayloadRelativePath);
            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            var intermittent = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            var failNextOpen = false;
            var faults = 0;
            intermittent.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid artifactId, CancellationToken cancellationToken) =>
                {
                    if (!failNextOpen || artifactId != target.ArtifactId)
                        return await artifacts.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);

                    failNextOpen = false;
                    using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    var result = await artifacts.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, result.Status);
                    Assert.IsNull(result.Content);
                    faults++;
                    return result;
                });

            using var service = new CameraAgentRetainedAstrometryService(store, intermittent.Object);
            ICameraAgentRetainedAstrometryService reader = service;
            var accepted = await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
            Assert.IsTrue(accepted.Completed);
            Assert.AreEqual(captureId, accepted.Summary?.CaptureId);

            failNextOpen = true;
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(false),
                await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false),
                "A one-shot physical open failure must not replace or clear the last verified fit.");
            Assert.AreEqual(1, faults);
            Assert.AreEqual(accepted, await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false));

            var reconciler = new DerivedProductReconciler(root, store);
            var displaced = Path.Combine(root, "displaced");
            File.Move(path, displaced);
            var missing = await reconciler.RunAsync(token).ConfigureAwait(false);
            Assert.AreEqual(1, missing.Missing);
            Assert.AreEqual("Missing", (await store.ReadOutputByArtifactIdAsync(target.ArtifactId, token).ConfigureAwait(false))?.AvailabilityState);
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(false),
                await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false));

            File.Move(displaced, path);
            await reconciler.RunAsync(token).ConfigureAwait(false);
            Assert.AreEqual("Available", (await store.ReadOutputByArtifactIdAsync(target.ArtifactId, token).ConfigureAwait(false))?.AvailabilityState);
            Assert.AreEqual(accepted, await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false));

            var corrupted = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            corrupted[0] ^= 1;
            await File.WriteAllBytesAsync(path, corrupted, token).ConfigureAwait(false);
            var reconciliation = await reconciler.RunAsync(token).ConfigureAwait(false);
            Assert.AreEqual(1, reconciliation.Quarantined);
            var quarantined = await store.ReadOutputByArtifactIdAsync(target.ArtifactId, token).ConfigureAwait(false);
            Assert.AreEqual("Quarantined", quarantined?.AvailabilityState);
            Assert.AreEqual("payload-checksum-mismatch", quarantined?.AvailabilityReason);
            Assert.IsFalse(File.Exists(path));

            var expectedCapture = olderOutput?.Capture.CaptureId;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var latest = await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
                Assert.IsTrue(latest.Completed, "A durably quarantined fit must not leave the newest-result search pending.");
                Assert.AreEqual(expectedCapture, latest.Summary?.CaptureId);
            }

            var selected = await reader.GetAsync(captureId, token).ConfigureAwait(false);
            Assert.AreEqual("Unavailable", selected.Availability);
            Assert.AreEqual("retained-evidence-invalid", selected.Reason);
            Assert.IsNull(selected.Solution);

            using var restartedStore = new SqliteCaptureProcessingStore(fixture.Fixture.Options);
            using var restartedArtifacts = new CameraAgentArtifactService(fixture.Fixture.Options, restartedStore, new CameraAgentPreviewEncoder());
            using var restartedReader = new CameraAgentRetainedAstrometryService(restartedStore, restartedArtifacts);
            var restarted = await restartedReader.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
            Assert.IsTrue(restarted.Completed);
            Assert.AreEqual(expectedCapture, restarted.Summary?.CaptureId);

            if (olderOutput is not null)
            {
                await restartedStore.PlanLifecycleOperationAsync(new(
                    Guid.NewGuid().ToString("N"), "delete", olderOutput.OutputIdentitySha256,
                    olderOutput.PayloadRelativePath, olderOutput.SidecarRelativePath,
                    "processing-quarantine/expired-astrometry", "retention-expired", 0), token).ConfigureAwait(false);
                await new DerivedProductReconciler(root, restartedStore).RunAsync(token).ConfigureAwait(false);
                Assert.IsNull(await restartedStore.ReadOutputByArtifactIdAsync(olderOutput.ArtifactId, token).ConfigureAwait(false));
                Assert.IsFalse(File.Exists(Path.Combine(root, olderOutput.PayloadRelativePath)));
                Assert.AreEqual(new CameraAgentLatestAstrometryRead(true),
                    await restartedReader.ReadLatestAcceptedAsync(token).ConfigureAwait(false));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
