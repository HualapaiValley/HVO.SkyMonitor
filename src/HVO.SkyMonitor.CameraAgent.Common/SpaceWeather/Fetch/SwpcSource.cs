using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

/// <summary>The approved core SWPC endpoints, transport bounds and v1 policy constants.</summary>
public static class SwpcSource
{
    /// <summary>The named HTTP client reserved for SWPC.</summary>
    public const string HttpClientName = "hvo-swpc";
    /// <summary>The only approved provider origin.</summary>
    public static Uri BaseUri { get; } = new("https://services.swpc.noaa.gov/");
    /// <summary>Minimum spacing between attempts for one product.</summary>
    public static TimeSpan MinimumAttemptSpacing => TimeSpan.FromSeconds(60);
    /// <summary>Maximum transient backoff.</summary>
    public static TimeSpan TransientBackoffCap => TimeSpan.FromSeconds(1_800);
    /// <summary>Rate-limit delay when Retry-After is absent or invalid.</summary>
    public static TimeSpan RateLimitedDefault => TimeSpan.FromSeconds(1_800);
    /// <summary>Minimum clamped Retry-After delay.</summary>
    public static TimeSpan RetryAfterMinimum => MinimumAttemptSpacing;
    /// <summary>Maximum clamped Retry-After delay.</summary>
    public static TimeSpan RetryAfterMaximum => TimeSpan.FromSeconds(21_600);
    /// <summary>Consecutive rate limits that halt the provider.</summary>
    public const int RateLimitedHaltCount = 3;
    /// <summary>Consecutive rejected bodies that raise provider attention.</summary>
    public const int RejectedAttentionCount = 3;
    /// <summary>Base interval multiplier for fresh retrievals.</summary>
    public const int FreshMultiplier = 3;
    /// <summary>Deadline covering headers, body and decoding.</summary>
    public static TimeSpan RequestTimeout => TimeSpan.FromSeconds(30);

    internal const int MaximumEntityTagLength = 1_024;
    internal const int MaximumLastModifiedLength = 128;
    internal const int MaximumMediaTypeLength = 256;
    internal const int MaximumRetryAfterLength = 29;
    private const string ProductToken = "HVO.SkyMonitor";
    private const string Contact = " (+https://github.com/HualapaiValley/HVO.SkyMonitor)";

    /// <summary>The product and assembly version, without build metadata, followed by the project URL.</summary>
    public static string UserAgent { get; } = CreateUserAgent(typeof(SwpcSource).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Returns the canonical URI; unapproved products, including OVATION, are rejected.</summary>
    public static Uri RequestUri(SwpcProduct product) => new(BaseUri, product switch
    {
        SwpcProduct.Alerts => "products/alerts.json",
        SwpcProduct.Scales => "products/noaa-scales.json",
        SwpcProduct.KpForecast => "products/noaa-planetary-k-index-forecast.json",
        SwpcProduct.ThreeDayForecast => "text/3-day-forecast.txt",
        _ => throw new ArgumentOutOfRangeException(nameof(product))
    });

    /// <summary>Returns the zero-jitter base interval.</summary>
    public static TimeSpan BaseInterval(SwpcProduct product) => TimeSpan.FromSeconds(product switch
    {
        SwpcProduct.Alerts => 120,
        SwpcProduct.Scales => 300,
        SwpcProduct.KpForecast => 900,
        SwpcProduct.ThreeDayForecast => 1_800,
        _ => throw new ArgumentOutOfRangeException(nameof(product))
    });

    /// <summary>Returns the required decoded-body media type.</summary>
    public static string MediaType(SwpcProduct product) => product switch
    {
        SwpcProduct.Alerts or SwpcProduct.Scales or SwpcProduct.KpForecast => "application/json",
        SwpcProduct.ThreeDayForecast => "text/plain",
        _ => throw new ArgumentOutOfRangeException(nameof(product))
    };

    /// <summary>Returns the outdated retrieval-age threshold.</summary>
    public static TimeSpan OutdatedAfter(SwpcProduct product) => TimeSpan.FromSeconds(product switch
    {
        SwpcProduct.Alerts => 3_600,
        SwpcProduct.Scales => 10_800,
        SwpcProduct.KpForecast => 21_600,
        SwpcProduct.ThreeDayForecast => 93_600,
        _ => throw new ArgumentOutOfRangeException(nameof(product))
    });

    /// <summary>Returns the shared fact model's decoded-body bound.</summary>
    public static int MaximumDecodedBytes(SwpcProduct product) => SwpcLimits.BodyBytes(product);

    /// <summary>
    /// Disables redirects, cookies, credentials, decompression, response draining and distributed-trace headers.
    /// Header size, connection count and connect time are bounded; TLS validation uses platform defaults.
    /// </summary>
    public static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PreAuthenticate = false,
        Credentials = null,
        ActivityHeadersPropagator = null,
        MaxResponseHeadersLength = 32,
        MaxResponseDrainSize = 0,
        MaxConnectionsPerServer = 1,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    /// <summary>Keeps one bounded printable-ASCII value unchanged, dropping invalid validators.</summary>
    internal static string? SanitizeValidator(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) return null;
        foreach (var character in value)
        {
            if (character is < ' ' or > '~') return null;
        }
        return value;
    }

    /// <summary>Returns the contract-trimmed media token as bounded printable ASCII, without parameters (T4).</summary>
    internal static string? MediaTypeToken(string? value)
    {
        var token = value.AsSpan();
        var semicolon = token.IndexOf(';');
        if (semicolon >= 0) token = token[..semicolon];
        while (!token.IsEmpty && IsFieldWhitespace(token[0])) token = token[1..];
        while (!token.IsEmpty && IsFieldWhitespace(token[^1])) token = token[..^1];
        if (token.IsEmpty || token.Length > MaximumMediaTypeLength) return null;
        foreach (var character in token)
        {
            if (character is < ' ' or > '~') return null;
        }
        return token.ToString();
    }

    // The contract's field-whitespace rule uses Unicode whitespace plus U+001C through U+001F.
    private static bool IsFieldWhitespace(char character) => char.IsWhiteSpace(character) || character is >= '\u001c' and <= '\u001f';

    /// <summary>Returns exactly one raw field value; commas within that value are preserved.</summary>
    internal static string? SingleHeaderValue(HttpHeaders headers, string name)
    {
        if (!headers.NonValidated.TryGetValues(name, out var values) || values.Count != 1) return null;
        foreach (var value in values) return value;
        return null;
    }

    internal static string CreateUserAgent(string? informationalVersion)
    {
        var version = informationalVersion;
        var metadata = version?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (metadata >= 0) version = version![..metadata];
        var fallback = ProductToken + Contact;
        if (string.IsNullOrWhiteSpace(version) || version.Any(char.IsWhiteSpace)) return fallback;
        // Validate the version separately: spaces or comment syntax must not become additional product tokens.
        if (!ProductHeaderValue.TryParse(ProductToken + "/" + version, out _)) return fallback;
        var candidate = ProductToken + "/" + version + Contact;
        return SwpcClient.IsValidUserAgent(candidate) ? candidate : fallback;
    }
}
