using HVO.SkyMonitor.CameraAgent.Simulator.Security;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Data;

public sealed class ApplicationUserApiKey
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;

    public ApplicationUser User { get; set; } = default!;

    public string KeyHash { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public ApiKeyAccessLevel AccessLevel { get; set; } = ApiKeyAccessLevel.Read;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ExpiresUtc { get; set; }

    public DateTimeOffset? LastUsedUtc { get; set; }

    public string? CreatedBy { get; set; }
}
