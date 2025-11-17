using System;
using System.Collections.Generic;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

/// <summary>
/// Configuration options for connecting to the central HVO.SkyMonitor identity service.
/// </summary>
public class CentralIdentityOptions
{
    /// <summary>
    /// The base URL of the central HVO.SkyMonitor identity service.
    /// Example: "https://localhost:5001" or "https://skymonitor.example.com"
    /// </summary>
    public Uri ServiceUrl { get; set; } = new("https://localhost:5001", UriKind.Absolute);

    /// <summary>
    /// The authentication mode to use for this camera agent.
    /// </summary>
    public AuthenticationMode Mode { get; set; } = AuthenticationMode.ClientCredentials;

    /// <summary>
    /// OAuth2 client credentials configuration (for SYSTEM accounts).
    /// Used when Mode is ClientCredentials.
    /// </summary>
    public ClientCredentialsOptions? ClientCredentials { get; set; }

    /// <summary>
    /// API key configuration (alternative to client credentials).
    /// Used when Mode is ApiKey.
    /// </summary>
    public ApiKeyOptions? ApiKey { get; set; }

    /// <summary>
    /// Token cache duration in seconds. Default: 300 seconds (5 minutes).
    /// Tokens are cached and reused until they expire or are within refresh window.
    /// </summary>
    public int TokenCacheDurationSeconds { get; set; } = 300;

    /// <summary>
    /// How many seconds before token expiration to proactively refresh.
    /// Default: 60 seconds. This ensures we don't use tokens that are about to expire.
    /// </summary>
    public int TokenRefreshWindowSeconds { get; set; } = 60;
}

/// <summary>
/// Authentication mode for camera agents.
/// </summary>
public enum AuthenticationMode
{
    /// <summary>
    /// Use OAuth2 client credentials flow to obtain access tokens.
    /// Best for service-to-service communication with token caching.
    /// </summary>
    ClientCredentials,

    /// <summary>
    /// Use API keys directly in requests (X-API-Key header).
    /// Simpler but less flexible than OAuth2.
    /// </summary>
    ApiKey
}

/// <summary>
/// OAuth2 client credentials configuration.
/// </summary>
public class ClientCredentialsOptions
{
    /// <summary>
    /// Default scopes requested by camera agents when no configuration override is provided.
    /// </summary>
    public static IReadOnlyList<string> DefaultScopes { get; } =
        new[] { "api.camera", "api.frames", "api.images" };

    /// <summary>
    /// OAuth2 client ID assigned to this camera agent.
    /// Example: "camera-agent-simulator"
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2 client secret for this camera agent.
    /// Should be stored securely (environment variable, Key Vault, etc.).
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Scopes to request when obtaining tokens. Configuration binding populates this collection; defaults are applied later.
    /// </summary>
    public IList<string> Scopes { get; } = new List<string>();
}

/// <summary>
/// API key configuration.
/// </summary>
public class ApiKeyOptions
{
    /// <summary>
    /// The API key to use for authentication.
    /// Should be prefixed with "smk_" and stored securely.
    /// </summary>
    public string Key { get; set; } = string.Empty;
}
