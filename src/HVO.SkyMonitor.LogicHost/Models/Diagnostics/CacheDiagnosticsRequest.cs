namespace HVO.SkyMonitor.LogicHost.Models.Diagnostics;

/// <summary>
/// Request payload for cache diagnostics.
/// </summary>
public sealed class CacheDiagnosticsRequest
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public int? ExpirationSeconds { get; set; }
}
