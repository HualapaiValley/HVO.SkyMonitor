using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Options;

public sealed class CameraAgentHostOptions
{
    [Required]
    [MinLength(1)]
    public string ConfigFilePath { get; init; } = "cameraagent.sample.json";

    [Range(1, 1440)]
    public int RetentionSweepIntervalMinutes { get; init; } = 30;

    [Required]
    public ObservatoryLocation Observatory { get; init; } = new(0, 0, 0, "UTC");
}
