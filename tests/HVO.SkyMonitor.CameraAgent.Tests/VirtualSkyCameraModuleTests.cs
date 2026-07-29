using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualSkyCameraModuleTests
{
    [TestMethod]
    public void ConfigurationPreflight_RejectsUnmappedVirtualSkyOptions()
    {
        var module = CreateModule(FixtureUtc);
        var preflight = (ICameraModuleConfigurationPreflight)module;
        var config = CreateConfig() with
        {
            Module = new CameraModuleDescriptor(
                "VirtualSky",
                JsonSerializer.SerializeToElement(new { unsupportedOption = true }))
        };

        _ = Assert.Throws<JsonException>(() => preflight.ValidateConfiguration(config));
    }

    [TestMethod]
    public void AddCameraAgentInfrastructure_RegistersConstellationTopologyByInterface()
    {
        var services = new ServiceCollection();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var topology = provider.GetRequiredService<IConstellationTopology>();
        var optionalAnnotationProvider = provider.GetRequiredService<IAnnotationSceneProvider>();

        StringAssert.Contains(topology.Metadata.Name, "D3-Celestial", StringComparison.Ordinal);
        Assert.IsNotEmpty(topology.GetSegments("ORI"));
        Assert.IsNotNull(optionalAnnotationProvider);
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

    [TestMethod]
    public async Task StandaloneProductionSmokeProfilePinsFullResolutionLocalGraphAsync()
    {
        const string fileName = "cameraagent.standalone-production-smoke.json";
        var config = await LoadProfileAsync(fileName).ConfigureAwait(false);
        var sensor = config.Rig.Sensor;
        var pipeline = config.Rig.Pipeline;
        var envelope = pipeline.Envelope;

        Assert.AreEqual(1936, sensor.WidthPixels);
        Assert.AreEqual(1216, sensor.HeightPixels);
        Assert.AreEqual(CameraPixelFormat.Mono16, sensor.PixelFormat);
        Assert.AreEqual(SensorResponseMode.Monochrome, sensor.ResponseMode);
        Assert.AreEqual(3872, sensor.StrideBytes);
        Assert.AreEqual(4_708_352, sensor.StrideBytes * sensor.HeightPixels);
        Assert.AreEqual(TimeSpan.FromSeconds(5), pipeline.CaptureInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(5), pipeline.DayExposure);
        Assert.AreEqual(TimeSpan.FromSeconds(5), pipeline.NightExposure);
        Assert.AreEqual(CaptureCadenceMode.MinimumStartInterval, pipeline.CadenceMode);
        Assert.AreEqual(1d, pipeline.DayGain);
        Assert.AreEqual(1d, pipeline.NightGain);
        Assert.IsNotNull(envelope);
        Assert.AreEqual(TimeSpan.FromSeconds(5), envelope.DayDefaults.Exposure);
        Assert.AreEqual(TimeSpan.FromSeconds(5), envelope.NightDefaults.Exposure);
        Assert.AreEqual(1d, envelope.DayDefaults.Gain);
        Assert.AreEqual(1d, envelope.NightDefaults.Gain);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var moduleModel = config.ModuleOptions!.Value.GetProperty("syntheticCalibration")
            .Deserialize<SyntheticCalibrationModelV1>(jsonOptions);
        Assert.AreEqual(
            new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero),
            config.ModuleOptions.Value.GetProperty("fixedSceneUtc").GetDateTimeOffset());
        var calibrationConfig = config.ResolveProcessingSteps()
            .Single(static step => step.Id == "Calibration");
        var calibrationOptions = calibrationConfig.Options!.Value
            .Deserialize<CalibrationProcessingStepOptions>(jsonOptions);
        Assert.IsNotNull(moduleModel);
        Assert.IsNotNull(calibrationOptions?.SyntheticCalibration);
        moduleModel.Validate(sensor.WidthPixels, sensor.HeightPixels);
        calibrationOptions.SyntheticCalibration.Validate(sensor.WidthPixels, sensor.HeightPixels);
        Assert.AreEqual(
            SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(moduleModel),
            SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(calibrationOptions.SyntheticCalibration));
        Assert.IsFalse(config.ModuleOptions.Value.GetProperty("asi174Sensor").GetProperty("enabled").GetBoolean());

        var steps = config.ResolveProcessingSteps();
        CollectionAssert.AreEqual(
            ExpectedStandaloneGraph,
            steps.OrderBy(static step => step.Order).Select(static step => step.Id).ToArray());
        Assert.IsFalse(steps.Any(static step =>
            step.Id is "Upload" or "ArchiveStorage" || step.Type.Contains("Upload", StringComparison.Ordinal)));
        var storage = steps.Single(static step => step.Id == "LocalStorage");
        Assert.AreEqual(false, storage.Options!.Value.GetProperty("queueForUpload").GetBoolean());
        Assert.AreEqual(7, storage.Options.Value.GetProperty("retentionDays").GetInt32());

        var deployment = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.StandaloneProductionSmoke.json")
            .Build();
        Assert.AreEqual(fileName, deployment["CameraAgent:ConfigFilePath"]);
        Assert.AreEqual("Disabled", deployment["CameraAgent:CentralIntegration:Mode"]);
        Assert.IsFalse(deployment.GetValue<bool>("CameraAgent:CaptureDistribution:UploadEnabled"));
        Assert.IsFalse(deployment.GetValue<bool>("CameraAgent:EnvironmentalDelivery:Enabled"));
        Assert.AreEqual("Production", deployment["Catalog:RequiredPackageKind"]);
        Assert.AreEqual("/var/lib/hvo/data/catalog", deployment["Catalog:Root"]);
        Assert.AreEqual(
            deployment["CameraAgent:RawIngressRoot"],
            storage.Options.Value.GetProperty("storageRoot").GetString());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            })
            .Build());
        using var provider = services.BuildServiceProvider();
        var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(config);
        CollectionAssert.AreEqual(
            ExpectedStandaloneGraph,
            graph.Nodes.Select(static node => node.Id).ToArray());
        graph.DisposeSteps();
    }

    [TestMethod]
    [DataRow("virtual-asi174.full.json", CameraPixelFormat.Mono16, SensorResponseMode.Monochrome, 3872, true)]
    [DataRow("virtual-asi174mc.full.json", CameraPixelFormat.Rgb24, SensorResponseMode.RenderedRgb, 5808, false)]
    public async Task FullAsi174ProfilesLoadCanonicalGeometry(
        string fileName,
        CameraPixelFormat pixelFormat,
        SensorResponseMode responseMode,
        int strideBytes,
        bool horizontalFlip)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = path,
            AgentId = "canonical-profile-test",
            Observatory = new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix")
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);

        var config = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("canonical-profile-test", config.AgentId);
        Assert.AreEqual(35.347, config.Observatory.LatitudeDegrees, 1e-12);
        Assert.AreEqual(1936, config.Rig.Sensor.WidthPixels);
        Assert.AreEqual(1216, config.Rig.Sensor.HeightPixels);
        Assert.AreEqual(pixelFormat, config.Rig.Sensor.PixelFormat);
        Assert.AreEqual(responseMode, config.Rig.Sensor.ResponseMode);
        Assert.AreEqual(strideBytes, config.Rig.Sensor.StrideBytes);
        Assert.AreEqual(968, config.Rig.Optics.PrincipalPointX);
        Assert.AreEqual(608, config.Rig.Optics.PrincipalPointY);
        Assert.AreEqual(595.84, config.Rig.Optics.ImageCircleRadiusPixels);
        Assert.AreEqual(horizontalFlip, config.Rig.Optics.HorizontalFlip);
        Assert.AreEqual(new RigOrientation(90, 0, 0), config.Rig.Orientation);
    }

    [TestMethod]
    public async Task Asi174Mono8RoiProfileLoadsNativeAndOutputGeometrySeparately()
    {
        var config = await LoadProfileAsync("virtual-asi174-mono8-roi-bin4.json").ConfigureAwait(false);
        var resolved = SensorReadoutResolver.Resolve(config.Rig.Sensor, config.Rig.Readout!);

        Assert.AreEqual(1936, config.Rig.Sensor.WidthPixels);
        Assert.AreEqual(1216, config.Rig.Sensor.HeightPixels);
        Assert.AreEqual(CameraPixelFormat.Mono16, config.Rig.Sensor.PixelFormat);
        Assert.AreEqual(new SensorCrop(648, 368, 640, 480), config.Rig.Readout!.Roi);
        Assert.AreEqual(160, resolved.Layout.Width);
        Assert.AreEqual(120, resolved.Layout.Height);
        Assert.AreEqual(CameraPixelFormat.Mono8, resolved.Layout.PixelFormat);
        Assert.AreEqual(19_200, resolved.Layout.ByteLength);
    }

    [TestMethod]
    public async Task ConfiguredAsi174ResponseMatchesLegacyCompatibilityResolver()
    {
        var configured = await LoadProfileAsync("virtual-asi174-mono8-roi-bin4.json").ConfigureAwait(false);
        using var options = JsonDocument.Parse(
            "{\"seed\":2025,\"maximumMagnitude\":6.5,\"maximumResults\":2000," +
            "\"magnitudeZeroElectronsPerSecond\":300,\"bortleClass\":3," +
            "\"asi174Sensor\":{\"enabled\":true,\"blackLevelAdu\":64}}");
        var legacy = configured with
        {
            Module = new CameraModuleDescriptor("VirtualSky", options.RootElement.Clone()),
            Rig = configured.Rig with
            {
                Sensor = configured.Rig.Sensor with { SimulationResponse = null }
            }
        };
        var configuredModule = CreateModule(FixtureUtc);
        var legacyModule = CreateModule(FixtureUtc);
        var renamedModule = CreateModule(FixtureUtc);
        var renamed = configured with
        {
            Rig = configured.Rig with
            {
                Sensor = configured.Rig.Sensor with { Name = "CustomMonochromeSensor" }
            }
        };
        await configuredModule.InitializeAsync(configured, CancellationToken.None).ConfigureAwait(false);
        await legacyModule.InitializeAsync(legacy, CancellationToken.None).ConfigureAwait(false);
        await renamedModule.InitializeAsync(renamed, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(
            FixtureUtc,
            TimeSpan.FromSeconds(1),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(1), 150, null, null));

        var configuredResult = await configuredModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var legacyResult = await legacyModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var renamedResult = await renamedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            legacyResult.Frame!.PixelData.ToArray(),
            configuredResult.Frame!.PixelData.ToArray());
        CollectionAssert.AreEqual(
            configuredResult.Frame.PixelData.ToArray(),
            renamedResult.Frame!.PixelData.ToArray());
        Assert.AreEqual("zwo-asi174mm-12bit-v1", configuredResult.Frame.Metadata.Extra!["sensorModel"]);
    }

    [TestMethod]
    public async Task ConfiguredSensorResponseRejectsInconsistentReadoutSensorAndPipeline()
    {
        var configured = await LoadProfileAsync("virtual-asi174-mono8-roi-bin4.json").ConfigureAwait(false);
        var missingReadout = configured with { Rig = configured.Rig with { Readout = null } };
        var colorSensor = configured with
        {
            Rig = configured.Rig with
            {
                Sensor = configured.Rig.Sensor with
                {
                    ColorMode = SensorColorMode.Color,
                    PixelFormat = CameraPixelFormat.BayerRggb16,
                    ResponseMode = SensorResponseMode.BayerRaw
                }
            }
        };
        var overstatedDepth = configured with
        {
            Rig = configured.Rig with
            {
                Readout = configured.Rig.Readout! with
                {
                    Roi = new SensorCrop(0, 0, 1936, 1216),
                    BinX = 1,
                    BinY = 1,
                    BinningAlgorithm = FrameBinningAlgorithm.IdentityV1,
                    PixelFormat = CameraPixelFormat.Mono16,
                    SampleDepthBits = 16,
                    ContainerDepthBits = 16,
                    StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
                    BlackLevel = 64,
                    WhiteLevel = 4095,
                    StrideBytes = 3872
                }
            }
        };
        var invalidBlackLevel = configured with
        {
            Rig = configured.Rig with
            {
                Sensor = configured.Rig.Sensor with
                {
                    SimulationResponse = configured.Rig.Sensor.SimulationResponse! with { BlackLevelAdu = 4096 }
                }
            }
        };
        var unsupportedGain = configured with
        {
            Rig = configured.Rig with
            {
                Pipeline = configured.Rig.Pipeline with { NightGain = 401 }
            }
        };
        var missingLevels = configured with
        {
            Rig = configured.Rig with
            {
                Readout = configured.Rig.Readout! with { BlackLevel = null, WhiteLevel = null }
            }
        };

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(missingReadout, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(colorSensor, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(overstatedDepth, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(invalidBlackLevel, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(unsupportedGain, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(missingLevels, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Asi174Mono8RoiProfileNegotiatesProcessingAgainstOutputFormat()
    {
        var config = await LoadProfileAsync("virtual-asi174-mono8-roi-bin4.json").ConfigureAwait(false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            })
            .Build());
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();

        var graph = factory.CreateGraph(config);

        Assert.IsFalse(graph.Nodes.Any(node => node.RecipeName is
            BuiltInProcessingRecipes.RollingMean or BuiltInProcessingRecipes.ReferenceCalibration));
        graph.DisposeSteps();

        var incompatible = config with
        {
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig(
                    "RollingCombination",
                    "rolling",
                    25,
                    JsonSerializer.SerializeToElement(new RollingCombinationProcessingStepOptions { WindowSize = 2 }))
            ]
        };
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(incompatible));
        StringAssert.Contains(exception.Message, "rolling", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "Mono8", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task FullAsi676ProfilesLoadProvisionalSharedGeometry()
    {
        var profiles = new[]
        {
            (FileName: "virtual-asi676mm.full.json", Name: "VirtualAsi676MmRaw16",
                Format: CameraPixelFormat.Mono16, Exposure: TimeSpan.FromMilliseconds(32)),
            (FileName: "virtual-asi676mc.full.json", Name: "VirtualAsi676McRggbRaw16Provisional",
                Format: CameraPixelFormat.BayerRggb16, Exposure: TimeSpan.FromSeconds(20))
        };

        foreach (var expected in profiles)
        {
            var config = await LoadProfileAsync(expected.FileName).ConfigureAwait(false);
            var module = CreateModule(FixtureUtc);

            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(expected.Name, config.Rig.Sensor.Name);
            Assert.AreEqual(3552, config.Rig.Sensor.WidthPixels);
            Assert.AreEqual(3552, config.Rig.Sensor.HeightPixels);
            Assert.AreEqual(2.0, config.Rig.Sensor.PixelSizeMicrons, 1e-12);
            Assert.AreEqual(expected.Format, config.Rig.Sensor.PixelFormat);
            Assert.AreEqual(7104, config.Rig.Sensor.StrideBytes);
            Assert.AreEqual(2.5, config.Rig.Optics.FocalLengthMillimeters, 1e-12);
            Assert.AreEqual(170, config.Rig.Optics.FieldOfViewDegrees, 1e-12);
            Assert.AreEqual(1627.5, config.Rig.Optics.ImageCircleRadiusPixels);
            Assert.AreEqual(1097.0456, config.Rig.Optics.FocalLengthXPixels);
            Assert.AreEqual(expected.Exposure, config.Rig.Pipeline.NightExposure);
            Assert.AreEqual(82, config.Rig.Pipeline.NightGain, 1e-12);
        }
    }

    [TestMethod]
    public async Task ConfiguredAsi676McResponseMatchesPublishedEnvelopeWithoutLegacyOption()
    {
        var config = await LoadProfileAsync("virtual-asi676mc.full.json").ConfigureAwait(false);
        var configured = config.Rig.Sensor.SimulationResponse;
        Assert.IsNotNull(configured);
        Assert.IsNotNull(config.Rig.Readout);
        Assert.IsFalse(config.ModuleOptions!.Value.TryGetProperty("asi676Sensor", out _));

        foreach (var gain in new[] { 0d, 82d, 180d })
        {
            var expected = Asi676SensorModel.Resolve(gain, 64);
            var actual = ConfiguredSensorResponseResolver.Resolve(configured, gain);
            Assert.AreEqual(expected.ElectronsPerAdu, actual.ElectronsPerAdu, 1e-12);
            Assert.AreEqual(expected.ReadNoiseElectrons, actual.ReadNoiseElectrons, 1e-12);
            Assert.AreEqual(expected.FullWellElectrons, actual.FullWellElectrons, 1e-9);
            Assert.AreEqual(expected.BlackLevelAdu, actual.BlackLevelAdu, 1e-12);
        }
    }

    [TestMethod]
    public async Task SyntheticCalibrationEffectsMatchIndependentGeneratedReferences()
    {
        var model = new SyntheticCalibrationModelV1
        {
            Gain = 1,
            Defects = [new SyntheticCalibrationDefect(4, 4)]
        };
        var syntheticOptions = System.Text.Json.JsonSerializer.SerializeToElement(new VirtualSkyCameraModuleOptions
        {
            Seed = 2025,
            MaximumResults = 10,
            VignettingStrength = 0,
            Bias = 0,
            ReadNoiseStandardDeviation = 0,
            DarkCurrentElectronsPerSecond = 0,
            SyntheticCalibration = model
        });
        var idealOptions = System.Text.Json.JsonSerializer.SerializeToElement(new VirtualSkyCameraModuleOptions
        {
            Seed = 2025,
            MaximumResults = 10,
            VignettingStrength = 0,
            Bias = 0,
            ReadNoiseStandardDeviation = 0,
            DarkCurrentElectronsPerSecond = 0
        });
        var baseConfig = CreateConfig(CameraPixelFormat.Mono16, 12, 10);
        var syntheticConfig = baseConfig with
        {
            Module = new CameraModuleDescriptor("VirtualSky", syntheticOptions)
        };
        var idealConfig = baseConfig with
        {
            Module = new CameraModuleDescriptor("VirtualSky", idealOptions)
        };
        var syntheticModule = CreateModule(FixtureUtc);
        var idealModule = CreateModule(FixtureUtc);
        await syntheticModule.InitializeAsync(syntheticConfig, CancellationToken.None).ConfigureAwait(false);
        await idealModule.InitializeAsync(idealConfig, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(
            FixtureUtc,
            TimeSpan.FromSeconds(1),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));

        var synthetic = (await syntheticModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
        var ideal = (await idealModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
        var references = SyntheticCalibrationReferenceGenerator.Generate(
            synthetic.Width, synthetic.Height, synthetic.PixelFormat, model);
        var corrected = Linear16ReferenceCalibration.Correct(
            new Linear16Frame(synthetic.Width, synthetic.Height, synthetic.StrideBytes!.Value, synthetic.PixelFormat, synthetic.PixelData),
            references.Bias,
            references.Dark,
            references.Flat,
            references.DefectMask,
            new(synthetic.Metadata.Exposure, model.DarkExposure, model.FlatExposure, references.FlatNormalizationAdu));

        Assert.AreNotEqual(
            Convert.ToHexString(SHA256.HashData(ideal.PixelData.Span)),
            Convert.ToHexString(SHA256.HashData(synthetic.PixelData.Span)));
        Assert.IsLessThanOrEqualTo(2d, MeanAbsoluteSampleDifference(ideal.PixelData.Span, corrected.PixelData.Span));
        Assert.AreEqual(model.TemperatureC, synthetic.Metadata.TemperatureC);
        Assert.IsTrue(synthetic.Metadata.Extra!.ContainsKey("syntheticCalibrationModelSha256"));
    }

    [TestMethod]
    public async Task VirtualCalibrationCorruptsCleanNativeTwinWithExactControlsMetadataAndReplay()
    {
        const string expectedModelSha256 = "116694B6E95ACD7FB5BC204A48AAD52FEC1E0BA1AA43D3470D69B1106DABCCB8";
        var model = new VirtualCalibrationSourceModelV1 { Seed = 208 };
        var calibration = new VirtualCalibrationLightOptions
        {
            SourceModel = model,
            BiasExposure = TimeSpan.FromMilliseconds(3),
            DarkExposure = TimeSpan.FromSeconds(7),
            FlatExposure = TimeSpan.FromMilliseconds(1500),
            DefectExposure = TimeSpan.FromMilliseconds(4),
            Offset = 8,
            TemperatureC = -12.5
        };
        var cleanConfig = CreateVirtualCalibrationConfig(null);
        var corruptedConfig = CreateVirtualCalibrationConfig(calibration);
        var cleanModule = CreateModule(FixtureUtc);
        var corruptedModule = CreateModule(FixtureUtc);
        var replayModule = CreateModule(FixtureUtc);
        await cleanModule.InitializeAsync(cleanConfig, CancellationToken.None).ConfigureAwait(false);
        await corruptedModule.InitializeAsync(corruptedConfig, CancellationToken.None).ConfigureAwait(false);
        await replayModule.InitializeAsync(corruptedConfig, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(3), 120, null, null);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint);

        var clean = (await cleanModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
        var corrupted = (await corruptedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
        var replay = (await replayModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
        Assert.IsNotNull(clean.Layout);
        var expected = VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
            new CalibrationSourceFrame(clean.Layout, clean.PixelData),
            new VirtualCalibrationLightParameters(
                calibration.BiasExposure,
                calibration.DarkExposure,
                calibration.FlatExposure,
                calibration.DefectExposure,
                setpoint.Exposure,
                setpoint.Gain,
                calibration.Offset,
                calibration.TemperatureC),
            model);

        Assert.AreEqual(expectedModelSha256, VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(model));
        CollectionAssert.AreNotEqual(clean.PixelData.ToArray(), corrupted.PixelData.ToArray());
        CollectionAssert.AreEqual(expected.PixelData.ToArray(), corrupted.PixelData.ToArray());
        CollectionAssert.AreEqual(corrupted.PixelData.ToArray(), replay.PixelData.ToArray());
        Assert.AreEqual("virtual-calibration-source-model-v1", corrupted.Metadata.Extra!["virtualCalibrationSchema"]);
        Assert.AreEqual(expectedModelSha256, corrupted.Metadata.Extra["virtualCalibrationModelSha256"]);
        Assert.AreEqual("virtual-calibration-light-corruption-v1", corrupted.Metadata.Extra["virtualCalibrationAlgorithm"]);
        StringAssert.EndsWith(
            corrupted.Metadata.Extra["renderAlgorithm"],
            "+virtual-calibration-light-corruption-v1",
            StringComparison.Ordinal);
        Assert.AreEqual(setpoint.Exposure, corrupted.Metadata.Exposure);
        Assert.AreEqual(setpoint.Gain, corrupted.Metadata.Gain);
        Assert.AreEqual(calibration.Offset, corrupted.Metadata.Offset);
        Assert.AreEqual(calibration.TemperatureC, corrupted.Metadata.TemperatureC);
        Assert.AreEqual(32, corrupted.Width);
        Assert.AreEqual(16, corrupted.Height);
        Assert.AreEqual(32 * 16 * 2, corrupted.PixelData.Length);
        Assert.AreEqual(12, corrupted.Layout!.SampleDepthBits);
        Assert.AreEqual(16, corrupted.Layout.ContainerDepthBits);
        Assert.AreEqual(FrameByteOrder.LittleEndian, corrupted.Layout.ByteOrder);
        Assert.AreEqual(FrameSamplePacking.ByteAligned, corrupted.Layout.Packing);
        Assert.AreEqual(FrameStoredCodeTransform.RightAlignedV1, corrupted.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.NativeSample, corrupted.Layout.LevelCodeSpace);
        Assert.AreEqual(64d, corrupted.Layout.BlackLevel);
        Assert.AreEqual(4095d, corrupted.Layout.WhiteLevel);
        Assert.IsTrue(MaximumSample(corrupted.PixelData.Span) <= 4095);
    }

    [TestMethod]
    public async Task VirtualCalibrationRejectsSyntheticCalibrationAndMissingOrUnsupportedReadout()
    {
        var calibration = new VirtualCalibrationLightOptions();
        var valid = CreateVirtualCalibrationConfig(calibration);
        var simultaneousOptions = new VirtualSkyCameraModuleOptions
        {
            SyntheticCalibration = new SyntheticCalibrationModelV1(),
            VirtualCalibration = calibration
        };
        var simultaneous = valid with
        {
            Module = new CameraModuleDescriptor(
                "VirtualSky",
                JsonSerializer.SerializeToElement(simultaneousOptions))
        };
        var missingReadout = valid with { Rig = valid.Rig with { Readout = null } };
        var unsupportedReadout = valid with
        {
            Rig = valid.Rig with
            {
                Readout = valid.Rig.Readout! with
                {
                    PixelFormat = CameraPixelFormat.Mono8,
                    SampleDepthBits = 8,
                    ContainerDepthBits = 8,
                    StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
                    LevelCodeSpace = FrameLevelCodeSpace.StoredContainer,
                    BlackLevel = 0,
                    WhiteLevel = byte.MaxValue,
                    StrideBytes = 32
                }
            }
        };

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(simultaneous, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(missingReadout, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(unsupportedReadout, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FullAsi174McTelescopeProfileBuildsRgbCompatibleGraph()
    {
        var config = await LoadProfileAsync("virtual-asi174mc-telescope.full.json").ConfigureAwait(false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(config);

        CollectionAssert.AreEqual(ExpectedRgbGraph, graph.Nodes.Select(static node => node.Id).ToArray());
        graph.DisposeSteps();

        var legacyGraph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(
            config with { ProcessingSteps = [] });
        Assert.IsFalse(legacyGraph.Nodes.Any(static node => node.RecipeName == BuiltInProcessingRecipes.RollingMean));
        Assert.IsFalse(legacyGraph.Nodes.Any(static node => node.RecipeName == BuiltInProcessingRecipes.LinearNormalization));
        legacyGraph.DisposeSteps();
    }

    [TestMethod]
    public async Task FullAsi174McRgbProfileHasFixedFrameEvidenceAndConfiguredPipeline()
    {
        var config = await LoadProfileAsync("virtual-asi174mc.full.json").ConfigureAwait(false);
        var services = new ServiceCollection();
        var catalog = CreateCanonicalStarCatalog();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(catalog);
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var sceneStore = provider.GetRequiredService<IProjectedSceneStore>();
        var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, sceneStore);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(
            config.Rig.Pipeline.NightExposure, config.Rig.Pipeline.NightGain, null, null);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint);

        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var rawBytes = result.Frame!.PixelData.ToArray();
        var statistics = CalculateByteStatistics(rawBytes);
        var checksum = Convert.ToHexString(SHA256.HashData(rawBytes));
        TestContext.WriteLine(
            $"ASI174MC RGB24: min={statistics.Minimum}, max={statistics.Maximum}, " +
            $"mean={statistics.Mean:R}, checksum={checksum}");

        var submission = new CaptureLoopSubmission(request, result, FixtureUtc, request.TargetInterval, TimeSpan.Zero);
        var context = new CaptureProcessingContext(config, submission);
        var pipeline = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreatePipeline(config);
        foreach (var step in pipeline)
        {
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        }

        Assert.AreEqual(1936 * 1216 * 3, rawBytes.Length);
        using var manifest = LoadConformanceManifest();
        var expectedRender = manifest.RootElement.GetProperty("renders").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "asi174mc-rgb24-full");
        var expectedStatistics = expectedRender.GetProperty("statistics");
        Assert.AreEqual((byte)expectedStatistics.GetProperty("minimum").GetInt32(), statistics.Minimum);
        Assert.AreEqual((byte)expectedStatistics.GetProperty("maximum").GetInt32(), statistics.Maximum);
        Assert.AreEqual(expectedStatistics.GetProperty("mean").GetDouble(), statistics.Mean, 1e-12);
        Assert.AreEqual(expectedRender.GetProperty("sha256").GetString(), checksum);
        Assert.AreEqual(expectedRender.GetProperty("rigProfileVersion").GetString(),
            result.Frame.Metadata.Scene!.RigProfileVersion);
        Assert.IsNotNull(context.Artifacts);
        Assert.AreEqual(CameraPixelFormat.Rgb24, context.Artifacts[FrameArtifactRole.Preview].Frame.PixelFormat);
        Assert.AreEqual(CameraPixelFormat.Rgb24, context.Artifacts[FrameArtifactRole.AnnotatedPreview].Frame.PixelFormat);
        Assert.AreEqual("rgb24-canonical-annotation-v2",
            context.Artifacts[FrameArtifactRole.AnnotatedPreview].RecipeVersion);
        CollectionAssert.AreEqual(rawBytes, context.Artifacts.Raw.Frame.PixelData.ToArray());
        CollectionAssert.AreNotEqual(rawBytes,
            context.Artifacts[FrameArtifactRole.AnnotatedPreview].Frame.PixelData.ToArray());
    }
    private static readonly DateTimeOffset FixtureUtc = DateTimeOffset.Parse(
        "2025-01-15T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string[] ExpectedRgbGraph = ["Preview", "Annotation"];
    private static readonly string[] ExpectedStandaloneGraph =
        ["Calibration", "RollingCombination", "CalibratedPreview", "Preview", "Annotation", "LocalStorage", "Telemetry"];
    private static readonly string[] ExpectedTestConstellationIds = ["TST"];
    private static readonly CanonicalAsi174Expectation[] CanonicalAsi174Expectations =
        LoadCanonicalAsi174Expectations();

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
        Assert.IsFalse(first.Frame.Metadata.Extra!.ContainsKey("virtualCalibrationSchema"));
        Assert.IsFalse(first.Frame.Metadata.Extra.ContainsKey("virtualCalibrationModelSha256"));
        Assert.IsFalse(first.Frame.Metadata.Extra.ContainsKey("virtualCalibrationAlgorithm"));
        var checksum = Convert.ToHexString(SHA256.HashData(first.Frame.PixelData.Span));
        TestContext.WriteLine($"Reduced SHA-256: {checksum}");
        Assert.AreEqual("5B77FD453893CC419FF5FFDA0D392329B5DD6557666B1B3D4B91AAD981FEB43F", checksum);
    }

    [TestMethod]
    public async Task CaptureAsync_DeploymentSnapshotOverridesLegacyObservatoryGeometry()
    {
        var hualapaiConfig = CreateConfig();
        var sidingSpring = DeploymentLocationSnapshot.Create(
            "siding-spring-synthetic",
            1,
            "GitHub issue #196 operator-pinned acceptance coordinates; not a physical survey",
            null,
            DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            null,
            -31.2733,
            149.0700,
            1165,
            "Australia/Sydney");
        var sidingSpringConfig = hualapaiConfig with { DeploymentLocation = sidingSpring };
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still);
        var hualapaiModule = CreateModule(FixtureUtc);
        var sidingSpringModule = CreateModule(FixtureUtc);
        await hualapaiModule.InitializeAsync(hualapaiConfig, CancellationToken.None).ConfigureAwait(false);
        await sidingSpringModule.InitializeAsync(sidingSpringConfig, CancellationToken.None).ConfigureAwait(false);

        var hualapai = await hualapaiModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var southern = await sidingSpringModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(35.347, sidingSpringConfig.Observatory.LatitudeDegrees, 1e-12);
        Assert.AreEqual(-31.2733, sidingSpringConfig.ResolveObservatory().LatitudeDegrees, 1e-12);
        Assert.AreNotEqual(hualapai.Frame!.Metadata.Scene!.SceneId, southern.Frame!.Metadata.Scene!.SceneId);
        CollectionAssert.AreNotEqual(hualapai.Frame.PixelData.ToArray(), southern.Frame.PixelData.ToArray());
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
            "{\"magnitudeZeroElectronsPerSecond\":18000,\"asi178Sensor\":{\"enabled\":true,\"blackLevelContainerAdu\":63}}");
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
        Assert.AreEqual("65535", result.Frame.Metadata.Extra["whiteLevelAdu"]);
        Assert.AreEqual(14, result.Frame.Layout!.SampleDepthBits);
        Assert.AreEqual(16, result.Frame.Layout.ContainerDepthBits);
        Assert.AreEqual(FrameStoredCodeTransform.FullRangeScaledV1, result.Frame.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, result.Frame.Layout.LevelCodeSpace);
        Assert.AreEqual(ColorFilterArrayPattern.Rggb, result.Frame.Layout.CfaPattern);
        Assert.AreEqual(0, result.Frame.Layout.Readout!.CfaOriginX);
        Assert.AreEqual(0, result.Frame.Layout.Readout.CfaOriginY);
        Assert.AreEqual(64, result.Frame.Layout.BlackLevel);
        for (var offset = 0; offset < result.Frame.PixelData.Length; offset += 2)
        {
            var stored = result.Frame.PixelData.Span[offset] | result.Frame.PixelData.Span[offset + 1] << 8;
            var native = (stored * 16_383 + ushort.MaxValue / 2) / ushort.MaxValue;
            var remapped = (native * ushort.MaxValue + 16_383 / 2) / 16_383;
            Assert.AreEqual(stored, remapped, $"Stored code at byte offset {offset} is not a mapped 14-bit sample.");
        }
    }

    [TestMethod]
    public async Task InitializeAsyncRejectsLegacyBayerReadoutBelowNativeSampleDepth()
    {
        using var document = JsonDocument.Parse("{\"asi178Sensor\":{\"enabled\":true}}");
        var config = CreateConfig(CameraPixelFormat.BayerRggb16, 32, 16);
        config = config with
        {
            Module = new CameraModuleDescriptor("VirtualSky", document.RootElement.Clone()),
            Rig = config.Rig with
            {
                Readout = new SensorReadoutProfile(
                    new SensorCrop(0, 0, 32, 16),
                    1,
                    1,
                    FrameBinningAlgorithm.IdentityV1,
                    CameraPixelFormat.BayerRggb16,
                    12,
                    16,
                    FrameSamplePacking.ByteAligned,
                    FrameStoredCodeTransform.RightAlignedV1,
                    FrameLevelCodeSpace.NativeSample,
                    0,
                    4095,
                    64,
                    CfaPattern: ColorFilterArrayPattern.Rggb,
                    CfaOriginX: 0,
                    CfaOriginY: 0)
            }
        };

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            CreateModule(FixtureUtc).InitializeAsync(config, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ConfiguredAsi178ProfileProducesFullRangeBayer14In16WithoutLegacyOption()
    {
        var config = await LoadProfileAsync("virtual-asi178mc.full.json").ConfigureAwait(false);
        Assert.IsNotNull(config.Rig.Sensor.SimulationResponse);
        Assert.IsNotNull(config.Rig.Sensor.SimulationResponse.ColorResponse);
        Assert.IsNotNull(config.Rig.Readout);
        Assert.IsFalse(config.ModuleOptions!.Value.TryGetProperty("asi178Sensor", out _));
        var expectedResponse = Asi178McSensorModel.Resolve(150, 64);
        var configuredResponse = ConfiguredSensorResponseResolver.Resolve(config.Rig.Sensor.SimulationResponse, 150);
        Assert.AreEqual(expectedResponse.ElectronsPerAdu, configuredResponse.ElectronsPerAdu, 1e-12);
        Assert.AreEqual(expectedResponse.ReadNoiseElectrons, configuredResponse.ReadNoiseElectrons, 1e-12);
        Assert.AreEqual(expectedResponse.FullWellElectrons, configuredResponse.FullWellElectrons, 1e-9);
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null)),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(14, result.Frame!.Layout!.SampleDepthBits);
        Assert.AreEqual(FrameStoredCodeTransform.FullRangeScaledV1, result.Frame.Layout.StoredCodeTransform);
        Assert.AreEqual(64, result.Frame.Layout.BlackLevel);
        Assert.AreEqual(ushort.MaxValue, result.Frame.Layout.WhiteLevel);
        Assert.AreEqual(checked(3096 * 2080 * 2), result.Frame.PixelData.Length);
    }

    [TestMethod]
    public async Task CaptureAsyncWithAsi174UsesConfiguredNativeBlackLevelInLayout()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            "{\"asi174Sensor\":{\"enabled\":true,\"blackLevelAdu\":73}}");
        var config = CreateConfig(CameraPixelFormat.Mono16, 16, 16) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", document.RootElement.Clone())
        };
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 100, null, null)),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(73, result.Frame!.Layout!.BlackLevel);
        Assert.AreEqual(FrameLevelCodeSpace.NativeSample, result.Frame.Layout.LevelCodeSpace);
    }

    [TestMethod]
    public async Task CaptureAsyncWithAsi174Mono8CenteredRoiBin4ProducesTruthfulReadout()
    {
        var config = CreateAsi174Mono8ReadoutConfig(fullFrame: false);
        var module = CreateModule(FixtureUtc);
        var repeatedModule = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await repeatedModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(
            FixtureUtc,
            TimeSpan.FromSeconds(1),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(1), 150, null, null));

        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var repeated = await repeatedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(160, result.Frame!.Width);
        Assert.AreEqual(120, result.Frame.Height);
        Assert.AreEqual(CameraPixelFormat.Mono8, result.Frame.PixelFormat);
        Assert.AreEqual(19_200, result.Frame.PixelData.Length);
        CollectionAssert.AreEqual(result.Frame.PixelData.ToArray(), repeated.Frame!.PixelData.ToArray());
        Assert.AreEqual(config.Rig.Readout!.Roi.X, result.Frame.Layout!.Readout!.RoiX);
        Assert.AreEqual(config.Rig.Readout.Roi.Y, result.Frame.Layout.Readout.RoiY);
        Assert.AreEqual(FrameBinningAlgorithm.DigitalAverageV1, result.Frame.Layout.Readout.BinningAlgorithm);
        Assert.AreEqual(8, result.Frame.Layout.SampleDepthBits);
        Assert.AreEqual(8, result.Frame.Layout.ContainerDepthBits);
        Assert.AreEqual(FrameStoredCodeTransform.IdentityV1, result.Frame.Layout.StoredCodeTransform);
        Assert.AreEqual(4d, result.Frame.Layout.BlackLevel);
        Assert.AreEqual(255d, result.Frame.Layout.WhiteLevel);
        var projection = RigProjectionContextFactory.Create(config.Rig);
        Assert.AreEqual(80, projection.PrincipalPointX);
        Assert.AreEqual(60, projection.PrincipalPointY);
        Assert.AreEqual(148.96, projection.ImageCircleRadiusPixels);
        Assert.IsTrue(result.Frame.Metadata.Scene!.Objects!.All(item =>
            item.PixelX >= 0 && item.PixelX <= 160 && item.PixelY >= 0 && item.PixelY <= 120));
    }

    [TestMethod]
    public async Task CaptureAsyncWithAsi174Mono8FullFrameBin4Produces484By304()
    {
        var config = CreateAsi174Mono8ReadoutConfig(fullFrame: true);
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromMilliseconds(100), 0, null, null)),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(484, result.Frame!.Width);
        Assert.AreEqual(304, result.Frame.Height);
        Assert.AreEqual(147_136, result.Frame.PixelData.Length);
        Assert.AreEqual(1936, result.Frame.Layout!.Readout!.RoiWidth);
        Assert.AreEqual(1216, result.Frame.Layout.Readout.RoiHeight);
        var projection = RigProjectionContextFactory.Create(config.Rig);
        Assert.AreEqual(242, projection.PrincipalPointX);
        Assert.AreEqual(152, projection.PrincipalPointY);
        Assert.AreEqual(148.96, projection.ImageCircleRadiusPixels);
    }

    [TestMethod]
    [DataRow(SampleByteOrder.LittleEndian)]
    [DataRow(SampleByteOrder.BigEndian)]
    public async Task CaptureAsyncWithGenericMono10In16ProducesRightAlignedCodes(SampleByteOrder byteOrder)
    {
        var config = CreateConfig(CameraPixelFormat.Mono16, 32, 16);
        config = config with
        {
            Rig = config.Rig with
            {
                Readout = new SensorReadoutProfile(
                    new SensorCrop(0, 0, 32, 16),
                    1,
                    1,
                    FrameBinningAlgorithm.IdentityV1,
                    CameraPixelFormat.Mono16,
                    10,
                    16,
                    FrameSamplePacking.ByteAligned,
                    FrameStoredCodeTransform.RightAlignedV1,
                    FrameLevelCodeSpace.NativeSample,
                    0,
                    1023,
                    64,
                    byteOrder)
            }
        };
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null)),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(10, result.Frame!.Layout!.SampleDepthBits);
        Assert.AreEqual(16, result.Frame.Layout.ContainerDepthBits);
        Assert.AreEqual(FrameStoredCodeTransform.RightAlignedV1, result.Frame.Layout.StoredCodeTransform);
        Assert.AreEqual(
            byteOrder == SampleByteOrder.LittleEndian ? FrameByteOrder.LittleEndian : FrameByteOrder.BigEndian,
            result.Frame.Layout.ByteOrder);
        Assert.IsTrue(MaximumSample(result.Frame.PixelData.Span, byteOrder) <= 1023);
    }

    [TestMethod]
    public async Task CaptureAsyncWithAsi676ModelsProducesNative12BitSamplesInRaw16Containers()
    {
        foreach (var fileName in new[] { "virtual-asi676mm.full.json", "virtual-asi676mc.full.json" })
        {
            var profile = await LoadProfileAsync(fileName).ConfigureAwait(false);
            var config = profile with
            {
                Rig = profile.Rig with
                {
                    Sensor = profile.Rig.Sensor with
                    {
                        WidthPixels = 64,
                        HeightPixels = 64,
                        StrideBytes = 128
                    },
                    Optics = profile.Rig.Optics with
                    {
                        PrincipalPointX = 32,
                        PrincipalPointY = 32,
                        ImageCircleRadiusPixels = 30,
                        FocalLengthXPixels = 20,
                        FocalLengthYPixels = 20
                    },
                    Readout = profile.Rig.Readout is null
                        ? null
                        : profile.Rig.Readout with
                        {
                            Roi = new SensorCrop(0, 0, 64, 64),
                            StrideBytes = 128
                        }
                }
            };
            var module = CreateModule(FixtureUtc);
            var repeatedModule = CreateModule(FixtureUtc);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            await repeatedModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

            var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 82, null, null));
            var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var repeated = await repeatedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(64 * 64 * 2, result.Frame!.PixelData.Length);
            CollectionAssert.AreEqual(result.Frame.PixelData.ToArray(), repeated.Frame!.PixelData.ToArray());
            Assert.AreEqual(Asi676SensorModel.Version, result.Frame.Metadata.Extra!["sensorModel"]);
            Assert.AreEqual("12", result.Frame.Metadata.Extra["sensorAdcBitDepth"]);
            Assert.AreEqual("16", result.Frame.Metadata.Extra["containerBitDepth"]);
            Assert.AreEqual("4095", result.Frame.Metadata.Extra["whiteLevelAdu"]);
            Assert.AreEqual(12, result.Frame.Layout!.SampleDepthBits);
            Assert.AreEqual(16, result.Frame.Layout.ContainerDepthBits);
            Assert.AreEqual(FrameStoredCodeTransform.RightAlignedV1, result.Frame.Layout.StoredCodeTransform);
            Assert.AreEqual(FrameLevelCodeSpace.NativeSample, result.Frame.Layout.LevelCodeSpace);
            Assert.AreEqual(config.Rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? 0 : null,
                result.Frame.Layout.Readout!.CfaOriginX);
            Assert.AreEqual("provisional-published-envelope",
                result.Frame.Metadata.Extra["responseCalibrationStatus"]);
            Assert.IsTrue(MaximumSample(result.Frame.PixelData.Span) <= 4095);
            Assert.AreEqual(profile.Rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16,
                result.Frame.Metadata.Extra.ContainsKey("cfaPattern"));
            Assert.AreEqual(config.Rig.ProfileVersion, result.Frame.Metadata.Scene!.RigProfileVersion);
            Assert.AreEqual(config.Rig.Sensor.SensorRecipeVersion,
                result.Frame.Metadata.Scene.SensorRecipeVersion);
            Assert.AreEqual(config.Rig.Optics.CalibrationVersion,
                result.Frame.Metadata.Scene.ProjectionCalibrationVersion);
            Assert.AreEqual(RigProjectionContextFactory.CreateProfileHashSha256(config.Rig),
                result.Frame.Metadata.Scene.RigProfileHashSha256);
            Assert.AreEqual(result.Frame.Metadata.Scene.SceneId, repeated.Frame.Metadata.Scene!.SceneId);
            if (profile.Rig.Sensor.PixelFormat == CameraPixelFormat.Mono16)
            {
                Assert.AreEqual("ASI676 native 12-bit ADU in Mono16 container",
                    result.Frame.Metadata.Extra["compatibilityLabel"]);
            }
            else
            {
                Assert.AreEqual("neutral-uncharacterized",
                    result.Frame.Metadata.Extra["channelResponseModel"]);
            }
        }
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
    public async Task CaptureAsyncWithFixedSceneUtcKeepsAstronomyFixedAndOperationalTimingCurrent()
    {
        var fixedSceneUtc = FixtureUtc.AddDays(-30);
        using var options = JsonDocument.Parse($$"""
            {
              "fixedSceneUtc": "{{fixedSceneUtc:O}}"
            }
            """);
        var config = CreateConfig() with
        {
            Module = new CameraModuleDescriptor("VirtualSky", options.RootElement.Clone())
        };
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var firstRequestedUtc = FixtureUtc;
        var laterRequestedUtc = FixtureUtc.AddHours(1);

        var first = await module.CaptureAsync(
            new CaptureRequest(firstRequestedUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);
        var later = await module.CaptureAsync(
            new CaptureRequest(laterRequestedUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(fixedSceneUtc, first.Frame!.Metadata.Scene!.SceneUtc);
        Assert.AreEqual(fixedSceneUtc, later.Frame!.Metadata.Scene!.SceneUtc);
        CollectionAssert.AreEqual(
            first.Frame.Metadata.Scene.Objects!.ToArray(),
            later.Frame.Metadata.Scene.Objects!.ToArray());
        Assert.AreEqual(firstRequestedUtc, first.Frame.TimestampUtc);
        Assert.AreEqual(laterRequestedUtc, later.Frame.TimestampUtc);
        Assert.AreEqual(firstRequestedUtc, first.AcquisitionTiming!.ExposureStartedUtc);
        Assert.AreEqual(laterRequestedUtc, later.AcquisitionTiming!.ExposureStartedUtc);
    }

    [TestMethod]
    public async Task CaptureAsyncWithFixedSequenceStartUsesEffectiveRequestCadence()
    {
        var sequenceStartUtc = FixtureUtc.AddDays(-30);
        using var options = JsonDocument.Parse($$"""
            {
              "fixedSequenceStartUtc": "{{sequenceStartUtc:O}}"
            }
            """);
        var config = CreateConfig() with
        {
            Module = new CameraModuleDescriptor("VirtualSky", options.RootElement.Clone()),
            Rig = CreateConfig().Rig with
            {
                Pipeline = CreateConfig().Rig.Pipeline with { CaptureInterval = TimeSpan.FromSeconds(10) }
            }
        };
        var module = CreateModule(FixtureUtc);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var first = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(7), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);
        var second = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc.AddHours(1), TimeSpan.FromSeconds(11), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);
        var third = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc.AddHours(2), TimeSpan.FromSeconds(3), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(sequenceStartUtc, first.Frame!.Metadata.Scene!.SceneUtc);
        Assert.AreEqual(sequenceStartUtc.AddSeconds(7), second.Frame!.Metadata.Scene!.SceneUtc);
        Assert.AreEqual(sequenceStartUtc.AddSeconds(18), third.Frame!.Metadata.Scene!.SceneUtc);
        Assert.AreEqual(FixtureUtc, first.Frame.TimestampUtc);
        Assert.AreEqual(FixtureUtc.AddHours(1), second.Frame.TimestampUtc);
        Assert.AreEqual(FixtureUtc.AddHours(2), third.Frame.TimestampUtc);
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
    public async Task CanonicalAsi174CaptureHasFixedGeometryCentroidAndStatistics()
    {
        foreach (var expected in CanonicalAsi174Expectations)
        {
            var module = CreateCanonicalStarModule();
            await module.InitializeAsync(CreateAsi174Config(expected.Width, expected.Height), CancellationToken.None)
                .ConfigureAwait(false);

            var result = await module.CaptureAsync(
                new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null)),
                CancellationToken.None).ConfigureAwait(false);

            var frame = result.Frame!;
            var objects = frame.Metadata.Scene!.Objects!;
            var sirius = objects.Single(item => item.Id == "HIP 32349");
            var centroid = CalculateLocalCentroid(frame, sirius.PixelX, sirius.PixelY, 6);
            var statistics = CalculateRawStatistics(frame.PixelData.Span);
            var checksum = Convert.ToHexString(SHA256.HashData(frame.PixelData.Span));

            Assert.HasCount(expected.ObjectPixels.Count, objects);
            foreach (var item in objects)
            {
                var expectedPixel = expected.ObjectPixels[item.Id];
                Assert.AreEqual(expectedPixel.X, item.PixelX, 1e-9, $"{expected.Width}x{expected.Height} {item.Id} X");
                Assert.AreEqual(expectedPixel.Y, item.PixelY, 1e-9, $"{expected.Width}x{expected.Height} {item.Id} Y");
            }
            Assert.AreEqual(Asi174MmSensorModel.Version, frame.Metadata.Extra!["sensorModel"]);
            Assert.AreEqual(expected.SiriusCentroid.X, centroid.X, 1e-9);
            Assert.AreEqual(expected.SiriusCentroid.Y, centroid.Y, 1e-9);
            Assert.AreEqual(expected.Minimum, statistics.Minimum);
            Assert.AreEqual(expected.Maximum, statistics.Maximum);
            Assert.AreEqual(expected.Mean, statistics.Mean, 1e-12);
            Assert.AreEqual(expected.Checksum, checksum);
        }
    }

    [TestMethod]
    public async Task CanonicalStarGeometryMovesNumericallyWithTimeBoresightRollAndFlip()
    {
        async Task<ProjectedObjectProvenance> CaptureAsync(
            DateTimeOffset utc,
            RigOrientation orientation,
            bool horizontalFlip = false)
        {
            var module = CreateCanonicalStarModule();
            var config = CreateConfig() with
            {
                Rig = CreateConfig().Rig with
                {
                    Orientation = orientation,
                    Optics = CreateConfig().Rig.Optics with { HorizontalFlip = horizontalFlip }
                }
            };
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var result = await module.CaptureAsync(
                new CaptureRequest(utc, TimeSpan.FromSeconds(1), CaptureMode.Still),
                CancellationToken.None).ConfigureAwait(false);
            return result.Frame!.Metadata.Scene!.Objects!.Single(item => item.Id == "HIP 32349");
        }

        var captured = new Dictionary<string, ProjectedObjectProvenance>(StringComparer.Ordinal);
        foreach (var expected in LoadOrientationExpectations())
        {
            var value = await CaptureAsync(expected.Utc,
                new RigOrientation(expected.BoresightAltitude, expected.BoresightAzimuth, expected.Roll),
                expected.HorizontalFlip).ConfigureAwait(false);
            AssertProjectedPixel(value, expected.Pixel.X, expected.Pixel.Y);
            captured.Add(expected.Id, value);
        }

        var normal = captured["primary"];
        var rolled = captured["roll-90"];
        var flipped = captured["horizontal-flip"];
        Assert.AreEqual(242 - (normal.PixelY - 152), rolled.PixelX, 1e-9);
        Assert.AreEqual(152 + (normal.PixelX - 242), rolled.PixelY, 1e-9);
        Assert.AreEqual(2 * 242 - normal.PixelX, flipped.PixelX, 1e-9);
        Assert.AreEqual(normal.PixelY, flipped.PixelY, 1e-9);
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
        Assert.IsGreaterThan(
            double.Parse(lowResult.Frame.Metadata.Extra["renderMean"], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(highResult.Frame.Metadata.Extra["renderMean"], System.Globalization.CultureInfo.InvariantCulture));
        CollectionAssert.AreEqual(
            lowResult.Frame.Metadata.Scene!.Objects!.Select(item => (item.Id, item.PixelX, item.PixelY)).ToArray(),
            highResult.Frame.Metadata.Scene!.Objects!.Select(item => (item.Id, item.PixelX, item.PixelY)).ToArray());
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
        Assert.AreEqual("4095", first.Frame.Metadata.Extra["whiteLevelAdu"]);
        Assert.AreEqual("ASI174MM native 12-bit ADU in Mono16",
            first.Frame.Metadata.Extra["compatibilityLabel"]);
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

    private static VirtualSkyCameraModule CreateCanonicalStarModule()
    {
        return new VirtualSkyCameraModule(TimeProvider.System, CreateCanonicalStarCatalog(), new ProjectedSceneStore());
    }

    private static InMemoryCelestialCatalog CreateCanonicalStarCatalog()
        => new([
            new CelestialCatalogObject("HIP 32349", "Sirius", 101.28715533 / 15, -16.71611586, -1.46, 0.009, "32349"),
            new CelestialCatalogObject("HIP 24608", "Capella", 79.17232794 / 15, 45.99799147, 0.08, 0.795, "24608"),
            new CelestialCatalogObject("HIP 37279", "Procyon", 114.8254935 / 15, 5.22499307, 0.34, 0.42, "37279"),
            new CelestialCatalogObject("HIP 27989", "Betelgeuse", 88.792939 / 15, 7.407064, 0.45, 1.5, "27989")
        ]);

    private static async Task<CameraModuleConfig> LoadProfileAsync(string fileName)
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, fileName),
            AgentId = "canonical-profile-test",
            Observatory = new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix")
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static CanonicalAsi174Expectation[] LoadCanonicalAsi174Expectations()
    {
        using var manifest = LoadConformanceManifest();
        var root = manifest.RootElement;
        var profiles = root.GetProperty("projectionProfiles");
        return root.GetProperty("renders").EnumerateArray()
            .Where(item => item.GetProperty("pixelFormat").GetString() == "Mono16")
            .Select(item =>
            {
                var profile = profiles.GetProperty(item.GetProperty("profileId").GetString()!);
                var pixels = item.GetProperty("objectPixels").EnumerateObject()
                    .ToDictionary(
                        property => property.Name,
                        property => ReadPixel(property.Value),
                        StringComparer.Ordinal);
                var centroid = item.GetProperty("centroids").GetProperty("HIP 32349");
                var statistics = item.GetProperty("statistics");
                return new CanonicalAsi174Expectation(
                    profile.GetProperty("width").GetInt32(),
                    profile.GetProperty("height").GetInt32(),
                    pixels,
                    ReadPixel(centroid),
                    (ushort)statistics.GetProperty("minimum").GetInt32(),
                    (ushort)statistics.GetProperty("maximum").GetInt32(),
                    statistics.GetProperty("mean").GetDouble(),
                    item.GetProperty("sha256").GetString()!);
            })
            .ToArray();
    }

    private static OrientationExpectation[] LoadOrientationExpectations()
    {
        using var manifest = LoadConformanceManifest();
        return manifest.RootElement.GetProperty("orientationCases").EnumerateArray()
            .Select(item => new OrientationExpectation(
                item.GetProperty("id").GetString()!,
                DateTimeOffset.Parse(item.GetProperty("utc").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture),
                item.GetProperty("boresightAltitudeDegrees").GetDouble(),
                item.GetProperty("boresightAzimuthDegrees").GetDouble(),
                item.GetProperty("rollDegrees").GetDouble(),
                item.GetProperty("horizontalFlip").GetBoolean(),
                ReadPixel(item.GetProperty("expectedReducedPixel"))))
            .ToArray();
    }

    private static System.Text.Json.JsonDocument LoadConformanceManifest()
        => System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hualapai-asi174-conformance-v1.json")));

    private static PixelPoint ReadPixel(System.Text.Json.JsonElement value)
        => new(value.GetProperty("x").GetDouble(), value.GetProperty("y").GetDouble());

    private static CameraModuleConfig CreateAsi174Config(int width, int height)
    {
        using var options = System.Text.Json.JsonDocument.Parse(
            "{\"seed\":2025,\"maximumMagnitude\":6.5,\"maximumResults\":10," +
            "\"magnitudeZeroElectronsPerSecond\":300,\"bortleClass\":3," +
            "\"rigProfileVersion\":\"canonical-asi174mm-v1\"," +
            "\"asi174Sensor\":{\"enabled\":true,\"blackLevelAdu\":64}}");
        return CreateConfig(CameraPixelFormat.Mono16, width, height) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", options.RootElement.Clone()),
            Rig = CreateConfig(CameraPixelFormat.Mono16, width, height).Rig with
            {
                Sensor = CreateConfig(CameraPixelFormat.Mono16, width, height).Rig.Sensor with
                {
                    SensorRecipeVersion = "virtual-asi174mm-electron-domain-v2"
                }
            }
        };
    }

    private static CameraModuleConfig CreateAsi174Mono8ReadoutConfig(bool fullFrame)
    {
        var config = CreateAsi174Config(1936, 1216);
        return config with
        {
            Rig = config.Rig with
            {
                Sensor = config.Rig.Sensor with { StrideBytes = 3872 },
                Readout = new SensorReadoutProfile(
                    fullFrame
                        ? new SensorCrop(0, 0, 1936, 1216)
                        : new SensorCrop(648, 368, 640, 480),
                    4,
                    4,
                    FrameBinningAlgorithm.DigitalAverageV1,
                    CameraPixelFormat.Mono8,
                    8,
                    8,
                    FrameSamplePacking.ByteAligned,
                    FrameStoredCodeTransform.IdentityV1,
                    FrameLevelCodeSpace.StoredContainer,
                    4,
                    255)
            }
        };
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

    private static CameraModuleConfig CreateVirtualCalibrationConfig(VirtualCalibrationLightOptions? calibration)
    {
        const int width = 32;
        const int height = 16;
        var config = CreateConfig(CameraPixelFormat.Mono16, width, height);
        var options = new VirtualSkyCameraModuleOptions
        {
            Seed = 2025,
            MaximumResults = 10,
            VirtualCalibration = calibration
        };
        return config with
        {
            Module = new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(options)),
            Rig = config.Rig with
            {
                Readout = new SensorReadoutProfile(
                    new SensorCrop(0, 0, width, height),
                    1,
                    1,
                    FrameBinningAlgorithm.IdentityV1,
                    CameraPixelFormat.Mono16,
                    12,
                    16,
                    FrameSamplePacking.ByteAligned,
                    FrameStoredCodeTransform.RightAlignedV1,
                    FrameLevelCodeSpace.NativeSample,
                    64,
                    4095,
                    width * 2)
            }
        };
    }

    private static ushort MaximumSample(
        ReadOnlySpan<byte> pixels,
        SampleByteOrder byteOrder = SampleByteOrder.LittleEndian)
    {
        ushort maximum = 0;
        for (var offset = 0; offset < pixels.Length; offset += 2)
        {
            var sample = byteOrder == SampleByteOrder.LittleEndian
                ? (ushort)(pixels[offset] | pixels[offset + 1] << 8)
                : (ushort)(pixels[offset] << 8 | pixels[offset + 1]);
            maximum = Math.Max(maximum, sample);
        }
        return maximum;
    }

    private static double MeanAbsoluteSampleDifference(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        double total = 0;
        for (var offset = 0; offset < expected.Length; offset += 2)
        {
            var expectedValue = (ushort)(expected[offset] | expected[offset + 1] << 8);
            var actualValue = (ushort)(actual[offset] | actual[offset + 1] << 8);
            total += Math.Abs(expectedValue - actualValue);
        }
        return total / (expected.Length / 2);
    }

    private static PixelPoint CalculateLocalCentroid(CameraFrame frame, double sourceX, double sourceY, int radius)
    {
        var centerX = (int)Math.Round(sourceX, MidpointRounding.AwayFromZero);
        var centerY = (int)Math.Round(sourceY, MidpointRounding.AwayFromZero);
        var strideBytes = frame.StrideBytes ?? checked(frame.Width * 2);
        var minimum = ushort.MaxValue;
        for (var y = Math.Max(0, centerY - radius); y <= Math.Min(frame.Height - 1, centerY + radius); y++)
        {
            for (var x = Math.Max(0, centerX - radius); x <= Math.Min(frame.Width - 1, centerX + radius); x++)
            {
                minimum = Math.Min(minimum, ReadSample(frame.PixelData.Span, strideBytes, x, y));
            }
        }

        double total = 0;
        double weightedX = 0;
        double weightedY = 0;
        for (var y = Math.Max(0, centerY - radius); y <= Math.Min(frame.Height - 1, centerY + radius); y++)
        {
            for (var x = Math.Max(0, centerX - radius); x <= Math.Min(frame.Width - 1, centerX + radius); x++)
            {
                var weight = ReadSample(frame.PixelData.Span, strideBytes, x, y) - minimum;
                total += weight;
                weightedX += (x + 0.5) * weight;
                weightedY += (y + 0.5) * weight;
            }
        }
        return new PixelPoint(weightedX / total, weightedY / total);
    }

    private static (ushort Minimum, ushort Maximum, double Mean) CalculateRawStatistics(ReadOnlySpan<byte> pixels)
    {
        var minimum = ushort.MaxValue;
        ushort maximum = 0;
        double total = 0;
        for (var offset = 0; offset < pixels.Length; offset += 2)
        {
            var value = (ushort)(pixels[offset] | pixels[offset + 1] << 8);
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            total += value;
        }
        return (minimum, maximum, total / (pixels.Length / 2));
    }

    private static (byte Minimum, byte Maximum, double Mean) CalculateByteStatistics(ReadOnlySpan<byte> pixels)
    {
        var minimum = byte.MaxValue;
        byte maximum = 0;
        double total = 0;
        foreach (var value in pixels)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            total += value;
        }
        return (minimum, maximum, total / pixels.Length);
    }

    private static ushort ReadSample(ReadOnlySpan<byte> pixels, int strideBytes, int x, int y)
    {
        var offset = y * strideBytes + x * 2;
        return (ushort)(pixels[offset] | pixels[offset + 1] << 8);
    }

    private static void AssertProjectedPixel(ProjectedObjectProvenance value, double expectedX, double expectedY)
    {
        Assert.AreEqual(expectedX, value.PixelX, 1e-9);
        Assert.AreEqual(expectedY, value.PixelY, 1e-9);
    }

    private sealed record CanonicalAsi174Expectation(
        int Width,
        int Height,
        IReadOnlyDictionary<string, PixelPoint> ObjectPixels,
        PixelPoint SiriusCentroid,
        ushort Minimum,
        ushort Maximum,
        double Mean,
        string Checksum);

    private sealed record OrientationExpectation(
        string Id,
        DateTimeOffset Utc,
        double BoresightAltitude,
        double BoresightAzimuth,
        double Roll,
        bool HorizontalFlip,
        PixelPoint Pixel);

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
