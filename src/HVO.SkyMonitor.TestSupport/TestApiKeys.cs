namespace HVO.SkyMonitor.TestSupport;

/// <summary>
/// Shared test API keys for system clients.
/// </summary>
public static class TestApiKeys
{
    /// <summary>
    /// API key for camera agent authentication.
    /// </summary>
    public static class CameraAgent
    {
        public const string Key = "test-camera-agent-key-12345678901234567890123456789012";
        public const string Name = "Test Camera Agent API Key";
        public const string Description = "API key for camera agent integration tests";
        public static readonly string[] Scopes = ["api.camera", "api.frames"];
    }

    /// <summary>
    /// API key for internal service authentication.
    /// </summary>
    public static class InternalService
    {
        public const string Key = "test-internal-service-key-12345678901234567890123456789012";
        public const string Name = "Test Internal Service API Key";
        public const string Description = "API key for internal service integration tests";
        public static readonly string[] Scopes = ["api.admin", "api.camera", "api.frames", "api.images"];
    }

    /// <summary>
    /// API key for webhook callbacks.
    /// </summary>
    public static class Webhook
    {
        public const string Key = "test-webhook-key-12345678901234567890123456789012";
        public const string Name = "Test Webhook API Key";
        public const string Description = "API key for webhook integration tests";
        public static readonly string[] Scopes = ["api.webhooks"];
    }

    /// <summary>
    /// Read-only API key for monitoring and metrics.
    /// </summary>
    public static class ReadOnly
    {
        public const string Key = "test-readonly-key-12345678901234567890123456789012";
        public const string Name = "Test Read-Only API Key";
        public const string Description = "API key for read-only monitoring tests";
        public static readonly string[] Scopes = ["api.viewer"];
    }
}
