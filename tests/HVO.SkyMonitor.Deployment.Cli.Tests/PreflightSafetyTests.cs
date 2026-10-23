using System.Globalization;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
public sealed class PreflightSafetyTests
{
    [TestMethod]
    [DataRow("symlink")]
    [DataRow("hardlink")]
    [DataRow("busy")]
    public async Task UnsafeRunLock_IsNeverFollowedOrTruncated(string kind)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); Directory.CreateDirectory(f.Host.StateRoot, SafeFileSystem.OwnerDirectoryMode); Directory.CreateDirectory(f.Host.EvidenceRoot, SafeFileSystem.OwnerDirectoryMode);
        var sentinel = f.Host.Directory + "/lock-sentinel"; DeploymentFakeHost.Write(sentinel, "preserve-lock-target\n"); var path = f.Host.StateRoot + "/run.lock";
        using var held = kind == "busy" ? new PartialPrepareState(f.Host.StateRoot, f.Host.EvidenceRoot, false, _ => { }, "preflight") : null;
        if (kind == "symlink") File.CreateSymbolicLink(path, sentinel);
        if (kind == "hardlink") Assert.AreEqual(0, (await new ProcessRunner().RunAsync("ln", [sentinel, path], CancellationToken.None)).ExitCode);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); Assert.AreEqual("preserve-lock-target\n", DeploymentFakeHost.Read(sentinel)); Assert.AreEqual(0, f.Opens);
    }
    [TestMethod]
    [DataRow(420)]
    [DataRow(438)]
    public async Task OwnedSingleLinkRunLock_IsNormalizedAfterAcquisition(int mode)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); Directory.CreateDirectory(f.Host.StateRoot, SafeFileSystem.OwnerDirectoryMode); Directory.CreateDirectory(f.Host.EvidenceRoot, SafeFileSystem.OwnerDirectoryMode);
        var path = f.Host.StateRoot + "/run.lock"; DeploymentFakeHost.Write(path, "retain-lock-content\n"); File.SetUnixFileMode(path, (UnixFileMode)mode);
        await f.RunAsync(); Assert.AreEqual((UnixFileMode.UserRead | UnixFileMode.UserWrite), File.GetUnixFileMode(path)); Assert.AreEqual("retain-lock-content\n", DeploymentFakeHost.Read(path));
    }
    [TestMethod]
    [DataRow("symlink", "unsafe")]
    [DataRow("file", "unsafe")]
    [DataRow("writable", "unsafe")]
    [DataRow("missing", "needs_prepare")]
    [DataRow("ready", "ready")]
    public void RuntimeRoot_EveryExistingComponentIsInspected(string kind, string expected)
    {
        using var f = new PreflightFixture(); var directory = f.Host.Directory + "/root with spaces"; Directory.CreateDirectory(directory, SafeFileSystem.OwnerDirectoryMode);
        var path = directory + "/runtime";
        if (kind == "symlink") Directory.CreateSymbolicLink(path, directory);
        if (kind == "file") DeploymentFakeHost.Write(path, "do-not-touch");
        if (kind == "writable") File.SetUnixFileMode(directory, SafeFileSystem.OwnerDirectoryMode | UnixFileMode.GroupWrite);
        if (kind == "ready") Directory.CreateDirectory(path, SafeFileSystem.OwnerDirectoryMode);
        using var files = new RootFiles(); Assert.AreEqual(expected, PreflightProbe.RuntimeRoot(files, path).Status);
    }
    [TestMethod]
    public void TrustedRootOwnedRuntime_RequiresPreparationForANonrootCaller()
    {
        using var f = new PreflightFixture(); var root = f.Host.Directory + "/root-owned-runtime"; Directory.CreateDirectory(root, SafeFileSystem.OwnerDirectoryMode);
        using var files = new RootFiles(rootOwned: true); Assert.AreEqual("needs_prepare", PreflightProbe.RuntimeRoot(files, root).Status);
    }
    [TestMethod]
    public async Task ProductRootSymlink_IsRejectedLocallyBeforeAnyHostContact()
    {
        using var f = new PreflightFixture(); var root = f.Host.Directory + "/product-link"; f.Host.SetProductRoot(root); await f.StageAsync();
        Directory.Move(root, root + "-real"); Directory.CreateSymbolicLink(root, root + "-real");
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); StringAssert.Contains(failure.Message, "noncanonical-product-root", StringComparison.Ordinal); Assert.AreEqual(0, f.Opens);
    }
    private sealed class RootFiles(bool rootOwned = false) : IDeploymentFiles
    {
        private readonly DeploymentFakeFiles files = new();
        public string Call(string operation, params string[] arguments)
        {
            if (operation == "uid") return rootOwned ? "4242" : NativeLinux.getuid().ToString(CultureInfo.InvariantCulture);
            var value = files.Call(operation, arguments);
            return rootOwned && operation == "stat" && value != "absent" ? "0 " + value[(value.IndexOf(' ', StringComparison.Ordinal) + 1)..] : value;
        }
        public void Dispose() => files.Dispose();
    }
    [TestMethod]
    [DataRow("secret")]
    [DataRow("certificate")]
    public async Task PrivateUpload_PublicationAndMoveFailuresVerifyAbsence(string kind)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); var target = f.Host.Contract.Targets[1];
        var root = f.Host.Physical(target.RuntimeRoot); Directory.CreateDirectory(root + "/upload-test", SafeFileSystem.OwnerDirectoryMode);
        var source = f.Host.Directory + "/private-source"; DeploymentFakeHost.Write(source, "private-upload\n");
        using var state = new PartialPrepareState(f.Host.StateRoot, f.Host.EvidenceRoot, false, _ => { }, "preflight"); PreflightPrivateUploads.Initialize(state, f.Host.Contract, f.Host.StateRoot);
        var copy = new PreflightUploadTransport(f.Host, f.Host.Contract, state, f.Host.StateRoot, "smoke", _ => throw Error("private-registry-publication-failed"));
        Assert.ThrowsExactly<InstallerException>(() => copy.Copy(source, target, target.RuntimeRoot + "/upload-test/" + kind, CancellationToken.None));
        Assert.IsFalse(File.Exists(root + "/upload-test/" + kind)); AssertRegistryEmpty(f);
        f.Host.PrimitiveHandler = (op, _) => op == "replace" ? throw Error("move-failed") : null;
        copy = new PreflightUploadTransport(f.Host, f.Host.Contract, state, f.Host.StateRoot, "smoke");
        Assert.ThrowsExactly<InstallerException>(() => copy.Copy(source, target, target.RuntimeRoot + "/upload-test/destination", CancellationToken.None));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(root + "/.hvo-deploy/uploads").Any()); AssertRegistryEmpty(f);
    }
    [TestMethod]
    public async Task PrivateCampaignRegistry_PersistsReinitializationAndVerifiedCleanup()
    {
        using var f = new PreflightFixture(); await f.StageAsync(); var target = f.Host.Contract.Targets[1];
        var root = f.Host.Physical(target.RuntimeRoot); Directory.CreateDirectory(root + "/.hvo-deploy/uploads", SafeFileSystem.OwnerDirectoryMode);
        using var state = new PartialPrepareState(f.Host.StateRoot, f.Host.EvidenceRoot, false, _ => { }, "preflight"); PreflightPrivateUploads.Initialize(state, f.Host.Contract, f.Host.StateRoot);
        var copy = new PreflightUploadTransport(f.Host, f.Host.Contract, state, f.Host.StateRoot, "acceptance-run");
        var path = target.RuntimeRoot + "/.hvo-deploy/uploads/hvo-upload-acceptance-run-123-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp";
        copy.Register(target, target.RuntimeRoot + "/.hvo-deploy/campaign-phase14/login.html", "remote-private");
        DeploymentFakeHost.Write(f.Host.Physical(path), "private\n"); copy.Register(target, path, "upload-temp"); PreflightPrivateUploads.Initialize(state, f.Host.Contract, f.Host.StateRoot);
        var entries = JsonNode.Parse(DeploymentFakeHost.Read(f.Host.StateRoot + "/private-upload-registry.json"))!.AsArray(); Assert.AreEqual(2, entries.Count); Assert.IsTrue(entries.All(n => n!.Text("phase") == "acceptance-run"));
        f.Host.PrimitiveHandler = null; copy.Cleanup(target, path, CancellationToken.None); copy.Forget(path);
        Assert.IsFalse(File.Exists(f.Host.Physical(path))); entries = JsonNode.Parse(DeploymentFakeHost.Read(f.Host.StateRoot + "/private-upload-registry.json"))!.AsArray(); Assert.AreEqual(1, entries.Count); Assert.AreEqual("remote-private", entries[0]!.Text("kind"));
    }
    [TestMethod]
    public async Task TamperedRegistry_RefusesBeforeHostAndPreservesArbitraryFile()
    {
        using var f = new PreflightFixture(); await f.StageAsync(); Directory.CreateDirectory(f.Host.StateRoot, SafeFileSystem.OwnerDirectoryMode); Directory.CreateDirectory(f.Host.EvidenceRoot, SafeFileSystem.OwnerDirectoryMode);
        var path = f.Host.Directory + "/arbitrary-do-not-delete"; DeploymentFakeHost.Write(path, "retain\n");
        DeploymentFakeHost.WriteJson(f.Host.StateRoot + "/private-upload-registry.json", new JsonArray(new JsonObject { ["phase"] = "up", ["kind"] = "upload-temp", ["target"] = f.Host.Inventory["cameraAgents"]![0]!.DeepClone(), ["path"] = path }));
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); Assert.AreEqual("retain\n", DeploymentFakeHost.Read(path)); Assert.AreEqual(0, f.Opens);
    }
    [TestMethod]
    [DataRow("volume")]
    [DataRow("network")]
    public async Task DockerNotFound_ProvesAbsence_TransportAndAuthenticationNeverDo(string kind)
    {
        using var f = new PreflightFixture(); await f.StageAsync();
        f.Host.DockerResponse = _ => new ProcessResult(1, "", "Error: No such " + kind + ": absent-" + kind);
        await PreflightUploadTransport.RequireResourceAbsentAsync(f.Host, "shared-context", kind, "absent-" + kind, CancellationToken.None);
        f.Host.DockerResponse = _ => new ProcessResult(1, "", "transport/authentication failed");
        await Assert.ThrowsExactlyAsync<InstallerException>(() => PreflightUploadTransport.RequireResourceAbsentAsync(f.Host, "shared-context", kind, "absent-" + kind, CancellationToken.None));
    }
    private static void AssertRegistryEmpty(PreflightFixture f) => Assert.AreEqual(0, JsonNode.Parse(DeploymentFakeHost.Read(f.Host.StateRoot + "/private-upload-registry.json"))!.AsArray().Count);
}
