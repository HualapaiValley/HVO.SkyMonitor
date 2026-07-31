namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralArtifactRetentionOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int BatchSize { get; set; } = 25;
    public TimeSpan StalePendingThreshold { get; set; } = TimeSpan.FromMinutes(15);
}
