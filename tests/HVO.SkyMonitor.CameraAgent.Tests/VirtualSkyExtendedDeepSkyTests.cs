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
    public async Task SnapshotlessDeepSkyCatalogsWithIdenticalSemanticsAndAnnotationsOffProduceDistinctSceneIdsAndProvenance()
    {
        // Exact F2 regression: annotations off, identical stellar inputs, two valid in-memory DeepSkyCatalogs
        // sharing identical DeepSkySemantics with SnapshotIdentity == null (1 M31 row vs 0 rows) MUST produce
        // distinct raw hashes, distinct SceneIds, and non-null distinct DeepSkyCatalog provenance, while
        // preserving exact F1 opt-out (null / enabled:false) byte-identity!
        var nullSnapshotSemantics = OpenNgcSemantics with { SnapshotIdentity = null };
        var midpoint = FixtureUtc.AddSeconds(1);
        var m31Pos = AltAzToJ2000(new AltAzPoint(60.0, 180.0), midpoint);
        var m31 = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G",
            m31Pos.RaHours, m31Pos.DecDegrees, "And", 60, 24, 35, 4.29, 3.44, 23.63, "Sb", 31, null, null, "Andromeda Galaxy");

        var oneRowCatalog = new CombinedVirtualCatalog([],
            new DeepSkyCatalog(
                nullSnapshotSemantics,
                [m31],
                [new("NGC 224", m31.Id, DeepSkyAliasKinds.Designation), new("M31", m31.Id, DeepSkyAliasKinds.Messier)],
                [],
                []));
        var zeroRowCatalog = new CombinedVirtualCatalog([],
            new DeepSkyCatalog(nullSnapshotSemantics, [], [], [], []));

        var enabledConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: true, enableDeepSkyAnnotations: false);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null));

        var oneRowModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), oneRowCatalog, new ProjectedSceneStore());
        await using var oneRowLifetime = oneRowModule.ConfigureAwait(false);
        await oneRowModule.InitializeAsync(enabledConfig, CancellationToken.None).ConfigureAwait(false);
        var oneRowFrame = (await oneRowModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        var oneRowRepeatModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), oneRowCatalog, new ProjectedSceneStore());
        await using var oneRowRepeatLifetime = oneRowRepeatModule.ConfigureAwait(false);
        await oneRowRepeatModule.InitializeAsync(enabledConfig, CancellationToken.None).ConfigureAwait(false);
        var oneRowRepeatFrame = (await oneRowRepeatModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        var zeroRowModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), zeroRowCatalog, new ProjectedSceneStore());
        await using var zeroRowLifetime = zeroRowModule.ConfigureAwait(false);
        await zeroRowModule.InitializeAsync(enabledConfig, CancellationToken.None).ConfigureAwait(false);
        var zeroRowFrame = (await zeroRowModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.IsFalse(oneRowFrame.PixelData.Span.SequenceEqual(zeroRowFrame.PixelData.Span),
            "1 M31 row vs 0 rows must produce different raw pixel bytes.");
        Assert.AreNotEqual(oneRowFrame.Metadata.Scene!.SceneId, zeroRowFrame.Metadata.Scene!.SceneId,
            "Different raw images from snapshotless DSO catalogs must never share a SceneId.");
        Assert.AreNotEqual(oneRowFrame.Metadata.Extra!["stellarNoiseIdentity"], zeroRowFrame.Metadata.Extra!["stellarNoiseIdentity"]);
        Assert.IsNotNull(oneRowFrame.Metadata.Scene!.DeepSkyCatalog,
            "Snapshotless DSO render catalog must publish deterministic content-bound DeepSkyCatalog provenance.");
        Assert.IsNotNull(zeroRowFrame.Metadata.Scene!.DeepSkyCatalog);
        Assert.AreEqual(64, oneRowFrame.Metadata.Scene!.DeepSkyCatalog!.DatabaseSha256.Length);
        Assert.AreEqual(64, zeroRowFrame.Metadata.Scene!.DeepSkyCatalog!.DatabaseSha256.Length);
        Assert.AreNotEqual(oneRowFrame.Metadata.Scene!.DeepSkyCatalog, zeroRowFrame.Metadata.Scene!.DeepSkyCatalog);

        // Repeat determinism check.
        Assert.AreEqual(oneRowFrame.Metadata.Scene!.SceneId, oneRowRepeatFrame.Metadata.Scene!.SceneId);
        Assert.AreEqual(oneRowFrame.Metadata.Scene!.DeepSkyCatalog, oneRowRepeatFrame.Metadata.Scene!.DeepSkyCatalog);
        CollectionAssert.AreEqual(oneRowFrame.PixelData.ToArray(), oneRowRepeatFrame.PixelData.ToArray());

        // Exact F1 opt-out preservation with snapshotless catalogs (null vs enabled:false).
        var omittedConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false,
            readNoise: 2.0, shotNoiseEnabled: true);
        var explicitDisabledConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false,
            readNoise: 2.0, shotNoiseEnabled: true,
            explicitDisabledRenderOptions: new VirtualSkyDeepSkyRenderOptions(Enabled: false));

        var optOutOneModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), oneRowCatalog, new ProjectedSceneStore());
        await using var optOutOneLifetime = optOutOneModule.ConfigureAwait(false);
        await optOutOneModule.InitializeAsync(omittedConfig, CancellationToken.None).ConfigureAwait(false);
        var optOutOneFrame = (await optOutOneModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        var optOutZeroModule = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), zeroRowCatalog, new ProjectedSceneStore());
        await using var optOutZeroLifetime = optOutZeroModule.ConfigureAwait(false);
        await optOutZeroModule.InitializeAsync(explicitDisabledConfig, CancellationToken.None).ConfigureAwait(false);
        var optOutZeroFrame = (await optOutZeroModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.AreEqual(optOutOneFrame.Metadata.Scene!.SceneId, optOutZeroFrame.Metadata.Scene!.SceneId);
        Assert.IsNull(optOutOneFrame.Metadata.Scene!.DeepSkyCatalog);
        Assert.IsNull(optOutZeroFrame.Metadata.Scene!.DeepSkyCatalog);
        CollectionAssert.AreEqual(optOutOneFrame.PixelData.ToArray(), optOutZeroFrame.PixelData.ToArray());
    }

    [TestMethod]
    public async Task AliasOnlyCrossReferenceChangeUnderIcQueryChangesSceneIdAndCatalogHashWhilePreservingOptOut()
    {
        // Exact F2 regression: with an IC-only query and annotations disabled, adding ONLY the "IC 123"
        // cross-reference alias to the same M31 row changes selection from 0 objects to 1 (distinct raw pixels)
        // and MUST produce a distinct SceneId and distinct DeepSkyCatalog hash (for both DeepSkyCatalog and a
        // snapshotless custom IDeepSkyCatalog wrapper), while preserving F1 opt-out byte-identity.
        var nullSnapshotSemantics = OpenNgcSemantics with { SnapshotIdentity = null };
        var midpoint = FixtureUtc.AddSeconds(1);
        var m31Pos = AltAzToJ2000(new AltAzPoint(60.0, 180.0), midpoint);
        var m31 = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G",
            m31Pos.RaHours, m31Pos.DecDegrees, "And", 60, 24, 35, 4.29, 3.44, 23.63, "Sb", 31, null, null, "Andromeda Galaxy");

        var baseAliases = new DeepSkyAlias[]
        {
            new("NGC 224", m31.Id, DeepSkyAliasKinds.Designation),
            new("M31", m31.Id, DeepSkyAliasKinds.Messier)
        };
        var withIcCrossRefAliases = new DeepSkyAlias[]
        {
            new("NGC 224", m31.Id, DeepSkyAliasKinds.Designation),
            new("M31", m31.Id, DeepSkyAliasKinds.Messier),
            new("IC 123", m31.Id, DeepSkyAliasKinds.CrossReference)
        };

        var withoutIcDso = new DeepSkyCatalog(nullSnapshotSemantics, [m31], baseAliases, [], []);
        var withIcDso = new DeepSkyCatalog(nullSnapshotSemantics, [m31], withIcCrossRefAliases, [], []);

        var icOnlyQuery = new OpenNgcDeepSkyOptions(Enabled: true, IncludeNgc: false, IncludeMessier: false, IncludeIc: true);
        var baseConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false);
        var icOptions = JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(
            baseConfig.Module.Options!.Value.GetRawText(), WebJsonOptions)!;
        var icEnabledConfig = baseConfig with
        {
            Module = new CameraModuleDescriptor(
                "VirtualSky",
                JsonSerializer.SerializeToElement(
                    new VirtualSkyCameraModuleOptions
                    {
                        Seed = icOptions.Seed,
                        FixedSceneUtc = icOptions.FixedSceneUtc,
                        FixedSequenceStartUtc = icOptions.FixedSequenceStartUtc,
                        IlluminationMode = icOptions.IlluminationMode,
                        BackgroundElectronsPerSecond = icOptions.BackgroundElectronsPerSecond,
                        MagnitudeZeroElectronsPerSecond = icOptions.MagnitudeZeroElectronsPerSecond,
                        PsfSigmaPixels = icOptions.PsfSigmaPixels,
                        PsfRadiusPixels = icOptions.PsfRadiusPixels,
                        VignettingStrength = icOptions.VignettingStrength,
                        Bias = icOptions.Bias,
                        ReadNoiseStandardDeviation = icOptions.ReadNoiseStandardDeviation,
                        ShotNoiseEnabled = icOptions.ShotNoiseEnabled,
                        DarkCurrentElectronsPerSecond = icOptions.DarkCurrentElectronsPerSecond,
                        RenderDeepSkySources = new VirtualSkyDeepSkyRenderOptions(
                            Enabled: true,
                            MinimumSignalToNoise: 1.0,
                            SamplesPerProjectedPixel: 2,
                            PreferCatalogOutlines: true,
                            Query: icOnlyQuery)
                    },
                    WebJsonOptions))
        };
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null));

        foreach (var wrapCustom in new[] { false, true })
        {
            IDeepSkyCatalog dsoWithout = wrapCustom ? new RecordingDeepSkyCatalog(withoutIcDso) : withoutIcDso;
            IDeepSkyCatalog dsoWith = wrapCustom ? new RecordingDeepSkyCatalog(withIcDso) : withIcDso;

            var modWithout = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
                new CombinedVirtualCatalog([], dsoWithout), new ProjectedSceneStore());
            await using var lifeWithout = modWithout.ConfigureAwait(false);
            await modWithout.InitializeAsync(icEnabledConfig, CancellationToken.None).ConfigureAwait(false);
            var frameWithout = (await modWithout.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

            var modWith = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
                new CombinedVirtualCatalog([], dsoWith), new ProjectedSceneStore());
            await using var lifeWith = modWith.ConfigureAwait(false);
            await modWith.InitializeAsync(icEnabledConfig, CancellationToken.None).ConfigureAwait(false);
            var frameWith = (await modWith.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

            Assert.AreEqual("0", frameWithout.Metadata.Extra!["deepSkyCandidateCount"],
                $"Without IC 123 alias (wrapCustom={wrapCustom}), IC-only query must select 0 candidates.");
            Assert.AreEqual("1", frameWith.Metadata.Extra!["deepSkyCandidateCount"],
                $"With IC 123 cross-reference alias (wrapCustom={wrapCustom}), IC-only query must select 1 candidate.");
            Assert.IsFalse(frameWithout.PixelData.Span.SequenceEqual(frameWith.PixelData.Span),
                $"Adding IC 123 alias under IC-only query (wrapCustom={wrapCustom}) must change raw pixels.");
            Assert.AreNotEqual(frameWithout.Metadata.Scene!.SceneId, frameWith.Metadata.Scene!.SceneId,
                $"Alias-only change affecting selection (wrapCustom={wrapCustom}) must produce distinct SceneId.");
            Assert.AreNotEqual(
                frameWithout.Metadata.Scene!.DeepSkyCatalog!.DatabaseSha256,
                frameWith.Metadata.Scene!.DeepSkyCatalog!.DatabaseSha256,
                $"Alias-only change affecting selection (wrapCustom={wrapCustom}) must produce distinct catalog hash.");
        }

        // Preserve exact F1 opt-out identity when RenderDeepSkySources is omitted or disabled.
        var optOutDisabledConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false,
            explicitDisabledRenderOptions: new VirtualSkyDeepSkyRenderOptions(Enabled: false, Query: icOnlyQuery));
        var optOutModA = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
            new CombinedVirtualCatalog([], withoutIcDso), new ProjectedSceneStore());
        await using var optOutLifeA = optOutModA.ConfigureAwait(false);
        await optOutModA.InitializeAsync(baseConfig, CancellationToken.None).ConfigureAwait(false);
        var optOutFrameA = (await optOutModA.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        var optOutModB = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
            new CombinedVirtualCatalog([], withIcDso), new ProjectedSceneStore());
        await using var optOutLifeB = optOutModB.ConfigureAwait(false);
        await optOutModB.InitializeAsync(optOutDisabledConfig, CancellationToken.None).ConfigureAwait(false);
        var optOutFrameB = (await optOutModB.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.AreEqual(optOutFrameA.Metadata.Scene!.SceneId, optOutFrameB.Metadata.Scene!.SceneId);
        CollectionAssert.AreEqual(optOutFrameA.PixelData.ToArray(), optOutFrameB.PixelData.ToArray());
    }

    [TestMethod]
    public async Task SnapshotlessCustomCatalogBoundsPerFrameOutlineLookupsToCandidateCap()
    {
        // Exact F22 regression: a snapshotless custom IDeepSkyCatalog with 2,048 objects and
        // MaximumCandidates = 1, MaximumRenderedSources = 1 must NOT enumerate or look up outlines
        // for all 2,048 rows on every capture — outline lookups must stay bounded by MaximumCandidates (<= 1 per capture).
        var nullSnapshotSemantics = OpenNgcSemantics with { SnapshotIdentity = null };
        var midpoint = FixtureUtc.AddSeconds(1);
        var centerPos = AltAzToJ2000(new AltAzPoint(60.0, 180.0), midpoint);

        const int totalObjects = 2_048;
        var objects = new List<DeepSkyObject>(totalObjects);
        var aliases = new List<DeepSkyAlias>(totalObjects + 1);
        for (var i = 0; i < totalObjects; i++)
        {
            var id = $"NGC{i + 1:D4}";
            var designation = $"NGC {i + 1}";
            // Place object 0 at boresight as Messier 1 (wins candidate cap 1); place others nearby.
            var raOffset = ((i % 32) - 16) * 0.002;
            var decOffset = ((i / 32) - 32) * 0.02;
            var ra = (((centerPos.RaHours + raOffset) % 24d) + 24d) % 24d;
            var dec = Math.Clamp(centerPos.DecDegrees + decOffset, -89.0, 89.0);
            var item = new DeepSkyObject(
                id, designation, i == 0 ? "M1" : designation, "Neb",
                ra, dec, "Tau", 6, 6, 0, 8.4, i == 0 ? 4.0 : 12.0 + (i % 5) * 0.1,
                null, null, i == 0 ? 1 : null, null, null, null);
            objects.Add(item);
            aliases.Add(new DeepSkyAlias(designation, id, DeepSkyAliasKinds.Designation));
            if (i == 0)
            {
                aliases.Add(new DeepSkyAlias("M1", id, DeepSkyAliasKinds.Messier));
            }
        }

        var outlineA = CreateSquareOutline("NGC0001", centerPos.RaHours, centerPos.DecDegrees, 0.08);
        var outlineB = CreateSquareOutline("NGC0001", centerPos.RaHours, centerPos.DecDegrees, 0.14);
        var innerA = new DeepSkyCatalog(nullSnapshotSemantics, objects, aliases, [], [outlineA]);
        var innerB = new DeepSkyCatalog(nullSnapshotSemantics, objects, aliases, [], [outlineB]);
        var spyA = new RecordingDeepSkyCatalog(innerA);
        var spyB = new RecordingDeepSkyCatalog(innerB);

        var baseConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false);
        var baseOpts = JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(
            baseConfig.Module.Options!.Value.GetRawText(), WebJsonOptions)!;
        var capOneConfig = baseConfig with
        {
            Module = new CameraModuleDescriptor(
                "VirtualSky",
                JsonSerializer.SerializeToElement(
                    new VirtualSkyCameraModuleOptions
                    {
                        Seed = baseOpts.Seed,
                        FixedSceneUtc = baseOpts.FixedSceneUtc,
                        FixedSequenceStartUtc = baseOpts.FixedSequenceStartUtc,
                        IlluminationMode = baseOpts.IlluminationMode,
                        BackgroundElectronsPerSecond = baseOpts.BackgroundElectronsPerSecond,
                        MagnitudeZeroElectronsPerSecond = baseOpts.MagnitudeZeroElectronsPerSecond,
                        PsfSigmaPixels = baseOpts.PsfSigmaPixels,
                        PsfRadiusPixels = baseOpts.PsfRadiusPixels,
                        VignettingStrength = baseOpts.VignettingStrength,
                        Bias = baseOpts.Bias,
                        ReadNoiseStandardDeviation = baseOpts.ReadNoiseStandardDeviation,
                        ShotNoiseEnabled = baseOpts.ShotNoiseEnabled,
                        DarkCurrentElectronsPerSecond = baseOpts.DarkCurrentElectronsPerSecond,
                        RenderDeepSkySources = new VirtualSkyDeepSkyRenderOptions(
                            Enabled: true,
                            MinimumSignalToNoise: 1.0,
                            MaximumCandidates: 1,
                            MaximumRenderedSources: 1,
                            SamplesPerProjectedPixel: 2,
                            PreferCatalogOutlines: true)
                    },
                    WebJsonOptions))
        };
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null));

        var modA = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
            new CombinedVirtualCatalog([], spyA), new ProjectedSceneStore());
        await using var lifeA = modA.ConfigureAwait(false);
        await modA.InitializeAsync(capOneConfig, CancellationToken.None).ConfigureAwait(false);
        var frameA1 = (await modA.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
        var lookupsAfterFirst = spyA.OutlineLookupCount;
        var frameA2 = (await modA.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
        var lookupsAfterSecond = spyA.OutlineLookupCount;

        Assert.IsTrue(lookupsAfterFirst <= 1,
            $"Snapshotless custom catalog with MaximumCandidates=1 must bound outline lookups per capture to <= 1; got {lookupsAfterFirst}.");
        Assert.IsTrue(lookupsAfterSecond - lookupsAfterFirst <= 1,
            $"Second capture must also stay bounded to <= 1 outline lookup; got {lookupsAfterSecond - lookupsAfterFirst}.");
        Assert.AreEqual(frameA1.Metadata.Scene!.DeepSkyCatalog, frameA2.Metadata.Scene!.DeepSkyCatalog,
            "Consecutive captures against the unchanged snapshotless catalog must keep identical catalog provenance.");

        // Repeating the exact first capture on a fresh module produces identical SceneId and bounded lookups.
        var spyARepeat = new RecordingDeepSkyCatalog(innerA);
        var modARepeat = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
            new CombinedVirtualCatalog([], spyARepeat), new ProjectedSceneStore());
        await using var lifeARepeat = modARepeat.ConfigureAwait(false);
        await modARepeat.InitializeAsync(capOneConfig, CancellationToken.None).ConfigureAwait(false);
        var frameARepeat = (await modARepeat.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
        Assert.IsTrue(spyARepeat.OutlineLookupCount <= 1);
        Assert.AreEqual(frameA1.Metadata.Scene!.SceneId, frameARepeat.Metadata.Scene!.SceneId);
        Assert.AreEqual(frameA1.Metadata.Scene!.DeepSkyCatalog, frameARepeat.Metadata.Scene!.DeepSkyCatalog);

        // Changing the selected object's outline on the snapshotless custom catalog still updates SceneId & hash!
        var modB = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
            new CombinedVirtualCatalog([], spyB), new ProjectedSceneStore());
        await using var lifeB = modB.ConfigureAwait(false);
        await modB.InitializeAsync(capOneConfig, CancellationToken.None).ConfigureAwait(false);
        var frameB = (await modB.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.IsTrue(spyB.OutlineLookupCount <= 1);
        Assert.AreNotEqual(frameA1.Metadata.Scene!.SceneId, frameB.Metadata.Scene!.SceneId,
            "Changing a selected candidate's outline on a snapshotless custom catalog must change SceneId.");
        Assert.AreNotEqual(
            frameA1.Metadata.Scene!.DeepSkyCatalog!.DatabaseSha256,
            frameB.Metadata.Scene!.DeepSkyCatalog!.DatabaseSha256,
            "Changing a selected candidate's outline on a snapshotless custom catalog must change catalog hash.");
    }

    [TestMethod]
    public async Task SyntheticScenarioPromotionUsesUnifiedSelectionForIdentityAndBoundsOutlineLookups()
    {
        // Exact F2 + F22 regression: with annotations disabled, caps=(1,1), and a 2,048-row snapshotless custom
        // catalog where a synthetic scenario promotes catalog row NGC0002 above NGC0001:
        // (1) changing ONLY NGC0002's catalog square outline (0.08 deg -> 0.40 deg) changes raw pixels AND MUST
        //     change SceneId, stellarNoiseIdentity, and DeepSkyCatalog.DatabaseSha256;
        // (2) per-capture outline lookups on the underlying catalog stay strictly <= 1 (never 2 for promoted + displaced).
        var nullSnapshotSemantics = OpenNgcSemantics with { SnapshotIdentity = null };
        var midpoint = FixtureUtc.AddSeconds(1);
        var centerPos = AltAzToJ2000(new AltAzPoint(60.0, 180.0), midpoint);

        const int totalObjects = 2_048;
        var objects = new List<DeepSkyObject>(totalObjects);
        var aliases = new List<DeepSkyAlias>(totalObjects + 1);
        for (var i = 0; i < totalObjects; i++)
        {
            var id = $"NGC{i + 1:D4}";
            var designation = $"NGC {i + 1}";
            var raOffset = i <= 1 ? 0d : ((i % 32) - 16) * 0.002;
            var decOffset = i <= 1 ? 0d : ((i / 32) - 32) * 0.02;
            var ra = (((centerPos.RaHours + raOffset) % 24d) + 24d) % 24d;
            var dec = Math.Clamp(centerPos.DecDegrees + decOffset, -89.0, 89.0);
            var item = new DeepSkyObject(
                id, designation, i == 0 ? "M2" : designation, "Neb",
                ra, dec, "Tau", 6, 6, 0, 8.4, i == 0 ? 4.0 : 12.0 + (i % 5) * 0.1,
                null, null, i == 0 ? 2 : null, null, null, null);
            objects.Add(item);
            aliases.Add(new DeepSkyAlias(designation, id, DeepSkyAliasKinds.Designation));
            if (i == 0)
            {
                aliases.Add(new DeepSkyAlias("M2", id, DeepSkyAliasKinds.Messier));
            }
        }

        var displacedOutline = CreateSquareOutline("NGC0001", centerPos.RaHours, centerPos.DecDegrees, 0.10);
        var promotedOutlineSmall = CreateSquareOutline("NGC0002", centerPos.RaHours, centerPos.DecDegrees, 0.08);
        var promotedOutlineLarge = CreateSquareOutline("NGC0002", centerPos.RaHours, centerPos.DecDegrees, 0.40);
        var innerSmall = new DeepSkyCatalog(nullSnapshotSemantics, objects, aliases, [], [displacedOutline, promotedOutlineSmall]);
        var innerLarge = new DeepSkyCatalog(nullSnapshotSemantics, objects, aliases, [], [displacedOutline, promotedOutlineLarge]);
        var spySmall = new RecordingDeepSkyCatalog(innerSmall);
        var spyLarge = new RecordingDeepSkyCatalog(innerLarge);

        var promotedRow = new DeepSkyObject(
            "NGC0002", "NGC 2", "M1", "Neb",
            centerPos.RaHours, centerPos.DecDegrees, "Tau", 6, 6, 0, 4.0, 3.0,
            null, null, 1, null, null, null);
        var promotionScenario = new DeepSkySyntheticScenario(
            "f2-f22-promotion-v1",
            AdditionalObjects: [promotedRow]);

        var baseConfig = CreateModuleConfig(
            CameraPixelFormat.Mono16, 128, 128, renderDeepSky: false, enableDeepSkyAnnotations: false);
        var baseOpts = JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(
            baseConfig.Module.Options!.Value.GetRawText(), WebJsonOptions)!;
        var promotedConfig = baseConfig with
        {
            Module = new CameraModuleDescriptor(
                "VirtualSky",
                JsonSerializer.SerializeToElement(
                    new VirtualSkyCameraModuleOptions
                    {
                        Seed = baseOpts.Seed,
                        FixedSceneUtc = baseOpts.FixedSceneUtc,
                        FixedSequenceStartUtc = baseOpts.FixedSequenceStartUtc,
                        IlluminationMode = baseOpts.IlluminationMode,
                        BackgroundElectronsPerSecond = baseOpts.BackgroundElectronsPerSecond,
                        MagnitudeZeroElectronsPerSecond = baseOpts.MagnitudeZeroElectronsPerSecond,
                        PsfSigmaPixels = baseOpts.PsfSigmaPixels,
                        PsfRadiusPixels = baseOpts.PsfRadiusPixels,
                        VignettingStrength = baseOpts.VignettingStrength,
                        Bias = baseOpts.Bias,
                        ReadNoiseStandardDeviation = baseOpts.ReadNoiseStandardDeviation,
                        ShotNoiseEnabled = baseOpts.ShotNoiseEnabled,
                        DarkCurrentElectronsPerSecond = baseOpts.DarkCurrentElectronsPerSecond,
                        RenderDeepSkySources = new VirtualSkyDeepSkyRenderOptions(
                            Enabled: true,
                            MinimumSignalToNoise: 1.0,
                            MaximumCandidates: 1,
                            MaximumRenderedSources: 1,
                            SamplesPerProjectedPixel: 2,
                            PreferCatalogOutlines: true,
                            SyntheticScenario: promotionScenario)
                    },
                    WebJsonOptions))
        };
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null));

        var modSmall = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
            new CombinedVirtualCatalog([], spySmall), new ProjectedSceneStore());
        await using var lifeSmall = modSmall.ConfigureAwait(false);
        await modSmall.InitializeAsync(promotedConfig, CancellationToken.None).ConfigureAwait(false);
        var frameSmall = (await modSmall.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        var modLarge = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
            new CombinedVirtualCatalog([], spyLarge), new ProjectedSceneStore());
        await using var lifeLarge = modLarge.ConfigureAwait(false);
        await modLarge.InitializeAsync(promotedConfig, CancellationToken.None).ConfigureAwait(false);
        var frameLarge = (await modLarge.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.AreEqual(1, spySmall.OutlineLookupCount,
            $"Synthetic promotion with MaximumCandidates=1 must perform at most 1 outline lookup; got {spySmall.OutlineLookupCount}.");
        Assert.AreEqual(1, spyLarge.OutlineLookupCount,
            $"Synthetic promotion with MaximumCandidates=1 must perform at most 1 outline lookup; got {spyLarge.OutlineLookupCount}.");

        var diagSmall = JsonSerializer.Deserialize<DeepSkySourceDiagnostic[]>(
            frameSmall.Metadata.Extra!["deepSkyDiagnostics"], WebJsonOptions)!;
        Assert.HasCount(1, diagSmall);
        Assert.AreEqual("NGC0002", diagSmall[0].ObjectId);
        Assert.AreEqual(DeepSkyGeometryBasis.CatalogOutline, diagSmall[0].GeometryBasis);

        var hashSmall = Convert.ToHexString(SHA256.HashData(frameSmall.PixelData.Span));
        var hashLarge = Convert.ToHexString(SHA256.HashData(frameLarge.PixelData.Span));
        Assert.AreNotEqual(hashSmall, hashLarge,
            "Changing promoted NGC0002 square outline from 0.08 deg to 0.40 deg must change raw pixel SHA-256.");
        Assert.AreNotEqual(frameSmall.Metadata.Scene!.SceneId, frameLarge.Metadata.Scene!.SceneId,
            "Distinct raw frames from promoted row's outline change must never share a SceneId.");
        Assert.AreNotEqual(frameSmall.Metadata.Extra!["stellarNoiseIdentity"], frameLarge.Metadata.Extra!["stellarNoiseIdentity"]);
        Assert.AreNotEqual(
            frameSmall.Metadata.Scene!.DeepSkyCatalog!.DatabaseSha256,
            frameLarge.Metadata.Scene!.DeepSkyCatalog!.DatabaseSha256,
            "Catalog provenance hash must bind the promoted rendered row's outline, not the displaced row.");
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

                // F8: Read and assert the replay execution's OWN output node directly by execution ID!
                var replayExecutionNode = await reopenedStore.ReadExecutionNodeAsync(
                    replayLease.Execution.ExecutionId, captureId, "scene", token).ConfigureAwait(false);
                Assert.IsNotNull(replayExecutionNode, "Replay execution must record its own completed scene node.");
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, replayExecutionNode.Status);
                Assert.HasCount(1, replayExecutionNode.Outputs);
                var replayOutput = replayExecutionNode.Outputs.Single();
                Assert.AreEqual(committedOutput.OutputIdentitySha256, replayOutput.OutputIdentitySha256);
                Assert.AreEqual(committedOutput.ContentIdentitySha256, replayOutput.ContentIdentitySha256);
                Assert.AreEqual(committedOutput.ArtifactId, replayOutput.ArtifactId);
                Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, replayOutput.ProductSchemaVersion);
                var replayOutputBytes = await File.ReadAllBytesAsync(
                    Path.Combine(tempRoot, replayOutput.PayloadRelativePath), token).ConfigureAwait(false);
                CollectionAssert.AreEqual(expectedSceneBytes, replayOutputBytes,
                    "Replay execution's own output payload must match the live committed v3 scene byte-for-byte.");
                var replayed = ProjectedSceneJson.Parse(replayOutputBytes);
                Assert.IsTrue(replayed.IsValid);
                Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, replayed.Scene!.SchemaVersion);
                Assert.AreEqual(expectedSceneSha, replayed.Scene.SceneIdentitySha256);
                CollectionAssert.AreEqual(expectedSceneBytes, ProjectedSceneJson.Serialize(replayed.Scene));

                // Live node remains intact and consistent with replay execution output.
                var afterReplayNode = await reopenedStore.ReadNodeAsync(captureId, "scene", token).ConfigureAwait(false);
                Assert.IsNotNull(afterReplayNode);
                Assert.HasCount(1, afterReplayNode.Outputs);
                Assert.AreEqual(committedOutput.OutputIdentitySha256, afterReplayNode.Outputs.Single().OutputIdentitySha256);

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

    [TestMethod]
    public async Task FullResolutionAsi174DeepSkyV3GeometrySerializationAndSqlitePersistenceBenchmark()
    {
        // F15 / carried #1191 benchmark: matched full-resolution 1936x1216 baseline (v1 projected scene,
        // DSO rendering & annotations off) vs enabled (v3 projected scene with DSO rendering, footprints,
        // sourced OpenNGC outlines, JSON serialization, and real SQLite + file persistence I/O).
        var catalog = CreateFixtureCatalog(includeHygStar: true);
        var baseline = await MeasureGeometryAndPersistencePipelineAsync(catalog, enableDeepSky: false).ConfigureAwait(false);
        var enabled = await MeasureGeometryAndPersistencePipelineAsync(catalog, enableDeepSky: true).ConfigureAwait(false);

        TestContext.WriteLine(
            $"W1_DSO_1936x1216_PERSIST_BASELINE (v1): schema={baseline.SchemaVersion} sceneBytes={baseline.ScenePayloadBytes} " +
            $"dsoObjects={baseline.DeepSkyObjectCount} outlines={baseline.OutlineCount} outlineParts={baseline.OutlinePartCount} outlineVertices={baseline.OutlineVertexCount} " +
            $"footprints={baseline.FootprintCount} footprintParts={baseline.FootprintPartCount} footprintVertices={baseline.FootprintVertexCount} " +
            $"medianCaptureStageMs={baseline.MedianCaptureStageMs:F2} medianPersistMs={baseline.MedianPersistMs:F2} medianTotalMs={baseline.MedianTotalMs:F2} " +
            $"avgCpuMs={baseline.AvgCpuMs:F2} avgAllocMiB={baseline.AvgAllocMiB:F2} dbBytes={baseline.SqliteDbBytes}");
        TestContext.WriteLine(
            $"W1_DSO_1936x1216_PERSIST_ENABLED (v3): schema={enabled.SchemaVersion} sceneBytes={enabled.ScenePayloadBytes} " +
            $"deltaSceneBytes={enabled.ScenePayloadBytes - baseline.ScenePayloadBytes:+0;-0} " +
            $"dsoObjects={enabled.DeepSkyObjectCount} outlines={enabled.OutlineCount} outlineParts={enabled.OutlinePartCount} outlineVertices={enabled.OutlineVertexCount} " +
            $"footprints={enabled.FootprintCount} footprintParts={enabled.FootprintPartCount} footprintVertices={enabled.FootprintVertexCount} " +
            $"medianCaptureStageMs={enabled.MedianCaptureStageMs:F2} medianPersistMs={enabled.MedianPersistMs:F2} " +
            $"deltaPersistMs={enabled.MedianPersistMs - baseline.MedianPersistMs:+0.00;-0.00} " +
            $"medianTotalMs={enabled.MedianTotalMs:F2} deltaTotalMs={enabled.MedianTotalMs - baseline.MedianTotalMs:+0.00;-0.00} " +
            $"avgCpuMs={enabled.AvgCpuMs:F2} deltaCpuMs={enabled.AvgCpuMs - baseline.AvgCpuMs:+0.00;-0.00} " +
            $"avgAllocMiB={enabled.AvgAllocMiB:F2} deltaAllocMiB={enabled.AvgAllocMiB - baseline.AvgAllocMiB:+0.00;-0.00} " +
            $"dbBytes={enabled.SqliteDbBytes} deltaDbBytes={enabled.SqliteDbBytes - baseline.SqliteDbBytes:+0;-0}");

        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, baseline.SchemaVersion);
        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, enabled.SchemaVersion);
        Assert.IsTrue(enabled.DeepSkyObjectCount >= 4);
        Assert.IsTrue(enabled.OutlineCount >= 1 && enabled.OutlineVertexCount > 0);
        Assert.IsTrue(enabled.FootprintCount >= 2 && enabled.FootprintVertexCount > 0);
        Assert.IsTrue(enabled.OutlineVertexCount <= ProjectedDeepSky.MaximumTotalOutlinePointCount);
        Assert.IsTrue(enabled.FootprintVertexCount <= ProjectedResolvedFootprint.MaximumPointCount * enabled.FootprintCount);
        Assert.IsTrue(enabled.ScenePayloadBytes > baseline.ScenePayloadBytes);
        Assert.IsTrue(enabled.ScenePayloadBytes < 262_144,
            $"Serialized v3 projected scene payload must stay tightly bounded (< 256 KiB); got {enabled.ScenePayloadBytes} B.");
        Assert.IsTrue(enabled.SqliteDbBytes < 16 * 1024 * 1024,
            $"SQLite persistence database size must stay tightly bounded; got {enabled.SqliteDbBytes} B.");
    }

    private static async Task<PersistenceBenchmarkResult> MeasureGeometryAndPersistencePipelineAsync(
        CombinedVirtualCatalog catalog,
        bool enableDeepSky)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"hvo-dso-bench-{(enableDeepSky ? "v3" : "v1")}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var token = CancellationToken.None;
            var baseConfig = CreateModuleConfig(
                CameraPixelFormat.Mono16, 1936, 1216,
                renderDeepSky: enableDeepSky,
                enableDeepSkyAnnotations: enableDeepSky,
                boresightAlt: 60, boresightAz: 180, focalLengthPx: 950,
                background: 20, readNoise: 1.5,
                annotationMaximumOutlines: enableDeepSky ? 16 : 0);
            // Both baseline and enabled retain ProjectedScene step so baseline measures v1 serialization + SQLite I/O!
            var config = baseConfig with
            {
                AgentId = "dso-v3-sqlite-agent",
                Pipeline = new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig("ProjectedScene", "scene", DependsOn: ["$raw"],
                        Options: enableDeepSky
                            ? JsonSerializer.SerializeToElement(new { DeepSkyEnabled = true, DeepSkyMaximumOutlines = 16 }, WebJsonOptions)
                            : null)
                ])
            };

            using var provider = CreateAgentProvider(tempRoot, catalog);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            await ingress.InitializeAsync(token).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            _ = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            var standardLane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static d => d.Name == "standard");
            var handler = provider.GetRequiredService<StandardCaptureLaneHandler>();
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();

            var module = new VirtualSkyCameraModule(
                ExposureTestTimeProvider.ForRendering(),
                provider.GetRequiredService<ICelestialCatalog>(),
                provider.GetRequiredService<IProjectedSceneStore>(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(config, token).ConfigureAwait(false);

            async Task<(double CaptureStageMs, double PersistMs, byte[] SceneBytes)> RunOneAsync(int seq)
            {
                var reqTime = FixtureUtc.AddSeconds(seq * 5);
                var req = new CaptureRequest(reqTime, TimeSpan.FromSeconds(5), CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));
                var swCapture = Stopwatch.StartNew();
                var capture = await module.CaptureAsync(req, token).ConfigureAwait(false);
                var captureStageMs = swCapture.Elapsed.TotalMilliseconds;

                var swPersist = Stopwatch.StartNew();
                var submission = new CaptureLoopSubmission(req, capture, req.RequestedStartUtc, req.TargetInterval, TimeSpan.Zero);
                var accepted = await ingress.AcceptAsync(config, submission, token).ConfigureAwait(false);
                Assert.IsNotNull(accepted);
                var lease = await ingress.ClaimAsync(standardLane, "dso-bench-worker", config, token).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                var captureId = lease.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId;
                var processed = await handler.HandleAsync(lease.Context, token).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
                await ingress.CompleteAsync(lease, token).ConfigureAwait(false);
                var persistMs = swPersist.Elapsed.TotalMilliseconds;

                var node = await store.ReadNodeAsync(captureId, "scene", token).ConfigureAwait(false);
                Assert.IsNotNull(node);
                var sceneBytes = await File.ReadAllBytesAsync(
                    Path.Combine(tempRoot, node.Outputs.Single().PayloadRelativePath), token).ConfigureAwait(false);
                return (captureStageMs, persistMs, sceneBytes);
            }

            // Warmup
            _ = await RunOneAsync(0).ConfigureAwait(false);

            const int runs = 3;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuStart = process.TotalProcessorTime;
            var allocStart = GC.GetTotalAllocatedBytes(true);

            var captureStageMsArr = new double[runs];
            var persistMsArr = new double[runs];
            var totalMsArr = new double[runs];
            byte[] finalSceneBytes = [];
            for (var i = 0; i < runs; i++)
            {
                var (capMs, persMs, bytes) = await RunOneAsync(i + 1).ConfigureAwait(false);
                captureStageMsArr[i] = capMs;
                persistMsArr[i] = persMs;
                totalMsArr[i] = capMs + persMs;
                finalSceneBytes = bytes;
            }

            process.Refresh();
            var avgCpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds / runs;
            var avgAllocMiB = (GC.GetTotalAllocatedBytes(true) - allocStart) / (double)runs / (1024d * 1024d);
            Array.Sort(captureStageMsArr);
            Array.Sort(persistMsArr);
            Array.Sort(totalMsArr);

            var parsed = ProjectedSceneJson.Parse(finalSceneBytes);
            Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
            var scene = parsed.Scene!;
            var outlines = scene.DeepSky?.Outlines ?? [];
            var footprints = scene.ResolvedFootprints ?? [];
            var sqliteFiles = Directory.GetFiles(tempRoot, "*.db*", SearchOption.AllDirectories);
            var dbBytes = sqliteFiles.Sum(static f => new FileInfo(f).Length);

            return new PersistenceBenchmarkResult(
                scene.SchemaVersion,
                finalSceneBytes.Length,
                scene.DeepSky?.Objects.Count ?? 0,
                outlines.Count,
                outlines.Sum(static o => o.Parts.Count),
                outlines.Sum(static o => o.Parts.Sum(static p => p.Points.Count)),
                footprints.Count,
                footprints.Sum(static f => f.Parts.Count),
                footprints.Sum(static f => f.Parts.Sum(static p => p.Points.Count)),
                captureStageMsArr[runs / 2],
                persistMsArr[runs / 2],
                totalMsArr[runs / 2],
                avgCpuMs,
                avgAllocMiB,
                dbBytes);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed record PersistenceBenchmarkResult(
        string SchemaVersion,
        int ScenePayloadBytes,
        int DeepSkyObjectCount,
        int OutlineCount,
        int OutlinePartCount,
        int OutlineVertexCount,
        int FootprintCount,
        int FootprintPartCount,
        int FootprintVertexCount,
        double MedianCaptureStageMs,
        double MedianPersistMs,
        double MedianTotalMs,
        double AvgCpuMs,
        double AvgAllocMiB,
        long SqliteDbBytes);

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
        public int OutlineLookupCount { get; private set; }
        public DeepSkySemantics Semantics => inner.Semantics;
        public IReadOnlyList<DeepSkyObject> Objects => inner.Objects;
        public int AliasCount => inner.AliasCount;
        public IReadOnlyList<DeepSkyTombstone> Tombstones => inner.Tombstones;
        public bool TryGetObject(string objectId, out DeepSkyObject value) => inner.TryGetObject(objectId, out value);
        public IReadOnlyList<DeepSkyOutline> GetOutlines(string objectId)
        {
            OutlineLookupCount++;
            return inner.GetOutlines(objectId);
        }
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
