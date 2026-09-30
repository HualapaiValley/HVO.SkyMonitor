using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Scheduling;

[TestClass]
[TestCategory("Unit")]
public sealed class ZwoCameraStarterTests
{
    [TestMethod]
    [DataRow("asi676mc", "ASI676MC", 3552, 12, 1)]
    [DataRow("asi178mc", "ASI178MC", 3096, 14, 10)]
    public void StarterUsesCheckedInPhysicalDefinition(string id, string expectedModel, int width, int depth, int offset)
    {
        var starter = ZwoCameraStarter.Create(id);
        Assert.AreEqual("ZwoAsi", starter.Module.Type);
        Assert.AreEqual(width, starter.Sensor.WidthPixels);
        Assert.AreEqual(depth, starter.Readout!.SampleDepthBits);
        var options = starter.Module.Options!.Value;
        Assert.AreEqual(expectedModel, options.GetProperty("expectedModel").GetString());
        Assert.AreEqual(offset, options.GetProperty("offset").GetInt32());
        Assert.AreEqual("HVO_ZWO_SDK_LIBRARY", options.GetProperty("libraryPathEnvironmentVariable").GetString());
        Assert.AreEqual("HVO_ZWO_CAMERA_SERIAL", options.GetProperty("cameraSerialEnvironmentVariable").GetString());
        Assert.IsFalse(JsonSerializer.Serialize(starter).Contains("private-camera-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnknownTemplateIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ZwoCameraStarter.Create("asi120mm-mini"));
        Assert.ThrowsExactly<ArgumentException>(() => ZwoCameraStarter.Create("../../cameraagent.sample.json"));
    }
}
