namespace HVO.SkyMonitor.Models.Diagnostics;

/// <summary>
/// Response payload describing cache round-trip verification.
/// </summary>
public sealed class CacheDiagnosticsResponse
{
    public string Key { get; set; } = string.Empty;

    public string WrittenValue { get; set; } = string.Empty;

    public string? RetrievedValue { get; set; }

    public bool CacheHit { get; set; }
}
