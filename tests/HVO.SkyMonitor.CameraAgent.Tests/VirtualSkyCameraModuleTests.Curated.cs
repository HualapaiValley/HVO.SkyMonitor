using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
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
            var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore(), stagingStore: staging);
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

    private sealed class CuratedVirtualCatalog : ICelestialCatalog, IDeepSkyCatalogSource
    {
        private readonly InMemoryCelestialCatalog _stars = CreateCanonicalStarCatalog();
        public IDeepSkyCatalog? DeepSky { get; } = CreateDeepSky();
        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _stars.Query(query);
        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query, CancellationToken cancellationToken = default) =>
            _stars.QueryCandidatesAsync(query, cancellationToken);

        private static DeepSkyCatalog CreateDeepSky()
        {
            var midpoint = FixtureUtc.AddMilliseconds(500);
            var item = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G",
                AstronomyTime.LocalMeanSiderealDegrees(midpoint, -113.878) / 15, 35.347, "And",
                null, null, null, null, null, null, null, 31, null, null, null);
            var semantics = new DeepSkySemantics("OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
                new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned",
                "J2000.0", "arcminute", "degrees-north-through-east-0-inclusive-to-180-exclusive",
                "1-widest-2-standard-3-narrowest", "b-mag-per-square-arcsecond-within-25-mag-isophote");
            return new(semantics, [item], [new("NGC 224", item.Id, DeepSkyAliasKinds.Designation),
                new("M31", item.Id, DeepSkyAliasKinds.Messier)], [], []);
        }
    }
}
