using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class HostPrerequisitesTests
{
    private const string ProductRoot = "/srv/data/hvo-skymonitor";
    private const string DockerRoot = "/srv/docker";
    private const long Required = 2L * 1024 * 1024 * 1024;

    private static readonly DockerDaemonIdentity Daemon = new("daemon-1", "pi", "arm64", "29.0");
    private static readonly ContainerResourceLimits Limits = CameraAgentContainerResources.Default;

    [TestMethod]
    public void Evaluate_HostThatEnforcesLimitsWithToolsAndRoom_IsSatisfied()
    {
        var host = new FakeHostEnvironment();

        var report = HostPrerequisites.Evaluate(Supported(), ProductRoot, Required, Required, host, Limits);

        Assert.IsTrue(report.Satisfied);
        Assert.AreEqual("satisfied", report.Outcome);
        Assert.IsEmpty(report.Findings);
        StringAssert.Contains(HostPrerequisites.Render(report), "satisfied.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Evaluate_DockerWithoutLimitSupport_ReportsEveryLimitWithTheCmdlineRemedyAndControllers()
    {
        var docker = Supported() with
        {
            MemoryLimit = false,
            CpuCfsQuota = false,
            PidsLimit = null,
            Warnings = ["WARNING: No memory limit support", "WARNING: No cpu cfs quota support", "WARNING: bridge-nf-call-iptables is disabled"]
        };
        var host = new FakeHostEnvironment { Controllers = "cpuset cpu io pids" };

        var report = HostPrerequisites.Evaluate(docker, ProductRoot, Required, Required, host, Limits);

        string[] expectedCodes = ["docker-memory-limit-unsupported", "docker-cpu-quota-unsupported", "docker-pids-limit-unsupported"];
        Assert.IsFalse(report.Satisfied);
        CollectionAssert.AreEqual(expectedCodes, report.Findings.Select(static finding => finding.Code).ToArray());
        Assert.IsTrue(report.Findings.All(static finding => finding.Blocking && finding.Boundary == "resource-limits"));
        StringAssert.StartsWith(report.Findings[0].Observed, "MemoryLimit=false", StringComparison.Ordinal);
        StringAssert.StartsWith(report.Findings[2].Observed, "PidsLimit=not reported", StringComparison.Ordinal);
        foreach (var finding in report.Findings)
        {
            StringAssert.Contains(finding.Observed, "cgroup v2", StringComparison.Ordinal);
            StringAssert.Contains(finding.Observed, "/sys/fs/cgroup/cgroup.controllers: \"cpuset cpu io pids\"", StringComparison.Ordinal);
            StringAssert.Contains(finding.Observed, "\"WARNING: No memory limit support\"", StringComparison.Ordinal);
            Assert.DoesNotContain("bridge-nf-call", finding.Observed!, "An unrelated daemon warning was quoted.");
            // The disable comes from the Pi firmware, so the remedy must not send the operator to change it in the file.
            StringAssert.Contains(finding.Remediation, "the firmware, not /boot/firmware/cmdline.txt, adds cgroup_disable=memory", StringComparison.Ordinal);
            StringAssert.Contains(finding.Remediation, "append cgroup_enable=memory", StringComparison.Ordinal);
            StringAssert.Contains(finding.Remediation, "/boot/firmware/cmdline.txt", StringComparison.Ordinal);
            StringAssert.Contains(finding.Remediation, "reboot", StringComparison.Ordinal);
        }
        var rendered = HostPrerequisites.Render(report);
        StringAssert.Contains(rendered, "not met.", StringComparison.Ordinal);
        StringAssert.Contains(rendered, "docker-pids-limit-unsupported", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Evaluate_UnreadableControllers_AreNotQuoted()
    {
        var report = HostPrerequisites.Evaluate(
            Supported() with { MemoryLimit = false }, ProductRoot, Required, Required, new FakeHostEnvironment(), Limits);

        var finding = report.Findings.Single();
        Assert.AreEqual("docker-memory-limit-unsupported", finding.Code);
        Assert.DoesNotContain("cgroup.controllers", finding.Observed!);
    }

    [TestMethod]
    public void Evaluate_FreeSpace_IsMeasuredOnTheFilesystemsThatHoldEachRoot()
    {
        var host = new FakeHostEnvironment
        {
            Fallback = new HostFileSystemMeasurement("/", "8:1", 0),
            FileSystems =
            {
                [ProductRoot] = new HostFileSystemMeasurement("/srv/data", "259:2", Required),
                [DockerRoot] = new HostFileSystemMeasurement(DockerRoot, "259:3", Required - 1)
            }
        };

        var report = HostPrerequisites.Evaluate(Supported(), ProductRoot, Required, Required, host, Limits);

        // '/' has no free space at all; only the filesystems that store the roots decide.
        CollectionAssert.AreEqual(new[] { ProductRoot, DockerRoot }, host.Measured.ToArray());
        var finding = report.Findings.Single();
        Assert.AreEqual("docker-root-storage-insufficient", finding.Code);
        Assert.AreEqual(DockerRoot, finding.Path);
        StringAssert.Contains(finding.Observed, $"{Required - 1} bytes available", StringComparison.Ordinal);
        StringAssert.Contains(finding.Expected, $"at least {Required} bytes", StringComparison.Ordinal);

        host.FileSystems[ProductRoot] = new HostFileSystemMeasurement("/srv/data", "259:2", Required - 1);
        host.FileSystems[DockerRoot] = new HostFileSystemMeasurement(DockerRoot, "259:3", Required);
        var product = HostPrerequisites.Evaluate(Supported(), ProductRoot, Required, Required, host, Limits).Findings.Single();
        Assert.AreEqual("product-root-storage-insufficient", product.Code);
        Assert.AreEqual("/srv/data", product.Path);
    }

    [TestMethod]
    public void Evaluate_RootsSharingOneFilesystem_RequireTheirSum()
    {
        var host = new FakeHostEnvironment
        {
            FileSystems =
            {
                [ProductRoot] = new HostFileSystemMeasurement("/srv/data", "259:2", Required * 2 - 1),
                [DockerRoot] = new HostFileSystemMeasurement("/srv", "259:2", Required * 2 - 1)
            }
        };

        var finding = HostPrerequisites.Evaluate(Supported(), ProductRoot, Required, Required, host, Limits).Findings.Single();

        Assert.AreEqual("product-root-storage-insufficient", finding.Code);
        StringAssert.Contains(finding.Expected, $"at least {Required * 2} bytes", StringComparison.Ordinal);
        StringAssert.Contains(finding.Expected, "share this filesystem", StringComparison.Ordinal);

        host.FileSystems[ProductRoot] = new HostFileSystemMeasurement("/srv/data", "259:2", Required * 2);
        host.FileSystems[DockerRoot] = new HostFileSystemMeasurement("/srv", "259:2", Required * 2);
        Assert.IsTrue(HostPrerequisites.Evaluate(Supported(), ProductRoot, Required, Required, host, Limits).Satisfied);
    }

    [TestMethod]
    public void Evaluate_DockerRootNotReported_IsRefused()
    {
        var report = HostPrerequisites.Evaluate(
            Supported() with { DockerRootDir = null }, ProductRoot, Required, Required, new FakeHostEnvironment(), Limits);

        Assert.AreEqual("docker-root-unreported", report.Findings.Single().Code);
        Assert.IsFalse(report.Satisfied);
    }

    [TestMethod]
    [DataRow(1, "2", false)]
    [DataRow(2, "2", true)]
    [DataRow(2, "2.5", false)]
    [DataRow(3, "2.5", true)]
    [DataRow(4, "6", false)]
    public void Evaluate_CpuLimitAboveTheDockerHostCpus_IsRefused(int hostCpus, string limit, bool satisfied)
    {
        var limits = CameraAgentContainerResources.Resolve(
            decimal.Parse(limit, System.Globalization.CultureInfo.InvariantCulture), null);

        var report = HostPrerequisites.Evaluate(
            Supported() with { CpuCount = hostCpus }, ProductRoot, Required, Required, new FakeHostEnvironment(), limits);

        Assert.AreEqual(satisfied, report.Satisfied);
        if (satisfied)
        {
            Assert.IsEmpty(report.Findings);
            return;
        }
        var finding = report.Findings.Single();
        Assert.AreEqual("docker-host-cpus-insufficient", finding.Code);
        Assert.AreEqual("capacity", finding.Boundary);
        Assert.IsTrue(finding.Blocking);
        Assert.AreEqual($"NCPU={hostCpus}", finding.Observed);
        StringAssert.Contains(finding.Expected, $"at least {limit} CPUs", StringComparison.Ordinal);
        StringAssert.Contains(finding.Remediation, "never limited to fewer than 2 CPUs", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Evaluate_UnreportedCpuCount_IsRefused()
    {
        var report = HostPrerequisites.Evaluate(
            Supported() with { CpuCount = null }, ProductRoot, Required, Required, new FakeHostEnvironment(), Limits);

        var finding = report.Findings.Single();
        Assert.IsFalse(report.Satisfied);
        Assert.AreEqual("docker-host-cpus-insufficient", finding.Code);
        Assert.AreEqual("NCPU=not reported", finding.Observed);
    }

    [TestMethod]
    public void Evaluate_MemoryLimitAboveTheDockerHostMemory_IsAdvisoryAndDoesNotRefuse()
    {
        // A 4 GB Raspberry Pi reports a little under 4 GiB, so the default limit is above what the host has.
        const long fourGigabytePi = 4_142_665_728;

        var report = HostPrerequisites.Evaluate(
            Supported() with { MemoryBytes = fourGigabytePi }, ProductRoot, Required, Required, new FakeHostEnvironment(), Limits);
        var unreported = HostPrerequisites.Evaluate(
            Supported() with { MemoryBytes = null }, ProductRoot, Required, Required, new FakeHostEnvironment(), Limits);
        var enough = HostPrerequisites.Evaluate(
            Supported() with { MemoryBytes = 4L * 1024 * 1024 * 1024 }, ProductRoot, Required, Required, new FakeHostEnvironment(), Limits);

        Assert.IsTrue(report.Satisfied);
        Assert.AreEqual("satisfied", report.Outcome);
        var finding = report.Findings.Single();
        Assert.AreEqual("docker-host-memory-below-limit", finding.Code);
        Assert.AreEqual("capacity", finding.Boundary);
        Assert.IsFalse(finding.Blocking);
        Assert.AreEqual($"MemTotal={fourGigabytePi} bytes", finding.Observed);
        StringAssert.Contains(finding.Expected, "4294967296 bytes (4 GiB)", StringComparison.Ordinal);
        StringAssert.Contains(finding.Remediation, "The installation proceeds", StringComparison.Ordinal);
        StringAssert.Contains(
            HostPrerequisites.Render(report), "- [advisory] docker-host-memory-below-limit (capacity)", StringComparison.Ordinal);
        Assert.IsTrue(unreported.Satisfied);
        Assert.AreEqual("MemTotal=not reported", unreported.Findings.Single().Observed);
        Assert.IsEmpty(enough.Findings);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void LinuxHostEnvironment_MeasuresTheNearestExistingAncestor()
    {
        var existing = Path.GetTempPath().TrimEnd('/');
        var missing = Path.Combine(existing, $"hvo-missing-{Guid.NewGuid():N}", "product", "root");

        var measured = LinuxHostEnvironment.Instance.MeasureFileSystem(missing);
        var direct = LinuxHostEnvironment.Instance.MeasureFileSystem(existing);

        Assert.AreEqual(existing, measured.MeasuredPath);
        Assert.AreEqual(direct.FileSystem, measured.FileSystem);
        Assert.IsGreaterThan(0L, measured.AvailableBytes);
        Assert.IsGreaterThan(0L, LinuxHostEnvironment.AvailableBytes(missing));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void StorageRequirements_ReserveTwiceTheBundleAndArchiveOnTheProductRootAndTwiceTheArchiveForDocker()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-storage-budget-{Guid.NewGuid():N}");
        var bundle = Path.Combine(root, "catalog.bundle");
        var archive = Path.Combine(root, "cameraagent.tar");
        Directory.CreateDirectory(bundle);
        try
        {
            File.WriteAllBytes(Path.Combine(bundle, "catalog.sqlite"), new byte[3000]);
            File.WriteAllBytes(Path.Combine(bundle, "manifest.json"), new byte[1000]);
            File.WriteAllBytes(archive, new byte[700]);

            var withArchive = CameraAgentInstaller.StorageRequirements(bundle, archive);
            var withoutArchive = CameraAgentInstaller.StorageRequirements(bundle, null);

            Assert.AreEqual((2 * (4000L + 700) + HostPrerequisites.StorageHeadroomBytes, 2 * 700L + HostPrerequisites.StorageHeadroomBytes), withArchive);
            Assert.AreEqual((2 * 4000L + HostPrerequisites.StorageHeadroomBytes, HostPrerequisites.StorageHeadroomBytes), withoutArchive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// An upgrade is held to the install's limit and CPU findings (#1357), each blocking, with the same codes, the same
    /// host details and the same remedy. It is not held to tar or to the install's room, which it does not need.
    /// </summary>
    [TestMethod]
    public void EvaluateContainerLimits_Upgrade_RefusesEveryLimitDockerCannotEnforceAndTooFewCpus()
    {
        var docker = Supported() with
        {
            MemoryLimit = false,
            CpuCfsQuota = false,
            PidsLimit = null,
            CpuCount = 1,
            Warnings = ["WARNING: No memory limit support", "WARNING: No swap limit support"]
        };
        var host = new FakeHostEnvironment { Controllers = "cpuset cpu io pids" };

        var report = HostPrerequisites.EvaluateContainerLimits(docker, ProductRoot, host, Limits, rollback: false);

        string[] expectedCodes =
            ["docker-memory-limit-unsupported", "docker-cpu-quota-unsupported", "docker-pids-limit-unsupported", "docker-host-cpus-insufficient"];
        Assert.IsFalse(report.Satisfied);
        CollectionAssert.AreEqual(expectedCodes, report.Findings.Select(static finding => finding.Code).ToArray());
        Assert.IsTrue(report.Findings.All(static finding => finding.Blocking));
        Assert.IsEmpty(host.Measured, "An upgrade measures its own room; the install's storage findings are not evaluated.");
        var install = HostPrerequisites.Evaluate(docker, ProductRoot, Required, Required, host, Limits);
        CollectionAssert.AreEqual(
            install.Findings.Where(static finding => finding.Boundary is "resource-limits" or "capacity").ToArray(),
            report.Findings.ToArray());
        foreach (var finding in report.Findings.Where(static finding => finding.Boundary == "resource-limits"))
        {
            StringAssert.Contains(finding.Remediation, "append cgroup_enable=memory", StringComparison.Ordinal);
            StringAssert.Contains(finding.Remediation, "reboot", StringComparison.Ordinal);
            // Swap is not a limit CameraAgent is given; Docker's own swap warning is quoted beside the limit findings.
            StringAssert.Contains(finding.Observed, "\"WARNING: No swap limit support\"", StringComparison.Ordinal);
        }
        Assert.IsNull(HostPrerequisites.RollbackWarning(report, "rollback"), "An upgrade report is not a rollback warning.");
    }

    [TestMethod]
    public void EvaluateContainerLimits_HostThatEnforcesTheLimits_IsSatisfiedForBoth()
    {
        foreach (var rollback in new[] { false, true })
        {
            var report = HostPrerequisites.EvaluateContainerLimits(Supported(), ProductRoot, new FakeHostEnvironment(), Limits, rollback);

            Assert.IsTrue(report.Satisfied, $"rollback={rollback}");
            Assert.IsEmpty(report.Findings, $"rollback={rollback}");
            Assert.IsNull(HostPrerequisites.RollbackWarning(report, "rollback"));
            Assert.IsFalse(HostPrerequisites.WithholdsCpuLimits(report));
        }
    }

    /// <summary>
    /// A rollback is the recovery path (#1357, as HVO.RoofController#151): a limit Docker cannot enforce is advisory, so
    /// it proceeds, and its warning names each one with its consequence and the remedy. A CPU limit Docker enforces on
    /// a host without that many CPUs still refuses it, because Docker refuses to create that container; a CPU limit
    /// Docker cannot enforce is withheld, so the CPUs no longer count.
    /// </summary>
    [TestMethod]
    [DataRow("memory", false, false, true, true, 4)]
    [DataRow("cpu", false, true, false, true, 1)]
    [DataRow("pids", false, true, true, false, 4)]
    [DataRow("all", false, false, false, false, 1)]
    [DataRow("cpus", true, true, true, true, 1)]
    public void EvaluateContainerLimits_Rollback_ProceedsWithAWarningUnlessDockerWouldRefuseTheCpuLimit(
        string shape, bool refused, bool memory, bool cpu, bool pids, int cpus)
    {
        var docker = Supported() with { MemoryLimit = memory, CpuCfsQuota = cpu, PidsLimit = pids, CpuCount = cpus };

        var report = HostPrerequisites.EvaluateContainerLimits(docker, ProductRoot, new FakeHostEnvironment(), Limits, rollback: true);
        var warning = HostPrerequisites.RollbackWarning(report, "rollback");

        Assert.AreEqual(!refused, report.Satisfied, shape);
        Assert.AreEqual(!cpu, HostPrerequisites.WithholdsCpuLimits(report), shape);
        var unenforced = new[]
        {
            (memory, "docker-memory-limit-unsupported", "Docker discards the memory limit"),
            (cpu, "docker-cpu-quota-unsupported", "CameraAgent and its replay runner are started without their CPU limits"),
            (pids, "docker-pids-limit-unsupported", "Docker discards the PIDs limit")
        }.Where(static limit => !limit.Item1).ToArray();
        CollectionAssert.AreEqual(
            unenforced.Select(static limit => limit.Item2).ToArray(),
            report.Findings.Where(static finding => finding.Boundary == "resource-limits").Select(static finding => finding.Code).ToArray(),
            shape);
        Assert.IsTrue(report.Findings.Where(static finding => finding.Boundary == "resource-limits").All(static finding => !finding.Blocking), shape);
        if (refused)
        {
            Assert.AreEqual("docker-host-cpus-insufficient", report.Findings.Single(static finding => finding.Blocking).Code);
            Assert.IsNull(warning, shape);
            return;
        }
        Assert.IsFalse(report.Findings.Any(static finding => finding.Code == "docker-host-cpus-insufficient"), shape);
        Assert.IsNotNull(warning, shape);
        StringAssert.StartsWith(warning, "WARNING: THE ROLLBACK GOES AHEAD, BUT DOCKER HERE CANNOT ENFORCE", StringComparison.Ordinal, shape);
        StringAssert.Contains(warning, "an install or an upgrade is refused here until this is fixed", StringComparison.Ordinal, shape);
        foreach (var (_, code, consequence) in unenforced)
        {
            StringAssert.Contains(warning, code, StringComparison.Ordinal, shape);
            StringAssert.Contains(warning, consequence, StringComparison.Ordinal, shape);
        }
        StringAssert.Contains(warning, "append cgroup_enable=memory", StringComparison.Ordinal, shape);
        Assert.AreEqual(unenforced.Length, HostPrerequisites.RollbackWarnings(report).Count, shape);
    }

    private static DockerHostCapabilities Supported()
        => new(Daemon, MemoryLimit: true, CpuCfsQuota: true, PidsLimit: true, "2", DockerRoot, [],
            CpuCount: 4, MemoryBytes: 8L * 1024 * 1024 * 1024);
}

/// <summary>
/// A host whose filesystems, tools, and cgroup controllers are stated by the test, so shared-host disk and PATH state
/// cannot decide a prerequisite contract. Every measured path is recorded.
/// </summary>
internal sealed class FakeHostEnvironment : IHostEnvironment
{
    public Dictionary<string, HostFileSystemMeasurement> FileSystems { get; } = new(StringComparer.Ordinal);

    public HostFileSystemMeasurement Fallback { get; init; } = new("/", "8:1", long.MaxValue / 4);

    public string? Controllers { get; init; }

    public List<string> Measured { get; } = [];

    public HostFileSystemMeasurement MeasureFileSystem(string path)
    {
        Measured.Add(path);
        return FileSystems.TryGetValue(path, out var measurement) ? measurement : Fallback;
    }

    public string? ReadCgroupControllers() => Controllers;
}
