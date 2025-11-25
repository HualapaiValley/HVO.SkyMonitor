using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static class DeviceRegistrationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}
