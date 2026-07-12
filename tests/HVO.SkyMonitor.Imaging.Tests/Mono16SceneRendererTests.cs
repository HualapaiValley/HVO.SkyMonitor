using System.Diagnostics;
using System.Reflection;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
public sealed class Mono16SceneRendererTests
{
    [TestMethod]
    public void RelativeFlux_UsesDocumentedMagnitudeFormulaAndIsMonotonic()
    {
        Assert.AreEqual(1, Mono16SceneRenderer.RelativeFlux(0), 1e-14);
        Assert.AreEqual(0.1, Mono16SceneRenderer.RelativeFlux(2.5), 1e-14);
        Assert.IsTrue(Mono16SceneRenderer.RelativeFlux(-1) > Mono16SceneRenderer.RelativeFlux(0));
        Assert.IsTrue(Mono16SceneRenderer.RelativeFlux(0) > Mono16SceneRenderer.RelativeFlux(1));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void RelativeFlux_RejectsNonFiniteMagnitude(double magnitude)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Mono16SceneRenderer.RelativeFlux(magnitude));

    [TestMethod]
    public async Task Render_RejectsNullSceneWrongFormatAndProjectionDimensionMismatch()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(3, 3, 2).ConfigureAwait(false);

        Assert.Throws<ArgumentNullException>(() => Mono16SceneRenderer.Render(null!, Layout(3, 3)));
        Assert.Throws<ArgumentException>(() => Mono16SceneRenderer.Render(scene,
            new ImageLayout(3, 3, CameraPixelFormat.Mono8, 3)));
        Assert.Throws<ArgumentException>(() => Mono16SceneRenderer.Render(scene, Layout(4, 3)));
        Assert.Throws<ArgumentException>(() => Mono16SceneRenderer.Render(scene, Layout(3, 4)));

