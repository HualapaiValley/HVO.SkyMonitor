using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DeploymentAsi676ProfileTests
{
    private static readonly ObservatoryLocation Location = new(35.5599378, -113.9119818, 520, "America/Phoenix");
    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [TestMethod]
    public async Task Profiles_PinFullResolutionCadenceAndRecurringScenario()
    {
        foreach (var profile in new[]
        {
            (File: "allsky01.virtual-asi676mc.full.json", Format: CameraPixelFormat.BayerRggb16),
            (File: "hvo-edge-01.virtual-asi676mm.full.json", Format: CameraPixelFormat.Mono16)
        })
        {
            var configuration = await LoadAsync(profile.File).ConfigureAwait(false);
            var layout = SensorReadoutResolver.Resolve(configuration.Rig.Sensor, configuration.Rig.Readout!).Layout;
            var options = JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(
                configuration.Module.Options!.Value, StrictJsonOptions)!;

            Assert.AreEqual(3552, layout.Width);
            Assert.AreEqual(3552, layout.Height);
            Assert.AreEqual(7104, layout.StrideBytes);
            Assert.AreEqual(25_233_408L, layout.ByteLength);
            Assert.AreEqual(profile.Format, layout.PixelFormat);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Rig.Pipeline.DayExposure);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Rig.Pipeline.NightExposure);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Rig.Pipeline.CaptureInterval);
            Assert.AreEqual(CaptureCadenceMode.MinimumStartInterval, configuration.Rig.Pipeline.CadenceMode);
            Assert.IsNotNull(configuration.Schedule);
            Assert.HasCount(1, configuration.Schedule.SetpointProfiles);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Schedule.SetpointProfiles[0].Exposure);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Schedule.SetpointProfiles[0].CaptureInterval);
            Assert.IsNotNull(options.TransientScenario?.Recurrence);
            Assert.AreEqual(4096, options.TransientScenario.Recurrence.EventCount);
            Assert.HasCount(2, options.TransientScenario.Recurrence.Profiles);
            options.TransientScenario.ValidateSensorBounds(layout.Width, layout.Height);
        }
    }

    [TestMethod]
    public async Task Profiles_DeclareExplicitProcessingGraphsAndDistinctIdentities()
    {
        var color = await LoadAsync("allsky01.virtual-asi676mc.full.json").ConfigureAwait(false);
        var mono = await LoadAsync("hvo-edge-01.virtual-asi676mm.full.json").ConfigureAwait(false);
        using var provider = CreateProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var colorPlan = factory.PreviewPlan(color);
        var monoPlan = factory.PreviewPlan(mono);

        Assert.HasCount(8, colorPlan.EffectiveNodes);
        Assert.HasCount(8, monoPlan.EffectiveNodes);
        var colorPipeline = color.Pipeline ?? throw new AssertFailedException("Color pipeline is required.");
        var monoPipeline = mono.Pipeline ?? throw new AssertFailedException("Mono pipeline is required.");
        Assert.IsTrue(colorPipeline.Steps.All(static step => step.DependsOn is { Count: > 0 }));
        Assert.IsTrue(monoPipeline.Steps.All(static step => step.DependsOn is { Count: > 0 }));
        Assert.AreNotEqual(CameraRigProfileIdentity.ComputeSha256(color.Rig), CameraRigProfileIdentity.ComputeSha256(mono.Rig));
        Assert.IsFalse(color.Module.Options!.Value.GetRawText().Contains("meteor", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(color.Module.Options.Value.GetRawText().Contains("fireball", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(mono.Module.Options!.Value.GetRawText().Contains("meteor", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(mono.Module.Options.Value.GetRawText().Contains("fireball", StringComparison.OrdinalIgnoreCase));
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CameraAgent:RawIngressRoot"] = Path.GetTempPath() })
            .Build());
        return services.BuildServiceProvider();
    }
}
