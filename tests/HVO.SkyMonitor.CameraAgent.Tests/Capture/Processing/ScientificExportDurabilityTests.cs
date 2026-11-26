using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task ScientificExportUsesOnlyItsVerifiedRetainedSolveAndReleasesCapacity()
    {
        var root = CreateTestRoot();
        var token = CancellationToken.None;
        try
        {
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(fixture.Fixture.Options, store, storage, telemetry);
            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var astrometry = new CameraAgentRetainedAstrometryService(store, artifacts);
            using var site = new SettingsFileSiteProfileStore(fixture.Fixture.Options,
                NullLogger<SettingsFileSiteProfileStore>.Instance);
            using var exports = new CameraAgentScientificExports(artifacts, astrometry,
                Mock.Of<IDeploymentLocationStore>(), site);
            var source = fixture.Fixture.Manifest.Descriptor;

            var first = await exports.PrepareAsync(source.Capture.CaptureId, source.Artifact.ArtifactId,
                ScientificExportFormat.Fits, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, first.Status, first.ReasonCode);
            Assert.IsNotNull(first.Download);
            await using (first.Download.ConfigureAwait(false))
            {
                var header = await ReadHeaderAsync(first.Download.Content).ConfigureAwait(false);
                Assert.DoesNotContain("CTYPE1", header);
                Assert.DoesNotContain("HVOSOLVE", header);

                await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
                var solved = await astrometry.GetAsync(source.Capture.CaptureId, token).ConfigureAwait(false);
                Assert.IsNotNull(solved.Mapping, solved.Reason);
                var wcs = solved.Mapping.ToCelestialWcs();
                Assert.IsNotNull(wcs);

                var second = await exports.PrepareAsync(source.Capture.CaptureId, source.Artifact.ArtifactId,
                    ScientificExportFormat.Fits, token).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, second.Status, second.ReasonCode);
                Assert.IsNotNull(second.Download);
                await using (second.Download.ConfigureAwait(false))
                {
                    var acceptedHeader = await ReadHeaderAsync(second.Download.Content).ConfigureAwait(false);
                    Assert.Contains($"RA---{wcs.Projection}", acceptedHeader);
                    Assert.Contains(solved.SolutionIdentitySha256!, acceptedHeader);
                    Assert.AreEqual(source.Artifact.ArtifactId, second.Download.Receipt.Source.ArtifactId);
                    Assert.AreEqual(source.Artifact.ChecksumSha256, second.Download.Receipt.Source.Sha256);
                    var busy = await exports.PrepareAsync(source.Capture.CaptureId, source.Artifact.ArtifactId,
                        ScientificExportFormat.Xisf, token).ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, busy.Status);
                    Assert.AreEqual("scientific-export.capacity", busy.ReasonCode);
                    Assert.IsNull(busy.Download);
                }
                var recovered = await exports.PrepareAsync(source.Capture.CaptureId, source.Artifact.ArtifactId,
                    ScientificExportFormat.Xisf, token).ConfigureAwait(false);
                Assert.IsNotNull(recovered.Download, recovered.ReasonCode);
                await using (recovered.Download.ConfigureAwait(false))
                {
                    Assert.AreEqual(".xisf", recovered.Download.Receipt.Encoding.Extension);
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }

        static async Task<string> ReadHeaderAsync(Stream content)
        {
            var header = new byte[5760];
            await content.ReadExactlyAsync(header).ConfigureAwait(false);
            var text = Encoding.ASCII.GetString(header);
            var end = text.IndexOf("END     ", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, end);
            return text[..end];
        }
    }
}
