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
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task HorizonMasksRetainLegacyRectanglesAndStableTemporalIdentity(bool empty, bool rggb)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-horizon-runtime", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            if (rggb) configuration = configuration with
            {
                Rig = configuration.Rig with
                {
                    Sensor = configuration.Rig.Sensor with
                    {
                        PixelFormat = CameraPixelFormat.BayerRggb16,
                        ColorMode = SensorColorMode.Color,
                        ResponseMode = SensorResponseMode.BayerRaw
                    }
                }
            };
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
            Assert.AreEqual(rggb ? 32 : 64, legacy.Width);
            Assert.IsTrue(Linear16MaskOperations.IsExcluded(legacy, rggb ? 1 : 2, rggb ? 2 : 3));
            if (!rggb)
            {
                Assert.IsTrue(Linear16MaskOperations.IsExcluded(legacy, 5, 7));
                Assert.IsFalse(Linear16MaskOperations.IsExcluded(legacy, 6, 7));
                Assert.IsFalse(Linear16MaskOperations.IsExcluded(legacy, 5, 8));
            }
            var fixedMask = masks.Single(m => m.Kind == TransientDetectorMaskKind.FixedHorizonObstruction);
            var unknown = masks.Single(m => m.Kind == TransientDetectorMaskKind.UnknownHorizonObstruction);
            Assert.AreEqual(!empty, Linear16MaskOperations.IsExcluded(fixedMask.Mask, legacy.Width / 2, legacy.Height / 2));
            Assert.AreEqual(empty, Linear16MaskOperations.IsExcluded(unknown.Mask, legacy.Width / 2, legacy.Height / 2));
            Assert.AreEqual(fixedMask.MaskIdentitySha256, sources[-1].Masks.Single(m => m.Kind == fixedMask.Kind).MaskIdentitySha256);
            Assert.AreEqual(unknown.MaskIdentitySha256, sources[-1].Masks.Single(m => m.Kind == unknown.Kind).MaskIdentitySha256);
            Assert.AreSame(fixedMask.Mask, sources[-1].Masks.Single(m => m.Kind == fixedMask.Kind).Mask,
                "Identical window geometry reuses one bounded mask generation.");
            var clearProfile = new HorizonObstructionProfile(profile.SchemaVersion, profile.ProfileId, 2, profile.InstallationId,
                profile.SiteIdentitySha256, profile.GeometryIdentitySha256, profile.Source, 90,
                [new(0, 0), new(90, 0), new(180, 0), new(270, 0)]);
            var clearConfig = configuration with { Rig = configuration.Rig with { HorizonObstruction = clearProfile } };
            var changedSources = await runtime.CreateSourcesAsync(new Dictionary<int, CameraModuleConfig> { [-1] = configuration, [0] = clearConfig },
                new TransientDetectionOptions(), frames, CancellationToken.None).ConfigureAwait(false);
            var clearMask = changedSources[0].Masks.Single(m => m.Kind == fixedMask.Kind);
            Assert.AreNotSame(changedSources[-1].Masks.Single(m => m.Kind == fixedMask.Kind).Mask, clearMask.Mask,
                "A different profile must regenerate even within the same temporal window.");
            Assert.IsFalse(Linear16MaskOperations.IsExcluded(clearMask.Mask, legacy.Width / 2, legacy.Height / 2));
            var projection = RigProjectionContextFactory.Create(clearConfig.Rig);
            var projector = ProjectorFactory.Create(projection);
            var evaluator = new HorizonObstructionEvaluator(clearProfile, clearConfig.Rig,
                new(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters), frames[0].Manifest.Descriptor.Location);
            var transform = changedSources[0].Input.Descriptor.SourceToDetectorTransform;
            for (var y = 0; y < clearMask.Mask.Height; y++)
                for (var x = 0; x < clearMask.Mask.Width; x++)
                {
                    var direction = projector.Unproject(new((x + .5 - transform.OffsetX) / transform.ScaleX,
                        (y + .5 - transform.OffsetY) / transform.ScaleY));
                    Assert.AreEqual(direction is { } point && evaluator.Evaluate(point) == HorizonObstructionEligibility.Obstructed,
                        Linear16MaskOperations.IsExcluded(clearMask.Mask, x, y), "Detector pixels use the validated source transform.");
                }
            var changed = configuration with
            {
                Rig = configuration.Rig with
                { Orientation = configuration.Rig.Orientation with { RollAdjustmentDegrees = 1 } }
            };
            var rejected = await Assert.ThrowsAsync<TransientWorkerExecutionException>(async () =>
                await runtime.CreateSourcesAsync(new Dictionary<int, CameraModuleConfig> { [-1] = configuration, [0] = changed },
                    new TransientDetectionOptions(), frames, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual("transient-runtime.obstruction-revalidation-required", rejected.ReasonCode);
            Assert.IsFalse(rejected.Retryable);
        }
        finally { Cleanup(root); }
    }
}
