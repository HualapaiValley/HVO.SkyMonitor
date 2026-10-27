using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class ExistingDownTests
{
    [TestMethod]
    [DataRow("write", false, "error")]
    [DataRow("write", false, "cancel")]
    [DataRow("write", true, "crash")]
    [DataRow("file-sync", false, "error")]
    [DataRow("file-sync", true, "cancel")]
    [DataRow("directory-sync", false, "error")]
    [DataRow("directory-sync", true, "crash")]
    [DataRow("reference-sync", false, "error")]
    [DataRow("reference-sync", true, "cancel")]
    [DataRow("cleanup", false, "cancel")]
    [DataRow("cleanup", true, "crash")]
    [DataRow("cleanup", true, "cancel")]
    public async Task StoppedRecovery_RestartPublicationAndCleanupInterruptionsRemainResumable(string point, bool after, string failure)
    {
        using var fixture = new ExistingDownFixture { KeepStoppedContainers = true };
        var (seeded, setting) = await StageStoppedBeforeLoginAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        var fired = false; var fileSynced = false; var directorySynced = false;
        var referenceRemoved = false; var referenceSynced = false;
        bool AtBoundary(string operation, string[] args) => point switch
        {
            "write" => operation == "write" && args[0] == setting,
            "file-sync" => operation == "sync" && args[0].StartsWith("/proc/", StringComparison.Ordinal),
            "directory-sync" => operation == "sync" && args[0] == Path.GetDirectoryName(setting),
            "reference-sync" => operation == "sync" && args[0] == Path.GetDirectoryName(setting) && referenceRemoved,
            _ => operation == "unlink" && args[0] == seeded[1]
        };
        void Interrupt(string operation, string[] args)
        {
            if (fired || !AtBoundary(operation, args)) return;
            fired = true;
            // An interrupted printf can leave an empty, safely owned file; the next attempt must repair it.
            if (point == "write" && after) DeploymentFakeHost.Write(fixture.Host.Physical(setting), "");
            if (failure == "error") throw new IOException("simulated restart-setting publication failure");
            if (failure == "crash") throw new PartialPrepareCrashException();
            cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested();
        }
        fixture.Host.BeforePrimitive = (operation, args) =>
        {
            if (operation == "unlink" && seeded.Contains(args[0], StringComparer.Ordinal))
                Assert.IsTrue(fileSynced && directorySynced, "Credentials must survive until both restart-setting syncs succeed.");
            if (operation == "unlink" && args[0] == seeded[0])
                Assert.IsTrue(referenceSynced, "The password reference must be durably absent before its target is erased.");
            if (!after) Interrupt(operation, args);
        };
        fixture.Host.AfterPrimitive = (operation, args) =>
        {
            if (operation == "sync" && args[0].StartsWith("/proc/", StringComparison.Ordinal)) fileSynced = true;
            if (operation == "sync" && args[0] == Path.GetDirectoryName(setting)) directorySynced = true;
            if (operation == "unlink" && args[0] == seeded[1]) referenceRemoved = true;
            if (operation == "sync" && args[0] == Path.GetDirectoryName(setting) && referenceRemoved) referenceSynced = true;
            if (after) Interrupt(operation, args);
        };

        if (failure == "error") await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync(token: cancellation.Token));
        else if (failure == "crash") await Assert.ThrowsAsync<PartialPrepareCrashException>(() => fixture.RunAsync());
        else await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync(token: cancellation.Token));

        Assert.IsTrue(fired);
        Assert.AreEqual(0, fixture.Stops);
        Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        Assert.AreEqual("intent", Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/down-ledger.json")).Array("resources").Single(n => n!.Text("resource") == "cameraagent:east")!.Text("status"));
        if (point == "reference-sync")
        {
            Assert.IsTrue(File.Exists(fixture.Host.Physical(seeded[0])));
            AssertCameraStartupConfiguration(fixture, "east");
        }
        else if (point != "cleanup")
        {
            foreach (var path in seeded) Assert.IsTrue(File.Exists(fixture.Host.Physical(path)), path);
            var registry = System.Text.Json.Nodes.JsonNode.Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/private-upload-registry.json"));
            Assert.AreEqual("[]", Canonical(registry));
        }
        else
        {
            Assert.AreEqual("true", DeploymentFakeHost.Read(fixture.Host.Physical(setting)));
            AssertCameraStartupConfiguration(fixture, "east");
        }
        fixture.Host.BeforePrimitive = null; fixture.Host.AfterPrimitive = null;

        var result = await fixture.RunAsync();

        Assert.AreEqual("passed", result.Text("phaseStatus"));
        AssertCameraStartupConfiguration(fixture, "east");
        Assert.AreEqual(2, fixture.Stops);
        Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        AssertPrivateAbsent(fixture); AssertPreserved(fixture);
    }

    [TestMethod]
    [DataRow("symlink")]
    [DataRow("hardlink")]
    [DataRow("public")]
    [DataRow("directory")]
    public async Task StoppedRecovery_UnsafeRestartSettingRefusesBeforeCredentialRemoval(string hazard)
    {
        using var fixture = new ExistingDownFixture();
        var (seeded, setting) = await StageStoppedBeforeLoginAsync(fixture);
        var path = fixture.Host.Physical(setting);
        var unrelated = fixture.Host.Physical(fixture.Host.Contract.Targets.Single(t => t.Name == "east").RuntimeRoot + "/unrelated-data");
        if (hazard == "symlink") File.CreateSymbolicLink(path, unrelated);
        else if (hazard == "hardlink") NativeLinux.CreateHardLink(unrelated, path);
        else if (hazard == "directory") Directory.CreateDirectory(path, SafeFileSystem.OwnerDirectoryMode);
        else { DeploymentFakeHost.Write(path, "true"); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); }
        var before = NativeLinux.GetNodeIdentity(path);

        var error = await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync());

        StringAssert.Contains(error.Message, "unsafe-private-file", StringComparison.Ordinal);
        Assert.AreEqual(before, NativeLinux.GetNodeIdentity(path));
        foreach (var credential in seeded) Assert.IsTrue(File.Exists(fixture.Host.Physical(credential)), credential);
        Assert.AreEqual(0, fixture.Stops); Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        AssertPreserved(fixture);
        if (hazard == "directory") Directory.Delete(path); else File.Delete(path);
        await fixture.RunAsync();
        AssertCameraStartupConfiguration(fixture, "east");
        AssertPrivateAbsent(fixture); AssertPreserved(fixture);
    }

    [TestMethod]
    [DataRow("true")]
    [DataRow("false")]
    [DataRow("")]
    public async Task StoppedRecovery_RepairsPartialSettingsAndPreservesPublishedSettingOnRetry(string content)
    {
        using var fixture = new ExistingDownFixture();
        var (seeded, setting) = await StageStoppedBeforeLoginAsync(fixture);
        var path = fixture.Host.Physical(setting);
        DeploymentFakeHost.Write(path, content);
        var before = NativeLinux.GetNodeIdentity(path);
        fixture.Host.BeforePrimitive = (operation, args) =>
        {
            if (operation == "unlink" && args[0] == seeded[0]) throw new PartialPrepareCrashException();
        };
        await Assert.ThrowsAsync<PartialPrepareCrashException>(() => fixture.RunAsync());
        Assert.AreEqual("true", DeploymentFakeHost.Read(path));
        if (content == "true") Assert.AreEqual(before, NativeLinux.GetNodeIdentity(path), "A valid published setting must never be unlinked on retry.");
        var published = NativeLinux.GetNodeIdentity(path);
        fixture.Host.BeforePrimitive = null;

        await fixture.RunAsync();

        Assert.AreEqual(published, NativeLinux.GetNodeIdentity(path));
        AssertCameraStartupConfiguration(fixture, "east");
        Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        AssertPrivateAbsent(fixture); AssertPreserved(fixture);
    }

    [TestMethod]
    public async Task StoppedRecovery_DoesNotTrustSuccessfulWriteWithIncorrectContents()
    {
        using var fixture = new ExistingDownFixture();
        var (seeded, setting) = await StageStoppedBeforeLoginAsync(fixture);
        fixture.Host.AfterPrimitive = (operation, args) =>
        {
            if (operation == "write" && args[0] == setting) DeploymentFakeHost.Write(fixture.Host.Physical(setting), "false");
        };

        var error = await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync());

        StringAssert.Contains(error.Message, "restart-setting-invalid", StringComparison.Ordinal);
        foreach (var path in seeded) Assert.IsTrue(File.Exists(fixture.Host.Physical(path)), path);
        Assert.AreEqual(0, fixture.Stops); Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        fixture.Host.AfterPrimitive = null;
        await fixture.RunAsync();
        AssertCameraStartupConfiguration(fixture, "east");
        AssertPrivateAbsent(fixture); AssertPreserved(fixture);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StoppedRecovery_RealSshPrimitivesPublishRestartValidConfiguration(bool present)
    {
        using var fixture = new ExistingDownFixture { KeepStoppedContainers = present };
        fixture.Host.SetProductRoot(fixture.Host.LogicalRoot + "/remote/quoted space '$;literal/skymonitor");
        fixture.Host.UseShellPrimitives = true;
        await StageStoppedBeforeLoginAsync(fixture);

        var result = await fixture.RunAsync();

        Assert.AreEqual("passed", result.Text("phaseStatus"));
        AssertCameraStartupConfiguration(fixture, "east");
        Assert.AreEqual(2, fixture.Stops); Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        AssertPrivateAbsent(fixture); AssertPreserved(fixture);
    }

    private static async Task<(string[] Seeded, string Setting)> StageStoppedBeforeLoginAsync(ExistingDownFixture fixture)
    {
        await fixture.StageAsync();
        var target = fixture.Host.Contract.Targets.Single(t => t.Name == "east");
        var seeded = new[] { target.RuntimeRoot + "/config/private/owner-password", target.RuntimeRoot + "/config/secrets/LocalIdentity__AdminPasswordFile" };
        DeploymentFakeHost.Write(fixture.Host.Physical(seeded[0]), "seeded-credential");
        DeploymentFakeHost.Write(fixture.Host.Physical(seeded[1]), "/run/hvo-private/owner-password");
        await Assert.ThrowsAsync<PartialPrepareCrashException>(() => fixture.RunAsync(point =>
        {
            if (point != "abrupt-after-down-commit") return;
            if (Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/down-ledger.json")).Array("resources").Any(n => n!.Text("resource") == "cameraagent:east")) throw new PartialPrepareCrashException();
        }));
        Assert.AreEqual(0, fixture.Stops); Assert.AreEqual(0, fixture.Queries.Count);
        fixture.StopCameraExternally("east");
        return (seeded, target.RuntimeRoot + "/config/secrets/LocalIdentity__AllowMissingAdminPassword");
    }
}
