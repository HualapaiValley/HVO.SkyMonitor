namespace HVO.SkyMonitor.Common.Security.SignedTickets;

/// <summary>
/// Configuration options for signed ticket generation and validation.
/// </summary>
public sealed class SignedTicketOptions
{
    /// <summary>
    /// HMAC secret key (minimum 256 bits / 32 bytes recommended).
    /// MUST be stored securely (Key Vault, secrets manager).
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Default ticket TTL in seconds (default: 300 = 5 minutes).
    /// Keep short for media endpoints - clients can request new tickets easily.
    /// </summary>
    public int DefaultTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum allowed clock skew in seconds (default: 30).
    /// Tolerates time differences between distributed servers.
    /// </summary>
    public int MaxClockSkewSeconds { get; set; } = 30;

    /// <summary>
    /// List of path prefixes where signed URLs are allowed.
    /// Example: ["/api/v1.0/frame/", "/api/v1.0/image/"]
    /// If empty, all paths are allowed (not recommended).
    /// </summary>
    public string[] AllowedPaths { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Whether to validate that requested paths match AllowedPaths.
    /// </summary>
    public bool EnforceAllowedPaths { get; set; } = true;
}
