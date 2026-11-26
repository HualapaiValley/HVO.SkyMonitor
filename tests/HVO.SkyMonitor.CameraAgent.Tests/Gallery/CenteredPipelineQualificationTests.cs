using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.CameraAgent.Tests.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

/// <summary>
/// Deterministic end-to-end qualification of the #1918 centered live pipeline on Mono16 and Bayer RGGB16
/// (both full-frame (0,0) and odd-origin (1,1) BGGR ROI crops) for issue #1928.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[TestProperty(CameraAgentTestAreas.PropertyName, CameraAgentTestAreas.Gallery)]
public sealed class CenteredPipelineQualificationTests
{
    private const int Width = 160;
    private const int Height = 128;
    private const int BlackLevel = 64;
    private const int WhiteLevel = 4095;
    private const double BackgroundAdu = 110;
    private const double SignalAdu = 3200;
    private const double NeutralAdu = 1800;

    // Pre-evaluation numerical tolerances recorded before scoring (#1928 acceptance bullet 2 & 4).
    private const double MonoCentroidTolerancePixels = 0.30;
    private const double BayerCentroidTolerancePixels = 0.65;
    private const double OverlayCoordinateTolerancePixels = 0.75;
    private const double LinearColorDominanceRatio = 3.0;
    private const double LinearNeutralSpreadFraction = 0.05;
    private const double JpegColorDominanceRatio = 2.0;
    private const int JpegDominantMargin = 35;
    private const int JpegNeutralMaxChannelSpread = 14;
    private const int JpegMonoMaxChannelSpread = 2;
    private const double MonoIntensityRelativeTolerance = 0.04;
    private const double HfdMinimumPixels = 0.5;
    private const double HfdMaximumPixels = 8.0;

