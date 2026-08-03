using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Modules.Zwo;

public sealed class ZwoAsiCameraModuleOptions
{
    [JsonRequired]
    public string LibraryPathEnvironmentVariable { get; init; } = string.Empty;

    [JsonRequired]
    public string CameraSerialEnvironmentVariable { get; init; } = string.Empty;

    [JsonRequired]
    public string ExpectedModel { get; init; } = string.Empty;

    public long Offset { get; init; } = 1;

    public long UsbBandwidth { get; init; } = 40;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(5);

    public TimeSpan CaptureTimeoutMargin { get; init; } = TimeSpan.FromSeconds(10);

    public bool MonoBin { get; init; }

    public bool HardwareBin { get; init; }
}
