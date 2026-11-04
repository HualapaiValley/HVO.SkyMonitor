using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment.Contracts;

/// <summary>The retained, content-free host observation. Numeric strings preserve the inventory/evidence wire contract.</summary>
public sealed record PreflightHostObservation(string HostIdentity, string HostName, string Architecture, string CpuCount,
    string MemoryKiB, string FreeSpaceKiB, string Clock, string ThermalReport, string ThrottleReport, string Safety)
{
    public static PreflightHostObservation? Parse(string wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var parts = wire.TrimEnd('\n').Split('\t');
        return parts.Length == 10 && !wire.TrimEnd('\n').Contains('\n', StringComparison.Ordinal)
            ? new(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], parts[6], parts[7], parts[8], parts[9]) : null;
    }
    public bool Valid => Digits(CpuCount) && Digits(MemoryKiB) && Digits(FreeSpaceKiB) && Digits(Clock) &&
        (ThermalReport == "unsupported" || Match(ThermalReport, "reported:-?[0-9]+")) &&
        (ThrottleReport == "unsupported" || Match(ThrottleReport, "reported:0x[0-9a-fA-F]+")) &&
        Match(Safety, "(?:ready|needs_prepare|unsafe):(?:none|present|unsupported)");
    public bool MeetsMemory(BigInteger minimum) => BigInteger.Parse(MemoryKiB, CultureInfo.InvariantCulture) >= minimum;
    public string RootStatus => Safety.Split(':')[0];
    public string SocketStatus => Safety.Split(':')[1];
    public static BigInteger? MemoryToKiB(string value)
    {
        if (!Match(value, "[1-9][0-9]*[MG]")) return null;
        return BigInteger.Parse(value[..^1], CultureInfo.InvariantCulture) * (value[^1] == 'G' ? 1024 * 1024 : 1024);
    }
    public static string? DockerArchitecture(string value) => value switch { "x86_64" or "amd64" => "amd64", "aarch64" or "arm64" => "arm64", _ => null };
    public static bool DockerVersion(string value) => Match(value, "[A-Za-z0-9][A-Za-z0-9._+-]{0,63}");
    public static bool ComposeVersion(string value) => Match(value, "v?[0-9]+[.][0-9]+[.][0-9]+(?:[._+-][A-Za-z0-9.-]+)?");
    private static bool Digits(string value) => Match(value, "[0-9]+");
    private static bool Match(string value, string pattern) => Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
