using System.Globalization;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(0.0, "ARC")]
    [DataRow(0.02, "ZPN")]
    public async Task ScientificExportUsesOnlyItsVerifiedRetainedFisheyeSolveAndReleasesCapacity(double radialDistortion, string projection)
    {
        var root = CreateTestRoot();
        var token = CancellationToken.None;
        try
        {
            var optics = new ProjectionContext(ProjectionModel.EquidistantFisheye, 256, 256, 144, 144, 512, 512,
                ProjectionAperture.Circular, 232, 72, 243, 17, RadialDistortionK1: radialDistortion);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(0, projectionOverride: optics).ConfigureAwait(false);
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true, reference: reference).ConfigureAwait(false);
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
                Mock.Of<IDeploymentLocationStore>(), site, fixture.Fixture.Options);
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
                Assert.IsNotNull(solved.Solution);
                Assert.AreEqual(projection, solved.Mapping.ToCelestialWcs()?.Projection);

                var second = await exports.PrepareAsync(source.Capture.CaptureId, source.Artifact.ArtifactId,
                    ScientificExportFormat.Fits, token).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, second.Status, second.ReasonCode);
                Assert.IsNotNull(second.Download);
                await using (second.Download.ConfigureAwait(false))
                {
                    var acceptedHeader = await ReadHeaderAsync(second.Download.Content).ConfigureAwait(false);
                    Assert.Contains($"RA---{projection}", acceptedHeader);
                    Assert.Contains($"DEC--{projection}", acceptedHeader);
                    Assert.AreEqual(projection == "ZPN", acceptedHeader.Contains("PV2_3", StringComparison.Ordinal));
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

                var assessment = solved.Solution.Assessment;
                foreach (var frame in new[]
                {
                    assessment.Frame with { SourceArtifactId = Guid.NewGuid() },
                    assessment.Frame with { SourceDescriptorSha256 = new string('F', 64) }
                })
                {
                    var mismatched = solved with { Solution = solved.Solution with { Assessment = assessment with { Frame = frame } } };
                    var retained = new Mock<ICameraAgentRetainedAstrometryService>();
                    retained.Setup(service => service.GetAsync(source.Capture.CaptureId, token)).ReturnsAsync(mismatched);
                    using var mismatchExports = new CameraAgentScientificExports(artifacts, retained.Object,
                        Mock.Of<IDeploymentLocationStore>(), site, fixture.Fixture.Options);
                    var rejected = await mismatchExports.PrepareAsync(source.Capture.CaptureId, source.Artifact.ArtifactId,
                        ScientificExportFormat.Fits, token).ConfigureAwait(false);
                    Assert.IsNotNull(rejected.Download, rejected.ReasonCode);
                    await using (rejected.Download.ConfigureAwait(false))
                    {
                        var rejectedHeader = await ReadHeaderAsync(rejected.Download.Content).ConfigureAwait(false);
                        Assert.DoesNotContain("CTYPE1", rejectedHeader);
                        Assert.DoesNotContain("HVOSOLVE", rejectedHeader);
                    }
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

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ScientificExportsStageBothDownloadsOnDiskWithBoundedManagedAllocations()
    {
        if (!OperatingSystem.IsLinux()) return;

        const int sourceBytes = 32 * 1024 * 1024;
        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"scientific-export-{Guid.NewGuid():N}")).FullName;
        try
        {
            var inputPath = Path.Combine(root, "raw.bin");
            using (var input = File.Create(inputPath))
                input.SetLength(sourceBytes);
            var seed = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono8, 1, 1, 1, [0]).Descriptor;
            using var inputForHash = File.OpenRead(inputPath);
            var checksum = await PayloadChecksum.ComputeSha256Async(inputForHash).ConfigureAwait(false);
            var descriptor = seed with
            {
                Layout = seed.Layout with { Width = 8192, Height = 4096, StrideBytes = 8192, ByteLength = sourceBytes },
                Artifact = seed.Artifact with { ChecksumSha256 = checksum }
            };
            using var exports = CreateScientificExports(root, descriptor, () =>
                new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous));

            var before = GC.GetTotalAllocatedBytes(precise: true);
            var first = await exports.PrepareAsync(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                ScientificExportFormat.Xisf, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(first.Download, first.ReasonCode);
            var firstSpool = default((string Path, string? Target));
            await using (first.Download.ConfigureAwait(false))
            {
                var second = await exports.PrepareAsync(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                    ScientificExportFormat.Fits, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(second.Download, second.ReasonCode);
                var secondSpool = default((string Path, string? Target));
                await using (second.Download.ConfigureAwait(false))
                {
                    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                    Console.WriteLine($"Two {sourceBytes:N0}-byte exports allocated {allocated:N0} managed bytes.");
                    Assert.IsLessThan(8L * 1024 * 1024, allocated);
                    firstSpool = AssertPrivateDiskSpool(root, first.Download, sourceBytes);
                    secondSpool = AssertPrivateDiskSpool(root, second.Download, sourceBytes);
                    Assert.AreNotEqual(firstSpool.Path, secondSpool.Path);
                }
                Assert.AreNotEqual(secondSpool.Target, new FileInfo(secondSpool.Path).LinkTarget);
            }
            Assert.AreNotEqual(firstSpool.Target, new FileInfo(firstSpool.Path).LinkTarget);
            Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(root, ".scientific-exports")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScientificExportClosesItsUnlinkedSpoolOnReadFailureOrCancellation(bool cancel)
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = CreateTestRoot();
        try
        {
            var directory = Path.Combine(root, ".scientific-exports");
            var descriptor = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono8, 2, 2, 2, new byte[4]).Descriptor;
            using var cancellation = new CancellationTokenSource();
            var openedSpool = default((string Path, string? Target));
            var fault = true;
            using var exports = CreateScientificExports(root, descriptor, () => fault
                ? new ScientificExportFaultStream(() =>
                {
                    openedSpool = Directory.EnumerateFileSystemEntries("/proc/self/fd")
                        .Select(path => (Path: path, Target: new FileInfo(path).LinkTarget))
                        .Single(link => link.Target?.StartsWith(directory + "/", StringComparison.Ordinal) == true);
                    Assert.EndsWith(" (deleted)", openedSpool.Target);
                    if (cancel) cancellation.Cancel();
                })
                : new MemoryStream(new byte[4]));

            Task<CameraAgentScientificExportResult> PrepareAsync(CancellationToken token) =>
                exports.PrepareAsync(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, ScientificExportFormat.Fits, token).AsTask();

            if (cancel)
                await Assert.ThrowsAsync<OperationCanceledException>(() => PrepareAsync(cancellation.Token)).ConfigureAwait(false);
            else
            {
                var failed = await PrepareAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, failed.Status);
                Assert.AreEqual("scientific-export.io-unavailable", failed.ReasonCode);
                Assert.IsNull(failed.Download);
            }
            Assert.IsNotNull(openedSpool.Path);
            Assert.AreNotEqual(openedSpool.Target, new FileInfo(openedSpool.Path).LinkTarget);
            Assert.IsEmpty(Directory.GetFileSystemEntries(directory));

            fault = false;
            var first = await PrepareAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(first.Download, first.ReasonCode);
            await using (first.Download.ConfigureAwait(false))
            {
                var second = await PrepareAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(second.Download, second.ReasonCode);
                await second.Download.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CameraAgentScientificExports CreateScientificExports(string root, ReconstructionDescriptor descriptor, Func<Stream> open)
    {
        var artifact = descriptor.Artifact;
        var captureId = descriptor.Capture.CaptureId;
        var artifacts = new Mock<ICameraAgentArtifactService>();
        artifacts.Setup(service => service.OpenContentAsync(artifact.ArtifactId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Found,
                new CameraAgentArtifactContentStream(open(), artifact.ArtifactId, captureId, artifact.Role, artifact.MediaType,
                    descriptor.Layout.ByteLength, artifact.ChecksumSha256, "raw.bin", descriptor)));
        var astrometry = new Mock<ICameraAgentRetainedAstrometryService>();
        astrometry.Setup(service => service.GetAsync(captureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentRetainedAstrometry(captureId, "Unavailable", "no-solution"));
        var site = new Mock<ISiteProfileStore>();
        site.Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SiteProfileState("", SiteProfileValues.Empty, null, null, null));
        return new(artifacts.Object, astrometry.Object, Mock.Of<IDeploymentLocationStore>(), site.Object,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static (string Path, string? Target) AssertPrivateDiskSpool(string root, CameraAgentScientificDownload download, int sourceBytes)
    {
        var spool = (FileStream)download.Content;
        var directory = Path.Combine(root, ".scientific-exports");
        Assert.AreEqual(directory, Path.GetDirectoryName(spool.Name));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        var fd = spool.SafeFileHandle.DangerousGetHandle().ToString(CultureInfo.InvariantCulture);
        var path = "/proc/self/fd/" + fd;
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(spool.SafeFileHandle));
        var target = new FileInfo(path).LinkTarget;
        Assert.AreEqual(spool.Name + " (deleted)", target);
        Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
        Assert.IsGreaterThan(sourceBytes, spool.Length);
        var mountId = File.ReadLines("/proc/self/fdinfo/" + fd)
            .Single(line => line.StartsWith("mnt_id:", StringComparison.Ordinal)).Split(':')[1].Trim();
        var mount = File.ReadLines("/proc/self/mountinfo").Single(line => line.StartsWith(mountId + " ", StringComparison.Ordinal));
        var fileSystem = mount.Split(" - ", 2, StringSplitOptions.None)[1].Split(' ')[0];
        Assert.IsFalse(fileSystem is "tmpfs" or "ramfs", mount);
        return (path, target);
    }

    private sealed class ScientificExportFaultStream(Action fail) : MemoryStream(new byte[4])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            fail();
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("Injected source read failure after creating the spool.");
        }
    }
}
