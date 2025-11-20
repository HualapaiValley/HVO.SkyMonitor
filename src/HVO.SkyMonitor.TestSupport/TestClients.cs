using System;

namespace HVO.SkyMonitor.TestSupport;

/// <summary>
/// Shared test OAuth/OIDC client configurations for system and UI clients.
/// </summary>
public static class TestClients
{
    /// <summary>
    /// System client for camera agents (machine-to-machine authentication).
    /// </summary>
    public static class SystemCameraAgent
    {
        public const string ClientId = "system-camera-agent";
        public const string ClientSecret = "test-camera-agent-secret-do-not-use-in-production";
        public const string DisplayName = "Camera Agent System Client";
        public static readonly string[] Scopes = ["api.camera", "api.frames", "api.images"];
    }

    /// <summary>
    /// Interactive confidential client for the camera agent web UI (Authorization Code + PKCE).
    /// </summary>
    public static class CameraAgentInteractive
    {
        public const string ClientId = "camera-agent-interactive";
        public const string ClientSecret = "test-camera-agent-interactive-secret-do-not-use-in-production";
        public const string DisplayName = "Camera Agent Interactive Client";
        public static readonly string[] Scopes = [
            "openid",
            "profile",
            "email",
            "api.camera",
            "api.frames",
            "api.images"
        ];

        public static readonly Uri[] RedirectUris =
        [
            new("https://localhost:7130/signin-central", UriKind.Absolute),
            new("http://localhost:5130/signin-central", UriKind.Absolute)
        ];

        public static readonly Uri[] PostLogoutRedirectUris =
        [
            new("https://localhost:7130/signout-callback-central", UriKind.Absolute),
            new("http://localhost:5130/signout-callback-central", UriKind.Absolute)
        ];
    }

    /// <summary>
    /// System client for internal services.
    /// </summary>
    public static class SystemInternal
    {
        public const string ClientId = "system-internal";
        public const string ClientSecret = "test-internal-secret-do-not-use-in-production";
        public const string DisplayName = "Internal System Client";
        public static readonly string[] Scopes = ["api.admin", "api.camera", "api.frames", "api.images"];
    }

    /// <summary>
    /// Web UI client for browser-based authentication (Authorization Code + PKCE).
    /// </summary>
    public static class WebUI
    {
        public const string ClientId = "web-ui";
        public const string DisplayName = "Web UI Client";
        public static readonly string[] Scopes = ["openid", "profile", "email", "api.viewer"];
        // Note: No client secret - uses PKCE for public clients
    }

    /// <summary>
    /// Mobile app client for device-based authentication.
    /// </summary>
    public static class MobileApp
    {
        public const string ClientId = "mobile-app";
        public const string DisplayName = "Mobile Application Client";
        public static readonly string[] Scopes = ["openid", "profile", "email", "api.viewer", "offline_access"];
        // Note: No client secret - uses PKCE for public clients
    }
}
