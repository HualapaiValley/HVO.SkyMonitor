using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class InstallerFlowTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PathPrograms_InstallAndPlan_ReportVerifiedProgramsAndRecordThem(bool plan)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-path-programs-{Guid.NewGuid():N}");
        using var release = ReleaseFixture.Create(root);
        var instanceId = Guid.NewGuid();
        var image = release.ConfigDigest($"linux/{ReleaseAcquirer.HostImageArchitecture()}");
        var runner = new InstallerProcessRunner(image, root, instanceId) { Labels = release.Labels() };
        var request = ReleaseRequest(root, RequireCatalogBundle(), instanceId, release.Folder) with { DryRun = plan };
        var destinations = new[]
        {
            Path.Combine(root, "programs", "home", ".local", "bin", "hvo-sky"),
            Path.Combine(root, "programs", "home", ".local", "bin", "hvo-sky-install"),
            Path.Combine(root, "programs", "usr", "local", "sbin", "hvo-sky-install")
        };
        try
        {
            var result = await CameraAgentInstaller.InstallAsync(request, runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe);

            foreach (var target in destinations)
            {
                Assert.IsTrue(result.Plan!.Items.Any(item => item.Target == target), $"plan omitted {target}");
                Assert.AreEqual(!plan, File.Exists(target));
                if (!plan)
                {
                    StringAssert.Contains(await File.ReadAllTextAsync(target), release.Version, StringComparison.Ordinal);
                    Assert.AreEqual((UnixFileMode)0x1ed, File.GetUnixFileMode(target));
                }
            }
            if (plan)
            {
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "programs")), "a plan must not create PATH folders");
            }
            else
            {
                using var record = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "operations", "install.json")));
                Assert.AreEqual(3, record.RootElement.GetProperty("instances")[0].GetProperty("programs").GetArrayLength());
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("hvo-sky")]
    [DataRow("hvo-sky-install")]
    public async Task PathPrograms_MismatchedReleaseProgram_IsRefusedBeforeDockerOrWrites(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-path-mismatch-{Guid.NewGuid():N}");
        using var release = ReleaseFixture.Create(root);
        var runtime = ReleaseAcquirer.HostImageArchitecture() == "amd64" ? "linux-x64" : "linux-arm64";
        var asset = Path.Combine(release.Folder, $"{name}-{runtime}");
        var bytes = await File.ReadAllBytesAsync(asset);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(asset, bytes);
        var instanceId = Guid.NewGuid();
        var runner = new InstallerProcessRunner(release.ConfigDigest($"linux/{ReleaseAcquirer.HostImageArchitecture()}"), root, instanceId)
        { Labels = release.Labels() };
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                ReleaseRequest(root, RequireCatalogBundle(), instanceId, release.Folder), runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            Assert.IsTrue(failure.IsRefusal);
            StringAssert.Contains(failure.Message, "SHA-256", StringComparison.Ordinal);
            Assert.AreEqual(0, runner.LoadCount);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "programs")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "cameraagents")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
