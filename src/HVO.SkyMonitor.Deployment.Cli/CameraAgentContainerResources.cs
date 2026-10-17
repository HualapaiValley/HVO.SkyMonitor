using System.Globalization;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The CameraAgent container's Compose resource limits: the installer defaults and minimums from section 7 of the HVO
/// install contract, the <c>--cpus</c> and <c>--memory</c> syntax, and the one place the limits are written into, read
/// from, and moved within a Compose file. The replay runner, when present, keeps its own fixed limits.
/// </summary>
internal static partial class CameraAgentContainerResources
{
    public const decimal MinimumCpus = 2m;
    public const decimal MaximumCpus = 1024m;
    public const long Gibibyte = 1024L * 1024 * 1024;
    public const long Mebibyte = 1024L * 1024;
    public const long MinimumMemoryBytes = 4 * Gibibyte;
    public const long MaximumMemoryBytes = 1024 * Gibibyte;

    /// <summary>
    /// The PIDs limit of the original template. It bounds runaway process and thread creation rather than capacity: the
    /// .NET runtime adds roughly one thread per CPU it is given, so the larger CPU limit needs no more than it allows.
    /// </summary>
    public const int PidsLimit = 256;

    private const string ReplayRunnerService = "\n  replay-runner:\n";

    /// <summary>The limits a new instance receives for every limit the operator does not set.</summary>
    public static ContainerResourceLimits Default { get; } = new(MinimumCpus, MinimumMemoryBytes, PidsLimit, false, false);

    /// <summary>
    /// The fixed limits every installer wrote before limits were recorded. An instance whose manifest records none runs
    /// these, and none of them was set by an operator.
    /// </summary>
    public static ContainerResourceLimits Legacy { get; } = new(1m, Gibibyte, PidsLimit, false, false);

    /// <summary>The limits an installation asks for: each one the operator set, otherwise the default.</summary>
    public static ContainerResourceLimits Resolve(decimal? cpus, string? memory)
        => new(
            cpus is { } requestedCpus ? ValidateCpus(requestedCpus) : Default.Cpus,
            memory is null ? Default.MemoryBytes : ParseMemory(memory),
            PidsLimit,
            CpusSetByOperator: cpus is not null,
            MemorySetByOperator: memory is not null);

    /// <summary>
    /// The limits an upgrade gives its candidate: each limit the operator set is kept, and each one left to the
    /// default moves to this installer's default. An instance that records no limits moves wholly to the default.
    /// </summary>
    public static ContainerResourceLimits UpgradeTarget(ContainerResourceLimits? recorded)
        => new(
            recorded is { CpusSetByOperator: true } ? recorded.Cpus : Default.Cpus,
            recorded is { MemorySetByOperator: true } ? recorded.MemoryBytes : Default.MemoryBytes,
            PidsLimit,
            recorded?.CpusSetByOperator ?? false,
            recorded?.MemorySetByOperator ?? false);

