using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

/// <summary>
/// Configuration values for outbound calls to the central SkyMonitor service.
/// </summary>
public sealed class SkyMonitorClientOptions
{
    public const string SectionName = "SkyMonitor";
    public const string HttpClientName = "SkyMonitor.Api";

    private const string DefaultBaseUrl = "https://localhost:5001";

    /// <summary>
    /// Base URL for the SkyMonitor service (e.g., https://skymonitor.local:5174).
    /// </summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Attempts to resolve <see cref="BaseUrl"/> into an absolute URI.
    /// </summary>
    public bool TryResolveBaseUri([NotNullWhen(true)] out Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            baseUri = null;
            return false;
        }

        var normalized = BaseUrl.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed))
        {
            baseUri = null;
            return false;
        }

        baseUri = parsed;
        return true;
    }

    /// <summary>
    /// Resolves <see cref="BaseUrl"/> or throws when invalid.
    /// </summary>
    public Uri ResolveBaseUri()
    {
        if (TryResolveBaseUri(out var uri))
        {
            return uri;
        }

        throw new InvalidOperationException($"SkyMonitor:BaseUrl '{BaseUrl}' is not a valid absolute URI.");
    }
}