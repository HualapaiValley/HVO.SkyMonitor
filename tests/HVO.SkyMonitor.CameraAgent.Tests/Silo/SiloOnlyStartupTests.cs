using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Hosting")]
public sealed class SiloOnlyStartupTests
{
    [TestMethod]
    public void MissingLocalBindingRefusesStartup()
    {
        var configuration = new ConfigurationManager();
        var failure = Assert.ThrowsExactly<InvalidOperationException>(configuration.ValidateCameraAgentStorageConfiguration);
        StringAssert.Contains(failure.Message, "requires CameraAgent:Silo settings", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("Storage:Root")]
    [DataRow("StorageRoot")]
    [DataRow("RetentionSweepIntervalMinutes")]
    [DataRow("DiskPressureThresholdPercent")]
    [DataRow("DiskPressureRecoveryPercent")]
    [DataRow("DiskPressureRetentionDays")]
    public void FilesystemSettingsRefuseStartupEvenWithValidSiloBinding(string key)
    {
        var configuration = TestBinding();
        configuration["CameraAgent:" + key] = "10";
        var failure = Assert.ThrowsExactly<InvalidOperationException>(configuration.ValidateCameraAgentStorageConfiguration);
        StringAssert.Contains(failure.Message, "retired filesystem storage setting", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LocalBindingIsRequiredWithOrWithoutArchive(bool archive)
    {
        var configuration = TestBinding();
        if (archive)
            configuration["CameraAgent:SiloArchive:ReplicationEnabled"] = "true";
        configuration.ValidateCameraAgentStorageConfiguration();
        using var services = BuildServices(configuration);
        Assert.AreEqual(configuration["CameraAgent:Silo:StoreId"], services.GetRequiredService<SiloInstalledSettings>().Binding.StoreId);
    }

    [TestMethod]
    [DataRow("storageRoot")]
    [DataRow("retentionDays")]
    [DataRow("queueForUpload")]
    [DataRow("policies")]
    public void StorageNodesRejectRetiredFilesystemOptions(string key)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, int> { [key] = 7 });
        _ = Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<SiloCaptureProcessingStepOptions>(json, JsonSerializerOptions.Web));
    }

    private static ServiceProvider BuildServices(ConfigurationManager configuration) => new ServiceCollection()
        .AddSingleton<IConfiguration>(configuration)
        .AddCameraAgentInfrastructure(configuration)
        .BuildServiceProvider();

    private static ConfigurationManager TestBinding()
    {
        var instance = Guid.NewGuid();
        var storeId = CameraAgentSiloBinding.StoreIdFor(instance);
        var bucket = CameraAgentSiloBinding.BucketFor(instance);
        var binding = new SiloStorageBinding(instance.ToString("D"), storeId, bucket);
        return new ConfigurationManager
        {
            ["CameraAgent:Silo:StateContract"] = CameraAgentSiloBinding.StateContract,
            ["CameraAgent:Silo:StoreId"] = storeId,
            ["CameraAgent:Silo:Bucket"] = bucket,
            ["CameraAgent:Silo:StorageBindingId"] = binding.BindingId,
            ["CameraAgent:Silo:WriterGeneration"] = "1",
            ["CameraAgent:Silo:Endpoint"] = "http://127.0.0.1",
            ["CameraAgent:Silo:AccessKey"] = "testfixturewriter",
            ["CameraAgent:Silo:SecretKey"] = "testfixturesecret"
        };
    }
}
