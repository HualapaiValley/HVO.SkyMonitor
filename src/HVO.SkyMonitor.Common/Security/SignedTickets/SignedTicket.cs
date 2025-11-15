namespace HVO.SkyMonitor.Common.Security.SignedTickets;

/// <summary>
/// Represents a signed ticket for time-limited, HMAC-protected access to specific endpoints.
/// Used for high-volume media endpoints (frames, images) to avoid per-request bearer token overhead.
/// </summary>
public sealed class SignedTicket
{
    /// <summary>
    /// Schema version for future compatibility.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// UTC timestamp when this ticket expires.
    /// </summary>
    public DateTime ExpiresUtc { get; set; }

    /// <summary>
    /// Subject identifier (user ID or system account ID).
    /// Used for audit trails and potential revocation.
    /// </summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// HTTP method this ticket is valid for (GET, POST, etc.).
    /// Prevents CSRF by binding to specific method.
    /// </summary>
    public string HttpMethod { get; set; } = "GET";

    /// <summary>
    /// Normalized request path (e.g., "/api/v1.0/frame/latest").
    /// Case-insensitive, no trailing slash.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Canonical query string (sorted alphabetically, URL-decoded for signature).
    /// Optional - use empty string if no query parameters.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Optional scope bits for additional authorization (comma-separated).
    /// Example: "api,camera,read"
    /// </summary>
    public string Scopes { get; set; } = string.Empty;
}
