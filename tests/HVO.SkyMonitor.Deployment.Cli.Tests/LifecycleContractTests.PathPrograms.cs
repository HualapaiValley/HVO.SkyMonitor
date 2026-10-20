using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PathPrograms_UpgradeRollbackAndRemoval_KeepCommandsWithTheirRelease(bool plan)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        await RecordInstalledReleaseAsync(fixture, "1.2.0");
        var targets = await SeedPathProgramsAsync(fixture);
        using var newer = CandidateRelease(fixture, out _, version: "1.3.0");
        fixture.Runner.CandidateRawIngressSchema = "13";
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        var upgrade = fixture.Request(LifecycleOperationKind.Upgrade) with
        { Release = newer.Folder, MigrationBackwardCompatible = true, DryRun = plan };

        var result = await CameraAgentLifecycleManager.ExecuteAsync(upgrade, fixture.Runner, _ => new FakeLifecycleClient(),
            _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None);
        if (plan)
        {
            foreach (var target in targets)
            {
                Assert.IsTrue(result.Plan!.Items.Any(item => item.Target == target && item.Action == DeploymentPlanAction.Change));
                StringAssert.Contains(await File.ReadAllTextAsync(target), "1.2.0", StringComparison.Ordinal);
                Assert.IsFalse(File.Exists(target + ".previous"));
            }
            return;
        }
        foreach (var target in targets)
        {
            StringAssert.Contains(await File.ReadAllTextAsync(target), "1.3.0", StringComparison.Ordinal);
            StringAssert.Contains(await File.ReadAllTextAsync(target + ".previous"), "1.2.0", StringComparison.Ordinal);
        }
        var location = InstallRecordLocation.For(fixture.Root, InstallRequest.DefaultProductRoot);
        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(location.Path)))
            Assert.AreEqual(3, json.RootElement.GetProperty("instances")[0].GetProperty("programs").GetArrayLength());

        await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner,
            _ => new FakeLifecycleClient(), _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);
        foreach (var target in targets)
        {
            StringAssert.Contains(await File.ReadAllTextAsync(target), "1.2.0", StringComparison.Ordinal);
            StringAssert.Contains(await File.ReadAllTextAsync(target + ".previous"), "1.3.0", StringComparison.Ordinal);
        }
        await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner,
            _ => new FakeLifecycleClient(), null, fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.IsFalse(File.Exists(targets[0]));
        Assert.IsFalse(File.Exists(targets[0] + ".previous"));
        Assert.IsTrue(File.Exists(targets[1]), "the preserved instance needs its durable reinstall command");
        await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Reinstall), fixture.Runner,
            _ => new FakeLifecycleClient(), _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None);
        StringAssert.Contains(await File.ReadAllTextAsync(targets[0]), "1.2.0", StringComparison.Ordinal);
        await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner,
            _ => new FakeLifecycleClient(), null, fixture.Uid, fixture.Gid, CancellationToken.None);
        await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Purge) with
        { ConfirmationInstanceId = fixture.InstanceId }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);
        foreach (var target in targets)
        {
            Assert.IsFalse(File.Exists(target));
            Assert.IsFalse(File.Exists(target + ".previous"));
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PathPrograms_PublicationInterruptedAfterRename_ResumeKeepsTheOriginalPreviousCopy()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        await RecordInstalledReleaseAsync(fixture, "1.2.0");
        var targets = await SeedPathProgramsAsync(fixture);
        using var newer = CandidateRelease(fixture, out _, version: "1.3.0");
        fixture.Runner.CandidateRawIngressSchema = "13";
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        { Release = newer.Folder, MigrationBackwardCompatible = true };
        var previousHook = NativeLinux.DirectoryFlushed;
        var interrupted = false;
        try
        {
            NativeLinux.DirectoryFlushed = path =>
            {
                if (!interrupted && path == Path.GetDirectoryName(targets[0]) && File.Exists(targets[0]) &&
                    File.ReadAllText(targets[0]).Contains("1.3.0", StringComparison.Ordinal))
                {
                    interrupted = true;
                    throw new InstallerException("Injected interruption after program rename.");
                }
            };
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
                fixture.Uid, fixture.Gid, CancellationToken.None));
        }
        finally
        {
            NativeLinux.DirectoryFlushed = previousHook;
        }
        Assert.IsTrue(interrupted);
        var journal = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Committed, journal!.Phase);
        var resumed = await CameraAgentLifecycleManager.ExecuteAsync(request with { Resume = true }, fixture.Runner,
            _ => new FakeLifecycleClient(), _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", resumed.Outcome);
        foreach (var target in targets)
        {
            StringAssert.Contains(await File.ReadAllTextAsync(target), "1.3.0", StringComparison.Ordinal);
            StringAssert.Contains(await File.ReadAllTextAsync(target + ".previous"), "1.2.0", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PathPrograms_SharedCommands_AreKeptUntilTheLastOwnerRemovesThem()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var targets = await SeedPathProgramsAsync(fixture);
        await PathPrograms.SettleAsync(fixture.Paths, InstallRequest.DefaultProductRoot, null, fixture.Manifest.Image.ImageId,
            CancellationToken.None);
        var otherId = Guid.NewGuid();
        var other = InstallationPaths.Create(fixture.Root, otherId, fixture.Manifest.Catalog.CatalogId);
        SafeFileSystem.CreateOwnerDirectory(other.DeploymentStateRoot);
        var otherManifest = fixture.Manifest with { InstanceId = otherId };
        await SafeFileSystem.WriteJsonAtomicAsync(other.ManifestPath, otherManifest,
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        using var release = ReleaseFixture.Create(fixture.Root, version: "1.3.0");
        await PathPrograms.SettleAsync(other, InstallRequest.DefaultProductRoot, release.Folder,
            release.ConfigDigest($"linux/{ReleaseAcquirer.HostImageArchitecture()}"), CancellationToken.None);
        await PathPrograms.RemoveAsync(fixture.Paths, InstallRequest.DefaultProductRoot, purge: false, CancellationToken.None);
        Assert.IsTrue(File.Exists(targets[0]), "another installed instance needs the client");
        await SafeFileSystem.WriteJsonAtomicAsync(other.ManifestPath,
            otherManifest with { LifecycleCondition = InstanceLifecycleCondition.Uninstalled },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await PathPrograms.RemoveAsync(fixture.Paths, InstallRequest.DefaultProductRoot, purge: false, CancellationToken.None);
        Assert.IsFalse(File.Exists(targets[0]));
        await PathPrograms.RemoveAsync(fixture.Paths, InstallRequest.DefaultProductRoot, purge: true, CancellationToken.None);
        Assert.IsTrue(File.Exists(targets[1]), "another preserved instance needs the installer");
        SafeFileSystem.MakeTreeOwnerWritable(other.InstanceRoot);
        Directory.Delete(other.InstanceRoot, recursive: true);
        await PathPrograms.RemoveAsync(fixture.Paths, InstallRequest.DefaultProductRoot, purge: true, CancellationToken.None);
        foreach (var target in targets) Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("hvo-sky")]
    [DataRow("hvo-sky-install")]
    public async Task PathPrograms_UpgradeWithCorruptProgram_RefusesBeforeLoadingOrChangingTheInstance(string name)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        using var release = CandidateRelease(fixture, out _);
        var runtime = ReleaseAcquirer.HostImageArchitecture() == "amd64" ? "linux-x64" : "linux-arm64";
        var source = Path.Combine(release.Folder, $"{name}-{runtime}");
        var bytes = await File.ReadAllBytesAsync(source);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(source, bytes);
        var manifest = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder, MigrationBackwardCompatible = true },
            fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        Assert.IsTrue(failure.IsRefusal);
        Assert.AreEqual(0, fixture.Runner.LoadCount);
        Assert.AreEqual(0, fixture.Runner.PullCount);
        Assert.AreEqual(manifest, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PathPrograms_RollbackWithMissingOrCorruptRetainedClient_RefusesBeforeTheImageChanges(bool missing)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        await RecordInstalledReleaseAsync(fixture, "1.2.0");
        await SeedPathProgramsAsync(fixture);
        using var release = CandidateRelease(fixture, out var candidateImageId, version: "1.3.0");
        fixture.Runner.CandidateRawIngressSchema = "13";
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Upgrade) with
        { Release = release.Folder, MigrationBackwardCompatible = true }, fixture.Runner, _ => new FakeLifecycleClient(),
            _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData("hvo-sky 1.2.0"u8));
        var retained = PathPrograms.Cached(fixture.Paths, sha256);
        if (missing) File.Delete(retained);
        else await File.WriteAllTextAsync(retained, "tampered");
        var journal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));
        Assert.IsTrue(failure.IsRefusal);
        Assert.AreEqual(candidateImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(journal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
    }

    private static async Task<string[]> SeedPathProgramsAsync(LifecycleFixture fixture)
    {
        var user = Path.Combine(fixture.Root, "programs", "home", ".local", "bin");
        var system = Path.Combine(fixture.Root, "programs", "usr", "local", "sbin");
        var targets = new[] { Path.Combine(user, "hvo-sky"), Path.Combine(user, "hvo-sky-install"), Path.Combine(system, "hvo-sky-install") };
        var cache = Path.Combine(fixture.Paths.DeploymentStateRoot, "programs");
        Directory.CreateDirectory(cache);
        var programs = new List<object>();
        foreach (var target in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var name = Path.GetFileName(target);
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{name} 1.2.0");
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await File.WriteAllBytesAsync(target, bytes);
            File.SetUnixFileMode(target, (UnixFileMode)0x1ed);
            await File.WriteAllBytesAsync(Path.Combine(cache, sha256), bytes);
            File.SetUnixFileMode(Path.Combine(cache, sha256), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            programs.Add(new { name, target, sha256 });
        }
        var json = JsonSerializer.Serialize(new { imageId = fixture.Manifest.Image.ImageId, version = "1.2.0", programs });
        foreach (var name in new[] { "path-programs.json", $"path-programs-{fixture.Manifest.Image.ImageId[7..]}.json" })
        {
            var path = Path.Combine(fixture.Paths.DeploymentStateRoot, name);
            await File.WriteAllTextAsync(path, json);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return targets;
    }
}
