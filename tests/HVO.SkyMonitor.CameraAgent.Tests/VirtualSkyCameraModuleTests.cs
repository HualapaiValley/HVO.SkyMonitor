using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class VirtualSkyCameraModuleTests
{
    [TestMethod]
    public void AddCameraAgentInfrastructure_RegistersConstellationTopologyByInterface()
    {
        var services = new ServiceCollection();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var topology = provider.GetRequiredService<IConstellationTopology>();

        StringAssert.Contains(topology.Metadata.Name, "D3-Celestial", StringComparison.Ordinal);
        Assert.IsNotEmpty(topology.GetSegments("ORI"));
    }

    [TestMethod]
    public void CameraAgentSample_UsesCanonicalReducedAsi174Fixture()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json");
        Assert.IsTrue(File.Exists(samplePath), $"Missing sample configuration at {samplePath}.");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(samplePath));
        var rig = document.RootElement.GetProperty("rig");
        var sensor = rig.GetProperty("sensor");
        var optics = rig.GetProperty("optics");

        Assert.AreEqual(484, sensor.GetProperty("widthPixels").GetInt32());
        Assert.AreEqual(304, sensor.GetProperty("heightPixels").GetInt32());
        Assert.AreEqual("Mono16", sensor.GetProperty("pixelFormat").GetString());
        Assert.AreEqual(242d, optics.GetProperty("principalPointX").GetDouble());
        Assert.AreEqual(152d, optics.GetProperty("principalPointY").GetDouble());
        Assert.AreEqual(148.96, optics.GetProperty("imageCircleRadiusPixels").GetDouble(), 1e-12);
        Assert.IsFalse(optics.GetProperty("horizontalFlip").GetBoolean());
    }
    private static readonly DateTimeOffset FixtureUtc = DateTimeOffset.Parse(
        "2025-01-15T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string[] ExpectedTestConstellationIds = ["TST"];

    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task CaptureAsyncWithMono16ProfileProducesDeterministicFrame()
    {
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still);

        var first = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var second = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraPixelFormat.Mono16, first.Frame!.PixelFormat);
        Assert.AreEqual(484 * 304 * 2, first.Frame.PixelData.Length);
        Assert.AreEqual(484 * 2, first.Frame.StrideBytes);
        Assert.IsNotNull(first.Frame.Metadata.Scene);
        Assert.AreEqual("https://astronexus.com/projects/hyg", first.Frame.Metadata.Scene.CatalogSourceUrl!.AbsoluteUri);
        Assert.AreEqual("CC BY-SA 4.0", first.Frame.Metadata.Scene.CatalogLicense);
        Assert.HasCount(1, first.Frame.Metadata.Scene.Objects!);
        CollectionAssert.AreEqual(first.Frame.PixelData.ToArray(), second.Frame!.PixelData.ToArray());
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(first.Frame.PixelData.Span)),
            Convert.ToHexString(SHA256.HashData(second.Frame.PixelData.Span)));
        var checksum = Convert.ToHexString(SHA256.HashData(first.Frame.PixelData.Span));
        TestContext.WriteLine($"Reduced SHA-256: {checksum}");
        Assert.AreEqual("5B77FD453893CC419FF5FFDA0D392329B5DD6557666B1B3D4B91AAD981FEB43F", checksum);
    }

    [TestMethod]
    public async Task CameraModuleFactoryCreatesConfiguredVirtualSkyModule()
    {
        var catalog = new InMemoryCelestialCatalog([]);
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ICelestialCatalog>(catalog)
            .AddSingleton<IProjectedSceneStore, ProjectedSceneStore>()
            .BuildServiceProvider();
        var factory = new CameraModuleFactory(services,
            [new CameraModuleRegistration("VirtualSky", typeof(VirtualSkyCameraModule))],
            NullLogger<CameraModuleFactory>.Instance);

        var module = factory.Create("VirtualSky");
        try
        {
            Assert.IsInstanceOfType<VirtualSkyCameraModule>(module);
        }
        finally
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CaptureAsyncWithRgb24ProfileProducesRgbCompatibilityFrame()
    {
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(CreateConfig(CameraPixelFormat.Rgb24), CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraPixelFormat.Rgb24, result.Frame!.PixelFormat);
        Assert.AreEqual(484 * 304 * 3, result.Frame.PixelData.Length);
        StringAssert.Contains(result.Frame.Metadata.Extra!["compatibilityLabel"], "non-Bayer", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task CaptureAsyncWithAsi178ProfileProducesRggbRaw16Frame()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            "{\"magnitudeZeroElectronsPerSecond\":18000,\"asi178Sensor\":{\"enabled\":true}}");
        var config = CreateConfig(CameraPixelFormat.BayerRggb16, 774, 520) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", document.RootElement.Clone())
        };
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null)),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraPixelFormat.BayerRggb16, result.Frame!.PixelFormat);
        Assert.AreEqual(774 * 520 * 2, result.Frame.PixelData.Length);
        Assert.AreEqual("RGGB", result.Frame.Metadata.Extra!["cfaPattern"]);
        Assert.AreEqual(Asi178McSensorModel.Version, result.Frame.Metadata.Extra["sensorModel"]);
        Assert.AreEqual("14", result.Frame.Metadata.Extra["sensorAdcBitDepth"]);
    }

    [TestMethod]
    public async Task CaptureAsyncWhenTimeAdvancesMovesStarGeometry()
    {
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None).ConfigureAwait(false);
        var first = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);
        var later = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc.AddHours(1), TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreNotEqual(first.Frame!.PixelData.ToArray(), later.Frame!.PixelData.ToArray());
    }

    [TestMethod]
    public async Task CaptureAsyncWithFullAsi174ProfileProducesCanonicalMono16Frame()
    {
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(CreateConfig(CameraPixelFormat.Mono16, 1936, 1216), CancellationToken.None).ConfigureAwait(false);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);
        started.Stop();
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;

        Assert.AreEqual(1936, result.Frame!.Width);
        Assert.AreEqual(1216, result.Frame.Height);
        Assert.AreEqual(1936 * 1216 * 2, result.Frame.PixelData.Length);
        Assert.AreEqual(1936 * 2, result.Frame.StrideBytes);
        var checksum = Convert.ToHexString(SHA256.HashData(result.Frame.PixelData.Span));
        TestContext.WriteLine($"Full SHA-256: {checksum}");
        Assert.AreEqual("64BDF671DF9B16A56B32E2A7DCF73295C4D91281C4C39EC2A5888DFB53051190", checksum);
        TestContext.WriteLine($"Full render elapsed: {started.Elapsed.TotalMilliseconds:F2} ms");
        TestContext.WriteLine($"Full render allocated: {allocated} bytes");
    }

    [TestMethod]
    public async Task CaptureAsyncWithRequestedSetpointChangesStatisticsNotObjectSelection()
    {
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None).ConfigureAwait(false);
        var low = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        var high = new CaptureSetpoint(TimeSpan.FromSeconds(2), 3, null, null);
        var lowResult = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still, low), CancellationToken.None).ConfigureAwait(false);
        var highResult = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still, high), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(high, highResult.NextSetpoint);
        Assert.AreNotEqual(lowResult.Frame!.Metadata.Extra!["renderMean"], highResult.Frame!.Metadata.Extra!["renderMean"]);
        Assert.AreEqual(lowResult.Frame.Metadata.Extra["visibleObjectCount"], highResult.Frame.Metadata.Extra["visibleObjectCount"]);
    }

    [TestMethod]
    public async Task CaptureAsyncWithAsi174ModelProducesIndependentReproducibleNoiseSequence()
    {
        const string optionsJson =
            "{\"magnitudeZeroElectronsPerSecond\":300,\"bortleClass\":3," +
            "\"asi174Sensor\":{\"enabled\":true,\"blackLevelAdu\":64}}";
        using var document = System.Text.Json.JsonDocument.Parse(optionsJson);
        var config = CreateConfig() with
        {
            Module = new CameraModuleDescriptor("VirtualSky", document.RootElement.Clone())
        };
        var firstModule = CreateModule(FixtureUtc);
        var repeatedModule = CreateModule(FixtureUtc);
        await firstModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await repeatedModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(
            FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null));

        var first = await firstModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var second = await firstModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var repeatedFirst = await repeatedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var repeatedSecond = await repeatedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreNotEqual(first.Frame!.PixelData.ToArray(), second.Frame!.PixelData.ToArray());
        CollectionAssert.AreEqual(first.Frame.PixelData.ToArray(), repeatedFirst.Frame!.PixelData.ToArray());
        CollectionAssert.AreEqual(second.Frame.PixelData.ToArray(), repeatedSecond.Frame!.PixelData.ToArray());
        Assert.AreEqual(Asi174MmSensorModel.Version, first.Frame.Metadata.Extra!["sensorModel"]);
        Assert.AreEqual("12", first.Frame.Metadata.Extra["adcBitDepth"]);
        Assert.AreEqual("ZWO 0.1 dB", first.Frame.Metadata.Extra["gainUnits"]);
        Assert.IsTrue(MaximumSample(first.Frame.PixelData.Span) <= 4095);
    }

    [TestMethod]
    public async Task CaptureAsync_BortleClassControlsLinearSkyBackground()
    {
        using var darkOptions = System.Text.Json.JsonDocument.Parse(
            "{\"bortleClass\":3,\"bortleThreeBackgroundElectronsPerSecond\":2}");
        using var brightOptions = System.Text.Json.JsonDocument.Parse(
            "{\"bortleClass\":7,\"bortleThreeBackgroundElectronsPerSecond\":2}");
        var darkModule = CreateModule(FixtureUtc);
        var brightModule = CreateModule(FixtureUtc);
        await darkModule.InitializeAsync(
            CreateConfig() with { Module = new CameraModuleDescriptor("VirtualSky", darkOptions.RootElement.Clone()) },
            CancellationToken.None).ConfigureAwait(false);
        await brightModule.InitializeAsync(
            CreateConfig() with { Module = new CameraModuleDescriptor("VirtualSky", brightOptions.RootElement.Clone()) },
            CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(
            FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(20), 1, null, null));

        var dark = await darkModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var bright = await brightModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

        var darkMean = double.Parse(dark.Frame!.Metadata.Extra!["renderMean"], System.Globalization.CultureInfo.InvariantCulture);
        var brightMean = double.Parse(bright.Frame!.Metadata.Extra!["renderMean"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsGreaterThan(darkMean, brightMean);
    }

    [TestMethod]
    public async Task InitializeAsyncWithInvalidMaximumResultsRejectsConfiguration()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"maximumResults\":2001}");
        var config = CreateConfig() with { Module = new CameraModuleDescriptor("VirtualSky", document.RootElement.Clone()) };
        var module = CreateModule(FixtureUtc);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => module.InitializeAsync(config, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InitializeAsyncRejectsUnsupportedOpticsSensorModeAndRenderOptions()
    {
        var module = CreateModule(FixtureUtc);
        var config = CreateConfig();
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => module.InitializeAsync(
            config with { Rig = config.Rig with { Optics = config.Rig.Optics with { FieldOfViewDegrees = 360 } } },
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => module.InitializeAsync(
            config with { Rig = config.Rig with { Optics = config.Rig.Optics with { RollDegrees = 1 } } },
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => module.InitializeAsync(
            config with { Rig = config.Rig with { Sensor = config.Rig.Sensor with { ResponseMode = SensorResponseMode.RenderedRgb } } },
            CancellationToken.None)).ConfigureAwait(false);

        using var document = System.Text.Json.JsonDocument.Parse("{\"psfSigmaPixels\":0}");
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => module.InitializeAsync(
            config with { Module = new CameraModuleDescriptor("VirtualSky", document.RootElement.Clone()) },
            CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("EquidistantFisheye", LensKind.Fisheye, 180d, CameraPixelFormat.Mono16)]
    [DataRow("EquidistantFisheye", LensKind.Fisheye, 180d, CameraPixelFormat.Rgb24)]
    [DataRow("EquisolidFisheye", LensKind.Fisheye, 180d, CameraPixelFormat.Mono16)]
    [DataRow("EquisolidFisheye", LensKind.Fisheye, 180d, CameraPixelFormat.Rgb24)]
    [DataRow("OrthographicFisheye", LensKind.Fisheye, 180d, CameraPixelFormat.Mono16)]
    [DataRow("OrthographicFisheye", LensKind.Fisheye, 180d, CameraPixelFormat.Rgb24)]
    [DataRow("StereographicFisheye", LensKind.Fisheye, 180d, CameraPixelFormat.Mono16)]
    [DataRow("StereographicFisheye", LensKind.Fisheye, 180d, CameraPixelFormat.Rgb24)]
    [DataRow("Perspective", LensKind.Rectilinear, 90d, CameraPixelFormat.Mono16)]
    [DataRow("Perspective", LensKind.Rectilinear, 90d, CameraPixelFormat.Rgb24)]
    [DataRow("Perspective", LensKind.Telescope, 10d, CameraPixelFormat.Mono16)]
    [DataRow("Perspective", LensKind.Telescope, 10d, CameraPixelFormat.Rgb24)]
    public async Task ReducedCompatibilityMatrixCapturesThroughSharedPipeline(
        string model, LensKind lensKind, double fieldOfView, CameraPixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(model);
        var config = CreateConfig(format);
        config = config with
        {
            Rig = config.Rig with
            {
                Optics = config.Rig.Optics with
                {
                    ProjectionModel = model,
                    LensKind = lensKind,
                    FieldOfViewDegrees = fieldOfView,
                    ImageCircleRadiusPixels = lensKind == LensKind.Fisheye ? 148.96 : null,
                    CalibrationVersion = $"{model}-v1"
                }
            }
        };
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        var bytesPerPixel = format == CameraPixelFormat.Mono16 ? 2 : 3;
        Assert.AreEqual(484 * 304 * bytesPerPixel, result.Frame!.PixelData.Length);
        Assert.AreEqual(model, result.Frame.Metadata.Scene!.ProjectionModel);
        if (lensKind is LensKind.Rectilinear or LensKind.Telescope)
        {
            var cornerSignal = format == CameraPixelFormat.Mono16
                ? BitConverter.ToUInt16(result.Frame.PixelData.Span[..2])
                : result.Frame.PixelData.Span[0] + result.Frame.PixelData.Span[1] + result.Frame.PixelData.Span[2];
            Assert.IsTrue(cornerSignal > 0,
                "Perspective profiles must render the rectangular sensor corners.");
        }
    }

    [TestMethod]
    [DataRow("EquisolidFisheye", LensKind.Fisheye, 180d, CameraPixelFormat.Mono16)]
    [DataRow("Perspective", LensKind.Rectilinear, 90d, CameraPixelFormat.Mono16)]
    [DataRow("Perspective", LensKind.Rectilinear, 90d, CameraPixelFormat.Rgb24)]
    [DataRow("Perspective", LensKind.Telescope, 10d, CameraPixelFormat.Mono16)]
    [DataRow("Perspective", LensKind.Telescope, 10d, CameraPixelFormat.Rgb24)]
    public async Task SelectedFullResolutionCompatibilityProfilesCapture(
        string model, LensKind lensKind, double fieldOfView, CameraPixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(model);
        var config = CreateConfig(format, 1936, 1216);
        config = config with
        {
            Rig = config.Rig with
            {
                Optics = config.Rig.Optics with
                {
                    ProjectionModel = model,
                    LensKind = lensKind,
                    FieldOfViewDegrees = fieldOfView,
                    ImageCircleRadiusPixels = lensKind == LensKind.Fisheye ? 595.84 : null,
                    CalibrationVersion = $"{model}-full-v1"
                }
            }
        };
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1936, result.Frame!.Width);
        Assert.AreEqual(1216, result.Frame.Height);
        Assert.AreEqual(1936 * 1216 * (format == CameraPixelFormat.Mono16 ? 2 : 3), result.Frame.PixelData.Length);
    }

    [TestMethod]
    public async Task FisheyeCalibratedFocalLengthControlsScaleAndRejectsAnisotropy()
    {
        var baseConfig = CreateConfig() with
        {
            Rig = CreateConfig().Rig with { Orientation = new RigOrientation(80, 0, 0) }
        };
        var narrow = baseConfig with
        {
            Rig = baseConfig.Rig with
            {
                Optics = baseConfig.Rig.Optics with { FocalLengthXPixels = 100, FocalLengthYPixels = 100 }
            }
        };
        var wide = narrow with
        {
            Rig = narrow.Rig with
            {
                Optics = narrow.Rig.Optics with { FocalLengthXPixels = 200, FocalLengthYPixels = 200 }
            }
        };
        var narrowModule = CreateModule(FixtureUtc);
        var wideModule = CreateModule(FixtureUtc);
        await narrowModule.InitializeAsync(narrow, CancellationToken.None).ConfigureAwait(false);
        await wideModule.InitializeAsync(wide, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still);

        var narrowResult = await narrowModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var wideResult = await wideModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var centerX = narrow.Rig.Sensor.WidthPixels / 2d;
        var centerY = narrow.Rig.Sensor.HeightPixels / 2d;
        var narrowObject = narrowResult.Frame!.Metadata.Scene!.Objects![0];
        var wideObject = wideResult.Frame!.Metadata.Scene!.Objects![0];
        var narrowRadius = Math.Sqrt(Math.Pow(narrowObject.PixelX - centerX, 2) + Math.Pow(narrowObject.PixelY - centerY, 2));
        var wideRadius = Math.Sqrt(Math.Pow(wideObject.PixelX - centerX, 2) + Math.Pow(wideObject.PixelY - centerY, 2));
        Assert.AreEqual(2, wideRadius / narrowRadius, 1e-9);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => narrowModule.InitializeAsync(
            narrow with
            {
                Rig = narrow.Rig with
                {
                    Optics = narrow.Rig.Optics with { FocalLengthXPixels = 100, FocalLengthYPixels = 101 }
                }
            }, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CaptureAsyncUsesValidatedCatalogMetadataForSceneProvenance()
    {
        var catalog = new ProvenanceCatalog(new CatalogMetadata(
            "validated fixture", "4.2-fixture.1", new Uri("https://example.test/hyg"),
            new string('A', 64), "CC BY-SA 4.0", "1"));
        var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore());
        await module.InitializeAsync(CreateConfig(), CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(catalog.Metadata.Name, result.Frame!.Metadata.Scene!.CatalogName);
        Assert.AreEqual(catalog.Metadata.Version, result.Frame.Metadata.Scene.CatalogVersion);
        Assert.AreEqual(catalog.Metadata.Checksum, result.Frame.Metadata.Scene.CatalogChecksumSha256);
    }

    [TestMethod]
    public async Task CaptureAsyncPersistsRequestedConstellationSegments()
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(FixtureUtc, -113.878) / 15d;
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("24378", "Rigel", rightAscension, 35.347, 0.18, HipparcosId: "24436"),
            new CelestialCatalogObject("25200", "Orion fixture", rightAscension, 30, 0.45, HipparcosId: "25281")
        ]);
        var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore(),
            StandardConstellationTopology.CreateD3Celestial());
        using var document = System.Text.Json.JsonDocument.Parse("{\"constellationIds\":[\"ORI\"]}");
        var config = CreateConfig() with
        {
            Module = new CameraModuleDescriptor("VirtualSky", document.RootElement.Clone())
        };
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsGreaterThan(0, result.Frame!.Metadata.Scene!.Segments!.Count);
        Assert.AreEqual("ORI", result.Frame.Metadata.Scene.Segments![0].ConstellationId);
        Assert.AreEqual("v0.7.32", result.Frame.Metadata.Scene.ConstellationTopologyVersion);
        Assert.AreEqual("BSD-3-Clause", result.Frame.Metadata.Scene.ConstellationTopologyLicense);
    }

    [TestMethod]
    public async Task IncludeConstellationEndpointStars_ChangesOnlyVirtualRenderObjectsNotLineGeometry()
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(FixtureUtc, -113.878) / 15d;
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("from", "From", rightAscension, 35.347, 1, HipparcosId: "1"),
            new CelestialCatalogObject("faint", "Faint", rightAscension, 30, 7, HipparcosId: "2")
        ]);
        var topology = new InMemoryConstellationTopology([new ConstellationSegment("TST", "1", "2")]);

        async Task<CaptureResult> CaptureAsync(bool includeEndpoints)
        {
            var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore(), topology);
            using var document = System.Text.Json.JsonDocument.Parse($$"""
                {
                  "maximumMagnitude": 6.5,
                  "maximumResults": 10,
                  "magnitudeZeroElectronsPerSecond": 1000000,
                  "constellationIds": ["TST"],
                  "includeConstellationEndpointStars": {{System.Text.Json.JsonSerializer.Serialize(includeEndpoints)}},
                  "shotNoiseEnabled": false,
                  "readNoiseStandardDeviation": 0
                }
                """);
            var config = CreateConfig() with
            {
                Module = new CameraModuleDescriptor("VirtualSky", document.RootElement.Clone())
            };
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            return await module.CaptureAsync(
                new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
                CancellationToken.None).ConfigureAwait(false);
        }

        var linesOnly = await CaptureAsync(false).ConfigureAwait(false);
        var withEndpoints = await CaptureAsync(true).ConfigureAwait(false);

        Assert.HasCount(1, linesOnly.Frame!.Metadata.Scene!.Objects!);
        Assert.HasCount(2, withEndpoints.Frame!.Metadata.Scene!.Objects!);
        Assert.IsFalse(linesOnly.Frame.Metadata.Scene.IncludeConstellationEndpointStars);
        Assert.IsTrue(withEndpoints.Frame.Metadata.Scene.IncludeConstellationEndpointStars);
        CollectionAssert.AreEqual(ExpectedTestConstellationIds, withEndpoints.Frame.Metadata.Scene.ConstellationIds!.ToArray());
        CollectionAssert.AreEqual(
            linesOnly.Frame.Metadata.Scene.Segments!.Select(item =>
                (item.FromPixelX, item.FromPixelY, item.ToPixelX, item.ToPixelY)).ToArray(),
            withEndpoints.Frame.Metadata.Scene.Segments!.Select(item =>
                (item.FromPixelX, item.FromPixelY, item.ToPixelX, item.ToPixelY)).ToArray());
        Assert.IsFalse(linesOnly.Frame.PixelData.Span.SequenceEqual(withEndpoints.Frame.PixelData.Span));
    }

    [TestMethod]
    public async Task CaptureAsyncPersistsRequestedPlanetAndEphemerisVersion()
    {
        var position = new SolarSystemPosition(new EquatorialPoint(2.530301, 89.264109), -2.5);
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = position
        }, "module-planets-v1");
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            new InMemoryCelestialCatalog([]),
            new ProjectedSceneStore(),
            planetEphemeris: ephemeris);
        using var document = System.Text.Json.JsonDocument.Parse("{\"solarSystemBodies\":[\"Jupiter\"]}");
        var config = CreateConfig() with
        {
            Module = new CameraModuleDescriptor("VirtualSky", document.RootElement.Clone())
        };
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        var provenance = result.Frame!.Metadata.Scene!;
        Assert.AreEqual("module-planets-v1", provenance.EphemerisModelVersion);
        Assert.AreEqual("solar-system:Jupiter", provenance.Objects!.Single().Id);
    }

    private static VirtualSkyCameraModule CreateModule(DateTimeOffset utc)
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(utc, -113.878) / 15d;
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("fixture-star", "Fixture Star", rightAscension, 35.347, 0, 0.65)
        ]);
        return new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore());
    }

    private static CameraModuleConfig CreateConfig(
        CameraPixelFormat format = CameraPixelFormat.Mono16, int width = 484, int height = 304) => new(
        new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"), new CameraModuleDescriptor("VirtualSky"),
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
                "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                width / 2d, height / 2d, 0.98 * Math.Min(width, height) / 2d,
                CalibrationVersion: "virtual-fisheye-180-equidistant-v1"),
             new RigOrientation(90, 0, 0),
              new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)));

    private static ushort MaximumSample(ReadOnlySpan<byte> pixels)
    {
        ushort maximum = 0;
        for (var offset = 0; offset < pixels.Length; offset += 2)
        {
            maximum = Math.Max(maximum, (ushort)(pixels[offset] | pixels[offset + 1] << 8));
        }
        return maximum;
    }

    private sealed class ProvenanceCatalog(CatalogMetadata metadata) : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        public CatalogMetadata Metadata { get; } = metadata;

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => [];

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<CelestialCatalogObject>>([]);

    }
}
