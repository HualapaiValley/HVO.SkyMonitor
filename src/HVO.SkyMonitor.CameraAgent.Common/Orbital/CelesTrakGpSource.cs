using System.Net;
using System.Net.Http.Headers;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// The single approved CelesTrak GP request (#527): canonical HTTPS, the explicitly configured active group and
/// JSON only. The provider key is the halt scope, so changing the group cannot evade a provider-wide halt.
/// </summary>
public static class CelesTrakGpSource
{
    public const string ProviderKey = "celestrak";
    public const string DatasetKey = "celestrak:gp:active";
    public const string RequestedPopulation = "celestrak:gp:GROUP=active";
    public const string HttpClientName = "orbital-elements-celestrak";
    public const int MaximumWireBytes = 32 * 1024 * 1024;

    /// <summary>Equals the shared contract's <c>RawLengthBytes</c> bound for one source descriptor.</summary>
    public const int MaximumRawBytes = 32 * 1024 * 1024;

    public const string UnderlyingLineage = "USSF public GP via CelesTrak";
    public const string TermsReference = "CelesTrak usage policy: at most one GP query per two hours per group and halt on " +
        "any non-200 response. Underlying USSF public GP data via CelesTrak; attribution retained; local calculation and " +
        "cache only, no feed redistribution.";

    /// <summary>Evidence-only validator bounds; they match the shared snapshot source descriptor.</summary>
    internal const int MaximumEntityTagLength = 1024;
    internal const int MaximumLastModifiedLength = 128;

    /// <summary>Kibibytes, the unit <see cref="SocketsHttpHandler.MaxResponseHeadersLength"/> uses.</summary>
    internal const int MaximumResponseHeadersKibibytes = 32;

    public static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromSeconds(30);

    public static Uri RequestUri { get; } = new("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=JSON");

    public static Uri TermsUri { get; } = new("https://celestrak.org/usage-policy.php");

    /// <summary>
    /// Primary handler for the named client. Redirects, cookies, automatic decompression, credentials, response
    /// draining and distributed-trace header propagation to the third-party host are disabled; TLS validation stays at
    /// the platform default and the response header block is bounded. A non-200 body is never drained to reuse the
    /// connection, so it is not read at the transport level either.
    /// </summary>
    public static HttpMessageHandler CreatePrimaryHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PreAuthenticate = false,
        Credentials = null,
        MaxResponseHeadersLength = MaximumResponseHeadersKibibytes,
        MaxResponseDrainSize = 0,
        ActivityHeadersPropagator = null,
        MaxConnectionsPerServer = 1,
        ConnectTimeout = MaximumRequestTimeout,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// Keeps a provider validator only when it is one bounded value of printable ASCII. A value that would need
    /// truncation or contains any control or non-ASCII character is dropped rather than altered.
    /// </summary>
    internal static string? SanitizeValidator(string? value, int maximumLength)
    {
        if (value is null || value.Length == 0 || value.Length > maximumLength || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        foreach (var c in value)
        {
            if (c is < ' ' or > '~')
            {
                return null;
            }
        }
        return value;
    }

    internal static string? SingleHeaderValue(HttpHeaders headers, string name)
    {
        if (!headers.NonValidated.TryGetValues(name, out var values) || values.Count != 1)
        {
            return null;
        }
        foreach (var value in values)
        {
            return value;
        }
        return null;
    }
}
