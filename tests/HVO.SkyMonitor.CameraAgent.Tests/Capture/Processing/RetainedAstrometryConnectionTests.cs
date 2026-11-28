using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [OSCondition(OperatingSystems.Linux)]
    public async Task RetainedAstrometry_LatestScanOpensOneJournalConnectionAndMatchesUnpooledHeadline()
    {
        var root = CreateTestRoot();
        CameraAgentArtifactService.RetainedHistoryRead? captured = null;
        try
        {
            var oldest = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(oldest.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(oldest.Fixture.Options, store, telemetry);
            var accepted = await ProduceLatestReadFixtureAsync(oldest, store, persistence, telemetry, gate).ConfigureAwait(false);
            for (var index = 1; index <= 2; index++)
            {
                var reference = await MeasuredAstrometryFixture.CreateAtAsync(index * 20).ConfigureAwait(false);
                var rejected = await CreateAstrometryDurabilityFixtureAsync(root, "rejected", hostCompatible: true,
                    captureSequence: 1 + index * 60, reference: reference, prior: (1 + (index - 1) * 60, index))
                    .ConfigureAwait(false);
                _ = await ProduceLatestReadFixtureAsync(rejected, store, persistence, telemetry, gate).ConfigureAwait(false);
            }

            using var unpooledArtifacts = SiloInfrastructureFixture.OpenArtifactReader(oldest.Fixture.Options, store);
            using var unpooled = CreateUnpooledAstrometryReader(store, unpooledArtifacts);
            var expected = await unpooled.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(expected.Completed);
            Assert.IsNotNull(expected.Summary);
            Assert.AreEqual(accepted.Capture.CaptureId, expected.Summary.CaptureId);
            Assert.AreEqual(accepted.ArtifactId, expected.Summary.ArtifactId);
            Assert.AreEqual(accepted.ContentIdentitySha256, expected.Summary.SolutionIdentitySha256);
            var timing = oldest.Fixture.Manifest.Descriptor.Timing;
            Assert.AreEqual(timing.ExposureStartedUtc + (timing.ExposureEndedUtc - timing.ExposureStartedUtc) / 2,
                expected.Summary.MidpointUtc);
            Assert.AreEqual(7L, unpooledArtifacts.JournalConnectionOpens, "Two rejections and five accepted-fit inputs.");

            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(oldest.Fixture.Options, store);
            using var pooled = new CameraAgentRetainedAstrometryService(store, artifacts,
                () => captured = artifacts.BeginRetainedHistoryRead());
            Assert.AreEqual(expected, await pooled.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(7L, artifacts.PayloadValidationReads);
            Assert.AreEqual(1L, artifacts.JournalConnectionOpens);
            Assert.IsNotNull(captured);
            Assert.ThrowsExactly<ObjectDisposedException>(() => captured.Take());

            captured = null;
            Assert.AreEqual(expected, await pooled.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(12L, artifacts.PayloadValidationReads);
            Assert.AreEqual(2L, artifacts.JournalConnectionOpens, "The next request owns its own handle.");
            Assert.IsNotNull(captured);
            Assert.ThrowsExactly<ObjectDisposedException>(() => captured.Take());
        }
        finally
        {
            captured?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static CameraAgentRetainedAstrometryService CreateUnpooledAstrometryReader(
        SqliteCaptureProcessingStore store,
        ICameraAgentArtifactService artifacts,
        int maximumReadBytes = CameraAgentRetainedAstrometryService.MaximumReadBytes)
        => new(store, artifacts, () => new UnpooledHistoryRead(artifacts), maximumReadBytes);

    private sealed class UnpooledHistoryRead(ICameraAgentArtifactService artifacts) : IRetainedHistoryArtifactRead
    {
        public ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
            => artifacts.OpenContentAsync(artifactId, cancellationToken);

        public void Dispose()
        {
        }
    }
}
