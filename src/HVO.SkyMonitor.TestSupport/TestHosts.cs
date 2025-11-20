namespace HVO.SkyMonitor.TestSupport;

/// <summary>
/// Shared test host URLs and domains.
/// </summary>
public static class TestHosts
{
    /// <summary>
    /// Base HTTP URL for development testing.
    /// </summary>
    public const string HttpDevelopmentUrl = "http://localhost:5174";

    /// <summary>
    /// Logical domain for test scenarios.
    /// </summary>
    public const string LogicalDomain = "skymonitor.local";

    /// <summary>
    /// Base HTTP URL using logical domain.
    /// </summary>
    public const string HttpLogicalUrl = $"http://{LogicalDomain}";

    /// <summary>
    /// HTTPS URL for development testing (when HTTPS is configured).
    /// </summary>
    public const string HttpsDevelopmentUrl = "https://localhost:7096";

    /// <summary>
    /// Public identity authority used for interactive sign-in flows.
    /// </summary>
    public static Uri IdentityPublicAuthority { get; } = new("https://localhost:7096", UriKind.Absolute);

    /// <summary>
    /// Camera Agent base URL.
    /// </summary>
    public const string CameraAgentUrl = "http://localhost:5130";
}
