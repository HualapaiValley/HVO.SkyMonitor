namespace HVO.SkyMonitor.LogicHost.Configuration;

/// <summary>
/// Configuration options for MinIO object storage connectivity.
/// </summary>
public sealed class MinioOptions
{
    /// <summary>
    /// Hostname or IP address of the MinIO server.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// TCP port for the MinIO server (default 9000).
    /// </summary>
    public int Port { get; set; } = 9000;

    /// <summary>
    /// When true, the MinIO client negotiates TLS.
    /// </summary>
    public bool UseSsl { get; set; }

    /// <summary>
    /// Optional region to send with requests.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Access key for authentication.
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Secret key for authentication.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Default bucket used by diagnostics endpoints.
    /// </summary>
    public string DefaultBucket { get; set; } = "skymonitor-diagnostics";
}
