using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
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
            using var store = SiloInfrastructureFixture.OpenProcessingStore(first.Fixture.Options);
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
            var measurement = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, measured, token).ConfigureAwait(false);
            var contextMember = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, context, token).ConfigureAwait(false);
            measurement.SetUnavailable(true);
            var temporary = await reader.ReadLatestAcceptedAsync(expectedNewest, token).ConfigureAwait(false);
            Assert.IsFalse(temporary.Completed);
            Assert.IsFalse(temporary.PreviousSummaryInvalidated);
            measurement.SetUnavailable(false);
            Assert.AreEqual(expectedNewest, (await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);

            measurement.SetUnavailable(true);
            contextMember.FlipByte();
            await contextMember.RecordCopyStateAsync(SiloAvailabilityState.Quarantined, token).ConfigureAwait(false);
            Assert.AreEqual(expectedOlder, (await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);

            contextMember.FlipByte();
            await contextMember.RecordCopyStateAsync(SiloAvailabilityState.Restored, token).ConfigureAwait(false);
            Assert.IsFalse((await reader.ReadLatestAcceptedAsync(expectedNewest, token).ConfigureAwait(false)).Completed);
            measurement.SetUnavailable(false);
            Assert.AreEqual(expectedNewest, (await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);

            measurement.SetUnavailable(true);
            await contextMember.ExpireAsync(token).ConfigureAwait(false);

            using var reopenedStore = SiloInfrastructureFixture.OpenProcessingStore(first.Fixture.Options);
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

            measurement.SetUnavailable(false);
            Assert.AreEqual(expectedOlder, (await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);
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
            using var store = SiloInfrastructureFixture.OpenProcessingStore(first.Fixture.Options);
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
            var raw = third.Fixture.Manifest.Descriptor;
            var source = change == "raw"
                ? await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, raw.Artifact.ArtifactId,
                    CaptureContractJson.ComputeDescriptorSha256(raw), token).ConfigureAwait(false)
                : await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, measured, token).ConfigureAwait(false);
            var contextMember = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, context, token).ConfigureAwait(false);
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
                contextMember.SetUnavailable(true);
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
            }
            else
            {
                var fallback = await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
                Assert.IsTrue(fallback.Completed);
                Assert.AreEqual(expectedOlder, fallback.Summary);
                source.FlipByte();
            }
            await source.RecordCopyStateAsync(SiloAvailabilityState.Restored, token).ConfigureAwait(false);
            Assert.AreEqual(expectedNewest, (await service.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary,
                "A legitimate recovery must make the exact newer fit readable again.");

            async Task ChangeLineageAsync()
            {
                if (change == "missing")
                {
                    await source.RecordCopyStateAsync(SiloAvailabilityState.Missing, token).ConfigureAwait(false);
                    return;
                }
                source.FlipByte();
                await source.RecordCopyStateAsync(SiloAvailabilityState.Quarantined, token).ConfigureAwait(false);
                if (change == "before")
                    contextMember.SetUnavailable(false);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
