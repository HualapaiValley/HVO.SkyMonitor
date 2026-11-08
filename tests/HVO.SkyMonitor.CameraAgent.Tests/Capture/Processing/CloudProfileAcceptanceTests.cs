using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CloudProfileAcceptanceTests
{
    private static readonly string[] CloudInput = ["$raw"];
    [TestMethod]
    public async Task ShippedVirtualSkyProfileEnablesOptionalCloudEvidenceAndRetainedLayers()
    {
        var config = await LoadAsync().ConfigureAwait(false);
        using var provider = ProcessingGraphOperationsTests.CreateProvider(Path.GetTempPath());
        var plan = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().PreviewPlan(config);
        var cloud = plan.EffectiveNodes.SingleOrDefault(node => node.Alias == "CloudAssessment");
        Assert.IsNotNull(cloud, "The shipped VirtualSky profile must include image-derived cloud assessment.");
        var layer = plan.EffectiveNodes.Single(node => node.Alias == "CloudPresentationLayer");
        foreach (var node in new[] { cloud, layer })
        {
            Assert.IsFalse(node.Required);
            Assert.AreEqual(CaptureProcessingPersistenceMode.DurableLocal, node.Publication!.Persistence);
        }
        CollectionAssert.AreEqual(CloudInput, cloud.Dependencies!.ToArray());
        CollectionAssert.AreEqual(new[] { cloud.Id }, layer.Dependencies!.ToArray());
        Assert.AreEqual("references/cloud/clear-reference.manifest.json", config.Pipeline.Steps.Single(step => step.Type == "CloudAssessment").Options!.Value.GetProperty("clearReferenceManifestPath").GetString());
        Assert.AreEqual(config.Rig.Sensor.WidthPixels, layer.Options!.Value.GetProperty("widthPixels").GetInt32());
        Assert.AreEqual(config.Rig.Sensor.HeightPixels, layer.Options!.Value.GetProperty("heightPixels").GetInt32());
        Assert.IsTrue(layer.Options.Value.GetProperty("requireQuantifiedAssessment").GetBoolean());
        foreach (var alias in new[] { "OverlayManifest", "PresentationMaterializer" })
            CollectionAssert.Contains(plan.EffectiveNodes.Single(node => node.Alias == alias).Dependencies!.ToArray(), layer.Id);
        foreach (var node in plan.EffectiveNodes.Where(node => node.Alias is "Storage" or "Telemetry" or "RollingCombination" or "Preview"))
            CollectionAssert.DoesNotContain(node.Dependencies!.ToArray(), layer.Id);
    }

    internal static Task<CameraModuleConfig> LoadAsync() => new FileCameraAgentConfigurationLoader(
        Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
            AgentId = "cloud-acceptance",
            Observatory = new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance).LoadAsync(CancellationToken.None);
}