    private static readonly DateTimeOffset BaseUtc = new(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExposureDuration = TimeSpan.FromMilliseconds(250);

    // 18 non-collinear stellar positions spanning all quadrants, well away from edges and color/clipping patches.
    // Distinct amplitudes prevent rank-order flips between fit and disjoint holdout splits.
    private static readonly (double X, double Y, double Peak)[] FieldStars =
    [
        (24.3, 48.4, 2400), (42.7, 46.2, 2300), (62.4, 49.8, 2200), (88.6, 44.5, 2100),
        (114.2, 48.3, 2000), (136.5, 52.6, 1900), (26.8, 70.3, 1800), (50.5, 68.7, 1700),
        (74.2, 65.4, 1600), (98.4, 69.2, 1500), (124.7, 67.5, 1400), (140.2, 76.4, 1300),
        (28.4, 92.6, 1200), (52.3, 88.4, 1150), (76.8, 86.2, 1100), (118.6, 88.9, 1050),
        (40.2, 108.5, 1000), (132.4, 106.2, 950)
    ];

    // Scrambled field for the poor-fit registration rejection control.
    private static readonly (double X, double Y, double Peak)[] ScrambledStars =
    [
        (20.5, 34.2, 2350), (35.1, 61.8, 2250), (57.9, 39.4, 2150), (71.3, 98.7, 2050),
        (93.8, 53.1, 1950), (111.4, 38.6, 1850), (131.7, 64.2, 1750), (144.2, 41.9, 1650),
        (29.6, 81.5, 1550), (46.8, 102.3, 1450), (64.2, 74.9, 1350), (84.5, 81.2, 1250),
        (106.3, 94.6, 1150), (127.9, 103.4, 1050), (139.5, 91.2, 980), (43.4, 52.7, 920)
    ];

    // Independent color / intensity target centers in reference (exposure 103) coordinates (11x11 patches).
    private static readonly PixelPoint RedTarget = new(32, 22);
    private static readonly PixelPoint GreenTarget = new(80, 22);
    private static readonly PixelPoint BlueTarget = new(128, 22);
    private static readonly PixelPoint NeutralTarget = new(78, 108);
    private static readonly PixelPoint ClippedPatchCenter = new(100, 104);

    // Known subpixel displacements (exposure -> reference 103), crossing mixed CFA sites.
    private static readonly (double Dx, double Dy)[] SequenceShifts =
    [
        (-2.50, +1.50), // 101
        (-1.25, +0.75), // 102
        (0.00, 0.00),   // 103 (reference 1)
        (+1.25, -0.75), // 104 (reference 2)
        (+2.50, -1.50), // 105 (carries clipped patch + moving trail)
        (+3.75, -2.25)  // 106
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ArtifactPreviewHonorsDeliveredOddOriginCfaPhaseAndIsolatesCacheByPhaseAsync()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-1928-artifact-phase");
        var token = CancellationToken.None;
        try
        {
            var clock = new QualificationClock { UtcNow = BaseUtc };
            using var provider = CreateProvider(root, clock);
            var options = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>();
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 15);
            await journal.InitializeAsync(token).ConfigureAwait(false);
            var processingStore = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            await processingStore.InitializeAsync(token).ConfigureAwait(false);
            var artifactService = provider.GetRequiredService<ICameraAgentArtifactService>();

            // 1. Verify all four delivered Bayer CFA phases (RGGB, GRBG, GBRG, BGGR) preserve RGB targets
            // through both default gallery preview encoding and explicit display-stretch encoding.
            var encoder = new CameraAgentPreviewEncoder();
            (int X, int Y, BayerPattern Pattern)[] allPhases =
            [
                (0, 0, BayerPattern.Rggb),
                (1, 0, BayerPattern.Grbg),
                (0, 1, BayerPattern.Gbrg),
                (1, 1, BayerPattern.Bggr)
            ];
            for (var i = 0; i < allPhases.Length; i++)
            {
                var (ox, oy, pattern) = allPhases[i];
                var phaseLayout = CreateLayout(CameraPixelFormat.BayerRggb16, cfaOriginX: ox, cfaOriginY: oy);
                var phaseBytes = RenderSyntheticFrame(
                    CameraPixelFormat.BayerRggb16, ox, oy, 0, 0, FieldStars, includeClippedPatch: false);
                var phaseRaw = await CommitRawArtifactAsync(
                    root, journal, phaseLayout, phaseBytes, BaseUtc.AddSeconds(i * 2), token).ConfigureAwait(false);

                var preview = await artifactService.GetPreviewAsync(phaseRaw.Artifact.ArtifactId, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, preview.Status);
                var decoded = JpegImageCodec.DecodeJpeg(preview.Content, token);
                Assert.AreEqual(Width, decoded.Width);
                Assert.AreEqual(Height, decoded.Height);
                AssertColorPreviewTargets(decoded, $"gallery-artifact-{pattern}-default");

                var stretched = encoder.Encode(
                    phaseLayout, phaseBytes, Width, 2 * 1024 * 1024, token,
                    new Mono16DisplayStretchOptions(0.01, 0.999, 0.05));
                var decodedStretched = JpegImageCodec.DecodeJpeg(stretched.Content, token);
                AssertColorPreviewTargets(decodedStretched, $"gallery-artifact-{pattern}-stretched");
            }

            // 2. Cache isolation: commit IDENTICAL raw bytes (same SHA-256) under all 4 CFA origins;
            // none may collide in the preview cache, and interpreting BGGR bytes as RGGB must invert R/B.
            var bggrBytes = RenderSyntheticFrame(
                CameraPixelFormat.BayerRggb16, 1, 1, 0, 0, FieldStars, includeClippedPatch: false);
            var samePayloadSha = PayloadChecksum.ComputeSha256(bggrBytes);
            var cacheChecksums = new Dictionary<BayerPattern, string>();
            CameraAgentArtifactPreviewResult? rggbFromBggr = null;
            for (var i = 0; i < allPhases.Length; i++)
            {
                var (ox, oy, pattern) = allPhases[i];
                var aliasLayout = CreateLayout(CameraPixelFormat.BayerRggb16, cfaOriginX: ox, cfaOriginY: oy);
                var aliasRaw = await CommitRawArtifactAsync(
                    root, journal, aliasLayout, bggrBytes, BaseUtc.AddSeconds(20 + i * 2), token).ConfigureAwait(false);
                Assert.AreEqual(samePayloadSha, aliasRaw.Artifact.ChecksumSha256);

                var aliasPreview = await artifactService.GetPreviewAsync(aliasRaw.Artifact.ArtifactId, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, aliasPreview.Status);
                cacheChecksums[pattern] = aliasPreview.ChecksumSha256!;
                if (pattern == BayerPattern.Rggb)
                {
                    rggbFromBggr = aliasPreview;
                }
            }

            Assert.AreEqual(4, cacheChecksums.Values.Distinct(StringComparer.Ordinal).Count(),
                "Preview cache must produce distinct entries for identical Bayer bytes with different CFA origins.");
            var decodedRggb = JpegImageCodec.DecodeJpeg(rggbFromBggr!.Content, token);
            var swappedRed = MeasureJpegPatch(decodedRggb, RedTarget, radius: 3);
            var swappedBlue = MeasureJpegPatch(decodedRggb, BlueTarget, radius: 3);
            Assert.IsGreaterThan(swappedRed.Red * JpegColorDominanceRatio, swappedRed.Blue,
                $"RGGB interpretation of BGGR bytes must invert red target, got {swappedRed}.");
            Assert.IsGreaterThan(swappedBlue.Blue * JpegColorDominanceRatio, swappedBlue.Red,
                $"RGGB interpretation of BGGR bytes must invert blue target, got {swappedBlue}.");
            _ = options;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("mono16-full", CameraPixelFormat.Mono16, 0, 0)]
    [DataRow("bayer-rggb16-full", CameraPixelFormat.BayerRggb16, 0, 0)]
    [DataRow("bayer-rggb16-odd-origin-bggr", CameraPixelFormat.BayerRggb16, 1, 1)]
    public async Task EndToEndCenteredPipelineQualification_Mono16BayerFullFrameAndOddOriginBggrAsync(
        string profileName, CameraPixelFormat format, int cfaOriginX, int cfaOriginY)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-1928-{profileName}");
        var token = CancellationToken.None;
        var bayer = format == CameraPixelFormat.BayerRggb16;
        var centroidTol = bayer ? BayerCentroidTolerancePixels : MonoCentroidTolerancePixels;
        try
        {
            var clock = new QualificationClock { UtcNow = BaseUtc };
            using var provider = CreateProvider(root, clock);
            var layout = CreateLayout(format, cfaOriginX, cfaOriginY);
            var profile = CreateProfile(root, profileName, layout);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile)
                .ConfigureAwait(false);
            var schedule = await ReadScheduleAsync(provider, token).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var processing = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
            var exposureProducts = provider.GetRequiredService<IExposureProductReader>();
            var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
            var registration = provider.GetRequiredService<CenteredWindowRegistrationWorker>();
            var singlePublisher = provider.GetRequiredService<CenteredViewPublicationWorker>();
            var windowPublisher = provider.GetRequiredService<CenteredViewWindowPublicationWorker>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var layerProducer = new CenteredViewLayerProducer(views, persistence);
            var reader = provider.GetRequiredService<ICenteredViewPresentationReader>();

            // Repository scope note (#1928): no real-camera raw binary Mono16/Bayer frames are checked into git.
            // The retained November Mono16 and staged odd-origin bayer-103.bin corpus lives outside the repo and
            // is replayed end-to-end by [TestCategory("Manual")] CenteredCorpusEvidenceTests when HVO_CAPTURE_1919_CORPUS
            // is mounted. This CI-runnable qualification replays deterministic checksummed non-VirtualSky frames with
            // verified descriptors and bit-for-bit reopened payload SHA-256s without claiming real-camera hardware capture.
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Qualification profile={profileName} evidenceOrigin=DeterministicSyntheticNonVirtualReplay " +
                $"repoRealCameraCorpus=NoneExternalViaHVO_CAPTURE_1919_CORPUS format={format} " +
                $"cfa={layout.CfaPattern} origin=({layout.Readout?.CfaOriginX},{layout.Readout?.CfaOriginY}) " +
                $"bits={layout.SampleDepthBits}/{layout.ContainerDepthBits} levels=[{layout.BlackLevel},{layout.WhiteLevel}] " +
                $"roi=({layout.Readout?.RoiX},{layout.Readout?.RoiY},{layout.Width}x{layout.Height}) bin={layout.Readout?.BinX}x{layout.Readout?.BinY} " +
                $"cadence={Cadence.TotalSeconds:0.###}s tolerances: centroid<={centroidTol:F2}px overlay<={OverlayCoordinateTolerancePixels:F2}px"));

            var artifactService = provider.GetRequiredService<ICameraAgentArtifactService>();
            var admitted = new List<(Guid CaptureId, ExposureKey Key, ReconstructionDescriptor Descriptor, string SourceSha256)>();
            CenteredViewStream? stream = null;

            for (var index = 0; index < SequenceShifts.Length; index++)
            {
                var (dx, dy) = SequenceShifts[index];
                var scheduled = BaseUtc.AddTicks(Cadence.Ticks * index);
                // Exposure 105 (index 4) carries a saturated clipping patch to verify source-clipping exclusion.
                var rawBytes = RenderSyntheticFrame(
                    format, cfaOriginX, cfaOriginY, dx, dy, FieldStars, includeClippedPatch: index == 4);
                var expectedSha = PayloadChecksum.ComputeSha256(rawBytes);

                var receipt = await AdmitSyntheticExposureAsync(
                    ingress, clock, active, schedule, scheduled, layout, rawBytes,
                    $"{profileName}-frame-{101 + index}", token).ConfigureAwait(false);
                var descriptor = receipt.Manifest.Descriptor;
                Assert.AreEqual(expectedSha, descriptor.Artifact.ChecksumSha256);
                Assert.AreEqual(layout, descriptor.Layout);
                Assert.AreEqual(1L + index, descriptor.Capture.CaptureSequence);

                stream ??= CenteredViewLookup.ResolveStream(
                    await ingress.ReadCurrentAsync(new(active.AgentId!, profileName), token).ConfigureAwait(false));
                Assert.IsNotNull(stream);
                var exposureKey = new ExposureKey(stream.Camera, stream.RunId, descriptor.Capture.CaptureId);
                admitted.Add((descriptor.Capture.CaptureId, exposureKey, descriptor, expectedSha));

                // Reopen BOTH the durable journal record AND the actual raw payload bytes from disk,
                // hashing the reopened payload stream directly and verifying bit-for-bit equality!
                var reopenedRaw = await ingress.ReadCenteredRawAsync(exposureKey, token).ConfigureAwait(false);
                Assert.IsNotNull(reopenedRaw);
                Assert.IsTrue(reopenedRaw.IsCommitted);
                Assert.AreEqual(descriptor.Capture, reopenedRaw.Manifest.Descriptor.Capture);
                Assert.AreEqual(descriptor.Layout, reopenedRaw.Manifest.Descriptor.Layout);
                Assert.AreEqual(descriptor.Timing, reopenedRaw.Manifest.Descriptor.Timing);
                Assert.AreEqual(descriptor.Controls, reopenedRaw.Manifest.Descriptor.Controls);
                Assert.AreEqual(expectedSha, reopenedRaw.Manifest.Descriptor.Artifact.ChecksumSha256);

                var openedRaw = await artifactService.OpenContentAsync(descriptor.Artifact.ArtifactId, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, openedRaw.Status);
                Assert.IsNotNull(openedRaw.Content);
                byte[] reopenedPayloadBytes;
                await using (openedRaw.Content.ConfigureAwait(false))
                {
                    using var buffer = new MemoryStream(rawBytes.Length);
                    await openedRaw.Content.CopyToAsync(buffer, token).ConfigureAwait(false);
                    reopenedPayloadBytes = buffer.ToArray();
                }
                Assert.AreEqual(rawBytes.Length, reopenedPayloadBytes.Length);
                Assert.AreEqual(expectedSha, PayloadChecksum.ComputeSha256(reopenedPayloadBytes),
                    "SHA-256 computed over actual reopened raw payload bytes must match admitted source bytes.");
                CollectionAssert.AreEqual(rawBytes, reopenedPayloadBytes,
                    "Reopened durable raw payload bytes must match admitted raw bytes bit-for-bit.");

                var galleryCapture = await provider.GetRequiredService<ICameraAgentGallery>()
                    .GetCaptureAsync(descriptor.Capture.CaptureId, token).ConfigureAwait(false);
                Assert.IsNotNull(galleryCapture);
                Assert.AreNotEqual(GalleryEvidenceOrigin.Simulated, galleryCapture.EvidenceOrigin,
                    "Non-VirtualSky captures must remain distinct from VirtualSky simulated evidence in the durable gallery.");

                // Run real standard processing lane (Calibration/Linear normalization + ExposureMeasurement).
                await ProcessStandardLaneAsync(provider, active, token).ConfigureAwait(false);

                // Verify qualified linear output + measured centroids & HFD.
                var linear = await exposureProducts.ReadLinearAsync(exposureKey, token).ConfigureAwait(false);
                Assert.IsNotNull(linear?.Product);
                Assert.AreEqual(LinearExposureDisposition.QualifiedNormalizationOnly, linear.Disposition);
                Assert.AreEqual(expectedSha, linear.Provenance!.RawPayloadSha256);

                var executionId = ProcessingGraphOperationsCoordinator.GetLiveExecutionId(
                    descriptor.Capture.CaptureId, stream.GenerationId);
                var sources = await processing.ReadCenteredViewSourcesAsync(exposureKey, executionId, 0, 24, token)
                    .ConfigureAwait(false);
                var measurementRef = Assert.ContainsSingle(sources.Where(item =>
                    item.Slot.SchemaVersion == ExposureMeasurementProducts.SchemaVersion)).Product!;
                var (_, measurement) = await persistence.ReadCenteredViewMeasurementAsync(
                    exposureKey.CaptureId,
                    new(measurementRef.ArtifactId, measurementRef.OutputIdentitySha256, measurementRef.PayloadSha256),
                    token).ConfigureAwait(false);
                Assert.AreEqual(ExposureMeasurementStatus.Usable, measurement.Sources.Status, measurement.Sources.Reason);
                Assert.IsGreaterThanOrEqualTo(12, measurement.Sources.Detections.Count);
                Assert.AreEqual(ExposureMeasurementStatus.Usable, measurement.Hfd.Status, measurement.Hfd.Reason);
                Assert.IsNotNull(measurement.Hfd.MedianPixels);
                Assert.IsTrue(measurement.Hfd.MedianPixels is >= HfdMinimumPixels and <= HfdMaximumPixels,
                    $"Expected finite HFD in [{HfdMinimumPixels},{HfdMaximumPixels}], got {measurement.Hfd.MedianPixels}.");
                if (bayer)
                {
                    var expectedPattern = cfaOriginX == 1 && cfaOriginY == 1 ? BayerPattern.Bggr : BayerPattern.Rggb;
                    Assert.AreEqual(expectedPattern, measurement.Sources.LocalBayerPattern);
                }
                else
                {
                    Assert.IsNull(measurement.Sources.LocalBayerPattern);
                }

                // Verify measured centroids agree with shifted ground-truth stars within pre-declared tolerance.
                var maxCentroidErr = 0.0;
                var matchedStars = 0;
                foreach (var (sx, sy, _) in FieldStars)
                {
                    var expectedX = sx - dx;
                    var expectedY = sy - dy;
                    var nearest = measurement.Sources.Detections
                        .Select(det => Math.Sqrt(Math.Pow(det.Pixel.X - expectedX, 2) + Math.Pow(det.Pixel.Y - expectedY, 2)))
                        .Min();
                    if (nearest <= centroidTol)
                    {
                        matchedStars++;
                        maxCentroidErr = Math.Max(maxCentroidErr, nearest);
                    }
                }
                Assert.IsGreaterThanOrEqualTo(12, matchedStars, $"Matched only {matchedStars} stars on frame {101 + index}.");
                TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  frame #{101 + index}: sha256={expectedSha[..12]} detections={measurement.Sources.Detections.Count} " +
                    $"matched={matchedStars} maxCentroidErr={maxCentroidErr:F3}px hfdMedian={measurement.Hfd.MedianPixels:F2}px"));

                await singlePublisher.DrainCameraAsync(stream.Camera, stream, CancellationToken.None, token)
                    .ConfigureAwait(false);
                await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
                await registration.RegisterPendingAsync(token).ConfigureAwait(false);
                await windowPublisher.PublishReadyAsync(token).ConfigureAwait(false);
            }

            Assert.IsNotNull(stream);

            // Verify both overlapping 5-frame stacks (103 and 104) registered via VerifiedCentroidSimilarity.
            var stack103Archive = (await views.ReadArchiveAsync(stream, admitted[2].CaptureId, token).ConfigureAwait(false))
                .Single(item => item.Base.Definition.Kind == CenteredViewKind.Stack);
            var single103Archive = (await views.ReadArchiveAsync(stream, admitted[2].CaptureId, token).ConfigureAwait(false))
                .Single(item => item.Base.Definition.Kind == CenteredViewKind.Single);
            var stack104Archive = (await views.ReadArchiveAsync(stream, admitted[3].CaptureId, token).ConfigureAwait(false))
                .Single(item => item.Base.Definition.Kind == CenteredViewKind.Stack);

            Assert.AreEqual(5, stack103Archive.Base.Definition.Contributors.Count);
            Assert.AreEqual(5, stack104Archive.Base.Definition.Contributors.Count);
            Assert.AreEqual(CenteredViewImageSource.QualifiedLinear, single103Archive.Base.Definition.Preview.Source);
            Assert.AreEqual(CenteredViewImageSource.RegisteredLinear, stack103Archive.Base.Definition.Preview.Source);

            // Verify registration science evidence, known displacements, source clipping, mono intensity & linear RGB channels.
            var science103Product = await persistence.ReadCenteredViewRetainedProductAsync(
                admitted[2].CaptureId, stack103Archive.Base.Definition.Science, token).ConfigureAwait(false);
            var evidence103 = CenteredRegisteredStackEvidence.Read(science103Product);
            Assert.AreEqual(CenteredStackDisposition.Registered, evidence103.Disposition);
            Assert.AreEqual(5 * ExposureDuration.Ticks, evidence103.TotalIntegrationTicks);

            var science104Product = await persistence.ReadCenteredViewRetainedProductAsync(
                admitted[3].CaptureId, stack104Archive.Base.Definition.Science, token).ConfigureAwait(false);
            var evidence104 = CenteredRegisteredStackEvidence.Read(science104Product);
            Assert.AreEqual(CenteredStackDisposition.Registered, evidence104.Disposition);

            // Verify direct registration recovered exact known displacements on exposure 105 -> 103 and 105 -> 104.
            var map105To103 = CenteredRegisteredStackRecipe.ReconstructSourceMap(
                evidence103, offset: 2, new Dictionary<Guid, ProcessingArtifact>())!;
            var map105To104 = CenteredRegisteredStackRecipe.ReconstructSourceMap(
                evidence104, offset: 1, new Dictionary<Guid, ProcessingArtifact>())!;
            Assert.AreEqual(CenteredRegistrationMethod.VerifiedCentroidSimilarity, evidence103.Sources[4].Method);
            Assert.AreEqual(CenteredRegistrationMethod.VerifiedCentroidSimilarity, evidence104.Sources[3].Method);

            var expected105To103Dx = SequenceShifts[4].Dx - SequenceShifts[2].Dx;
            var expected105To103Dy = SequenceShifts[4].Dy - SequenceShifts[2].Dy;
            var expected105To104Dx = SequenceShifts[4].Dx - SequenceShifts[3].Dx;
            var expected105To104Dy = SequenceShifts[4].Dy - SequenceShifts[3].Dy;
            var probe = new PixelPoint(80.5, 64.5);
            var mapped103 = map105To103.SourceToReference(probe)!.Value;
            var mapped104 = map105To104.SourceToReference(probe)!.Value;
            Assert.AreEqual(probe.X + expected105To103Dx, mapped103.X, centroidTol);
            Assert.AreEqual(probe.Y + expected105To103Dy, mapped103.Y, centroidTol);
            Assert.AreEqual(probe.X + expected105To104Dx, mapped104.X, centroidTol);
            Assert.AreEqual(probe.Y + expected105To104Dy, mapped104.Y, centroidTol);

            // Verify source clipping on exposure 105 (bit 4 = 1<<4 = 16) is excluded from stack 103 support at ClippedPatchCenter.
            var supportPin = evidence103.Outputs.Single(output => output.Channel == "support");
            var supportProduct = await persistence.ReadCenteredViewRetainedProductAsync(
                admitted[2].CaptureId,
                new(supportPin.ArtifactId, supportPin.OutputIdentitySha256, supportPin.PayloadSha256), token)
                .ConfigureAwait(false);
            var unclippedSupport = supportProduct.Payload.Span[(int)NeutralTarget.Y * Width + (int)NeutralTarget.X];
            var clippedSupport = supportProduct.Payload.Span[(int)ClippedPatchCenter.Y * Width + (int)ClippedPatchCenter.X];
            Assert.AreEqual((byte)31, unclippedSupport, "Unclipped interior pixel must include all 5 contributors.");
            Assert.AreEqual(0, clippedSupport & (1 << 4), "Clipped source 105 photosites must be excluded from support.");
            Assert.AreEqual((byte)15, clippedSupport, "Non-clipped exposures 101..104 must still support that pixel.");

            // Verify linear registered stack channel fidelity (no mixed-CFA contamination, no R/B swap, mono preserved).
            if (bayer)
            {
                var redPin = evidence103.Outputs.Single(output => output.Channel == "red");
                var greenPin = evidence103.Outputs.Single(output => output.Channel == "green");
                var bluePin = evidence103.Outputs.Single(output => output.Channel == "blue");
                Assert.AreNotEqual(redPin.PayloadSha256, greenPin.PayloadSha256);
                Assert.AreNotEqual(bluePin.PayloadSha256, greenPin.PayloadSha256);
                Assert.AreNotEqual(redPin.PayloadSha256, bluePin.PayloadSha256);
                var redPlane = await persistence.ReadCenteredViewRetainedProductAsync(
                    admitted[2].CaptureId, new(redPin.ArtifactId, redPin.OutputIdentitySha256, redPin.PayloadSha256), token)
                    .ConfigureAwait(false);
                var greenPlane = await persistence.ReadCenteredViewRetainedProductAsync(
                    admitted[2].CaptureId, new(greenPin.ArtifactId, greenPin.OutputIdentitySha256, greenPin.PayloadSha256), token)
                    .ConfigureAwait(false);
                var bluePlane = await persistence.ReadCenteredViewRetainedProductAsync(
                    admitted[2].CaptureId, new(bluePin.ArtifactId, bluePin.OutputIdentitySha256, bluePin.PayloadSha256), token)
                    .ConfigureAwait(false);
                AssertLinearColorTargets(redPlane.Payload, greenPlane.Payload, bluePlane.Payload, Width, $"{profileName}-linear");
            }
            else
            {
                var monoPin = evidence103.Outputs.Single(output => output.Channel == "mono");
                var monoPlane = await persistence.ReadCenteredViewRetainedProductAsync(
                    admitted[2].CaptureId, new(monoPin.ArtifactId, monoPin.OutputIdentitySha256, monoPin.PayloadSha256), token)
                    .ConfigureAwait(false);
                var measuredNeutral = MeasureLinearPatchMean(monoPlane.Payload, Width, NeutralTarget, radius: 2);
                Assert.AreEqual(NeutralAdu, measuredNeutral, NeutralAdu * MonoIntensityRelativeTolerance,
                    $"Mono16 registered intensity must preserve {NeutralAdu} ADU within {MonoIntensityRelativeTolerance:P0}.");
            }

            // Decode actual published JPEG previews for Single 103, Stack 103, and Stack 104 through public presentation reader.
            foreach (var (label, archiveBase) in new[]
            {
                ("single-103", single103Archive),
                ("stack-103", stack103Archive),
                ("stack-104", stack104Archive)
            })
            {
                var decoded = await DecodePublishedPreviewAsync(reader, archiveBase, token).ConfigureAwait(false);
                Assert.AreEqual(Width, decoded.Width, $"{label} preview width");
                Assert.AreEqual(Height, decoded.Height, $"{label} preview height");
                if (bayer)
                {
                    var offsetX = label == "stack-104" ? SequenceShifts[2].Dx - SequenceShifts[3].Dx : 0;
                    var offsetY = label == "stack-104" ? SequenceShifts[2].Dy - SequenceShifts[3].Dy : 0;
                    AssertColorPreviewTargets(decoded, $"{profileName}-{label}", offsetX, offsetY);
                }
                else
                {
                    AssertMonoPreviewEquality(decoded, $"{profileName}-{label}");
                }
            }

            // Publish moving trail on exposure 105 and verify exact base-scoped SVG pixel coordinates on BOTH stacks 103 & 104!
            var trailStart105 = new PixelPoint(40.0, 56.0);
            var trailEnd105 = new PixelPoint(72.0, 74.0);
            await PublishMovingTrailOnSourceAsync(
                persistence, layerProducer, admitted[4].Key, admitted[4].Descriptor, layout,
                trailStart105, trailEnd105, token).ConfigureAwait(false);

            var enriched103 = (await views.ReadArchiveAsync(stream, admitted[2].CaptureId, token).ConfigureAwait(false))
                .Single(item => item.Base.BaseId == stack103Archive.Base.BaseId);
            var enriched104 = (await views.ReadArchiveAsync(stream, admitted[3].CaptureId, token).ConfigureAwait(false))
                .Single(item => item.Base.BaseId == stack104Archive.Base.BaseId);

            var line103 = await ReadTrailSvgLineAsync(reader, enriched103.Base.BaseId, enriched103.LatestRevisionId, token)
                .ConfigureAwait(false);
            var line104 = await ReadTrailSvgLineAsync(reader, enriched104.Base.BaseId, enriched104.LatestRevisionId, token)
                .ConfigureAwait(false);
            AssertTrailSegmentMatchesExpected(
                line103,
                new(trailStart105.X + expected105To103Dx, trailStart105.Y + expected105To103Dy),
                new(trailEnd105.X + expected105To103Dx, trailEnd105.Y + expected105To103Dy),
                $"{profileName}-stack-103-trail");
            AssertTrailSegmentMatchesExpected(
                line104,
                new(trailStart105.X + expected105To104Dx, trailStart105.Y + expected105To104Dy),
                new(trailEnd105.X + expected105To104Dx, trailEnd105.Y + expected105To104Dy),
                $"{profileName}-stack-104-trail");
            Assert.IsGreaterThan(0.9, Math.Abs(line103.X1 - line104.X1),
                "Overlapping stacks 103 and 104 must project exposure 105's moving trail to distinct base-scoped coordinates.");

            // Verify Operations evidence strings identify the exact input layout, CFA origin, and registration method.
            var view103 = await views.ReadRevisionAsync(enriched103.Base.BaseId, enriched103.LatestRevisionId, token)
                .ConfigureAwait(false);
            Assert.IsNotNull(view103);
            StringAssert.Contains(CenteredPublicationEvidence.FormatLayout(view103.Base.Definition.Reference.Input.Raw.Layout),
                bayer ? "BayerRggb16 (color)" : "Mono16 (mono)", StringComparison.Ordinal);
            StringAssert.Contains(CenteredPublicationEvidence.FormatReadout(view103.Base.Definition.Reference.Input.Raw.Layout),
                bayer ? $"origin ({cfaOriginX}, {cfaOriginY})" : "No CFA", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, 0, 0)]
    [DataRow(CameraPixelFormat.BayerRggb16, 1, 1)]
    public async Task CenteredPipelineQualification_BlankPoorFitMissedSlotAndPartialStackAsync(
        CameraPixelFormat format, int cfaOriginX, int cfaOriginY)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-1928-partial-{format}");
        var token = CancellationToken.None;
        try
        {
            var clock = new QualificationClock { UtcNow = BaseUtc };
            using var provider = CreateProvider(root, clock);
            var layout = CreateLayout(format, cfaOriginX, cfaOriginY);
            var profile = CreateProfile(root, "partial-qual", layout);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile)
                .ConfigureAwait(false);
            var schedule = await ReadScheduleAsync(provider, token).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var processing = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
            var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
            var registration = provider.GetRequiredService<CenteredWindowRegistrationWorker>();
            var singlePublisher = provider.GetRequiredService<CenteredViewPublicationWorker>();
            var windowPublisher = provider.GetRequiredService<CenteredViewWindowPublicationWorker>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();

            // Slot -2 (t=0s): valid shifted exposure 101 -> contributes via VerifiedCentroidSimilarity.
            var valid101 = await AdmitSyntheticExposureAsync(
                ingress, clock, active, schedule, BaseUtc, layout,
                RenderSyntheticFrame(format, cfaOriginX, cfaOriginY, -2.0, +1.0, FieldStars, includeClippedPatch: false),
                "slot-m2-valid", token).ConfigureAwait(false);
            await ProcessStandardLaneAsync(provider, active, token).ConfigureAwait(false);

            // Slot -1 (t=2s): blank exposure 102 -> explicit insufficient evidence (Empty/NoCandidates), rejected by registration.
            var blank102 = await AdmitSyntheticExposureAsync(
                ingress, clock, active, schedule, BaseUtc.AddSeconds(2), layout,
                RenderBlankFrame(format), "slot-m1-blank", token).ConfigureAwait(false);
            await ProcessStandardLaneAsync(provider, active, token).ConfigureAwait(false);

            // Slot 0 (t=4s): reference exposure 103 -> contributes via ReferenceIdentity.
            var ref103 = await AdmitSyntheticExposureAsync(
                ingress, clock, active, schedule, BaseUtc.AddSeconds(4), layout,
                RenderSyntheticFrame(format, cfaOriginX, cfaOriginY, 0, 0, FieldStars, includeClippedPatch: false),
                "slot-0-reference", token).ConfigureAwait(false);
            await ProcessStandardLaneAsync(provider, active, token).ConfigureAwait(false);

            // Slot +1 (t=6s): MISSED SLOT (intentionally omitted -> nominal-slot-missed on deadline close).

            // Slot +2 (t=8s): poor-fit scrambled star field exposure 104 -> usable measurement, but RegistrationRejected (NoDirectMatch).
            var poorFit104 = await AdmitSyntheticExposureAsync(
                ingress, clock, active, schedule, BaseUtc.AddSeconds(8), layout,
                RenderSyntheticFrame(format, cfaOriginX, cfaOriginY, 0, 0, ScrambledStars, includeClippedPatch: false),
                "slot-p2-poor-fit", token).ConfigureAwait(false);
            await ProcessStandardLaneAsync(provider, active, token).ConfigureAwait(false);

            var stream = CenteredViewLookup.ResolveStream(
                await ingress.ReadCurrentAsync(new(active.AgentId!, "partial-qual"), token).ConfigureAwait(false));
            Assert.IsNotNull(stream);

            // Verify explicit insufficient measurement evidence on the blank exposure 102.
            var blankKey = new ExposureKey(stream.Camera, stream.RunId, blank102.Manifest.Descriptor.Capture.CaptureId);
            var blankExec = ProcessingGraphOperationsCoordinator.GetLiveExecutionId(blankKey.CaptureId, stream.GenerationId);
            var blankSources = await processing.ReadCenteredViewSourcesAsync(blankKey, blankExec, 0, 24, token)
                .ConfigureAwait(false);
            var blankMeasurementRef = Assert.ContainsSingle(blankSources.Where(item =>
                item.Slot.SchemaVersion == ExposureMeasurementProducts.SchemaVersion)).Product!;
            var (_, blankMeasurement) = await persistence.ReadCenteredViewMeasurementAsync(
                blankKey.CaptureId,
                new(blankMeasurementRef.ArtifactId, blankMeasurementRef.OutputIdentitySha256, blankMeasurementRef.PayloadSha256),
                token).ConfigureAwait(false);
            Assert.AreNotEqual(ExposureMeasurementStatus.Usable, blankMeasurement.Sources.Status);
            Assert.IsEmpty(blankMeasurement.Sources.Detections);
            Assert.IsFalse(string.IsNullOrWhiteSpace(blankMeasurement.Sources.Reason));

            // Advance clock past window close deadline so the missed +1 slot freezes as Missing.
            clock.UtcNow = BaseUtc.AddSeconds(30);
            await singlePublisher.DrainCameraAsync(stream.Camera, stream, CancellationToken.None, token)
                .ConfigureAwait(false);
            await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
            await registration.RegisterPendingAsync(token).ConfigureAwait(false);
            await windowPublisher.PublishReadyAsync(token).ConfigureAwait(false);

            var refId = ref103.Manifest.Descriptor.Capture.CaptureId;
            var disposition = await processing.ReadCenteredWindowByReferenceAsync(refId, token).ConfigureAwait(false);
            Assert.IsNotNull(disposition?.Publication, disposition?.FailureReason);
            Assert.AreEqual(CenteredStackDisposition.Registered, disposition.Publication.StackDisposition);
            CollectionAssert.AreEqual(
                new[] { valid101.Manifest.Descriptor.Capture.CaptureId, refId },
                disposition.Publication.ContributingCaptureIds.ToArray());

            var stack = (await views.ReadArchiveAsync(stream, refId, token).ConfigureAwait(false))
                .Single(item => item.Base.Definition.Kind == CenteredViewKind.Stack);
            var science = await persistence.ReadCenteredViewRetainedProductAsync(
                refId, stack.Base.Definition.Science, token).ConfigureAwait(false);
            var evidence = CenteredRegisteredStackEvidence.Read(science);

            // Verify every single slot's exact disposition: -2 Contributed, -1 Blank/Empty, 0 Reference, +1 MissingSlot, +2 PoorFit Rejected.
            Assert.AreEqual(CenteredRegistrationReason.Contributed, evidence.Sources[0].Reason);
            Assert.AreEqual(CenteredRegistrationMethod.VerifiedCentroidSimilarity, evidence.Sources[0].Method);
            Assert.AreEqual(CenteredRegistrationReason.MeasurementEmpty, evidence.Sources[1].Reason);
            Assert.AreEqual(CenteredRegistrationReason.Contributed, evidence.Sources[2].Reason);
            Assert.AreEqual(CenteredRegistrationMethod.ReferenceIdentity, evidence.Sources[2].Method);
            Assert.AreEqual(CenteredRegistrationReason.MissingSlot, evidence.Sources[3].Reason);
            Assert.AreEqual(CenteredRegistrationReason.RegistrationRejected, evidence.Sources[4].Reason);
            Assert.AreEqual(DirectRegistrationStatus.NoDirectMatch, evidence.Sources[4].PairCheck?.Status);
            Assert.AreEqual(2 * ExposureDuration.Ticks, evidence.TotalIntegrationTicks);
            Assert.AreEqual(2, stack.Base.Definition.Contributors.Count);
            Assert.AreEqual(CenteredWindowSlotState.MissingExposure.ToString(), stack.Base.Definition.Window!.Slots[3].ClosureState);
            _ = poorFit104;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task CenteredPipelineQualification_VirtualSkyMonoAndBayerEvidenceDistinctionAsync(CameraPixelFormat format)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-1928-virtualsky-{format}");
        var token = CancellationToken.None;
        const int size = 224;
        try
        {
            var clock = new QualificationClock { UtcNow = BaseUtc };
            var deviceClock = new ExposureTestTimeProvider(BaseUtc, autoAdvance: true);
            using var provider = CreateProvider(root, clock);
            var profile = CreateVirtualSkyProfile(root, BaseUtc, format, size);
            var catalog = CreateVirtualSkyChromaticWideCatalog(BaseUtc, profile);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile)
                .ConfigureAwait(false);
            var schedule = await ReadScheduleAsync(provider, token).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
            var registration = provider.GetRequiredService<CenteredWindowRegistrationWorker>();
            var singlePublisher = provider.GetRequiredService<CenteredViewPublicationWorker>();
            var windowPublisher = provider.GetRequiredService<CenteredViewWindowPublicationWorker>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var reader = provider.GetRequiredService<ICenteredViewPresentationReader>();
            var module = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var cameraLease = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);

            var captureIds = new List<Guid>();
            CenteredViewStream? stream = null;
            for (var index = 0; index < 5; index++)
            {
                var scheduled = BaseUtc.AddSeconds(index * 2);
                clock.UtcNow = scheduled;
                deviceClock.Advance(scheduled - deviceClock.GetUtcNow());
                var reservation = await ingress.ReserveCenteredAsync(
                    active, new(null, null, null, null, "virtualsky-qual"), null, token, schedule: schedule)
                    .ConfigureAwait(false);
                var request = new CaptureRequest(scheduled, Cadence, CaptureMode.Still,
                    new(ExposureDuration, 1, null, null))
                {
                    BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                    BoundGraph = reservation.Revision.Pipeline
                };
                await ingress.DispatchCenteredAsync(
                    reservation.Pending.Key, request.RequestedSetpoint!, null, "virtualsky-qual",
                    token, scheduled, request.TargetInterval).ConfigureAwait(false);
                var captured = await module.CaptureBoundAsync(reservation.Pending.Key, request, token)
                    .ConfigureAwait(false);
                clock.UtcNow = captured.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
                var receipt = await ingress.AcceptCenteredAsync(
                    active,
                    new(request, captured, scheduled, request.TargetInterval,
                        captured.AcquisitionTiming.ReadoutCompletedUtc - scheduled),
                    reservation, token).ConfigureAwait(false);

                // Explicitly distinguish VirtualSky simulated evidence from physical/retained non-virtual evidence via the durable gallery!
                var gallery = provider.GetRequiredService<ICameraAgentGallery>();
                var galleryCapture = await gallery.GetCaptureAsync(receipt.Manifest.Descriptor.Capture.CaptureId, token)
                    .ConfigureAwait(false);
                Assert.IsNotNull(galleryCapture);
                Assert.AreEqual(
                    GalleryEvidenceOrigin.Simulated,
                    galleryCapture.EvidenceOrigin,
                    "VirtualSky captures must be explicitly classified as Simulated evidence in the durable gallery.");
                Assert.AreEqual(format, receipt.Manifest.Descriptor.Layout.PixelFormat);
                captureIds.Add(receipt.Manifest.Descriptor.Capture.CaptureId);

                await ProcessStandardLaneAsync(provider, active, token).ConfigureAwait(false);
                stream ??= CenteredViewLookup.ResolveStream(
                    await ingress.ReadCurrentAsync(reservation.Pending.Key.Camera, token).ConfigureAwait(false));
                await singlePublisher.DrainCameraAsync(stream!.Camera, stream, CancellationToken.None, token)
                    .ConfigureAwait(false);
                await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
                await registration.RegisterPendingAsync(token).ConfigureAwait(false);
                await windowPublisher.PublishReadyAsync(token).ConfigureAwait(false);
            }

            Assert.IsNotNull(stream);
            var stack103 = (await views.ReadArchiveAsync(stream, captureIds[2], token).ConfigureAwait(false))
                .Single(item => item.Base.Definition.Kind == CenteredViewKind.Stack);
            Assert.AreEqual(5, stack103.Base.Definition.Contributors.Count);
            var decoded = await DecodePublishedPreviewAsync(reader, stack103, token).ConfigureAwait(false);
            Assert.AreEqual(size, decoded.Width);
            Assert.AreEqual(size, decoded.Height);
            if (format == CameraPixelFormat.Mono16)
            {
                AssertMonoPreviewEquality(decoded, "virtualsky-mono16-stack103");
            }
            else
            {
                // Verify warm (red, B-V=+2.0) vs cool (blue, B-V=-0.4) chromatic stars remain distinguishable
                // above background in BOTH the linear registered stack planes and the decoded VirtualSky Bayer JPEG!
                var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
                var scienceProduct = await persistence.ReadCenteredViewRetainedProductAsync(
                    captureIds[2], stack103.Base.Definition.Science, token).ConfigureAwait(false);
                var stackEvidence = CenteredRegisteredStackEvidence.Read(scienceProduct);
                var redPin = stackEvidence.Outputs.Single(output => output.Channel == "red");
                var bluePin = stackEvidence.Outputs.Single(output => output.Channel == "blue");
                var redPlane = await persistence.ReadCenteredViewRetainedProductAsync(
                    captureIds[2], new(redPin.ArtifactId, redPin.OutputIdentitySha256, redPin.PayloadSha256), token)
                    .ConfigureAwait(false);
                var bluePlane = await persistence.ReadCenteredViewRetainedProductAsync(
                    captureIds[2], new(bluePin.ArtifactId, bluePin.OutputIdentitySha256, bluePin.PayloadSha256), token)
                    .ConfigureAwait(false);
                var warmCenter = new PixelPoint(size / 2.0 + 30, size / 2.0 - 53);
                var coolCenter = new PixelPoint(size / 2.0 + 62, size / 2.0 - 18);
                var bgCenter = new PixelPoint(size / 2.0, size / 2.0);
                var linBgR = MeasureLinearPatchMean(redPlane.Payload, size, bgCenter, 3);
                var linBgB = MeasureLinearPatchMean(bluePlane.Payload, size, bgCenter, 3);
                var linWarmR = Math.Max(1.0, MeasureLinearPatchMean(redPlane.Payload, size, warmCenter, 2) - linBgR);
                var linWarmB = Math.Max(1.0, MeasureLinearPatchMean(bluePlane.Payload, size, warmCenter, 2) - linBgB);
                var linCoolR = Math.Max(1.0, MeasureLinearPatchMean(redPlane.Payload, size, coolCenter, 2) - linBgR);
                var linCoolB = Math.Max(1.0, MeasureLinearPatchMean(bluePlane.Payload, size, coolCenter, 2) - linBgB);
                Assert.IsGreaterThan(linWarmB * 4.0, linWarmR, $"VirtualSky linear warm star expected R/B > 4.0, got {linWarmR:F1}/{linWarmB:F1}.");
                Assert.IsGreaterThan(linCoolR * 1.15, linCoolB, $"VirtualSky linear cool star expected B/R > 1.15, got {linCoolB:F1}/{linCoolR:F1}.");

                var bg = MeasureJpegPatch(decoded, bgCenter, radius: 3);
                var warm = MeasureJpegPatch(decoded, warmCenter, radius: 1);
                var cool = MeasureJpegPatch(decoded, coolCenter, radius: 1);
                var warmR = warm.Red - bg.Red;
                var warmB = warm.Blue - bg.Blue;
                var coolR = cool.Red - bg.Red;
                var coolB = cool.Blue - bg.Blue;
                Assert.IsGreaterThanOrEqualTo(25.0, warmR, $"VirtualSky JPEG warm star must rise well above background, warm={warm} bg={bg}.");
                Assert.IsGreaterThan(Math.Max(1.0, warmB) * 1.40, warmR, $"VirtualSky JPEG warm star expected delta R/B > 1.40, got warm={warm} bg={bg}.");
                // ASI178MC linear B/R at B-V=-0.4 is ~1.27, which maps under gamma=2.2 to ~1.115 in sRGB; require strictly > 1.05 above background!
                Assert.IsGreaterThanOrEqualTo(25.0, coolB, $"VirtualSky JPEG cool target must rise well above background, cool={cool} bg={bg}.");
                Assert.IsGreaterThan(Math.Max(1.0, coolR) * 1.05, coolB, $"VirtualSky JPEG cool target expected non-vacuous blue dominance B/R > 1.05, got cool={cool} bg={bg}.");
                TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"VirtualSky Bayer stack103 linear warm R/B={linWarmR / linWarmB:F2} cool B/R={linCoolB / linCoolR:F2}; " +
                    $"JPEG warm delta R={warmR:F1}/B={warmB:F1} cool delta B={coolB:F1}/R={coolR:F1}"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CenteredPipelineQualification_ProspectiveNegativeControlsDetectAllDefects()
    {
        // Build a clean baseline odd-origin (1,1) BGGR preview JPEG and verify it passes the positive oracle first.
        var layout = CreateLayout(CameraPixelFormat.BayerRggb16, 1, 1);
        var bggrBytes = RenderSyntheticFrame(
            CameraPixelFormat.BayerRggb16, 1, 1, 0, 0, FieldStars, includeClippedPatch: false);
        var encoder = new CameraAgentPreviewEncoder();
        var validJpeg = encoder.Encode(layout, bggrBytes, Width, 2 * 1024 * 1024, CancellationToken.None);
        var validDecoded = JpegImageCodec.DecodeJpeg(validJpeg.Content, CancellationToken.None);
        AssertColorPreviewTargets(validDecoded, "negative-control-baseline");

        // 1. Prospective failure: wrong channel order (R/B swap / mismatched RGGB phase on BGGR crop).
        var wrongPhaseRgb = BayerRggb16Demosaicer.DemosaicToRgb24(Width, Height, bggrBytes, pattern: BayerPattern.Rggb);
        var wrongPhaseDecoded = new DecodedImage(Width, Height, CameraPixelFormat.Rgb24, Width * 3, wrongPhaseRgb, "raw", "test");
        Assert.ThrowsExactly<AssertFailedException>(() =>
            AssertColorPreviewTargets(wrongPhaseDecoded, "prospective-wrong-channel-order"));

        // 2. Prospective failure: identical-gray color rendering (grayscale collapse of color image).
        var collapsedGray = new byte[validDecoded.PixelData.Length];
        var src = validDecoded.PixelData.Span;
        for (var i = 0; i < collapsedGray.Length; i += 3)
        {
            var lum = (byte)Math.Round(0.2126 * src[i] + 0.7152 * src[i + 1] + 0.0722 * src[i + 2]);
            collapsedGray[i] = lum;
            collapsedGray[i + 1] = lum;
            collapsedGray[i + 2] = lum;
        }
        var grayDecoded = new DecodedImage(Width, Height, CameraPixelFormat.Rgb24, Width * 3, collapsedGray, "raw", "test");
        Assert.ThrowsExactly<AssertFailedException>(() =>
            AssertColorPreviewTargets(grayDecoded, "prospective-grayscale-collapse"));

        // 3. Prospective failure: false color injected into a Mono16 preview.
        var monoLayout = CreateLayout(CameraPixelFormat.Mono16, 0, 0);
        var monoBytes = RenderSyntheticFrame(CameraPixelFormat.Mono16, 0, 0, 0, 0, FieldStars, includeClippedPatch: false);
        var monoJpeg = encoder.Encode(monoLayout, monoBytes, Width, 2 * 1024 * 1024, CancellationToken.None);
        var monoDecoded = JpegImageCodec.DecodeJpeg(monoJpeg.Content, CancellationToken.None);
        AssertMonoPreviewEquality(monoDecoded, "negative-control-mono-baseline");
        var tintedMono = new byte[Width * Height * 3];
        for (var i = 0; i < Width * Height; i++)
        {
            var v = monoDecoded.PixelData.Span[i * (monoDecoded.PixelFormat == CameraPixelFormat.Mono8 ? 1 : 3)];
            tintedMono[i * 3] = (byte)Math.Min(255, v + 35);
            tintedMono[i * 3 + 1] = v;
            tintedMono[i * 3 + 2] = (byte)Math.Max(0, v - 35);
        }
        var falseColorDecoded = new DecodedImage(Width, Height, CameraPixelFormat.Rgb24, Width * 3, tintedMono, "raw", "test");
        Assert.ThrowsExactly<AssertFailedException>(() =>
            AssertMonoPreviewEquality(falseColorDecoded, "prospective-false-color-mono"));

        // 4. Prospective failure: wrong transform exercised through the REAL ImageSimilarity + preview transform +
        // GroupedSvgPresentationRenderer code path (swapping stack 104's shift onto stack 103's contributor trail).
        var sourceTrailStart = new PixelPoint(40.0, 56.0);
        var sourceTrailEnd = new PixelPoint(72.0, 74.0);
        var previewTransform = new CenteredViewPreviewTransform(Width, Height, 0, 0, Width, Height, Width, Height);
        var validTrailSvgLine = RenderMappedTrailThroughProductionSvg(
            new ImageSimilarity(1, 0, 2.5, -1.5), previewTransform, sourceTrailStart, sourceTrailEnd);
        AssertTrailSegmentMatchesExpected(
            validTrailSvgLine,
            new(sourceTrailStart.X + 2.5, sourceTrailStart.Y - 1.5),
            new(sourceTrailEnd.X + 2.5, sourceTrailEnd.Y - 1.5),
            "negative-control-trail-baseline");

        var wrongTransformSvgLine = RenderMappedTrailThroughProductionSvg(
            new ImageSimilarity(1, 0, 1.25, -0.75), previewTransform, sourceTrailStart, sourceTrailEnd);
        Assert.ThrowsExactly<AssertFailedException>(() =>
            AssertTrailSegmentMatchesExpected(
                wrongTransformSvgLine,
                new(sourceTrailStart.X + 2.5, sourceTrailStart.Y - 1.5),
                new(sourceTrailEnd.X + 2.5, sourceTrailEnd.Y - 1.5),
                "prospective-wrong-transform"));

        // 5. Prospective failure: naive mixed-CFA mosaic shift by (1,1) without linear reconstruction swaps/blends R and B!
        var shiftedNaive = new byte[bggrBytes.Length];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var sx = Math.Clamp(x + 1, 0, Width - 1);
                var sy = Math.Clamp(y + 1, 0, Height - 1);
                var a = BinaryPrimitives.ReadUInt16LittleEndian(bggrBytes.AsSpan((y * Width + x) * 2));
                var b = BinaryPrimitives.ReadUInt16LittleEndian(bggrBytes.AsSpan((sy * Width + sx) * 2));
                BinaryPrimitives.WriteUInt16LittleEndian(shiftedNaive.AsSpan((y * Width + x) * 2), (ushort)((a + b) / 2));
            }
        }
        var mixedRgb = BayerRggb16Demosaicer.DemosaicToRgb24(Width, Height, shiftedNaive, pattern: BayerPattern.Bggr);
        var mixedDecoded = new DecodedImage(Width, Height, CameraPixelFormat.Rgb24, Width * 3, mixedRgb, "raw", "test");
        Assert.ThrowsExactly<AssertFailedException>(() =>
            AssertColorPreviewTargets(mixedDecoded, "prospective-mixed-cfa-resampling"));
    }

    private static (double X1, double Y1, double X2, double Y2) RenderMappedTrailThroughProductionSvg(
        ImageSimilarity registration,
        CenteredViewPreviewTransform previewTransform,
        PixelPoint sourceStart,
        PixelPoint sourceEnd)
    {
        var mappedStart = previewTransform.ToPreview(registration.SourceToReference(sourceStart)!.Value);
        var mappedEnd = previewTransform.ToPreview(registration.SourceToReference(sourceEnd)!.Value);
        var payload = PresentationLayerPayloadJson.Create(
            new string('D', 64),
            previewTransform.PreviewWidth,
            previewTransform.PreviewHeight,
            segments: [new(mappedStart, mappedEnd, 2, new(255, 200, 80))]);
        var compatibility = new PresentationCompatibilityDescriptor(
            previewTransform.PreviewWidth, previewTransform.PreviewHeight, new string('F', 64), new string('1', 64));
        var source = new PresentationProductReference(
            Guid.NewGuid(), new string('E', 64), PresentationLayerPayloadJson.MediaType, compatibility);
        var layer = LayeredPresentationJson.CreateLayer(
            TransientTrailPresentation.LayerKind,
            source,
            null,
            PresentationCoordinateSpace.ScenePixels,
            PresentationLayerCompositor.AlgorithmVersion,
            TransientTrailPresentation.ProducerVersion,
            500,
            PresentationBlendMode.Normal,
            1_000_000,
            true,
            JsonSerializer.SerializeToElement(new { }));
        var manifest = LayeredPresentationJson.CreateManifest(source, null, [layer]);
        var rendered = GroupedSvgPresentationRenderer.Render(manifest, [payload], new string('2', 64));
        var document = XDocument.Parse(Encoding.UTF8.GetString(rendered.Svg.Span));
        XNamespace svg = "http://www.w3.org/2000/svg";
        var group = document.Descendants(svg + "g")
            .Single(element => element.Attribute("data-layer-kind")?.Value == TransientTrailPresentation.LayerKind);
        var line = group.Descendants(svg + "line").Single();
        return (
            double.Parse(line.Attribute("x1")!.Value, CultureInfo.InvariantCulture),
            double.Parse(line.Attribute("y1")!.Value, CultureInfo.InvariantCulture),
            double.Parse(line.Attribute("x2")!.Value, CultureInfo.InvariantCulture),
            double.Parse(line.Attribute("y2")!.Value, CultureInfo.InvariantCulture));
    }

    internal static void AssertColorPreviewTargets(
        DecodedImage image, string label, double offsetX = 0, double offsetY = 0)
    {
        Assert.AreEqual(CameraPixelFormat.Rgb24, image.PixelFormat, $"{label}: color preview must decode as Rgb24.");
        var red = MeasureJpegPatch(image, new(RedTarget.X + offsetX, RedTarget.Y + offsetY), radius: 3);
        var green = MeasureJpegPatch(image, new(GreenTarget.X + offsetX, GreenTarget.Y + offsetY), radius: 3);
        var blue = MeasureJpegPatch(image, new(BlueTarget.X + offsetX, BlueTarget.Y + offsetY), radius: 3);
        var neutral = MeasureJpegPatch(image, new(NeutralTarget.X + offsetX, NeutralTarget.Y + offsetY), radius: 3);

        Assert.IsGreaterThan(red.Blue * JpegColorDominanceRatio, red.Red,
            $"{label}: red target failed R/B >= {JpegColorDominanceRatio:F1}, got {red}.");
        Assert.IsGreaterThan(red.Green + JpegDominantMargin, red.Red,
            $"{label}: red target failed R > G + {JpegDominantMargin}, got {red}.");
        Assert.IsGreaterThan(blue.Red * JpegColorDominanceRatio, blue.Blue,
            $"{label}: blue target failed B/R >= {JpegColorDominanceRatio:F1}, got {blue}.");
        Assert.IsGreaterThan(blue.Green + JpegDominantMargin, blue.Blue,
            $"{label}: blue target failed B > G + {JpegDominantMargin}, got {blue}.");
        Assert.IsGreaterThan(green.Red + JpegDominantMargin, green.Green,
            $"{label}: green target failed G > R + {JpegDominantMargin}, got {green}.");
        Assert.IsGreaterThan(green.Blue + JpegDominantMargin, green.Green,
            $"{label}: green target failed G > B + {JpegDominantMargin}, got {green}.");

        var neutralSpread = Math.Max(Math.Abs(neutral.Red - neutral.Green),
            Math.Max(Math.Abs(neutral.Green - neutral.Blue), Math.Abs(neutral.Red - neutral.Blue)));
        Assert.IsLessThanOrEqualTo(JpegNeutralMaxChannelSpread, neutralSpread,
            $"{label}: neutral target showed false color spread {neutralSpread} > {JpegNeutralMaxChannelSpread}, got {neutral}.");
    }

    internal static void AssertMonoPreviewEquality(DecodedImage image, string label)
    {
        if (image.PixelFormat == CameraPixelFormat.Mono8)
        {
            return;
        }
        Assert.AreEqual(CameraPixelFormat.Rgb24, image.PixelFormat, $"{label}: unexpected mono preview pixel format.");
        var span = image.PixelData.Span;
        for (var y = 8; y < image.Height - 8; y += 8)
        {
            for (var x = 8; x < image.Width - 8; x += 8)
            {
                var offset = y * image.StrideBytes + x * 3;
                var spread = Math.Max(Math.Abs(span[offset] - span[offset + 1]),
                    Math.Max(Math.Abs(span[offset + 1] - span[offset + 2]), Math.Abs(span[offset] - span[offset + 2])));
                Assert.IsLessThanOrEqualTo(JpegMonoMaxChannelSpread, spread,
                    $"{label}: false color on mono preview at ({x},{y}): RGB=({span[offset]},{span[offset + 1]},{span[offset + 2]}).");
            }
        }
    }

    internal static void AssertLinearColorTargets(
        ReadOnlyMemory<byte> redPlane, ReadOnlyMemory<byte> greenPlane, ReadOnlyMemory<byte> bluePlane, int width, string label)
    {
        var redTarget = (
            R: MeasureLinearPatchMean(redPlane, width, RedTarget, 2),
            G: MeasureLinearPatchMean(greenPlane, width, RedTarget, 2),
            B: MeasureLinearPatchMean(bluePlane, width, RedTarget, 2));
        var greenTarget = (
            R: MeasureLinearPatchMean(redPlane, width, GreenTarget, 2),
            G: MeasureLinearPatchMean(greenPlane, width, GreenTarget, 2),
            B: MeasureLinearPatchMean(bluePlane, width, GreenTarget, 2));
        var blueTarget = (
            R: MeasureLinearPatchMean(redPlane, width, BlueTarget, 2),
            G: MeasureLinearPatchMean(greenPlane, width, BlueTarget, 2),
            B: MeasureLinearPatchMean(bluePlane, width, BlueTarget, 2));
        var neutralTarget = (
            R: MeasureLinearPatchMean(redPlane, width, NeutralTarget, 2),
            G: MeasureLinearPatchMean(greenPlane, width, NeutralTarget, 2),
            B: MeasureLinearPatchMean(bluePlane, width, NeutralTarget, 2));

        Assert.IsGreaterThan(redTarget.B * LinearColorDominanceRatio, redTarget.R, $"{label}: linear red target {redTarget}");
        Assert.IsGreaterThan(redTarget.G * LinearColorDominanceRatio, redTarget.R, $"{label}: linear red target {redTarget}");
        Assert.IsGreaterThan(blueTarget.R * LinearColorDominanceRatio, blueTarget.B, $"{label}: linear blue target {blueTarget}");
        Assert.IsGreaterThan(blueTarget.G * LinearColorDominanceRatio, blueTarget.B, $"{label}: linear blue target {blueTarget}");
        Assert.IsGreaterThan(greenTarget.R * LinearColorDominanceRatio, greenTarget.G, $"{label}: linear green target {greenTarget}");
        Assert.IsGreaterThan(greenTarget.B * LinearColorDominanceRatio, greenTarget.G, $"{label}: linear green target {greenTarget}");

        var mean = (neutralTarget.R + neutralTarget.G + neutralTarget.B) / 3.0;
        Assert.IsLessThanOrEqualTo(mean * LinearNeutralSpreadFraction, Math.Abs(neutralTarget.R - mean), $"{label}: neutral R {neutralTarget}");
        Assert.IsLessThanOrEqualTo(mean * LinearNeutralSpreadFraction, Math.Abs(neutralTarget.G - mean), $"{label}: neutral G {neutralTarget}");
        Assert.IsLessThanOrEqualTo(mean * LinearNeutralSpreadFraction, Math.Abs(neutralTarget.B - mean), $"{label}: neutral B {neutralTarget}");
    }

    internal static void AssertTrailSegmentMatchesExpected(
        (double X1, double Y1, double X2, double Y2) actual,
        PixelPoint expectedStart,
        PixelPoint expectedEnd,
        string label)
    {
        Assert.AreEqual(expectedStart.X, actual.X1, OverlayCoordinateTolerancePixels, $"{label}: X1 mismatch");
        Assert.AreEqual(expectedStart.Y, actual.Y1, OverlayCoordinateTolerancePixels, $"{label}: Y1 mismatch");
        Assert.AreEqual(expectedEnd.X, actual.X2, OverlayCoordinateTolerancePixels, $"{label}: X2 mismatch");
        Assert.AreEqual(expectedEnd.Y, actual.Y2, OverlayCoordinateTolerancePixels, $"{label}: Y2 mismatch");
    }

    private static (double Red, double Green, double Blue) MeasureJpegPatch(
        DecodedImage image, PixelPoint center, int radius)
    {
        var cx = (int)Math.Round(center.X);
        var cy = (int)Math.Round(center.Y);
        var span = image.PixelData.Span;
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                var offset = y * image.StrideBytes + x * 3;
                r += span[offset];
                g += span[offset + 1];
                b += span[offset + 2];
                count++;
            }
        }
        return (r / count, g / count, b / count);
    }

    private static double MeasureLinearPatchMean(ReadOnlyMemory<byte> plane, int width, PixelPoint center, int radius)
    {
        var cx = (int)Math.Round(center.X);
        var cy = (int)Math.Round(center.Y);
        var span = plane.Span;
        double sum = 0;
        var count = 0;
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                sum += BinaryPrimitives.ReadUInt16LittleEndian(span.Slice((y * width + x) * 2, 2));
                count++;
            }
        }
        return sum / count;
    }

    private static FrameLayoutDescriptor CreateLayout(CameraPixelFormat format, int cfaOriginX, int cfaOriginY)
    {
        var bayer = format == CameraPixelFormat.BayerRggb16;
        return new FrameLayoutDescriptor(
            Width, Height, Width * 2, format, FrameByteOrder.LittleEndian, 12, 16,
            FrameSamplePacking.ByteAligned, bayer ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            BlackLevel, WhiteLevel, Width * Height * 2)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(
                Width + cfaOriginX * 2, Height + cfaOriginY * 2,
                cfaOriginX, cfaOriginY, Width, Height, 1, 1, FrameBinningAlgorithm.IdentityV1,
                bayer ? cfaOriginX : null, bayer ? cfaOriginY : null)
        };
    }

    private static byte[] RenderSyntheticFrame(
        CameraPixelFormat format,
        int cfaOriginX,
        int cfaOriginY,
        double shiftDx,
        double shiftDy,
        (double X, double Y, double Peak)[] stars,
        bool includeClippedPatch)
    {
        var bayer = format == CameraPixelFormat.BayerRggb16;
        var bytes = new byte[Width * Height * 2];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                // Reference-frame coordinates corresponding to delivered sample (x, y) under translation (shiftDx, shiftDy).
                var rx = x + shiftDx;
                var ry = y + shiftDy;

                double value;
                if (includeClippedPatch &&
                    Math.Abs(rx - ClippedPatchCenter.X) <= 3 &&
                    Math.Abs(ry - ClippedPatchCenter.Y) <= 3)
                {
                    value = WhiteLevel;
                }
                else
                {
                    var channel = bayer ? (((y + cfaOriginY) & 1, (x + cfaOriginX) & 1) switch
                    {
                        (0, 0) => 0,
                        (1, 1) => 2,
                        _ => 1
                    }) : -1;

                    value = BackgroundAdu;
                    if (InPatch(rx, ry, RedTarget, 6))
                    {
                        value = !bayer ? NeutralAdu : channel == 0 ? SignalAdu : BackgroundAdu;
                    }
                    else if (InPatch(rx, ry, GreenTarget, 6))
                    {
                        value = !bayer ? NeutralAdu : channel == 1 ? SignalAdu : BackgroundAdu;
                    }
                    else if (InPatch(rx, ry, BlueTarget, 6))
                    {
                        value = !bayer ? NeutralAdu : channel == 2 ? SignalAdu : BackgroundAdu;
                    }
                    else if (InPatch(rx, ry, NeutralTarget, 6))
                    {
                        value = NeutralAdu;
                    }

                    // Add shifted Gaussian stars (sample center at x + 0.5, y + 0.5).
                    var px = x + 0.5 + shiftDx;
                    var py = y + 0.5 + shiftDy;
                    foreach (var (sx, sy, peak) in stars)
                    {
                        var dx = px - sx;
                        var dy = py - sy;
                        var r2 = dx * dx + dy * dy;
                        if (r2 < 25)
                        {
                            value += peak * Math.Exp(-0.5 * r2 / (1.15 * 1.15));
                        }
                    }
                }

                var code = (ushort)Math.Clamp(Math.Round(value), 0, WhiteLevel);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((y * Width + x) * 2), code);
            }
        }
        return bytes;
    }

    private static byte[] RenderBlankFrame(CameraPixelFormat format)
    {
        var bytes = new byte[Width * Height * 2];
        var code = (ushort)(format == CameraPixelFormat.BayerRggb16 ? 110 : 110);
        for (var i = 0; i < Width * Height; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), code);
        }
        return bytes;
    }

    private static bool InPatch(double x, double y, PixelPoint center, double halfWidth) =>
        Math.Abs(x - center.X) <= halfWidth && Math.Abs(y - center.Y) <= halfWidth;

    private static ServiceProvider CreateProvider(string root, TimeProvider clock) =>
        ProcessingGraphOperationsTests.CreateProvider(root, new Dictionary<string, string?>(), timeProvider: clock,
            configureServices: services =>
            {
                HVO.SkyMonitor.CameraAgent.Program.AddCameraModules(services);
                services.AddSingleton(new CameraModuleRegistration("QualRetained", typeof(QualificationRetainedCameraModule)));
                services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
                services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            });

    private static CameraModuleConfig CreateProfile(string root, string cameraId, FrameLayoutDescriptor layout)
    {
        var baseline = StoragePolicyRawIngressIntegrationTests.Profile(root);
        var bayer = layout.PixelFormat == CameraPixelFormat.BayerRggb16;
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var storage = baseline.Pipeline.Steps.Single(step => step.Type == "Storage");
        var readout = layout.Readout!;
        return baseline with
        {
            CenteredExposure = new(cameraId, CenteredExposureProfile.CurrentExecutionVersion),
            DeploymentLocation = DeploymentLocationSnapshot.Create("qual-site", 1, "fixture",
                null, DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix"),
            Module = new("QualRetained"),
            Rig = baseline.Rig with
            {
                Sensor = new SensorProfile(
                    $"Qual-{cameraId}", readout.NativeWidth, readout.NativeHeight, 2.4,
                    bayer ? SensorColorMode.Color : SensorColorMode.Mono,
                    layout.PixelFormat,
                    bayer ? SensorResponseMode.BayerRaw : SensorResponseMode.Monochrome,
                    layout.StrideBytes, SampleByteOrder.LittleEndian),
                Optics = new OpticsProfile(
                    "Perspective", 8, 90, 0, LensKind.Rectilinear,
                    readout.NativeWidth / 2.0, readout.NativeHeight / 2.0, null),
                Readout = new SensorReadoutProfile(
                    new SensorCrop(readout.RoiX, readout.RoiY, readout.RoiWidth, readout.RoiHeight),
                    readout.BinX, readout.BinY, readout.BinningAlgorithm, layout.PixelFormat,
                    layout.SampleDepthBits, layout.ContainerDepthBits, layout.Packing,
                    layout.StoredCodeTransform ?? FrameStoredCodeTransform.RightAlignedV1,
                    layout.LevelCodeSpace ?? FrameLevelCodeSpace.NativeSample,
                    layout.BlackLevel, layout.WhiteLevel,
                    layout.StrideBytes, SampleByteOrder.LittleEndian, layout.CfaPattern, readout.CfaOriginX, readout.CfaOriginY)
            },
            Pipeline = baseline.Pipeline with
            {
                Steps =
                [
                    new("Calibration", "linear", DependsOn: ["$raw"], Required: false, Publication: durable,
                        Options: JsonSerializer.SerializeToElement(new { strategy = "None", outputVariant = "qualified-linear" })),
                    storage with { DependsOn = ["$raw"] }
                ]
            }
        };
    }

    private static CameraModuleConfig CreateVirtualSkyProfile(
        string root, DateTimeOffset start, CameraPixelFormat format, int size)
    {
        var baseline = StoragePolicyRawIngressIntegrationTests.Profile(root);
        var bayer = format == CameraPixelFormat.BayerRggb16;
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var storage = baseline.Pipeline.Steps.Single(step => step.Type == "Storage");
        var readout = bayer
            ? null
            : new SensorReadoutProfile(new SensorCrop(0, 0, size, size), 1, 1, FrameBinningAlgorithm.IdentityV1,
                CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1,
                FrameLevelCodeSpace.NativeSample, 0, 4095, size * sizeof(ushort));
        return baseline with
        {
            Observatory = new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
            CenteredExposure = new("virtual-qual", CenteredExposureProfile.CurrentExecutionVersion),
            DeploymentLocation = DeploymentLocationSnapshot.Create("virtual-qual-site", 1, "virtual",
                null, DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix"),
            Rig = baseline.Rig with
            {
                Sensor = new SensorProfile(
                    bayer ? "VirtualAsi178McRaw16" : "VirtualAsi174Mm", size, size, 5.86,
                    bayer ? SensorColorMode.Color : SensorColorMode.Mono,
                    format, bayer ? SensorResponseMode.BayerRaw : SensorResponseMode.Monochrome,
                    size * sizeof(ushort), SampleByteOrder.LittleEndian),
                Optics = new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    size / 2.0, size / 2.0, 0.98 * size / 2.0),
                Orientation = new RigOrientation(90, 0, 0),
                Readout = readout
            },
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 1921,
                fixedSceneUtc = start,
                illuminationMode = "ControlledNight",
                magnitudeZeroElectronsPerSecond = 14000,
                asi178Sensor = new { enabled = bayer }
            })),
            Pipeline = baseline.Pipeline with
            {
                Steps =
                [
                    new("Calibration", "linear", DependsOn: ["$raw"], Required: false, Publication: durable,
                        Options: JsonSerializer.SerializeToElement(new { strategy = "None", outputVariant = "qualified-linear" })),
                    new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false, Publication: durable),
                    storage with { DependsOn = ["$raw"] }
                ]
            }
        };
    }

    private static InMemoryCelestialCatalog CreateVirtualSkyChromaticWideCatalog(
        DateTimeOffset start, CameraModuleConfig profile)
    {
        PixelPoint[] offsets =
        [
            new(30, -53), new(62, -18), new(-16, 21), new(-45, -38), new(-16, -19), new(28, 52),
            new(-60, -22), new(-61, 18), new(-49, 43), new(-22, 45), new(15, -40), new(4, 41), new(41, 11), new(20, 6),
            new(-70, -49), new(-97, -5), new(-21, -94), new(73, 46), new(54, 74), new(-89, -23), new(36, -86), new(-96, 31),
            new(13, -98), new(-81, 52), new(-38, 79), new(-82, 16), new(58, -64), new(68, -41), new(20, 87), new(1, 98)
        ];
        var projection = RigProjectionContextFactory.CreateNative(profile.Rig);
        var projector = ProjectorFactory.Create(projection);
        var entries = offsets.Select((offset, index) =>
        {
            // Index 0 is an unsaturated warm (red, B-V=+2.0) star; index 1 is an unsaturated cool (blue, B-V=-0.4) star.
            var colorIndex = index == 0 ? 2.0 : index == 1 ? -0.4 : 0.3 + index % 11 * 0.05;
            var magnitude = index == 0 ? 0.2 : index == 1 ? 1.4 : 0.4 + index * 0.025;
            return (Offset: offset, Name: $"qual-wide-{index}", Magnitude: magnitude, ColorIndex: colorIndex);
        }).ToList();
        // Add tight unsaturated chromatic companions around both warm (30,-53) and cool (62,-18) targets
        // so both span a full 8x8 JPEG chroma block without saturating to white.
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }
                entries.Add((new PixelPoint(30 + dx, -53 + dy), $"qual-warm-{dx}_{dy}", 0.4, 2.0));
                entries.Add((new PixelPoint(62 + dx, -18 + dy), $"qual-cool-{dx}_{dy}", 0.2, -0.4));
            }
        }

        return new(entries.Select(entry =>
        {
            var direction = projector.Unproject(new(projection.PrincipalPointX + entry.Offset.X, projection.PrincipalPointY + entry.Offset.Y));
            Assert.IsNotNull(direction);
            var ofDate = CoordinateTransforms.HorizontalToEquatorial(direction.Value,
                start, profile.Observatory.LatitudeDegrees, profile.Observatory.LongitudeDegrees);
            var star = EquatorialPrecession.PrecessToJ2000(ofDate, start);
            return new CelestialCatalogObject(entry.Name, entry.Name, star.RightAscensionHours, star.DeclinationDegrees,
                entry.Magnitude, entry.ColorIndex);
        }));
    }

    private static async Task<CenteredScheduleBinding> ReadScheduleAsync(
        ServiceProvider provider, CancellationToken token)
    {
        var runtime = provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        var grant = (await runtime.WaitForGrantAsync(wait.Token).ConfigureAwait(false)).Evidence;
        return new(grant.ScheduleRevisionSha256, grant.LocalProfileSha256, grant.SetpointProfileId, grant.IntervalId,
            grant.OverrideId, CaptureCadenceMode.MinimumStartInterval);
    }

    private static async Task<RawCaptureReceipt> AdmitSyntheticExposureAsync(
        RawCaptureIngress ingress,
        QualificationClock clock,
        CameraModuleConfig active,
        CenteredScheduleBinding schedule,
        DateTimeOffset scheduledUtc,
        FrameLayoutDescriptor layout,
        byte[] rawBytes,
        string sourceId,
        CancellationToken token,
        TimeSpan? exposureDuration = null,
        TimeSpan? cadence = null)
    {
        var effectiveExposure = exposureDuration ?? ExposureDuration;
        var effectiveCadence = cadence ?? Cadence;
        clock.UtcNow = scheduledUtc;
        var reservation = await ingress.ReserveCenteredAsync(
            active, new(null, null, null, null, "qual-1928"), null, token, schedule: schedule)
            .ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(effectiveExposure, 10, null, null);
        var request = new CaptureRequest(scheduledUtc, effectiveCadence, CaptureMode.Still, setpoint)
        {
            BoundGraphRevisionId = reservation.Revision.State.RevisionId,
            BoundGraph = reservation.Revision.Pipeline
        };
        await ingress.DispatchCenteredAsync(
            reservation.Pending.Key, setpoint, null, "qual-1928", token, scheduledUtc, effectiveCadence)
            .ConfigureAwait(false);

        var exposureEnd = scheduledUtc.Add(effectiveExposure);
        var readoutEnd = exposureEnd.AddMilliseconds(10);
        var frame = new CameraFrame(
            scheduledUtc, layout.Width, layout.Height, layout.PixelFormat, rawBytes,
            new FrameMetadata(effectiveExposure, 10, -10, SourceId: sourceId),
            layout.StrideBytes)
        {
            Layout = layout
        };
        var result = new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
        {
            AcquisitionTiming = new(scheduledUtc, exposureEnd, readoutEnd)
        };
        clock.UtcNow = readoutEnd.AddMilliseconds(1);
        return await ingress.AcceptCenteredAsync(
            active,
            new(request, result, scheduledUtc, effectiveCadence, readoutEnd - scheduledUtc),
            reservation, token).ConfigureAwait(false);
    }

    private static async Task ProcessStandardLaneAsync(
        ServiceProvider provider, CameraModuleConfig config, CancellationToken token)
    {
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
        var lease = await laneStore.ClaimAsync(lane, "qual-1928", config, token).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var result = await provider.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, token)
            .ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, token).ConfigureAwait(false);
    }

    private static async Task<DecodedImage> DecodePublishedPreviewAsync(
        ICenteredViewPresentationReader reader, CenteredViewArchiveBase archiveBase, CancellationToken token)
    {
        var opened = await reader.OpenPreviewAsync(archiveBase.Base.BaseId, archiveBase.LatestRevisionId, token)
            .ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, opened.Status);
        Assert.IsNotNull(opened.Content);
        await using var lease = opened.Content.ConfigureAwait(false);
        var bytes = new byte[checked((int)opened.Content.ByteLength)];
        await opened.Content.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        Assert.AreEqual(archiveBase.Base.Definition.Preview.PayloadSha256, PayloadChecksum.ComputeSha256(bytes));
        return JpegImageCodec.DecodeJpeg(bytes, token);
    }

    private static async Task PublishMovingTrailOnSourceAsync(
        CaptureProcessingPersistence persistence,
        CenteredViewLayerProducer layerProducer,
        ExposureKey sourceKey,
        ReconstructionDescriptor rawDescriptor,
        FrameLayoutDescriptor layout,
        PixelPoint start,
        PixelPoint end,
        CancellationToken token)
    {
        var rawArtifact = new ProcessingArtifact(
            rawDescriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, rawDescriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(rawDescriptor.Artifact.Recipe).IdentitySha256,
            rawDescriptor.Artifact.MediaType, layout, Array.Empty<byte>(),
            rawDescriptor.Artifact.CreatedUtc, rawDescriptor.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(rawDescriptor))
        {
            CaptureId = sourceKey.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(rawDescriptor)
        };
        var payload = PresentationLayerPayloadJson.Create(
            rawArtifact.DescriptorIdentitySha256!, layout.Width, layout.Height,
            segments: [new(start, end, 2, new(255, 210, 90))]);
        var product = PresentationProcessingProducts.CreateLayerProduct(
            payload, "qual-moving-trail-source", [rawArtifact], "qual-trail-v1");
        await persistence.PersistCenteredViewProductAsync(rawDescriptor, product, token).ConfigureAwait(false);
        var slot = new ExposureProductSlot(Guid.NewGuid(), "measured-trails", payload.SchemaVersion, product.Variant);
        var productRef = new ExposureProductRef(
            sourceKey, slot, ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            product.OutputIdentitySha256, product.ChecksumSha256, payload.ContentIdentitySha256);
        var lookup = new ExposureProductLookup(sourceKey, slot, ExposureProductAvailability.Available, 1, productRef, null);
        await layerProducer.PublishToContributorsAsync(
            lookup, new(TransientTrailPresentation.LayerKind, 500, true),
            rawDescriptor.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
    }

    private static async Task<(double X1, double Y1, double X2, double Y2)> ReadTrailSvgLineAsync(
        ICenteredViewPresentationReader reader, string baseId, string revisionId, CancellationToken token)
    {
        var rendered = await reader.ReadAsync(baseId, revisionId, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
        Assert.IsNotNull(rendered.Presentation);
        var svg = XDocument.Parse(Encoding.UTF8.GetString(rendered.Presentation.Svg.Span));
        var trailGroup = svg.Descendants().Single(element =>
            element.Name.LocalName == "g" &&
            string.Equals(element.Attribute("data-layer-kind")?.Value, TransientTrailPresentation.LayerKind, StringComparison.Ordinal));
        var line = trailGroup.Descendants().Single(element => element.Name.LocalName == "line");
        return (
            double.Parse(line.Attribute("x1")!.Value, CultureInfo.InvariantCulture),
            double.Parse(line.Attribute("y1")!.Value, CultureInfo.InvariantCulture),
            double.Parse(line.Attribute("x2")!.Value, CultureInfo.InvariantCulture),
            double.Parse(line.Attribute("y2")!.Value, CultureInfo.InvariantCulture));
    }

    private static async Task<ReconstructionDescriptor> CommitRawArtifactAsync(
        string root,
        SqliteRawCaptureJournal journal,
        FrameLayoutDescriptor layout,
        byte[] payload,
        DateTimeOffset startedUtc,
        CancellationToken token)
    {
        var captureId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var identity = await journal.ReserveIdentityAsync("test-agent", captureId, artifactId, token)
            .ConfigureAwait(false);
        var template = ReconstructableCaptureContractTests.CreateManifest(
            layout.PixelFormat, layout.Width, layout.Height, layout.StrideBytes, payload);
        var descriptor = template.Descriptor with
        {
            Layout = layout,
            Capture = template.Descriptor.Capture with
            {
                AgentId = identity.AgentId,
                CaptureSequence = identity.CaptureSequence,
                CaptureId = identity.CaptureId
            },
            Timing = template.Descriptor.Timing with
            {
                RequestedStartUtc = startedUtc,
                ExposureStartedUtc = startedUtc,
                ExposureEndedUtc = startedUtc.Add(ExposureDuration),
                ReadoutCompletedUtc = startedUtc.Add(ExposureDuration),
                DurableIngressUtc = startedUtc.Add(ExposureDuration)
            },
            Artifact = template.Descriptor.Artifact with
            {
                ArtifactId = identity.ArtifactId,
                MediaType = layout.PixelFormat == CameraPixelFormat.BayerRggb16
                    ? "application/x-skymonitor-bayer-rggb16"
                    : "application/x-skymonitor-mono16",
                ChecksumSha256 = PayloadChecksum.ComputeSha256(payload)
            }
        };
        var relativePath = $"frames/{artifactId:N}.bin";
        var sidecarRelativePath = $"frames/{artifactId:N}.json";
        var manifest = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath);
        var sidecar = CaptureContractJson.Serialize(manifest);
        var payloadPath = Path.Combine(root, relativePath);
        var sidecarPath = Path.Combine(root, sidecarRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        await File.WriteAllBytesAsync(payloadPath, payload, token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(sidecarPath, sidecar, token).ConfigureAwait(false);
        await journal.CommitAsync(new RawIngressJournalEntry(
            identity.AgentId, identity.CaptureSequence, identity.CaptureId, identity.ArtifactId,
            CaptureContractJson.ComputeDescriptorSha256(descriptor),
            CaptureContractJson.ComputeManifestSha256(sidecar),
            descriptor.Artifact.ChecksumSha256, descriptor.Layout.ByteLength,
            relativePath, sidecarRelativePath, sidecar,
            descriptor.Timing.ExposureStartedUtc, descriptor.Timing.DurableIngressUtc), token)
            .ConfigureAwait(false);
        return descriptor;
    }

    [TestMethod]
    public async Task CenteredPipelineQualification_CorpusReplayHelpersVerifyProvenanceOraclesAndPartialRootsAsync()
    {
        var token = TestContext.CancellationToken;
        var corpusRoot = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-1928-corpus-contract");
        try
        {
            // 1. Stage a geometry/-only partial corpus root first and verify BOTH mono-november AND the combined
            // raw-admission entry point report Assert.Inconclusive cleanly (no DirectoryNotFoundException), while
            // Bayer geometry passes full 3-channel RGB color + displacement oracles across stacks 103 & 104!
            var geometryDir = Path.Combine(corpusRoot, "geometry");
            Directory.CreateDirectory(geometryDir);
            var bayerFrameObjects = new List<object>();
            for (var i = 0; i < SequenceShifts.Length; i++)
            {
                var (dx, dy) = SequenceShifts[i];
                var file = $"bayer-{101 + i}.bin";
                var bytes = RenderSyntheticFrame(
                    CameraPixelFormat.BayerRggb16, 1, 1, dx, dy, FieldStars, includeClippedPatch: false);
                await File.WriteAllBytesAsync(Path.Combine(geometryDir, file), bytes, token).ConfigureAwait(false);
                bayerFrameObjects.Add(new
                {
                    file,
                    frame = 101 + i,
                    sha256 = PayloadChecksum.ComputeSha256(bytes),
                    inputPositionMinusReference103 = new[] { -dx, -dy }
                });
            }
            int[] oddOrigin = [1, 1];
            var oracleJson = JsonSerializer.Serialize(new
            {
                width = Width,
                height = Height,
                sampleDepthBits = 12,
                containerDepthBits = 16,
                blackLevel = BlackLevel,
                whiteLevel = WhiteLevel,
                backgroundAdu = BackgroundAdu,
                byteOrder = "little",
                bayerOriginOnRggbSensor = oddOrigin,
                bayerInDeliveredImage = "BGGR",
                sourcesAt103 = new[]
                {
                    new[] { RedTarget.X, RedTarget.Y },
                    new[] { GreenTarget.X, GreenTarget.Y },
                    new[] { BlueTarget.X, BlueTarget.Y }
                },
                knownSource105ToCenter103 = new[]
                {
                    SequenceShifts[4].Dx - SequenceShifts[2].Dx,
                    SequenceShifts[4].Dy - SequenceShifts[2].Dy
                },
                knownSource105ToCenter104 = new[]
                {
                    SequenceShifts[4].Dx - SequenceShifts[3].Dx,
                    SequenceShifts[4].Dy - SequenceShifts[3].Dy
                },
                frames = bayerFrameObjects
            });
            await File.WriteAllTextAsync(Path.Combine(geometryDir, "geometry-oracle.json"), oracleJson, token)
                .ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<AssertInconclusiveException>(async () =>
                await RunRetainedNovemberMonoEndToEndAsync(corpusRoot, token).ConfigureAwait(false))
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<AssertInconclusiveException>(async () =>
                await CenteredCorpusEvidenceTests.RunCombinedRawAdmissionOrInconclusiveAsync(corpusRoot).ConfigureAwait(false))
                .ConfigureAwait(false);
            await RunRetainedBayerGeometryEndToEndAsync(corpusRoot, token).ConfigureAwait(false);

            // 2. Stage mono-november/expected-mono.json with a 20-second study capture, original captureId,
            // and unconditional raw brightness stats, and verify RunRetainedNovemberMonoEndToEndAsync preserves
            // all provenance facts directly on the reopened durable descriptor!
            var monoDir = Path.Combine(corpusRoot, "mono-november");
            Directory.CreateDirectory(monoDir);
            var monoBytes = RenderSyntheticFrame(
                CameraPixelFormat.Mono16, 0, 0, 0, 0, FieldStars, includeClippedPatch: false);
            await File.WriteAllBytesAsync(Path.Combine(monoDir, "study-mono-1.bin"), monoBytes, token).ConfigureAwait(false);
            var monoStats = ImageStatisticsCalculator.Calculate(
                Width, Height, Width * 2, CameraPixelFormat.Mono16, monoBytes, (ushort)WhiteLevel, token);
            var monoManifestJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    artifact = "study-mono-1.bin",
                    captureId = Guid.Parse("19280001-1111-4111-8111-192800010001"),
                    rawSha256 = PayloadChecksum.ComputeSha256(monoBytes),
                    width = Width,
                    height = Height,
                    blackLevel = (double)BlackLevel,
                    whiteLevel = (double)WhiteLevel,
                    exposureSeconds = 20.0,
                    pixelCount = monoStats.SampleCount,
                    minAdu = monoStats.Minimum,
                    maxAdu = monoStats.Maximum,
                    sumAdu = monoStats.Sum,
                    zeroCount = monoStats.ZeroCount,
                    whiteLevelCount = monoStats.SaturatedCount
                }
            });
            await File.WriteAllTextAsync(Path.Combine(monoDir, "expected-mono.json"), monoManifestJson, token)
                .ConfigureAwait(false);

            await RunRetainedNovemberMonoEndToEndAsync(corpusRoot, token).ConfigureAwait(false);

            // Reject missing, null, and non-20-second exposures before admission, including a sub-tick difference.
            var invalidManifest = JsonNode.Parse(monoManifestJson)!.AsArray();
            var invalidEntry = invalidManifest[0]!.AsObject();
            (string? ExposureJson, string ExpectedDiagnostic)[] invalidExposures =
            [
                (null, "must explicitly declare numeric exposureSeconds"),
                ("null", "must explicitly declare numeric exposureSeconds"),
                ("19.0", "must declare exposureSeconds=20"),
                ("20.0000000005", "must declare exposureSeconds=20")
            ];
            foreach (var (exposureJson, expectedDiagnostic) in invalidExposures)
            {
                if (exposureJson is null)
                {
                    invalidEntry.Remove("exposureSeconds");
                }
                else
                {
                    invalidEntry["exposureSeconds"] = JsonNode.Parse(exposureJson);
                }

                await File.WriteAllTextAsync(Path.Combine(monoDir, "expected-mono.json"), invalidManifest.ToJsonString(), token)
                    .ConfigureAwait(false);
                var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
                    () => RunRetainedNovemberMonoEndToEndAsync(corpusRoot, token),
                    $"Expected rejection for exposureSeconds {exposureJson ?? "(omitted)"}.")
                    .ConfigureAwait(false);
                StringAssert.Contains(failure.Message, expectedDiagnostic, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(corpusRoot, recursive: true);
        }
    }

    internal static async Task RunRetainedNovemberMonoEndToEndAsync(string evidenceRoot, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRoot);
        var monoDir = Path.Combine(evidenceRoot, "mono-november");
        var manifestPath = Path.Combine(monoDir, "expected-mono.json");
        if (!File.Exists(manifestPath))
        {
            Assert.Inconclusive($"Retained November Mono16 corpus manifest not found at '{manifestPath}'.");
        }

        // Each entry in mono-november/expected-mono.json is a separate 20-second study capture carrying its
        // original captureId, layout, and raw brightness statistics (without original wall-clock capture
        // timestamps/cadence). Replay each entry in an isolated session using explicit replay-harness
        // submission timestamps while preserving and asserting the manifest's original captureId, 20-second
        // exposure duration, layout, raw brightness oracle, and bit-for-bit reopened raw SHA-256 directly on
        // the durable record consumed by processing.
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false));
        var entries = manifest.RootElement.EnumerateArray().ToArray();
        Assert.IsGreaterThan(0, entries.Length, "mono-november/expected-mono.json must contain at least one entry.");

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var artifact = entry.GetProperty("artifact").GetString()!;
            var originalCaptureId = entry.GetProperty("captureId").GetGuid();
            Assert.AreNotEqual(Guid.Empty, originalCaptureId, $"Manifest entry {artifact} must declare an original captureId.");
            var payload = await File.ReadAllBytesAsync(Path.Combine(monoDir, artifact), token).ConfigureAwait(false);
            var expectedSha = entry.GetProperty("rawSha256").GetString()!.ToUpperInvariant();
            Assert.AreEqual(expectedSha, PayloadChecksum.ComputeSha256(payload), $"Corpus checksum mismatch for {artifact}.");

            var width = entry.GetProperty("width").GetInt32();
            var height = entry.GetProperty("height").GetInt32();
            var whiteLevel = entry.GetProperty("whiteLevel").GetDouble();
            var expectedPixelCount = entry.GetProperty("pixelCount").GetInt64();
            var expectedMinAdu = entry.GetProperty("minAdu").GetUInt16();
            var expectedMaxAdu = entry.GetProperty("maxAdu").GetUInt16();
            var expectedSumAdu = entry.GetProperty("sumAdu").GetUInt64();
            var expectedZeroCount = entry.GetProperty("zeroCount").GetInt64();
            var expectedWhiteLevelCount = entry.GetProperty("whiteLevelCount").GetInt64();

            var expectedStudyExposure = TimeSpan.FromSeconds(20);
            var exposureDuration = RequireExpectedManifestExposureDuration(entry, artifact, expectedStudyExposure);
            // Deterministic replay-harness submission clock & non-overlapping cadence (not claimed from manifest unless present).
            var replaySubmissionUtc = ResolveManifestStartedUtc(entry, BaseUtc.AddHours(index));
            var replayHarnessCadence = expectedStudyExposure + TimeSpan.FromSeconds(5);

            var monoLayout = new FrameLayoutDescriptor(
                width, height, width * 2, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
                12, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
                entry.GetProperty("blackLevel").GetDouble(), whiteLevel, payload.Length)
            {
                StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
                Readout = new FrameReadoutDescriptor(
                    width, height, 0, 0, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null)
            };

            var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-1928-mono-study-{index}");
            try
            {
                var clock = new QualificationClock { UtcNow = replaySubmissionUtc };
                using var provider = CreateProvider(root, clock);
                var profile = CreateProfile(root, $"corpus-mono-study-{index}", monoLayout);
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile)
                    .ConfigureAwait(false);
                var schedule = await ReadScheduleAsync(provider, token).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var processing = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
                var exposureProducts = provider.GetRequiredService<IExposureProductReader>();
                var artifactService = provider.GetRequiredService<ICameraAgentArtifactService>();
                var singlePublisher = provider.GetRequiredService<CenteredViewPublicationWorker>();
                var views = provider.GetRequiredService<SqliteCenteredViews>();
                var reader = provider.GetRequiredService<ICenteredViewPresentationReader>();

                // Replay this separate 20-second study capture through centered admission -> linear -> measurement -> single publication.
                var expectedSourceTag = $"mono-november:{originalCaptureId:D}:{artifact}";
                var receipt = await AdmitSyntheticExposureAsync(
                    ingress, clock, active, schedule, replaySubmissionUtc, monoLayout, payload, expectedSourceTag, token,
                    exposureDuration: exposureDuration, cadence: replayHarnessCadence).ConfigureAwait(false);
                var descriptor = receipt.Manifest.Descriptor;
                Assert.AreEqual(expectedSha, descriptor.Artifact.ChecksumSha256);
                Assert.AreEqual(expectedSourceTag, descriptor.Artifact.SourceId);
                Assert.AreEqual(monoLayout, descriptor.Layout);
                Assert.AreEqual(expectedStudyExposure, descriptor.Controls.EffectiveExposure);
                Assert.AreEqual(replaySubmissionUtc, descriptor.Timing.ExposureStartedUtc);
                Assert.AreEqual(expectedStudyExposure, descriptor.Timing.ExposureEndedUtc - descriptor.Timing.ExposureStartedUtc);

                var stream = CenteredViewLookup.ResolveStream(
                    await ingress.ReadCurrentAsync(new(active.AgentId!, $"corpus-mono-study-{index}"), token).ConfigureAwait(false));
                Assert.IsNotNull(stream);
                var key = new ExposureKey(stream.Camera, stream.RunId, descriptor.Capture.CaptureId);

                // Reopen the durable raw manifest actually consumed by processing and assert original manifest captureId,
                // 20-second exposure duration, layout, and raw bytes + brightness oracle unconditionally!
                var reopenedRaw = await ingress.ReadCenteredRawAsync(key, token).ConfigureAwait(false);
                Assert.IsNotNull(reopenedRaw);
                Assert.AreEqual(expectedSha, reopenedRaw.Manifest.Descriptor.Artifact.ChecksumSha256);
                Assert.AreEqual(expectedSourceTag, reopenedRaw.Manifest.Descriptor.Artifact.SourceId,
                    "Reopened durable raw descriptor must preserve the original manifest captureId provenance tag.");
                StringAssert.Contains(reopenedRaw.Manifest.Descriptor.Artifact.SourceId, originalCaptureId.ToString("D"),
                    StringComparison.Ordinal);
                Assert.AreEqual(expectedStudyExposure, reopenedRaw.Manifest.Descriptor.Controls.EffectiveExposure);
                Assert.AreEqual(replaySubmissionUtc, reopenedRaw.Manifest.Descriptor.Timing.ExposureStartedUtc);
                Assert.AreEqual(expectedStudyExposure,
                    reopenedRaw.Manifest.Descriptor.Timing.ExposureEndedUtc - reopenedRaw.Manifest.Descriptor.Timing.ExposureStartedUtc);
                Assert.AreEqual(monoLayout, reopenedRaw.Manifest.Descriptor.Layout);

                var reopenedBytes = await ReadReopenedArtifactBytesAsync(artifactService, descriptor.Artifact.ArtifactId, payload.Length, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(expectedSha, PayloadChecksum.ComputeSha256(reopenedBytes));
                CollectionAssert.AreEqual(payload, reopenedBytes);

                var reopenedStats = ImageStatisticsCalculator.Calculate(
                    monoLayout.Width, monoLayout.Height, monoLayout.StrideBytes, CameraPixelFormat.Mono16,
                    reopenedBytes, checked((ushort)Math.Round(whiteLevel)), token);
                Assert.AreEqual(expectedPixelCount, reopenedStats.SampleCount);
                Assert.AreEqual(expectedMinAdu, reopenedStats.Minimum);
                Assert.AreEqual(expectedMaxAdu, reopenedStats.Maximum);
                Assert.AreEqual(expectedSumAdu, reopenedStats.Sum);
                Assert.AreEqual(expectedZeroCount, reopenedStats.ZeroCount);
                Assert.AreEqual(expectedWhiteLevelCount, reopenedStats.SaturatedCount);

                await ProcessStandardLaneAsync(provider, active, token).ConfigureAwait(false);
                var linear = await exposureProducts.ReadLinearAsync(key, token).ConfigureAwait(false);
                Assert.IsNotNull(linear?.Product);
                Assert.AreEqual(expectedSha, linear.Provenance!.RawPayloadSha256);

                var executionId = ProcessingGraphOperationsCoordinator.GetLiveExecutionId(key.CaptureId, stream.GenerationId);
                var sources = await processing.ReadCenteredViewSourcesAsync(key, executionId, 0, 24, token).ConfigureAwait(false);
                var measurementRef = Assert.ContainsSingle(sources.Where(item =>
                    item.Slot.SchemaVersion == ExposureMeasurementProducts.SchemaVersion)).Product!;
                var (_, measurement) = await persistence.ReadCenteredViewMeasurementAsync(
                    key.CaptureId,
                    new(measurementRef.ArtifactId, measurementRef.OutputIdentitySha256, measurementRef.PayloadSha256),
                    token).ConfigureAwait(false);
                Assert.AreEqual(ExposureMeasurementStatus.Usable, measurement.Sources.Status, measurement.Sources.Reason);
                Assert.IsNotEmpty(measurement.Sources.Detections);
                var stats = measurement.RawBrightness;
                Assert.IsNotNull(stats, $"ExposureMeasurement must report raw brightness statistics for {artifact}.");
                Assert.AreEqual(expectedPixelCount, stats.SampleCount);
                Assert.AreEqual(expectedMinAdu, stats.Minimum);
                Assert.AreEqual(expectedMaxAdu, stats.Maximum);
                Assert.AreEqual(expectedSumAdu, stats.Sum);
                Assert.AreEqual(expectedZeroCount, stats.ZeroCount);
                Assert.AreEqual(expectedWhiteLevelCount, stats.SensorClipping.Count);

                await singlePublisher.DrainCameraAsync(stream.Camera, stream, CancellationToken.None, token)
                    .ConfigureAwait(false);
                var single = (await views.ReadArchiveAsync(stream, descriptor.Capture.CaptureId, token)
                    .ConfigureAwait(false)).Single(item => item.Base.Definition.Kind == CenteredViewKind.Single);
                var decoded = await DecodePublishedPreviewAsync(reader, single, token).ConfigureAwait(false);
                Assert.AreEqual(monoLayout.Width, decoded.Width);
                Assert.AreEqual(monoLayout.Height, decoded.Height);
                AssertMonoPreviewEquality(decoded, $"corpus-mono-november-{artifact}");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    internal static async Task RunRetainedBayerGeometryEndToEndAsync(string evidenceRoot, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRoot);
        var geometryDir = File.Exists(Path.Combine(evidenceRoot, "geometry", "geometry-oracle.json"))
            ? Path.Combine(evidenceRoot, "geometry")
            : File.Exists(Path.Combine(evidenceRoot, "geometry-oracle.json"))
                ? evidenceRoot
                : null;
        if (geometryDir is null)
        {
            Assert.Inconclusive($"Retained Bayer geometry oracle not found under '{evidenceRoot}'.");
        }

        using var oracle = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(geometryDir, "geometry-oracle.json"), token).ConfigureAwait(false));
        var rootElem = oracle.RootElement;
        var oracleWidth = rootElem.GetProperty("width").GetInt32();
        var oracleHeight = rootElem.GetProperty("height").GetInt32();
        var sampleDepthBits = rootElem.GetProperty("sampleDepthBits").GetInt32();
        var containerDepthBits = rootElem.GetProperty("containerDepthBits").GetInt32();
        var blackLevel = rootElem.GetProperty("blackLevel").GetDouble();
        var whiteLevel = rootElem.GetProperty("whiteLevel").GetDouble();
        var backgroundAdu = rootElem.GetProperty("backgroundAdu").GetDouble();
        var byteOrderText = rootElem.GetProperty("byteOrder").GetString()!;
        var byteOrder = byteOrderText.Equals("little", StringComparison.OrdinalIgnoreCase)
            ? FrameByteOrder.LittleEndian
            : throw new AssertFailedException($"Unsupported oracle byteOrder '{byteOrderText}'.");
        var bayerOrigin = rootElem.GetProperty("bayerOriginOnRggbSensor");
        var cfaOriginX = bayerOrigin[0].GetInt32();
        var cfaOriginY = bayerOrigin[1].GetInt32();
        var expectedDeliveredPattern = Enum.Parse<BayerPattern>(
            rootElem.GetProperty("bayerInDeliveredImage").GetString()!, ignoreCase: true);

        var sourcesAt103 = rootElem.GetProperty("sourcesAt103").EnumerateArray()
            .Select(static row => new PixelPoint(row[0].GetDouble(), row[1].GetDouble()))
            .ToArray();
        Assert.IsGreaterThanOrEqualTo(3, sourcesAt103.Length, "geometry-oracle.json must provide R, G, and B targets in sourcesAt103.");

        var bayerEntries = rootElem.GetProperty("frames").EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("file", out var name) &&
                name.GetString()?.StartsWith("bayer-", StringComparison.Ordinal) == true)
            .OrderBy(static value => value.GetProperty("frame").GetInt32())
            .ToArray();
        Assert.IsGreaterThanOrEqualTo(6, bayerEntries.Length, "geometry-oracle.json must provide the complete 6-frame bayer sequence (101..106).");

        var bayerInputs = new List<(string Name, int FrameNumber, byte[] Bytes, string ExpectedSha256, double ShiftX, double ShiftY)>();
        foreach (var entry in bayerEntries)
        {
            var file = entry.GetProperty("file").GetString()!;
            var frameNumber = entry.GetProperty("frame").GetInt32();
            var shift = entry.GetProperty("inputPositionMinusReference103");
            var payload = await File.ReadAllBytesAsync(Path.Combine(geometryDir, file), token).ConfigureAwait(false);
            var expectedSha = entry.GetProperty("sha256").GetString()!.ToUpperInvariant();
            Assert.AreEqual(expectedSha, PayloadChecksum.ComputeSha256(payload), $"Corpus checksum mismatch for {file}.");
            bayerInputs.Add((file, frameNumber, payload, expectedSha, shift[0].GetDouble(), shift[1].GetDouble()));
        }

        int[] requiredFrames = [101, 102, 103, 104, 105, 106];
        foreach (var required in requiredFrames)
        {
            Assert.IsTrue(bayerInputs.Any(item => item.FrameNumber == required),
                $"Retained Bayer geometry corpus is missing required frame {required}.");
        }

        var bayerLayout = new FrameLayoutDescriptor(
            oracleWidth, oracleHeight, oracleWidth * 2, CameraPixelFormat.BayerRggb16, byteOrder,
            sampleDepthBits, containerDepthBits, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb,
            blackLevel, whiteLevel, bayerInputs[0].Bytes.Length)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(
                oracleWidth + cfaOriginX * 2, oracleHeight + cfaOriginY * 2,
                cfaOriginX, cfaOriginY, oracleWidth, oracleHeight, 1, 1,
                FrameBinningAlgorithm.IdentityV1, cfaOriginX, cfaOriginY)
        };
        Assert.AreEqual(expectedDeliveredPattern, BayerRggb16Demosaicer.ResolvePattern(bayerLayout));

        var frame104Entry = bayerInputs.Single(item => item.FrameNumber == 104);
        var sourcesAt104 = sourcesAt103
            .Select(pt => new PixelPoint(pt.X + frame104Entry.ShiftX, pt.Y + frame104Entry.ShiftY))
            .ToArray();

        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-1928-corpus-odd-origin-bayer");
        try
        {
            var clock = new QualificationClock { UtcNow = BaseUtc };
            using var provider = CreateProvider(root, clock);
            var profile = CreateProfile(root, "corpus-odd-origin-bayer", bayerLayout);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile)
                .ConfigureAwait(false);
            var schedule = await ReadScheduleAsync(provider, token).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var processing = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
            var exposureProducts = provider.GetRequiredService<IExposureProductReader>();
            var artifactService = provider.GetRequiredService<ICameraAgentArtifactService>();
            var singlePublisher = provider.GetRequiredService<CenteredViewPublicationWorker>();
            var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
            var registration = provider.GetRequiredService<CenteredWindowRegistrationWorker>();
            var windowPublisher = provider.GetRequiredService<CenteredViewWindowPublicationWorker>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var reader = provider.GetRequiredService<ICenteredViewPresentationReader>();

            CenteredViewStream? stream = null;
            var admitted = new Dictionary<int, (Guid CaptureId, ExposureKey Key)>();
            for (var index = 0; index < bayerInputs.Count; index++)
            {
                var (name, frameNumber, bytes, expectedSha, _, _) = bayerInputs[index];
                var scheduled = BaseUtc.AddTicks(Cadence.Ticks * index);
                var receipt = await AdmitSyntheticExposureAsync(
                    ingress, clock, active, schedule, scheduled, bayerLayout, bytes, name, token).ConfigureAwait(false);
                var descriptor = receipt.Manifest.Descriptor;
                Assert.AreEqual(expectedSha, descriptor.Artifact.ChecksumSha256);

                var reopenedBytes = await ReadReopenedArtifactBytesAsync(artifactService, descriptor.Artifact.ArtifactId, bytes.Length, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(expectedSha, PayloadChecksum.ComputeSha256(reopenedBytes));
                CollectionAssert.AreEqual(bytes, reopenedBytes);

                await ProcessStandardLaneAsync(provider, active, token).ConfigureAwait(false);
                stream ??= CenteredViewLookup.ResolveStream(
                    await ingress.ReadCurrentAsync(new(active.AgentId!, "corpus-odd-origin-bayer"), token).ConfigureAwait(false));
                Assert.IsNotNull(stream);
                var key = new ExposureKey(stream.Camera, stream.RunId, descriptor.Capture.CaptureId);
                admitted[frameNumber] = (descriptor.Capture.CaptureId, key);

                var linear = await exposureProducts.ReadLinearAsync(key, token).ConfigureAwait(false);
                Assert.IsNotNull(linear?.Product);
                Assert.AreEqual(expectedSha, linear.Provenance!.RawPayloadSha256);

                var executionId = ProcessingGraphOperationsCoordinator.GetLiveExecutionId(key.CaptureId, stream.GenerationId);
                var sources = await processing.ReadCenteredViewSourcesAsync(key, executionId, 0, 24, token).ConfigureAwait(false);
                var measurementRef = Assert.ContainsSingle(sources.Where(item =>
                    item.Slot.SchemaVersion == ExposureMeasurementProducts.SchemaVersion)).Product!;
                var (_, measurement) = await persistence.ReadCenteredViewMeasurementAsync(
                    key.CaptureId,
                    new(measurementRef.ArtifactId, measurementRef.OutputIdentitySha256, measurementRef.PayloadSha256),
                    token).ConfigureAwait(false);
                Assert.AreEqual(ExposureMeasurementStatus.Usable, measurement.Sources.Status, measurement.Sources.Reason);
                Assert.AreEqual(expectedDeliveredPattern, measurement.Sources.LocalBayerPattern);

                await singlePublisher.DrainCameraAsync(stream.Camera, stream, CancellationToken.None, token)
                    .ConfigureAwait(false);
                await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
                await registration.RegisterPendingAsync(token).ConfigureAwait(false);
                await windowPublisher.PublishReadyAsync(token).ConfigureAwait(false);

                var single = (await views.ReadArchiveAsync(stream, descriptor.Capture.CaptureId, token)
                    .ConfigureAwait(false)).Single(item => item.Base.Definition.Kind == CenteredViewKind.Single);
                var decodedSingle = await DecodePublishedPreviewAsync(reader, single, token).ConfigureAwait(false);
                Assert.AreEqual(bayerLayout.Width, decodedSingle.Width);
                Assert.AreEqual(bayerLayout.Height, decodedSingle.Height);
                if (frameNumber == 103)
                {
                    AssertOracleBayerJpegColors(decodedSingle, sourcesAt103, "corpus-bayer-single-103");
                }
                else if (frameNumber == 104)
                {
                    AssertOracleBayerJpegColors(decodedSingle, sourcesAt104, "corpus-bayer-single-104");
                }
            }

            Assert.IsNotNull(stream);

            // Unconditionally verify BOTH overlapping 5-frame registered stacks (centered on 103 and 104) against
            // ALL 3 color channels (R, G, B) in linear planes + decoded JPEGs AND known source-105 displacements!
            var cap103 = admitted[103];
            var cap104 = admitted[104];
            var cap105 = admitted[105];
            var stack103 = (await views.ReadArchiveAsync(stream, cap103.CaptureId, token).ConfigureAwait(false))
                .Single(item => item.Base.Definition.Kind == CenteredViewKind.Stack);
            var stack104 = (await views.ReadArchiveAsync(stream, cap104.CaptureId, token).ConfigureAwait(false))
                .Single(item => item.Base.Definition.Kind == CenteredViewKind.Stack);
            Assert.AreEqual(5, stack103.Base.Definition.Contributors.Count);
            Assert.AreEqual(5, stack104.Base.Definition.Contributors.Count);

            var science103 = await persistence.ReadCenteredViewRetainedProductAsync(
                cap103.CaptureId, stack103.Base.Definition.Science, token).ConfigureAwait(false);
            var evidence103 = CenteredRegisteredStackEvidence.Read(science103);
            var science104 = await persistence.ReadCenteredViewRetainedProductAsync(
                cap104.CaptureId, stack104.Base.Definition.Science, token).ConfigureAwait(false);
            var evidence104 = CenteredRegisteredStackEvidence.Read(science104);
            Assert.AreEqual(CenteredStackDisposition.Registered, evidence103.Disposition);
            Assert.AreEqual(CenteredStackDisposition.Registered, evidence104.Disposition);

            // Verify registered-stack displacement oracle against knownSource105ToCenter103 & knownSource105ToCenter104.
            var firstExpected = rootElem.GetProperty("knownSource105ToCenter103");
            var nextExpected = rootElem.GetProperty("knownSource105ToCenter104");
            var dxFirst = firstExpected[0].GetDouble();
            var dyFirst = firstExpected[1].GetDouble();
            var dxNext = nextExpected[0].GetDouble();
            var dyNext = nextExpected[1].GetDouble();
            var match103 = evidence103.Sources.Single(source => source.CaptureId == cap105.CaptureId);
            var match104 = evidence104.Sources.Single(source => source.CaptureId == cap105.CaptureId);
            Assert.AreEqual(CenteredRegistrationMethod.VerifiedCentroidSimilarity, match103.Method);
            Assert.AreEqual(CenteredRegistrationMethod.VerifiedCentroidSimilarity, match104.Method);
            var map105To103 = CenteredRegisteredStackRecipe.ReconstructSourceMap(
                evidence103, match103.Offset, new Dictionary<Guid, ProcessingArtifact>(), token)!;
            var map105To104 = CenteredRegisteredStackRecipe.ReconstructSourceMap(
                evidence104, match104.Offset, new Dictionary<Guid, ProcessingArtifact>(), token)!;
            foreach (var heldPosition in sourcesAt103)
            {
                var sampleOn105 = new PixelPoint(heldPosition.X - dxFirst, heldPosition.Y - dyFirst);
                var mapped103 = map105To103.SourceToReference(sampleOn105)!.Value;
                var mapped104 = map105To104.SourceToReference(sampleOn105)!.Value;
                Assert.AreEqual(heldPosition.X, mapped103.X, BayerCentroidTolerancePixels);
                Assert.AreEqual(heldPosition.Y, mapped103.Y, BayerCentroidTolerancePixels);
                Assert.AreEqual(sampleOn105.X + dxNext, mapped104.X, BayerCentroidTolerancePixels);
                Assert.AreEqual(sampleOn105.Y + dyNext, mapped104.Y, BayerCentroidTolerancePixels);
                Assert.IsGreaterThan(0.9, Math.Abs(mapped103.X - mapped104.X),
                    "Overlapping corpus stacks 103 and 104 must keep source 105 transforms separated.");
                Assert.AreEqual(dxFirst - dxNext, mapped103.X - mapped104.X, BayerCentroidTolerancePixels * 2,
                    "Overlapping corpus stack 103 vs 104 X separation must match oracle difference.");
            }

            // Score ALL 3 channels (R, G, and B) in linear registered planes AND decoded published JPEGs for BOTH stack 103 AND stack 104!
            await AssertOracleLinearStackRgbAsync(
                persistence, cap103.CaptureId, evidence103, bayerLayout.Width, backgroundAdu, sourcesAt103, "corpus-linear-stack-103", token)
                .ConfigureAwait(false);
            await AssertOracleLinearStackRgbAsync(
                persistence, cap104.CaptureId, evidence104, bayerLayout.Width, backgroundAdu, sourcesAt104, "corpus-linear-stack-104", token)
                .ConfigureAwait(false);

            var decodedStack103 = await DecodePublishedPreviewAsync(reader, stack103, token).ConfigureAwait(false);
            Assert.AreEqual(bayerLayout.Width, decodedStack103.Width);
            Assert.AreEqual(bayerLayout.Height, decodedStack103.Height);
            AssertOracleBayerJpegColors(decodedStack103, sourcesAt103, "corpus-bayer-stack-103");

            var decodedStack104 = await DecodePublishedPreviewAsync(reader, stack104, token).ConfigureAwait(false);
            Assert.AreEqual(bayerLayout.Width, decodedStack104.Width);
            Assert.AreEqual(bayerLayout.Height, decodedStack104.Height);
            AssertOracleBayerJpegColors(decodedStack104, sourcesAt104, "corpus-bayer-stack-104");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertOracleLinearStackRgbAsync(
        CaptureProcessingPersistence persistence,
        Guid referenceCaptureId,
        CenteredRegisteredStackEvidence evidence,
        int width,
        double backgroundAdu,
        PixelPoint[] targets,
        string label,
        CancellationToken token)
    {
        var redPin = evidence.Outputs.Single(output => output.Channel == "red");
        var greenPin = evidence.Outputs.Single(output => output.Channel == "green");
        var bluePin = evidence.Outputs.Single(output => output.Channel == "blue");
        Assert.AreNotEqual(redPin.PayloadSha256, greenPin.PayloadSha256, $"{label}: red/green planes must be distinct.");
        Assert.AreNotEqual(bluePin.PayloadSha256, greenPin.PayloadSha256, $"{label}: blue/green planes must be distinct.");
        Assert.AreNotEqual(redPin.PayloadSha256, bluePin.PayloadSha256, $"{label}: red/blue planes must be distinct.");

        var redPlane = await persistence.ReadCenteredViewRetainedProductAsync(
            referenceCaptureId, new(redPin.ArtifactId, redPin.OutputIdentitySha256, redPin.PayloadSha256), token)
            .ConfigureAwait(false);
        var greenPlane = await persistence.ReadCenteredViewRetainedProductAsync(
            referenceCaptureId, new(greenPin.ArtifactId, greenPin.OutputIdentitySha256, greenPin.PayloadSha256), token)
            .ConfigureAwait(false);
        var bluePlane = await persistence.ReadCenteredViewRetainedProductAsync(
            referenceCaptureId, new(bluePin.ArtifactId, bluePin.OutputIdentitySha256, bluePin.PayloadSha256), token)
            .ConfigureAwait(false);

        (double R, double G, double B) MeasureAboveBg(PixelPoint pt) => (
            Math.Max(0.0, MeasureLinearPatchMean(redPlane.Payload, width, pt, radius: 1) - backgroundAdu),
            Math.Max(0.0, MeasureLinearPatchMean(greenPlane.Payload, width, pt, radius: 1) - backgroundAdu),
            Math.Max(0.0, MeasureLinearPatchMean(bluePlane.Payload, width, pt, radius: 1) - backgroundAdu));

        var redTarget = MeasureAboveBg(targets[0]);
        var greenTarget = MeasureAboveBg(targets[1]);
        var blueTarget = MeasureAboveBg(targets[2]);

        Assert.IsGreaterThan(2.0 * redTarget.G, redTarget.R, $"{label}: target[0] expected R > 2*G, got {redTarget}.");
        Assert.IsGreaterThan(2.0 * redTarget.B, redTarget.R, $"{label}: target[0] expected R > 2*B, got {redTarget}.");
        Assert.IsGreaterThan(2.0 * greenTarget.R, greenTarget.G, $"{label}: target[1] expected G > 2*R, got {greenTarget}.");
        Assert.IsGreaterThan(2.0 * greenTarget.B, greenTarget.G, $"{label}: target[1] expected G > 2*B, got {greenTarget}.");
        Assert.IsGreaterThan(2.0 * blueTarget.R, blueTarget.B, $"{label}: target[2] expected B > 2*R, got {blueTarget}.");
        Assert.IsGreaterThan(2.0 * blueTarget.G, blueTarget.B, $"{label}: target[2] expected B > 2*G, got {blueTarget}.");
    }

    private static async Task<byte[]> ReadReopenedArtifactBytesAsync(
        ICameraAgentArtifactService artifactService, Guid artifactId, int expectedLength, CancellationToken token)
    {
        var openedRaw = await artifactService.OpenContentAsync(artifactId, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, openedRaw.Status);
        Assert.IsNotNull(openedRaw.Content);
        await using (openedRaw.Content.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream(expectedLength);
            await openedRaw.Content.CopyToAsync(buffer, token).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    private static TimeSpan RequireExpectedManifestExposureDuration(
        JsonElement entry, string artifact, TimeSpan expectedExposure)
    {
        Assert.IsTrue(
            entry.TryGetProperty("exposureSeconds", out var secondsProp) && secondsProp.ValueKind == JsonValueKind.Number,
            $"Manifest entry '{artifact}' must explicitly declare numeric exposureSeconds.");
        var exposureSeconds = secondsProp.GetDouble();
        Assert.AreEqual(
            expectedExposure.TotalSeconds,
            exposureSeconds,
            $"Manifest entry '{artifact}' must declare exposureSeconds={expectedExposure.TotalSeconds:0.###}, got {exposureSeconds}.");
        return TimeSpan.FromSeconds(exposureSeconds);
    }

    private static DateTimeOffset ResolveManifestStartedUtc(JsonElement entry, DateTimeOffset fallback)
    {
        foreach (var propertyName in new[] { "exposureStartedUtc", "capturedUtc", "startedUtc", "timestampUtc" })
        {
            if (entry.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(prop.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }
        return fallback;
    }

    private static void AssertOracleBayerJpegColors(DecodedImage decoded, PixelPoint[] targets, string label)
    {
        Assert.AreEqual(CameraPixelFormat.Rgb24, decoded.PixelFormat, $"{label}: Bayer preview must decode as Rgb24.");
        var redTarget = MeasureJpegPatch(decoded, targets[0], radius: 1);
        var greenTarget = MeasureJpegPatch(decoded, targets[1], radius: 1);
        var blueTarget = MeasureJpegPatch(decoded, targets[2], radius: 1);
        Assert.IsGreaterThan(redTarget.Green + 20, redTarget.Red, $"{label}: target[0] expected R > G + 20, got {redTarget}.");
        Assert.IsGreaterThan(redTarget.Blue + 20, redTarget.Red, $"{label}: target[0] expected R > B + 20, got {redTarget}.");
        Assert.IsGreaterThan(greenTarget.Red + 20, greenTarget.Green, $"{label}: target[1] expected G > R + 20, got {greenTarget}.");
        Assert.IsGreaterThan(greenTarget.Blue + 20, greenTarget.Green, $"{label}: target[1] expected G > B + 20, got {greenTarget}.");
        Assert.IsGreaterThan(blueTarget.Red + 20, blueTarget.Blue, $"{label}: target[2] expected B > R + 20, got {blueTarget}.");
        Assert.IsGreaterThan(blueTarget.Green + 20, blueTarget.Blue, $"{label}: target[2] expected B > G + 20, got {blueTarget}.");
    }

    private sealed class QualificationClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = BaseUtc;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Activated by CameraModuleFactory via dependency injection.")]
    private sealed class QualificationRetainedCameraModule : ICameraModule, ICameraBoundExposureCapture, ICameraSetpointController
    {
        public string Id => "qual-retained";
        public string DisplayName => "Qualification Retained Camera";
        public string ModuleType => "QualRetained";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public bool EnforcesBoundStartLimit => true;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Replay test supplies raw frames directly through admission.");
        public Task<CaptureResult> CaptureBoundAsync(ExposureAttemptKey attempt, CaptureRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Replay test supplies raw frames directly through admission.");
        public ValueTask<DateTimeOffset> ApplySetpointAsync(CaptureSetpoint setpoint, CancellationToken cancellationToken) =>
            ValueTask.FromResult(BaseUtc);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
