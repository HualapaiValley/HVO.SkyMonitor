using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Modules.VirtualSky")]
public sealed class VirtualSkyExtendedDeepSkyTests
{
    private static readonly DateTimeOffset FixtureUtc = new(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly ObservatoryLocation HvoLocation = new(35.347, -113.878, 1000, "America/Phoenix");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly DeepSkySemantics OpenNgcSemantics = new(
        "OpenNGC",
        "v20260501",
        "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
        new Uri("https://github.com/mattiaverga/OpenNGC"),
        "CC BY-SA 4.0",
        "equatorial-j2000-icrs-aligned",
        "J2000.0",
        "arcminute",
        "degrees-north-through-east-0-inclusive-to-180-exclusive",
        "1-widest-2-standard-3-narrowest",
        "b-mag-per-square-arcsecond-within-25-mag-isophote",
        new CatalogSnapshotIdentity("openngc-fixture", "openngc-fixture-1", "5", "6", new string('f', 64)));

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.Rgb24)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task VirtualSkyRendersDeterministicDeepSkyAcrossAllSensorFormatsAndPreservesOptOutBaseline(CameraPixelFormat format)
    {
        var catalog = CreateFixtureCatalog(includeHygStar: true);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null));

        // 1. Baseline (opt-out) capture.
        var baseModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore());
        await using var baseLifetime = baseModule.ConfigureAwait(false);
        await baseModule.InitializeAsync(CreateModuleConfig(format, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false), CancellationToken.None).ConfigureAwait(false);
        var baseline = (await baseModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
        Assert.IsFalse(baseline.Metadata.Extra!.ContainsKey("deepSkyRenderAlgorithm"));

        // 2. Opt-in deep-sky rendering without annotation layer vs with annotation layer (proving strict separation).
        var staging = new RecordingStagingStore();
        var rawOnlyModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore());
        await using var rawOnlyLifetime = rawOnlyModule.ConfigureAwait(false);
        await rawOnlyModule.InitializeAsync(CreateModuleConfig(format, 128, 128, renderDeepSky: true, enableDeepSkyAnnotations: false), CancellationToken.None).ConfigureAwait(false);
        var renderedRawOnly = (await rawOnlyModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        var withAnnotModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore(), stagingStore: staging);
        await using var withAnnotLifetime = withAnnotModule.ConfigureAwait(false);
        await withAnnotModule.InitializeAsync(CreateModuleConfig(format, 128, 128, renderDeepSky: true, enableDeepSkyAnnotations: true), CancellationToken.None).ConfigureAwait(false);
        var renderedWithAnnot = (await withAnnotModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        // 3. Repeat determinism check.
        var repeatModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore());
        await using var repeatLifetime = repeatModule.ConfigureAwait(false);
        await repeatModule.InitializeAsync(CreateModuleConfig(format, 128, 128, renderDeepSky: true, enableDeepSkyAnnotations: false), CancellationToken.None).ConfigureAwait(false);
        var repeatFrame = (await repeatModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        CollectionAssert.AreEqual(renderedRawOnly.PixelData.ToArray(), repeatFrame.PixelData.ToArray(),
            "Deep-sky rendering must be byte-identical across deterministic runs.");
        CollectionAssert.AreEqual(renderedRawOnly.PixelData.ToArray(), renderedWithAnnot.PixelData.ToArray(),
            "Presentation outlines/projected-scene annotations must never alter raw pixel bytes.");
        Assert.IsFalse(baseline.PixelData.Span.SequenceEqual(renderedRawOnly.PixelData.Span),
            "Enabling RenderDeepSkySources must add real physical signal to raw pixels.");
        Assert.AreNotEqual(baseline.Metadata.Scene!.SceneId, renderedRawOnly.Metadata.Scene!.SceneId);

        var extra = renderedRawOnly.Metadata.Extra!;
        Assert.AreEqual(DeepSkyRenderPlan.AlgorithmVersion, extra["deepSkyRenderAlgorithm"]);
        Assert.AreEqual(DeepSkySimulationParameters.CurrentSchemaVersion, extra["deepSkyProfileSchemaVersion"]);
        StringAssert.Contains(extra["renderAlgorithm"], DeepSkyRenderPlan.AlgorithmVersion, StringComparison.Ordinal);
        Assert.IsTrue(int.Parse(extra["deepSkyAdmittedCount"], CultureInfo.InvariantCulture) >= 3);
        Assert.IsTrue(long.Parse(extra["deepSkySampledPoints"], CultureInfo.InvariantCulture) > 0);
        Assert.IsTrue(int.Parse(extra["deepSkyRasterizedPixels"], CultureInfo.InvariantCulture) > 0);
        Assert.AreEqual(64, extra["deepSkyDiagnosticsSha256"].Length);
        Assert.AreEqual(extra["deepSkyDiagnosticsSha256"], repeatFrame.Metadata.Extra!["deepSkyDiagnosticsSha256"]);

        var stagedScene = staging.Scenes.Single();
        Assert.IsNotNull(stagedScene.DeepSky);
        Assert.IsTrue(stagedScene.DeepSky.Objects.Count >= 3);
        Assert.IsTrue(stagedScene.ResolvedFootprints.Any(static f => f.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject));
    }

    [TestMethod]
    public async Task ExplicitDisabledRenderDeepSkySourcesMatchesOmittedNullSceneIdAndRawBytesWithNoise()
    {
        // Exact F1 probe: with sensor noise enabled, omitted renderDeepSkySources (null) vs explicit
        // {"renderDeepSkySources":{"enabled":false}} (default or custom fields) MUST produce identical
        // SceneId, stellarNoiseIdentity, and raw pixel bytes!
        var catalog = CreateFixtureCatalog(includeHygStar: true);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null));

        var omittedConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false,
            readNoise: 3.0, shotNoiseEnabled: true);
        var explicitDisabledDefaultConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false,
            readNoise: 3.0, shotNoiseEnabled: true,
            explicitDisabledRenderOptions: new VirtualSkyDeepSkyRenderOptions(Enabled: false));
        var explicitDisabledCustomConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false,
            readNoise: 3.0, shotNoiseEnabled: true,
            explicitDisabledRenderOptions: new VirtualSkyDeepSkyRenderOptions(
                Enabled: false, MinimumSignalToNoise: 9.0, MaximumCandidates: 16, SamplesPerProjectedPixel: 4));

        var omittedModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore());
        await using var omittedLifetime = omittedModule.ConfigureAwait(false);
        await omittedModule.InitializeAsync(omittedConfig, CancellationToken.None).ConfigureAwait(false);
        var omittedFrame = (await omittedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        var disabledDefaultModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore());
        await using var disabledDefaultLifetime = disabledDefaultModule.ConfigureAwait(false);
        await disabledDefaultModule.InitializeAsync(explicitDisabledDefaultConfig, CancellationToken.None).ConfigureAwait(false);
        var disabledDefaultFrame = (await disabledDefaultModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        var disabledCustomModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore());
        await using var disabledCustomLifetime = disabledCustomModule.ConfigureAwait(false);
        await disabledCustomModule.InitializeAsync(explicitDisabledCustomConfig, CancellationToken.None).ConfigureAwait(false);
        var disabledCustomFrame = (await disabledCustomModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.AreEqual(omittedFrame.Metadata.Scene!.SceneId, disabledDefaultFrame.Metadata.Scene!.SceneId,
            "Explicit disabled RenderDeepSkySources must preserve exact omitted SceneId.");
        Assert.AreEqual(omittedFrame.Metadata.Scene!.SceneId, disabledCustomFrame.Metadata.Scene!.SceneId,
            "Explicit disabled RenderDeepSkySources with non-default fields must preserve exact omitted SceneId.");
        Assert.AreEqual(omittedFrame.Metadata.Extra!["stellarNoiseIdentity"], disabledDefaultFrame.Metadata.Extra!["stellarNoiseIdentity"]);
        Assert.AreEqual(omittedFrame.Metadata.Extra!["stellarNoiseIdentity"], disabledCustomFrame.Metadata.Extra!["stellarNoiseIdentity"]);
        Assert.IsFalse(disabledDefaultFrame.Metadata.Extra!.ContainsKey("deepSkyRenderAlgorithm"));
        Assert.IsNull(disabledDefaultFrame.Metadata.Scene!.DeepSkyCatalog);
        CollectionAssert.AreEqual(omittedFrame.PixelData.ToArray(), disabledDefaultFrame.PixelData.ToArray(),
            "Explicit disabled RenderDeepSkySources must preserve byte-identical raw pixels with sensor noise enabled.");
        CollectionAssert.AreEqual(omittedFrame.PixelData.ToArray(), disabledCustomFrame.PixelData.ToArray());
    }

    [TestMethod]
    public async Task SwappingDeepSkyCatalogSnapshotWithAnnotationsDisabledChangesSceneIdAndPreservesProvenance()
    {
        // Exact F2 probe: with annotations disabled, swapping a populated DSO catalog snapshot for an empty
        // differently-versioned snapshot MUST produce distinct SceneId, distinct raw pixels, and non-null
        // retained DeepSkyCatalog provenance matching each active snapshot!
        var populatedCatalog = CreateFixtureCatalog(includeHygStar: true);
        var emptySemantics = OpenNgcSemantics with
        {
            SnapshotIdentity = new CatalogSnapshotIdentity("openngc-empty", "openngc-empty-2", "5", "6", new string('a', 64))
        };
        var emptyDsoCatalog = new DeepSkyCatalog(emptySemantics, [], [], [], []);
        var emptyCatalog = new CombinedVirtualCatalog([], emptyDsoCatalog);
        var config = CreateModuleConfig(CameraPixelFormat.Mono16, 128, 128, renderDeepSky: true, enableDeepSkyAnnotations: false);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null));

        var popModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), populatedCatalog, new ProjectedSceneStore());
        await using var popLifetime = popModule.ConfigureAwait(false);
        await popModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var popFrame = (await popModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        var emptyModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), emptyCatalog, new ProjectedSceneStore());
        await using var emptyLifetime = emptyModule.ConfigureAwait(false);
        await emptyModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var emptyFrame = (await emptyModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.IsFalse(popFrame.PixelData.Span.SequenceEqual(emptyFrame.PixelData.Span),
            "Populated vs empty DSO catalog snapshot must produce different raw pixels.");
        Assert.AreNotEqual(popFrame.Metadata.Scene!.SceneId, emptyFrame.Metadata.Scene!.SceneId,
            "Distinct raw images from different DSO catalog snapshots must never share a SceneId.");
        Assert.AreNotEqual(popFrame.Metadata.Extra!["stellarNoiseIdentity"], emptyFrame.Metadata.Extra!["stellarNoiseIdentity"]);
        Assert.AreEqual(OpenNgcSemantics.SnapshotIdentity, popFrame.Metadata.Scene!.DeepSkyCatalog,
            "Active render DSO catalog snapshot provenance must be retained even when annotations are disabled.");
        Assert.AreEqual(emptySemantics.SnapshotIdentity, emptyFrame.Metadata.Scene!.DeepSkyCatalog);
    }

    [TestMethod]
    public async Task HostDeepSkyCandidateSelectionUsesBoundedCatalogQueryLimitAndBinnedHashMatchesDeliveredDiagnostics()
    {
        // F12 host query bound check: SelectDeepSkyRenderCandidates must never request int.MaxValue from IDeepSkyCatalog.Query.
        var baseCatalog = CreateFixtureCatalog(includeHygStar: true);
        var spyDeepSky = new RecordingDeepSkyCatalog(baseCatalog.DeepSky!);
        var spyCombined = new CombinedVirtualCatalog([], spyDeepSky);

        // F14 binned diagnostics hash check: 2x2 readout must publish deepSkyDiagnosticsSha256 computed over
        // the FINAL delivered (binned) diagnostics payload, distinct from native diagnostics hash.
        var baseBinnedConfig = CreateModuleConfig(CameraPixelFormat.Mono16, 128, 128, renderDeepSky: true, enableDeepSkyAnnotations: false);
        var binnedConfig = baseBinnedConfig with
        {
            Rig = baseBinnedConfig.Rig with
            {
                Readout = new SensorReadoutProfile(
                    new SensorCrop(0, 0, 128, 128),
                    BinX: 2,
                    BinY: 2,
                    FrameBinningAlgorithm.DigitalAverageV1,
                    CameraPixelFormat.Mono16,
                    SampleDepthBits: 16,
                    ContainerDepthBits: 16,
                    FrameSamplePacking.ByteAligned,
                    FrameStoredCodeTransform.IdentityV1,
                    FrameLevelCodeSpace.NativeSample,
                    BlackLevel: 100,
                    WhiteLevel: 65535,
                    StrideBytes: 128)
            }
        };

        var module = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), spyCombined, new ProjectedSceneStore());
        await using var lifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(binnedConfig, CancellationToken.None).ConfigureAwait(false);
        var frame = (await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null)),
            CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.IsNotEmpty(spyDeepSky.Queries);
        Assert.IsTrue(spyDeepSky.Queries.All(static q => q.MaximumResults > 0 && q.MaximumResults <= VirtualSkyCameraModule.MaximumDeepSkyCatalogQueryResults),
            $"Host catalog query must use a bounded limit <= {VirtualSkyCameraModule.MaximumDeepSkyCatalogQueryResults}; saw [{string.Join(", ", spyDeepSky.Queries.Select(static q => q.MaximumResults))}].");

        Assert.AreEqual(64, frame.Width);
        Assert.AreEqual(64, frame.Height);
        var extra = frame.Metadata.Extra!;
        var deliveredDiagnostics = JsonSerializer.Deserialize<DeepSkySourceDiagnostic[]>(extra["deepSkyDiagnostics"], WebJsonOptions)!;
        var recomputedHash = DeepSkyRenderPlan.ComputeDiagnosticsSha256(deliveredDiagnostics);
        Assert.AreEqual(recomputedHash, extra["deepSkyDiagnosticsSha256"],
            "Published deepSkyDiagnosticsSha256 must match a consumer's recomputation over delivered binned diagnostics.");
        Assert.IsTrue(extra.TryGetValue("deepSkyNativeDiagnosticsSha256", out var nativeHash));
        Assert.AreNotEqual(nativeHash, extra["deepSkyDiagnosticsSha256"],
            "Delivered binned diagnostics hash must differ from native-coordinate diagnostics hash.");
    }

    [TestMethod]
    public async Task MeasuredAssociationsStillComeStrictlyFromFinalPixels()
    {
        // Place one bright catalog star, one unresolved PN, and one large resolved diffuse nebula well separated in a 256x256 frame.
        var midpoint = FixtureUtc.AddSeconds(1);
        var starJ2000 = AltAzToJ2000(new AltAzPoint(60.0, 178.0), midpoint);
        var unresolvedPnJ2000 = AltAzToJ2000(new AltAzPoint(60.0, 180.0), midpoint);
        var resolvedNebulaJ2000 = AltAzToJ2000(new AltAzPoint(61.5, 181.5), midpoint);

        var star = new CelestialCatalogObject("HIP 999001", "BrightStar", starJ2000.RaHours, starJ2000.DecDegrees, 2.0, 0.2);
        var unresolvedPn = new DeepSkyObject("PN-UNRES", "PN-UNRES", "PN-UNRES", "PN",
            unresolvedPnJ2000.RaHours, unresolvedPnJ2000.DecDegrees, "Per",
            0.02, 0.02, 0, 2.5, 2.5, null, null, null, null, null, null);
        var resolvedNebula = new DeepSkyObject("NGC1976", "NGC 1976", "M42", "EmN",
            resolvedNebulaJ2000.RaHours, resolvedNebulaJ2000.DecDegrees, "Ori",
            70, 55, 25, 1.0, 0.5, null, null, 42, null, null, "Great Orion Nebula");

        var dsoCatalog = new DeepSkyCatalog(
            OpenNgcSemantics,
            [unresolvedPn, resolvedNebula],
            [
                new DeepSkyAlias(unresolvedPn.Designation, unresolvedPn.Id, DeepSkyAliasKinds.Designation),
                new DeepSkyAlias(resolvedNebula.Designation, resolvedNebula.Id, DeepSkyAliasKinds.Designation),
                new DeepSkyAlias("M42", resolvedNebula.Id, DeepSkyAliasKinds.Messier)
            ],
            [],
            []);
        var catalog = new CombinedVirtualCatalog([star], dsoCatalog);
        var module = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore());
        await using var lifetime = module.ConfigureAwait(false);
        var config = CreateModuleConfig(CameraPixelFormat.Mono16, 256, 256, renderDeepSky: true, enableDeepSkyAnnotations: false,
            boresightAlt: 60, boresightAz: 180, focalLengthPx: 2000, background: 50, readNoise: 2.0);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null)),
            CancellationToken.None).ConfigureAwait(false);
        var frame = result.Frame!;

        // Run StellarSourceMeasurer strictly on the final raw Mono16 pixels.
        var stride = frame.StrideBytes ?? frame.Width * 2;
        var pixelSpan = frame.PixelData.Span;
        var samples = new double[frame.Width * frame.Height];
        var validMask = new bool[samples.Length];
        var saturatedMask = new bool[samples.Length];
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var idx = y * frame.Width + x;
                var o = y * stride + x * 2;
                var adu = pixelSpan[o] | (pixelSpan[o + 1] << 8);
                samples[idx] = adu;
                validMask[idx] = true;
                saturatedMask[idx] = adu >= ushort.MaxValue;
            }
        }
        var measured = StellarSourceMeasurer.Measure(
            samples, validMask, saturatedMask, frame.Width, frame.Height,
            new StellarMeasurementOptions(
                BackgroundTileSizePixels: 64,
                MinimumPeakAboveBackground: 15,
                MaximumComponentSamples: 300,
                MaximumMinorSigma: 2.5));

        Assert.AreEqual(StellarMeasurementStatus.Completed, measured.Status);
        var diagnostics = JsonSerializer.Deserialize<DeepSkySourceDiagnostic[]>(
            frame.Metadata.Extra!["deepSkyDiagnostics"], WebJsonOptions)!;
        var pnDiag = diagnostics.Single(static d => d.ObjectId == "PN-UNRES");
        var nebDiag = diagnostics.Single(static d => d.ObjectId == "NGC1976");
        Assert.IsTrue(pnDiag.UsedUnresolvedPsfPath);
        Assert.IsFalse(nebDiag.UsedUnresolvedPsfPath);

        // Both the bright star and unresolved PN must be detected directly from pixels; the large diffuse nebula must be excluded as extended/broad, not detected as a star.
        var pnPixel = pnDiag.Footprint!.SourcePixel;
        var nebPixel = nebDiag.Footprint!.SourcePixel;
        Assert.IsTrue(measured.Detections.Any(d => double.Hypot(d.Pixel.X - pnPixel.X, d.Pixel.Y - pnPixel.Y) < 1.5),
            "Unresolved PN must be detected directly from rendered pixels as a stellar source.");
        Assert.IsFalse(measured.Detections.Any(d => double.Hypot(d.Pixel.X - nebPixel.X, d.Pixel.Y - nebPixel.Y) < 10.0),
            "Large resolved diffuse nebula must not be measured as a compact star.");
        Assert.IsTrue(measured.Exclusions.Any(e =>
                (e.ReasonCode is StellarExclusionReasons.ExtendedRegion or StellarExclusionReasons.TooBroad) &&
                double.Hypot(e.Peak.X - nebPixel.X, e.Peak.Y - nebPixel.Y) < 20.0),
            "Large resolved diffuse nebula must be excluded by pixel-based extended/broad measurer gates.");
    }

    [TestMethod]
    public async Task SqlitePersistenceAndReplayPreserveProjectedSceneV3WithDeepSkyOutlines()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"hvo-dso-v3-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var token = CancellationToken.None;
            var catalog = CreateFixtureCatalog(includeHygStar: false);
            var config = CreateModuleConfig(
                CameraPixelFormat.Mono16, 128, 128, renderDeepSky: true, enableDeepSkyAnnotations: true,
                annotationMaximumOutlines: 16) with
            {
                AgentId = "dso-v3-sqlite-agent"
            };
            var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));

            Guid captureId;
            Guid rawArtifactId;
            string activeRevisionId;
            string expectedSceneSha;
            byte[] expectedSceneBytes;
            DurableProcessingOutput committedOutput;

            using (var provider = CreateAgentProvider(tempRoot, catalog))
            {
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                await ingress.InitializeAsync(token).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
                activeRevisionId = registry.ActiveRevisionId;

                var module = new VirtualSkyCameraModule(
                    ExposureTestTimeProvider.ForRendering(),
                    provider.GetRequiredService<ICelestialCatalog>(),
                    provider.GetRequiredService<IProjectedSceneStore>(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(config, token).ConfigureAwait(false);

                var capture = await module.CaptureAsync(request, token).ConfigureAwait(false);
                Assert.AreEqual(SceneProvenance.DeepSkyProjectedSceneSchemaVersion,
                    capture.Frame!.Metadata.Scene!.ProjectedSceneSchemaVersion);

                var submission = new CaptureLoopSubmission(request, capture, request.RequestedStartUtc, request.TargetInterval, TimeSpan.Zero);
                var accepted = await ingress.AcceptAsync(config, submission, token).ConfigureAwait(false);
                Assert.IsNotNull(accepted);
                rawArtifactId = accepted.Manifest.Descriptor.Artifact.ArtifactId;

                var standardLane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static d => d.Name == "standard");
                var lease = await ingress.ClaimAsync(standardLane, "dso-v3-sqlite-test", config, token).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                captureId = lease.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId;
                var processed = await provider.GetRequiredService<StandardCaptureLaneHandler>()
                    .HandleAsync(lease.Context, token).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
                await ingress.CompleteAsync(lease, token).ConfigureAwait(false);

                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var canonicalCaptures = await store.ReadCanonicalSceneCapturesAsync([captureId], token).ConfigureAwait(false);
                Assert.IsTrue(canonicalCaptures.Contains(captureId), "SQLite must index projected-scene-v3 as a canonical scene capture.");

                var node = await store.ReadNodeAsync(captureId, "scene", token).ConfigureAwait(false);
                Assert.IsNotNull(node);
                committedOutput = node.Outputs.Single();
                expectedSceneBytes = await File.ReadAllBytesAsync(Path.Combine(tempRoot, committedOutput.PayloadRelativePath), token).ConfigureAwait(false);
                var parsed = ProjectedSceneJson.Parse(expectedSceneBytes);
                Assert.IsTrue(parsed.IsValid);
                Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, parsed.Scene!.SchemaVersion);
                Assert.IsTrue(parsed.Scene.DeepSky!.Objects.Count >= 3);
                Assert.IsTrue(parsed.Scene.DeepSky.Outlines.Count >= 1,
                    "V3 persisted scene should retain sourced OpenNGC outlines when configured.");
                Assert.IsTrue(parsed.Scene.ResolvedFootprints!.Any(static f => f.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject));
                expectedSceneSha = parsed.Scene.SceneIdentitySha256;
            }

            // Reopen a completely fresh DI container & SqliteCaptureProcessingStore, execute archived replay through
            // republication, verify idempotent byte-exact replay, AND prove conflicting content/lineage reinsertion fails closed!
            using (var reopenedProvider = CreateAgentProvider(tempRoot, catalog))
            {
                var reopenedStore = reopenedProvider.GetRequiredService<SqliteCaptureProcessingStore>();
                var reopenedOperations = reopenedProvider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var pipelineFactory = reopenedProvider.GetRequiredService<ICaptureProcessingPipelineFactory>();
                var persistence = reopenedProvider.GetRequiredService<CaptureProcessingPersistence>();
                var telemetry = reopenedProvider.GetRequiredService<CaptureProcessingTelemetry>();

                var reopenedCanonical = await reopenedStore.ReadCanonicalSceneCapturesAsync([captureId], token).ConfigureAwait(false);
                Assert.IsTrue(reopenedCanonical.Contains(captureId));

                // 1. Submit and execute an actual archived replay after restart.
                var replaySubmission = await reopenedOperations.SubmitReplayAsync(
                    new ProcessingReplaySubmission(captureId, activeRevisionId, rawArtifactId, TriggerReference: "dso-v3-idempotent-replay"),
                    "dso-v3-idempotent-replay", "owner", token).ConfigureAwait(false);
                var replayLease = await reopenedStore.ClaimReplayAsync("dso-v3-replay-worker", token).ConfigureAwait(false);
                Assert.IsNotNull(replayLease);
                Assert.AreEqual(replaySubmission.Execution.ExecutionId, replayLease.Execution.ExecutionId);

                var replayGraph = pipelineFactory.CreateRetainedGraph(replayLease.Configuration);
                var replayExecution = new ProcessingExecutionContext(
                    replayLease.Execution.ExecutionId,
                    ProcessingGraphExecutionClass.Replay,
                    replayLease.Execution.GraphRevisionId,
                    replayLease.Execution.LocalPlanIdentitySha256,
                    false,
                    replayLease.WorkId,
                    replayLease.LeaseToken,
                    replayLease.LeaseOwner,
                    replayLease.Execution.DeadlineUtc,
                    replayLease.ClaimCount);
                var replayResult = await FrameProcessingWorker.ProcessGraphItemAsync(
                    new FrameProcessingItem(
                        replayLease.Configuration,
                        replayLease.Submission,
                        replayLease.RawCapture,
                        replayLease.WorkId,
                        replayLease.LeaseToken,
                        replayExecution),
                    replayGraph,
                    persistence,
                    telemetry,
                    replayLease.Execution.AttemptCount,
                    NullLogger.Instance,
                    token,
                    maximumAttempts: 3,
                    TimeProvider.System,
                    durableAttempt: replayLease.ClaimCount).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replayResult.Outcome, replayResult.Reason);

                var completedReplay = await reopenedStore.CompleteReplayAsync(replayLease, replayResult, token).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completedReplay.Status);

                var afterReplayNode = await reopenedStore.ReadNodeAsync(captureId, "scene", token).ConfigureAwait(false);
                Assert.IsNotNull(afterReplayNode);
                Assert.HasCount(1, afterReplayNode.Outputs);
                var replayedBytes = await File.ReadAllBytesAsync(Path.Combine(tempRoot, afterReplayNode.Outputs.Single().PayloadRelativePath), token).ConfigureAwait(false);
                var replayed = ProjectedSceneJson.Parse(replayedBytes);
                Assert.IsTrue(replayed.IsValid);
                Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, replayed.Scene!.SchemaVersion);
                Assert.AreEqual(expectedSceneSha, replayed.Scene.SceneIdentitySha256);
                CollectionAssert.AreEqual(expectedSceneBytes, ProjectedSceneJson.Serialize(replayed.Scene));

                // 2. Deliberately attempt conflicting reinsertion under the SAME OutputIdentitySha256:
                //    (a) conflicting ContentIdentitySha256, and (b) conflicting SourceArtifactIds lineage.
                //    InsertOutputAsync MUST reject both with InvalidDataException and preserve the original intact!
                var conflictSubmission = await reopenedOperations.SubmitReplayAsync(
                    new ProcessingReplaySubmission(captureId, activeRevisionId, rawArtifactId, TriggerReference: "dso-v3-conflict-probe"),
                    "dso-v3-conflict-probe", "owner", token).ConfigureAwait(false);
                var conflictLease = await reopenedStore.ClaimReplayAsync("dso-v3-conflict-worker", token).ConfigureAwait(false);
                Assert.IsNotNull(conflictLease);
                Assert.AreEqual(conflictSubmission.Execution.ExecutionId, conflictLease.Execution.ExecutionId);
                var conflictExecution = new ProcessingExecutionContext(
                    conflictLease.Execution.ExecutionId,
                    ProcessingGraphExecutionClass.Replay,
                    conflictLease.Execution.GraphRevisionId,
                    conflictLease.Execution.LocalPlanIdentitySha256,
                    false,
                    conflictLease.WorkId,
                    conflictLease.LeaseToken,
                    conflictLease.LeaseOwner,
                    conflictLease.Execution.DeadlineUtc,
                    conflictLease.ClaimCount);
                var sceneNode = replayGraph.Nodes.Single(static n => n.Id == "scene");
                var originalNode = (await reopenedStore.ReadNodeAsync(captureId, "scene", token).ConfigureAwait(false))!;
                var originalOutput = originalNode.Outputs.Single();

                var conflictingContentOutput = originalOutput with { ContentIdentitySha256 = new string('9', 64) };
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await reopenedStore.WriteNodeAsync(
                        captureId, sceneNode, DurableProcessingNodeStatus.Completed, null, 1,
                        originalNode.ProcessingProfileIdentitySha256, originalNode.StartedUtc, originalNode.CompletedUtc,
                        originalNode.Duration, ProcessingOutcomeStatus.Produced, originalNode.Inputs ?? [],
                        conflictLease.WorkId, conflictLease.LeaseToken, [conflictingContentOutput],
                        conflictExecution, ProcessingNodeExecutionRoute.InProcess, token).ConfigureAwait(false)).ConfigureAwait(false);

                var originalManifest = (DurableTypedMetadataProductManifestV3)originalOutput.ProductManifest!;
                var conflictingManifest = originalManifest with
                {
                    Artifact = originalManifest.Artifact with { SourceArtifactIds = [Guid.NewGuid()] }
                };
                var conflictingLineageOutput = originalOutput with
                {
                    ProductManifest = conflictingManifest,
                    EvidenceJson = JsonSerializer.SerializeToUtf8Bytes(conflictingManifest, WebJsonOptions)
                };
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await reopenedStore.WriteNodeAsync(
                        captureId, sceneNode, DurableProcessingNodeStatus.Completed, null, 1,
                        originalNode.ProcessingProfileIdentitySha256, originalNode.StartedUtc, originalNode.CompletedUtc,
                        originalNode.Duration, ProcessingOutcomeStatus.Produced, originalNode.Inputs ?? [],
                        conflictLease.WorkId, conflictLease.LeaseToken, [conflictingLineageOutput],
                        conflictExecution, ProcessingNodeExecutionRoute.InProcess, token).ConfigureAwait(false)).ConfigureAwait(false);

                // Verify the committed original row and on-disk payload remain untouched after rejected conflicts.
                var intactNode = await reopenedStore.ReadNodeAsync(captureId, "scene", token).ConfigureAwait(false);
                Assert.IsNotNull(intactNode);
                var intactOutput = intactNode.Outputs.Single();
                Assert.AreEqual(originalOutput.ContentIdentitySha256, intactOutput.ContentIdentitySha256);
                CollectionAssert.AreEqual(
                    originalOutput.Artifact.SourceArtifactIds.ToArray(),
                    intactOutput.Artifact.SourceArtifactIds.ToArray());
                var intactBytes = await File.ReadAllBytesAsync(Path.Combine(tempRoot, intactOutput.PayloadRelativePath), token).ConfigureAwait(false);
                CollectionAssert.AreEqual(expectedSceneBytes, intactBytes);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task FullResolutionAsi174DeepSkyCaptureMeasuresBoundedLatencyCpuAllocationsAndCounts()
    {
        var catalog = CreateFixtureCatalog(includeHygStar: true);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null));

        // Measure matched baseline (disabled) vs enabled full-resolution 1936x1216 Mono16 captures.
        var baselineStats = await MeasureFullResolutionCaptureAsync(catalog, request, renderDeepSky: false).ConfigureAwait(false);
        var enabledStats = await MeasureFullResolutionCaptureAsync(catalog, request, renderDeepSky: true).ConfigureAwait(false);

        var extra = enabledStats.FinalFrame.Metadata.Extra!;
        TestContext.WriteLine(
            $"W1_DSO_1936x1216_BASELINE: medianWallMs={baselineStats.MedianWallMs:F2} avgCpuMs={baselineStats.AvgCpuMs:F2} " +
            $"avgAllocMiB={baselineStats.AvgAllocMiB:F2} sampledWsStartMiB={baselineStats.SampledWsStartMiB:F1} sampledWsPeakMiB={baselineStats.SampledWsPeakMiB:F1} " +
            $"projMs={baselineStats.MedianProjectionMs:F2} predMs={baselineStats.MedianPredictionMs:F2} refMs={baselineStats.MedianReferenceMs:F2} renderMs={baselineStats.MedianRenderMs:F2} " +
            $"pixelSha256={baselineStats.PixelShaPrefix}");
        TestContext.WriteLine(
            $"W1_DSO_1936x1216_ENABLED: candidates={extra["deepSkyCandidateCount"]} eligible={extra["deepSkyEligibleCount"]} " +
            $"admitted={extra["deepSkyAdmittedCount"]} annotationOnly={extra["deepSkyAnnotationOnlyCount"]} " +
            $"unresolvedPsf={extra["deepSkyUnresolvedPsfCount"]} darkAtten={extra["deepSkyDarkAttenuationCount"]} " +
            $"sampledPoints={extra["deepSkySampledPoints"]} rasterizedPixels={extra["deepSkyRasterizedPixels"]} " +
            $"kernelVisits={extra["deepSkyKernelCellVisits"]} diagBytes={extra["deepSkyDiagnosticsBytes"]} " +
            $"dsoSelectMs={enabledStats.MedianDsoSelectMs:F2} dsoPrepMs={enabledStats.MedianDsoPrepMs:F2} dsoSerializeMs={enabledStats.MedianDsoSerializeMs:F2} " +
            $"projMs={enabledStats.MedianProjectionMs:F2} predMs={enabledStats.MedianPredictionMs:F2} refMs={enabledStats.MedianReferenceMs:F2} renderMs={enabledStats.MedianRenderMs:F2} " +
            $"medianWallMs={enabledStats.MedianWallMs:F2} deltaWallMs={enabledStats.MedianWallMs - baselineStats.MedianWallMs:+0.00;-0.00} " +
            $"avgCpuMs={enabledStats.AvgCpuMs:F2} deltaCpuMs={enabledStats.AvgCpuMs - baselineStats.AvgCpuMs:+0.00;-0.00} " +
            $"avgAllocMiB={enabledStats.AvgAllocMiB:F2} deltaAllocMiB={enabledStats.AvgAllocMiB - baselineStats.AvgAllocMiB:+0.00;-0.00} " +
            $"sampledWsStartMiB={enabledStats.SampledWsStartMiB:F1} sampledWsPeakMiB={enabledStats.SampledWsPeakMiB:F1} " +
            $"pixelSha256={enabledStats.PixelShaPrefix}");

        Assert.IsTrue(int.Parse(extra["deepSkyAdmittedCount"], CultureInfo.InvariantCulture) >= 4);
        Assert.IsTrue(long.Parse(extra["deepSkySampledPoints"], CultureInfo.InvariantCulture) <= 2_000_000);
        Assert.IsTrue(int.Parse(extra["deepSkyRasterizedPixels"], CultureInfo.InvariantCulture) <= DeepSkyRenderPlan.MaximumSparsePixelsLimit);
        Assert.IsTrue(int.Parse(extra["deepSkyDiagnosticsBytes"], CultureInfo.InvariantCulture) is > 0 and < 65_536);
        Assert.AreNotEqual(baselineStats.PixelShaPrefix, enabledStats.PixelShaPrefix);
    }

    private static async Task<CaptureBenchmarkResult> MeasureFullResolutionCaptureAsync(
        CombinedVirtualCatalog catalog,
        CaptureRequest request,
        bool renderDeepSky)
    {
        var module = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore());
        await using var lifetime = module.ConfigureAwait(false);
        var config = CreateModuleConfig(CameraPixelFormat.Mono16, 1936, 1216, renderDeepSky: renderDeepSky, enableDeepSkyAnnotations: false,
            boresightAlt: 60, boresightAz: 180, focalLengthPx: 950, background: 20, readNoise: 1.5);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        // Warmup
        _ = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

        const int runs = 5;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocStart = GC.GetTotalAllocatedBytes(true);
        var wsStart = process.WorkingSet64;
        var peakWs = wsStart;
        var wallMs = new double[runs];
        var projMs = new double[runs];
        var predMs = new double[runs];
        var refMs = new double[runs];
        var renderMs = new double[runs];
        var dsoSelectMs = new double[runs];
        var dsoPrepMs = new double[runs];
        var dsoSerializeMs = new double[runs];
        CameraFrame? finalFrame = null;

        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            finalFrame = (await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
            wallMs[i] = sw.Elapsed.TotalMilliseconds;
            var ex = finalFrame.Metadata.Extra!;
            projMs[i] = ParseExtraDouble(ex, "stellarProjectionMilliseconds");
            predMs[i] = ParseExtraDouble(ex, "stellarPredictionMilliseconds");
            refMs[i] = ParseExtraDouble(ex, "stellarReferenceSceneMilliseconds");
            renderMs[i] = ParseExtraDouble(ex, "stellarRenderMilliseconds");
            dsoSelectMs[i] = ParseExtraDouble(ex, "deepSkyCandidateSelectMilliseconds");
            dsoPrepMs[i] = ParseExtraDouble(ex, "deepSkyPrepareMilliseconds");
            dsoSerializeMs[i] = ParseExtraDouble(ex, "deepSkyDiagnosticsSerializeMilliseconds");
            process.Refresh();
            peakWs = Math.Max(peakWs, process.WorkingSet64);
        }

        process.Refresh();
        var cpuPerCaptureMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds / runs;
        var allocPerCaptureMiB = (GC.GetTotalAllocatedBytes(true) - allocStart) / (double)runs / (1024d * 1024d);
        Array.Sort(wallMs);
        Array.Sort(projMs);
        Array.Sort(predMs);
        Array.Sort(refMs);
        Array.Sort(renderMs);
        Array.Sort(dsoSelectMs);
        Array.Sort(dsoPrepMs);
        Array.Sort(dsoSerializeMs);

        return new CaptureBenchmarkResult(
            finalFrame!,
            wallMs[runs / 2],
            cpuPerCaptureMs,
            allocPerCaptureMiB,
            wsStart / (1024d * 1024d),
            peakWs / (1024d * 1024d),
            projMs[runs / 2],
            predMs[runs / 2],
            refMs[runs / 2],
            renderMs[runs / 2],
            dsoSelectMs[runs / 2],
            dsoPrepMs[runs / 2],
            dsoSerializeMs[runs / 2],
            Convert.ToHexString(SHA256.HashData(finalFrame!.PixelData.Span))[..16]);
    }

    private static double ParseExtraDouble(IReadOnlyDictionary<string, string> extra, string key) =>
        extra.TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
            ? val
            : 0d;

    private sealed record CaptureBenchmarkResult(
        CameraFrame FinalFrame,
        double MedianWallMs,
        double AvgCpuMs,
        double AvgAllocMiB,
        double SampledWsStartMiB,
        double SampledWsPeakMiB,
        double MedianProjectionMs,
        double MedianPredictionMs,
        double MedianReferenceMs,
        double MedianRenderMs,
        double MedianDsoSelectMs,
        double MedianDsoPrepMs,
        double MedianDsoSerializeMs,
        string PixelShaPrefix);

    private static ServiceProvider CreateAgentProvider(string root, ICelestialCatalog catalog)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(catalog);
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:AgentId"] = "dso-v3-sqlite-agent",
                ["CameraAgent:CentralIntegration:Mode"] = "Disabled",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
        return services.BuildServiceProvider();
    }

    private static CombinedVirtualCatalog CreateFixtureCatalog(bool includeHygStar)
    {
        var midpoint = FixtureUtc.AddSeconds(1);
        var p1 = AltAzToJ2000(new AltAzPoint(60.0, 180.0), midpoint);
        var p2 = AltAzToJ2000(new AltAzPoint(61.5, 182.0), midpoint);
        var p3 = AltAzToJ2000(new AltAzPoint(58.5, 178.0), midpoint);
        var p4 = AltAzToJ2000(new AltAzPoint(60.5, 177.5), midpoint);
        var p5 = AltAzToJ2000(new AltAzPoint(59.0, 182.5), midpoint);
        var p6 = AltAzToJ2000(new AltAzPoint(61.0, 179.0), midpoint);

        var m31 = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G", p1.RaHours, p1.DecDegrees, "And", 60, 24, 35, 4.29, 3.44, 23.63, "Sb", 31, null, null, "Andromeda Galaxy");
        var m42 = new DeepSkyObject("NGC1976", "NGC 1976", "M42", "Cl+N", p2.RaHours, p2.DecDegrees, "Ori", 45, 30, 0, 4.0, 4.0, null, null, 42, null, null, "Great Orion Nebula");
        var m45 = new DeepSkyObject("Mel022", "Mel 22", "M45", "OCl", p3.RaHours, p3.DecDegrees, "Tau", 40, 40, 90, null, 3.2, null, null, 45, null, null, "Pleiades");
        var m13 = new DeepSkyObject("NGC6205", "NGC 6205", "M13", "GCl", p4.RaHours, p4.DecDegrees, "Her", 20, 20, null, 6.4, 5.8, null, null, 13, null, null, "Hercules Cluster");
        var m76 = new DeepSkyObject("NGC0650", "NGC 650", "M76", "PN", p5.RaHours, p5.DecDegrees, "Per", 0.05, 0.05, null, 12.2, 6.0, null, null, 76, null, null, "Little Dumbbell");
        var ngc2023 = new DeepSkyObject("NGC2023", "NGC 2023", "NGC 2023", "RfN", p6.RaHours, p6.DecDegrees, "Ori", 10, 8, null, null, null, null, null, null, null, null, null);
        var darkOverM42 = new DeepSkyObject("LDN-M42", "LDN-M42", "LDN-M42", "DrkN", p2.RaHours, p2.DecDegrees, "Ori", 14, 14, 0, null, null, null, null, null, null, null, null);

        DeepSkyObject[] objects = [m31, m42, m45, m13, m76, ngc2023, darkOverM42];
        var aliases = new List<DeepSkyAlias>
        {
            new("M31", m31.Id, DeepSkyAliasKinds.Messier),
            new("M42", m42.Id, DeepSkyAliasKinds.Messier),
            new("M45", m45.Id, DeepSkyAliasKinds.Messier),
            new("M13", m13.Id, DeepSkyAliasKinds.Messier),
            new("M76", m76.Id, DeepSkyAliasKinds.Messier)
        };
        foreach (var item in objects)
        {
            aliases.Add(new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation));
        }

        var m42Outline = CreateSquareOutline("NGC1976", p2.RaHours, p2.DecDegrees, 0.30);
        var dsoCatalog = new DeepSkyCatalog(OpenNgcSemantics, objects, aliases, [], [m42Outline]);

        var stars = includeHygStar
            ? new[] { new CelestialCatalogObject("HIP 17702", "Alcyone", p3.RaHours, p3.DecDegrees, 4.2, -0.09) }
            : Array.Empty<CelestialCatalogObject>();
        return new CombinedVirtualCatalog(stars, dsoCatalog);
    }

    private static CameraModuleConfig CreateModuleConfig(
        CameraPixelFormat format,
        int width,
        int height,
        bool renderDeepSky,
        bool enableDeepSkyAnnotations,
        double boresightAlt = 60,
        double boresightAz = 180,
        double focalLengthPx = 900,
        double background = 5,
        double readNoise = 0,
        bool shotNoiseEnabled = false,
        VirtualSkyDeepSkyRenderOptions? explicitDisabledRenderOptions = null,
        int annotationMaximumOutlines = 0)
    {
        var focalLengthMm = focalLengthPx * 5.86 / 1000.0;
        var moduleOptions = new VirtualSkyCameraModuleOptions
        {
            Seed = 2026,
            FixedSceneUtc = FixtureUtc,
            FixedSequenceStartUtc = FixtureUtc,
            IlluminationMode = VirtualSkyIlluminationMode.ControlledNight,
            Asi178Sensor = new Asi178McSensorOptions { Enabled = format == CameraPixelFormat.BayerRggb16 },
            BackgroundElectronsPerSecond = background,
            MagnitudeZeroElectronsPerSecond = 10_000,
            PsfSigmaPixels = 1.0,
            PsfRadiusPixels = 4.0,
            VignettingStrength = 0,
            Bias = format == CameraPixelFormat.Rgb24 ? 0 : 100,
            ReadNoiseStandardDeviation = readNoise,
            ShotNoiseEnabled = shotNoiseEnabled,
            DarkCurrentElectronsPerSecond = 0,
            DeepSky = enableDeepSkyAnnotations
                ? new OpenNgcDeepSkyOptions(Enabled: true, MaximumOutlines: annotationMaximumOutlines)
                : null,
            RenderDeepSkySources = renderDeepSky
                ? new VirtualSkyDeepSkyRenderOptions(Enabled: true, MinimumSignalToNoise: 1.0, SamplesPerProjectedPixel: 2, PreferCatalogOutlines: true)
                : explicitDisabledRenderOptions
        };

        return new CameraModuleConfig(
            HvoLocation,
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(moduleOptions, WebJsonOptions)),
            new CameraRigConfig(
                new SensorProfile(
                    format switch
                    {
                        CameraPixelFormat.Mono16 => "VirtualAsi174Mm",
                        CameraPixelFormat.Rgb24 => "VirtualAsi174McRgb",
                        CameraPixelFormat.BayerRggb16 => "VirtualAsi178McRaw16",
                        _ => throw new ArgumentOutOfRangeException(nameof(format))
                    },
                    width, height, 5.86,
                    format == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color,
                    format,
                    format switch
                    {
                        CameraPixelFormat.Mono16 => SensorResponseMode.Monochrome,
                        CameraPixelFormat.Rgb24 => SensorResponseMode.RenderedRgb,
                        CameraPixelFormat.BayerRggb16 => SensorResponseMode.BayerRaw,
                        _ => throw new ArgumentOutOfRangeException(nameof(format))
                    },
                    SensorRecipeVersion: format switch
                    {
                        CameraPixelFormat.Mono16 => "mono16-v1",
                        CameraPixelFormat.Rgb24 => "rgb24-compat-v1",
                        CameraPixelFormat.BayerRggb16 => "rggb16-v1",
                        _ => throw new ArgumentOutOfRangeException(nameof(format))
                    }),
                new OpticsProfile(
                    "Perspective",
                    focalLengthMm,
                    2d * Math.Atan(width / (2d * focalLengthPx)) * 180d / Math.PI,
                    0,
                    LensKind.Rectilinear,
                    width / 2.0,
                    height / 2.0,
                    Math.Max(width, height),
                    CalibrationVersion: "perspective-v1"),
                new RigOrientation(boresightAlt, boresightAz, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig(enableDeepSkyAnnotations
                ? [new CaptureProcessingStepConfig("ProjectedScene", "scene", DependsOn: ["$raw"],
                    Options: annotationMaximumOutlines > 0
                        ? JsonSerializer.SerializeToElement(new { DeepSkyEnabled = true, DeepSkyMaximumOutlines = annotationMaximumOutlines }, WebJsonOptions)
                        : null)]
                : []));
    }

    private static (double RaHours, double DecDegrees) AltAzToJ2000(AltAzPoint altAz, DateTimeOffset utc)
    {
        var eqDate = CoordinateTransforms.HorizontalToEquatorial(altAz, utc, HvoLocation.LatitudeDegrees, HvoLocation.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(eqDate, utc);
        return (j2000.RightAscensionHours, j2000.DeclinationDegrees);
    }

    private static DeepSkyOutline CreateSquareOutline(string objectId, double raHours, double decDeg, double halfSizeDeg)
    {
        var cosDec = Math.Max(0.2, Math.Cos(decDeg * Math.PI / 180d));
        var dRaDeg = halfSizeDeg / cosDec;
        var raDeg = raHours * 15d;
        double WrapRa(double d) => ((d % 360d) + 360d) % 360d;
        var p0 = new DeepSkyOutlinePoint(WrapRa(raDeg - dRaDeg), decDeg - halfSizeDeg);
        return new DeepSkyOutline(objectId, DeepSkyOutline.WidestLevel,
        [
            new DeepSkyOutlineRing(
            [
                p0,
                new DeepSkyOutlinePoint(WrapRa(raDeg + dRaDeg), decDeg - halfSizeDeg),
                new DeepSkyOutlinePoint(WrapRa(raDeg + dRaDeg), decDeg + halfSizeDeg),
                new DeepSkyOutlinePoint(WrapRa(raDeg - dRaDeg), decDeg + halfSizeDeg),
                p0
            ])
        ]);
    }

    private sealed class CombinedVirtualCatalog(
        IReadOnlyList<CelestialCatalogObject> stars,
        IDeepSkyCatalog deepSky) : ICelestialCatalog, IDeepSkyCatalogSource
    {
        private readonly InMemoryCelestialCatalog _stars = new(stars);
        public IDeepSkyCatalog? DeepSky { get; } = deepSky;
        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _stars.Query(query);
        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query, CancellationToken cancellationToken = default) =>
            _stars.QueryCandidatesAsync(query, cancellationToken);
    }

    private sealed class RecordingDeepSkyCatalog(IDeepSkyCatalog inner) : IDeepSkyCatalog
    {
        public List<DeepSkyQuery> Queries { get; } = [];
        public DeepSkySemantics Semantics => inner.Semantics;
        public IReadOnlyList<DeepSkyObject> Objects => inner.Objects;
        public int AliasCount => inner.AliasCount;
        public IReadOnlyList<DeepSkyTombstone> Tombstones => inner.Tombstones;
        public bool TryGetObject(string objectId, out DeepSkyObject value) => inner.TryGetObject(objectId, out value);
        public IReadOnlyList<DeepSkyOutline> GetOutlines(string objectId) => inner.GetOutlines(objectId);
        public IReadOnlyList<DeepSkyAlias> FindByAlias(string designation) => inner.FindByAlias(designation);
        public bool TryGetTombstone(string id, out DeepSkyTombstone tombstone) => inner.TryGetTombstone(id, out tombstone);
        public DeepSkyQueryResult Query(DeepSkyQuery query)
        {
            Queries.Add(query);
            return inner.Query(query);
        }
    }

    private sealed class RecordingStagingStore : IProjectedSceneStagingStore
    {
        public List<VisibleScene> Scenes { get; } = [];
        public ValueTask StageAsync(string stageKey, string sceneId, VisibleScene scene, CancellationToken cancellationToken)
        {
            Scenes.Add(scene);
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteAsync(string stageKey, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask MarkCompletedAsync(string stageKey, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteCompletedAsync(string stageKey, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
