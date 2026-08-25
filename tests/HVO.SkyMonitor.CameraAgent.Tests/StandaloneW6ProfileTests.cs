using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
            "7B395B8DD577024944C263E1EA642BB472E3144110FF3FE75DBBCE67B187F172",
            rigSha256,
            rigSha256);
        Assert.AreEqual(
            "DDD63791961E018687C7E6FA095CA17EFB88F1CC5AE5861D58690129FB9A27B2",
            CaptureScheduleContract.ComputeSha256(configuration.Schedule!));
        var localProfileSha256 = LocalCaptureProfileContract.ComputeSha256(
            LocalCaptureProfileDefinition.CreateForConfiguration(configuration, configuration.Schedule!));
        Assert.AreEqual(
            "7A6869542F282F1EAF0F461B58E5BCCE046292BA415C1BF3A3B6C767D71B2653",
            localProfileSha256,
            localProfileSha256);
        Assert.AreEqual(
            "2453844C3A527CDF894FE128C0B7C81572315A217AFB4A46464DFED0A2F69EED",
            preview.DesiredSha256);
        Assert.AreEqual(
            "F96FD92667C1F4CA129C8138B0B7C3F2334D5945F1E368EAE8C0C289F9711226",
            preview.EffectiveSha256);
        Assert.HasCount(11, preview.EffectiveNodes);
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
            "DBA167BAF1AAF12FDEBF9BAC029D72BBE0943CAE0AB00C9BB187D70DB0A78FEF",
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
