using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

/// <summary>
/// How this agent checks its clock against network time servers, read from the <c>CameraAgent:TimeSync</c>
/// configuration section on every measurement round, so an edit to the operator settings file applies at the next
/// round without a restart. Out-of-range values are clamped rather than rejected, and invalid server entries are
/// reported per entry, so a bad hand edit never stops the clock check from running.
/// </summary>
public sealed partial record TimeSyncSettings(
    bool Enabled,
    IReadOnlyList<string> Servers,
    TimeSpan Interval,
    TimeSpan Tolerance,
    TimeSpan QueryTimeout)
{
    public const string SectionKey = "CameraAgent:TimeSync";

    /// <summary>The key the operator UI writes: the servers as one comma-separated value.</summary>
    public const string ServersKey = SectionKey + ":Servers";

    public const int MaximumServers = 4;

    /// <summary>The longest server entry accepted: a DNS name of at most 253 characters, a colon and a port.</summary>
    public const int MaximumServerLength = 259;

    public static readonly IReadOnlyList<string> DefaultServers = ["pool.ntp.org"];

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(24);

    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan MinimumTolerance = TimeSpan.FromMilliseconds(10);

    public static readonly TimeSpan MaximumTolerance = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan MinimumQueryTimeout = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan MaximumQueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>What a host with no time sync configuration reads: checking on, no servers listed, so the default is used.</summary>
    public static TimeSyncSettings Default { get; } = new(
        true, [], DefaultInterval, DefaultTolerance, DefaultQueryTimeout);

    /// <summary>
    /// The configured servers, or the default when none is configured. At most <see cref="MaximumServers"/> are
    /// queried in a round; entries beyond that are ignored.
    /// </summary>
    public IReadOnlyList<string> EffectiveServers => Servers.Count == 0 ? DefaultServers : Servers;

    public static TimeSyncSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionKey);
        return new TimeSyncSettings(
            !bool.TryParse(section["Enabled"], out var enabled) || enabled,
            ReadServers(section.GetSection("Servers")),
            Clamp(section["Interval"], DefaultInterval, MinimumInterval, MaximumInterval),
            Clamp(section["Tolerance"], DefaultTolerance, MinimumTolerance, MaximumTolerance),
            Clamp(section["QueryTimeout"], DefaultQueryTimeout, MinimumQueryTimeout, MaximumQueryTimeout));
    }

    /// <summary>
    /// Splits a comma- or whitespace-separated server list. A hand-edited JSON array of names is accepted as well, so
    /// either form in the settings file configures the same list.
    /// </summary>
    public static IReadOnlyList<string> ReadServers(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return section.Value is { } value
            ? SplitServers(value)
            : [.. section.GetChildren()
                .Select(static child => child.Value?.Trim())
                .Where(static entry => !string.IsNullOrEmpty(entry))
                .Select(static entry => entry!)];
    }

    public static IReadOnlyList<string> SplitServers(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Whether <paramref name="entry"/> names a time server this agent may query: a DNS host name, an IPv4 address, or
    /// a bracketed IPv6 address, each optionally followed by <c>:port</c>. URLs, credentials and paths are rejected.
    /// </summary>
    public static bool TryParseServer(string? entry, out string host, out int port)
    {
        host = string.Empty;
        port = SntpPacket.Port;
        if (string.IsNullOrWhiteSpace(entry) || entry.Length > MaximumServerLength || entry.Trim().Length != entry.Length)
        {
            return false;
        }
        var name = entry;
        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }
            name = entry[1..close];
            var rest = entry[(close + 1)..];
            if (!IPAddress.TryParse(name, out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
                (rest.Length > 0 && !TryParsePort(rest, out port)))
            {
                return false;
            }
            host = name;
            return true;
        }
        var colon = entry.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!TryParsePort(entry[colon..], out port))
            {
                return false;
            }
            name = entry[..colon];
        }
        if (IsIPv4(name) || HostName().IsMatch(name))
        {
            host = name;
            return true;
        }
        return false;
    }

    private static bool TryParsePort(string suffix, out int port)
    {
        port = SntpPacket.Port;
        return suffix.Length is > 1 and <= 6 && suffix[0] == ':' && suffix[1..].All(char.IsAsciiDigit) &&
            int.TryParse(suffix[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }

    private static bool IsIPv4(string value)
        => value.Count(static character => character == '.') == 3 && value.All(static character => character == '.' || char.IsAsciiDigit(character)) &&
            IPAddress.TryParse(value, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    private static TimeSpan Clamp(string? value, TimeSpan fallback, TimeSpan minimum, TimeSpan maximum)
        => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed < minimum ? minimum : parsed > maximum ? maximum : parsed
            : fallback;

    // RFC 1123 labels: letters, digits and inner hyphens, at most 63 characters each and 253 in all, with a
    // non-numeric last label so a mistyped IPv4 address is never looked up as a name.
    [GeneratedRegex(@"^(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*(?:[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)$", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();
}