        var defaultResult = Mono16SceneRenderer.Render(scene, Layout(3, 3));
        Assert.IsTrue(defaultResult.Statistics.ActivePixelCount > 0);
    }

    [TestMethod]
    public async Task Render_RejectsEveryInvalidLinearOption()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(3, 3, 2).ConfigureAwait(false);
        Mono16SceneRenderOptions[] invalid =
        [
            BaseOptions with { ExposureSeconds = -1 },
            BaseOptions with { ExposureSeconds = double.NaN },
            BaseOptions with { Gain = -1 },
            BaseOptions with { Gain = double.PositiveInfinity },
            BaseOptions with { MagnitudeZeroElectronsPerSecond = -1 },
            BaseOptions with { MagnitudeZeroElectronsPerSecond = double.NaN },
            BaseOptions with { BackgroundElectronsPerSecond = -1 },
            BaseOptions with { BackgroundElectronsPerSecond = double.PositiveInfinity },
            BaseOptions with { PsfSigmaPixels = 0 },
            BaseOptions with { PsfSigmaPixels = double.NaN },
            BaseOptions with { PsfRadiusPixels = 0 },
            BaseOptions with { PsfRadiusPixels = 65 },
            BaseOptions with { PsfRadiusPixels = double.NaN },
            BaseOptions with { VignettingStrength = -0.1 },
            BaseOptions with { VignettingStrength = 1.1 },
            BaseOptions with { VignettingStrength = double.NaN },
            BaseOptions with { Bias = -1 },
            BaseOptions with { Bias = double.NaN },
            BaseOptions with { ReadNoiseStandardDeviation = -1 },
            BaseOptions with { ReadNoiseStandardDeviation = double.PositiveInfinity },
            BaseOptions with { DarkCurrentElectronsPerSecond = -1 },
            BaseOptions with { DarkCurrentElectronsPerSecond = double.NaN },
            BaseOptions with { Defects = [new SensorDefect(0, 0, double.NaN)] },
            BaseOptions with { Defects = [new SensorDefect(0, 0, FixedValue: double.PositiveInfinity)] },
            BaseOptions with { ExposureSeconds = double.MaxValue, Gain = 2 },
            BaseOptions with { ExposureSeconds = 2, BackgroundElectronsPerSecond = double.MaxValue },
            BaseOptions with { ExposureSeconds = 2, MagnitudeZeroElectronsPerSecond = double.MaxValue },
            BaseOptions with { ExposureSeconds = 2, DarkCurrentElectronsPerSecond = double.MaxValue }
        ];

        foreach (var options in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Mono16SceneRenderer.Render(scene, Layout(3, 3), options));
        }

        Assert.Throws<ArgumentNullException>(() => Mono16SceneRenderer.Render(scene, Layout(3, 3),
            BaseOptions with { Defects = null! }));
        Assert.Throws<ArgumentNullException>(() => Mono16SceneRenderer.Render(scene, Layout(3, 3),
            BaseOptions with { Defects = new SensorDefect[] { null! } }));
    }

    [TestMethod]
    public async Task Render_AppliesFixedAndOffsetDefectsAfterNoiseAndRejectsOutOfBoundsDefects()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(5, 5, 2).ConfigureAwait(false);
        var options = BaseOptions with
        {
            BackgroundElectronsPerSecond = 10,
            Defects =
            [
                new SensorDefect(2, 2, Offset: 7),
                new SensorDefect(3, 2, FixedValue: 42),
                new SensorDefect(0, 0, FixedValue: 500)
            ]
        };

        var result = Mono16SceneRenderer.Render(scene, Layout(5, 5), options);

        Assert.AreEqual((ushort)17, Read(result.Pixels, 5, 2, 2));
        Assert.AreEqual((ushort)42, Read(result.Pixels, 5, 3, 2));
        Assert.AreEqual((ushort)0, Read(result.Pixels, 5, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mono16SceneRenderer.Render(scene, Layout(5, 5),
            BaseOptions with { Defects = [new SensorDefect(-1, 0)] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mono16SceneRenderer.Render(scene, Layout(5, 5),
            BaseOptions with { Defects = [new SensorDefect(0, 5)] }));
    }

    [TestMethod]
    public async Task Render_ExercisesShotAndDarkNoisePoissonRegimesDeterministically()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(3, 3, 2).ConfigureAwait(false);
        var zeroMean = BaseOptions with { ShotNoiseEnabled = true, DarkNoiseEnabled = true };
        var smallMean = zeroMean with { BackgroundElectronsPerSecond = 4, DarkCurrentElectronsPerSecond = 3, Seed = 7 };
        var largeMean = zeroMean with { BackgroundElectronsPerSecond = 100, DarkCurrentElectronsPerSecond = 50, Seed = 7 };

        var zero = Mono16SceneRenderer.Render(scene, Layout(3, 3), zeroMean);
        var small = Mono16SceneRenderer.Render(scene, Layout(3, 3), smallMean);
        var large = Mono16SceneRenderer.Render(scene, Layout(3, 3), largeMean);
        var repeated = Mono16SceneRenderer.Render(scene, Layout(3, 3), largeMean);

        Assert.AreEqual((ushort)0, Read(zero.Pixels, 3, 1, 1));
        Assert.IsTrue(Read(small.Pixels, 3, 1, 1) > 0);
        Assert.IsTrue(Read(large.Pixels, 3, 1, 1) > Read(small.Pixels, 3, 1, 1));
        CollectionAssert.AreEqual(large.Pixels.ToArray(), repeated.Pixels.ToArray());
    }

    [TestMethod]
    public async Task Render_TracksLowClippingAndEmptyMaskStatistics()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(3, 3, 1).ConfigureAwait(false);
        var clipped = Mono16SceneRenderer.Render(scene, Layout(3, 3), BaseOptions with
        {
            Defects = [new SensorDefect(1, 1, FixedValue: -2)]
        });
        var emptyScene = await SceneTestFactory.CreateEmptyAsync(2, 1, 0.1).ConfigureAwait(false);
        var empty = Mono16SceneRenderer.Render(emptyScene, Layout(2, 1), BaseOptions);

        Assert.AreEqual(1, clipped.Statistics.ClippedLow);
        Assert.AreEqual((ushort)0, Read(clipped.Pixels, 3, 1, 1));
        Assert.AreEqual(0, empty.Statistics.ActivePixelCount);
        Assert.AreEqual(0, empty.Statistics.Minimum);
        Assert.AreEqual(0, empty.Statistics.Maximum);
        Assert.AreEqual(0, empty.Statistics.Mean);
    }

    [TestMethod]
    public async Task Render_RejectsMalformedProjectedObjectDataAndHandlesZeroFluxOutsideMask()
    {
        var source = await SceneTestFactory.CreateCenteredAsync(5, 5).ConfigureAwait(false);
        var badPixel = ReplaceObject(source, source.Objects[0] with { Pixel = new PixelPoint(double.NaN, 2) });
        var overflowingFlux = ReplaceObject(source, source.Objects[0] with { Magnitude = -double.MaxValue });
        var zeroFlux = ReplaceObject(source, source.Objects[0] with { Magnitude = double.MaxValue, Pixel = new PixelPoint(-10, -10) });

        Assert.Throws<ArgumentException>(() => Mono16SceneRenderer.Render(badPixel, Layout(5, 5), BaseOptions));
        Assert.Throws<ArgumentException>(() => Mono16SceneRenderer.Render(overflowingFlux, Layout(5, 5), BaseOptions));

        var result = Mono16SceneRenderer.Render(zeroFlux, Layout(5, 5), BaseOptions);
        Assert.AreEqual(0, result.Objects[0].DepositedSignal);
        Assert.AreEqual(0, result.Objects[0].RetainedEnergyFraction);
        Assert.AreEqual(result.Objects[0].SourcePixel, result.Objects[0].DepositedCentroid);
    }

    [TestMethod]
    public async Task Render_CenteredPsfIsSymmetricBoundedAndConservesEnergy()
    {
        var scene = await SceneTestFactory.CreateCenteredAsync(21, 21).ConfigureAwait(false);
        var options = BaseOptions with { MagnitudeZeroElectronsPerSecond = 20_000, PsfSigmaPixels = 1, PsfRadiusPixels = 4 };

        var result = Mono16SceneRenderer.Render(scene, Layout(21, 21), options);
        var center = 10;

        Assert.AreEqual(Read(result.Pixels, 21, center - 2, center), Read(result.Pixels, 21, center + 2, center));
        Assert.AreEqual(Read(result.Pixels, 21, center, center - 2), Read(result.Pixels, 21, center, center + 2));
        Assert.AreEqual(1, result.Objects[0].RetainedEnergyFraction, 0.005);
        Assert.IsTrue(Read(result.Pixels, 21, center + 5, center) == 0);
        Assert.AreEqual(10.5, result.Objects[0].DepositedCentroid.X, 1e-9);
        Assert.AreEqual(10.5, result.Objects[0].DepositedCentroid.Y, 1e-9);
    }

    [TestMethod]
    public async Task Render_EdgePsfLosesEnergyWithoutRenormalization()
    {
        var centered = await SceneTestFactory.CreateCenteredAsync(21, 21).ConfigureAwait(false);
        var edge = await SceneTestFactory.CreateCenteredAsync(21, 21, principalX: 0, principalY: 10, radius: 10).ConfigureAwait(false);

        var centerResult = Mono16SceneRenderer.Render(centered, Layout(21, 21), BaseOptions);
        var edgeResult = Mono16SceneRenderer.Render(edge, Layout(21, 21), BaseOptions);

        Assert.IsTrue(edgeResult.Objects[0].RetainedEnergyFraction < centerResult.Objects[0].RetainedEnergyFraction);
        Assert.IsTrue(edgeResult.Objects[0].RetainedEnergyFraction < 0.8);
    }

    [TestMethod]
    public async Task Render_EnforcesCircleAndRadialVignetting()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(9, 9, 4).ConfigureAwait(false);
        var options = BaseOptions with { BackgroundElectronsPerSecond = 1000, VignettingStrength = 1 };

        var result = Mono16SceneRenderer.Render(scene, Layout(9, 9), options);

        Assert.AreEqual((ushort)1000, Read(result.Pixels, 9, 4, 4));
        Assert.AreEqual((ushort)0, Read(result.Pixels, 9, 0, 0));
        Assert.AreEqual((ushort)0, Read(result.Pixels, 9, 4, 0));
        Assert.IsTrue(Read(result.Pixels, 9, 4, 1) < Read(result.Pixels, 9, 4, 2));
    }

    [TestMethod]
    public async Task Render_QuantizesLittleEndianClipsAndHandlesZeroExposure()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(3, 3, 2).ConfigureAwait(false);
        var maximum = Mono16SceneRenderer.Render(scene, Layout(3, 3), BaseOptions with
        {
            BackgroundElectronsPerSecond = 100_000,
            ExposureSeconds = 1
        });
        var zero = Mono16SceneRenderer.Render(scene, Layout(3, 3), BaseOptions with
        {
            BackgroundElectronsPerSecond = 100_000,
            ExposureSeconds = 0
        });

        Assert.AreEqual((byte)255, maximum.Pixels.Span[8]);
        Assert.AreEqual((byte)255, maximum.Pixels.Span[9]);
        Assert.AreEqual((ushort)0, Read(zero.Pixels, 3, 1, 1));
        Assert.IsTrue(maximum.Statistics.ClippedHigh > 0);
    }

    [TestMethod]
    public async Task Render_GainAndSeededNoiseAreDeterministic()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(9, 9, 4).ConfigureAwait(false);
        var noisy = BaseOptions with { BackgroundElectronsPerSecond = 100, Gain = 2, ReadNoiseStandardDeviation = 3, Seed = 42 };
        var first = Mono16SceneRenderer.Render(scene, Layout(9, 9), noisy);
        var second = Mono16SceneRenderer.Render(scene, Layout(9, 9), noisy);
        var changedSeed = Mono16SceneRenderer.Render(scene, Layout(9, 9), noisy with { Seed = 43 });
        var lowGain = Mono16SceneRenderer.Render(scene, Layout(9, 9), noisy with { Gain = 1, ReadNoiseStandardDeviation = 0 });

        CollectionAssert.AreEqual(first.Pixels.ToArray(), second.Pixels.ToArray());
        CollectionAssert.AreNotEqual(first.Pixels.ToArray(), changedSeed.Pixels.ToArray());
        Assert.IsTrue(Read(first.Pixels, 9, 4, 4) > Read(lowGain.Pixels, 9, 4, 4));
    }

    [TestMethod]
    public void Asi174MmSensorModel_UsesDocumentedGainAndReadNoiseResponse()
    {
        var gainZero = Asi174MmSensorModel.Resolve(0);
        var gainTwoHundred = Asi174MmSensorModel.Resolve(200);

        Assert.AreEqual(7.9, gainZero.ElectronsPerAdu, 1e-12);
        Assert.AreEqual(0.79, gainTwoHundred.ElectronsPerAdu, 1e-12);
        Assert.AreEqual(5.9, gainZero.ReadNoiseElectrons, 1e-12);
        Assert.AreEqual(3.8, gainTwoHundred.ReadNoiseElectrons, 1e-12);
        Assert.Throws<ArgumentOutOfRangeException>(() => Asi174MmSensorModel.Resolve(401));
    }

    [TestMethod]
    public async Task Render_PhysicalResponseScalesExposureAndClipsAtNativeAdcMaximum()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(3, 3, 2).ConfigureAwait(false);
        var response = new MonoSensorResponse
        {
            AdcBitDepth = 12,
            FullWellElectrons = 32_400,
            ElectronsPerAdu = 1,
            ReadNoiseElectrons = 0,
            BlackLevelAdu = 10
        };
        var oneSecond = Mono16SceneRenderer.Render(scene, Layout(3, 3), BaseOptions with
        {
            BackgroundElectronsPerSecond = 100,
            SensorResponse = response
        });
        var twoSeconds = Mono16SceneRenderer.Render(scene, Layout(3, 3), BaseOptions with
        {
            BackgroundElectronsPerSecond = 100,
            ExposureSeconds = 2,
            Gain = 200,
            SensorResponse = response
        });
        var saturated = Mono16SceneRenderer.Render(scene, Layout(3, 3), BaseOptions with
        {
            BackgroundElectronsPerSecond = 100_000,
            SensorResponse = response
        });

        Assert.AreEqual((ushort)110, Read(oneSecond.Pixels, 3, 1, 1));
        Assert.AreEqual((ushort)210, Read(twoSeconds.Pixels, 3, 1, 1));
        Assert.AreEqual((ushort)4095, Read(saturated.Pixels, 3, 1, 1));
        Assert.AreEqual(Mono16SceneRenderer.ElectronDomainAlgorithmVersion, oneSecond.AlgorithmVersion);
    }

    [TestMethod]
    public async Task FocusedRenderTiming()
    {
        var scene = await SceneTestFactory.CreateCenteredAsync(512, 512).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        _ = Mono16SceneRenderer.Render(scene, Layout(512, 512), BaseOptions);
        stopwatch.Stop();
        TestContext.WriteLine($"Mono16 512x512 one-star render: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
    }

    public TestContext TestContext { get; set; } = null!;

    private static Mono16SceneRenderOptions BaseOptions => new()
    {
        ExposureSeconds = 1,
        Gain = 1,
        MagnitudeZeroElectronsPerSecond = 1000,
        PsfSigmaPixels = 1,
        PsfRadiusPixels = 4
    };

    private static ImageLayout Layout(int width, int height) => new(width, height, CameraPixelFormat.Mono16, width * 2);
    private static ushort Read(ReadOnlyMemory<byte> pixels, int width, int x, int y)
        => BitConverter.ToUInt16(pixels.Span.Slice((y * width + x) * 2, 2));

    private static VisibleScene ReplaceObject(VisibleScene scene, ProjectedCelestialObject replacement)
    {
        var constructor = typeof(VisibleScene).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(VisibleSceneRequest), typeof(IEnumerable<ProjectedCelestialObject>), typeof(IEnumerable<ProjectedConstellationSegment>)],
            modifiers: null)!;
        return (VisibleScene)constructor.Invoke([scene.Request, new[] { replacement }, scene.Segments]);
    }
}
