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
            "7B395B8DD577024944C263E1EA642BB472E3144110FF3FE75DBBCE67B187F172",
            rigSha256,
            rigSha256);
        var processingSha256 = RawCaptureDescriptorFactory.CreateProcessingProfile(configuration).Sha256;
        Assert.AreEqual(
            "66EA97A4D415905A9DB7E197E3A465AC6C1555CDA680CD303B90DF171A1AF486",
            processingSha256,
            processingSha256);
        Assert.AreEqual(
            "DDD63791961E018687C7E6FA095CA17EFB88F1CC5AE5861D58690129FB9A27B2",
            CaptureScheduleContract.ComputeSha256(configuration.Schedule!));
        var localProfileSha256 = LocalCaptureProfileContract.ComputeSha256(
            LocalCaptureProfileDefinition.CreateForConfiguration(configuration, configuration.Schedule!));
        Assert.AreEqual(
            "0B34090E5589D72D085597DAB3703C73566EA88AA48E8ACDFEF599C6A1D983A5",
            localProfileSha256,
            localProfileSha256);
        Assert.AreEqual(
            "86053709D0818A19E574583EB30FB6BACFEB40AC2F5C0E3F491F7A580D331B3E",
            preview.DesiredSha256,
            preview.DesiredSha256);
        Assert.AreEqual(
            "40018B0AF40F40B641FFF7138C56559B28F4C57C00500D59A696B46B68FFFCA8",
            preview.EffectiveSha256,
            preview.EffectiveSha256);
        Assert.HasCount(14, preview.EffectiveNodes);
        Assert.IsFalse(preview.EffectiveNodes.Any(static node => node.Id is "sky-annotation" or "weather-overlay"));
        Assert.IsFalse(preview.EffectiveNodes.Single(static node => node.Id == "cloud").Required);
        Assert.IsFalse(preview.EffectiveNodes.Single(static node => node.Id == "cloud-presentation").Required);
        CollectionAssert.DoesNotContain(
            preview.EffectiveNodes.Single(static node => node.Id == "storage").Dependencies!.ToArray(),
            "cloud-presentation");
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
            "3233765432377F454526BAF795268FC9A73B3DEB8F0800A0D21BD652006E500C",
            CameraRigProfileIdentity.ComputeSha256(configuration.Rig));
        var processingSha256 = RawCaptureDescriptorFactory.CreateProcessingProfile(configuration).Sha256;
        Assert.AreEqual(
            "F5BA5B24130B8C2359E9518DBA7C4DC3E899FB2FDD826FA46269F1A2AC9DDC0B",
            processingSha256,
            processingSha256);
        Assert.AreEqual(
            "355D9C9A53CB600E6F1798109A4BFAC8D539F6A9B0A1DF9A9D44A9EF6283ED01",
            CaptureScheduleContract.ComputeSha256(configuration.Schedule!));
        Assert.AreEqual(
            "5185EEA24AE697CD841FFF787F96D886AF8EF5DF08A162098671F6B6EBFCBDB4",
            LocalCaptureProfileContract.ComputeSha256(
                LocalCaptureProfileDefinition.CreateForConfiguration(configuration, configuration.Schedule!)));
        Assert.AreEqual(
            "A5F687646DFBC2BFE5716E45AA2ED9028901EAE940D2A9FC8EC70F98C283512D",
            preview.DesiredSha256);
        Assert.AreEqual(
            "D234D4CF0B9447DDAE2E237EE5024B1756BEA591D9FA2C4D1DD0CF1DD560EA21",
            preview.EffectiveSha256,
            preview.EffectiveSha256);
        Assert.HasCount(4, preview.EffectiveNodes);
        Assert.IsFalse(preview.EffectiveNodes.Any(static node =>
            node.Alias is "Calibration" or "RollingCombination"));
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
