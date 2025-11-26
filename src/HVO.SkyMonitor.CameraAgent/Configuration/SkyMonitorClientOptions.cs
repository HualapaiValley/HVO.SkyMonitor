using System;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

/// <summary>
/// Configuration values for outbound calls to the central SkyMonitor service.
/// </summary>
public sealed class SkyMonitorClientOptions
{
    public const string SectionName = "SkyMonitor";
    public const string HttpClientName = "SkyMonitor.Api";

    private const string DefaultBaseUrl = "http://logichost:8080";

    /// <summary>
    /// Base URL for the SkyMonitor service (e.g., https://skymonitor.local:5174).
    /// </summary>
    public Uri BaseUrl { get; set; } = new(DefaultBaseUrl, UriKind.Absolute);

    /// <summary>
    /// Attempts to resolve <see cref="BaseUrl"/> into an absolute URI.
    /// </summary>
    public bool TryResolveBaseUri([NotNullWhen(true)] out Uri? baseUri)
    {
        if (BaseUrl is null)
        {
            baseUri = null;
            return false;
        }
        baseUri = BaseUrl.IsAbsoluteUri ? BaseUrl : null;
        return baseUri is not null;
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
