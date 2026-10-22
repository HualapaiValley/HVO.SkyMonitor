using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>
/// The approved HTTPS endpoints, terms and provider checksums of the managed time-data refresh. They are the sources
/// <c>scripts/time-data:update</c> uses for the bundled pair; nothing here is configurable, so a refresh can only reach
/// these three URLs.
/// </summary>
public static class CameraAgentTimeDataSources
{
    public const string HttpClientName = "time-data-refresh";
    public const string EarthOrientationFile = "finals2000A.daily.extended";
    public const string LeapSecondsFile = "leap-seconds.list";
    public const string EarthOrientationProduct =
        "IERS Rapid Service/Prediction Center finals2000A daily extended (IAU2000A, since 1973, one year of predictions)";
    public const string LeapSecondsProduct = "IERS Earth Orientation Center leap-seconds.list (NTP format)";
    public const string EarthOrientationTerms = "Distribution Statement A. Approved for public release: distribution unlimited.";
    public const string LeapSecondsTerms = "This file is in the public domain.";
    public const string EarthOrientationChecksumAlgorithm = "SHA-512";
    public const string LeapSecondsChecksumAlgorithm = "SHA-1 (#h)";

    /// <summary>The checksum list is a few hundred bytes; the leap-file bound is ample.</summary>
    public const int MaximumChecksumBytes = CameraAgentPinnedOrbitalTimeSource.MaximumLeapSecondsBytes;

    /// <summary>Each request's deadline, covering connection, headers and body.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    public static Uri EarthOrientationUri { get; } = new("https://maia.usno.navy.mil/ser7/" + EarthOrientationFile);
    public static Uri EarthOrientationChecksumsUri { get; } = new("https://maia.usno.navy.mil/ser7/checksums.sha512");
    public static Uri EarthOrientationTermsUri { get; } = new("https://maia.usno.navy.mil/products/daily.htm");
    public static Uri LeapSecondsUri { get; } = new("https://hpiers.obspm.fr/iers/bul/bulc/ntp/" + LeapSecondsFile);

    /// <summary>No redirects, cookies, credentials or decompression; the operating system chooses the TLS version.</summary>
    public static HttpMessageHandler CreatePrimaryHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PreAuthenticate = false,
        Credentials = null,
        MaxResponseHeadersLength = 32,
        MaxResponseDrainSize = 0,
        ActivityHeadersPropagator = null,
        MaxConnectionsPerServer = 1,
        ConnectTimeout = RequestTimeout,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
    };

    /// <summary>
    /// The SHA-512 that USNO's <c>checksums.sha512</c> lists for <see cref="EarthOrientationFile"/>, exactly once, as
    /// 128 lowercase hex digits; null when it is missing, repeated or malformed.
    /// </summary>
    public static string? PublishedEarthOrientationSha512(ReadOnlySpan<byte> checksums)
    {
        string? found = null;
        foreach (var line in Encoding.ASCII.GetString(checksums).Split('\n'))
        {
            var fields = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[1].TrimStart('*') != EarthOrientationFile) continue;
            if (found is not null) return null;
            found = fields[0];
        }
        return found is { Length: 128 } && found.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? found : null;
    }

    /// <summary>True when the bytes' SHA-512 equals the published value.</summary>
    public static bool MatchesSha512(ReadOnlySpan<byte> bytes, string published)
        => string.Equals(Convert.ToHexStringLower(SHA512.HashData(bytes)), published, StringComparison.Ordinal);

    /// <summary>
    /// The leap file's own <c>#h</c> SHA-1, verified: the update time (<c>#$</c>), the expiry (<c>#@</c>) and every leap
    /// row, without comments or whitespace, as the IERS README describes. Returns the value when it matches and the file
    /// carries exactly one <c>#h</c> line; null otherwise.
    /// </summary>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is the provider's published checksum, checked for transfer integrity only; SHA-256 identifies the bytes and HTTPS to the approved endpoint establishes origin.")]
    public static string? VerifiedLeapSecondsHash(ReadOnlySpan<byte> leapSeconds)
    {
        string? published = null;
        var data = new StringBuilder();
        foreach (var raw in Encoding.ASCII.GetString(leapSeconds).Split('\n'))
        {
            if (raw.StartsWith("#h", StringComparison.Ordinal))
            {
                if (published is not null) return null;
                published = Squeeze(raw[2..]);
            }
            else if (raw.StartsWith("#$", StringComparison.Ordinal) || raw.StartsWith("#@", StringComparison.Ordinal))
                data.Append(Squeeze(StripComment(raw[2..])));
            else if (!raw.StartsWith('#'))
                data.Append(Squeeze(StripComment(raw)));
        }
        if (published is not { Length: 40 } || !published.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        var computed = Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes(data.ToString())));
        return computed == published ? published : null;

        static string StripComment(string line) => line.IndexOf('#', StringComparison.Ordinal) is var at and >= 0 ? line[..at] : line;
        static string Squeeze(string line) => string.Concat(line.Where(static c => c is not (' ' or '\t' or '\r')));
    }

    /// <summary>True when the leap file still states its public-domain terms.</summary>
    public static bool CarriesLeapSecondsTerms(ReadOnlySpan<byte> leapSeconds)
        => Encoding.ASCII.GetString(leapSeconds).Contains(LeapSecondsTerms, StringComparison.Ordinal);
}
