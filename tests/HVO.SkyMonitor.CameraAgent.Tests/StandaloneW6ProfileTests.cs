using System.ComponentModel.DataAnnotations;
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
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StandaloneW6ProfileTests
{
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
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var preview = factory.PreviewPlan(configuration);

        var rigSha256 = CameraRigProfileIdentity.ComputeSha256(configuration.Rig);
        Assert.AreEqual(
            "7191D84F4BA368482546AB6A09FBFEDD2F273BD626FABE6F3156C55C54DFCA9B",
            rigSha256,
            rigSha256);
        var processingSha256 = RawCaptureDescriptorFactory.CreateProcessingProfile(configuration).Sha256;
        Assert.AreEqual(
            "FE3EA5C9A5FB0605FA7522C7271E39FE32B0C8B44178BFF6A5625956C3AFAACE",
            processingSha256,
            processingSha256);
        Assert.AreEqual(
            "6A5E298C74CA520E3EB3EEE6CE67D30A8BDFC1340ACC2FE1AA5DDDF0F6F9CFDD",
            CaptureScheduleContract.ComputeSha256(configuration.Schedule!));
        var localProfileSha256 = LocalCaptureProfileContract.ComputeSha256(
            LocalCaptureProfileDefinition.CreateForConfiguration(configuration, configuration.Schedule!));
        Assert.AreEqual(
            "6081518D9D7349F2333671C28AFAB11AF7ACEA63276AA990F54250632BCD54E3",
            localProfileSha256,
            localProfileSha256);
        Assert.AreEqual(
            "9B21B31E30070315093EE6F53727813840CDF3F75008838342B5446A4F488069",
            preview.DesiredSha256,
            preview.DesiredSha256);
        Assert.AreEqual(
            "01BDA19E83DB375F0A87CE13A1A8395BAFFDC31A8AEAF17BD72001A36B437386",
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
        var graph = factory.CreateGraph(configuration);
        try
        {
            var shared = graph.SharedPlan!;
            var scene = shared.Nodes.Single(static node => node.Definition.Id == "scene-presentation").Definition;
            Assert.AreEqual(PresentationLayerProducers.SceneProducerVersion, scene.StepVersion);
            Assert.IsTrue(scene.Outputs.All(static output =>
                output.Recipe is
                {
                    Name: PresentationProcessingProducts.LayerRecipeName,
                    SemanticVersion: "1.0.0",
                    ImplementationVersion: PresentationLayerProducers.SceneProducerVersion
                }));
            var manifest = shared.Nodes.Single(static node => node.Definition.Id == "overlay-manifest").Definition;
            Assert.AreEqual("overlay-manifest-v1", manifest.Outputs[0].Recipe!.ImplementationVersion);
            Assert.IsTrue(manifest.Inputs.Count(static input =>
                input.RecipeNames.Contains(PresentationProcessingProducts.LayerRecipeName)) >= 5);
            var materializer = shared.Nodes.Single(static node => node.Definition.Id == "presentation-materializer").Definition;
            Assert.AreEqual("typed-presentation-materialization-v1", materializer.StepVersion);
            Assert.AreEqual(
                "typed-presentation-materialization-v1",
                materializer.Outputs[0].Recipe!.ImplementationVersion);
            CollectionAssert.AreEqual(
                new[]
                {
                    $"presentation-compositor/{PresentationLayerCompositor.AlgorithmVersion}",
                    $"{PresentationMaterializationExecutor.PackedEncoderName}/{PresentationMaterializationExecutor.PackedEncoderVersion}"
                },
                materializer.Outputs[0].Algorithms.Select(static algorithm =>
                    $"{algorithm.Name}/{algorithm.Version}").ToArray());
        }
        finally
        {
            graph.DisposeSteps();
        }
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
            "44A1253961E3BE879BF69C27DC57F24DD492FE4416232913EEB99E6273D7A7E5",
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
    public async Task LayeredGraphAcceptsOmittedCloudProcessingAndLegacyLayerKinds()
    {
        var configuration = await LoadAsync("cameraagent.standalone-w6.json").ConfigureAwait(false);
        var removed = new HashSet<string>(["cloud", "cloud-presentation"], StringComparer.Ordinal);
        var steps = configuration.Pipeline!.Steps
            .Where(step => !removed.Contains(step.Id ?? string.Empty))
            .Select(step => step with
            {
                DependsOn = (step.DependsOn ?? []).Where(dependency => !removed.Contains(dependency)).ToArray()
            })
            .ToArray();
        using var provider = CreateProvider();

        var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(
            configuration with { Pipeline = configuration.Pipeline with { Steps = steps } });

        Assert.HasCount(12, graph.Nodes);
        Assert.IsFalse(graph.Nodes.Any(node => removed.Contains(node.Id)));
        Assert.IsTrue(PresentationLayerKinds.Matches("scene-annotation", "star-annotations"));
        Assert.IsTrue(PresentationLayerKinds.Matches("scene-annotation", "scene-cardinals"));
        Assert.IsTrue(PresentationLayerKinds.Matches("scene-annotation", "scene-image-circle"));
        Assert.IsTrue(PresentationLayerKinds.Matches("scene-cardinals", "cardinal-directions"));
        Assert.IsTrue(PresentationLayerKinds.Matches("scene-image-circle", "image-circle"));
        Assert.IsFalse(PresentationLayerKinds.Matches("cardinal-directions", "scene-annotation"));
        Assert.IsTrue(PresentationLayerKinds.Matches("constellations", "scene-constellations"));
        Assert.IsTrue(PresentationLayerKinds.Matches("corner-annotations", "environment"));
        Assert.IsFalse(PresentationLayerKinds.Matches("environment", "constellations"));
        Assert.AreEqual(19, PresentationLayerKinds.ResolveZOrder("scene-annotation", "scene-image-circle", 20));
        Assert.AreEqual(20, PresentationLayerKinds.ResolveZOrder("scene-annotation", "scene-annotation", 20));
        Assert.AreEqual(21, PresentationLayerKinds.ResolveZOrder("scene-annotation", "scene-cardinals", 20));
        Assert.AreEqual(-1025, PresentationLayerKinds.ResolveZOrder("scene-annotation", "scene-image-circle", -1024));
        Assert.AreEqual(-1024, PresentationLayerKinds.ResolveZOrder("scene-annotation", "scene-annotation", -1024));
        Assert.AreEqual(1024, PresentationLayerKinds.ResolveZOrder("scene-annotation", "scene-annotation", 1024));
        Assert.AreEqual(1025, PresentationLayerKinds.ResolveZOrder("scene-annotation", "scene-cardinals", 1024));
        Assert.IsTrue(new OverlayManifestProcessingStepOptions
        {
            Layers =
            [
                new() { Kind = "scene-cardinals" },
                new() { Kind = "cardinal-directions" }
            ]
        }.Validate(new ValidationContext(new object())).Any());
        graph.DisposeSteps();
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
    public async Task MaterializerLayerSelectionSetHasOrderIndependentSharedIdentity()
    {
        var configuration = await LoadAsync("cameraagent.standalone-w6.json").ConfigureAwait(false);
        using var provider = CreateProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();

        CameraModuleConfig WithKinds(params string[] kinds)
        {
            var steps = configuration.Pipeline.Steps.Select(step => step.Id == "presentation-materializer"
                ? step with
                {
                    Options = AddKinds(step.Options!.Value, kinds)
                }
                : step).ToArray();
            return configuration with { Pipeline = configuration.Pipeline with { Steps = steps } };
        }

        var first = factory.CreateGraph(WithKinds("environment", "scene-annotation"));
        var second = factory.CreateGraph(WithKinds("scene-annotation", "environment"));
        var legacyAnnotation = factory.CreateGraph(WithKinds("star-annotations"));
        var groupedAnnotation = factory.CreateGraph(WithKinds("scene-annotation"));
        var legacyCardinals = factory.CreateGraph(WithKinds("cardinal-directions"));
        var sceneCardinals = factory.CreateGraph(WithKinds("scene-cardinals"));
        try
        {
            Assert.AreEqual(first.SharedPlan!.PlanIdentitySha256, second.SharedPlan!.PlanIdentitySha256);
            Assert.AreNotEqual(
                legacyAnnotation.SharedPlan!.PlanIdentitySha256,
                groupedAnnotation.SharedPlan!.PlanIdentitySha256);
            Assert.AreEqual(
                legacyCardinals.SharedPlan!.PlanIdentitySha256,
                sceneCardinals.SharedPlan!.PlanIdentitySha256);
        }
        finally
        {
            first.DisposeSteps();
            second.DisposeSteps();
            legacyAnnotation.DisposeSteps();
            groupedAnnotation.DisposeSteps();
            legacyCardinals.DisposeSteps();
            sceneCardinals.DisposeSteps();
        }

        static System.Text.Json.JsonElement AddKinds(System.Text.Json.JsonElement options, string[] kinds)
        {
            var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                options.GetRawText())!;
            values["enabledLayerKinds"] = System.Text.Json.JsonSerializer.SerializeToElement(kinds);
            return System.Text.Json.JsonSerializer.SerializeToElement(values);
        }
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
