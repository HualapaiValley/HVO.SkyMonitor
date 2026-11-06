using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PathPrograms_UpgradeUnderPrivateGroupUmask_PassesItsOwnDestinationCheck()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        await RecordInstalledReleaseAsync(fixture, "1.2.0");
        using var newer = CandidateRelease(fixture, out _, version: "1.3.0");
        using var accounts = new PathProgramAccountScope();
        fixture.Runner.CandidateRawIngressSchema = "13";
        var previous = PathProgramsTrustTests.SetUmask(0x2);
        try
        {
            var local = Path.Combine(fixture.Root, "programs", "home", ".local");
            Directory.CreateDirectory(local);
            Assert.AreEqual((UnixFileMode)0x1fd, File.GetUnixFileMode(local), "the existing folder must reproduce umask 0002");
            var result = await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Upgrade) with
            { Release = newer.Folder, MigrationBackwardCompatible = true }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
            Assert.AreEqual("completed", result.Outcome);
            var bin = Path.Combine(local, "bin");
            Assert.AreEqual(PathPrograms.ProgramMode, File.GetUnixFileMode(bin));
            Assert.AreEqual((UnixFileMode)0x1fd, File.GetUnixFileMode(local), "existing private-group folders are preserved");
            foreach (var target in new[] { Path.Combine(bin, "hvo-sky"), Path.Combine(bin, "hvo-sky-install"),
                         Path.Combine(fixture.Root, "programs", "usr", "local", "sbin", "hvo-sky-install") })
            {
                PathPrograms.EnsureTrustedDestination(target, fixture.Root);
                StringAssert.Contains(await File.ReadAllTextAsync(target), "1.3.0", StringComparison.Ordinal);
            }
        }
        finally { _ = PathProgramsTrustTests.SetUmask(previous); }
    }
}
