using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal static class ZwoCameraStarter
{
    private static readonly JsonSerializerOptions SampleJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static NamedCameraEquipment Create(string templateId)
    {
        var resource = templateId switch
        {
            "asi676mc" => "cameraagent.zwo-asi676mc.sample.json",
            "asi178mc" => "cameraagent.zwo-asi178mc.sample.json",
            _ => throw new ArgumentException("Unsupported camera starter template.", nameof(templateId))
        };
        using var stream = typeof(ZwoCameraStarter).Assembly.GetManifestResourceStream(
            $"HVO.SkyMonitor.CameraAgent.{resource}")
            ?? throw new InvalidOperationException("Camera starter template is unavailable.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var module = root.GetProperty("module").Deserialize<CameraModuleDescriptor>(SampleJson)
            ?? throw new InvalidOperationException("Camera starter module is invalid.");
        var rig = root.GetProperty("rig");
        var sensor = rig.GetProperty("sensor").Deserialize<SensorProfile>(SampleJson)
            ?? throw new InvalidOperationException("Camera starter sensor is invalid.");
        var readout = rig.GetProperty("readout").Deserialize<SensorReadoutProfile>(SampleJson)
            ?? throw new InvalidOperationException("Camera starter readout is invalid.");
        if (module.Type != "ZwoAsi" || module.Options is null)
            throw new InvalidOperationException("Camera starter module is invalid.");
        return new NamedCameraEquipment(module, sensor, readout);
    }
}
