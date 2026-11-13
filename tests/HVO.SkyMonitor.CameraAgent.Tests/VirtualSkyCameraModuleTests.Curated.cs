using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    public async Task OpenNgcCaptureUsesOneImmutableActiveGraphSelectionAndPreservesRawPixels()
    {
        var identity = new CatalogSnapshotIdentity("openngc-fixture", "openngc-fixture-1", "5", "6", new string('a', 64));
        var staging = new RecordingProjectedSceneStagingStore();
        var selected = new TestDeepSkyGraphSelection();
        var module = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), new CuratedVirtualCatalog(identity),
            new ProjectedSceneStore(), stagingStore: staging, graphSelection: selected);
        await using var lifetime = module.ConfigureAwait(false);
        var config = CreateConfig(CameraPixelFormat.Mono16, 64, 64) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new { seed = 1017, fixedSequenceStartUtc = FixtureUtc, fixedSceneUtc = FixtureUtc })),
            Pipeline = new CapturePipelineConfig([new CaptureProcessingStepConfig("ProjectedScene", DependsOn: ["$raw"])])
        };
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still);
        selected.Selection = new("graph-one", config.Pipeline with
        {
            Steps = [config.Pipeline.Steps[0] with
            { Options = JsonSerializer.SerializeToElement(new { deepSkyEnabled = true, deepSkyNgc = false, deepSkyIc = false }) }]
        });
        var first = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var retained = JsonSerializer.SerializeToUtf8Bytes(staging.Scenes.Single().DeepSky);
        selected.Selection = new("graph-two", config.Pipeline with
        {
            Steps = [config.Pipeline.Steps[0] with
            { Options = JsonSerializer.SerializeToElement(new { deepSkyEnabled = true, deepSkyNgc = false, deepSkyMessier = false, deepSkyIc = false }) }]
        });
        var second = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("graph-one", first.Frame!.Metadata.Scene!.CaptureGraphRevisionId);
        Assert.AreEqual("graph-two", second.Frame!.Metadata.Scene!.CaptureGraphRevisionId);
        Assert.AreEqual(identity, first.Frame.Metadata.Scene.DeepSkyCatalog);
        Assert.AreEqual(identity, second.Frame.Metadata.Scene.DeepSkyCatalog);
        Assert.HasCount(1, staging.Scenes[0].DeepSky!.Objects);
        Assert.IsEmpty(staging.Scenes[1].DeepSky!.Objects);
        Assert.AreNotEqual(first.Frame.Metadata.Scene.SceneId, second.Frame.Metadata.Scene.SceneId);
        CollectionAssert.AreEqual(first.Frame.PixelData.ToArray(), second.Frame.PixelData.ToArray());
        CollectionAssert.AreEqual(retained, JsonSerializer.SerializeToUtf8Bytes(staging.Scenes[0].DeepSky));
        Assert.AreEqual(2, selected.Reads);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OpenNgcActivationAndRollbackResolveBothQueriesFromTheCapturedGraph(bool activateGeneral)
    {
        var staging = new RecordingProjectedSceneStagingStore();
        var selected = new TestDeepSkyGraphSelection();
        var module = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), new CuratedVirtualCatalog(),
            new ProjectedSceneStore(), stagingStore: staging, graphSelection: selected);
        await using var lifetime = module.ConfigureAwait(false);
        CapturePipelineConfig Pipeline(bool general) => new([new CaptureProcessingStepConfig("ProjectedScene", DependsOn: ["$raw"],
            Options: JsonSerializer.SerializeToElement(new { deepSkyEnabled = general,
                curatedAnnotations = new CuratedDeepSkyOptions(Enabled: !general) }))]);
        var config = CreateConfig(CameraPixelFormat.Mono16, 64, 64) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
            { seed = 1017, fixedSequenceStartUtc = FixtureUtc, fixedSceneUtc = FixtureUtc })),
            Pipeline = Pipeline(!activateGeneral)
        };
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        foreach (var general in new[] { activateGeneral, !activateGeneral })
        {
            selected.Selection = new(general ? "general" : "curated", Pipeline(general));
            var result = await module.CaptureAsync(new(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(selected.Selection.RevisionId, result.Frame!.Metadata.Scene!.CaptureGraphRevisionId);
            Assert.AreEqual(general ? ProjectedDeepSky.QueryAlgorithmVersion : ProjectedDeepSky.CurrentAlgorithmVersion,
                staging.Scenes[^1].DeepSky!.AlgorithmVersion);
        }
    }

    private sealed class TestDeepSkyGraphSelection : IVirtualSkyGraphSelection
    {
        public VirtualSkyGraphSelection? Selection { get; set; }
        public int Reads { get; private set; }
        public ValueTask<VirtualSkyGraphSelection?> ReadAsync(CameraModuleConfig configuration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult(Selection);
        }
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.Rgb24)]
    public async Task CuratedAnnotationOptInPreservesActualRawPixelsAndRetainsNewScene(CameraPixelFormat format)
    {
        var catalog = new CuratedVirtualCatalog();
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still);
        byte[]? baseline = null;
        string? baselineSceneId = null;
        foreach (var enabled in new[] { false, true })
        {
            var staging = new RecordingProjectedSceneStagingStore();
            var module = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore(), stagingStore: staging);
            await using var lifetime = module.ConfigureAwait(false);
            var config = CreateConfig(format, 64, 64) with
            {
                Module = new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 1017,
                    fixedSequenceStartUtc = FixtureUtc,
                    curatedAnnotations = enabled ? new CuratedDeepSkyOptions(Enabled: true) : null
                })),
                Pipeline = new CapturePipelineConfig([new CaptureProcessingStepConfig("ProjectedScene", DependsOn: ["$raw"])])
            };
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(result.Frame);
            var scene = staging.Scenes.Single();
            if (!enabled)
            {
                baseline = result.Frame.PixelData.ToArray();
                baselineSceneId = result.Frame.Metadata.Scene!.SceneId;
                Assert.IsNull(scene.DeepSky);
            }
            else
            {
                CollectionAssert.AreEqual(baseline!, result.Frame.PixelData.ToArray());
                Assert.AreNotEqual(baselineSceneId, result.Frame.Metadata.Scene!.SceneId);
                Assert.AreEqual(SceneProvenance.DeepSkyProjectedSceneSchemaVersion,
                    result.Frame.Metadata.Scene.ProjectedSceneSchemaVersion);
                Assert.HasCount(1, scene.DeepSky!.Objects);
            }
        }
    }

    [TestMethod]
    public async Task ProjectedSceneStepCuratedAnnotationsApplyWhenTheModuleOmitsThem()
    {
        var staging = new RecordingProjectedSceneStagingStore();
        var module = new VirtualSkyCameraModule(
            ExposureTestTimeProvider.ForRendering(), new CuratedVirtualCatalog(), new ProjectedSceneStore(), stagingStore: staging);
        await using var lifetime = module.ConfigureAwait(false);
        var config = CreateConfig(CameraPixelFormat.Mono16, 64, 64) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 1017,
                fixedSequenceStartUtc = FixtureUtc
            })),
            Pipeline = new CapturePipelineConfig(
            [
                new CaptureProcessingStepConfig(
                    "ProjectedScene",
                    DependsOn: ["$raw"],
                    Options: JsonSerializer.SerializeToElement(new
                    {
                        curatedAnnotations = new { enabled = true, maximumObjects = 128, maximumFootprints = 32, minimumGlyphPixels = 8 }
                    }))
            ])
        };
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(result.Frame);
        Assert.AreEqual(
            SceneProvenance.DeepSkyProjectedSceneSchemaVersion,
            result.Frame.Metadata.Scene!.ProjectedSceneSchemaVersion);
        Assert.HasCount(1, staging.Scenes.Single().DeepSky!.Objects);
    }

    [TestMethod]
    public async Task ModuleCuratedAnnotationsStayAuthoritativeWhenTheStepDisagrees()
    {
        var staging = new RecordingProjectedSceneStagingStore();
        var module = new VirtualSkyCameraModule(
            ExposureTestTimeProvider.ForRendering(), new CuratedVirtualCatalog(), new ProjectedSceneStore(), stagingStore: staging);
        await using var lifetime = module.ConfigureAwait(false);
        var config = CreateConfig(CameraPixelFormat.Mono16, 64, 64) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 1017,
                fixedSequenceStartUtc = FixtureUtc,
                curatedAnnotations = new { enabled = false }
            })),
            Pipeline = new CapturePipelineConfig(
            [
                new CaptureProcessingStepConfig(
                    "ProjectedScene",
                    DependsOn: ["$raw"],
                    Options: JsonSerializer.SerializeToElement(new { curatedAnnotations = new { enabled = true } }))
            ])
        };
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(result.Frame);
        Assert.IsNull(staging.Scenes.Single().DeepSky);
        Assert.AreNotEqual(
            SceneProvenance.DeepSkyProjectedSceneSchemaVersion,
            result.Frame.Metadata.Scene!.ProjectedSceneSchemaVersion);
    }

    private sealed class CuratedVirtualCatalog(CatalogSnapshotIdentity? identity = null) : ICelestialCatalog, IDeepSkyCatalogSource
    {
        private readonly InMemoryCelestialCatalog _stars = CreateCanonicalStarCatalog();
        public IDeepSkyCatalog? DeepSky { get; } = CreateDeepSky(identity);
        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _stars.Query(query);
        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query, CancellationToken cancellationToken = default) =>
            _stars.QueryCandidatesAsync(query, cancellationToken);

        private static DeepSkyCatalog CreateDeepSky(CatalogSnapshotIdentity? identity)
        {
            var midpoint = FixtureUtc.AddMilliseconds(500);
            var item = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G",
                AstronomyTime.LocalMeanSiderealDegrees(midpoint, -113.878) / 15, 35.347, "And",
                null, null, null, null, null, null, null, 31, null, null, null);
            var semantics = new DeepSkySemantics("OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
                new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned",
                "J2000.0", "arcminute", "degrees-north-through-east-0-inclusive-to-180-exclusive",
                "1-widest-2-standard-3-narrowest", "b-mag-per-square-arcsecond-within-25-mag-isophote");
            return new(semantics with { SnapshotIdentity = identity }, [item], [new("NGC 224", item.Id, DeepSkyAliasKinds.Designation),
                new("M31", item.Id, DeepSkyAliasKinds.Messier)], [], []);
        }
    }
}
