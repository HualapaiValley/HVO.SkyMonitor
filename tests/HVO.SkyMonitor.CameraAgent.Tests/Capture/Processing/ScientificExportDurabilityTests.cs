using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.CameraAgent.Tests.Orbital;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    private const long SmallScientificSpoolReservation = 128 * 1024;

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
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(fixture.Fixture.Options, store, telemetry);
            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var astrometry = new CameraAgentRetainedAstrometryService(store, artifacts);
            using var site = new SettingsFileSiteProfileStore(fixture.Fixture.Options,
                NullLogger<SettingsFileSiteProfileStore>.Instance);
            using var exports = new CameraAgentScientificExports(artifacts, astrometry,
                Mock.Of<IDeploymentLocationStore>(), site, fixture.Fixture.Options, new FileSystemStorageCapacityProvider(),
                new RawIngressState(TimeProvider.System), NullLogger<CameraAgentScientificExports>.Instance);
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
                        Mock.Of<IDeploymentLocationStore>(), site, fixture.Fixture.Options, new FileSystemStorageCapacityProvider(),
                        new RawIngressState(TimeProvider.System), NullLogger<CameraAgentScientificExports>.Instance);
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

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false, "PermissionDenied")]
    [DataRow(true, "Containment")]
    public async Task ScientificExportRejectsInsecureStagingDirectoryBeforeOpeningSpool(bool symlink, string cause)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("This case verifies Linux staging-directory protection.");
            return;
        }

        var root = CreateTestRoot();
        try
        {
            var staging = Path.Combine(root, ".scientific-exports");
            var target = Path.Combine(root, "link-target");
            Directory.CreateDirectory(symlink ? target : staging);
            if (symlink)
                Directory.CreateSymbolicLink(staging, target);
            else
                File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var logger = new OrbitalTestLogger<CameraAgentScientificExports>();
            var descriptor = ScientificExportTestDescriptor();
            using var exports = CreateScientificExports(root, descriptor,
                () => new ScientificExportFaultStream(() => Assert.Fail("Source read would require an opened spool.")), logger: logger);
            var result = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, result.Status);
            Assert.AreEqual("scientific-export.io-unavailable", result.ReasonCode);
            Assert.IsNull(result.Download);
            Assert.IsEmpty(Directory.GetFileSystemEntries(symlink ? target : staging));
            var entry = logger.Entries.Single();
            Assert.AreEqual(14722, entry.EventId);
            Assert.Contains("spool-open", entry.Message);
            Assert.Contains(cause, entry.Message);
            Assert.DoesNotContain(root, entry.Message);
            Assert.IsNull(entry.Exception);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ScientificExportFitsIngressHeadroomAfterRealSpoolAllocation()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        const int width = 1936;
        const int height = 1096;
        const int stride = width * 2;
        const long reserve = 128L * 1024 * 1024;
        var root = CreateTestRoot();
        try
        {
            var payload = new byte[stride * height];
            RandomNumberGenerator.Fill(payload);
            var descriptor = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, width, height, stride, payload).Descriptor;
            var ingressRequired = checked(reserve + descriptor.Layout.ByteLength);
            var available = ingressRequired + 4_390_912; // 65 data units and two metadata units.
            var capacity = new Mock<IStorageCapacityProvider>(MockBehavior.Strict);
            capacity.Setup(provider => provider.GetCapacity(root)).Returns(() => new StorageCapacity(long.MaxValue, available));
            using var exports = CreateScientificExports(root, descriptor, () => new MemoryStream(payload), capacity.Object, ingressReserve: reserve);

            var accepted = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, accepted.Status, accepted.ReasonCode);
            Assert.IsNotNull(accepted.Download);
            long allocated;
            await using (accepted.Download.ConfigureAwait(false))
            {
                Assert.AreEqual(4_248_000L, accepted.Download.Receipt.ByteLength);
                allocated = ReadScientificSpoolAllocation((FileStream)accepted.Download.Content);
                Assert.IsGreaterThan(accepted.Download.Receipt.ByteLength + 256, allocated);
                Assert.IsGreaterThanOrEqualTo(ingressRequired, available - allocated);
                Console.WriteLine($"FITS length {accepted.Download.Receipt.ByteLength}, allocated {allocated}, ingress headroom {available - allocated - reserve}, ingress payload {descriptor.Layout.ByteLength} bytes.");
            }

            // Keep the boundary independent of background writes to the shared test volume.
            available = ingressRequired + allocated - 256;
            var rejected = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
            if (rejected.Download is { } unexpected)
                await unexpected.DisposeAsync().ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, rejected.Status);
            Assert.AreEqual("scientific-export.capacity", rejected.ReasonCode);
            Assert.IsNull(rejected.Download);
            Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(root, ".scientific-exports")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static long ReadScientificSpoolAllocation(FileStream spool)
    {
        // Linux unlinks the spool at open. stat follows the original process's live file descriptor.
        var path = OperatingSystem.IsLinux()
            ? $"/proc/{Environment.ProcessId}/fd/{spool.SafeFileHandle.DangerousGetHandle()}"
            : spool.Name;
        var arguments = OperatingSystem.IsLinux() ? new[] { "-L", "-c", "%b", path } : ["-f", "%b", path];
        using var process = Process.Start(new ProcessStartInfo("stat", arguments) { RedirectStandardOutput = true })
            ?? throw new InvalidOperationException("Could not start stat for the staged export.");
        var blocks = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
        return checked(long.Parse(blocks, CultureInfo.InvariantCulture) * 512);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(ScientificExportFormat.Fits, 64L * 1024 * 1024, 0L)]
    [DataRow(ScientificExportFormat.FitsTileGzip, 0L, 2L)]
    [DataRow(ScientificExportFormat.Xisf, 128L * 1024 * 1024, 16L * 1024 * 1024)]
    public async Task ScientificExportRequiresStagingBoundAndNextCaptureHeadroom(
        ScientificExportFormat format, long configuredReserve, long recentCaptureBytes)
    {
        var root = CreateTestRoot();
        try
        {
            var descriptor = ScientificExportTestDescriptor();
            var estimate = ScientificCaptureExport.MaximumOutputBytes(descriptor, format);
            var ingress = new RawIngressState(TimeProvider.System);
            ingress.RecordCapturePayloadBytes(recentCaptureBytes);
            var headroom = Math.Max(descriptor.Layout.ByteLength, recentCaptureBytes);
            var ingressRequired = Math.Max(64L * 1024 * 1024, configuredReserve) + headroom;
            var stagingRequired = ingressRequired + SmallScientificSpoolReservation;
            var capacity = new Mock<IStorageCapacityProvider>(MockBehavior.Strict);
            capacity.SetupSequence(provider => provider.GetCapacity(root))
                .Returns(new StorageCapacity(long.MaxValue, stagingRequired - 1))
                .Returns(new StorageCapacity(long.MaxValue, stagingRequired))
                .Returns(new StorageCapacity(long.MaxValue, ingressRequired - 1))
                .Returns(new StorageCapacity(long.MaxValue, stagingRequired))
                .Returns(new StorageCapacity(long.MaxValue, ingressRequired));
            using var exports = CreateScientificExports(root, descriptor, () => new MemoryStream(new byte[4]),
                capacityProvider: capacity.Object, ingressReserve: configuredReserve, ingress: ingress);
            var rejected = await PrepareScientificExportAsync(exports, descriptor, format).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, rejected.Status);
            Assert.AreEqual("scientific-export.capacity", rejected.ReasonCode);
            Assert.IsNull(rejected.Download);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, ".scientific-exports")));

            var overallocated = await PrepareScientificExportAsync(exports, descriptor, format).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, overallocated.Status);
            Assert.AreEqual("scientific-export.capacity", overallocated.ReasonCode);
            Assert.IsNull(overallocated.Download);
            Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(root, ".scientific-exports")));

            var accepted = await PrepareScientificExportAsync(exports, descriptor, format).ConfigureAwait(false);
            Assert.IsNotNull(accepted.Download, accepted.ReasonCode);
            await using var download = accepted.Download.ConfigureAwait(false);
            Assert.IsLessThanOrEqualTo(estimate, accepted.Download.Receipt.ByteLength);
            capacity.Verify(provider => provider.GetCapacity(root), Times.Exactly(5));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ScientificExportLogsFailedCapacityProbeWithoutPathAndDoesNotStage()
    {
        var root = CreateTestRoot();
        try
        {
            var descriptor = ScientificExportTestDescriptor();
            var capacity = new Mock<IStorageCapacityProvider>();
            capacity.Setup(provider => provider.GetCapacity(root)).Throws(new IOException($"Failed to inspect {root}"));
            var logger = new OrbitalTestLogger<CameraAgentScientificExports>();
            using var exports = CreateScientificExports(root, descriptor, () => new MemoryStream(new byte[4]),
                capacityProvider: capacity.Object, logger: logger);
            var rejected = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, rejected.Status);
            Assert.AreEqual("scientific-export.capacity", rejected.ReasonCode);
            Assert.IsNull(rejected.Download);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, ".scientific-exports")));
            var entry = logger.Entries.Single();
            Assert.AreEqual(14722, entry.EventId);
            Assert.Contains("scientific-export.capacity", entry.Message);
            Assert.Contains("capacity-probe", entry.Message);
            Assert.Contains("IOException", entry.Message);
            Assert.DoesNotContain(root, entry.Message);
            Assert.IsNull(entry.Exception);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ScientificExportCountsPendingWritesAgainstDiskHeadroom()
    {
        var root = CreateTestRoot();
        try
        {
            var descriptor = ScientificExportTestDescriptor();
            var estimate = ScientificCaptureExport.MaximumOutputBytes(descriptor, ScientificExportFormat.Fits);
            var available = 64L * 1024 * 1024 + descriptor.Layout.ByteLength + SmallScientificSpoolReservation;
            var capacity = new Mock<IStorageCapacityProvider>();
            capacity.Setup(provider => provider.GetCapacity(root))
                .Returns(() => new StorageCapacity(long.MaxValue, available));
            var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opens = 0;
            using var exports = CreateScientificExports(root, descriptor, () => Interlocked.Increment(ref opens) == 1
                ? new PausedScientificExportStream(writing, resume.Task)
                : new MemoryStream(new byte[4]), capacityProvider: capacity.Object);
            var pending = PrepareScientificExportAsync(exports, descriptor).AsTask();
            try
            {
                await writing.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                // A second rounded reservation would fit here if the first were only charged its encoded length.
                available += estimate;
                var rejected = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
                Assert.IsNull(rejected.Download);
                Assert.AreEqual("scientific-export.capacity", rejected.ReasonCode);
            }
            finally
            {
                resume.SetResult();
                var first = await pending.ConfigureAwait(false);
                Assert.IsNotNull(first.Download, first.ReasonCode);
                await first.Download.DisposeAsync().ConfigureAwait(false);
            }
            var recovered = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
            Assert.IsNotNull(recovered.Download, recovered.ReasonCode);
            await recovered.Download.DisposeAsync().ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScientificExportReleasesReservationAfterEncodeFailure(bool cancel)
    {
        var root = CreateTestRoot();
        try
        {
            var descriptor = ScientificExportTestDescriptor();
            var capacity = new Mock<IStorageCapacityProvider>(MockBehavior.Strict);
            capacity.Setup(provider => provider.GetCapacity(root))
                .Returns(new StorageCapacity(long.MaxValue, 64L * 1024 * 1024 + descriptor.Layout.ByteLength + SmallScientificSpoolReservation));
            using var cancellation = new CancellationTokenSource();
            var opens = 0;
            using var exports = CreateScientificExports(root, descriptor, () => ++opens == 1
                ? new ScientificExportFaultStream(() =>
                {
                    Assert.IsTrue(Directory.Exists(Path.Combine(root, ".scientific-exports")));
                    if (cancel) cancellation.Cancel();
                })
                : new MemoryStream(new byte[4]), capacityProvider: capacity.Object);
            Task<CameraAgentScientificExportResult> PrepareAsync(CancellationToken token) => exports.PrepareAsync(
                descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, ScientificExportFormat.Fits, token).AsTask();

            if (cancel)
                await Assert.ThrowsAsync<OperationCanceledException>(() => PrepareAsync(cancellation.Token)).ConfigureAwait(false);
            else
            {
                var failed = await PrepareAsync(cancellation.Token).ConfigureAwait(false);
                Assert.AreEqual("scientific-export.io-unavailable", failed.ReasonCode);
                Assert.IsNull(failed.Download);
            }

            // Free space covers exactly one reservation. A leaked reservation would reject the retry.
            var recovered = await PrepareAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(recovered.Download, recovered.ReasonCode);
            await using var download = recovered.Download.ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, recovered.Status);
            capacity.Verify(provider => provider.GetCapacity(root), Times.Exactly(3));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("IOException")]
    [DataRow("UnauthorizedAccessException")]
    [DataRow("SqliteException")]
    public async Task ScientificExportLogsAstrometryLookupFailureWithoutStaging(string cause)
    {
        var root = CreateTestRoot();
        try
        {
            var descriptor = ScientificExportTestDescriptor();
            var capacity = new Mock<IStorageCapacityProvider>(MockBehavior.Strict);
            var logger = new OrbitalTestLogger<CameraAgentScientificExports>();
            var message = $"Injected failure at {root}.";
            Exception? failure = cause switch
            {
                "IOException" => new IOException(message),
                "UnauthorizedAccessException" => new UnauthorizedAccessException(message),
                _ => new Microsoft.Data.Sqlite.SqliteException(message, 5)
            };
            using var exports = CreateScientificExports(root, descriptor, () => new MemoryStream(new byte[4]),
                capacityProvider: capacity.Object, logger: logger,
                retained: () => failure is { } exception ? throw exception : new(descriptor.Capture.CaptureId, "NotRetained", "assessment-not-retained"));
            var rejected = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, rejected.Status);
            Assert.AreEqual("scientific-export.astrometry-unavailable", rejected.ReasonCode);
            Assert.IsNull(rejected.Download);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, ".scientific-exports")));
            capacity.VerifyNoOtherCalls();
            var entry = logger.Entries.Single();
            Assert.Contains("astrometry-read", entry.Message);
            Assert.Contains(cause, entry.Message);
            Assert.DoesNotContain(root, entry.Message);
            Assert.IsNull(entry.Exception);

            failure = null;
            capacity.Setup(provider => provider.GetCapacity(root)).Returns(new StorageCapacity(long.MaxValue, long.MaxValue));
            var recovered = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
            Assert.IsNotNull(recovered.Download, recovered.ReasonCode);
            await recovered.Download.DisposeAsync().ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("read-capacity", false, CameraAgentArtifactReadStatus.Unavailable, "scientific-export.capacity")]
    [DataRow(CameraAgentRetainedAstrometryService.TemporaryEvidenceUnavailable, false,
        CameraAgentArtifactReadStatus.Unavailable, "scientific-export.astrometry-unavailable")]
    [DataRow(CameraAgentRetainedAstrometryService.TemporaryEvidenceUnavailable, true,
        CameraAgentArtifactReadStatus.Conflict, "scientific-export.astrometry-missing")]
    public async Task ScientificExportDistinguishesRetainedAstrometryFailures(
        string retainedReason, bool missing, CameraAgentArtifactReadStatus status, string exportReason)
    {
        var root = CreateTestRoot();
        try
        {
            var descriptor = ScientificExportTestDescriptor();
            var reason = retainedReason;
            using var exports = CreateScientificExports(root, descriptor, () => new MemoryStream(new byte[4]),
                retained: () => new(descriptor.Capture.CaptureId, "Unavailable", reason) { HasMissingEvidence = missing });
            var rejected = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
            Assert.AreEqual(status, rejected.Status);
            Assert.AreEqual(exportReason, rejected.ReasonCode);
            Assert.IsNull(rejected.Download);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, ".scientific-exports")));

            reason = "assessment-not-retained";
            missing = false;
            var recovered = await PrepareScientificExportAsync(exports, descriptor).ConfigureAwait(false);
            Assert.IsNotNull(recovered.Download, recovered.ReasonCode);
            await using var download = recovered.Download.ConfigureAwait(false);
            var header = new byte[2880];
            await recovered.Download.Content.ReadExactlyAsync(header).ConfigureAwait(false);
            Assert.DoesNotContain("CTYPE1", Encoding.ASCII.GetString(header));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ScientificExportReuseChecksSourceAndCurrentMetadataWithoutStagingAgain()
    {
        var root = CreateTestRoot();
        try
        {
            var descriptor = ScientificExportTestDescriptor();
            var captured = DeploymentLocationSnapshot.Create("captured", 1, "test", null,
                descriptor.Timing.ExposureStartedUtc, null, 35.5, -113.25, 1400, "UTC");
            var current = DeploymentLocationSnapshot.Create("current", 2, "test", null,
                descriptor.Timing.ExposureEndedUtc, null, 41, -120, 900, "UTC");
            descriptor = descriptor with { Location = captured.ToProvenance() };
            var deployment = new Mock<IDeploymentLocationStore>();
            deployment.SetupGet(store => store.Active).Returns(current);
            deployment.Setup(store => store.Resolve(captured.ToProvenance(), descriptor.Timing.ExposureStartedUtc))
                .Returns(captured);
            var site = new SiteProfileValues("Current observatory", null, "Current owner", "Private contact");
            var capacity = new Mock<IStorageCapacityProvider>();
            capacity.Setup(provider => provider.GetCapacity(root)).Returns(new StorageCapacity(long.MaxValue, long.MaxValue));
            var payload = new byte[4];
            using var exports = CreateScientificExports(root, descriptor, () => new MemoryStream(payload),
                capacityProvider: capacity.Object, siteProfile: () => site, deployment: deployment.Object);
            var first = await PrepareScientificExportAsync(exports, descriptor, ScientificExportFormat.FitsTileGzip).ConfigureAwait(false);
            Assert.IsNotNull(first.Download, first.ReasonCode);
            await using var firstLease = first.Download.ConfigureAwait(false);
            var firstBytes = new byte[first.Download.Content.Length];
            await first.Download.Content.ReadExactlyAsync(firstBytes).ConfigureAwait(false);
            var header = Encoding.ASCII.GetString(firstBytes);
            Assert.Contains("'Current observatory'", header);
            Assert.Contains("'Current owner'", header);
            Assert.Contains("35.5", header);
            Assert.Contains("-113.25", header);
            Assert.DoesNotContain("Private contact", header);

            var reuse = await PrepareScientificExportAsync(exports, descriptor, ScientificExportFormat.FitsTileGzip, reuseRecent: true)
                .ConfigureAwait(false);
            Assert.IsNotNull(reuse.Download, reuse.ReasonCode);
            await using (reuse.Download.ConfigureAwait(false))
            {
                Assert.AreEqual(first.Download.Receipt, reuse.Download.Receipt);
                var reusedBytes = new byte[reuse.Download.Content.Length];
                await reuse.Download.Content.ReadExactlyAsync(reusedBytes).ConfigureAwait(false);
                CollectionAssert.AreEqual(firstBytes, reusedBytes);
            }
            capacity.Verify(provider => provider.GetCapacity(root), Times.Exactly(2));

            payload[0] = 1;
            var corrupted = await PrepareScientificExportAsync(exports, descriptor, ScientificExportFormat.FitsTileGzip, reuseRecent: true)
                .ConfigureAwait(false);
            Assert.AreEqual(ScientificCaptureExport.CorruptSource, corrupted.ReasonCode);
            Assert.IsNull(corrupted.Download);
            capacity.Verify(provider => provider.GetCapacity(root), Times.Exactly(2));

            payload[0] = 0;
            site = site with { ObservatoryName = "Renamed observatory" };
            var updated = await PrepareScientificExportAsync(exports, descriptor, ScientificExportFormat.FitsTileGzip, reuseRecent: true)
                .ConfigureAwait(false);
            Assert.IsNotNull(updated.Download, updated.ReasonCode);
            await using var updatedLease = updated.Download.ConfigureAwait(false);
            Assert.AreNotEqual(first.Download.Receipt.Sha256, updated.Download.Receipt.Sha256);
            capacity.Verify(provider => provider.GetCapacity(root), Times.Exactly(4));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ReconstructionDescriptor ScientificExportTestDescriptor() =>
        ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono8, 2, 2, 2, new byte[4]).Descriptor;

    private static ValueTask<CameraAgentScientificExportResult> PrepareScientificExportAsync(CameraAgentScientificExports exports,
        ReconstructionDescriptor descriptor, ScientificExportFormat format = ScientificExportFormat.Fits, bool reuseRecent = false)
        => exports.PrepareAsync(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, format, CancellationToken.None, reuseRecent);

    private static CameraAgentScientificExports CreateScientificExports(string root, ReconstructionDescriptor descriptor, Func<Stream> open,
        IStorageCapacityProvider? capacityProvider = null, ILogger<CameraAgentScientificExports>? logger = null,
        long ingressReserve = 64L * 1024 * 1024, Func<CameraAgentRetainedAstrometry>? retained = null,
        Func<SiteProfileValues>? siteProfile = null, IDeploymentLocationStore? deployment = null, RawIngressState? ingress = null)
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
            .ReturnsAsync(() => retained?.Invoke() ?? new CameraAgentRetainedAstrometry(captureId, "NotRetained", "assessment-not-retained"));
        var site = new Mock<ISiteProfileStore>();
        site.Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SiteProfileState("", siteProfile?.Invoke() ?? SiteProfileValues.Empty, null, null, null));
        return new(artifacts.Object, astrometry.Object, deployment ?? Mock.Of<IDeploymentLocationStore>(), site.Object,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = ingressReserve }),
            capacityProvider ?? new FileSystemStorageCapacityProvider(), ingress ?? new RawIngressState(TimeProvider.System),
            logger ?? NullLogger<CameraAgentScientificExports>.Instance);
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

    private sealed class PausedScientificExportStream(TaskCompletionSource writing, Task resume) : MemoryStream(new byte[4])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            writing.TrySetResult();
            await resume.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
