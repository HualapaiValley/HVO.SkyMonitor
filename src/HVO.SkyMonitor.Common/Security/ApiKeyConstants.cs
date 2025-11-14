namespace HVO.SkyMonitor.Common.Security;

/// <summary>
/// Constants for API key authentication.
/// </summary>
public static class ApiKeyClaims
{
    public const string AuthenticationType = "ApiKey";
    public const string AccessLevel = "ApiKeyAccessLevel";
    public const string KeyId = "ApiKeyId";
}

/// <summary>
/// Access levels for API keys.
/// </summary>
public enum ApiKeyAccessLevel
{
    Read = 1,
    ReadWrite = 2
}

/// <summary>
/// Authorization policy names.
/// </summary>
public static class AuthorizationPolicyNames
{
    public const string ApiKeyOrCookie = "ApiKeyOrCookie";
    public const string ApiKeyRead = "ApiKeyRead";
    public const string ApiKeyReadWrite = "ApiKeyReadWrite";
}
