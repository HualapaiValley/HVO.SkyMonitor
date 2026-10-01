using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StellarExposureRenderPlanTests
{
    private static readonly DateTimeOffset Utc = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Observer = new(0, 0, 0);
    private static readonly CatalogMetadata Metadata = new("HYG", "fixture", new("https://astronexus.com/projects/hyg"),
        new string('A', 64), "CC BY-SA 4.0", "2");
    private static readonly ProjectionContext Projection = new(ProjectionModel.Perspective, 16, 16, 100, 100, 32, 32,
        ProjectionAperture.Rectangular);

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, false)]
    [DataRow(CameraPixelFormat.Mono16, true)]
    [DataRow(CameraPixelFormat.Rgb24, false)]
    [DataRow(CameraPixelFormat.Rgb24, true)]
    [DataRow(CameraPixelFormat.BayerRggb16, false)]
    [DataRow(CameraPixelFormat.BayerRggb16, true)]
    public async Task NativeColorRenderPathsApplyCloudOnceAndRetainTemporalWork(CameraPixelFormat format, bool clouded)
    {
        var (geometry, scene) = await Scene(1).ConfigureAwait(false);
        var cloud = new VirtualCloudRenderContext(new VirtualCloudField(new VirtualCloudScenarioDefinition
        {
            ScenarioId = "stellar-color-cloud",
            EpochUtc = Utc,
            HorizonFadeDegrees = 0,
            TemporalSampleCount = 2,
            Keyframes = [new() { Coverage = 1, MaximumOpacity = .5, ScatterFraction = .1 }]
        }), Utc.AddSeconds(-.05), TimeSpan.FromSeconds(.1));
        var response = new MonoSensorResponse
        {
            AdcBitDepth = 16,
            FullWellElectrons = 1e6,
            ElectronsPerAdu = 1,
            ReadNoiseElectrons = 0
        };
        LinearSceneRenderOptions clear = format switch
        {
            CameraPixelFormat.Rgb24 => new Rgb24CompatibilityRenderOptions(),
            CameraPixelFormat.BayerRggb16 => new BayerRggb16RenderOptions { SensorResponse = response },
            _ => new Mono16SceneRenderOptions { SensorResponse = response }
        };
        clear = clear with
        {
            ExposureSeconds = .1,
            MagnitudeZeroElectronsPerSecond = format == CameraPixelFormat.Rgb24 ? 10000 : 1000000,
            BackgroundElectronsPerSecond = 100,
            DarkCurrentElectronsPerSecond = 0,
            ReadNoiseStandardDeviation = 0
        };
        var options = clear with { Cloud = clouded ? cloud : null };
        var clearPlan = StellarExposureRenderPlan.Prepare(geometry, clear, new(MinimumSignalToNoise: .01));
        var plan = StellarExposureRenderPlan.Prepare(geometry, options, new(MinimumSignalToNoise: .01));
        var layout = new ImageLayout(32, 32, format, format == CameraPixelFormat.Rgb24 ? 96 : 64);
        SceneRenderResult Render(LinearSceneRenderOptions configured, StellarExposureRenderPlan selected)
            => format switch
            {
                CameraPixelFormat.Rgb24 => Rgb24CompatibilityRenderer.Render(scene, layout,
                    (Rgb24CompatibilityRenderOptions)configured with { StellarExposure = selected }),
                CameraPixelFormat.BayerRggb16 => BayerRggb16Renderer.Render(scene, layout,
                    (BayerRggb16RenderOptions)configured with { StellarExposure = selected }),
                _ => Mono16SceneRenderer.Render(scene, layout,
                    (Mono16SceneRenderOptions)configured with { StellarExposure = selected })
            };
        var result = Render(options, plan);
        CollectionAssert.AreEqual(result.Pixels.ToArray(), Render(options, plan).Pixels.ToArray());
        Assert.HasCount(1, result.Objects);
        Assert.AreSame(plan.Predictions, result.StellarPredictions);
        Assert.IsNotNull(result.StellarStatistics);
        var planes = format == CameraPixelFormat.Mono16 ? 1 : 3;
        Assert.AreEqual(planes, result.StellarStatistics.RenderPlanes);
        Assert.AreEqual(plan.PredictionKernelCellVisits * planes, result.StellarStatistics.RenderKernelCellVisits);
        Assert.AreEqual(clearPlan.Predictions[0].Signal.SourceElectrons * (clouded ? .5 : 1),
            plan.Predictions[0].Signal.SourceElectrons, 1e-6);
        if (format != CameraPixelFormat.Rgb24)
        {
            // This corner is outside the source kernel: 100e/s * 0.1s, or (0.5+0.1)*that under clouds.
            Assert.AreEqual((ushort)(clouded ? 6 : 10),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(result.Pixels.Span));
        }
        if (clouded) Assert.IsLessThan(Render(clear, clearPlan).Statistics.Maximum, result.Statistics.Maximum);
    }

    [TestMethod]
    public async Task NativeAperturePredictionMatchesIndependentElectronEquation()
    {
        var (geometry, _) = await Scene(1).ConfigureAwait(false);
        var options = Options();
        var plan = StellarExposureRenderPlan.Prepare(geometry, options, new(MinimumSignalToNoise: .01));
        Assert.HasCount(1, plan.Sources);
        var prediction = plan.Predictions[0];
        var sample = geometry.Sources[0].Samples[0];
        var aperturePixels = Kernel(sample.Pixel).Count;
        var electrons = 1000 * Math.Pow(10, -.4) * .1;
        var variance = electrons + aperturePixels * (2 * .1 + .3 * .1 + 9);
        Assert.AreEqual(electrons, prediction.Signal.SourceElectrons, electrons * 1e-6);
        Assert.AreEqual(variance, prediction.Signal.NoiseVariance, variance * 1e-6);
        Assert.AreEqual(electrons / Math.Sqrt(variance), prediction.Signal.SignalToNoise, 1e-6);
        Assert.AreEqual(1, prediction.RetainedOpticalEnergy, 1e-12);
        Assert.AreEqual(plan.ComputePredictionsSha256(),
            StellarExposureRenderPlan.Prepare(geometry, options, new(MinimumSignalToNoise: .01)).ComputePredictionsSha256());
        Assert.AreNotEqual(plan.ComputePredictionsSha256(),
            StellarExposureRenderPlan.Prepare(geometry, options, new(MinimumSignalToNoise: 1e6)).ComputePredictionsSha256());
    }

    [TestMethod]
    public async Task SaturatedSourcesRemainAdmittedWithReasonWhileFaintSourcesAreRejected()
    {
        var (geometry, _) = await Scene(1).ConfigureAwait(false);
        var options = Options() with { SensorResponse = new() { FullWellElectrons = 1, ReadNoiseElectrons = 3 } };
        var saturated = StellarExposureRenderPlan.Prepare(geometry, options, new(MinimumSignalToNoise: 1e6));
        Assert.HasCount(1, saturated.Sources);
        Assert.AreEqual("expected-saturation", saturated.Predictions[0].Reason);
        Assert.IsTrue(saturated.Predictions[0].ExpectedSaturation);
        var faint = StellarExposureRenderPlan.Prepare(geometry, Options(), new(MinimumSignalToNoise: 1e6));
        Assert.IsEmpty(faint.Sources);
        Assert.AreEqual("below-expected-snr", faint.Predictions[0].Reason);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 0)]
    [DataRow(0, 1)]
    [DataRow(1, 1)]
    public async Task NativeCfaAdmissionUsesActualPhotositeColorAndResponse(int originX, int originY)
    {
        var (geometry, _) = await Scene(1, 1.1, new(16.3, 15.7)).ConfigureAwait(false);
        var options = new BayerRggb16RenderOptions
        {
            ExposureSeconds = .1,
            MagnitudeZeroElectronsPerSecond = 1000,
            SensorResponse = new() { ReadNoiseElectrons = 0, FullWellElectrons = 1e6 },
            ChannelResponse = new(.6, .8, .9)
        };
        var plan = StellarExposureRenderPlan.Prepare(geometry, options,
            new(MinimumSignalToNoise: .01, CfaOriginX: originX, CfaOriginY: originY));
        var kernel = Kernel(geometry.Sources[0].Samples[0].Pixel);
        var normalization = kernel.Sum(static pixel => pixel.Weight);
        var weighted = 0d;
        foreach (var pixel in kernel)
        {
            var factor = ((pixel.Y + originY) % 2, (pixel.X + originX) % 2) switch
            {
                (0, 0) => Math.Exp(.35 * 1.1) * .6,
                (1, 1) => Math.Exp(-.65 * 1.1) * .9,
                _ => .8
            };
            weighted += pixel.Weight / normalization * factor;
        }
        var expected = 1000 * .1 * Math.Pow(10, -.4) * weighted;
        Assert.AreEqual(expected, plan.Predictions[0].Signal.SourceElectrons, expected * 1e-6);
        Assert.AreEqual(Math.Sqrt(expected), plan.Predictions[0].Signal.SignalToNoise, 1e-6);
    }

    [TestMethod]
    public async Task DigitalAverageAndSumShareNativeNoiseAndIncludeFullBinBackground()
    {
        var (geometry, _) = await Scene(1).ConfigureAwait(false);
        var average = StellarExposureRenderPlan.Prepare(geometry, Options(),
            new(MinimumSignalToNoise: .01, DigitalBinX: 4, DigitalBinY: 4));
        var sum = StellarExposureRenderPlan.Prepare(geometry, Options(),
            new(MinimumSignalToNoise: .01, DigitalBinX: 4, DigitalBinY: 4, DigitalAverage: false));
        var native = StellarExposureRenderPlan.Prepare(geometry, Options(), new(MinimumSignalToNoise: .01));
        var a = average.Predictions[0].Signal; var s = sum.Predictions[0].Signal;
        Assert.AreEqual(s.SourceElectrons / 16, a.SourceElectrons, 1e-12);
        Assert.AreEqual(s.NoiseVariance / 256, a.NoiseVariance, 1e-12);
        Assert.AreEqual(s.SignalToNoise, a.SignalToNoise, 1e-12);
        Assert.IsGreaterThan(native.Predictions[0].Signal.NoiseVariance, s.NoiseVariance);
    }

    [TestMethod]
    public async Task RendererBindsAdmissionOptionsAndDepositsOneTemporalSignalBeforeSensorPass()
    {
        var (geometry, scene) = await Scene(1).ConfigureAwait(false);
        var options = Options() with
        {
            MagnitudeZeroElectronsPerSecond = 100000,
            BackgroundElectronsPerSecond = 0,
            DarkCurrentElectronsPerSecond = 0,
            SensorResponse = new()
            {
                AdcBitDepth = 16,
                FullWellElectrons = 1e6,
                ElectronsPerAdu = 1,
                ReadNoiseElectrons = 0
            }
        };
        var plan = StellarExposureRenderPlan.Prepare(geometry, options, new(MinimumSignalToNoise: .01));
        var layout = new ImageLayout(32, 32, CameraPixelFormat.Mono16, 64);
        var result = Mono16SceneRenderer.Render(scene, layout, options with { StellarExposure = plan });
        var total = 0L;
        for (var index = 0; index < result.Pixels.Length; index += 2)
            total += System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(result.Pixels.Span.Slice(index, 2));
        Assert.AreEqual(plan.Predictions[0].Signal.SourceElectrons, total, Kernel(geometry.Sources[0].Samples[0].Pixel).Count / 2d);
        StringAssert.Contains(result.AlgorithmVersion, StellarExposureRenderPlan.AlgorithmVersion, StringComparison.Ordinal);
        Assert.IsNotNull(result.StellarStatistics);
        Assert.AreEqual(1, result.StellarStatistics.CatalogCandidates);
        Assert.AreEqual(1, result.StellarStatistics.AdmittedSources);
        Assert.AreEqual(1, result.StellarStatistics.RenderPlanes);
        Assert.AreEqual(1024L, result.StellarStatistics.NativeBufferPixels);
        Assert.AreEqual(plan.PredictionKernelCellVisits, result.StellarStatistics.PredictionKernelCellVisits);
        Assert.AreEqual(plan.PredictionKernelCellVisits, result.StellarStatistics.RenderKernelCellVisits);
        Assert.AreSame(plan.Predictions, result.StellarPredictions);
        Assert.Throws<ArgumentException>(() => Mono16SceneRenderer.Render(scene, layout,
            options with { StellarExposure = plan, BackgroundElectronsPerSecond = 1 }));
    }

    [TestMethod]
    public async Task AdmissionWorkBudgetAcceptsExactLimitAndRefusesOneBelow()
    {
        var (geometry, _) = await Scene(1).ConfigureAwait(false);
        var baseline = StellarExposureRenderPlan.Prepare(geometry, Options(), new(MinimumSignalToNoise: .01));
        var exact = StellarExposureRenderPlan.Prepare(geometry, Options(),
            new(MinimumSignalToNoise: .01, MaximumKernelCellVisits: baseline.PredictionKernelCellVisits));
        Assert.AreEqual(baseline.PredictionKernelCellVisits, exact.PredictionKernelCellVisits);
        Assert.Throws<InvalidOperationException>(() => StellarExposureRenderPlan.Prepare(geometry, Options(),
            new(MaximumKernelCellVisits: baseline.PredictionKernelCellVisits - 1)));
    }

    [TestMethod]
    public async Task CompatibilityRgbAdmissionUsesObservableChannelWeightsBeforeDisplayQuantization()
    {
        var (geometry, _) = await Scene(1, 1.1).ConfigureAwait(false);
        var options = new Rgb24CompatibilityRenderOptions
        {
            ExposureSeconds = .1,
            MagnitudeZeroElectronsPerSecond = 1000,
            Gain = 2,
            ReadNoiseStandardDeviation = 4,
            BackgroundElectronsPerSecond = 10,
            ChannelResponse = new(2, 0, 0)
        };
        var plan = StellarExposureRenderPlan.Prepare(geometry, options, new(MinimumSignalToNoise: .01));
        var source = 100 * Math.Pow(10, -.4) * Math.Exp(.35 * 1.1);
        var variance = source + Kernel(geometry.Sources[0].Samples[0].Pixel).Count * (1 + 4);
        Assert.AreEqual(source, plan.Predictions[0].Signal.SourceElectrons, source * 1e-6);
        Assert.AreEqual(source / Math.Sqrt(variance), plan.Predictions[0].Signal.SignalToNoise, 1e-6);
        var disabled = StellarExposureRenderPlan.Prepare(geometry, options with { WhiteBalance = new(0, 0, 0) });
        Assert.IsEmpty(disabled.Sources);
    }

    [TestMethod]
    public async Task ShiftedCfaPredictionCannotBePublishedAsNativeRggbPixels()
    {
        var (geometry, scene) = await Scene(1).ConfigureAwait(false);
        var options = new BayerRggb16RenderOptions { ExposureSeconds = .1, MagnitudeZeroElectronsPerSecond = 1000 };
        var plan = StellarExposureRenderPlan.Prepare(geometry, options,
            new(MinimumSignalToNoise: .01, CfaOriginX: 1));
        Assert.Throws<ArgumentException>(() => BayerRggb16Renderer.Render(scene,
            new(32, 32, CameraPixelFormat.BayerRggb16, 64), options with { StellarExposure = plan }));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(16)]
    public async Task AccumulatedExposureHasOnePoissonAndReadNoiseStage(int temporalSamples)
    {
        var (_, scene) = await Scene(0, pixel: new(16.3, 15.7)).ConfigureAwait(false);
        var source = new InMemoryCelestialCatalog([new CelestialCatalogObject("source", "source",
            scene.Objects[0].J2000Equatorial.RightAscensionHours, scene.Objects[0].J2000Equatorial.DeclinationDegrees, 0)]);
        var geometry = await new StellarExposureGeometryBuilder(source).BuildAsync(scene.Request,
            Utc.AddSeconds(-.05), TimeSpan.FromSeconds(.1), new(MinimumSamplesPerSource: temporalSamples)).ConfigureAwait(false);
        var options = Options() with
        {
            MagnitudeZeroElectronsPerSecond = 5000,
            BackgroundElectronsPerSecond = 20,
            ShotNoiseEnabled = true,
            DarkNoiseEnabled = true,
            SensorResponse = new()
            { AdcBitDepth = 16, ElectronsPerAdu = 1, ReadNoiseElectrons = 3, FullWellElectrons = 1e6, BlackLevelAdu = 512 }
        };
        var plan = StellarExposureRenderPlan.Prepare(geometry, options, new(MinimumSignalToNoise: .01));
        var kernel = Kernel(geometry.Sources[0].Samples[0].Pixel);
        var pixel = kernel.MaxBy(static item => item.Weight);
        var fraction = kernel.Single(item => item.X == pixel.X && item.Y == pixel.Y).Weight / kernel.Sum(static item => item.Weight);
        var expectedCharge = 500 * fraction + 2 + .03;
        var expectedVariance = expectedCharge + 9 + 1d / 12;
        var values = new double[512];
        for (var index = 0; index < values.Length; index++)
        {
            var result = Mono16SceneRenderer.Render(scene, new(32, 32, CameraPixelFormat.Mono16, 64),
                options with { Seed = 52201 + index * 104729, StellarExposure = plan });
            values[index] = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
                result.Pixels.Span.Slice((pixel.Y * 32 + pixel.X) * 2, 2));
        }
        var mean = values.Average();
        var variance = values.Sum(value => Math.Pow(value - mean, 2)) / (values.Length - 1);
        Assert.AreEqual(512 + expectedCharge, mean, 3);
        Assert.AreEqual(expectedVariance, variance, expectedVariance * .2);
    }

    private static Mono16SceneRenderOptions Options() => new()
    {
        ExposureSeconds = .1,
        MagnitudeZeroElectronsPerSecond = 1000,
        BackgroundElectronsPerSecond = 2,
        DarkCurrentElectronsPerSecond = .3,
        SensorResponse = new() { AdcBitDepth = 16, ElectronsPerAdu = 1, ReadNoiseElectrons = 3, FullWellElectrons = 1e6 }
    };

    private static async Task<(StellarExposureGeometry Geometry, VisibleScene Scene)> Scene(double magnitude,
        double? color = null, PixelPoint? pixel = null)
    {
        var horizontal = ProjectorFactory.Create(Projection).Unproject(pixel ?? new(16, 16))!.Value;
        var ofDate = CoordinateTransforms.HorizontalToEquatorial(horizontal, Utc, 0, 0);
        var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, Utc);
        var catalog = new InMemoryCelestialCatalog([new("source", "source", j2000.RightAscensionHours,
            j2000.DeclinationDegrees, magnitude, ColorIndex: color)]);
        var request = new VisibleSceneRequest(Utc, Observer, Projection, new(7, 100), Metadata,
            horizonPolicy: HorizonPolicy.ProjectionOnly);
        var geometry = await new StellarExposureGeometryBuilder(catalog).BuildAsync(request,
            Utc.AddSeconds(-.05), TimeSpan.FromSeconds(.1)).ConfigureAwait(false);
        var scene = await new VisibleSceneBuilder(catalog).BuildAsync(request).ConfigureAwait(false);
        return (geometry, scene);
    }

    private static List<(int X, int Y, double Weight)> Kernel(PixelPoint center)
    {
        var result = new List<(int, int, double)>();
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var radiusSquared = Math.Pow(x + .5 - center.X, 2) + Math.Pow(y + .5 - center.Y, 2);
                if (radiusSquared <= 16) result.Add((x, y, Math.Exp(-radiusSquared / 2)));
            }
        return result;
    }
}
