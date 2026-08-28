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
            "40D49A5166B07FACECA41173263DA75DA8A9A7E28480CB361BDA34191994DB61",
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
