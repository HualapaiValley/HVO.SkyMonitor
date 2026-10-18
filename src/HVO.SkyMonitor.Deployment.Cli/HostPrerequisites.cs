using System.Globalization;
using System.Text;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>The filesystem that stores a path, measured at the path's nearest existing ancestor.</summary>
/// <param name="MeasuredPath">The nearest existing ancestor that was measured.</param>
/// <param name="FileSystem">An identity of the filesystem (its device), equal for two paths on the same filesystem.</param>
/// <param name="AvailableBytes">Bytes available to the invoking user on that filesystem.</param>
internal sealed record HostFileSystemMeasurement(string MeasuredPath, string FileSystem, long AvailableBytes);

/// <summary>The host facts the installation prerequisites read, separated so their evaluation is testable.</summary>
internal interface IHostEnvironment
{
    /// <summary>Measures the filesystem that holds <paramref name="path"/>, which need not exist yet.</summary>
    HostFileSystemMeasurement MeasureFileSystem(string path);

    /// <summary>Whether an executable named <paramref name="name"/> is found on <c>PATH</c>, as process start resolves it.</summary>
    bool HasExecutable(string name);

    /// <summary>The cgroup v2 controllers the kernel offers, or <see langword="null"/> when they cannot be read.</summary>
    string? ReadCgroupControllers();
}

internal sealed class LinuxHostEnvironment : IHostEnvironment
{
    public const string CgroupControllersPath = "/sys/fs/cgroup/cgroup.controllers";
    private const int MaximumControllersLength = 512;

    public static LinuxHostEnvironment Instance { get; } = new();

    public HostFileSystemMeasurement MeasureFileSystem(string path)
    {
        var existing = NearestExistingAncestor(path);
        var identity = NativeLinux.GetTargetNodeIdentity(existing);
        return new HostFileSystemMeasurement(
            existing,
            string.Create(CultureInfo.InvariantCulture, $"{identity.DeviceMajor}:{identity.DeviceMinor}"),
            AvailableBytes(existing));
    }

    /// <summary>
    /// The bytes available on the filesystem that holds <paramref name="path"/>. <see cref="DriveInfo"/> on Linux
    /// measures exactly the path it is given (<c>statvfs</c>), so the nearest existing ancestor is measured rather than
    /// the root of the path's name, which would always be <c>/</c>.
    /// </summary>
    public static long AvailableBytes(string path) => new DriveInfo(NearestExistingAncestor(path)).AvailableFreeSpace;

