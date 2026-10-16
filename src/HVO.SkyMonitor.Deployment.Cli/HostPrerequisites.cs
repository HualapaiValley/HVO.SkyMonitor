using System.Globalization;
using System.Text;

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
/// is created: Docker must enforce the Compose memory, CPU and PIDs limits; <c>tar</c>, which upgrade backups run, must
/// be present; and the filesystems that hold the product root and Docker's data root must have room. Every finding is
/// reported together so an operator resolves the complete set in one pass. The CLI uses SQLite in-process and never
/// runs <c>sqlite3</c> or <c>jq</c>, so neither is required.
/// </summary>
internal static class HostPrerequisites
{
    public const int SchemaVersion = 1;

    /// <summary>Headroom required on each filesystem beyond the bytes an installation is known to write.</summary>
    public const long StorageHeadroomBytes = 1024L * 1024 * 1024;

    internal const string ResourceLimitRemediation =
        "Docker must enforce the CameraAgent Compose memory, CPU and PIDs limits. Raspberry Pi OS boots with " +
        "cgroup_disable=memory: add cgroup_enable=memory to the single line in /boot/firmware/cmdline.txt, reboot, and " +
        "confirm that docker info reports MemoryLimit, CpuCfsQuota and PidsLimit as true. On other hosts enable the " +
        "memory, cpu and pids cgroup controllers for the Docker daemon.";

    internal const string StorageRemediation =
        "Free space on that filesystem, or provide a larger one: place the product root there with " +
        "'hvo-skymonitor host set-product-root' before any instance exists, or move Docker's data-root.";

    /// <summary>The <c>docker info</c> capability fields, the finding code for each, and the limit it enforces.</summary>
    private static readonly (string Field, string Code, string Limit)[] ResourceLimits =
    [
        ("MemoryLimit", "docker-memory-limit-unsupported", "memory limit (mem_limit)"),
        ("CpuCfsQuota", "docker-cpu-quota-unsupported", "CPU quota (cpus)"),
        ("PidsLimit", "docker-pids-limit-unsupported", "PIDs limit (pids_limit)")
    ];

    public static HostPrerequisiteReport Evaluate(
        DockerHostCapabilities docker,
        string productRoot,
        long productRootRequiredBytes,
        long dockerRootRequiredBytes,
        IHostEnvironment host)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(productRoot);
        ArgumentNullException.ThrowIfNull(host);
        var findings = new List<HostPrerequisiteFinding>();
        EvaluateResourceLimits(findings, docker, host);
        EvaluateTools(findings, host);
        EvaluateStorage(findings, docker, productRoot, productRootRequiredBytes, dockerRootRequiredBytes, host);
        var satisfied = !findings.Any(static finding => finding.Blocking);
        return new HostPrerequisiteReport(
            SchemaVersion,
            satisfied ? "satisfied" : "unsatisfied",
            satisfied,
            productRoot,
            findings,
            DateTimeOffset.UtcNow);
    }

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
                $"- [{(finding.Blocking ? "blocking" : "advisory")}] {finding.Code} ({finding.Boundary}) at {finding.Path}: observed {finding.Observed ?? "none"}; expected {finding.Expected ?? "unspecified"}. {finding.Remediation}");
        }
        if (report.Findings.Count == 0)
        {
            builder.AppendLine("- Every prerequisite is met.");
        }
        return builder.ToString().TrimEnd();
    }

    private static void EvaluateResourceLimits(
        List<HostPrerequisiteFinding> findings,
        DockerHostCapabilities docker,
        IHostEnvironment host)
    {
        string? context = null;
        foreach (var (field, code, limit) in ResourceLimits)
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
                "resource-limits",
                Blocking: true,
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
            "Install tar (for example 'sudo apt-get install tar'); upgrade and rollback run it to take and restore the consistent instance backup."));
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
