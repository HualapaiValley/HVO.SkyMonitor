namespace HVO.SkyMonitor.LogicHost.Models.Diagnostics;

/// <summary>
/// Request payload for MinIO diagnostics.
/// </summary>
public sealed class StorageDiagnosticsRequest
{
    public string? Bucket { get; set; }

    public string? ObjectName { get; set; }

    public string Content { get; set; } = string.Empty;
}
