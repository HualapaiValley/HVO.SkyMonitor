using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

public sealed partial class TransientWorkerRuntimeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HorizonMasksRetainLegacyRectanglesAndStableTemporalIdentity(bool empty)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-horizon-runtime", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            var rig = configuration.Rig with
            {
                ControlPolicy = (configuration.Rig.ControlPolicy ?? new CameraControlPolicy()) with
                { Metering = new CaptureMeteringPolicy { ExcludedRegions = [new(2, 3, 4, 5)] } }
            };
            var site = configuration.Observatory;
            var profile = new HorizonObstructionProfile(HorizonObstructionProfile.CurrentSchemaVersion, "test-horizon", 1, "test-installation",
                HorizonObstructionProfile.ComputeSiteIdentitySha256(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters, configuration.DeploymentLocation?.ToProvenance()),
                HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig), HorizonObstructionSource.Synthetic, 90,
                empty ? [] : [new(0, 90), new(90, 90), new(180, 90), new(270, 90)]);
            configuration = configuration with { Rig = rig with { HorizonObstruction = profile } };
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);
            await StageVirtualFramesAsync(provider, configuration, new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero), 3).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var i = 0; i < 3; i++) Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            var store = provider.GetRequiredService<SqliteTransientRuntimeStore>();
            var frames = await store.LoadWindowAsync(configuration.AgentId!, 3, [-1, 0], CancellationToken.None).ConfigureAwait(false);
            var runtime = new TransientDetectorRuntime(new InMemoryCelestialCatalog([]));
            var sources = await runtime.CreateSourcesAsync(new Dictionary<int, CameraModuleConfig> { [-1] = configuration, [0] = configuration },
                new TransientDetectionOptions(), frames, CancellationToken.None).ConfigureAwait(false);
            var masks = sources[0].Masks;
            var legacy = masks.Single(m => m.Kind == TransientDetectorMaskKind.Obstruction).Mask;
            Assert.IsTrue(Linear16MaskOperations.IsExcluded(legacy, 2, 3));
            Assert.IsTrue(Linear16MaskOperations.IsExcluded(legacy, 5, 7));
            Assert.IsFalse(Linear16MaskOperations.IsExcluded(legacy, 6, 7));
            Assert.IsFalse(Linear16MaskOperations.IsExcluded(legacy, 5, 8));
            var fixedMask = masks.Single(m => m.Kind == TransientDetectorMaskKind.FixedHorizonObstruction);
            var unknown = masks.Single(m => m.Kind == TransientDetectorMaskKind.UnknownHorizonObstruction);
            Assert.AreEqual(!empty, Linear16MaskOperations.IsExcluded(fixedMask.Mask, 32, 24));
            Assert.AreEqual(empty, Linear16MaskOperations.IsExcluded(unknown.Mask, 32, 24));
            Assert.AreEqual(fixedMask.MaskIdentitySha256, sources[-1].Masks.Single(m => m.Kind == fixedMask.Kind).MaskIdentitySha256);
            Assert.AreEqual(unknown.MaskIdentitySha256, sources[-1].Masks.Single(m => m.Kind == unknown.Kind).MaskIdentitySha256);
        }
        finally { Cleanup(root); }
    }
}
