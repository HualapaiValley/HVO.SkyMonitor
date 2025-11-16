using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Services;

/// <summary>
/// Provides structured logging for authentication and authorization events.
/// Identity Hardening: Structured Logging
/// </summary>
public interface IAuthenticationEventLogger
{
    void LogLoginSuccess(string userId, string username, string? ipAddress);
    void LogLoginFailure(string? username, string reason, string? ipAddress);
    void LogTokenIssued(string clientId, string grantType, string? userId, string[] scopes);
    void LogTokenRefreshed(string clientId, string? userId);
    void LogApiKeyUsed(string keyId, string? userId, string accessLevel, string endpoint);
    void LogSignedUrlValidationFailure(string path, string reason, string? ipAddress);
    void LogAccountLockout(string userId, string username, string? ipAddress);
    void LogPasswordChangeSuccess(string userId, string username);
    void LogPasswordChangeFailure(string userId, string username, string reason);
}

public sealed class AuthenticationEventLogger : IAuthenticationEventLogger
{
    private readonly ILogger<AuthenticationEventLogger> _logger;

    public AuthenticationEventLogger(ILogger<AuthenticationEventLogger> logger)
    {
        _logger = logger;
    }

    public void LogLoginSuccess(string userId, string username, string? ipAddress)
    {
        _logger.LogInformation(
            "Login successful: UserId={UserId}, Username={Username}, IpAddress={IpAddress}",
            userId, username, ipAddress ?? "unknown");
    }

    public void LogLoginFailure(string? username, string reason, string? ipAddress)
    {
        _logger.LogWarning(
            "Login failed: Username={Username}, Reason={Reason}, IpAddress={IpAddress}",
            username ?? "unknown", reason, ipAddress ?? "unknown");
    }

    public void LogTokenIssued(string clientId, string grantType, string? userId, string[] scopes)
    {
        _logger.LogInformation(
            "OAuth2 token issued: ClientId={ClientId}, GrantType={GrantType}, UserId={UserId}, Scopes={Scopes}",
            clientId, grantType, userId ?? "none", string.Join(", ", scopes));
    }

    public void LogTokenRefreshed(string clientId, string? userId)
    {
        _logger.LogInformation(
            "OAuth2 token refreshed: ClientId={ClientId}, UserId={UserId}",
            clientId, userId ?? "none");
    }

    public void LogApiKeyUsed(string keyId, string? userId, string accessLevel, string endpoint)
    {
        _logger.LogInformation(
            "API key used: KeyId={KeyId}, UserId={UserId}, AccessLevel={AccessLevel}, Endpoint={Endpoint}",
            keyId, userId ?? "unknown", accessLevel, endpoint);
    }

    public void LogSignedUrlValidationFailure(string path, string reason, string? ipAddress)
    {
        _logger.LogWarning(
            "Signed URL validation failed: Path={Path}, Reason={Reason}, IpAddress={IpAddress}",
            path, reason, ipAddress ?? "unknown");
    }

    public void LogAccountLockout(string userId, string username, string? ipAddress)
    {
        _logger.LogWarning(
            "Account locked out: UserId={UserId}, Username={Username}, IpAddress={IpAddress}",
            userId, username, ipAddress ?? "unknown");
    }

    public void LogPasswordChangeSuccess(string userId, string username)
    {
        _logger.LogInformation(
            "Password changed successfully: UserId={UserId}, Username={Username}",
            userId, username);
    }

    public void LogPasswordChangeFailure(string userId, string username, string reason)
    {
        _logger.LogWarning(
            "Password change failed: UserId={UserId}, Username={Username}, Reason={Reason}",
            userId, username, reason);
    }
}
