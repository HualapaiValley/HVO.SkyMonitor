using System;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.Common.Security;

/// <summary>
/// Shared API key entity used across all services.
/// </summary>
public sealed class ApiKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string UserId { get; set; } = string.Empty;

    public IdentityUser? User { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public ApiKeyAccessLevel AccessLevel { get; set; } = ApiKeyAccessLevel.Read;

    public string HashedKey { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ExpiresUtc { get; set; }

    public DateTimeOffset? LastUsedUtc { get; set; }

    public string? CreatedBy { get; set; }
}
