using System.ComponentModel;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.RandomImage;

public sealed class RandomImageCameraModuleOptions
{
    [DefaultValue("Noise")]
    public string Pattern { get; init; } = "Noise";

    [DefaultValue(42)]
    public int Seed { get; init; } = 42;
}
