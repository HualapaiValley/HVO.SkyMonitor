using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class DeepSkyExtendedRenderingQualificationTests
{
    private static readonly DateTimeOffset Utc = new(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation HvoObserver = new(35.347, -113.878, 1000);
    private static readonly CatalogMetadata CatalogMeta = new(
        "fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "CC0-1.0", "2");

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
        "b-mag-per-square-arcsecond-within-25-mag-isophote");

    [TestMethod]
    public async Task FluxNormalizationOccursBeforeClippingAndLostLightIsNeverRenormalized()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var request = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var renderOptions = CreateDeterministicMonoOptions(background: 0);

        var centeredJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));
        var edgeJ2000 = AltAzToJ2000(new AltAzPoint(63.27, 180));
        var centered = CreateDso("NGC0224", "M31", "G", centeredJ2000.RaHours, centeredJ2000.DecDegrees, 60, 24, 35, 4.29, 3.44, 23.63, "Sb", 31);
        var edge = CreateDso("NGC0224-EDGE", "M31-Edge", "G", edgeJ2000.RaHours, edgeJ2000.DecDegrees, 60, 24, 35, 4.29, 3.44, 23.63, "Sb", 31);

        var centeredPlan = DeepSkyRenderPlan.Prepare(request, null, [centered], null, renderOptions,
            CreateSettings(renderOptions, request, samplesPerPixel: 2));
        var edgePlan = DeepSkyRenderPlan.Prepare(request, null, [edge], null, renderOptions,
            CreateSettings(renderOptions, request, samplesPerPixel: 2));

        var expectedRate = renderOptions.MagnitudeZeroElectronsPerSecond * Math.Pow(10, -0.4 * 3.44);
        Assert.AreEqual(expectedRate, centeredPlan.SourceElectronRate, expectedRate * 1e-9);
        Assert.AreEqual(expectedRate, edgePlan.SourceElectronRate, expectedRate * 1e-9);
        Assert.IsTrue(centeredPlan.RetainedElectronRate / centeredPlan.SourceElectronRate > 0.99,
            $"Centered source should retain >99% of flux; got {centeredPlan.RetainedElectronRate / centeredPlan.SourceElectronRate:F6}.");
        Assert.IsTrue(edgePlan.RetainedElectronRate / edgePlan.SourceElectronRate is > 0.25 and < 0.70,
            $"Sensor-edge clipped source should lose roughly half its flux without renormalization; got {edgePlan.RetainedElectronRate / edgePlan.SourceElectronRate:F4}.");

        // Horizon clipping test: point camera at altitude 0 deg so the galaxy straddles the geometric horizon.
        var horizonProj = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 0, BoresightAzimuthDegrees: 180);
        var horizonReq = CreateRequest(horizonProj, HorizonPolicy.GeometricHorizon);
        var onHorizonJ2000 = AltAzToJ2000(new AltAzPoint(0.0, 180));
        var horizonDso = CreateDso("NGC0224-HOR", "M31-Horizon", "G", onHorizonJ2000.RaHours, onHorizonJ2000.DecDegrees, 60, 24, 90, 4.29, 3.44, 23.63, "Sb", 31);
        var horizonPlan = DeepSkyRenderPlan.Prepare(horizonReq, null, [horizonDso], null, renderOptions,
            CreateSettings(renderOptions, horizonReq, samplesPerPixel: 2));
        Assert.AreEqual(expectedRate, horizonPlan.SourceElectronRate, expectedRate * 1e-9);
        Assert.IsTrue(horizonPlan.RetainedElectronRate / horizonPlan.SourceElectronRate is > 0.30 and < 0.65,
            $"Horizon-clipped source must drop below-horizon light; retained={horizonPlan.RetainedElectronRate / horizonPlan.SourceElectronRate:F4}.");

        // Circular aperture clipping test: place source on the circular image-circle edge.
        var fisheyeProj = new ProjectionContext(
            ProjectionModel.EquidistantFisheye, 80, 80, 400, 400, 160, 160,
            ProjectionAperture.Circular, ImageCircleRadiusPixels: 60,
            BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var fisheyeReq = CreateRequest(fisheyeProj, HorizonPolicy.ProjectionOnly);
        var radiusDeg = 60d / 400d * (180d / Math.PI);
        var apertureJ2000 = AltAzToJ2000(new AltAzPoint(60 + radiusDeg, 180));
        var apertureDso = CreateDso("NGC0224-AP", "M31-Aperture", "G", apertureJ2000.RaHours, apertureJ2000.DecDegrees, 90, 60, 0, 4.29, 3.44, 23.63, "Sb", 31);
        var aperturePlan = DeepSkyRenderPlan.Prepare(fisheyeReq, null, [apertureDso], null, renderOptions,
            CreateSettings(renderOptions, fisheyeReq, samplesPerPixel: 2));
        Assert.AreEqual(expectedRate, aperturePlan.SourceElectronRate, expectedRate * 1e-9);
        Assert.IsTrue(aperturePlan.RetainedElectronRate / aperturePlan.SourceElectronRate is > 0.25 and < 0.70,
            $"Aperture-clipped source must lose exterior energy; retained={aperturePlan.RetainedElectronRate / aperturePlan.SourceElectronRate:F4}.");

        for (var y = 0; y < 160; y++)
        {
            for (var x = 0; x < 160; x++)
            {
                if (!fisheyeProj.ContainsSample(x + 0.5, y + 0.5))
                {
                    Assert.AreEqual(0d, aperturePlan.ElectronRate(x, y));
                }
            }
        }

        // Verify adaptive sampling convergence across SamplesPerProjectedPixel = 1, 2, 4.
        var ratesBySampleDensity = new double[3];
        var peaksBySampleDensity = new double[3];
        int[] densities = [1, 2, 4];
        for (var i = 0; i < densities.Length; i++)
        {
            var plan = DeepSkyRenderPlan.Prepare(request, null, [centered], null, renderOptions,
                CreateSettings(renderOptions, request, samplesPerPixel: densities[i]));
            ratesBySampleDensity[i] = plan.RetainedElectronRate;
            peaksBySampleDensity[i] = plan.MaximumElectronRate;
        }
        Assert.AreEqual(ratesBySampleDensity[2], ratesBySampleDensity[0], ratesBySampleDensity[2] * 0.005);
        Assert.AreEqual(ratesBySampleDensity[2], ratesBySampleDensity[1], ratesBySampleDensity[2] * 0.002);
        Assert.AreEqual(peaksBySampleDensity[2], peaksBySampleDensity[1], peaksBySampleDensity[2] * 0.05);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnresolvedPlanetaryNebulaConvergesSmoothlyToExactStellarPsfPath()
    {
        var projection = new ProjectionContext(
            ProjectionModel.EquidistantFisheye, 64, 64, 120, 120, 128, 128,
            ProjectionAperture.Circular, ImageCircleRadiusPixels: 60,
            BoresightAltitudeDegrees: 65, BoresightAzimuthDegrees: 140);
        var layout = Mono16Layout(128, 128);
        var targetJ2000 = AltAzToJ2000(new AltAzPoint(65, 140));
        const double magnitude = 4.0;
        const double bv = 0.0;

        var unresolvedPn = CreateDso("PN-UNRES", "Unresolved PN", "PN", targetJ2000.RaHours, targetJ2000.DecDegrees,
            0.05, 0.05, 0, magnitude + bv, magnitude, null, null, null);
        var equivalentStar = new CelestialCatalogObject("STAR-EQ", "Equivalent Star",
            targetJ2000.RaHours, targetJ2000.DecDegrees, magnitude, bv);

        var starScene = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([equivalentStar]))
            .BuildAsync(CreateRequest(projection, HorizonPolicy.GeometricHorizon)).ConfigureAwait(false);
        var emptyScene = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]))
            .BuildAsync(CreateRequest(projection, HorizonPolicy.GeometricHorizon)).ConfigureAwait(false);

        var baseOptions = CreateDeterministicMonoOptions(background: 0, psfSigma: 1.1, psfRadius: 4.4);
        var pnPlan = DeepSkyRenderPlan.Prepare(emptyScene.Request, null, [unresolvedPn], null, baseOptions,
            CreateSettings(baseOptions, emptyScene.Request));

        var diag = pnPlan.Diagnostics.Single();
        Assert.IsTrue(diag.Admitted);
        Assert.IsTrue(diag.UsedUnresolvedPsfPath, "Unresolved PN must use the stellar PSF path.");

        var starRender = Mono16SceneRenderer.Render(starScene, layout, baseOptions);
        var pnRender = Mono16SceneRenderer.Render(emptyScene, layout, baseOptions with { DeepSkySources = pnPlan });

        var maxAduDiff = MaxMono16Difference(starRender.Pixels.Span, pnRender.Pixels.Span);
        Assert.IsTrue(maxAduDiff <= 1, $"Unresolved PN diverged from stellar PSF by {maxAduDiff} ADU.");

        var compactPn = unresolvedPn with { Id = "PN-COMPACT", MajorAxisArcminutes = 40, MinorAxisArcminutes = 40 };
        var resolvedPn = unresolvedPn with { Id = "PN-RESOLVED", MajorAxisArcminutes = 150, MinorAxisArcminutes = 150 };
        var compactPlan = DeepSkyRenderPlan.Prepare(emptyScene.Request, null, [compactPn], null, baseOptions,
            CreateSettings(baseOptions, emptyScene.Request));
        var resolvedPlan = DeepSkyRenderPlan.Prepare(emptyScene.Request, null, [resolvedPn], null, baseOptions,
            CreateSettings(baseOptions, emptyScene.Request));

        Assert.IsFalse(compactPlan.Diagnostics.Single().UsedUnresolvedPsfPath);
        Assert.IsFalse(resolvedPlan.Diagnostics.Single().UsedUnresolvedPsfPath);
        Assert.AreEqual(pnPlan.SourceElectronRate, compactPlan.SourceElectronRate, pnPlan.SourceElectronRate * 1e-9);
        Assert.AreEqual(pnPlan.SourceElectronRate, resolvedPlan.SourceElectronRate, pnPlan.SourceElectronRate * 1e-9);
        Assert.IsTrue(pnPlan.MaximumElectronRate > compactPlan.MaximumElectronRate &&
                      compactPlan.MaximumElectronRate > resolvedPlan.MaximumElectronRate,
            $"Peak surface brightness should decrease smoothly with angular size: {pnPlan.MaximumElectronRate:F2} > {compactPlan.MaximumElectronRate:F2} > {resolvedPlan.MaximumElectronRate:F2}.");
    }

    [TestMethod]
    public async Task SurfaceBrightnessExtentExposureBackgroundVignettingCloudsAndBinningInvariantsHold()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1800, 1800, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var layout = Mono16Layout(160, 160);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));
        var offAxisJ2000 = AltAzToJ2000(new AltAzPoint(61.8, 182.0));
        var smallNebula = CreateDso("NEB-SMALL", "NEB-SMALL", "EmN", centerJ2000.RaHours, centerJ2000.DecDegrees, 40, 40, 0, 1.5, 1.0, null, null, null);
        var doubleNebula = CreateDso("NEB-DOUBLE", "NEB-DOUBLE", "EmN", centerJ2000.RaHours, centerJ2000.DecDegrees, 80, 80, 0, 1.5, 1.0, null, null, null);
        var offAxisGalaxy = CreateDso("G-OFF", "G-OFF", "G", offAxisJ2000.RaHours, offAxisJ2000.DecDegrees, 20, 20, 0, 2.5, 2.0, null, "Sc", null);
        var scene = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]))
            .BuildAsync(CreateRequest(projection, HorizonPolicy.GeometricHorizon)).ConfigureAwait(false);

        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, gain: 1.0);
        var smallPlan = DeepSkyRenderPlan.Prepare(scene.Request, null, [smallNebula], null, opts, CreateSettings(opts, scene.Request));
        var doublePlan = DeepSkyRenderPlan.Prepare(scene.Request, null, [doubleNebula], null, opts, CreateSettings(opts, scene.Request));

        // 1. Extent/surface-brightness invariant: 2x angular diameter => ~4x area => ~0.25x peak and central-aperture surface brightness, same integrated flux.
        Assert.AreEqual(smallPlan.SourceElectronRate, doublePlan.SourceElectronRate, smallPlan.SourceElectronRate * 1e-9);
        var peakRatio = doublePlan.MaximumElectronRate / smallPlan.MaximumElectronRate;
        Assert.IsTrue(peakRatio is > 0.23 and < 0.28, $"Expected ~0.25x peak SB at 2x diameter, got {peakRatio:F4}.");
        double CoreMean(DeepSkyRenderPlan p)
        {
            var sum = 0d;
            for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                    sum += p.ElectronRate(80 + dx, 80 + dy);
            return sum / 25d;
        }
        var coreRatio = CoreMean(doublePlan) / CoreMean(smallPlan);
        Assert.IsTrue(coreRatio is > 0.23 and < 0.29, $"Expected ~0.25x core SB at 2x diameter, got {coreRatio:F4}.");

        // 2. Exposure scaling invariant: 2x exposure => 2x excess ADU (plan bound to exact exposure options).
        var opts2s = opts with { ExposureSeconds = 2.0 };
        var doublePlan2s = DeepSkyRenderPlan.Prepare(scene.Request, null, [doubleNebula], null, opts2s, CreateSettings(opts2s, scene.Request));
        var render1s = Mono16SceneRenderer.Render(scene, layout, opts with { ExposureSeconds = 1.0, DeepSkySources = doublePlan });
        var render2s = Mono16SceneRenderer.Render(scene, layout, opts2s with { DeepSkySources = doublePlan2s });
        var sum1s = SumMono16(render1s.Pixels.Span);
        var sum2s = SumMono16(render2s.Pixels.Span);
        Assert.IsTrue(sum1s > 1000, $"Expected meaningful signal, got {sum1s}.");
        Assert.AreEqual(2.0, sum2s / (double)sum1s, 0.02);

        // 3. Sky background & SNR admission/rejection invariant (same minSnr threshold on bright vs dark sky).
        var faintGalaxy = CreateDso("G-FAINT", "G-FAINT", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 30, 30, 0, 6.0, 5.5, null, "Sb", null);
        var darkSkyOpts = CreateDeterministicMonoOptions(background: 0.5, exposureSeconds: 10.0, readNoise: 1.5);
        var brightSkyOpts = CreateDeterministicMonoOptions(background: 5000.0, exposureSeconds: 10.0, readNoise: 1.5);
        var brightSkyPlan = DeepSkyRenderPlan.Prepare(scene.Request, null, [faintGalaxy], null, brightSkyOpts,
            CreateSettings(brightSkyOpts, scene.Request, minSnr: 5.0));
        Assert.IsFalse(brightSkyPlan.Diagnostics.Single().Admitted);
        Assert.AreEqual("annotation-only-below-snr", brightSkyPlan.Diagnostics.Single().Reason);
        Assert.AreEqual(0d, brightSkyPlan.RetainedElectronRate);
        var darkSkyPlan = DeepSkyRenderPlan.Prepare(scene.Request, null, [faintGalaxy], null, darkSkyOpts,
            CreateSettings(darkSkyOpts, scene.Request, minSnr: 5.0));
        Assert.IsTrue(darkSkyPlan.Diagnostics.Single().Admitted);

        // 4. Vignetting invariant: off-axis galaxy loses signal proportionally to VignettingStrength.
        var offAxisPlan = DeepSkyRenderPlan.Prepare(scene.Request, null, [offAxisGalaxy], null, opts, CreateSettings(opts, scene.Request));
        var optsVig = opts with { VignettingStrength = 0.5 };
        var offAxisPlanVig = DeepSkyRenderPlan.Prepare(scene.Request, null, [offAxisGalaxy], null, optsVig, CreateSettings(optsVig, scene.Request));
        var noVig = Mono16SceneRenderer.Render(scene, layout, opts with { VignettingStrength = 0, DeepSkySources = offAxisPlan });
        var withVig = Mono16SceneRenderer.Render(scene, layout, optsVig with { DeepSkySources = offAxisPlanVig });
        Assert.IsTrue(SumMono16(withVig.Pixels.Span) < SumMono16(noVig.Pixels.Span) * 0.90,
            "Off-axis deep-sky flux must undergo optical vignetting.");

        // 5. Cloud attenuation invariant (uniform opacity = 0.6, scatter = 0 => ~0.4x flux).
        var cloudScenario = new VirtualCloudScenarioDefinition
        {
            ScenarioId = "cloud-attenuation-test",
            ScenarioVersion = "1",
            Seed = 42,
            EpochUtc = Utc,
            SpatialFrequency = 0.5,
            Octaves = 1,
            EdgeSoftness = 0.05,
            HorizonFadeDegrees = 1,
            TemporalSampleCount = 1,
            Keyframes = [new VirtualCloudKeyframe { OffsetSeconds = 0, Coverage = 1.0, MaximumOpacity = 0.6, ScatterFraction = 0.0 }]
        };
        var cloudContext = new VirtualCloudRenderContext(new VirtualCloudField(cloudScenario), Utc, TimeSpan.FromSeconds(1));
        var optsCloud = opts with { Cloud = cloudContext };
        var doublePlanCloud = DeepSkyRenderPlan.Prepare(scene.Request, null, [doubleNebula], null, optsCloud, CreateSettings(optsCloud, scene.Request));
        var clouded = Mono16SceneRenderer.Render(scene, layout, optsCloud with { DeepSkySources = doublePlanCloud });
        var cloudRatio = SumMono16(clouded.Pixels.Span) / (double)sum1s;
        Assert.IsTrue(cloudRatio is > 0.35 and < 0.45, $"Expected ~0.40 cloud transmission, got {cloudRatio:F4}.");

        // 6. Binning invariant via MonoDigitalReadoutRenderer (2x2 DigitalSumV1 & DigitalAverageV1) + recomputed hash (F14).
        var binnedLayout = Mono16Layout(80, 80);
        var readoutSum = new SensorReadoutProfile(new SensorCrop(0, 0, 160, 160), 2, 2, FrameBinningAlgorithm.DigitalSumV1,
            CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.IdentityV1, FrameLevelCodeSpace.NativeSample, 0, 65535, 160);
        var readoutAvg = readoutSum with { BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1 };

        var binnedSum = MonoDigitalReadoutRenderer.Apply(render1s, layout, binnedLayout, readoutSum, 16);
        var binnedAvg = MonoDigitalReadoutRenderer.Apply(render1s, layout, binnedLayout, readoutAvg, 16);
        Assert.AreEqual(sum1s, SumMono16(binnedSum.Pixels.Span), sum1s * 0.005);
        Assert.AreEqual(sum1s / 4.0, SumMono16(binnedAvg.Pixels.Span), sum1s * 0.02);

        var nativeFp = render1s.DeepSkyDiagnostics!.Single().Footprint!;
        var binnedFp = binnedSum.DeepSkyDiagnostics!.Single().Footprint!;
        Assert.AreEqual(nativeFp.SourcePixel.X / 2.0, binnedFp.SourcePixel.X, 1e-9);
        Assert.AreEqual(nativeFp.SourcePixel.Y / 2.0, binnedFp.SourcePixel.Y, 1e-9);
        Assert.AreEqual(nativeFp.MinimumX / 2, binnedFp.MinimumX);
        Assert.AreEqual(nativeFp.MaximumY / 2, binnedFp.MaximumY);
        Assert.AreEqual(
            DeepSkyRenderPlan.ComputeDiagnosticsSha256(binnedSum.DeepSkyDiagnostics!),
            binnedSum.DeepSkyStatistics!.DiagnosticsSha256,
            "Readout renderer must recompute DiagnosticsSha256 over the final binned diagnostics.");
        Assert.AreNotEqual(render1s.DeepSkyStatistics!.DiagnosticsSha256, binnedSum.DeepSkyStatistics!.DiagnosticsSha256);
    }

    [TestMethod]
    public async Task AllVersionedProfilesOpenClusterHygDeduplicationDarkNebulaAndMissingPhotometryWork()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 100, 100, 1600, 1600, 200, 200,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var layout = Mono16Layout(200, 200);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 2.0);
        var emptyScene = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(req).ConfigureAwait(false);

        // 1. M31-like Galaxy: PA rotation (0 deg vs 90 deg) rotates image extent cleanly.
        var g0 = CreateDso("M31-PA0", "M31 PA0", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 60, 18, 0, 4.3, 3.44, 23.63, "Sb", 31);
        var g90 = g0 with { Id = "M31-PA90", PositionAngleDegrees = 90 };
        var planG0 = DeepSkyRenderPlan.Prepare(req, null, [g0], null, opts, CreateSettings(opts, req));
        var planG90 = DeepSkyRenderPlan.Prepare(req, null, [g90], null, opts, CreateSettings(opts, req));
        var fp0 = planG0.Diagnostics.Single().Footprint!;
        var fp90 = planG90.Diagnostics.Single().Footprint!;
        var span0X = fp0.MaximumX - fp0.MinimumX;
        var span0Y = fp0.MaximumY - fp0.MinimumY;
        var span90X = fp90.MaximumX - fp90.MinimumX;
        var span90Y = fp90.MaximumY - fp90.MinimumY;
        Assert.IsTrue((span0Y > span0X && span90X > span90Y) || (span0X > span0Y && span90Y > span90X),
            $"90-degree PA rotation must swap dominant screen axis: PA0=({span0X}x{span0Y}), PA90=({span90X}x{span90Y}).");
        Assert.AreEqual(DeepSkySimulationParameters.CurrentSchemaVersion, planG0.Diagnostics.Single().ProfileSchemaVersion);
        Assert.IsTrue(planG0.Diagnostics.Single().IsSimulationParameter);

        // 2. M13-like Globular Cluster: concentrated core + deterministic bounded granularity.
        var m13 = CreateDso("NGC6205", "M13", "GCl", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, null, 6.4, 5.8, null, null, 13);
        var planM13A = DeepSkyRenderPlan.Prepare(req, null, [m13], null, opts, CreateSettings(opts, req));
        var planM13B = DeepSkyRenderPlan.Prepare(req, null, [m13], null, opts, CreateSettings(opts, req));
        Assert.AreEqual(DeepSkySourceProfileResolver.GlobularMorphologyModel, planM13A.Diagnostics.Single().MorphologyModel);
        Assert.AreEqual(planM13A.Statistics.DiagnosticsSha256, planM13B.Statistics.DiagnosticsSha256);
        Assert.IsTrue(planM13A.ElectronRate(100, 100) > planM13A.ElectronRate(100, 106), "Globular cluster must peak at core.");

        // 3. Supernova Remnant (SNR): limb-brightened shell -> ring brighter than interior center.
        var snr = CreateDso("NGC6960-SYN", "Veil Shell", "SNR", centerJ2000.RaHours, centerJ2000.DecDegrees, 48, 48, 0, 5.5, 5.0, null, null, null);
        var planSnr = DeepSkyRenderPlan.Prepare(req, null, [snr], null, opts, CreateSettings(opts, req));
        Assert.AreEqual(DeepSkySourceProfileResolver.SupernovaRemnantMorphologyModel, planSnr.Diagnostics.Single().MorphologyModel);
        var centerRate = planSnr.ElectronRate(100, 100);
        var maxRingRate = 0d;
        for (var dx = 7; dx <= 11; dx++) maxRingRate = Math.Max(maxRingRate, planSnr.ElectronRate(100 + dx, 100));
        Assert.IsTrue(maxRingRate > centerRate * 1.5, $"SNR shell limb ({maxRingRate:F3}) must brighten relative to interior ({centerRate:F3}).");

        // 4. M45-like Open Cluster: HYG star double-counting prevention (zero HYG -> partial HYG -> full HYG).
        var m45 = CreateDso("Mel022", "M45", "OCl", centerJ2000.RaHours, centerJ2000.DecDegrees, 60, 60, 0, null, 3.0, null, null, 45);
        var m45Rate = opts.MagnitudeZeroElectronsPerSecond * Math.Pow(10, -0.4 * 3.0);
        var halfStar = new CelestialCatalogObject("HIP 17702-HALF", "AlcyoneHalf", centerJ2000.RaHours, centerJ2000.DecDegrees, 3.752575, -0.09);
        var fullStar = new CelestialCatalogObject("HIP 17702-FULL", "AlcyoneFull", centerJ2000.RaHours, centerJ2000.DecDegrees, 2.5, -0.09);

        var planOclNone = DeepSkyRenderPlan.Prepare(req, null, [m45], [], opts, CreateSettings(opts, req));
        var planOclPartial = DeepSkyRenderPlan.Prepare(req, null, [m45], [halfStar], opts, CreateSettings(opts, req));
        var planOclFull = DeepSkyRenderPlan.Prepare(req, null, [m45], [fullStar], opts, CreateSettings(opts, req));

        Assert.AreEqual(0, planOclNone.Diagnostics.Single().MatchedHygStarCount);
        Assert.IsTrue(planOclNone.Diagnostics.Single().UnresolvedMemberCount > 0);
        Assert.AreEqual(m45Rate, planOclNone.SourceElectronRate, m45Rate * 1e-6);

        Assert.AreEqual(1, planOclPartial.Diagnostics.Single().MatchedHygStarCount);
        Assert.AreEqual(0.5, planOclPartial.Diagnostics.Single().MatchedHygFluxFraction, 0.01);
        Assert.AreEqual(m45Rate * 0.5, planOclPartial.SourceElectronRate, m45Rate * 0.01);

        Assert.IsFalse(planOclFull.Diagnostics.Single().Admitted);
        Assert.AreEqual("annotation-only-hyg-flux-covered", planOclFull.Diagnostics.Single().Reason);
        Assert.AreEqual(0d, planOclFull.RetainedElectronRate);

        // 5. M42-like Composite Cluster+Nebula (Cl+N) with catalog outline polygon.
        var m42 = CreateDso("NGC1976", "M42", "Cl+N", centerJ2000.RaHours, centerJ2000.DecDegrees, 60, 45, 0, 4.0, 4.0, null, null, 42);
        var outline = CreateSquareOutline("NGC1976", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.35);
        var m42Catalog = new DeepSkyCatalog(OpenNgcSemantics, [m42],
            [new DeepSkyAlias(m42.Designation, m42.Id, DeepSkyAliasKinds.Designation), new DeepSkyAlias("M42", m42.Id, DeepSkyAliasKinds.Messier)],
            [], [outline]);
        var planM42 = DeepSkyRenderPlan.Prepare(req, m42Catalog, [m42], [halfStar], opts, CreateSettings(opts, req));
        var m42Diag = planM42.Diagnostics.Single();
        Assert.IsTrue(m42Diag.Admitted);
        Assert.AreEqual(DeepSkyGeometryBasis.CatalogOutline, m42Diag.GeometryBasis);
        Assert.AreEqual(DeepSkySourceProfileResolver.CompositeMorphologyModel, m42Diag.MorphologyModel);
        Assert.AreEqual(1, m42Diag.MatchedHygStarCount);

        // 6. Missing geometry (unoriented ellipse without PA or outline) and missing photometry (NGC2023 with valid geometry): strictly annotation-only unless synthetic scenario overrides.
        var unorientedEllipse = CreateDso("NGC2023-NOPA", "NGC2023-NOPA", "RfN", centerJ2000.RaHours, centerJ2000.DecDegrees, 10, 8, null, null, 7.0, null, null, null);
        var unorientedPlan = DeepSkyRenderPlan.Prepare(req, null, [unorientedEllipse], null, opts, CreateSettings(opts, req));
        Assert.IsFalse(unorientedPlan.Diagnostics.Single().Admitted);
        Assert.AreEqual("annotation-only-missing-geometry", unorientedPlan.Diagnostics.Single().Reason);

        var ngc2023 = CreateDso("NGC2023", "NGC 2023", "RfN", centerJ2000.RaHours, centerJ2000.DecDegrees, 10, 10, null, null, null, null, null, null);
        var missingPlan = DeepSkyRenderPlan.Prepare(req, null, [ngc2023], null, opts,
            CreateSettings(opts, req, allowB25Fallback: true));
        Assert.IsFalse(missingPlan.Diagnostics.Single().Admitted);
        Assert.AreEqual("annotation-only-missing-photometry", missingPlan.Diagnostics.Single().Reason);
        var emptyRender = Mono16SceneRenderer.Render(emptyScene, layout, opts);
        var missingRender = Mono16SceneRenderer.Render(emptyScene, layout, opts with { DeepSkySources = missingPlan });
        CollectionAssert.AreEqual(emptyRender.Pixels.ToArray(), missingRender.Pixels.ToArray());

        var syntheticScenario = new DeepSkySyntheticScenario(
            "synth-ngc2023-v1",
            Overrides: [new DeepSkySyntheticSourceOverride("NGC2023", VisualMagnitude: 6.0, ColorIndex: -0.1)]);
        var synthPlan = DeepSkyRenderPlan.Prepare(req, null, [ngc2023], null, opts,
            CreateSettings(opts, req, syntheticScenario: syntheticScenario));
        Assert.IsTrue(synthPlan.Diagnostics.Single().Admitted);
        Assert.AreEqual(DeepSkyPhotometryBasis.SyntheticScenario, synthPlan.Diagnostics.Single().PhotometryBasis);
        Assert.AreEqual(DeepSkySpectralOrigin.SyntheticScenario, synthPlan.Diagnostics.Single().SpectralOrigin);
        Assert.IsNull(ngc2023.VMagnitude, "Synthetic scenario must never mutate catalog facts.");

        // 7. Dark Nebula (DrkN): never attenuates terrestrial skyglow; attenuates compatible diffuse nebula & celestial diffuse background.
        var darkNeb = CreateDso("LDN-TEST", "Dark Cloud", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, null, null, null, null, null);
        var terrestrialOpts = CreateDeterministicMonoOptions(background: 500, exposureSeconds: 1.0);
        var darkNoDiffuse = DeepSkyRenderPlan.Prepare(req, null, [darkNeb], null, terrestrialOpts,
            CreateSettings(terrestrialOpts, req, celestialDiffuseBg: 0));
        Assert.IsFalse(darkNoDiffuse.Diagnostics.Single().Admitted);
        Assert.AreEqual("annotation-only-dark-no-compatible-background", darkNoDiffuse.Diagnostics.Single().Reason);
        var skyWithoutDark = Mono16SceneRenderer.Render(emptyScene, layout, terrestrialOpts);
        var skyWithDark = Mono16SceneRenderer.Render(emptyScene, layout, terrestrialOpts with { DeepSkySources = darkNoDiffuse });
        CollectionAssert.AreEqual(skyWithoutDark.Pixels.ToArray(), skyWithDark.Pixels.ToArray(),
            "Dark nebula without compatible celestial diffuse background must not attenuate foreground skyglow.");

        var darkWithDeclaredBg = DeepSkyRenderPlan.Prepare(req, null, [darkNeb], null, opts,
            CreateSettings(opts, req, celestialDiffuseBg: 100.0));
        var darkDiagBg = darkWithDeclaredBg.Diagnostics.Single();
        Assert.IsTrue(darkDiagBg.Admitted);
        Assert.IsTrue(darkDiagBg.AttenuatedDiffuseElectronRate > 0);
        Assert.IsTrue(darkWithDeclaredBg.ElectronRate(100, 100) < darkWithDeclaredBg.ElectronRate(10, 10) * 0.5,
            "Dark nebula must attenuate celestial diffuse background inside its footprint while leaving outside untouched.");

        var bgNebula = CreateDso("EMN-BG", "Emission Sheet", "EmN", centerJ2000.RaHours, centerJ2000.DecDegrees, 80, 80, 0, 3.0, 3.0, null, null, null);
        var onlyNebPlan = DeepSkyRenderPlan.Prepare(req, null, [bgNebula], null, opts, CreateSettings(opts, req));
        var nebWithDarkPlan = DeepSkyRenderPlan.Prepare(req, null, [bgNebula, darkNeb], null, opts, CreateSettings(opts, req));
        var darkDiagNeb = nebWithDarkPlan.Diagnostics.Single(static d => d.ObjectId == "LDN-TEST");
        Assert.IsTrue(darkDiagNeb.Admitted);
        Assert.IsTrue(darkDiagNeb.AttenuatedDiffuseElectronRate > 0);
        Assert.IsTrue(nebWithDarkPlan.ElectronRate(100, 100) < onlyNebPlan.ElectronRate(100, 100) * 0.6,
            "Dark nebula must attenuate overlapping diffuse nebula irradiance.");
    }

    [TestMethod]
    public async Task AntipodalOutlineAndProjectionDistortionBehaveSafelyAndAccurately()
    {
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));
        var offAxisJ2000 = AltAzToJ2000(new AltAzPoint(25, 180)); // 35 deg off-axis from altitude 60 deg boresight

        var centerRaDeg = centerJ2000.RaHours * 15d;
        var antiRaDeg = (centerRaDeg + 180d) % 360d;
        var antiDecDeg = -centerJ2000.DecDegrees;
        var badRing = new DeepSkyOutlineRing(
        [
            new DeepSkyOutlinePoint(centerRaDeg, centerJ2000.DecDegrees),
            new DeepSkyOutlinePoint(antiRaDeg, antiDecDeg),
            new DeepSkyOutlinePoint((centerRaDeg + 1.5) % 360d, centerJ2000.DecDegrees + 0.1),
            new DeepSkyOutlinePoint(centerRaDeg, centerJ2000.DecDegrees)
        ]);
        var badOutlineWithAxes = new DeepSkyOutline("DSO-BAD-AXES", DeepSkyOutline.WidestLevel, [badRing]);
        var badOutlineNoAxes = new DeepSkyOutline("DSO-BAD-NOAXES", DeepSkyOutline.WidestLevel, [badRing]);

        var dsoFallback = CreateDso("DSO-BAD-AXES", "DSO-BAD-AXES", "Neb", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 16, 0, 5.0, 5.0, null, null, null);
        var dsoSkip = CreateDso("DSO-BAD-NOAXES", "DSO-BAD-NOAXES", "Neb", centerJ2000.RaHours, centerJ2000.DecDegrees, null, null, null, 5.0, 5.0, null, null, null);
        var sibling = CreateDso("DSO-SIBLING", "DSO-SIBLING", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 16, 30, 5.0, 5.0, null, "Sb", null);
        var catalog = new DeepSkyCatalog(OpenNgcSemantics, [dsoFallback, dsoSkip, sibling],
            [
                new DeepSkyAlias(dsoFallback.Designation, dsoFallback.Id, DeepSkyAliasKinds.Designation),
                new DeepSkyAlias(dsoSkip.Designation, dsoSkip.Id, DeepSkyAliasKinds.Designation),
                new DeepSkyAlias(sibling.Designation, sibling.Id, DeepSkyAliasKinds.Designation)
            ],
            [], [badOutlineWithAxes, badOutlineNoAxes]);

        var proj = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1200, 1200, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(proj, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0);

        var plan = DeepSkyRenderPlan.Prepare(req, catalog, [dsoFallback, dsoSkip, sibling], null, opts, CreateSettings(opts, req));
        var fallbackDiag = plan.Diagnostics.Single(static d => d.ObjectId == "DSO-BAD-AXES");
        var skipDiag = plan.Diagnostics.Single(static d => d.ObjectId == "DSO-BAD-NOAXES");
        var siblingDiag = plan.Diagnostics.Single(static d => d.ObjectId == "DSO-SIBLING");

        Assert.IsTrue(fallbackDiag.Admitted);
        Assert.AreEqual(DeepSkyGeometryBasis.CatalogOrientedEllipse, fallbackDiag.GeometryBasis);
        Assert.IsFalse(skipDiag.Admitted);
        Assert.AreEqual("annotation-only-invalid-antipodal-outline", skipDiag.Reason);
        Assert.IsTrue(siblingDiag.Admitted, "Invalid outline on one object must never abort sibling objects.");

        var offAxisDso = CreateDso("OFF-DIST", "OFF-DIST", "EmN", offAxisJ2000.RaHours, offAxisJ2000.DecDegrees, 240, 240, 0, 4.5, 4.0, null, null, null);
        var perspZero = new ProjectionContext(ProjectionModel.Perspective, 160, 160, 200, 200, 320, 320,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180, RadialDistortionK1: 0);
        var perspPos = perspZero with { RadialDistortionK1 = 0.08 };
        var perspNeg = perspZero with { RadialDistortionK1 = -0.08 };
        var fisheye = new ProjectionContext(ProjectionModel.EquidistantFisheye, 160, 160, 200, 200, 320, 320,
            ProjectionAperture.Circular, ImageCircleRadiusPixels: 158, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);

        var pZero = DeepSkyRenderPlan.Prepare(CreateRequest(perspZero, HorizonPolicy.GeometricHorizon), null, [offAxisDso], null, opts,
            CreateSettings(opts, CreateRequest(perspZero, HorizonPolicy.GeometricHorizon)));
        var pPos = DeepSkyRenderPlan.Prepare(CreateRequest(perspPos, HorizonPolicy.GeometricHorizon), null, [offAxisDso], null, opts,
            CreateSettings(opts, CreateRequest(perspPos, HorizonPolicy.GeometricHorizon)));
        var pNeg = DeepSkyRenderPlan.Prepare(CreateRequest(perspNeg, HorizonPolicy.GeometricHorizon), null, [offAxisDso], null, opts,
            CreateSettings(opts, CreateRequest(perspNeg, HorizonPolicy.GeometricHorizon)));
        var pFish = DeepSkyRenderPlan.Prepare(CreateRequest(fisheye, HorizonPolicy.GeometricHorizon), null, [offAxisDso], null, opts,
            CreateSettings(opts, CreateRequest(fisheye, HorizonPolicy.GeometricHorizon)));

        Assert.AreEqual(pZero.SourceElectronRate, pPos.SourceElectronRate, 1e-9);
        Assert.AreEqual(pZero.SourceElectronRate, pNeg.SourceElectronRate, 1e-9);
        Assert.AreEqual(pZero.SourceElectronRate, pFish.SourceElectronRate, 1e-9);
        Assert.IsTrue(pPos.RetainedElectronRate / pPos.SourceElectronRate > 0.90);
        Assert.IsTrue(pNeg.RetainedElectronRate / pNeg.SourceElectronRate > 0.95);
        Assert.IsTrue(pFish.RetainedElectronRate / pFish.SourceElectronRate > 0.98);

        var rZero = double.Hypot(pZero.Diagnostics.Single().Footprint!.DepositedCentroid.X - 160, pZero.Diagnostics.Single().Footprint!.DepositedCentroid.Y - 160);
        var rPos = double.Hypot(pPos.Diagnostics.Single().Footprint!.DepositedCentroid.X - 160, pPos.Diagnostics.Single().Footprint!.DepositedCentroid.Y - 160);
        var rNeg = double.Hypot(pNeg.Diagnostics.Single().Footprint!.DepositedCentroid.X - 160, pNeg.Diagnostics.Single().Footprint!.DepositedCentroid.Y - 160);
        Assert.IsTrue(rPos > rZero && rZero > rNeg, $"Positive k1 must push off-axis centroid outward and negative k1 inward: {rPos:F2} > {rZero:F2} > {rNeg:F2}.");
        Assert.IsTrue(pZero.MaximumElectronRate < pFish.MaximumElectronRate * 0.70,
            $"Perspective off-axis solid-angle stretch (cos^-3 35 deg) must yield substantially lower peak pixel rate than equidistant fisheye ({pZero.MaximumElectronRate:F2} vs {pFish.MaximumElectronRate:F2}).");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Mono16Rgb24AndBayerRggb16RenderDeterministicallyWithSharedGeometryAndSpectralRatios()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1500, 1500, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var emptyScene = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(req).ConfigureAwait(false);

        var galPos = AltAzToJ2000(new AltAzPoint(59.0, 179.0));
        var nebPos = AltAzToJ2000(new AltAzPoint(61.0, 181.0));
        var galaxy = CreateDso("NGC0224", "M31", "G", galPos.RaHours, galPos.DecDegrees, 36, 18, 35, 4.29, 3.44, 23.63, "Sb", 31);
        var nebula = CreateDso("NGC1976", "M42", "EmN", nebPos.RaHours, nebPos.DecDegrees, 36, 36, 0, null, 3.44, null, null, 42);

        var monoBase = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var monoPlan = DeepSkyRenderPlan.Prepare(req, null, [galaxy, nebula], null, monoBase, CreateSettings(monoBase, req));

        var monoLayout = Mono16Layout(160, 160);
        var mono1 = Mono16SceneRenderer.Render(emptyScene, monoLayout, monoBase with { DeepSkySources = monoPlan });
        var mono2 = Mono16SceneRenderer.Render(emptyScene, monoLayout, monoBase with { DeepSkySources = monoPlan });
        CollectionAssert.AreEqual(mono1.Pixels.ToArray(), mono2.Pixels.ToArray());
        StringAssert.Contains(mono1.AlgorithmVersion, DeepSkyRenderPlan.AlgorithmVersion, StringComparison.Ordinal);

        var rgbLayout = new ImageLayout(160, 160, CameraPixelFormat.Rgb24, 160 * 3);
        var rgbBase = new Rgb24CompatibilityRenderOptions
        {
            ExposureSeconds = 1.0,
            Gain = 1.0,
            MagnitudeZeroElectronsPerSecond = 10_000,
            BackgroundElectronsPerSecond = 0,
            PsfSigmaPixels = 1.0,
            PsfRadiusPixels = 4.0,
            VignettingStrength = 0,
            Bias = 0,
            ReadNoiseStandardDeviation = 0,
            ShotNoiseEnabled = false,
            DarkCurrentElectronsPerSecond = 0,
            Seed = 777
        };
        var rgbPlan = DeepSkyRenderPlan.Prepare(req, null, [galaxy, nebula], null, rgbBase, CreateSettings(rgbBase, req));
        var rgbOpts = rgbBase with { DeepSkySources = rgbPlan };
        var rgb1 = Rgb24CompatibilityRenderer.Render(emptyScene, rgbLayout, rgbOpts);
        var rgb2 = Rgb24CompatibilityRenderer.Render(emptyScene, rgbLayout, rgbOpts);
        CollectionAssert.AreEqual(rgb1.Pixels.ToArray(), rgb2.Pixels.ToArray());
        StringAssert.Contains(rgb1.AlgorithmVersion, DeepSkyRenderPlan.AlgorithmVersion, StringComparison.Ordinal);

        var bayerLayout = new ImageLayout(160, 160, CameraPixelFormat.BayerRggb16, 160 * 2);
        var bayerBase = new BayerRggb16RenderOptions
        {
            ExposureSeconds = 1.0,
            Gain = 1.0,
            MagnitudeZeroElectronsPerSecond = 10_000,
            BackgroundElectronsPerSecond = 0,
            PsfSigmaPixels = 1.0,
            PsfRadiusPixels = 4.0,
            VignettingStrength = 0,
            Bias = 0,
            ReadNoiseStandardDeviation = 0,
            ShotNoiseEnabled = false,
            DarkCurrentElectronsPerSecond = 0,
            Seed = 777
        };
        var bayerPlan = DeepSkyRenderPlan.Prepare(req, null, [galaxy, nebula], null, bayerBase, CreateSettings(bayerBase, req));
        var bayerOpts = bayerBase with { DeepSkySources = bayerPlan };
        var bayer1 = BayerRggb16Renderer.Render(emptyScene, bayerLayout, bayerOpts);
        var bayer2 = BayerRggb16Renderer.Render(emptyScene, bayerLayout, bayerOpts);
        CollectionAssert.AreEqual(bayer1.Pixels.ToArray(), bayer2.Pixels.ToArray());
        StringAssert.Contains(bayer1.AlgorithmVersion, DeepSkyRenderPlan.AlgorithmVersion, StringComparison.Ordinal);

        Assert.AreEqual(mono1.DeepSkyDiagnostics!.Single(static d => d.ObjectId == "NGC1976").Footprint,
            rgb1.DeepSkyDiagnostics!.Single(static d => d.ObjectId == "NGC1976").Footprint);
        Assert.AreEqual(mono1.DeepSkyDiagnostics!.Single(static d => d.ObjectId == "NGC1976").Footprint,
            bayer1.DeepSkyDiagnostics!.Single(static d => d.ObjectId == "NGC1976").Footprint);

        var nebFp = monoPlan.Diagnostics.Single(static d => d.ObjectId == "NGC1976").Footprint!;
        var (rgbR, _, rgbB) = SumRgb24Box(rgb1.Pixels.Span, 160, nebFp.MinimumX, nebFp.MinimumY, nebFp.MaximumX, nebFp.MaximumY);
        var (bayerMeanR, _, bayerMeanB) = MeanBayerRggb16Box(bayer1.Pixels.Span, 160, nebFp.MinimumX, nebFp.MinimumY, nebFp.MaximumX, nebFp.MaximumY);
        Assert.IsTrue(rgbR > rgbB, $"Emission nebula should have stronger red H-alpha than blue in RGB24 (R={rgbR}, B={rgbB}).");
        Assert.IsTrue(bayerMeanR > bayerMeanB, $"Emission nebula should have stronger mean red photosite response than blue in Bayer RGGB16 (meanR={bayerMeanR:F2}, meanB={bayerMeanB:F2}).");
    }

    [TestMethod]
    public void DarkNebulaObeysMultiplicativeTransmissionSubpixelAreaScalingAndUnresolvedDiffuseAttenuation()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1600, 1600, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        // (a) Two identical finite-optical-depth dark clouds over 100 e-/px/s diffuse bg obey multiplicative exp(-(tau1+tau2)) > 0.
        var dark1 = CreateDso("LDN-STACK-1", "Dark 1", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 60, 60, 0, null, null, null, null, null);
        var dark2 = CreateDso("LDN-STACK-2", "Dark 2", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 60, 60, 0, null, null, null, null, null);
        var oneCloudPlan = DeepSkyRenderPlan.Prepare(req, null, [dark1], null, opts, CreateSettings(opts, req, celestialDiffuseBg: 100.0));
        var twoCloudPlan = DeepSkyRenderPlan.Prepare(req, null, [dark1, dark2], null, opts, CreateSettings(opts, req, celestialDiffuseBg: 100.0));

        var singleCenter = oneCloudPlan.ElectronRate(80, 80);
        var doubleCenter = twoCloudPlan.ElectronRate(80, 80);
        var expectedSingle = 100.0 * Math.Exp(-1.6);
        var expectedDouble = 100.0 * Math.Exp(-3.2);
        Assert.IsTrue(doubleCenter > 2.0,
            $"Stacked finite-tau dark clouds must retain positive multiplicative transmission exp(-(tau1+tau2)); got {doubleCenter:F4} e-/px/s.");
        Assert.AreEqual(expectedSingle, singleCenter, expectedSingle * 0.15,
            $"Single dark cloud center should match ~100*exp(-1.6)={expectedSingle:F2}; got {singleCenter:F2}.");
        Assert.AreEqual(expectedDouble, doubleCenter, expectedDouble * 0.25,
            $"Double stacked dark cloud center should match ~100*exp(-3.2)={expectedDouble:F2}; got {doubleCenter:F2}.");

        // (b) Shrinking a subpixel dark cloud 100x in diameter (0.001' -> 0.00001') reduces projected area 10,000x
        //     and reduces absorbed flux by ~10,000x (uncovered subpixel radiance is preserved).
        var subpixelLarger = CreateDso("LDN-SUB-1", "Subpixel 0.001", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, null, null, null, null);
        var subpixelSmaller = CreateDso("LDN-SUB-2", "Subpixel 0.00001", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.00001, 0.00001, 0, null, null, null, null, null);
        var largerPlan = DeepSkyRenderPlan.Prepare(req, null, [subpixelLarger], null, opts, CreateSettings(opts, req, celestialDiffuseBg: 100.0));
        var smallerPlan = DeepSkyRenderPlan.Prepare(req, null, [subpixelSmaller], null, opts, CreateSettings(opts, req, celestialDiffuseBg: 100.0));
        var attenLarger = largerPlan.Diagnostics.Single().AttenuatedDiffuseElectronRate;
        var attenSmaller = smallerPlan.Diagnostics.Single().AttenuatedDiffuseElectronRate;
        Assert.IsTrue(attenLarger > 0 && attenSmaller > 0);
        var areaScaleRatio = attenLarger / attenSmaller;
        Assert.IsTrue(areaScaleRatio is > 9_500 and < 10_500,
            $"Shrinking dark cloud from 0.001' to 0.00001' must scale absorbed flux by ~10,000x; got {areaScaleRatio:F2}x (larger={attenLarger:E4}, smaller={attenSmaller:E4}).");

        // (c) Unresolved diffuse emitter (0.01' EmN) routes through pre-PSF diffuse map and is attenuated by overlapping dark cloud.
        var unresolvedEmn = CreateDso("EMN-UNRES", "Compact HII", "EmN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.01, 0.01, 0, 3.0, 3.0, null, null, null);
        var unattenuatedPlan = DeepSkyRenderPlan.Prepare(req, null, [unresolvedEmn], null, opts, CreateSettings(opts, req));
        var attenuatedPlan = DeepSkyRenderPlan.Prepare(req, null, [unresolvedEmn, dark1], null, opts, CreateSettings(opts, req));
        Assert.IsTrue(unattenuatedPlan.Diagnostics.Single().UsedUnresolvedPsfPath);
        var darkDiag = attenuatedPlan.Diagnostics.Single(static d => d.ObjectId == "LDN-STACK-1");
        Assert.IsTrue(darkDiag.Admitted && darkDiag.AttenuatedDiffuseElectronRate > 0,
            "Overlapping dark nebula must attenuate an unresolved diffuse emitter via the pre-PSF diffuse map.");
        Assert.IsTrue(attenuatedPlan.ElectronRate(80, 80) < unattenuatedPlan.ElectronRate(80, 80) * 0.40,
            $"Unresolved diffuse emitter peak must drop cleanly under tau=1.6 dark cloud ({attenuatedPlan.ElectronRate(80, 80):F2} vs {unattenuatedPlan.ElectronRate(80, 80):F2}).");
    }

    [TestMethod]
    public async Task UnresolvedPlanetaryNebulaConvergesToExposureIntegratedStellarPsfAcrossMotionHorizonAndMovingClouds()
    {
        var exposure = TimeSpan.FromSeconds(30);
        var startUtc = Utc;
        var midUtc = startUtc.AddTicks(exposure.Ticks / 2);
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1800, 1800, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 50, BoresightAzimuthDegrees: 180);
        var layout = Mono16Layout(160, 160);
        var targetJ2000 = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(50, 180), midUtc, HvoObserver.LatitudeDegrees, HvoObserver.LongitudeDegrees),
            midUtc);

        // Moving cloud scenario over the 30-second exposure.
        var cloudScenario = new VirtualCloudScenarioDefinition
        {
            ScenarioId = "dso-temporal-cloud-v1",
            ScenarioVersion = "1",
            Seed = 99,
            EpochUtc = startUtc,
            SpatialFrequency = 0.8,
            DriftEastCellsPerSecond = 0.08,
            EvolutionCellsPerSecond = 0.04,
            Octaves = 2,
            EdgeSoftness = 0.1,
            HorizonFadeDegrees = 1,
            TemporalSampleCount = 6,
            Keyframes =
            [
                new VirtualCloudKeyframe { OffsetSeconds = 0, Coverage = 0.7, MaximumOpacity = 0.7, ScatterFraction = 0.0 },
                new VirtualCloudKeyframe { OffsetSeconds = 30, Coverage = 0.9, MaximumOpacity = 0.8, ScatterFraction = 0.0 }
            ]
        };
        var cloudCtx = new VirtualCloudRenderContext(new VirtualCloudField(cloudScenario), startUtc, exposure);
        var baseOpts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 30.0, gain: 1.0, psfSigma: 1.0, psfRadius: 4.0) with
        {
            Cloud = cloudCtx
        };

        // 1. Sidereal motion + time-varying cloud transmission convergence over 30s.
        var star = new CelestialCatalogObject("STAR-30S", "Star30s", targetJ2000.RightAscensionHours, targetJ2000.DeclinationDegrees, 4.0, 0.0);
        var pn = CreateDso("PN-30S", "PN30s", "PN", targetJ2000.RightAscensionHours, targetJ2000.DeclinationDegrees, 0.02, 0.02, 0, 4.0, 4.0, null, null, null);
        var request = new VisibleSceneRequest(midUtc, HvoObserver, projection, new CatalogQuery(20, 100), CatalogMeta,
            new RefractionOptions(false), HorizonPolicy.GeometricHorizon);
        var starCatalog = new InMemoryCelestialCatalog([star]);
        var emptyScene = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(request).ConfigureAwait(false);
        var starScene = await new VisibleSceneBuilder(starCatalog).BuildAsync(request).ConfigureAwait(false);

        var stellarGeom = await new StellarExposureGeometryBuilder(starCatalog)
            .BuildAsync(request, startUtc, exposure, new StellarExposureGeometryOptions(
                MaximumSamplesPerSource: 64, MaximumStepPixels: 0.15, PsfSupportRadiusPixels: 4.0, MinimumSamplesPerSource: 6))
            .ConfigureAwait(false);
        var stellarPlan = StellarExposureRenderPlan.Prepare(stellarGeom, baseOpts, new StellarExposureRenderSettings(MinimumSignalToNoise: 1e-3));
        var dsoSettings = CreateSettings(baseOpts, request, minSnr: 1e-3) with
        {
            ExposureStartUtc = startUtc,
            MaximumTemporalSamples = 64,
            MaximumTemporalStepPixels = 0.15,
            MinimumTemporalSamples = 6
        };
        var pnPlan = DeepSkyRenderPlan.Prepare(request, null, [pn], null, baseOpts, dsoSettings);
        Assert.IsTrue(pnPlan.Diagnostics.Single().UsedUnresolvedPsfPath);
        Assert.IsTrue(pnPlan.Diagnostics.Single().SampledPoints >= 6,
            $"30s exposure unresolved PN must integrate multiple temporal samples; got {pnPlan.Diagnostics.Single().SampledPoints}.");

        var starRender = Mono16SceneRenderer.Render(starScene, layout, baseOpts with { StellarExposure = stellarPlan });
        var pnRender = Mono16SceneRenderer.Render(emptyScene, layout, baseOpts with { DeepSkySources = pnPlan });
        Assert.AreEqual(0, MaxMono16Difference(starRender.Pixels.Span, pnRender.Pixels.Span),
            "Unresolved PN must converge identically (0 ADU) to exposure-integrated stellar PSF with sidereal motion & moving clouds.");

        // 2. Horizon-crossing convergence over 30s (rising across eastern geometric horizon during exposure).
        var horProj = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1800, 1800, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 0, BoresightAzimuthDegrees: 90);
        var horReq = new VisibleSceneRequest(midUtc, HvoObserver, horProj, new CatalogQuery(20, 100), CatalogMeta,
            new RefractionOptions(false), HorizonPolicy.GeometricHorizon);
        var risingJ2000 = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(0.0, 90), midUtc, HvoObserver.LatitudeDegrees, HvoObserver.LongitudeDegrees),
            midUtc);
        var horStar = new CelestialCatalogObject("STAR-HOR", "StarHor", risingJ2000.RightAscensionHours, risingJ2000.DeclinationDegrees, 4.0, 0.0);
        var horPn = CreateDso("PN-HOR", "PNHor", "PN", risingJ2000.RightAscensionHours, risingJ2000.DeclinationDegrees, 0.02, 0.02, 0, 4.0, 4.0, null, null, null);
        var horStarCat = new InMemoryCelestialCatalog([horStar]);
        var horOpts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 30.0, gain: 1.0, psfSigma: 1.0, psfRadius: 4.0);
        var horEmptyScene = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(horReq).ConfigureAwait(false);
        var horStarScene = await new VisibleSceneBuilder(horStarCat).BuildAsync(horReq).ConfigureAwait(false);
        var horStellarGeom = await new StellarExposureGeometryBuilder(horStarCat)
            .BuildAsync(horReq, startUtc, exposure, new StellarExposureGeometryOptions(
                MaximumSamplesPerSource: 64, MaximumStepPixels: 0.15, PsfSupportRadiusPixels: 4.0, MinimumSamplesPerSource: 1))
            .ConfigureAwait(false);
        var horStellarPlan = StellarExposureRenderPlan.Prepare(horStellarGeom, horOpts, new StellarExposureRenderSettings(MinimumSignalToNoise: 1e-3));
        var horDsoSettings = CreateSettings(horOpts, horReq, minSnr: 1e-3) with
        {
            ExposureStartUtc = startUtc,
            MaximumTemporalSamples = 64,
            MaximumTemporalStepPixels = 0.15,
            MinimumTemporalSamples = 1
        };
        var horPnPlan = DeepSkyRenderPlan.Prepare(horReq, null, [horPn], null, horOpts, horDsoSettings);
        Assert.IsTrue(horPnPlan.VisibleElectronRate > 0 && horPnPlan.VisibleElectronRate < horPnPlan.SourceElectronRate * 0.75,
            "Horizon-crossing unresolved PN must retain only its above-horizon exposure fraction.");
        var horStarRender = Mono16SceneRenderer.Render(horStarScene, layout, horOpts with { StellarExposure = horStellarPlan });
        var horPnRender = Mono16SceneRenderer.Render(horEmptyScene, layout, horOpts with { DeepSkySources = horPnPlan });
        Assert.AreEqual(0, MaxMono16Difference(horStarRender.Pixels.Span, horPnRender.Pixels.Span),
            "Horizon-crossing unresolved PN must converge identically (0 ADU) to exposure-integrated stellar PSF.");
    }

    [TestMethod]
    public void UnresolvedOpenClusterAndCompositeDeduplicateRenderedHygStarsBeforeUnresolvedFallback()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1200, 1200, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        // Exact F10 probe: 0.01' mag-4 OCl (missing minor axis!) centered on an already-rendered mag-3 HYG star -> residual flux == 0.
        var unresolvedOcl = CreateDso("OCL-TINY", "Tiny OCl", "OCl",
            centerJ2000.RaHours, centerJ2000.DecDegrees, majArcmin: 0.01, minArcmin: null, paDeg: null,
            bMag: null, vMag: 4.0, surfBr: null, hubble: null, messier: null);
        var brightHygStar = new CelestialCatalogObject("HIP 100001", "BrightMember",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 3.0, 0.1);

        var oclPlan = DeepSkyRenderPlan.Prepare(req, null, [unresolvedOcl], [brightHygStar], opts, CreateSettings(opts, req));
        var oclDiag = oclPlan.Diagnostics.Single();
        Assert.IsFalse(oclDiag.Admitted, "Unresolved OCl covered by rendered mag-3 HYG star must not double-count.");
        Assert.AreEqual("annotation-only-hyg-flux-covered", oclDiag.Reason);
        Assert.AreEqual(1, oclDiag.MatchedHygStarCount);
        Assert.AreEqual(0d, oclPlan.SourceElectronRate);
        Assert.AreEqual(0d, oclPlan.RetainedElectronRate);

        // Unresolved Cl+N (missing minor axis) centered on mag-3 HYG star deducts cluster flux to 0 and keeps only nebula fraction (75%).
        var unresolvedComposite = CreateDso("CLN-TINY", "Tiny Cl+N", "Cl+N",
            centerJ2000.RaHours, centerJ2000.DecDegrees, majArcmin: 0.01, minArcmin: null, paDeg: null,
            bMag: null, vMag: 4.0, surfBr: null, hubble: null, messier: null);
        var compPlan = DeepSkyRenderPlan.Prepare(req, null, [unresolvedComposite], [brightHygStar], opts, CreateSettings(opts, req));
        var compDiag = compPlan.Diagnostics.Single();
        var rawMag4Rate = opts.MagnitudeZeroElectronsPerSecond * Math.Pow(10, -0.4 * 4.0);
        Assert.IsTrue(compDiag.Admitted);
        Assert.AreEqual(1, compDiag.MatchedHygStarCount);
        Assert.AreEqual(rawMag4Rate * 0.75, compPlan.SourceElectronRate, rawMag4Rate * 1e-6);
    }

    [TestMethod]
    public async Task AdmissionBindsRenderOptionsDiffuseBackgroundRgbWeightsAndCompleteBinVariance()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var emptyScene = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(req).ConfigureAwait(false);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));
        var galaxy = CreateDso("G-SNR-TEST", "Galaxy", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 30, 20, 0, 5.0, 4.5, null, "Sb", null);
        var opts = CreateDeterministicMonoOptions(background: 1.0, exposureSeconds: 2.0, readNoise: 1.5);

        // (a) Exact F11 probe: raising celestial diffuse background to 1,000,000 e-/px/s collapses S/N and rejects admission!
        var darkDiffusePlan = DeepSkyRenderPlan.Prepare(req, null, [galaxy], null, opts,
            CreateSettings(opts, req, minSnr: 5.0, celestialDiffuseBg: 0));
        var brightDiffusePlan = DeepSkyRenderPlan.Prepare(req, null, [galaxy], null, opts,
            CreateSettings(opts, req, minSnr: 5.0, celestialDiffuseBg: 1_000_000.0));
        Assert.IsTrue(darkDiffusePlan.Diagnostics.Single().Admitted);
        Assert.IsFalse(brightDiffusePlan.Diagnostics.Single().Admitted);
        Assert.AreEqual("annotation-only-below-snr", brightDiffusePlan.Diagnostics.Single().Reason);
        Assert.IsTrue(brightDiffusePlan.Diagnostics.Single().PredictedSignal.SignalToNoise < 1.0);

        // (b) RGB24 ChannelResponse & WhiteBalance affect PredictedSignal.
        var rgbBalanced = new Rgb24CompatibilityRenderOptions
        {
            ExposureSeconds = 2.0,
            Gain = 1.0,
            MagnitudeZeroElectronsPerSecond = 10_000,
            BackgroundElectronsPerSecond = 1.0,
            PsfSigmaPixels = 1.0,
            PsfRadiusPixels = 4.0,
            ShotNoiseEnabled = false,
            WhiteBalance = new RgbChannelSettings(1, 1, 1),
            ChannelResponse = new RgbChannelSettings(1, 1, 1)
        };
        var rgbRedWeighted = rgbBalanced with { WhiteBalance = new RgbChannelSettings(2.0, 0.25, 0.25) };
        var planRgbBal = DeepSkyRenderPlan.Prepare(req, null, [galaxy], null, rgbBalanced, CreateSettings(rgbBalanced, req));
        var planRgbRed = DeepSkyRenderPlan.Prepare(req, null, [galaxy], null, rgbRedWeighted, CreateSettings(rgbRedWeighted, req));
        Assert.AreNotEqual(
            planRgbBal.Diagnostics.Single().PredictedSignal.SignalToNoise,
            planRgbRed.Diagnostics.Single().PredictedSignal.SignalToNoise,
            "RGB24 white balance and channel response must enter admission prediction.");

        // (c) Complete output-bin pixel variance for DigitalBinX=2, DigitalBinY=2 (DigitalAverage=false keeps signal equal while adding sibling photosite background variance).
        var pn = CreateDso("PN-BIN", "PNBin", "PN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.02, 0.02, 0, 5.0, 5.0, null, null, null);
        var noisyBgOpts = CreateDeterministicMonoOptions(background: 200.0, exposureSeconds: 2.0, readNoise: 5.0);
        var planUnbinned = DeepSkyRenderPlan.Prepare(req, null, [pn], null, noisyBgOpts,
            CreateSettings(noisyBgOpts, req) with { DigitalBinX = 1, DigitalBinY = 1, DigitalAverage = false });
        var planBinned2x2 = DeepSkyRenderPlan.Prepare(req, null, [pn], null, noisyBgOpts,
            CreateSettings(noisyBgOpts, req) with { DigitalBinX = 2, DigitalBinY = 2, DigitalAverage = false });
        Assert.IsTrue(planBinned2x2.Diagnostics.Single().PredictedSignal.NoiseVariance >
                      planUnbinned.Diagnostics.Single().PredictedSignal.NoiseVariance,
            "Complete 2x2 digital-bin support must include sibling photosites' background/read noise variance.");

        // (d) ValidateFor rejects mismatched render options (e.g. different exposure).
        Assert.ThrowsExactly<ArgumentException>(() =>
            Mono16SceneRenderer.Render(emptyScene, Mono16Layout(160, 160),
                opts with { ExposureSeconds = 4.0, DeepSkySources = darkDiffusePlan }));
    }

    [TestMethod]
    public void CandidateSelectionIsOrderInvariantAndEnforcesCapsOnDarkAndUnresolvedPaths()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        // (a) Reversing candidate input order with count > MaximumCandidates produces bit-identical selection & DiagnosticsSha256!
        DeepSkyObject[] sixCandidates =
        [
            CreateDso("NGC-Z", "Z", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, 9.0, 8.5, null, "Sb", null),
            CreateDso("NGC-B", "B", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, 5.0, 4.5, null, "Sb", 31),
            CreateDso("NGC-A", "A", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, 6.0, 5.5, null, "Sb", null),
            CreateDso("NGC-M", "M", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, 4.0, 3.5, null, "Sb", 42),
            CreateDso("NGC-C", "C", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, 7.0, 6.5, null, "Sb", null),
            CreateDso("NGC-D", "D", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, 5.5, 5.0, null, "Sb", null)
        ];
        var capSettings = CreateSettings(opts, req) with { MaximumCandidates = 3 };
        var planForward = DeepSkyRenderPlan.Prepare(req, null, sixCandidates, null, opts, capSettings);
        var planReversed = DeepSkyRenderPlan.Prepare(req, null, sixCandidates.Reverse().ToArray(), null, opts, capSettings);

        Assert.AreEqual(3, planForward.Statistics.CandidateCount);
        Assert.AreEqual(planForward.Statistics.DiagnosticsSha256, planReversed.Statistics.DiagnosticsSha256);
        CollectionAssert.AreEqual(
            planForward.Diagnostics.Select(static d => d.ObjectId).ToArray(),
            planReversed.Diagnostics.Select(static d => d.ObjectId).ToArray());
        Assert.IsTrue(planForward.Diagnostics.Any(static d => d.ObjectId == "NGC-B"));
        Assert.IsTrue(planForward.Diagnostics.Any(static d => d.ObjectId == "NGC-M"));
        Assert.IsTrue(planForward.Diagnostics.Any(static d => d.ObjectId == "NGC-D"));

        // (b) Synthetic additions enter ranking BEFORE truncation and respect MaximumCandidates!
        var synthMessier = CreateDso("SYN-M1", "Synth M1", "G", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, 3.0, 2.5, null, "Sb", 1);
        var planWithSynth = DeepSkyRenderPlan.Prepare(req, null, sixCandidates, null, opts,
            capSettings with { SyntheticScenario = new DeepSkySyntheticScenario("cap-synth-v1", AdditionalObjects: [synthMessier]) });
        Assert.AreEqual(3, planWithSynth.Statistics.CandidateCount);
        Assert.IsTrue(planWithSynth.Diagnostics.Any(static d => d.ObjectId == "SYN-M1"));

        // (c) MaximumRenderedSources = 1 with 2 dark sources admits 1 and budget-skips the 2nd!
        var darkA = CreateDso("LDN-A", "Dark A", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, null, null, null, null, null);
        var darkB = CreateDso("LDN-B", "Dark B", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, null, null, null, null, null);
        var darkCapPlan = DeepSkyRenderPlan.Prepare(req, null, [darkA, darkB], null, opts,
            CreateSettings(opts, req, celestialDiffuseBg: 100.0) with { MaximumRenderedSources = 1 });
        Assert.AreEqual(1, darkCapPlan.Statistics.AdmittedCount);
        Assert.AreEqual(1, darkCapPlan.Statistics.DarkAttenuationCount);
        Assert.IsTrue(darkCapPlan.Diagnostics.Any(static d => d.Admitted && d.Reason == "admitted-dark-attenuation"));
        Assert.IsTrue(darkCapPlan.Diagnostics.Any(static d => !d.Admitted && d.Reason == "budget-skipped"));

        // (d) MaximumTotalSamplePoints is enforced on unresolved sources too!
        var twentyUnresolved = Enumerable.Range(0, 20)
            .Select(i => CreateDso($"PN-{i:D2}", $"PN-{i:D2}", "PN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.02, 0.02, 0, 5.0, 5.0, null, null, null))
            .ToArray();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, twentyUnresolved, null, opts,
                CreateSettings(opts, req) with { MaximumTotalSamplePoints = 16, MaximumCandidates = 32, MaximumRenderedSources = 32 }));
    }

    [TestMethod]
    public void AcceptedConfigurationMinimaMergedSyntheticValidationAndOutlineEdgeVisitBudget()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        // (a) Valid accepted minimum MaximumSamplePointsPerSource = 16 succeeds on resolved nebula (both ellipse & outline)!
        var resolvedNebula = CreateDso("NEB-MIN16", "Nebula Min16", "EmN", centerJ2000.RaHours, centerJ2000.DecDegrees, 30, 30, 0, 4.0, 4.0, null, null, null);
        var minSampleSettings = CreateSettings(opts, req) with { MaximumSamplePointsPerSource = 16 };
        var minEllipsePlan = DeepSkyRenderPlan.Prepare(req, null, [resolvedNebula], null, opts, minSampleSettings);
        Assert.IsTrue(minEllipsePlan.Diagnostics.Single().Admitted);
        Assert.IsTrue(minEllipsePlan.Diagnostics.Single().SampledPoints <= 16);

        var sqOutline = CreateSquareOutline("NEB-MIN16", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.25);
        var minOutlinePlan = DeepSkyRenderPlan.Prepare(req, null, [resolvedNebula], null, opts,
            minSampleSettings with { SyntheticScenario = new DeepSkySyntheticScenario("min16-out-v1", AdditionalOutlines: [sqOutline]) });
        Assert.IsTrue(minOutlinePlan.Diagnostics.Single().Admitted);

        // (b) Valid accepted MaximumUnresolvedClusterMembers = 2 succeeds without throwing!
        var resolvedOcl = CreateDso("OCL-MIN2", "OCl Min2", "OCl", centerJ2000.RaHours, centerJ2000.DecDegrees, 40, 40, 0, null, 4.0, null, null, null);
        var minMemberScenario = new DeepSkySyntheticScenario("min-members-v1",
            Overrides: [new DeepSkySyntheticSourceOverride("OCL-MIN2", MaximumUnresolvedClusterMembers: 2)]);
        var minMemberPlan = DeepSkyRenderPlan.Prepare(req, null, [resolvedOcl], null, opts,
            CreateSettings(opts, req, syntheticScenario: minMemberScenario));
        Assert.IsTrue(minMemberPlan.Diagnostics.Single().Admitted);
        Assert.AreEqual(2, minMemberPlan.Diagnostics.Single().UnresolvedMemberCount);

        // (c) Merged synthetic override creating minor > major is rejected!
        var badMergeScenario = new DeepSkySyntheticScenario("bad-merge-v1",
            Overrides: [new DeepSkySyntheticSourceOverride("NEB-MIN16", MinorAxisArcminutes: 50)]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [resolvedNebula], null, opts,
                CreateSettings(opts, req, syntheticScenario: badMergeScenario)));

        // (d) Unclosed synthetic outline ring is rejected at validation!
        var unclosedOutline = new DeepSkyOutline("NEB-MIN16", 1,
        [
            new DeepSkyOutlineRing([new(10, 10), new(11, 10), new(11, 11), new(10, 11)])
        ]);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DeepSkySyntheticScenario("unclosed-v1", AdditionalOutlines: [unclosedOutline]).Validate());

        // (e) High-vertex-count valid synthetic outline trips MaximumOutlineEdgeVisits before rasterization!
        const int vertices = 400;
        var ringPts = new List<DeepSkyOutlinePoint>(vertices + 1);
        var cosDec = Math.Max(0.2, Math.Cos(centerJ2000.DecDegrees * Math.PI / 180d));
        for (var i = 0; i < vertices; i++)
        {
            var angle = 2d * Math.PI * i / vertices;
            var raDeg = (((centerJ2000.RaHours * 15d + 0.25 * Math.Cos(angle) / cosDec) % 360d) + 360d) % 360d;
            var decDeg = centerJ2000.DecDegrees + 0.25 * Math.Sin(angle);
            ringPts.Add(new DeepSkyOutlinePoint(raDeg, decDeg));
        }
        ringPts.Add(ringPts[0]);
        var highVertexOutline = new DeepSkyOutline("NEB-MIN16", 1, [new DeepSkyOutlineRing(ringPts)]);
        var tightEdgeBudgetSettings = CreateSettings(opts, req) with
        {
            MaximumOutlineEdgeVisits = 1_000,
            SyntheticScenario = new DeepSkySyntheticScenario("high-vert-v1", AdditionalOutlines: [highVertexOutline])
        };
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [resolvedNebula], null, opts, tightEdgeBudgetSettings));
    }

    [TestMethod]
    [DataRow(ProjectionModel.Perspective, 1600.0)]
    [DataRow(ProjectionModel.Perspective, 800.0)]
    [DataRow(ProjectionModel.EquisolidFisheye, 1600.0)]
    [DataRow(ProjectionModel.EquisolidFisheye, 800.0)]
    public void CoincidentSubpixelDarkCloudsComposeOpticalDepthAdditivelyAcrossFocalLengthsAndProjections(
        ProjectionModel model,
        double focalLengthPx)
    {
        // Exact F5 regression: two coincident 0.001' dark clouds each with peak optical depth 1.6
        // MUST equal a single 0.001' cloud with peak optical depth 3.2 (exp(-tau1) * exp(-tau2) == exp(-(tau1+tau2))).
        var projection = new ProjectionContext(
            model, 80, 80, focalLengthPx, focalLengthPx, 160, 160,
            model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
            model == ProjectionModel.Perspective ? null : 75d,
            BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        var cloud1 = CreateDso("LDN-COINC-1", "Coinc 1", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, null, null, null, null);
        var cloud2 = CreateDso("LDN-COINC-2", "Coinc 2", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, null, null, null, null);
        var singleDoubleTau = CreateDso("LDN-TAU32", "Single tau=3.2", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, null, null, null, null);

        var twoPlan = DeepSkyRenderPlan.Prepare(req, null, [cloud1, cloud2], null, opts,
            CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: 100.0));
        var singleTau32Plan = DeepSkyRenderPlan.Prepare(req, null, [singleDoubleTau], null, opts,
            CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: 100.0,
                syntheticScenario: new DeepSkySyntheticScenario("tau-32-v1",
                    Overrides: [new DeepSkySyntheticSourceOverride("LDN-TAU32", PeakOpticalDepth: 3.2)])));

        var twoAbsorbed = twoPlan.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate);
        var singleAbsorbed = singleTau32Plan.Diagnostics.Single().AttenuatedDiffuseElectronRate;
        Assert.IsTrue(singleAbsorbed > 0);
        var relErr = Math.Abs(twoAbsorbed - singleAbsorbed) / singleAbsorbed;
        Assert.IsTrue(relErr < 1e-4,
            $"Two coincident 0.001' tau=1.6 dark clouds ({twoAbsorbed:E6} e-/s) must equal one tau=3.2 cloud ({singleAbsorbed:E6} e-/s) " +
            $"for {model} f={focalLengthPx} px; relErr={relErr:P4}.");

        var maxPixelDiff = 0d;
        for (var y = 72; y <= 88; y++)
        {
            for (var x = 72; x <= 88; x++)
            {
                maxPixelDiff = Math.Max(maxPixelDiff, Math.Abs(twoPlan.ElectronRate(x, y) - singleTau32Plan.ElectronRate(x, y)));
            }
        }
        Assert.IsTrue(maxPixelDiff <= singleAbsorbed * 1e-4,
            $"Per-pixel rates for two coincident tau=1.6 clouds must match single tau=3.2 cloud; maxPixelDiff={maxPixelDiff:E4}.");
    }

    [TestMethod]
    public async Task SubpixelDiffuseEmitterConvergesToUnresolvedPointSourcePsfWithoutAbsorber()
    {
        // Exact F9 regression: a 0.001' diffuse emitter with no absorber must converge to the qualified
        // unresolved point-source PSF path (same position, flux, PSF, subpixel placement).
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 2.0, psfSigma: 1.0, psfRadius: 4.0);
        var layout = Mono16Layout(160, 160);
        var emptyScene = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(req).ConfigureAwait(false);

        // Place at subpixel offset (non-integer pixel center) so bilinear smearing would maximally diverge!
        var targetJ2000 = AltAzToJ2000(new AltAzPoint(60.012, 180.015));
        var subpixelDiffuse = CreateDso("EMN-SUBPX", "Subpixel EmN", "EmN", targetJ2000.RaHours, targetJ2000.DecDegrees,
            0.001, 0.001, 0, null, 4.0, null, null, null);
        var unresolvedPn = CreateDso("PN-SUBPX", "Unresolved PN", "PN", targetJ2000.RaHours, targetJ2000.DecDegrees,
            0.001, 0.001, 0, null, 4.0, null, null, null);

        var diffusePlan = DeepSkyRenderPlan.Prepare(req, null, [subpixelDiffuse], null, opts, CreateSettings(opts, req));
        var pointPlan = DeepSkyRenderPlan.Prepare(req, null, [unresolvedPn], null, opts, CreateSettings(opts, req));

        Assert.IsTrue(diffusePlan.Diagnostics.Single().UsedUnresolvedPsfPath);
        Assert.IsTrue(pointPlan.Diagnostics.Single().UsedUnresolvedPsfPath);

        var l1Diff = 0d;
        var totalFlux = 0d;
        for (var y = 0; y < 160; y++)
        {
            for (var x = 0; x < 160; x++)
            {
                var rDiff = diffusePlan.ElectronRate(x, y);
                var rPt = pointPlan.ElectronRate(x, y);
                l1Diff += Math.Abs(rDiff - rPt);
                totalFlux += rPt;
            }
        }
        Assert.IsTrue(totalFlux > 0);
        var l1Fraction = l1Diff / totalFlux;
        Assert.IsTrue(l1Fraction < 0.01,
            $"Subpixel diffuse emitter diverged from unresolved point-source path by {l1Fraction:P4} L1 flux (must be < 1%).");

        var diffuseRender = Mono16SceneRenderer.Render(emptyScene, layout, opts with { DeepSkySources = diffusePlan });
        var pointRender = Mono16SceneRenderer.Render(emptyScene, layout, opts with { DeepSkySources = pointPlan });
        var maxAduDelta = MaxMono16Difference(diffuseRender.Pixels.Span, pointRender.Pixels.Span);
        Assert.IsTrue(maxAduDelta <= 1,
            $"Subpixel diffuse emitter diverged from unresolved point-source render by {maxAduDelta} ADU (must be <= 1).");
    }

    [TestMethod]
    public void OverlappingRenderedDiffuseEmissionEntersLocalSnrNoiseAndRejectsSubthresholdPointSource()
    {
        // Exact F11 regression: a 100-electron point source over ~10,000 rendered diffuse electrons in its
        // extraction pixels must report S/N < 1.1 (~0.995) and fail MinimumSignalToNoise = 5, regardless of sort order!
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        // 100 e-/s point source: 10_000 * 10^(-0.4 * 5.0) = 100.0 e-/s (100 e- in 1 s).
        // Bright overlapping EmN sheet (~10,000 e- inside the point source's PSF extraction pixels):
        // 10_000 * 10^(-0.4 * -0.5) = ~15,849 e-/s across a compact ~12' sheet (center extraction gets ~10,000 e-).
        var brightDiffuse = CreateDso("EMN-BRIGHT-SHEET", "Bright Diffuse", "EmN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 12, 12, 0, null, -0.5, null, null, null);
        var faintPointNonMessier = CreateDso("PN-100E", "Faint 100e PN", "PN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, 5.0, null, null, null);
        // Also test Messier priority order where the faint point source sorts BEFORE the non-Messier diffuse nebula!
        var faintPointMessierFirst = faintPointNonMessier with { Id = "PN-100E-M", MessierNumber = 76 };

        // Baseline: alone on dark sky, 100 e- point source has S/N = sqrt(100) = 10 >= 5 -> admitted.
        var alonePlan = DeepSkyRenderPlan.Prepare(req, null, [faintPointNonMessier], null, opts,
            CreateSettings(opts, req, minSnr: 5.0));
        Assert.IsTrue(alonePlan.Diagnostics.Single().Admitted);
        Assert.AreEqual(10.0, alonePlan.Diagnostics.Single().PredictedSignal.SignalToNoise, 0.05);

        // Over bright diffuse nebula (diffuse sorts first AND faint Messier sorts first): S/N collapses < 1.1 -> rejected!
        foreach (var pointCandidate in new[] { faintPointNonMessier, faintPointMessierFirst })
        {
            var combinedPlan = DeepSkyRenderPlan.Prepare(req, null, [brightDiffuse, pointCandidate], null, opts,
                CreateSettings(opts, req, minSnr: 5.0));
            var diffuseDiag = combinedPlan.Diagnostics.Single(static d => d.ObjectId == "EMN-BRIGHT-SHEET");
            var pointDiag = combinedPlan.Diagnostics.Single(d => d.ObjectId == pointCandidate.Id);
            Assert.IsTrue(diffuseDiag.Admitted);
            Assert.IsFalse(pointDiag.Admitted,
                $"100 e- point source ({pointCandidate.Id}) over overlapping diffuse emission must be rejected at minSnr=5; " +
                $"got S/N={pointDiag.PredictedSignal.SignalToNoise:F4}.");
            Assert.AreEqual("annotation-only-below-snr", pointDiag.Reason);
            Assert.IsTrue(pointDiag.PredictedSignal.SignalToNoise is > 0 and < 1.1,
                $"Expected S/N < 1.1 over ~10,000 overlapping diffuse electrons; got {pointDiag.PredictedSignal.SignalToNoise:F4} " +
                $"(signal={pointDiag.PredictedSignal.SourceElectrons:F2}, var={pointDiag.PredictedSignal.NoiseVariance:F2}).");
        }
    }

    [TestMethod]
    public void CombinedSparsePixelBudgetAndOpenClusterPerSourceSampleCapAreEnforced()
    {
        // Exact F12 regressions:
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);

        // (a) Spatially separate diffuse emitter + unresolved planetary nebula must enforce MaximumSparsePixels
        //     across their combined union (never returning 120 rasterized pixels when MaximumSparsePixels = 68).
        var posDiffuse = AltAzToJ2000(new AltAzPoint(59.0, 179.0));
        var posPn = AltAzToJ2000(new AltAzPoint(61.0, 181.0));
        var diffuse = CreateDso("EMN-SEP", "Separate Diffuse", "EmN", posDiffuse.RaHours, posDiffuse.DecDegrees,
            6, 6, 0, null, 4.0, null, null, null);
        var pn = CreateDso("PN-SEP", "Separate PN", "PN", posPn.RaHours, posPn.DecDegrees,
            0.001, 0.001, 0, null, 4.0, null, null, null);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [diffuse, pn], null, opts,
                CreateSettings(opts, req) with { MaximumSparsePixels = 68 }));

        // (b) Resolved open cluster (and Cl+N) must respect MaximumSamplePointsPerSource = 16 (never returning 24 members).
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));
        var resolvedOcl = CreateDso("OCL-CAP16", "Open Cluster Cap16", "OCl",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 60, 60, 0, null, 4.0, null, null, null);
        var oclPlan = DeepSkyRenderPlan.Prepare(req, null, [resolvedOcl], null, opts,
            CreateSettings(opts, req) with { MaximumSamplePointsPerSource = 16 });
        var oclDiag = oclPlan.Diagnostics.Single();
        Assert.IsTrue(oclDiag.Admitted);
        Assert.IsTrue(oclDiag.SampledPoints <= 16,
            $"Open cluster must not exceed MaximumSamplePointsPerSource=16; got {oclDiag.SampledPoints}.");
        Assert.IsTrue(oclDiag.UnresolvedMemberCount <= 16);
    }

    [TestMethod]
    public void ClusterMemberOutlineContainmentChargesMaximumOutlineEdgeVisits()
    {
        // Exact F13 regression: Cl+N with 4-edge square outline, zero nebular fraction, and MaximumOutlineEdgeVisits=1
        // must charge edge visits BEFORE polygon containment and stop deterministically instead of generating members.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        var cln = CreateDso("CLN-EDGE-CAP", "Cl+N Edge Cap", "Cl+N",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 40, 40, 0, null, 4.0, null, null, null);
        var squareOutline = CreateSquareOutline("CLN-EDGE-CAP", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.25);
        var scenario = new DeepSkySyntheticScenario(
            "cln-edge-cap-v1",
            AdditionalOutlines: [squareOutline],
            Overrides: [new DeepSkySyntheticSourceOverride("CLN-EDGE-CAP", CompositeNebulaFluxFraction: 0.0)]);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [cln], null, opts,
                CreateSettings(opts, req, syntheticScenario: scenario) with { MaximumOutlineEdgeVisits = 1 }));
    }

    [TestMethod]
    public void DarkNebulaEnforcesMinimumSignalToNoiseAndPublishesRealSignalVarianceAndSnr()
    {
        // Exact F19 regression:
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1600, 1600, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        // (a) 0.001' dark cloud against 100 e-/px/s with MinimumSignalToNoise = 1,000,000 must be REJECTED,
        //     consume 0 rendered-source slots, and publish real non-zero signal, variance, and S/N.
        var tinyDark = CreateDso("LDN-TINY-SNR", "Tiny Dark", "DrkN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, null, null, null, null);
        var rejectedPlan = DeepSkyRenderPlan.Prepare(req, null, [tinyDark], null, opts,
            CreateSettings(opts, req, minSnr: 1_000_000.0, celestialDiffuseBg: 100.0));
        var rejDiag = rejectedPlan.Diagnostics.Single();
        Assert.IsFalse(rejDiag.Admitted, "Sub-threshold 0.001' dark cloud must be rejected when MinimumSignalToNoise=1,000,000.");
        Assert.AreEqual("annotation-only-below-snr", rejDiag.Reason);
        Assert.AreEqual(0, rejectedPlan.Statistics.AdmittedCount);
        Assert.AreEqual(0, rejectedPlan.Statistics.DarkAttenuationCount);
        Assert.AreEqual(0d, rejDiag.AttenuatedDiffuseElectronRate);
        Assert.IsTrue(rejDiag.PredictedSignal.SourceElectrons > 0, "Rejected dark diagnostic must still publish real absorption signal.");
        Assert.IsTrue(rejDiag.PredictedSignal.NoiseVariance > 0, "Rejected dark diagnostic must publish real noise variance.");
        Assert.IsTrue(rejDiag.PredictedSignal.SignalToNoise is > 0 and < 1e-4,
            $"Expected tiny S/N << 1; got {rejDiag.PredictedSignal.SignalToNoise:E4}.");
        Assert.AreEqual(100.0, rejectedPlan.ElectronRate(80, 80), 1e-12,
            "Rejected dark cloud must not attenuate rendered pixels.");

        // (b) Strong 20' dark cloud against 100 e-/px/s with MinimumSignalToNoise = 5.0 passes admission
        //     and publishes non-zero signal, variance, and S/N >= 5.0.
        var strongDark = CreateDso("LDN-STRONG-SNR", "Strong Dark", "DrkN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, null, null, null, null, null);
        var admittedPlan = DeepSkyRenderPlan.Prepare(req, null, [strongDark], null, opts,
            CreateSettings(opts, req, minSnr: 5.0, celestialDiffuseBg: 100.0));
        var admDiag = admittedPlan.Diagnostics.Single();
        Assert.IsTrue(admDiag.Admitted);
        Assert.AreEqual("admitted-dark-attenuation", admDiag.Reason);
        Assert.AreEqual(1, admittedPlan.Statistics.AdmittedCount);
        Assert.AreEqual(1, admittedPlan.Statistics.DarkAttenuationCount);
        Assert.IsTrue(admDiag.AttenuatedDiffuseElectronRate > 0);
        Assert.IsTrue(admDiag.PredictedSignal.SourceElectrons > 0);
        Assert.IsTrue(admDiag.PredictedSignal.NoiseVariance > 0);
        Assert.IsTrue(admDiag.PredictedSignal.SignalToNoise >= 5.0,
            $"Strong dark cloud S/N should be >= 5.0; got {admDiag.PredictedSignal.SignalToNoise:F2}.");
    }

    [TestMethod]
    public void ThreeOverlappingDarkCloudsAreStrictlyMonotonicAdditiveAndOrderInvariant()
    {
        // Exact F5 regression: at f = 1400, two concentric 0.6' tau=1.6 clouds + a 6' tau=1.6 cloud must:
        // (1) absorb STRICTLY MORE than the 6' tau=1.6 cloud alone (monotonicity: adding dark clouds never brightens the field!),
        // (2) match combining the two 0.6' clouds into one 0.6' tau=3.2 cloud + 6' tau=1.6 cloud (exact optical-depth additivity),
        // (3) be invariant across candidate admission orders.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        var smallA = CreateDso("LDN-S1", "Small 1", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees,
            0.6, 0.6, 0, null, null, null, null, 1);
        var smallB = CreateDso("LDN-S2", "Small 2", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees,
            0.6, 0.6, 0, null, null, null, null, 2);
        var large = CreateDso("LDN-L1", "Large", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees,
            6.0, 6.0, 0, null, null, null, null, 3);
        var largeFirst = large with { Id = "LDN-L0", MessierNumber = 1 };
        var smallA2 = smallA with { MessierNumber = 2 };
        var smallB2 = smallB with { MessierNumber = 3 };
        var smallCombined = CreateDso("LDN-S-DOUBLE", "Small Combined", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees,
            0.6, 0.6, 0, null, null, null, null, 1);

        const double bg = 100.0;
        var baseSettings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg);
        var doubleTauScenario = new DeepSkySyntheticScenario(
            "f5-double-tau-v1",
            Overrides: [new DeepSkySyntheticSourceOverride(smallCombined.Id, PeakOpticalDepth: 3.2)]);

        double TotalAbsorbed(DeepSkyRenderPlan plan)
        {
            var sum = 0d;
            for (var y = 0; y < projection.HeightPixels; y++)
            {
                for (var x = 0; x < projection.WidthPixels; x++)
                {
                    sum += bg - plan.ElectronRate(x, y);
                }
            }
            return sum;
        }

        var largeAlonePlan = DeepSkyRenderPlan.Prepare(req, null, [large], null, opts, baseSettings);
        var threeSmallFirstPlan = DeepSkyRenderPlan.Prepare(req, null, [smallA, smallB, large], null, opts, baseSettings);
        var threeLargeFirstPlan = DeepSkyRenderPlan.Prepare(req, null, [largeFirst, smallA2, smallB2], null, opts, baseSettings);
        var combinedSmallAndLargePlan = DeepSkyRenderPlan.Prepare(
            req, null, [smallCombined, large], null, opts,
            CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg, syntheticScenario: doubleTauScenario));

        var absorbedLargeAlone = TotalAbsorbed(largeAlonePlan);
        var absorbedThreeSmallFirst = TotalAbsorbed(threeSmallFirstPlan);
        var absorbedThreeLargeFirst = TotalAbsorbed(threeLargeFirstPlan);
        var absorbedCombined = TotalAbsorbed(combinedSmallAndLargePlan);

        Assert.IsTrue(absorbedThreeSmallFirst > absorbedLargeAlone,
            $"Adding two 0.6' dark clouds on top of a 6' dark cloud must strictly increase absorption " +
            $"(3-cloud={absorbedThreeSmallFirst:F6} e-/s vs large-alone={absorbedLargeAlone:F6} e-/s).");
        Assert.AreEqual(absorbedCombined, absorbedThreeSmallFirst, 1e-6,
            $"Two concentric 0.6' tau=1.6 clouds + 6' tau=1.6 cloud ({absorbedThreeSmallFirst:F6}) must match " +
            $"one 0.6' tau=3.2 cloud + 6' tau=1.6 cloud ({absorbedCombined:F6}) by exact optical-depth additivity.");
        Assert.AreEqual(absorbedCombined, absorbedThreeLargeFirst, 1e-6,
            $"3-cloud overlap must be invariant under admission order (large-first={absorbedThreeLargeFirst:F6} vs combined={absorbedCombined:F6}).");
    }

    [TestMethod]
    public void SnrDemotionFreesRenderSlotForSubsequentEligibleCandidate()
    {
        // Exact F21 regression: with MaximumRenderedSources = 2, a 100 e- Messier PN (slot 1) and an overlapping
        // 12' bright diffuse emitter (slot 2) trigger demotion of the faint PN (S/N ~0.8 < 5).
        // The freed slot MUST be backfilled by an isolated mag-4 PN 1 deg away instead of marking it budget-skipped!
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));
        var offsetJ2000 = AltAzToJ2000(new AltAzPoint(61, 180));

        var faintPn = CreateDso("PN-DEMOTED", "Faint PN", "PN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, 5.0, null, null, 1);
        var brightDiffuse = CreateDso("EMN-SHEET", "Bright Diffuse", "EmN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 12, 12, 0, null, -0.5, null, null, 2);
        var isolatedPn = CreateDso("PN-BACKFILL", "Isolated Mag4 PN", "PN",
            offsetJ2000.RaHours, offsetJ2000.DecDegrees, 0.001, 0.001, 0, null, 4.0, null, null, 3);

        var capTwoSettings = CreateSettings(opts, req, minSnr: 5.0) with { MaximumRenderedSources = 2 };
        var plan = DeepSkyRenderPlan.Prepare(req, null, [faintPn, brightDiffuse, isolatedPn], null, opts, capTwoSettings);

        var faintDiag = plan.Diagnostics.Single(static d => d.ObjectId == "PN-DEMOTED");
        var diffuseDiag = plan.Diagnostics.Single(static d => d.ObjectId == "EMN-SHEET");
        var backfillDiag = plan.Diagnostics.Single(static d => d.ObjectId == "PN-BACKFILL");

        Assert.IsFalse(faintDiag.Admitted, "Overlapped faint PN must be demoted below S/N 5.");
        Assert.AreEqual("annotation-only-below-snr", faintDiag.Reason);
        Assert.IsTrue(diffuseDiag.Admitted);
        Assert.IsTrue(backfillDiag.Admitted,
            $"Freed render slot after S/N demotion must admit isolated mag-4 PN instead of leaving slot unused; " +
            $"reason={backfillDiag.Reason}, admittedCount={plan.Statistics.AdmittedCount}.");
        Assert.AreEqual(2, plan.Statistics.AdmittedCount);
        Assert.IsTrue(backfillDiag.PredictedSignal.SignalToNoise > 15.0,
            $"Isolated mag-4 PN S/N should be ~15.85; got {backfillDiag.PredictedSignal.SignalToNoise:F3}.");
    }

    [TestMethod]
    public void CoLocatedUnresolvedDiffuseEmitterAndDarkCloudMaintainAttenuationAcrossTemporalExposureSamples()
    {
        // Exact F9 regression: a co-located 0.001' diffuse emitter (EmN) and 0.001' tau=1.6 dark cloud (DrkN)
        // must maintain exact exp(-1.6) ~= 0.201896518 transmission across single-sample 1s, 16-sample 1s,
        // and default multi-sample 20s exposures without sidereal drift decoupling the pair!
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        var emitter = CreateDso("EMN-TINY-CO", "Tiny Emitter", "EmN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, 3.0, null, null, 1);
        var absorber = CreateDso("LDN-TINY-CO", "Tiny Absorber", "DrkN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, null, null, null, 2);

        var expectedTransmission = Math.Exp(-1.6);
        foreach (var (exposureSeconds, minTemporalSamples) in new[] { (1.0, 1), (1.0, 16), (20.0, 1) })
        {
            var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: exposureSeconds);
            var settings = CreateSettings(opts, req, minSnr: 0) with { MinimumTemporalSamples = minTemporalSamples };

            var unattenuated = DeepSkyRenderPlan.Prepare(req, null, [emitter], null, opts, settings);
            var attenuated = DeepSkyRenderPlan.Prepare(req, null, [emitter, absorber], null, opts, settings);

            double SumTemporal(DeepSkyRenderPlan p)
            {
                var total = 0d;
                for (var y = 0; y < projection.HeightPixels; y++)
                {
                    for (var x = 0; x < projection.WidthPixels; x++)
                    {
                        total += p.ElectronRate(x, y);
                    }
                }
                return total;
            }

            var fluxWithout = SumTemporal(unattenuated);
            var fluxWith = SumTemporal(attenuated);
            Assert.IsTrue(fluxWithout > 0);
            var transmission = fluxWith / fluxWithout;
            Assert.AreEqual(expectedTransmission, transmission, 1e-4,
                $"Co-located 0.001' emitter+absorber at exposure={exposureSeconds}s, minSamples={minTemporalSamples} " +
                $"must attenuate by exp(-1.6)={expectedTransmission:F6}; got {transmission:F6}.");
        }
    }

    [TestMethod]
    public void UnresolvedCompositeClusterNebulaRespectsPerSourceSampleBudget()
    {
        // Exact F12 regression: unresolved Cl+N with two nonzero components (nebula + residual cluster)
        // under MaximumSamplePointsPerSource = 16 and MinimumTemporalSamples = 16 must share ONE temporal
        // trajectory evaluation and charge <= 16 samples (never 32).
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        var unresolvedCln = CreateDso("CLN-TINY-CAP16", "Unresolved Cl+N", "Cl+N",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, 4.0, null, null, null);
        var settings = CreateSettings(opts, req, minSnr: 0) with
        {
            MaximumSamplePointsPerSource = 16,
            MinimumTemporalSamples = 16
        };
        var plan = DeepSkyRenderPlan.Prepare(req, null, [unresolvedCln], null, opts, settings);
        var diag = plan.Diagnostics.Single();
        Assert.IsTrue(diag.Admitted);
        Assert.IsTrue(diag.UsedUnresolvedPsfPath);
        Assert.IsTrue(diag.SampledPoints <= 16,
            $"Unresolved two-component Cl+N must not exceed MaximumSamplePointsPerSource=16; got {diag.SampledPoints}.");
        Assert.AreEqual(diag.SampledPoints, plan.Statistics.TotalSampledPoints);
        Assert.AreEqual(diag.SourceElectronRate, diag.RetainedElectronRate, 1e-2,
            "Shared unit-flux temporal scaling must conserve total Cl+N flux across both components.");
    }

    [TestMethod]
    public void DarkNebulaApertureVarianceIncludesCoLocatedForegroundSourceShotNoise()
    {
        // Exact F19 regression: adding a co-located mag -4 PN (~398,107 e-) inside a 20' dark cloud's aperture
        // (160x160 perspective, f = 1400 px, bg = 100 e-/px/s) must leave the dark cloud's diffuse-only absorption
        // deficit unchanged (~2,611 e-), increase its aperture variance by ~398,107, and drop its S/N from ~16.8
        // to ~4.01 < 5 (causing rejection at minSnr=5). A co-located bright foreground HYG star must behave identically.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60, 180));

        var darkCloud = CreateDso("LDN-20-FG", "20-arcmin Dark Cloud", "DrkN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, null, null, null, null, null);
        var brightForegroundPn = CreateDso("PN-MAG-MINUS4", "Bright Foreground PN", "PN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, -4.0, null, null, null);
        var brightForegroundStar = new CelestialCatalogObject(
            "HIP-FG-BRIGHT", "Foreground Star", centerJ2000.RaHours, centerJ2000.DecDegrees, -4.0, 0.0);

        var settings = CreateSettings(opts, req, minSnr: 5.0, celestialDiffuseBg: 100.0);
        var alonePlan = DeepSkyRenderPlan.Prepare(req, null, [darkCloud], null, opts, settings);
        var withForegroundPnPlan = DeepSkyRenderPlan.Prepare(req, null, [brightForegroundPn, darkCloud], null, opts, settings);
        var withForegroundStarPlan = DeepSkyRenderPlan.Prepare(req, null, [darkCloud], [brightForegroundStar], opts, settings);

        var aloneDark = alonePlan.Diagnostics.Single();
        var fgDark = withForegroundPnPlan.Diagnostics.Single(static d => d.ObjectId == "LDN-20-FG");
        var fgPn = withForegroundPnPlan.Diagnostics.Single(static d => d.ObjectId == "PN-MAG-MINUS4");
        var starDark = withForegroundStarPlan.Diagnostics.Single(static d => d.ObjectId == "LDN-20-FG");

        Assert.IsTrue(aloneDark.Admitted);
        Assert.IsTrue(aloneDark.PredictedSignal.SignalToNoise > 15.0);
        Assert.IsTrue(fgPn.Admitted);

        var expectedFgElectrons = 10_000d * Math.Pow(10d, -0.4 * (-4.0));
        foreach (var (label, diag) in new[] { ("foreground PN", fgDark), ("foreground HYG star", starDark) })
        {
            // Diffuse-only absorption deficit must stay identical (foreground source is never attenuated by the dark cloud).
            Assert.AreEqual(aloneDark.PredictedSignal.SourceElectrons, diag.PredictedSignal.SourceElectrons, 1e-6,
                $"Co-located {label} must not change the dark cloud's diffuse-only absorption deficit.");

            // Variance must include the ~398,107 e- foreground shot noise, collapsing S/N below 5.0!
            var varianceDelta = diag.PredictedSignal.NoiseVariance - aloneDark.PredictedSignal.NoiseVariance;
            Assert.IsTrue(varianceDelta > 0.95 * expectedFgElectrons,
                $"Dark-cloud aperture variance must include co-located {label} shot noise (~{expectedFgElectrons:F0}); got delta={varianceDelta:F1}.");
            Assert.IsFalse(diag.Admitted,
                $"20' dark cloud behind mag -4 {label} must drop below S/N 5.0 and be rejected; got S/N={diag.PredictedSignal.SignalToNoise:F3}.");
            Assert.AreEqual("annotation-only-below-snr", diag.Reason);
            Assert.IsTrue(diag.PredictedSignal.SignalToNoise is > 3.5 and < 5.0,
                $"Expected dark-cloud S/N ~4.01 behind mag -4 {label}; got {diag.PredictedSignal.SignalToNoise:F3}.");
        }
    }

    [TestMethod]
    [DataRow(225)]
    [DataRow(64)]
    public void OffsetOverlappingDarkCloudsUnderTightPerSourceSampleCapAreOrderIndependent(int maxSamplesPerSource)
    {
        // Exact F5 regression: 3' and 6' dark clouds offset by 0.025 deg with a tight per-source sample cap
        // (e.g., 225 or 64) and S/N gating off MUST yield identical admission counts and pixel-summed deficits
        // regardless of candidate priority order.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);
        var posSmall = AltAzToJ2000(new AltAzPoint(60.0, 180.0));
        var posLarge = AltAzToJ2000(new AltAzPoint(60.025, 180.0));

        var smallFirst = CreateDso("LDN-3ARCMIN", "Small 3'", "DrkN",
            posSmall.RaHours, posSmall.DecDegrees, 3.0, 3.0, 0, null, null, null, null, 1);
        var largeSecond = CreateDso("LDN-6ARCMIN", "Large 6'", "DrkN",
            posLarge.RaHours, posLarge.DecDegrees, 6.0, 6.0, 0, null, null, null, null, 2);

        var largeFirst = largeSecond with { MessierNumber = 1 };
        var smallSecond = smallFirst with { MessierNumber = 2 };

        const double bg = 100.0;
        var settings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg) with
        {
            MaximumSamplePointsPerSource = maxSamplesPerSource
        };

        var planSmallFirst = DeepSkyRenderPlan.Prepare(req, null, [smallFirst, largeSecond], null, opts, settings);
        var planLargeFirst = DeepSkyRenderPlan.Prepare(req, null, [largeFirst, smallSecond], null, opts, settings);

        Assert.AreEqual(2, planSmallFirst.Statistics.AdmittedCount,
            $"Small-first order must admit both offset clouds (cap={maxSamplesPerSource}).");
        Assert.AreEqual(2, planLargeFirst.Statistics.AdmittedCount,
            $"Large-first order must admit both offset clouds (cap={maxSamplesPerSource}).");
        Assert.AreEqual(2, planSmallFirst.Statistics.DarkAttenuationCount);
        Assert.AreEqual(2, planLargeFirst.Statistics.DarkAttenuationCount);

        double SumDeficit(DeepSkyRenderPlan plan)
        {
            var sum = 0d;
            for (var y = 0; y < projection.HeightPixels; y++)
            {
                for (var x = 0; x < projection.WidthPixels; x++)
                {
                    sum += bg - plan.ElectronRate(x, y);
                }
            }
            return sum;
        }

        var deficitSmallFirst = SumDeficit(planSmallFirst);
        var deficitLargeFirst = SumDeficit(planLargeFirst);
        Assert.IsTrue(deficitSmallFirst > 0d);
        Assert.AreEqual(deficitSmallFirst, deficitLargeFirst, 1e-10,
            $"Reversing ONLY priority on offset 3'+6' dark clouds (cap={maxSamplesPerSource}) must preserve identical pixel-summed deficit " +
            $"(smallFirst={deficitSmallFirst:F9}, largeFirst={deficitLargeFirst:F9}).");

        var maxPixelDiff = 0d;
        for (var y = 0; y < projection.HeightPixels; y++)
        {
            for (var x = 0; x < projection.WidthPixels; x++)
            {
                maxPixelDiff = Math.Max(maxPixelDiff,
                    Math.Abs(planSmallFirst.ElectronRate(x, y) - planLargeFirst.ElectronRate(x, y)));
            }
        }
        Assert.IsTrue(maxPixelDiff <= 1e-12,
            $"Per-pixel rates must match identically across priority order; maxPixelDiff={maxPixelDiff:E4}.");
    }

    [TestMethod]
    public void ThreeCoincidentFineDarkCloudsNeverExceedReportedOrConfiguredSampleBudget()
    {
        // Exact F23 regression: three coincident 0.6' dark clouds each sample 316 interior points once (cached),
        // totaling 948 interior evaluations == TotalSampledPoints == 948 (never rebuilding 1+2+3 = 1,896 points!).
        // Tight MaximumTotalSamplePoints = 948 succeeds with exact 3*tau=4.8 additivity, while 947 throws before work.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60.0, 180.0));

        var c1 = CreateDso("LDN-C1", "Coinc 1", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 1);
        var c2 = CreateDso("LDN-C2", "Coinc 2", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 2);
        var c3 = CreateDso("LDN-C3", "Coinc 3", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 3);
        var combinedTau48 = CreateDso("LDN-TAU48", "Combined 4.8", "DrkN", centerJ2000.RaHours, centerJ2000.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 1);

        const double bg = 100.0;
        var exactBudgetSettings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg) with
        {
            MaximumTotalSamplePoints = 948
        };
        var plan = DeepSkyRenderPlan.Prepare(req, null, [c1, c2, c3], null, opts, exactBudgetSettings);
        Assert.AreEqual(3, plan.Statistics.AdmittedCount);
        Assert.AreEqual(948L, plan.Statistics.TotalSampledPoints);
        Assert.AreEqual(948, plan.Diagnostics.Sum(static d => d.SampledPoints));
        Assert.IsTrue(plan.Diagnostics.All(static d => d.SampledPoints == 316));

        var single48Plan = DeepSkyRenderPlan.Prepare(
            req, null, [combinedTau48], null, opts,
            CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg,
                syntheticScenario: new DeepSkySyntheticScenario("f23-tau48-v1",
                    Overrides: [new DeepSkySyntheticSourceOverride(combinedTau48.Id, PeakOpticalDepth: 4.8)])));
        var threeAbsorbed = plan.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate);
        var single48Absorbed = single48Plan.Diagnostics.Single().AttenuatedDiffuseElectronRate;
        Assert.AreEqual(single48Absorbed, threeAbsorbed, single48Absorbed * 1e-6,
            "Three cached coincident 0.6' tau=1.6 clouds must match a single 0.6' tau=4.8 cloud.");

        var underBudgetSettings = exactBudgetSettings with { MaximumTotalSamplePoints = 947 };
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [c1, c2, c3], null, opts, underBudgetSettings));

        // Reverse and interleaved arrival orders (exercising insertIdx == matchedIdx and shiftedFineUpdates reuse)
        // plus distinct optical depths summing to 4.8 MUST also stay within the exact 948-sample budget.
        var revPlan = DeepSkyRenderPlan.Prepare(req, null, [c3, c1, c2], null, opts, exactBudgetSettings);
        Assert.AreEqual(3, revPlan.Statistics.AdmittedCount);
        Assert.AreEqual(948L, revPlan.Statistics.TotalSampledPoints);
        Assert.AreEqual(single48Absorbed, revPlan.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate), single48Absorbed * 1e-6);

        var distinctTauScenario = new DeepSkySyntheticScenario("f23-distinct-tau-v1",
            Overrides:
            [
                new DeepSkySyntheticSourceOverride(c1.Id, PeakOpticalDepth: 1.0),
                new DeepSkySyntheticSourceOverride(c2.Id, PeakOpticalDepth: 1.5),
                new DeepSkySyntheticSourceOverride(c3.Id, PeakOpticalDepth: 2.3)
            ]);
        var distinctTauPlan = DeepSkyRenderPlan.Prepare(req, null, [c2, c3, c1], null, opts,
            exactBudgetSettings with { SyntheticScenario = distinctTauScenario });
        Assert.AreEqual(3, distinctTauPlan.Statistics.AdmittedCount);
        Assert.AreEqual(948L, distinctTauPlan.Statistics.TotalSampledPoints);
        Assert.AreEqual(single48Absorbed, distinctTauPlan.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate), single48Absorbed * 1e-6);
    }

    [TestMethod]
    public void OffsetOverlappingDarkCloudsChargeEveryCrossCloudOpticalDepthEvaluationAgainstSampleBudget()
    {
        // Exact F23 regression for non-coincident overlapping dark clouds (where cached tau cannot be reused):
        // every cross-cloud optical-depth evaluation MUST be charged against MaximumTotalSamplePoints before work,
        // both for fine-on-fine overlap (forward and reverse insertion orders) and resolved-on-fine overlap.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);

        var p1 = AltAzToJ2000(new AltAzPoint(60.000, 180.0));
        var p2 = AltAzToJ2000(new AltAzPoint(60.002, 180.0));
        var p3 = AltAzToJ2000(new AltAzPoint(60.004, 180.0));
        var c1 = CreateDso("LDN-OFF-1", "Offset 1", "DrkN", p1.RaHours, p1.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 1);
        var c2 = CreateDso("LDN-OFF-2", "Offset 2", "DrkN", p2.RaHours, p2.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 2);
        var c3 = CreateDso("LDN-OFF-3", "Offset 3", "DrkN", p3.RaHours, p3.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 3);
        var c3First = c3 with { MessierNumber = 1 };
        var c2Second = c2 with { MessierNumber = 2 };
        var c1Third = c1 with { MessierNumber = 3 };

        const double bg = 100.0;
        var baseSettings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg);

        // 3 offset mutually-overlapping 0.6' clouds: 3 * 316 = 948 own samples + (0 + 1 + 2) * 316 = 948 cross-cloud evaluations = 1,896 total.
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [c1, c2, c3], null, opts, baseSettings with { MaximumTotalSamplePoints = 948 }));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [c1, c2, c3], null, opts, baseSettings with { MaximumTotalSamplePoints = 1_895 }));

        var forwardPlan = DeepSkyRenderPlan.Prepare(req, null, [c1, c2, c3], null, opts, baseSettings with { MaximumTotalSamplePoints = 1_896 });
        Assert.AreEqual(3, forwardPlan.Statistics.AdmittedCount);
        Assert.AreEqual(1_896L, forwardPlan.Statistics.TotalSampledPoints);
        Assert.AreEqual(1_896, forwardPlan.Diagnostics.Sum(static d => d.SampledPoints));

        // Reverse priority order exercises shiftedFineUpdates charging path and must enforce the exact same 1,896 budget.
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [c3First, c2Second, c1Third], null, opts, baseSettings with { MaximumTotalSamplePoints = 1_895 }));
        var reversePlan = DeepSkyRenderPlan.Prepare(req, null, [c3First, c2Second, c1Third], null, opts, baseSettings with { MaximumTotalSamplePoints = 1_896 });
        Assert.AreEqual(3, reversePlan.Statistics.AdmittedCount);
        Assert.AreEqual(1_896L, reversePlan.Statistics.TotalSampledPoints);
        Assert.AreEqual(1_896, reversePlan.Diagnostics.Sum(static d => d.SampledPoints));
        Assert.AreEqual(
            forwardPlan.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate),
            reversePlan.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate),
            1e-9);

        // Resolved (6') + Fine (0.6') overlap in both orders must charge own_resolved + own_fine + 316 cross-path evaluations.
        var large = CreateDso("LDN-RES-6", "Resolved 6'", "DrkN", p1.RaHours, p1.DecDegrees, 6.0, 6.0, 0, null, null, null, null, 1);
        var smallAfter = c1 with { MessierNumber = 2 };
        var smallBefore = c1 with { MessierNumber = 1 };
        var largeAfter = large with { MessierNumber = 2 };

        var resolvedOnlySamples = checked((int)DeepSkyRenderPlan.Prepare(req, null, [large], null, opts, baseSettings).Statistics.TotalSampledPoints);
        var fineOnlySamples = checked((int)DeepSkyRenderPlan.Prepare(req, null, [c1], null, opts, baseSettings).Statistics.TotalSampledPoints);
        var expectedCombinedSamples = resolvedOnlySamples + fineOnlySamples + fineOnlySamples;

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [large, smallAfter], null, opts,
                baseSettings with { MaximumTotalSamplePoints = expectedCombinedSamples - 1 }));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [smallBefore, largeAfter], null, opts,
                baseSettings with { MaximumTotalSamplePoints = expectedCombinedSamples - 1 }));

        var resThenFine = DeepSkyRenderPlan.Prepare(req, null, [large, smallAfter], null, opts,
            baseSettings with { MaximumTotalSamplePoints = expectedCombinedSamples });
        var fineThenRes = DeepSkyRenderPlan.Prepare(req, null, [smallBefore, largeAfter], null, opts,
            baseSettings with { MaximumTotalSamplePoints = expectedCombinedSamples });
        Assert.AreEqual((long)expectedCombinedSamples, resThenFine.Statistics.TotalSampledPoints);
        Assert.AreEqual((long)expectedCombinedSamples, fineThenRes.Statistics.TotalSampledPoints);
        Assert.AreEqual(expectedCombinedSamples, resThenFine.Diagnostics.Sum(static d => d.SampledPoints));
        Assert.AreEqual(expectedCombinedSamples, fineThenRes.Diagnostics.Sum(static d => d.SampledPoints));
    }

    [TestMethod]
    public void OffsetDarkCloudPreservesGridInvarianceForZeroDepthOrSkippedNeighborAndMonotonicityAtLowOpticalDepth()
    {
        // Exact F5 regressions:
        // (1) At f = 1400 with two 3' dark clouds offset by 0.025 deg, cap = 64, and S/N gating off,
        //     adding a transparent (tau = 0), S/N-rejected, or budget-skipped neighbor MUST leave the first cloud's
        //     pixel-summed deficit and per-pixel rates bit-for-bit identical (no neighbor-induced grid mutation).
        // (2) Adding the second cloud at tau = 0.001 MUST admit both clouds and strictly increase the pixel-summed
        //     deficit (monotonicity) regardless of priority order or which cloud has tau = 0.001.
        // (3) The same zero-depth invariance and tau = 0.001 monotonicity hold for the 3' + 6' pair at cap = 225 and default cap.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);
        var pos1 = AltAzToJ2000(new AltAzPoint(60.0, 180.0));
        var pos2 = AltAzToJ2000(new AltAzPoint(60.025, 180.0));

        var cloud1 = CreateDso("LDN-3A", "3' Cloud A", "DrkN", pos1.RaHours, pos1.DecDegrees, 3.0, 3.0, 0, null, null, null, null, 1);
        var cloud2 = CreateDso("LDN-3B", "3' Cloud B", "DrkN", pos2.RaHours, pos2.DecDegrees, 3.0, 3.0, 0, null, null, null, null, 2);
        var cloud6 = CreateDso("LDN-6B", "6' Cloud B", "DrkN", pos2.RaHours, pos2.DecDegrees, 6.0, 6.0, 0, null, null, null, null, 2);

        const double bg = 100.0;

        double SumDeficit(DeepSkyRenderPlan plan)
        {
            var sum = 0d;
            for (var y = 0; y < projection.HeightPixels; y++)
            {
                for (var x = 0; x < projection.WidthPixels; x++)
                {
                    sum += bg - plan.ElectronRate(x, y);
                }
            }
            return sum;
        }

        double MaxRateDiff(DeepSkyRenderPlan a, DeepSkyRenderPlan b)
        {
            var max = 0d;
            for (var y = 0; y < projection.HeightPixels; y++)
            {
                for (var x = 0; x < projection.WidthPixels; x++)
                {
                    max = Math.Max(max, Math.Abs(a.ElectronRate(x, y) - b.ElectronRate(x, y)));
                }
            }
            return max;
        }

        var cap64Settings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg) with
        {
            MaximumSamplePointsPerSource = 64
        };
        var alonePlan = DeepSkyRenderPlan.Prepare(req, null, [cloud1], null, opts, cap64Settings);
        var aloneDeficit = SumDeficit(alonePlan);
        Assert.IsTrue(aloneDeficit > 55.0, $"Expected ~59.7 e-/s deficit for 3' cloud alone; got {aloneDeficit:F6}.");

        // (a) Zero-depth (tau = 0) second cloud in either priority order preserves bit-identical attenuation.
        var zeroDepthScenario = new DeepSkySyntheticScenario("f5-zero-depth-v1",
            Overrides: [new DeepSkySyntheticSourceOverride(cloud2.Id, PeakOpticalDepth: 0.0)]);
        var withZeroSecond = DeepSkyRenderPlan.Prepare(req, null, [cloud1, cloud2], null, opts,
            cap64Settings with { SyntheticScenario = zeroDepthScenario });
        var withZeroFirst = DeepSkyRenderPlan.Prepare(req, null, [cloud2 with { MessierNumber = 1 }, cloud1 with { MessierNumber = 2 }], null, opts,
            cap64Settings with { SyntheticScenario = zeroDepthScenario });

        Assert.AreEqual(1, withZeroSecond.Statistics.AdmittedCount);
        Assert.AreEqual(0, withZeroSecond.Diagnostics.Single(d => d.ObjectId == cloud2.Id).SampledPoints);
        Assert.AreEqual(aloneDeficit, SumDeficit(withZeroSecond), 1e-12,
            $"Zero-depth neighbor must not alter first cloud's deficit (alone={aloneDeficit:F9}, withZero={SumDeficit(withZeroSecond):F9}).");
        Assert.AreEqual(0d, MaxRateDiff(alonePlan, withZeroSecond), 1e-12);
        Assert.AreEqual(aloneDeficit, SumDeficit(withZeroFirst), 1e-12);
        Assert.AreEqual(0d, MaxRateDiff(alonePlan, withZeroFirst), 1e-12);

        // (b) Budget-skipped or S/N-rejected neighbor preserves bit-identical attenuation.
        var withBudgetSkipped = DeepSkyRenderPlan.Prepare(req, null, [cloud1, cloud2], null, opts,
            cap64Settings with { MaximumRenderedSources = 1 });
        Assert.AreEqual(aloneDeficit, SumDeficit(withBudgetSkipped), 1e-12);
        Assert.AreEqual(0d, MaxRateDiff(alonePlan, withBudgetSkipped), 1e-12);

        var lowTauScenario = new DeepSkySyntheticScenario("f5-low-tau-v1",
            Overrides: [new DeepSkySyntheticSourceOverride(cloud2.Id, PeakOpticalDepth: 0.001)]);
        var cloud1Snr = alonePlan.Diagnostics.Single().PredictedSignal.SignalToNoise;
        Assert.IsTrue(cloud1Snr > 0.1, $"Expected measurable S/N for 3' cloud alone; got {cloud1Snr:F4}.");
        var withSnrRejected = DeepSkyRenderPlan.Prepare(req, null, [cloud1, cloud2], null, opts,
            cap64Settings with { MinimumSignalToNoise = cloud1Snr * 0.5, SyntheticScenario = lowTauScenario });
        Assert.IsTrue(withSnrRejected.Diagnostics.Single(d => d.ObjectId == cloud1.Id).Admitted);
        Assert.IsFalse(withSnrRejected.Diagnostics.Single(d => d.ObjectId == cloud2.Id).Admitted);
        Assert.AreEqual("annotation-only-below-snr", withSnrRejected.Diagnostics.Single(d => d.ObjectId == cloud2.Id).Reason);
        Assert.AreEqual(aloneDeficit, SumDeficit(withSnrRejected), 1e-12);
        Assert.AreEqual(0d, MaxRateDiff(alonePlan, withSnrRejected), 1e-12);

        // (c) Adding the second cloud at tau = 0.001 with S/N gating off admits both and STRICTLY increases deficit.
        var withLowTau = DeepSkyRenderPlan.Prepare(req, null, [cloud1, cloud2], null, opts,
            cap64Settings with { SyntheticScenario = lowTauScenario });
        var withLowTauRev = DeepSkyRenderPlan.Prepare(req, null, [cloud2 with { MessierNumber = 1 }, cloud1 with { MessierNumber = 2 }], null, opts,
            cap64Settings with { SyntheticScenario = lowTauScenario });
        var lowTauDeficit = SumDeficit(withLowTau);
        Assert.AreEqual(2, withLowTau.Statistics.AdmittedCount);
        Assert.AreEqual(2, withLowTau.Statistics.DarkAttenuationCount);
        Assert.IsTrue(lowTauDeficit > aloneDeficit,
            $"Adding positive optical depth (tau=0.001) must strictly increase attenuation (alone={aloneDeficit:F9}, withTau0001={lowTauDeficit:F9}).");
        Assert.AreEqual(lowTauDeficit, SumDeficit(withLowTauRev), 1e-12);
        Assert.AreEqual(0d, MaxRateDiff(withLowTau, withLowTauRev), 1e-12);

        // Reversed roles: cloud2 (tau = 1.6) alone vs cloud2 + cloud1 (tau = 0.001).
        var cloud2AloneDeficit = SumDeficit(DeepSkyRenderPlan.Prepare(req, null, [cloud2], null, opts, cap64Settings));
        var lowTau1Scenario = new DeepSkySyntheticScenario("f5-low-tau1-v1",
            Overrides: [new DeepSkySyntheticSourceOverride(cloud1.Id, PeakOpticalDepth: 0.001)]);
        var cloud2PlusLowTau1 = DeepSkyRenderPlan.Prepare(req, null, [cloud2, cloud1], null, opts,
            cap64Settings with { SyntheticScenario = lowTau1Scenario });
        Assert.AreEqual(2, cloud2PlusLowTau1.Statistics.AdmittedCount);
        Assert.IsTrue(SumDeficit(cloud2PlusLowTau1) > cloud2AloneDeficit,
            $"Adding cloud1 (tau=0.001) onto cloud2 (tau=1.6) must strictly increase attenuation ({SumDeficit(cloud2PlusLowTau1):F9} vs {cloud2AloneDeficit:F9}).");

        // (d) 3' + 6' pair at cap = 225 and default cap: zero-depth invariance and tau = 0.001 monotonicity in both directions.
        foreach (var cap in new[] { 225, 65_536 })
        {
            var settings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg) with
            {
                MaximumSamplePointsPerSource = cap
            };
            var d3Alone = SumDeficit(DeepSkyRenderPlan.Prepare(req, null, [cloud1], null, opts, settings));
            var d6Alone = SumDeficit(DeepSkyRenderPlan.Prepare(req, null, [cloud6], null, opts, settings));

            var d3PlusZero6 = SumDeficit(DeepSkyRenderPlan.Prepare(req, null, [cloud1, cloud6], null, opts,
                settings with { SyntheticScenario = new DeepSkySyntheticScenario($"f5-z6-{cap}", Overrides: [new DeepSkySyntheticSourceOverride(cloud6.Id, PeakOpticalDepth: 0.0)]) }));
            var d6PlusZero3 = SumDeficit(DeepSkyRenderPlan.Prepare(req, null, [cloud6, cloud1], null, opts,
                settings with { SyntheticScenario = new DeepSkySyntheticScenario($"f5-z3-{cap}", Overrides: [new DeepSkySyntheticSourceOverride(cloud1.Id, PeakOpticalDepth: 0.0)]) }));
            Assert.AreEqual(d3Alone, d3PlusZero6, 1e-12, $"3' + 6'(tau=0) invariance failed at cap={cap}.");
            Assert.AreEqual(d6Alone, d6PlusZero3, 1e-12, $"6' + 3'(tau=0) invariance failed at cap={cap}.");

            var d3PlusLow6 = SumDeficit(DeepSkyRenderPlan.Prepare(req, null, [cloud1, cloud6], null, opts,
                settings with { SyntheticScenario = new DeepSkySyntheticScenario($"f5-l6-{cap}", Overrides: [new DeepSkySyntheticSourceOverride(cloud6.Id, PeakOpticalDepth: 0.001)]) }));
            var d6PlusLow3 = SumDeficit(DeepSkyRenderPlan.Prepare(req, null, [cloud6, cloud1], null, opts,
                settings with { SyntheticScenario = new DeepSkySyntheticScenario($"f5-l3-{cap}", Overrides: [new DeepSkySyntheticSourceOverride(cloud1.Id, PeakOpticalDepth: 0.001)]) }));
            Assert.IsTrue(d3PlusLow6 > d3Alone, $"3' + 6'(tau=0.001) monotonicity failed at cap={cap} ({d3PlusLow6:F9} <= {d3Alone:F9}).");
            Assert.IsTrue(d6PlusLow3 > d6Alone, $"6' + 3'(tau=0.001) monotonicity failed at cap={cap} ({d6PlusLow3:F9} <= {d6Alone:F9}).");
        }
    }

    [TestMethod]
    public void NarrowHighAspectDarkEllipseRemainsProperlySampledAndAttenuatedAcrossSampleCaps()
    {
        // Exact F27 regression: a centered 20' x 0.4' dark ellipse at f = 1400, PA = 0 deg, against a declared
        // 100 e-/px/s diffuse background has major span ~8.14 px but minor span ~0.163 px. Both projected dimensions
        // must be accounted for when choosing quadrature so the narrow minor axis never falls between samples at
        // cap = 64 (or any higher/lower cap) or rotated position angles, including narrow sourced outlines.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60.0, 180.0));

        const double bg = 100.0;
        // Exact analytic absorption for a 20' x 0.4' ellipse with tau(q) = 1.6*(1-q^2) at f = 1400, bg = 100 is ~52.22 e-/s.
        foreach (var paDeg in new[] { 0.0, 45.0, 90.0 })
        {
            var narrowEllipse = CreateDso($"LDN-NARROW-PA{paDeg:F0}", "Narrow 20'x0.4' Dark Ellipse", "DrkN",
                centerJ2000.RaHours, centerJ2000.DecDegrees, 20.0, 0.4, paDeg, null, null, null, null, null);

            foreach (var cap in new[] { 32, 64, 225, 65_536 })
            {
                var settings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg) with
                {
                    MaximumSamplePointsPerSource = cap
                };
                var plan = DeepSkyRenderPlan.Prepare(req, null, [narrowEllipse], null, opts, settings);
                var diag = plan.Diagnostics.Single();

                Assert.IsTrue(diag.Admitted,
                    $"20' x 0.4' dark ellipse (PA={paDeg} deg, cap={cap}) must be admitted; got reason='{diag.Reason}'.");
                Assert.AreEqual("admitted-dark-attenuation", diag.Reason);
                Assert.AreEqual(1, plan.Statistics.DarkAttenuationCount);
                Assert.IsTrue(diag.SampledPoints > 0 && diag.SampledPoints <= cap,
                    $"Expected 0 < SampledPoints <= {cap} for PA={paDeg} deg; got {diag.SampledPoints}.");

                var pixelDeficit = 0d;
                for (var y = 0; y < projection.HeightPixels; y++)
                {
                    for (var x = 0; x < projection.WidthPixels; x++)
                    {
                        pixelDeficit += bg - plan.ElectronRate(x, y);
                    }
                }
                Assert.IsTrue(pixelDeficit is > 45.0 and < 60.0,
                    $"20' x 0.4' dark ellipse (PA={paDeg} deg, cap={cap}) must remove ~52-55 e-/s (analytic ~52.22 e-/s); got {pixelDeficit:F6} e-/s.");
            }
        }

        // F28 regression: narrow rotated rectangular outline (20' x 0.4') across rotations (0, 17, 45, 73, 90, 137 deg)
        // and caps (32, 64, 225, 65,536) MUST integrate over its exact projected polygon area (~1.32679 px^2) and
        // NEVER exceed the physical maximum absorption bg * projectedArea * (1 - exp(-tau_max)) (~105.891 e-/s).
        var cosDec = Math.Max(0.2, Math.Cos(centerJ2000.DecDegrees * Math.PI / 180d));
        var raDeg0 = centerJ2000.RaHours * 15d;
        var halfMajorDeg = 10.0 / 60.0;
        var halfMinorDeg = 0.2 / 60.0;
        var outlineProjector = ProjectorFactory.Create(projection);

        double ProjectedPolygonAreaPixels(IReadOnlyList<DeepSkyOutlinePoint> closedRing)
        {
            var pts = new List<PixelPoint>(closedRing.Count);
            for (var i = 0; i < closedRing.Count - 1; i++)
            {
                var p = closedRing[i];
                var horiz = CoordinateTransforms.EquatorialToHorizontal(
                    EquatorialPrecession.PrecessJ2000(new EquatorialPoint(p.RightAscensionDegrees / 15d, p.DeclinationDegrees), Utc),
                    Utc, HvoObserver.LatitudeDegrees, HvoObserver.LongitudeDegrees);
                var px = outlineProjector.Project(horiz);
                Assert.IsNotNull(px, "Outline test vertex must project inside the test camera.");
                pts.Add(px.Value);
            }
            var area2 = 0d;
            for (var i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                area2 += a.X * b.Y - b.X * a.Y;
            }
            return 0.5 * Math.Abs(area2);
        }

        foreach (var paDeg in new[] { 0.0, 17.0, 45.0, 73.0, 90.0, 137.0 })
        {
            var angleRad = paDeg * Math.PI / 180.0;
            var c = Math.Cos(angleRad);
            var s = Math.Sin(angleRad);
            DeepSkyOutlinePoint RotatedCorner(double localMajor, double localMinor)
            {
                var dNorth = localMajor * c - localMinor * s;
                var dEast = localMajor * s + localMinor * c;
                var ra = (((raDeg0 + dEast / cosDec) % 360d) + 360d) % 360d;
                return new DeepSkyOutlinePoint(ra, centerJ2000.DecDegrees + dNorth);
            }

            var o0 = RotatedCorner(-halfMajorDeg, -halfMinorDeg);
            DeepSkyOutlinePoint[] ringPoints =
            [
                o0,
                RotatedCorner(halfMajorDeg, -halfMinorDeg),
                RotatedCorner(halfMajorDeg, halfMinorDeg),
                RotatedCorner(-halfMajorDeg, halfMinorDeg),
                o0
            ];
            var projectedAreaPx2 = ProjectedPolygonAreaPixels(ringPoints);
            var physicalUpperBound = bg * projectedAreaPx2 * (1d - Math.Exp(-1.6));
            Assert.IsTrue(projectedAreaPx2 is > 1.30 and < 1.36,
                $"Expected 20'x0.4' projected area ~1.3268 px^2 at PA={paDeg} deg; got {projectedAreaPx2:F6}.");

            var objectId = $"LDN-NARROW-OUTLINE-PA{paDeg:F0}";
            var narrowOutline = new DeepSkyOutline(objectId, DeepSkyOutline.WidestLevel, [new DeepSkyOutlineRing(ringPoints)]);
            var outlineDark = CreateDso(objectId, $"Narrow 20'x0.4' Outline PA={paDeg:F0}", "DrkN",
                centerJ2000.RaHours, centerJ2000.DecDegrees, 20.0, 0.4, paDeg, null, null, null, null, null);

            foreach (var cap in new[] { 32, 64, 225, 65_536 })
            {
                var outlinePlan = DeepSkyRenderPlan.Prepare(req, null, [outlineDark], null, opts,
                    CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg,
                        syntheticScenario: new DeepSkySyntheticScenario($"f28-outline-{paDeg:F0}-{cap}", AdditionalOutlines: [narrowOutline])) with
                    {
                        MaximumSamplePointsPerSource = cap
                    });
                var diag = outlinePlan.Diagnostics.Single();
                Assert.IsTrue(diag.Admitted,
                    $"Rotated 20'x0.4' dark outline (PA={paDeg} deg, cap={cap}) must be admitted; got reason='{diag.Reason}'.");
                Assert.AreEqual("admitted-dark-attenuation", diag.Reason);
                Assert.IsTrue(diag.SampledPoints > 0 && diag.SampledPoints <= cap,
                    $"Expected 0 < SampledPoints <= {cap} for outline PA={paDeg} deg; got {diag.SampledPoints}.");

                var pixelDeficit = 0d;
                for (var y = 0; y < projection.HeightPixels; y++)
                {
                    for (var x = 0; x < projection.WidthPixels; x++)
                    {
                        pixelDeficit += bg - outlinePlan.ElectronRate(x, y);
                    }
                }
                Assert.AreEqual(pixelDeficit, diag.AttenuatedDiffuseElectronRate, 1e-8,
                    $"Diagnostic deficit must match delivered pixel deficit for PA={paDeg} deg, cap={cap}.");
                Assert.IsTrue(pixelDeficit <= physicalUpperBound * (1d + 1e-4),
                    $"20'x0.4' dark outline (PA={paDeg} deg, cap={cap}) absorbed {pixelDeficit:F6} e-/s, exceeding physical bound " +
                    $"{physicalUpperBound:F6} e-/s (projectedArea={projectedAreaPx2:F6} px^2).");
                Assert.IsTrue(pixelDeficit > 70.0,
                    $"20'x0.4' dark outline (PA={paDeg} deg, cap={cap}) must remain accurately attenuated (~93 e-/s); got {pixelDeficit:F6} e-/s.");
            }
        }

        // Non-rectangular rotated thin diamond (50% of its oriented bounding box) MUST also obey exact polygon-cell area clipping.
        {
            var angleRad = 45.0 * Math.PI / 180.0;
            var c = Math.Cos(angleRad);
            var s = Math.Sin(angleRad);
            DeepSkyOutlinePoint RotatedPoint(double localMajor, double localMinor)
            {
                var dNorth = localMajor * c - localMinor * s;
                var dEast = localMajor * s + localMinor * c;
                var ra = (((raDeg0 + dEast / cosDec) % 360d) + 360d) % 360d;
                return new DeepSkyOutlinePoint(ra, centerJ2000.DecDegrees + dNorth);
            }
            var d0 = RotatedPoint(-halfMajorDeg, 0d);
            DeepSkyOutlinePoint[] diamondRing =
            [
                d0,
                RotatedPoint(0d, -halfMinorDeg),
                RotatedPoint(halfMajorDeg, 0d),
                RotatedPoint(0d, halfMinorDeg),
                d0
            ];
            var diamondAreaPx2 = ProjectedPolygonAreaPixels(diamondRing);
            var diamondUpperBound = bg * diamondAreaPx2 * (1d - Math.Exp(-1.6));
            var diamondOutline = new DeepSkyOutline("LDN-DIAMOND-45", DeepSkyOutline.WidestLevel, [new DeepSkyOutlineRing(diamondRing)]);
            var diamondDark = CreateDso("LDN-DIAMOND-45", "Thin 45-deg Diamond Outline", "DrkN",
                centerJ2000.RaHours, centerJ2000.DecDegrees, 20.0, 0.4, 45.0, null, null, null, null, null);
            foreach (var cap in new[] { 32, 64, 225, 65_536 })
            {
                var diamondPlan = DeepSkyRenderPlan.Prepare(req, null, [diamondDark], null, opts,
                    CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg,
                        syntheticScenario: new DeepSkySyntheticScenario($"f28-diamond-{cap}", AdditionalOutlines: [diamondOutline])) with
                    {
                        MaximumSamplePointsPerSource = cap
                    });
                var diag = diamondPlan.Diagnostics.Single();
                Assert.IsTrue(diag.Admitted);
                Assert.IsTrue(diag.SampledPoints > 0 && diag.SampledPoints <= cap);
                var pixelDeficit = 0d;
                for (var y = 0; y < projection.HeightPixels; y++)
                {
                    for (var x = 0; x < projection.WidthPixels; x++)
                    {
                        pixelDeficit += bg - diamondPlan.ElectronRate(x, y);
                    }
                }
                Assert.IsTrue(pixelDeficit > 25.0 && pixelDeficit <= diamondUpperBound * (1d + 1e-4),
                    $"45-deg thin diamond outline (cap={cap}) must absorb within (25, {diamondUpperBound:F4}] e-/s; got {pixelDeficit:F6} e-/s.");
            }
        }
    }

    [TestMethod]
    public void CenteredCirclePlusNarrowRotatedDarkEllipseNeverBrightensAnyPixelAndReconcilesDiagnostics()
    {
        // Exact F5 delta6 regression: centered 10' circular cloud + centered 20' x 0.4' PA 45 deg ellipse at f = 1400,
        // bg = 100 e-/px/s, S/N gating off. Adding the ellipse at tau = 0.001 or tau = 1.6 (in either admission order,
        // across tight cap = 64, 32, 225 and full cap = 65,536) MUST NEVER brighten ANY pixel (including (80, 79)),
        // MUST strictly increase total attenuation, and MUST keep diagnostics exactly reconciled with delivered pixel deficit.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60.0, 180.0));

        var circle = CreateDso("LDN-C10", "10' Circle", "DrkN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 10.0, 10.0, 0, null, null, null, null, 1);
        var ellipse = CreateDso("LDN-E20X04", "20'x0.4' PA45 Ellipse", "DrkN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 20.0, 0.4, 45.0, null, null, null, null, 2);
        var fineCircle = CreateDso("LDN-C1P5", "1.5' Fine Circle", "DrkN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 1.5, 1.5, 0, null, null, null, null, 1);

        const double bg = 100.0;

        double SumDeficit(DeepSkyRenderPlan plan)
        {
            var sum = 0d;
            for (var y = 0; y < projection.HeightPixels; y++)
            {
                for (var x = 0; x < projection.WidthPixels; x++)
                {
                    sum += bg - plan.ElectronRate(x, y);
                }
            }
            return sum;
        }

        void AssertNoBrightenedPixel(DeepSkyRenderPlan baseline, DeepSkyRenderPlan withAdded, string context)
        {
            var maxBrightening = 0d;
            var worstX = -1;
            var worstY = -1;
            for (var y = 0; y < projection.HeightPixels; y++)
            {
                for (var x = 0; x < projection.WidthPixels; x++)
                {
                    var diff = withAdded.ElectronRate(x, y) - baseline.ElectronRate(x, y);
                    if (diff > maxBrightening)
                    {
                        maxBrightening = diff;
                        worstX = x;
                        worstY = y;
                    }
                }
            }
            Assert.IsTrue(maxBrightening <= 1e-12,
                $"{context}: adding a positive-depth absorber brightened pixel ({worstX},{worstY}) by +{maxBrightening:E6} e-/px/s " +
                $"(baseline={baseline.ElectronRate(worstX, worstY):F6}, combined={withAdded.ElectronRate(worstX, worstY):F6}).");
        }

        foreach (var cap in new[] { 32, 64, 225, 65_536 })
        {
            var baseSettings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg) with
            {
                MaximumSamplePointsPerSource = cap
            };
            var circleAlone = DeepSkyRenderPlan.Prepare(req, null, [circle], null, opts, baseSettings);
            var circleDeficit = SumDeficit(circleAlone);
            Assert.AreEqual(circleDeficit, circleAlone.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate), 1e-8);

            // Transparent, budget-skipped, and S/N-rejected ellipse neighbors must change ZERO pixels.
            var withZeroEllipse = DeepSkyRenderPlan.Prepare(req, null, [circle, ellipse], null, opts,
                baseSettings with
                {
                    SyntheticScenario = new DeepSkySyntheticScenario($"f5-c10-zero-{cap}",
                        Overrides: [new DeepSkySyntheticSourceOverride(ellipse.Id, PeakOpticalDepth: 0.0)])
                });
            Assert.AreEqual(1, withZeroEllipse.Statistics.AdmittedCount);
            Assert.AreEqual(circleDeficit, SumDeficit(withZeroEllipse), 1e-12);

            var withBudgetSkipped = DeepSkyRenderPlan.Prepare(req, null, [circle, ellipse], null, opts,
                baseSettings with { MaximumRenderedSources = 1 });
            Assert.AreEqual(circleDeficit, SumDeficit(withBudgetSkipped), 1e-12);

            foreach (var ellipseTau in new[] { 0.001, 1.6 })
            {
                var scenario = new DeepSkySyntheticScenario($"f5-c10-e-{cap}-{ellipseTau}",
                    Overrides: [new DeepSkySyntheticSourceOverride(ellipse.Id, PeakOpticalDepth: ellipseTau)]);
                var settings = baseSettings with { SyntheticScenario = scenario };

                var ellipseAlone = DeepSkyRenderPlan.Prepare(req, null, [ellipse], null, opts, settings);
                var circleFirst = DeepSkyRenderPlan.Prepare(req, null, [circle, ellipse], null, opts, settings);
                var ellipseFirst = DeepSkyRenderPlan.Prepare(req, null,
                    [ellipse with { MessierNumber = 1 }, circle with { MessierNumber = 2 }], null, opts, settings);

                Assert.AreEqual(2, circleFirst.Statistics.AdmittedCount);
                Assert.AreEqual(2, circleFirst.Statistics.DarkAttenuationCount);
                Assert.AreEqual(2, ellipseFirst.Statistics.AdmittedCount);
                Assert.IsTrue(circleFirst.Diagnostics.All(d => d.SampledPoints <= cap),
                    $"Per-source SampledPoints must stay <= {cap} (circleFirst).");
                Assert.IsTrue(ellipseFirst.Diagnostics.All(d => d.SampledPoints <= cap),
                    $"Per-source SampledPoints must stay <= {cap} (ellipseFirst).");

                AssertNoBrightenedPixel(circleAlone, circleFirst, $"10' circle + ellipse(tau={ellipseTau}, cap={cap})");
                AssertNoBrightenedPixel(ellipseAlone, ellipseFirst, $"ellipse(tau={ellipseTau}) + 10' circle (cap={cap})");
                Assert.IsTrue(circleFirst.ElectronRate(80, 79) <= circleAlone.ElectronRate(80, 79) + 1e-12,
                    $"Pixel (80,79) must not brighten at cap={cap}, tau={ellipseTau}.");

                var combinedDeficit = SumDeficit(circleFirst);
                var combinedRevDeficit = SumDeficit(ellipseFirst);
                Assert.IsTrue(combinedDeficit > circleDeficit,
                    $"Adding ellipse (tau={ellipseTau}, cap={cap}) must strictly increase total attenuation " +
                    $"({combinedDeficit:F6} vs {circleDeficit:F6}).");
                Assert.AreEqual(combinedDeficit, circleFirst.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate), 1e-8,
                    $"Diagnostics sum must exactly match delivered pixel deficit (circleFirst, cap={cap}, tau={ellipseTau}).");
                Assert.AreEqual(combinedRevDeficit, ellipseFirst.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate), 1e-8,
                    $"Diagnostics sum must exactly match delivered pixel deficit (ellipseFirst, cap={cap}, tau={ellipseTau}).");
                Assert.AreEqual(combinedDeficit, combinedRevDeficit, 1e-9,
                    $"Circle-first and ellipse-first orders must produce identical total deficit (cap={cap}, tau={ellipseTau}).");
            }
        }

        // Fine-on-fine cross-cloud case at default cap = 65,536 (1.5' fine circle + 20'x0.4' PA45 fine ellipse):
        // broad-before-narrow canonical ordering + monotonic composition guarantees zero brightened pixels in both orders.
        var fullSettings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg);
        var fineCircleAlone = DeepSkyRenderPlan.Prepare(req, null, [fineCircle], null, opts, fullSettings);
        foreach (var ellipseTau in new[] { 0.001, 1.6 })
        {
            var scenario = new DeepSkySyntheticScenario($"f5-fine1p5-{ellipseTau}",
                Overrides: [new DeepSkySyntheticSourceOverride(ellipse.Id, PeakOpticalDepth: ellipseTau)]);
            var settings = fullSettings with { SyntheticScenario = scenario };
            var ellipseAlone = DeepSkyRenderPlan.Prepare(req, null, [ellipse], null, opts, settings);
            var bothA = DeepSkyRenderPlan.Prepare(req, null, [fineCircle, ellipse], null, opts, settings);
            var bothB = DeepSkyRenderPlan.Prepare(req, null,
                [ellipse with { MessierNumber = 1 }, fineCircle with { MessierNumber = 2 }], null, opts, settings);

            AssertNoBrightenedPixel(fineCircleAlone, bothA, $"1.5' fine circle + ellipse(tau={ellipseTau}, cap=65536)");
            AssertNoBrightenedPixel(ellipseAlone, bothB, $"ellipse(tau={ellipseTau}) + 1.5' fine circle (cap=65536)");
            Assert.AreEqual(SumDeficit(bothA), bothA.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate), 1e-8);
            Assert.AreEqual(SumDeficit(bothA), SumDeficit(bothB), 1e-9);
        }
    }

    [TestMethod]
    public void OverlappingDarkCloudsStrictlyEnforceMaximumSamplePointsPerSourceAndTotalBudget()
    {
        // Exact F29 regression: three 0.6' dark circles spaced 0.0001 deg in altitude at peak depths 1, 1.5, 2
        // with MaximumSamplePointsPerSource = 64 MUST strictly enforce SampledPoints <= 64 for EVERY source
        // (both reported diagnostics and actual radial evaluations), strictly enforce MaximumTotalSamplePoints,
        // maintain order invariance and strict per-pixel monotonicity, and keep diagnostics reconciled with pixels.
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);

        var p1 = AltAzToJ2000(new AltAzPoint(60.0000, 180.0));
        var p2 = AltAzToJ2000(new AltAzPoint(60.0001, 180.0));
        var p3 = AltAzToJ2000(new AltAzPoint(60.0002, 180.0));

        var c1 = CreateDso("LDN-F29-1", "F29 Circle 1", "DrkN", p1.RaHours, p1.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 1);
        var c2 = CreateDso("LDN-F29-2", "F29 Circle 2", "DrkN", p2.RaHours, p2.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 2);
        var c3 = CreateDso("LDN-F29-3", "F29 Circle 3", "DrkN", p3.RaHours, p3.DecDegrees, 0.6, 0.6, 0, null, null, null, null, 3);

        const double bg = 100.0;
        var scenario = new DeepSkySyntheticScenario("f29-depths-v1",
            Overrides:
            [
                new DeepSkySyntheticSourceOverride(c1.Id, PeakOpticalDepth: 1.0),
                new DeepSkySyntheticSourceOverride(c2.Id, PeakOpticalDepth: 1.5),
                new DeepSkySyntheticSourceOverride(c3.Id, PeakOpticalDepth: 2.0)
            ]);

        double SumDeficit(DeepSkyRenderPlan plan)
        {
            var sum = 0d;
            for (var y = 0; y < projection.HeightPixels; y++)
            {
                for (var x = 0; x < projection.WidthPixels; x++)
                {
                    sum += bg - plan.ElectronRate(x, y);
                }
            }
            return sum;
        }

        // Test exact cap = 64 repro, minimum cap = 16, intermediate mixed cap = 700, and default cap = 65,536.
        foreach (var perSourceCap in new[] { 16, 64, 700, 65_536 })
        {
            var settings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: bg, syntheticScenario: scenario) with
            {
                MaximumSamplePointsPerSource = perSourceCap
            };

            var plan1 = DeepSkyRenderPlan.Prepare(req, null, [c1], null, opts, settings);
            var plan2 = DeepSkyRenderPlan.Prepare(req, null, [c1, c2], null, opts, settings);
            var plan3 = DeepSkyRenderPlan.Prepare(req, null, [c1, c2, c3], null, opts, settings);

            Assert.AreEqual(3, plan3.Statistics.AdmittedCount);
            Assert.AreEqual(3, plan3.Statistics.DarkAttenuationCount);
            foreach (var diag in plan3.Diagnostics)
            {
                Assert.IsTrue(diag.SampledPoints > 0 && diag.SampledPoints <= perSourceCap,
                    $"Source '{diag.ObjectId}' reported {diag.SampledPoints} sampled points, exceeding MaximumSamplePointsPerSource={perSourceCap}.");
            }

            var reportedSum = plan3.Diagnostics.Sum(static d => d.SampledPoints);
            Assert.AreEqual((long)reportedSum, plan3.Statistics.TotalSampledPoints);
            Assert.IsTrue(plan3.Statistics.TotalSampledPoints <= 3L * perSourceCap,
                $"Scene TotalSampledPoints={plan3.Statistics.TotalSampledPoints} exceeded 3 * {perSourceCap}.");

            // Exact scene-total budget enforcement: TotalSampledPoints succeeds, TotalSampledPoints - 1 throws.
            var exactTotal = checked((int)plan3.Statistics.TotalSampledPoints);
            var exactTotalPlan = DeepSkyRenderPlan.Prepare(req, null, [c1, c2, c3], null, opts,
                settings with { MaximumTotalSamplePoints = exactTotal });
            Assert.AreEqual(plan3.Statistics.TotalSampledPoints, exactTotalPlan.Statistics.TotalSampledPoints);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                DeepSkyRenderPlan.Prepare(req, null, [c1, c2, c3], null, opts,
                    settings with { MaximumTotalSamplePoints = exactTotal - 1 }));

            // Strict per-pixel monotonicity across 1 -> 2 -> 3 clouds and exact diagnostic reconciliation.
            for (var y = 0; y < projection.HeightPixels; y++)
            {
                for (var x = 0; x < projection.WidthPixels; x++)
                {
                    Assert.IsTrue(plan2.ElectronRate(x, y) <= plan1.ElectronRate(x, y) + 1e-12,
                        $"Adding c2 brightened pixel ({x},{y}) at cap={perSourceCap}.");
                    Assert.IsTrue(plan3.ElectronRate(x, y) <= plan2.ElectronRate(x, y) + 1e-12,
                        $"Adding c3 brightened pixel ({x},{y}) at cap={perSourceCap}.");
                }
            }
            Assert.AreEqual(SumDeficit(plan3), plan3.Diagnostics.Sum(static d => d.AttenuatedDiffuseElectronRate), 1e-8);

            // Reversed priority order also strictly obeys MaximumSamplePointsPerSource.
            var revPlan = DeepSkyRenderPlan.Prepare(req, null,
                [c3 with { MessierNumber = 1 }, c2 with { MessierNumber = 2 }, c1 with { MessierNumber = 3 }],
                null, opts, settings);
            Assert.AreEqual(3, revPlan.Statistics.AdmittedCount);
            Assert.IsTrue(revPlan.Diagnostics.All(d => d.SampledPoints > 0 && d.SampledPoints <= perSourceCap),
                $"Reversed order exceeded MaximumSamplePointsPerSource={perSourceCap}.");
            if (perSourceCap != 700)
            {
                Assert.AreEqual(SumDeficit(plan3), SumDeficit(revPlan), 1e-9,
                    $"Forward and reverse orders must produce identical deficits at cap={perSourceCap}.");
            }
        }

        // Temporal unresolved-diffuse overlap must also obey MaximumSamplePointsPerSource (own + temporal <= cap).
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60.0, 180.0));
        var tinyEmitter = CreateDso("EMN-F29-TEMP", "Tiny Emitter", "EmN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, 3.0, null, null, 1);
        var tinyDark = CreateDso("LDN-F29-TEMP", "Tiny Dark", "DrkN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 0.001, 0.001, 0, null, null, null, null, 2);
        foreach (var cap in new[] { 16, 64 })
        {
            var tempSettings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: 0) with
            {
                MaximumSamplePointsPerSource = cap,
                MinimumTemporalSamples = 16
            };
            var tempPlan = DeepSkyRenderPlan.Prepare(req, null, [tinyEmitter, tinyDark], null, opts, tempSettings);
            var darkDiag = tempPlan.Diagnostics.Single(d => d.ObjectId == tinyDark.Id);
            Assert.IsTrue(darkDiag.Admitted);
            Assert.IsTrue(darkDiag.SampledPoints > 0 && darkDiag.SampledPoints <= cap,
                $"Dark cloud + unresolved temporal samples must stay <= {cap}; got {darkDiag.SampledPoints}.");
        }
    }

    [TestMethod]
    public void DarkNebulaForegroundStarKernelVisitsStrictlyObeyConfiguredBudget()
    {
        // Exact F24 regression: a 20' dark cloud has a fixed baseline kernel-visit count. Adding 100 foreground HYG
        // stars inside its aperture MUST charge their kernel visits against MaximumKernelCellVisits (throwing when
        // the budget is pinned at the starless baseline and reporting > baseline visits when allowed to succeed).
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = CreateRequest(projection, HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 1.0, readNoise: 0);
        var centerJ2000 = AltAzToJ2000(new AltAzPoint(60.0, 180.0));

        var darkCloud = CreateDso("LDN-20-BUDGET", "20' Dark Cloud", "DrkN",
            centerJ2000.RaHours, centerJ2000.DecDegrees, 20, 20, 0, null, null, null, null, null);
        var baseSettings = CreateSettings(opts, req, minSnr: 0, celestialDiffuseBg: 100.0);
        var starlessPlan = DeepSkyRenderPlan.Prepare(req, null, [darkCloud], null, opts, baseSettings);
        var baselineKernelVisits = starlessPlan.Statistics.TotalKernelCellVisits;
        Assert.IsTrue(baselineKernelVisits > 0);

        var hundredStars = Enumerable.Range(0, 100)
            .Select(i =>
            {
                var dAlt = ((i % 10) - 4.5) * 0.01;
                var dAz = ((i / 10) - 4.5) * 0.01;
                var pos = AltAzToJ2000(new AltAzPoint(60.0 + dAlt, 180.0 + dAz));
                return new CelestialCatalogObject($"HIP-FG-{i:D3}", $"FgStar{i}", pos.RaHours, pos.DecDegrees, 1.0, 0.0);
            })
            .ToArray();

        // Pinning MaximumKernelCellVisits to starless baseline MUST fail instead of bypassing with long.MaxValue!
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSkyRenderPlan.Prepare(req, null, [darkCloud], hundredStars, opts,
                baseSettings with { MaximumKernelCellVisits = baselineKernelVisits }));

        // With generous budget, foreground star kernel visits are honestly counted in TotalKernelCellVisits.
        var withStarsPlan = DeepSkyRenderPlan.Prepare(req, null, [darkCloud], hundredStars, opts, baseSettings);
        Assert.IsTrue(withStarsPlan.Statistics.TotalKernelCellVisits > baselineKernelVisits,
            $"Adding 100 foreground stars inside dark cloud aperture must increase TotalKernelCellVisits above {baselineKernelVisits}; " +
            $"got {withStarsPlan.Statistics.TotalKernelCellVisits}.");
        Assert.IsTrue(
            withStarsPlan.Diagnostics.Single().PredictedSignal.NoiseVariance >
            starlessPlan.Diagnostics.Single().PredictedSignal.NoiseVariance * 10.0,
            "100 bright foreground stars must materially increase dark-cloud aperture noise variance.");
    }

    [TestMethod]
    public async Task DarkNebulaForegroundStarVarianceUsesExposureIntegratedRasterAcrossLongExposure()
    {
        // Exact F25 regression: for a 60 s exposure with a mag -4 star offset by 0.023 h in RA from a 20' dark cloud,
        // sidereal drift over the 60 s exposure puts ~43.5% of the star's flux inside the dark cloud's aperture
        // (S/N ~45.9 < 48), whereas a midpoint-only calculation predicts S/N ~51.2 > 48. Both direct Prepare and
        // StellarExposure-backed Prepare MUST match the exposure-integrated stellar raster and reject at S/N = 48.
        var exposure = TimeSpan.FromSeconds(60);
        var startUtc = Utc;
        var midUtc = startUtc.AddTicks(exposure.Ticks / 2);
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 80, 80, 1400, 1400, 160, 160,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var req = new VisibleSceneRequest(midUtc, HvoObserver, projection, new CatalogQuery(20, 100), CatalogMeta,
            new RefractionOptions(false), HorizonPolicy.GeometricHorizon);
        var opts = CreateDeterministicMonoOptions(background: 0, exposureSeconds: 60.0, readNoise: 0);

        var centerEqDate = CoordinateTransforms.HorizontalToEquatorial(
            new AltAzPoint(60.0, 180.0), midUtc, HvoObserver.LatitudeDegrees, HvoObserver.LongitudeDegrees);
        var centerJ2000 = EquatorialPrecession.PrecessToJ2000(centerEqDate, midUtc);

        var darkCloud = CreateDso("LDN-20-60S", "20' Dark Cloud 60s", "DrkN",
            centerJ2000.RightAscensionHours, centerJ2000.DeclinationDegrees, 20, 20, 0, null, null, null, null, null);
        var starRaHours = (((centerJ2000.RightAscensionHours + 0.023) % 24d) + 24d) % 24d;
        var movingStar = new CelestialCatalogObject(
            "HIP-MOVING-M4", "MovingMagMinus4", starRaHours, centerJ2000.DeclinationDegrees, -4.0, 0.0);

        var starCatalog = new InMemoryCelestialCatalog([movingStar]);
        var stellarGeom = await new StellarExposureGeometryBuilder(starCatalog)
            .BuildAsync(req, startUtc, exposure, new StellarExposureGeometryOptions(
                MaximumCandidates: 1, MaximumSamplesPerSource: 64, MaximumStepPixels: 0.15, PsfSupportRadiusPixels: 4.0))
            .ConfigureAwait(false);
        var stellarPlan = StellarExposureRenderPlan.Prepare(
            stellarGeom, opts, new StellarExposureRenderSettings(MinimumSignalToNoise: 1e-3));
        Assert.IsTrue(stellarPlan.Sources.Single().Samples.Count >= 30,
            $"60 s exposure moving star must span many temporal samples; got {stellarPlan.Sources.Single().Samples.Count}.");

        var settingsAt48 = CreateSettings(opts, req, minSnr: 48.0, celestialDiffuseBg: 100.0) with
        {
            ExposureStartUtc = startUtc,
            MaximumTemporalSamples = 64,
            MaximumTemporalStepPixels = 0.15
        };

        var directPlan = DeepSkyRenderPlan.Prepare(req, null, [darkCloud], [movingStar], opts, settingsAt48);
        var reusedStellarPlan = DeepSkyRenderPlan.Prepare(
            req, null, [darkCloud], [movingStar], opts with { StellarExposure = stellarPlan }, settingsAt48);

        var directDiag = directPlan.Diagnostics.Single();
        var reusedDiag = reusedStellarPlan.Diagnostics.Single();

        Assert.AreEqual(directDiag.PredictedSignal.SourceElectrons, reusedDiag.PredictedSignal.SourceElectrons, 1e-9);
        Assert.AreEqual(directDiag.PredictedSignal.NoiseVariance, reusedDiag.PredictedSignal.NoiseVariance, 1e-6,
            "Direct Prepare and StellarExposure-backed Prepare must compute identical exposure-integrated foreground variance.");
        Assert.AreEqual(directDiag.PredictedSignal.SignalToNoise, reusedDiag.PredictedSignal.SignalToNoise, 1e-9);

        Assert.IsFalse(directDiag.Admitted,
            $"Exposure-integrated moving mag -4 star must pull 20' dark-cloud S/N below 48; got S/N={directDiag.PredictedSignal.SignalToNoise:F4}.");
        Assert.AreEqual("annotation-only-below-snr", directDiag.Reason);
        Assert.IsFalse(reusedDiag.Admitted);
        Assert.AreEqual("annotation-only-below-snr", reusedDiag.Reason);
        Assert.IsTrue(directDiag.PredictedSignal.SignalToNoise is > 43.0 and < 48.0,
            $"Expected exposure-integrated S/N ~45.9 < 48; got {directDiag.PredictedSignal.SignalToNoise:F4}.");

        // Control: lowering threshold to 40 admits the dark cloud cleanly.
        var planAt40 = DeepSkyRenderPlan.Prepare(req, null, [darkCloud], [movingStar], opts,
            settingsAt48 with { MinimumSignalToNoise = 40.0 });
        Assert.IsTrue(planAt40.Diagnostics.Single().Admitted);
    }

    private static ImageLayout Mono16Layout(int width, int height) =>
        new(width, height, CameraPixelFormat.Mono16, width * 2);

    private static VisibleSceneRequest CreateRequest(ProjectionContext projection, HorizonPolicy horizonPolicy) =>
        new(Utc, HvoObserver, projection, new CatalogQuery(20, 100), CatalogMeta, new RefractionOptions(false), horizonPolicy);

    private static Mono16SceneRenderOptions CreateDeterministicMonoOptions(
        double background = 0,
        double exposureSeconds = 1.0,
        double gain = 1.0,
        double psfSigma = 1.0,
        double psfRadius = 4.0,
        double readNoise = 0) => new()
        {
            ExposureSeconds = exposureSeconds,
            Gain = gain,
            MagnitudeZeroElectronsPerSecond = 10_000,
            BackgroundElectronsPerSecond = background,
            PsfSigmaPixels = psfSigma,
            PsfRadiusPixels = psfRadius,
            VignettingStrength = 0,
            Bias = 0,
            ReadNoiseStandardDeviation = readNoise,
            ShotNoiseEnabled = false,
            DarkCurrentElectronsPerSecond = 0,
            Seed = 12345
        };

    private static DeepSkyRenderSettings CreateSettings(
        LinearSceneRenderOptions renderOptions,
        VisibleSceneRequest request,
        double minSnr = 0,
        int samplesPerPixel = 2,
        bool allowB25Fallback = false,
        double celestialDiffuseBg = 0,
        DeepSkySyntheticScenario? syntheticScenario = null) =>
        new(
            MinimumSignalToNoise: minSnr,
            SamplesPerProjectedPixel: samplesPerPixel,
            PsfSigmaPixels: renderOptions.PsfSigmaPixels,
            PsfRadiusPixels: renderOptions.PsfRadiusPixels,
            HorizonPolicy: request.HorizonPolicy,
            Refraction: request.Refraction,
            AllowSurfaceBrightnessB25Fallback: allowB25Fallback,
            CelestialDiffuseBackgroundElectronsPerSecond: celestialDiffuseBg,
            SyntheticScenario: syntheticScenario);

    private static (double RaHours, double DecDegrees) AltAzToJ2000(AltAzPoint altAz)
    {
        var eqDate = CoordinateTransforms.HorizontalToEquatorial(altAz, Utc, HvoObserver.LatitudeDegrees, HvoObserver.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(eqDate, Utc);
        return (j2000.RightAscensionHours, j2000.DeclinationDegrees);
    }

    private static DeepSkyObject CreateDso(
        string id,
        string displayName,
        string objectType,
        double raHours,
        double decDegrees,
        double? majArcmin,
        double? minArcmin,
        double? paDeg,
        double? bMag,
        double? vMag,
        double? surfBr,
        string? hubble,
        int? messier) =>
        new(id, id, displayName, objectType, raHours, decDegrees, "Ori",
            majArcmin, minArcmin, paDeg, bMag, vMag, surfBr, hubble, messier, null, null, null);

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

    private static long SumMono16(ReadOnlySpan<byte> bytes)
    {
        long sum = 0;
        for (var i = 0; i < bytes.Length; i += 2) sum += bytes[i] | (bytes[i + 1] << 8);
        return sum;
    }

    private static int MaxMono16Difference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Assert.AreEqual(a.Length, b.Length);
        var max = 0;
        for (var i = 0; i < a.Length; i += 2)
        {
            var va = a[i] | (a[i + 1] << 8);
            var vb = b[i] | (b[i + 1] << 8);
            max = Math.Max(max, Math.Abs(va - vb));
        }
        return max;
    }

    private static (long R, long G, long B) SumRgb24Box(ReadOnlySpan<byte> pixels, int width, int minX, int minY, int maxX, int maxY)
    {
        long r = 0, g = 0, b = 0;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var o = (y * width + x) * 3;
                r += pixels[o];
                g += pixels[o + 1];
                b += pixels[o + 2];
            }
        }
        return (r, g, b);
    }

    private static (double R, double G, double B) MeanBayerRggb16Box(ReadOnlySpan<byte> pixels, int width, int minX, int minY, int maxX, int maxY)
    {
        long r = 0, g = 0, b = 0;
        int rc = 0, gc = 0, bc = 0;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var o = (y * width + x) * 2;
                var v = pixels[o] | (pixels[o + 1] << 8);
                var phase = ((y & 1) << 1) | (x & 1);
                if (phase == 0) { r += v; rc++; }
                else if (phase == 3) { b += v; bc++; }
                else { g += v; gc++; }
            }
        }
        return (rc > 0 ? r / (double)rc : 0, gc > 0 ? g / (double)gc : 0, bc > 0 ? b / (double)bc : 0);
    }
}
