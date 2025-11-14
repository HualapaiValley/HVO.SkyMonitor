using HVO.SkyMonitor.Common.Security;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Represents an API key for programmatic access.
/// </summary>
public class ApiKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string HashedKey { get; set; } = string.Empty;
    public ApiKeyAccessLevel AccessLevel { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;

    // Foreign key to User
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
}
