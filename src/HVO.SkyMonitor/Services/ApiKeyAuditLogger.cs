using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Services;

/// <summary>
/// Provides structured audit logging for API key lifecycle events.
/// </summary>
internal interface IApiKeyAuditLogger
{
    void LogKeyCreated(string keyId, string userId, string displayName, string accessLevel, DateTimeOffset? expiresUtc);
    void LogKeyDeleted(string keyId, string userId, string displayName);
    void LogKeyActivated(string keyId, string userId, string displayName);
    void LogKeyDeactivated(string keyId, string userId, string displayName);
    void LogKeyRotated(string oldKeyId, string newKeyId, string userId, string displayName);
}

internal sealed class ApiKeyAuditLogger : IApiKeyAuditLogger
{
    private readonly ILogger<ApiKeyAuditLogger> _logger;

    public ApiKeyAuditLogger(ILogger<ApiKeyAuditLogger> logger)
    {
        _logger = logger;
    }

    public void LogKeyCreated(string keyId, string userId, string displayName, string accessLevel, DateTimeOffset? expiresUtc)
    {
        _logger.LogInformation(
            "API Key Created: KeyId={KeyId}, UserId={UserId}, DisplayName={DisplayName}, AccessLevel={AccessLevel}, ExpiresUtc={ExpiresUtc}",
            keyId, userId, displayName, accessLevel, expiresUtc?.ToString("O") ?? "Never");
    }

    public void LogKeyDeleted(string keyId, string userId, string displayName)
    {
        _logger.LogWarning(
            "API Key Deleted: KeyId={KeyId}, UserId={UserId}, DisplayName={DisplayName}",
            keyId, userId, displayName);
    }

    public void LogKeyActivated(string keyId, string userId, string displayName)
    {
        _logger.LogInformation(
            "API Key Activated: KeyId={KeyId}, UserId={UserId}, DisplayName={DisplayName}",
            keyId, userId, displayName);
    }

    public void LogKeyDeactivated(string keyId, string userId, string displayName)
    {
        _logger.LogWarning(
            "API Key Deactivated: KeyId={KeyId}, UserId={UserId}, DisplayName={DisplayName}",
            keyId, userId, displayName);
    }

    public void LogKeyRotated(string oldKeyId, string newKeyId, string userId, string displayName)
    {
        _logger.LogInformation(
            "API Key Rotated: OldKeyId={OldKeyId}, NewKeyId={NewKeyId}, UserId={UserId}, DisplayName={DisplayName}",
            oldKeyId, newKeyId, userId, displayName);
    }
}
