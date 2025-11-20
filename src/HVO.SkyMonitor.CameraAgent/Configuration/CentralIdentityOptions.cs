using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

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

    /// <summary>
    /// Interactive client configuration for user sign-in.
    /// </summary>
    public InteractiveClientOptions? InteractiveClient { get; set; }
        = new InteractiveClientOptions();
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
    /// Example: "camera-agent"
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

/// <summary>
/// Interactive OpenID Connect client configuration for user sign-in.
/// </summary>
public class InteractiveClientOptions
{
    private static readonly string[] DefaultScopes =
    [
        "openid",
        "profile",
        "email"
    ];

    /// <summary>
    /// Publicly reachable authority base used for browser redirects.
    /// Falls back to <see cref="CentralIdentityOptions.ServiceUrl"/> when not specified.
    /// </summary>
    public Uri? PublicAuthority { get; set; }

    /// <summary>
    /// OAuth2/OIDC client identifier registered with Central Identity.
    /// </summary>
    [Required]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2/OIDC client secret registered with Central Identity.
    /// </summary>
    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Scopes requested during sign-in.
    /// Includes OpenID Connect defaults plus any API scopes required by the agent.
    /// </summary>
    public IList<string> Scopes { get; } = new List<string>(DefaultScopes);

    /// <summary>
    /// Callback path used by the OpenID Connect middleware.
    /// </summary>
    [Required]
    public string CallbackPath { get; set; } = "/signin-central";

    /// <summary>
    /// Path invoked after a remote sign-out completes.
    /// </summary>
    [Required]
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-central";

    /// <summary>
    /// Remote sign-out coordination path.
    /// </summary>
    [Required]
    public string RemoteSignOutPath { get; set; } = "/signout-central";
}
