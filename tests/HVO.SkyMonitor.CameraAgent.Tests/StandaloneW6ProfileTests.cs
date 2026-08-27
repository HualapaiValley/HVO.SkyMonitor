using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StandaloneW6ProfileTests
{
    private static readonly IReadOnlyDictionary<string, string> Pre433HistoricalPlanSha256s =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projected-scene"] = "43678EA0F4440E611BAAEE90864128A6691404E008289DF0D53965D57B84B4D9",
            ["calibration"] = "F78C7C80FE1614D5DDE773DEED1DEB570840D974CF7D4CCB4188B2FBC0BC405C",
            ["calibrated-preview"] = "C12900BBDCE042B994277E4E0F3D8CF7CBEADF038BC642FC6B01C80ECFE1435A",
            ["rolling"] = "3D8C8BB7F89830B66E17A3DE97C0E1517E2198EBC76637E69DF5214787BD3E09",
            ["quality"] = "74FCF615403E8488D214F195A5EAE9B8BCA89FF00B45C9D25A90995215166058",
            ["cloud"] = "C3937D26381FB9343A0C2439548D718B62CAA7778FE965B9E48D5E8C7BBD620E",
            ["combined-preview"] = "F86502BE3335BEF3D77A7B9BA3613D703B70B297E9C64F016F9B204B051C2E61",
            ["sky-annotation"] = "369E3771B85E36A555D516241B66AF9A11D1D7AF6E564359BB8D53F4E4C20754",
            ["weather-overlay"] = "D6187E7E7D8F377FA1F912F31C55005A8A922C609266D1425FF00D1E1B74C7C4",
            ["storage"] = "43AAE55408B69B7B1946395176ECEFA5ED77361CAEA1E1E81C4409B5A20A539D",
            ["telemetry"] = "461C6454B441D84F01830076279AC915DC6A88824C663A73DF1D7B7C8D6A8E60"
        };
    private static readonly ObservatoryLocation Location = new(
        35.5599378,
        -113.9119818,
        520,
        "America/Phoenix");

    [TestMethod]
    public async Task Asi676Profile_HasPinnedW6IdentityAndEffectiveGraph()
    {
        var configuration = await LoadAsync("cameraagent.standalone-w6.json").ConfigureAwait(false);
        using var provider = CreateProvider();
        var preview = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().PreviewPlan(configuration);

        var rigSha256 = CameraRigProfileIdentity.ComputeSha256(configuration.Rig);
        Assert.AreEqual(
            "7191D84F4BA368482546AB6A09FBFEDD2F273BD626FABE6F3156C55C54DFCA9B",
            rigSha256,
            rigSha256);
        var processingSha256 = RawCaptureDescriptorFactory.CreateProcessingProfile(configuration).Sha256;
        Assert.AreEqual(
            "8F9EC413736CCF95026581287D8FFA6C8B05BAF1174420D95A3E49B185A3E18D",
            processingSha256,
            processingSha256);
        Assert.AreEqual(
            "6A5E298C74CA520E3EB3EEE6CE67D30A8BDFC1340ACC2FE1AA5DDDF0F6F9CFDD",
            CaptureScheduleContract.ComputeSha256(configuration.Schedule!));
        var localProfileSha256 = LocalCaptureProfileContract.ComputeSha256(
            LocalCaptureProfileDefinition.CreateForConfiguration(configuration, configuration.Schedule!));
        Assert.AreEqual(
            "966C360EA52CDCE8AC150A138D94903587B56B2D2F0EC2618AE5E6506259388C",
            localProfileSha256,
            localProfileSha256);
        Assert.AreEqual(
            "13DBCBB11F6FBF633B2259FFC668F5109E33F664501ED38B03F6F7ED4AE3F363",
            preview.DesiredSha256,
            preview.DesiredSha256);
        Assert.AreEqual(
            "FD3E214AD0A65808F481789D341CEE763443DA8383A52EC55FE52E07DD42F07F",
            preview.EffectiveSha256,
            preview.EffectiveSha256);
        Assert.HasCount(14, preview.EffectiveNodes);
        Assert.IsFalse(preview.EffectiveNodes.Any(static node => node.Id is "sky-annotation" or "weather-overlay"));
        Assert.IsFalse(preview.EffectiveNodes.Single(static node => node.Id == "cloud").Required);
        Assert.IsFalse(preview.EffectiveNodes.Single(static node => node.Id == "cloud-presentation").Required);
        CollectionAssert.DoesNotContain(
            preview.EffectiveNodes.Single(static node => node.Id == "storage").Dependencies!.ToArray(),
            "cloud-presentation");
        CollectionAssert.Contains(
            preview.EffectiveNodes.Single(static node => node.Id == "storage").Dependencies!.ToArray(),
            "$raw");
        CollectionAssert.DoesNotContain(
            preview.EffectiveNodes.Single(static node => node.Id == "telemetry").Dependencies!.ToArray(),
            "cloud-presentation");
        Assert.IsTrue(preview.EffectiveNodes.Single(static node => node.Id == "presentation-materializer")
            .Publication?.Persistence == CaptureProcessingPersistenceMode.DurableLocal);
        Assert.AreEqual(TimeSpan.FromSeconds(5), configuration.Rig.Pipeline.NightExposure);
        Assert.AreEqual(TimeSpan.FromSeconds(10), configuration.Rig.Pipeline.CaptureInterval);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, configuration.Rig.Readout!.PixelFormat);
        Assert.AreEqual(25_233_408L, SensorReadoutResolver.Resolve(
            configuration.Rig.Sensor, configuration.Rig.Readout).Layout.ByteLength);
        var cloud = configuration.Module.Options!.Value.GetProperty("cloudScenario");
        Assert.AreEqual(0d, cloud.GetProperty("driftEastCellsPerSecond").GetDouble());
        Assert.AreEqual(0d, cloud.GetProperty("driftNorthCellsPerSecond").GetDouble());
        Assert.AreEqual(0d, cloud.GetProperty("evolutionCellsPerSecond").GetDouble());
    }

    [TestMethod]
    public async Task Asi174Mono8Profile_UsesSameV2PathWithoutIncompatibleCalibrationOrRolling()
    {
        var configuration = await LoadAsync("cameraagent.standalone-w6-mono8.json").ConfigureAwait(false);
        using var provider = CreateProvider();
        var preview = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().PreviewPlan(configuration);

        Assert.AreEqual(
            "FBF90275979743BC7B13128808CBE079D206F9D5435EB45F55D9AC956118A479",
            CameraRigProfileIdentity.ComputeSha256(configuration.Rig));
        var processingSha256 = RawCaptureDescriptorFactory.CreateProcessingProfile(configuration).Sha256;
        Assert.AreEqual(
            "2F106F303DE1CD41AE1E1B8B001B15BD521A6121A9595BD3BB0D38B00DE30631",
            processingSha256,
            processingSha256);
        Assert.AreEqual(
            "99892B9195FDAF6800CB1B8D914B39A610A989B2775B2BD980E50EE8C15CCD80",
            CaptureScheduleContract.ComputeSha256(configuration.Schedule!));
        Assert.AreEqual(
            "72473832303887743861B9C82F1F11A55147254E34948C628EE1FBE77DF42B7D",
            LocalCaptureProfileContract.ComputeSha256(
                LocalCaptureProfileDefinition.CreateForConfiguration(configuration, configuration.Schedule!)));
        Assert.AreEqual(
            "2538EB75560857DA6319DD4F20533C6F6B768E7DB47F95B661150B85D6ECFBAB",
            preview.DesiredSha256);
        Assert.AreEqual(
            "14225DF460F651A0F51C578F2BE55A6B81E3F1B5770E15B2D65AFBC2DBBDC1D9",
            preview.EffectiveSha256,
            preview.EffectiveSha256);
        Assert.HasCount(4, preview.EffectiveNodes);
        Assert.IsFalse(preview.EffectiveNodes.Any(static node =>
            node.Alias is "Calibration" or "RollingCombination"));
        CollectionAssert.Contains(
            preview.EffectiveNodes.Single(static node => node.Id == "storage").Dependencies!.ToArray(),
            "$raw");
        var layout = SensorReadoutResolver.Resolve(configuration.Rig.Sensor, configuration.Rig.Readout!).Layout;
        Assert.AreEqual(160, layout.Width);
        Assert.AreEqual(120, layout.Height);
        Assert.AreEqual(CameraPixelFormat.Mono8, layout.PixelFormat);
        Assert.AreEqual(19_200L, layout.ByteLength);
    }

    [TestMethod]
    public async Task OverlayManifestRejectsMissingRepeatedMetadataDependency()
    {
        var configuration = await LoadAsync("cameraagent.standalone-w6.json").ConfigureAwait(false);
        var steps = configuration.Pipeline!.Steps.Select(step => step.Id == "overlay-manifest"
            ? step with { DependsOn = ["combined-preview", "scene-presentation", "cloud-presentation"] }
            : step).ToArray();
        using var provider = CreateProvider();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(
                configuration with { Pipeline = configuration.Pipeline with { Steps = steps } }));

        StringAssert.Contains(exception.Message, "required dependency inputs", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OverlayManifestRejectsWrongRepeatedMetadataRecipe()
    {
        var configuration = await LoadAsync("cameraagent.standalone-w6.json").ConfigureAwait(false);
        var steps = configuration.Pipeline!.Steps.Select(step => step.Id == "overlay-manifest"
            ? step with { DependsOn = ["combined-preview", "scene-presentation", "quality", "environment-presentation"] }
            : step).ToArray();
        using var provider = CreateProvider();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(
                configuration with { Pipeline = configuration.Pipeline with { Steps = steps } }));

        StringAssert.Contains(exception.Message, "required dependency inputs", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OverlayManifestRejectsAmbiguousRepeatedMetadataProducers()
    {
        var configuration = await LoadAsync("cameraagent.standalone-w6.json").ConfigureAwait(false);
        var projected = configuration.Pipeline!.Steps.Single(step => step.Id == "cloud-presentation");
        var steps = configuration.Pipeline.Steps
            .Append(projected with
            {
                Id = "cloud-presentation-copy",
                Order = projected.Order + 1,
                Options = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    maskOutputVariant = "copy-cloud-mask",
                    labelOutputVariant = "copy-cloud-label",
                    widthPixels = 3552,
                    heightPixels = 3552
                })
            })
            .Select(step => step.Id == "overlay-manifest"
                ? step with { DependsOn = ["combined-preview", "scene-presentation", "cloud-presentation", "cloud-presentation-copy", "environment-presentation"] }
                : step).ToArray();
        using var provider = CreateProvider();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(
                configuration with { Pipeline = configuration.Pipeline with { Steps = steps } }));

        StringAssert.Contains(exception.Message, "required dependency inputs", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task StoragePolicyMatchesSecondaryOutputAndRejectsItsUpload()
    {
        var configuration = await LoadAsync("cameraagent.standalone-w6.json").ConfigureAwait(false);
        using var provider = CreateProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();

        CameraModuleConfig WithSecondaryPolicy(bool queueForUpload)
        {
            var steps = configuration.Pipeline!.Steps.Select(step => step.Id == "storage"
                ? step with
                {
                    Options = System.Text.Json.JsonSerializer.SerializeToElement(
                        new NoOpFileStorageProcessingStepOptions
                        {
                            StorageRoot = "/var/lib/hvo/data/agent",
                            RetentionDays = 1,
                            UpdateLatestFrame = true,
                            QueueForUpload = false,
                            Policies =
                            [
                                new ArtifactStoragePolicyOptions
                                {
                                    StepId = "scene-presentation",
                                    Variant = "w6-constellation-layer",
                                    QueueForUpload = queueForUpload
                                }
                            ]
                        })
                }
                : step).ToArray();
            return configuration with { Pipeline = configuration.Pipeline with { Steps = steps } };
        }

        var accepted = factory.CreateGraph(WithSecondaryPolicy(queueForUpload: false));
        accepted.DisposeSteps();
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            factory.CreateGraph(WithSecondaryPolicy(queueForUpload: true)));
        StringAssert.Contains(exception.Message, "w6-constellation-layer", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Pre433W6FixturePinsEveryAllowlistedHistoricalNodeAndExcludesChangedCloud()
    {
        var current = await LoadAsync("cameraagent.standalone-w6.json").ConfigureAwait(false);
        var prefix = current.Pipeline!.Steps.TakeWhile(static step => step.Id != "scene-presentation").ToList();
        var oldAnnotation = new CaptureProcessingStepConfig("Annotation", "sky-annotation", 70,
            SerializeLegacyOptions(new AnnotationProcessingStepOptions
            {
                MarkRadius = 6,
                MarkerValue = 144,
                DrawLabels = true,
                MaximumLabelMagnitude = 2.5,
                LabelScale = 2,
                DrawConstellationLines = true,
                ConstellationIds = ["ORI", "UMA", "UMI", "CAS", "CYG", "LYR"],
                ConstellationLineValue = 160,
                ConstellationLineRed = 96,
                ConstellationLineGreen = 160,
                ConstellationLineBlue = 255,
                ConstellationLineThickness = 2,
                ConstellationLineOpacity = 0.8,
                DrawImageCircle = true,
                DrawCardinalDirections = true,
                DrawMetadataCorners = true,
                RequireProjectedSceneDependency = true,
                TopLeftTokens = ["agent-identity", "capture-sequence", "utc"],
                TopRightTokens = ["schedule-profile", "exposure", "cadence", "gain", "offset", "sensor-setpoint"],
                BottomLeftTokens = ["environment"],
                BottomRightTokens = ["catalog", "calibration", "stack", "processing-profile"],
                EnvironmentalKinds = [EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RelativeHumidity,
                    EnvironmentalObservationKind.AtmosphericPressure, EnvironmentalObservationKind.WindSpeed,
                    EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.CloudCover],
                MetadataValue = 255,
                MetadataScale = 1,
                MetadataInset = 4,
                MetadataLineSpacing = 2,
                ImageCircleValue = 96,
                CardinalValue = 255,
                CardinalScale = 2,
                RecipeVersion = "projected-scene-annotation-v3",
                OutputVariant = "w6-annotated"
            }), ["combined-preview", "projected-scene"]);
        var oldWeather = new CaptureProcessingStepConfig("WeatherCloudOverlay", "weather-overlay", 80,
            SerializeLegacyOptions(new WeatherCloudOverlayProcessingStepOptions
            {
                RecipeVersion = "weather-cloud-overlay-v1",
                OutputVariant = "w6-weather-overlay",
                LineThickness = 1,
                DrawLabels = true,
                MaximumLabelCharacters = 32
            }), ["sky-annotation", "cloud"], Required: false);
        var currentStorage = current.Pipeline.Steps.Single(static step => step.Id == "storage");
        var oldStorage = currentStorage with
        {
            DependsOn = ["projected-scene", "calibration", "calibrated-preview", "rolling", "combined-preview",
                "quality", "cloud", "sky-annotation", "weather-overlay"]
        };
        var currentTelemetry = current.Pipeline.Steps.Single(static step => step.Id == "telemetry");
        var oldTelemetry = currentTelemetry with
        {
            DependsOn = [.. oldStorage.DependsOn!, "storage"]
        };
        var oldConfig = current with
        {
            Pipeline = current.Pipeline with { Steps = [.. prefix, oldAnnotation, oldWeather, oldStorage, oldTelemetry] }
        };
        using var provider = CreateProvider();
        var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(oldConfig);
        try
        {
            var hashes = graph.Nodes.ToDictionary(static node => node.Id, static node => node.LegacyPlanSha256);
            Assert.IsNotNull(hashes["cloud"]);
            Assert.IsNotNull(hashes["projected-scene"]);
            Assert.IsNotNull(hashes["calibration"]);
            Assert.IsNotNull(hashes["calibrated-preview"]);
            Assert.IsNotNull(hashes["rolling"]);
            Assert.IsNotNull(hashes["combined-preview"]);
            Assert.IsNotNull(hashes["quality"]);
            Assert.IsNotNull(hashes["sky-annotation"]);
            Assert.IsNotNull(hashes["weather-overlay"]);
            Assert.IsNotNull(hashes["storage"]);
            Assert.IsNotNull(hashes["telemetry"]);
            foreach (var expected in Pre433HistoricalPlanSha256s)
                Assert.AreEqual(expected.Value, hashes[expected.Key], $"{expected.Key}:{hashes[expected.Key]}");
            var currentGraph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(current);
            try
            {
                Assert.IsNull(currentGraph.Nodes.Single(static node => node.Id == "cloud").LegacyPlanSha256);
            }
            finally
            {
                currentGraph.DisposeSteps();
            }
        }
        finally
        {
            graph.DisposeSteps();
        }
    }

    private static async Task<CameraModuleConfig> LoadAsync(string fileName)
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, fileName),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = Location
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static System.Text.Json.JsonElement SerializeLegacyOptions<T>(T options)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(options)!.AsObject();
        node.Remove("enabled");
        node.Remove("Enabled");
        return System.Text.Json.JsonSerializer.SerializeToElement(node);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            })
            .Build());
        return services.BuildServiceProvider();
    }
}
