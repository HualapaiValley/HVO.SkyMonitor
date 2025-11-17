namespace HVO.SkyMonitor.LogicHost.Models.Diagnostics;

/// <summary>
/// Response describing the stored object echoed back from MinIO.
/// </summary>
public sealed class StorageDiagnosticsResponse
{
    public string Bucket { get; set; } = string.Empty;

    public string ObjectName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}
