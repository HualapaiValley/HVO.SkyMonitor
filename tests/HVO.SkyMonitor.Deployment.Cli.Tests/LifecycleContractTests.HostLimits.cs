using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// A host whose Docker cannot enforce the CameraAgent container limits (#1357, install contract section 7, as
/// HVO.RoofController#151). An upgrade, its plan and its <c>--resume</c> are refused before anything changes, as an
/// install is, listing every finding and the remedy. Restore-only can go ahead with a warning on standard error
/// and in the operation's install log, and keeps every limit
/// Docker can enforce. A host that enforces the limits sees no difference.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string MemoryLimitUnsupported = "docker-memory-limit-unsupported";
    private const string CpuQuotaUnsupported = "docker-cpu-quota-unsupported";
    private const string PidsLimitUnsupported = "docker-pids-limit-unsupported";
    private const string HostCpusInsufficient = "docker-host-cpus-insufficient";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("memory")]
    [DataRow("cpu")]
    [DataRow("pids")]
    [DataRow("all")]
    [DataRow("cpus")]
    public async Task UpgradeAsync_HostThatCannotEnforceTheLimits_IsRefusedBeforeAnythingChanges(string shape)
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent);
        fixture.Runner.CandidateHeld = [];
        var expected = Degrade(fixture.Runner, shape);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            Release = release.Folder,
            ImageSource = ReleaseImageSource.Registry,
        };
        var before = FileSystemActivity.Snapshot(fixture.Root);

        foreach (var dryRun in new[] { true, false })
        {
            var name = $"{shape}: {(dryRun ? "the plan" : "the upgrade")}";
            var refused = await Assert.ThrowsExactlyAsync<HostPrerequisiteException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                request with { DryRun = dryRun }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
                error: TextWriter.Null));

            AssertRefusedFindings(refused, expected, name);
            // Nothing changed: no pull, no image Docker holds or removed, no container touched, and the whole tree as it was.
            Assert.AreEqual(0, fixture.Runner.PullCount, $"{name} pulled");
            Assert.IsEmpty(fixture.Runner.CandidateHeld!, $"{name} left Docker holding an image");
            Assert.IsEmpty(fixture.Runner.RemovedImages, name);
            Assert.AreEqual(0, fixture.Runner.ComposeUpCount + fixture.Runner.ComposeStopCount, name);
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), $"{name} changed the product root");
        }
    }

    /// <summary>
    /// An upgrade interrupted after its commit, on a host that has since lost the memory limit, is refused on
    /// <c>--resume</c> too, with nothing changed: resuming goes forward to the candidate, which the host cannot bound.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ResumeOnAHostThatLostTheMemoryLimit_IsRefusedBeforeAnythingChanges()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient { RejectNextResume = true };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));
        Assert.AreEqual(
            LifecycleOperationPhase.Committed,
            (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!.Phase);
        fixture.Runner.MemoryLimit = false;
        var before = FileSystemActivity.Snapshot(fixture.Root);
        var (ups, stops, resumes) = (fixture.Runner.ComposeUpCount, fixture.Runner.ComposeStopCount, lifecycle.ResumeCount);

        var refused = await Assert.ThrowsExactlyAsync<HostPrerequisiteException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture) with { Resume = true }, fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));

        AssertRefusedFindings(refused, [MemoryLimitUnsupported], "the resumed upgrade");
        Assert.AreEqual((ups, stops, resumes), (fixture.Runner.ComposeUpCount, fixture.Runner.ComposeStopCount, lifecycle.ResumeCount));
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "the resumed upgrade changed the product root");
    }

    /// <summary>
    /// A snapshot-backed restore-only recovery from <c>CandidateVerified</c>, on a host that has since lost CPU quota
    /// support (PR-1370-R1 F1). It stops the runtime and puts the snapshot back, so it must then start the original
    /// image from a model Docker accepts there: without its CPU limits, with every other limit. The Compose files the
    /// instance records, which restore-only authenticates, stay exactly as they were.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Upgrade_HostThatEnforcesTheLimits_GivesNoWarningAndKeepsNoInstallLog()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, $"sha256:{new string('b', 64)}", fixture.Uid, fixture.Gid);
        using var upgradeError = new StringWriter();

        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, error: upgradeError);
        Assert.AreEqual("completed", upgraded.Outcome);
        Assert.IsNull(upgraded.Warnings);
        Assert.DoesNotContain("GOES AHEAD", upgradeError.ToString());
        Assert.IsFalse(File.Exists(InstallLogPath(fixture, upgraded.OperationId!.Value)));
        Assert.DoesNotContain("withheld", await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml")));
    }

    /// <summary>Both refusals exit 3 and leave the instance untouched.</summary>
    /// <summary>Makes Docker report what <paramref name="shape"/> names, and returns the findings that refuse an upgrade.</summary>
    private static string[] Degrade(FakeRunner runner, string shape)
    {
        switch (shape)
        {
            case "memory":
                runner.MemoryLimit = false;
                return [MemoryLimitUnsupported];
            case "cpu":
                runner.CpuCfsQuota = false;
                return [CpuQuotaUnsupported];
            case "pids":
                runner.PidsLimit = null;
                return [PidsLimitUnsupported];
            case "all":
                runner.MemoryLimit = false;
                runner.CpuCfsQuota = false;
                runner.PidsLimit = false;
                return [MemoryLimitUnsupported, CpuQuotaUnsupported, PidsLimitUnsupported];
            case "cpus":
                runner.Cpus = 1;
                return [HostCpusInsufficient];
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    /// <summary>The refusal lists every finding, each blocking, and names the remedy for each limit Docker cannot enforce.</summary>
    private static void AssertRefusedFindings(HostPrerequisiteException refused, string[] expected, string name)
    {
        var report = refused.Report!;
        Assert.IsFalse(report.Satisfied, name);
        CollectionAssert.AreEqual(
            expected, report.Findings.Where(static finding => finding.Blocking).Select(static finding => finding.Code).ToArray(), name);
        foreach (var code in expected)
        {
            StringAssert.Contains(refused.Message, code, StringComparison.Ordinal, name);
        }
        foreach (var finding in report.Findings.Where(static finding => finding.Boundary == "resource-limits"))
        {
            StringAssert.Contains(finding.Remediation, "append cgroup_enable=memory", StringComparison.Ordinal, name);
            StringAssert.Contains(finding.Remediation, "reboot", StringComparison.Ordinal, name);
        }
    }

    /// <summary>The loud warning names each limit Docker cannot enforce and the remedy.</summary>
    private static void AssertRecoveryWarning(string text, string[] unenforced, string name, string operation)
    {
        StringAssert.Contains(text, $"WARNING: THE {operation} GOES AHEAD, BUT DOCKER HERE CANNOT ENFORCE", StringComparison.Ordinal, name);
        foreach (var code in new[] { MemoryLimitUnsupported, CpuQuotaUnsupported, PidsLimitUnsupported })
        {
            if (unenforced.Contains(code))
            {
                StringAssert.Contains(text, code, StringComparison.Ordinal, name);
            }
            else
            {
                Assert.DoesNotContain(code, text, name);
            }
        }
        StringAssert.Contains(text, "append cgroup_enable=memory", StringComparison.Ordinal, name);
    }

    private static string InstallLogPath(LifecycleFixture fixture, Guid operationId)
        => Path.Combine(fixture.Paths.OperationsRoot, "lifecycle", operationId.ToString("D"), "install.log");

    /// <summary>Runs <paramref name="request"/> through the installer's entry point, as the operator's command line would.</summary>
    private static async Task<(int ExitCode, string Error)> RunProgramAsync(LifecycleFixture fixture, LifecycleRequest request)
    {
        var services = ProgramServices.Production with
        {
            Parse = _ => request,
            Lifecycle = (command, token) => CameraAgentLifecycleManager.ExecuteAsync(
                command, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
                fixture.Uid, fixture.Gid, token)
        };
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await Program.RunAsync([], services, CancellationToken.None);
            return (exitCode, error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