    /// <summary>
    /// Returns a CPU limit from 2 to 1,024 with at most two decimal places, normalized so equal limits compare, hash and
    /// render alike. A limit below the minimum is refused rather than acknowledged: the contract makes 2 CPUs a floor.
    /// </summary>
    public static decimal ValidateCpus(decimal cpus)
    {
        if (cpus is < MinimumCpus or > MaximumCpus || decimal.Round(cpus, 2) != cpus)
        {
            throw new InstallUsageException(
                "--cpus must be a number from 2 to 1024 with at most two decimal places; CameraAgent is never limited to fewer than 2 CPUs.");
        }
        return decimal.Parse(FormatCpus(cpus), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses a memory limit written as whole mebibytes or gibibytes, such as <c>6144m</c> or <c>6g</c>, from 4 GiB to
    /// 1 TiB. A limit below the minimum is refused rather than acknowledged: the contract makes 4 GiB a floor.
    /// </summary>
    public static long ParseMemory(string memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var match = MemoryPattern().Match(memory);
        var bytes = match.Success
            ? long.Parse(match.Groups["amount"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) *
              (match.Groups["unit"].Value is "g" or "G" ? Gibibyte : Mebibyte)
            : 0;
        if (bytes is < MinimumMemoryBytes or > MaximumMemoryBytes)
        {
            throw new InstallUsageException(
                "--memory must be whole mebibytes or gibibytes such as 6144m or 6g, from 4g to 1024g; CameraAgent is never limited to less than 4 GiB.");
        }
        return bytes;
    }

    /// <summary>Whether a recorded limit set is one this installer could have written.</summary>
    public static bool IsValid(ContainerResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return limits.Cpus is >= MinimumCpus and <= MaximumCpus && decimal.Round(limits.Cpus, 2) == limits.Cpus &&
               limits.MemoryBytes is >= MinimumMemoryBytes and <= MaximumMemoryBytes &&
               limits.MemoryBytes % Mebibyte == 0 && limits.PidsLimit > 0;
    }

    public static string FormatCpus(decimal cpus) => cpus.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The operator-facing form of a set of limits, such as <c>2 CPUs, 4 GiB of memory and 256 PIDs</c>.</summary>
    public static string Describe(ContainerResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return string.Create(CultureInfo.InvariantCulture,
            $"{FormatCpus(limits.Cpus)} CPUs, {DescribeMemory(limits.MemoryBytes)} of memory and {limits.PidsLimit} PIDs");
    }

    public static string DescribeMemory(long bytes)
        => bytes % Gibibyte == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / Gibibyte} GiB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / Mebibyte} MiB");

    /// <summary>
    /// The CameraAgent service's limit lines. Every installer has written exactly these three lines, so the original
    /// 1 CPU and 1 GiB render byte for byte as the template always did.
    /// </summary>
    public static string RenderLimits(ContainerResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var memory = limits.MemoryBytes % Gibibyte == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{limits.MemoryBytes / Gibibyte}G")
            : string.Create(CultureInfo.InvariantCulture, $"{limits.MemoryBytes / Mebibyte}M");
        return string.Create(CultureInfo.InvariantCulture,
            $"    cpus: \"{FormatCpus(limits.Cpus)}\"\n    mem_limit: {memory}\n    pids_limit: {limits.PidsLimit}\n");
    }

    /// <summary>
    /// Returns <paramref name="compose"/> with the CameraAgent service carrying <paramref name="target"/>, or the same
    /// text when it already does. The CameraAgent service is the first in every generated model and the replay runner
    /// follows it, so only the text before the runner is searched. The service must carry exactly one recognisable
    /// limit block and, when <paramref name="recorded"/> is given, the limits it names: a file that disagrees with its
    /// record is refused rather than silently rewritten.
    /// </summary>
    public static string WithLimits(string compose, ContainerResourceLimits target, ContainerResourceLimits? recorded)
    {
        ArgumentNullException.ThrowIfNull(compose);
        ArgumentNullException.ThrowIfNull(target);
        var runner = compose.IndexOf(ReplayRunnerService, StringComparison.Ordinal);
        var matches = LimitsPattern().Matches(runner < 0 ? compose : compose[..runner]);
        if (matches.Count != 1)
        {
            throw new InstallerException("The retained Compose file does not carry exactly one CameraAgent resource limit block.");
        }
        var match = matches[0];
        var current = new ContainerResourceLimits(
            decimal.Parse(match.Groups["cpus"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
            long.Parse(match.Groups["amount"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) *
            (match.Groups["unit"].Value == "G" ? Gibibyte : Mebibyte),
            int.Parse(match.Groups["pids"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
            false,
            false);
        if (recorded is not null && !SameLimits(current, recorded))
        {
            throw new InstallerException(
                "The retained Compose file does not carry the CameraAgent resource limits its instance manifest records.");
        }
        return SameLimits(current, target)
            ? compose
            : string.Concat(compose.AsSpan(0, match.Index), RenderLimits(target), compose.AsSpan(match.Index + match.Length));
    }

    /// <summary>Whether two limit sets impose the same limits, whoever set them.</summary>
    public static bool SameLimits(ContainerResourceLimits left, ContainerResourceLimits right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Cpus == right.Cpus && left.MemoryBytes == right.MemoryBytes && left.PidsLimit == right.PidsLimit;
    }

    [GeneratedRegex("^(?<amount>[1-9][0-9]{0,6})(?<unit>[mMgG])$", RegexOptions.CultureInvariant)]
    private static partial Regex MemoryPattern();

    [GeneratedRegex(
        "^    cpus: \"(?<cpus>[0-9]{1,4}(?:\\.[0-9]{1,2})?)\"\\n    mem_limit: (?<amount>[1-9][0-9]{0,6})(?<unit>[MG])\\n    pids_limit: (?<pids>[1-9][0-9]{0,6})\\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LimitsPattern();
}
