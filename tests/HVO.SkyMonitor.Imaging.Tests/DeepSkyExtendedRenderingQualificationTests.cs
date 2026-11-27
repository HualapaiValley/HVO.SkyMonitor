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

        // 2. Exposure scaling invariant: 2x exposure => 2x excess ADU.
        var render1s = Mono16SceneRenderer.Render(scene, layout, opts with { ExposureSeconds = 1.0, DeepSkySources = doublePlan });
        var render2s = Mono16SceneRenderer.Render(scene, layout, opts with { ExposureSeconds = 2.0, DeepSkySources = doublePlan });
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
        var noVig = Mono16SceneRenderer.Render(scene, layout, opts with { VignettingStrength = 0, DeepSkySources = offAxisPlan });
        var withVig = Mono16SceneRenderer.Render(scene, layout, opts with { VignettingStrength = 0.5, DeepSkySources = offAxisPlan });
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
        var clouded = Mono16SceneRenderer.Render(scene, layout, opts with { Cloud = cloudContext, DeepSkySources = doublePlan });
        var cloudRatio = SumMono16(clouded.Pixels.Span) / (double)sum1s;
        Assert.IsTrue(cloudRatio is > 0.35 and < 0.45, $"Expected ~0.40 cloud transmission, got {cloudRatio:F4}.");

        // 6. Binning invariant via MonoDigitalReadoutRenderer (2x2 DigitalSumV1 & DigitalAverageV1).
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
        var plan = DeepSkyRenderPlan.Prepare(req, null, [galaxy, nebula], null, monoBase, CreateSettings(monoBase, req));

        var monoLayout = Mono16Layout(160, 160);
        var mono1 = Mono16SceneRenderer.Render(emptyScene, monoLayout, monoBase with { DeepSkySources = plan });
        var mono2 = Mono16SceneRenderer.Render(emptyScene, monoLayout, monoBase with { DeepSkySources = plan });
        CollectionAssert.AreEqual(mono1.Pixels.ToArray(), mono2.Pixels.ToArray());
        StringAssert.Contains(mono1.AlgorithmVersion, DeepSkyRenderPlan.AlgorithmVersion, StringComparison.Ordinal);

        var rgbLayout = new ImageLayout(160, 160, CameraPixelFormat.Rgb24, 160 * 3);
        var rgbOpts = new Rgb24CompatibilityRenderOptions
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
            Seed = 777,
            DeepSkySources = plan
        };
        var rgb1 = Rgb24CompatibilityRenderer.Render(emptyScene, rgbLayout, rgbOpts);
        var rgb2 = Rgb24CompatibilityRenderer.Render(emptyScene, rgbLayout, rgbOpts);
        CollectionAssert.AreEqual(rgb1.Pixels.ToArray(), rgb2.Pixels.ToArray());
        StringAssert.Contains(rgb1.AlgorithmVersion, DeepSkyRenderPlan.AlgorithmVersion, StringComparison.Ordinal);

        var bayerLayout = new ImageLayout(160, 160, CameraPixelFormat.BayerRggb16, 160 * 2);
        var bayerOpts = new BayerRggb16RenderOptions
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
            Seed = 777,
            DeepSkySources = plan
        };
        var bayer1 = BayerRggb16Renderer.Render(emptyScene, bayerLayout, bayerOpts);
        var bayer2 = BayerRggb16Renderer.Render(emptyScene, bayerLayout, bayerOpts);
        CollectionAssert.AreEqual(bayer1.Pixels.ToArray(), bayer2.Pixels.ToArray());
        StringAssert.Contains(bayer1.AlgorithmVersion, DeepSkyRenderPlan.AlgorithmVersion, StringComparison.Ordinal);

        Assert.AreEqual(mono1.DeepSkyStatistics!.DiagnosticsSha256, rgb1.DeepSkyStatistics!.DiagnosticsSha256);
        Assert.AreEqual(mono1.DeepSkyStatistics!.DiagnosticsSha256, bayer1.DeepSkyStatistics!.DiagnosticsSha256);

        var nebFp = plan.Diagnostics.Single(static d => d.ObjectId == "NGC1976").Footprint!;
        var (rgbR, _, rgbB) = SumRgb24Box(rgb1.Pixels.Span, 160, nebFp.MinimumX, nebFp.MinimumY, nebFp.MaximumX, nebFp.MaximumY);
        var (bayerMeanR, _, bayerMeanB) = MeanBayerRggb16Box(bayer1.Pixels.Span, 160, nebFp.MinimumX, nebFp.MinimumY, nebFp.MaximumX, nebFp.MaximumY);
        Assert.IsTrue(rgbR > rgbB, $"Emission nebula should have stronger red H-alpha than blue in RGB24 (R={rgbR}, B={rgbB}).");
        Assert.IsTrue(bayerMeanR > bayerMeanB, $"Emission nebula should have stronger mean red photosite response than blue in Bayer RGGB16 (meanR={bayerMeanR:F2}, meanB={bayerMeanB:F2}).");
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
