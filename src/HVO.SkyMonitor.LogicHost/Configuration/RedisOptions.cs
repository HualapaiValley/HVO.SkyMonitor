namespace HVO.SkyMonitor.LogicHost.Configuration;

/// <summary>
/// Redis cache configuration for diagnostics testing.
/// </summary>
public sealed class RedisOptions
{
    /// <summary>
    /// StackExchange.Redis connection string (e.g., "localhost:6379").
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// Instance prefix for distributed cache keys.
    /// </summary>
    public string InstanceName { get; set; } = "skymonitor:";
}