    internal static string NearestExistingAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current)
                ?? throw new InstallerException($"Could not identify the filesystem that stores '{path}'.");
        }
        return current;
    }

    public bool HasExecutable(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) &&
                (File.GetUnixFileMode(candidate) &
                 (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return true;
            }
        }
        return false;
    }

    public string? ReadCgroupControllers()
    {
        try
        {
            var value = File.ReadAllText(CgroupControllersPath).Trim();
            return value.Length <= MaximumControllersLength ? value : value[..MaximumControllersLength];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed record HostPrerequisiteFinding(
    string Code,
    string Boundary,
    bool Blocking,
    string Path,
    string? Observed,
    string? Expected,
    string Remediation);

internal sealed record HostPrerequisiteReport(
    int SchemaVersion,
    string Outcome,
    bool Satisfied,
    string ProductRoot,
    IReadOnlyList<HostPrerequisiteFinding> Findings,
    DateTimeOffset EvaluatedUtc);

/// <summary>Refuses an installation whose host cannot run it as deployed, carrying every finding at once.</summary>
internal sealed class HostPrerequisiteException : Exception
{
    public HostPrerequisiteException()
    {
    }

    public HostPrerequisiteException(string message)
        : base(message)
    {
    }

    public HostPrerequisiteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public HostPrerequisiteException(HostPrerequisiteReport report)
        : base(Describe(report))
    {
        Report = report;
    }

    public HostPrerequisiteReport? Report { get; }

    private static string Describe(HostPrerequisiteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var codes = string.Join(", ", report.Findings.Where(static finding => finding.Blocking)
            .Select(static finding => finding.Code).Distinct(StringComparer.Ordinal));
        return $"Host prerequisites are not met ({codes}); resolve every reported finding and rerun.";
    }
}

/// <summary>
/// The host prerequisites <c>cameraagent install</c> and <c>cameraagent install --dry-run</c> evaluate before anything
/// is created: Docker must enforce the Compose memory, CPU and PIDs limits and its host must have the CPUs the CPU
/// limit names; <c>tar</c>, which an archive of the whole instance needs, must be present; and the filesystems that hold the product root
/// and Docker's data root must have room. A memory limit above the host's memory is advisory. Every finding is
/// reported together so an operator resolves the complete set in one pass. The CLI uses SQLite in-process and never
/// runs <c>sqlite3</c> or <c>jq</c>, so neither is required. An upgrade and a rollback are held to the limit and CPU
/// findings (<see cref="EvaluateContainerLimits"/>, #1357).
/// </summary>
internal static class HostPrerequisites
{
    public const int SchemaVersion = 1;

    /// <summary>Headroom required on each filesystem beyond the bytes an installation is known to write.</summary>
    public const long StorageHeadroomBytes = 1024L * 1024 * 1024;

    internal const string ResourceLimitRemediation =
        "Docker must enforce the CameraAgent Compose memory, CPU and PIDs limits. On a Raspberry Pi the firmware, not " +
        "/boot/firmware/cmdline.txt, adds cgroup_disable=memory to the kernel command line: append cgroup_enable=memory " +
        "to the single line in that file, reboot, and confirm that docker info reports MemoryLimit, CpuCfsQuota and " +
        "PidsLimit as true. On other hosts enable the memory, cpu and pids cgroup controllers for the Docker daemon.";

    internal const string CpuCapacityRemediation =
        "Docker refuses a CPU limit larger than the CPUs its host has. Use a host whose Docker daemon reports at least " +
        "that many CPUs, or lower --cpus; CameraAgent is never limited to fewer than 2 CPUs, so a host with fewer is not supported.";

    internal const string MemoryCapacityRemediation =
        "The installation proceeds, but a memory limit the host cannot reach does not stop CameraAgent from exhausting " +
        "the host's memory. Add memory, or lower --memory; CameraAgent is never limited to less than 4 GiB.";

    internal const string StorageRemediation =
        "Free space on that filesystem, or provide a larger one: place the product root there with " +
        "'hvo-sky-install host set-product-root' before any instance exists, or move Docker's data-root.";

    private const string ResourceLimitsBoundary = "resource-limits";

    private const string CpuQuotaUnsupportedCode = "docker-cpu-quota-unsupported";

    /// <summary>
    /// The <c>docker info</c> capability fields, the finding code for each, the limit it enforces, its name, and what a
    /// recovery that goes ahead without it means for CameraAgent (#1357).
    /// </summary>
    private static readonly (string Field, string Code, string Limit, string Name, string Consequence)[] ResourceLimits =
    [
        ("MemoryLimit", "docker-memory-limit-unsupported", "memory limit (mem_limit)", "memory",
            "Docker discards the memory limit, so CameraAgent can exhaust the host's memory."),
        ("CpuCfsQuota", CpuQuotaUnsupportedCode, "CPU quota (cpus)", "CPU",
            "Docker refuses to create a container with a CPU limit here, so CameraAgent and its replay runner are started without their CPU limits."),
        ("PidsLimit", "docker-pids-limit-unsupported", "PIDs limit (pids_limit)", "PIDs",
            "Docker discards the PIDs limit, so CameraAgent's processes and threads are not bounded.")
    ];

    public static HostPrerequisiteReport Evaluate(
        DockerHostCapabilities docker,
        string productRoot,
        long productRootRequiredBytes,
        long dockerRootRequiredBytes,
        IHostEnvironment host,
        ContainerResourceLimits resources)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(productRoot);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(resources);
        var findings = new List<HostPrerequisiteFinding>();
        EvaluateResourceLimits(findings, docker, host, blocking: true);
        EvaluateCapacity(findings, docker, resources, checkCpus: true);
        EvaluateTools(findings, host);
        EvaluateStorage(findings, docker, productRoot, productRootRequiredBytes, dockerRootRequiredBytes, host);
        return Report(productRoot, findings);
    }

    private static HostPrerequisiteReport Report(string productRoot, List<HostPrerequisiteFinding> findings)
    {
        var satisfied = !findings.Any(static finding => finding.Blocking);
        return new HostPrerequisiteReport(
            SchemaVersion,
            satisfied ? "satisfied" : "unsatisfied",
            satisfied,
            productRoot,
            findings,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The prerequisites an upgrade or a rollback is held to before it changes anything (#1357): the limit and CPU
    /// findings of <see cref="Evaluate"/>, by the same checks. An image change measures its own room and runs no
    /// <c>tar</c>, so it is not held to those. An upgrade is refused by any blocking finding, as an install is. A
    /// <paramref name="rollback"/> is the recovery path (install contract section 7), so a limit Docker cannot enforce
    /// is advisory and <see cref="RollbackWarning"/> names it. A CPU limit Docker enforces still needs the CPUs it names,
    /// since Docker refuses that container; one Docker cannot enforce is withheld, so the CPUs no longer count.
    /// </summary>
    public static HostPrerequisiteReport EvaluateContainerLimits(
        DockerHostCapabilities docker,
        string productRoot,
        IHostEnvironment host,
        ContainerResourceLimits resources,
        bool rollback)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(productRoot);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(resources);
        var findings = new List<HostPrerequisiteFinding>();
        EvaluateResourceLimits(findings, docker, host, blocking: !rollback);
        EvaluateCapacity(findings, docker, resources, checkCpus: !rollback || docker.CpuCfsQuota == true);
        return Report(productRoot, findings);
    }

    /// <summary>
    /// Whether Docker cannot enforce a CPU limit here. Docker refuses to create a container with one on such a host,
    /// unlike a memory or PIDs limit, which it discards with a warning, so a rollback withholds every CPU limit
    /// (<see cref="CameraAgentContainerResources.WithoutCpuLimits"/>).
    /// </summary>
    public static bool WithholdsCpuLimits(HostPrerequisiteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Findings.Any(static finding => finding.Code == CpuQuotaUnsupportedCode);
    }

    /// <summary>
    /// The loud warning a recovery <paramref name="operation"/> (a rollback or a restore-only recovery) gives before it
    /// changes anything, on standard error and in the operation's install log, when Docker cannot enforce some of the
    /// CameraAgent limits: each one, what it means for CameraAgent, the host's details and the remedy.
    /// <see langword="null"/> when Docker enforces them all.
    /// </summary>
    public static string? RollbackWarning(HostPrerequisiteReport report, string operation)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrEmpty(operation);
        var unenforced = UnenforcedLimits(report);
        if (unenforced.Count == 0)
        {
            return null;
        }
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"WARNING: THE {operation.ToUpperInvariant()} GOES AHEAD, BUT DOCKER HERE CANNOT ENFORCE THESE CAMERAAGENT LIMITS, so CameraAgent runs WITHOUT them.");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"A {operation} is a recovery path, so it is not refused (install contract section 7); an install or an upgrade is refused here until this is fixed. Every limit Docker enforces is kept.");
        foreach (var finding in unenforced)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {DescribeUnenforced(finding)}");
        }
        builder.Append(ResourceLimitRemediation);
        return builder.ToString();
    }

    /// <summary>
    /// The limits a recovery goes ahead without, as the warnings of its structured result: a <c>--json</c> invocation
    /// reserves standard error for one-line JSON objects, as an install reports its advisory findings.
    /// </summary>
    public static IReadOnlyList<InstallationWarning> RollbackWarnings(HostPrerequisiteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return UnenforcedLimits(report)
            .Select(static finding => new InstallationWarning(finding.Code, $"{DescribeUnenforced(finding)} {ResourceLimitRemediation}"))
            .ToArray();
    }

    /// <summary>The limits a recovery goes ahead without, by name, such as <c>memory and PIDs</c>; empty when there are none.</summary>
    public static IReadOnlyList<string> UnenforcedLimitNames(HostPrerequisiteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return UnenforcedLimits(report)
            .Select(static finding => ResourceLimits.Single(limit => limit.Code == finding.Code).Name)
            .ToArray();
    }

    private static List<HostPrerequisiteFinding> UnenforcedLimits(HostPrerequisiteReport report)
        => report.Findings.Where(static finding => !finding.Blocking && finding.Boundary == ResourceLimitsBoundary).ToList();

    private static string DescribeUnenforced(HostPrerequisiteFinding finding)
        => $"{finding.Code} at {finding.Path}: observed {finding.Observed ?? "none"}. " +
           ResourceLimits.Single(limit => limit.Code == finding.Code).Consequence;

    /// <summary>Renders every finding at once so an operator resolves the complete prerequisite set in one pass.</summary>
    public static string Render(HostPrerequisiteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"CameraAgent host prerequisites for product root {report.ProductRoot}: ");
        builder.AppendLine(report.Satisfied ? "satisfied." : "not met.");
        foreach (var finding in report.Findings)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- [{(finding.Blocking ? "blocking" : "advisory")}] {finding.Code} ({finding.Boundary}) at {Describe(finding)}");
        }
        if (report.Findings.Count == 0)
        {
            builder.AppendLine("- Every prerequisite is met.");
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The advisory findings of a report that does not refuse the installation, as the warnings its structured result
    /// carries. A <c>--json</c> invocation does not render them, because standard error is reserved for an error object.
    /// </summary>
    public static IReadOnlyList<InstallationWarning> Warnings(HostPrerequisiteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Findings
            .Where(static finding => !finding.Blocking)
            .Select(static finding => new InstallationWarning(finding.Code, Describe(finding)))
            .ToArray();
    }

    /// <summary>Where a finding was measured, what was observed and expected there, and its remedy.</summary>
    private static string Describe(HostPrerequisiteFinding finding)
        => $"{finding.Path}: observed {finding.Observed ?? "none"}; expected {finding.Expected ?? "unspecified"}. {finding.Remediation}";

    private static void EvaluateResourceLimits(
        List<HostPrerequisiteFinding> findings,
        DockerHostCapabilities docker,
        IHostEnvironment host,
        bool blocking)
    {
        string? context = null;
        foreach (var (field, code, limit, _, _) in ResourceLimits)
        {
            var supported = field switch
            {
                "MemoryLimit" => docker.MemoryLimit,
                "CpuCfsQuota" => docker.CpuCfsQuota,
                _ => docker.PidsLimit
            };
            if (supported == true)
            {
                continue;
            }
            context ??= DescribeCgroups(docker, host);
            findings.Add(new HostPrerequisiteFinding(
                code,
                ResourceLimitsBoundary,
                blocking,
                $"docker info .{field}",
                $"{field}={(supported is null ? "not reported" : "false")}{context}",
                $"{field}=true so Docker enforces the CameraAgent {limit}",
                ResourceLimitRemediation));
        }
    }

    private static string DescribeCgroups(DockerHostCapabilities docker, IHostEnvironment host)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(docker.CgroupVersion))
        {
            builder.Append(CultureInfo.InvariantCulture, $"; cgroup v{docker.CgroupVersion}");
        }
        if (host.ReadCgroupControllers() is { } controllers)
        {
            builder.Append(CultureInfo.InvariantCulture, $"; {LinuxHostEnvironment.CgroupControllersPath}: \"{controllers}\"");
        }
        foreach (var warning in docker.Warnings.Where(static warning =>
                     warning.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
                     warning.Contains("cfs", StringComparison.OrdinalIgnoreCase) ||
                     warning.Contains("cgroup", StringComparison.OrdinalIgnoreCase)))
        {
            builder.Append(CultureInfo.InvariantCulture, $"; Docker warns \"{warning.Trim()}\"");
        }
        return builder.ToString();
    }

    /// <summary>
    /// Compares the CameraAgent limits with the CPUs and memory <c>docker info</c> reports for the Docker host, which is
    /// the host the container runs on. Docker refuses to create a container whose CPU quota exceeds its host's CPUs, so
    /// that is refused here rather than when Compose starts it. A memory limit above the host's memory is applied and
    /// CameraAgent runs, so it is advisory: refusing it would turn away hosts that report slightly under 4 GiB.
    /// </summary>
    private static void EvaluateCapacity(
        List<HostPrerequisiteFinding> findings,
        DockerHostCapabilities docker,
        ContainerResourceLimits resources,
        bool checkCpus)
    {
        var cpus = CameraAgentContainerResources.FormatCpus(resources.Cpus);
        var cpuCount = docker.CpuCount ?? 0;
        if (checkCpus && cpuCount < resources.Cpus)
        {
            findings.Add(new HostPrerequisiteFinding(
                "docker-host-cpus-insufficient",
                "capacity",
                Blocking: true,
                "docker info .NCPU",
                docker.CpuCount is null ? "NCPU=not reported" : string.Create(CultureInfo.InvariantCulture, $"NCPU={cpuCount}"),
                $"at least {cpus} CPUs for the CameraAgent CPU limit",
                CpuCapacityRemediation));
        }
        var memoryBytes = docker.MemoryBytes ?? 0;
        if (memoryBytes < resources.MemoryBytes)
        {
            findings.Add(new HostPrerequisiteFinding(
                "docker-host-memory-below-limit",
                "capacity",
                Blocking: false,
                "docker info .MemTotal",
                docker.MemoryBytes is null
                    ? "MemTotal=not reported"
                    : string.Create(CultureInfo.InvariantCulture, $"MemTotal={memoryBytes} bytes"),
                string.Create(CultureInfo.InvariantCulture,
                    $"at least {resources.MemoryBytes} bytes ({CameraAgentContainerResources.DescribeMemory(resources.MemoryBytes)}) for the CameraAgent memory limit"),
                MemoryCapacityRemediation));
        }
    }

    private static void EvaluateTools(List<HostPrerequisiteFinding> findings, IHostEnvironment host)
    {
        if (host.HasExecutable("tar"))
        {
            return;
        }
        findings.Add(new HostPrerequisiteFinding(
            "tar-missing",
            "host-tools",
            Blocking: true,
            "PATH",
            "no executable tar on PATH",
            "an executable tar",
            "Install tar (for example 'sudo apt-get install tar'); an archive of the whole instance needs it."));
    }

    private static void EvaluateStorage(
        List<HostPrerequisiteFinding> findings,
        DockerHostCapabilities docker,
        string productRoot,
        long productRootRequiredBytes,
        long dockerRootRequiredBytes,
        IHostEnvironment host)
    {
        var product = host.MeasureFileSystem(productRoot);
        if (string.IsNullOrWhiteSpace(docker.DockerRootDir) || !Path.IsPathFullyQualified(docker.DockerRootDir))
        {
            findings.Add(new HostPrerequisiteFinding(
                "docker-root-unreported",
                "storage",
                Blocking: true,
                "docker info .DockerRootDir",
                docker.DockerRootDir ?? "not reported",
                "the absolute path of Docker's data root",
                "Use a local Docker Engine that reports its DockerRootDir so the space for the CameraAgent image can be verified."));
            AddIfInsufficient(findings, "product-root-storage-insufficient", product, productRootRequiredBytes,
                $"the product root {productRoot}");
            return;
        }

        var dockerRoot = host.MeasureFileSystem(docker.DockerRootDir);
        if (string.Equals(product.FileSystem, dockerRoot.FileSystem, StringComparison.Ordinal))
        {
            AddIfInsufficient(findings, "product-root-storage-insufficient", product,
                checked(productRootRequiredBytes + dockerRootRequiredBytes),
                $"the product root {productRoot} ({productRootRequiredBytes} bytes) and Docker's data root {docker.DockerRootDir} ({dockerRootRequiredBytes} bytes), which share this filesystem");
            return;
        }
        AddIfInsufficient(findings, "product-root-storage-insufficient", product, productRootRequiredBytes,
            $"the product root {productRoot}");
        AddIfInsufficient(findings, "docker-root-storage-insufficient", dockerRoot, dockerRootRequiredBytes,
            $"Docker's data root {docker.DockerRootDir}");
    }

    private static void AddIfInsufficient(
        List<HostPrerequisiteFinding> findings,
        string code,
        HostFileSystemMeasurement measurement,
        long requiredBytes,
        string purpose)
    {
        if (measurement.AvailableBytes >= requiredBytes)
        {
            return;
        }
        findings.Add(new HostPrerequisiteFinding(
            code,
            "storage",
            Blocking: true,
            measurement.MeasuredPath,
            string.Create(CultureInfo.InvariantCulture,
                $"{measurement.AvailableBytes} bytes available on the filesystem holding {measurement.MeasuredPath}"),
            string.Create(CultureInfo.InvariantCulture, $"at least {requiredBytes} bytes for {purpose}"),
            StorageRemediation));
    }
}
