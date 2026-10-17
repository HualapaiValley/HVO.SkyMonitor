using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Manual real-root contracts for the purge namespace boundary. Each case runs the published Deployment CLI as the
/// unprivileged runtime user inside a disposable private mount namespace: <c>/var/lib</c> is a namespace-private tmpfs
/// and <c>/var/lib/hvo-sky</c> is a bind of a fresh staging tree carrying the installer's real
/// <c>root:root 0755</c> namespace ownership, so the runtime user meets the kernel denial of the field incident and the
/// real <c>sudo</c> route hands only the rename and the removal to the real root helper. The host's <c>/var/lib</c>
/// and every installed instance stay untouched; Docker is only queried. Requires Linux, a non-root runner with
/// non-interactive <c>sudo</c>, <c>unshare</c> and <c>setpriv</c>, a reachable Docker daemon, and
/// <c>HVO_DEPLOYMENT_CLI_PUBLISHED</c> naming a self-contained single-file publish of the Deployment CLI.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class PurgeNamespaceRootTests
{
    private const string PublishedCliVariable = "HVO_DEPLOYMENT_CLI_PUBLISHED";

    // Mounts the private namespace, then runs the remaining arguments in it. The tmpfs hides the host's /var/lib, so the
    // product root the CLI and the helper both resolve is exactly the staged tree and nothing on the host.
    private const string NamespaceScript =
        "set -eu\n" +
        "staging=$1\n" +
        "shift\n" +
        "mount -t tmpfs -o mode=0755,size=1m hvo-purge-test /var/lib\n" +
        "mkdir -m 0755 /var/lib/hvo-sky\n" +
        "mount --bind \"$staging\" /var/lib/hvo-sky\n" +
        "exec \"$@\"\n";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PublishedCliPurgesInstanceUnderRootOwnedNamespace()
    {
        using var staging = await RootStagingAsync();
        var sibling = staging.CreateSibling();
        await staging.ClaimNamespaceAsync();
        // The defect's premise: the runtime user's own authenticated rename is denied by the root-owned namespace.
        Assert.IsFalse(SafeTreeDeletion.TryRenameChild(
            staging.Namespace, staging.Fixture.InstanceId.ToString("D"), ".purge-direct-probe",
            NativeLinux.GetNodeIdentity(staging.InstanceRoot)));

        var purged = await staging.RunCliAsync("purge");

        Assert.AreEqual(0, purged.ExitCode, purged.StandardError);
        using var result = JsonDocument.Parse(purged.StandardOutput);
        Assert.AreEqual("completed", result.RootElement.GetProperty("outcome").GetString());
        Assert.IsFalse(Path.Exists(staging.InstanceRoot));
        Assert.IsEmpty(staging.Tombstones());
        Assert.AreEqual("preserved", await File.ReadAllTextAsync(Path.Combine(sibling, "sentinel")));
        staging.AssertNamespaceRootOwned();
        var operation = await staging.RetainedOperationAsync();
        Assert.AreEqual(InstallationStatus.Completed, operation.Status);
        Assert.AreEqual(result.RootElement.GetProperty("operationId").GetGuid(), operation.OperationId);
        Assert.IsNull((await staging.ReadEvidenceAsync(operation.OperationId)).Settlement);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PublishedCliDeniedHelperLeavesTreeIntactAndResumeCompletes()
    {
        using var staging = await RootStagingAsync();
        await staging.ClaimNamespaceAsync();
        var original = NativeLinux.GetNodeIdentity(staging.InstanceRoot);
        var inventory = staging.Inventory();

        var denied = await staging.RunCliAsync("purge", denySudo: true);

        Assert.AreEqual(1, denied.ExitCode, denied.StandardOutput);
        StringAssert.Contains(denied.StandardError,
            "Purge stopped before its quarantine was confirmed; rerun the same command with --resume.", StringComparison.Ordinal);
        StringAssert.Contains(denied.StandardError, "denied by the purge test policy", StringComparison.Ordinal);
        Assert.AreEqual(original, NativeLinux.GetNodeIdentity(staging.InstanceRoot));
        CollectionAssert.AreEqual(inventory, staging.Inventory());
        Assert.IsEmpty(staging.Tombstones());
        staging.AssertNamespaceRootOwned();
        var stranded = await staging.RetainedOperationAsync();
        Assert.AreEqual(LifecycleOperationPhase.Mutating, stranded.Phase);
        Assert.AreEqual(InstallationStatus.Running, stranded.Status);

        var resumed = await staging.RunCliAsync("purge", "--resume");

        Assert.AreEqual(0, resumed.ExitCode, resumed.StandardError);
        using var result = JsonDocument.Parse(resumed.StandardOutput);
        Assert.AreEqual("completed", result.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(stranded.OperationId, result.RootElement.GetProperty("operationId").GetGuid());
        Assert.IsFalse(Path.Exists(staging.InstanceRoot));
        Assert.IsEmpty(staging.Tombstones());
        staging.AssertNamespaceRootOwned();
    }

    /// <summary>
    /// The field incident end to end: a purge stranded by a denied rename, its instance root then removed outside the
    /// CLI, is refused until the operator's disposition names the operation, then settles truthfully as already absent
    /// exactly once and never recreates the instance name.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PublishedCliSettlesStrandedPurgeWhoseRootWasRemovedOutOfBand()
    {
        using var staging = await RootStagingAsync();
        await staging.ClaimNamespaceAsync();
        Assert.AreEqual(1, (await staging.RunCliAsync("purge", denySudo: true)).ExitCode);
        var stranded = await staging.RetainedOperationAsync();
        Assert.AreEqual(LifecycleOperationPhase.Mutating, stranded.Phase);
        await staging.RemoveInstanceRootAsRootAsync();
        var namespaceEntries = staging.NamespaceEntries();
        var unconfirmed = await staging.RunCliAsync("purge", "--resume");

        Assert.AreEqual(1, unconfirmed.ExitCode, unconfirmed.StandardOutput);
        StringAssert.Contains(unconfirmed.StandardError, "its absence does not prove removal", StringComparison.Ordinal);
        StringAssert.Contains(
            unconfirmed.StandardError, $"--confirm-removed-operation-id {stranded.OperationId:D}", StringComparison.Ordinal);
        Assert.AreEqual(LifecycleOperationPhase.Mutating, (await staging.RetainedOperationAsync()).Phase);
        Assert.IsNull((await staging.ReadEvidenceAsync(stranded.OperationId)).Settlement);

        var settled = await staging.RunCliAsync("purge", "--resume", confirmRemovedOperationId: stranded.OperationId);

        Assert.AreEqual(0, settled.ExitCode, settled.StandardError);
        using var result = JsonDocument.Parse(settled.StandardOutput);
        Assert.AreEqual(
            CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement, result.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(stranded.OperationId, result.RootElement.GetProperty("operationId").GetGuid());
        CollectionAssert.AreEqual(namespaceEntries, staging.NamespaceEntries());
        var evidence = await staging.ReadEvidenceAsync(stranded.OperationId);
        Assert.AreEqual(CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement, evidence.Settlement);
        Assert.IsNotNull(evidence.SettledUtc);
        Assert.AreEqual(InstallationStatus.Completed, (await staging.RetainedOperationAsync()).Status);

        var replayed = await staging.RunCliAsync("purge", "--resume", confirmRemovedOperationId: stranded.OperationId);

        Assert.AreEqual(1, replayed.ExitCode, replayed.StandardOutput);
        StringAssert.Contains(replayed.StandardError, "No matching interrupted purge operation exists.", StringComparison.Ordinal);
        Assert.AreEqual(evidence.SettledUtc, (await staging.ReadEvidenceAsync(stranded.OperationId)).SettledUtc);
        CollectionAssert.AreEqual(namespaceEntries, staging.NamespaceEntries());
        staging.AssertNamespaceRootOwned();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PublishedHelperRefusesUnboundInvocationsAndAppliesOnlyBoundMutations()
    {
        using var staging = await RootStagingAsync();
        await staging.ClaimNamespaceAsync();
        var instanceId = staging.Fixture.InstanceId;
        var operationId = Guid.NewGuid();
        var identity = NativeLinux.GetNodeIdentity(staging.InstanceRoot);
        var quarantine = HelperArguments(PrivilegedPurgeNamespace.Action.Quarantine, instanceId, operationId, identity);

        // Invocation refusals: no root, a sudo caller other than the bound owner, or a product root other than the default.
        var unprivileged = await new ProcessRunner().RunAsync(staging.Cli, quarantine, CancellationToken.None);
        Assert.AreEqual(2, unprivileged.ExitCode, unprivileged.StandardError);
        Assert.AreEqual(2, (await staging.RunHelperAsync(
            HelperArguments(PrivilegedPurgeNamespace.Action.Quarantine, instanceId, operationId, identity with { Uid = identity.Uid + 1 }))).ExitCode);
        Assert.AreEqual(2, (await staging.RunHelperAsync(
            [.. quarantine.Select(static argument => argument == InstallRequest.DefaultProductRoot ? "/var/lib/hvo-sky-other" : argument)])).ExitCode);

        // Identity and namespace refusals leave the instance where it is.
        await AssertHelperRefusedAsync(staging,
            HelperArguments(PrivilegedPurgeNamespace.Action.Quarantine, instanceId, operationId, identity with { Inode = identity.Inode + 1 }),
            "does not match the authenticated purge identity");
        await staging.AsRootAsync("chmod", "0775", "--", staging.Namespace);
        await AssertHelperRefusedAsync(staging, quarantine, "Product namespace component 'cameraagents'");
        await staging.AsRootAsync("chmod", "0755", "--", staging.Namespace);
        await staging.AsRootAsync("chown", $"{staging.Fixture.Uid}:{staging.Fixture.Gid}", "--", staging.Fixture.Root);
        await AssertHelperRefusedAsync(staging, quarantine, "Product namespace component 'skymonitor'");
        await staging.AsRootAsync("chown", "0:0", "--", staging.Fixture.Root);
        var linked = Guid.NewGuid();
        await staging.AsRootAsync("ln", "-s", "--", staging.InstanceRoot, Path.Combine(staging.Namespace, linked.ToString("D")));
        await AssertHelperRefusedAsync(staging,
            HelperArguments(PrivilegedPurgeNamespace.Action.Quarantine, linked, operationId, identity),
            "The instance root does not match the authenticated purge identity.");
        await staging.AsRootAsync("rm", "--", Path.Combine(staging.Namespace, linked.ToString("D")));

        // The bound quarantine renames exactly the authenticated tree; removal waits for the runtime user to empty it.
        var applied = await staging.RunHelperAsync(quarantine);
        Assert.AreEqual(0, applied.ExitCode, applied.StandardError);
        var tombstoneName = PrivilegedPurgeNamespace.TombstoneName(instanceId, operationId);
        Assert.IsFalse(Path.Exists(staging.InstanceRoot));
        Assert.AreEqual(identity.Inode, NativeLinux.GetNodeIdentity(Path.Combine(staging.Namespace, tombstoneName)).Inode);
        await AssertHelperRefusedAsync(staging, quarantine, "The instance root to quarantine is absent.");
        await AssertHelperRefusedAsync(staging,
            HelperArguments(PrivilegedPurgeNamespace.Action.Remove, instanceId, operationId, identity),
            "The purge tombstone still contains directories.");
        var emptied = SafeTreeDeletion.EmptyChild(
            staging.Namespace, tombstoneName, staging.Fixture.Uid, staging.Fixture.Gid, identity);
        var remove = HelperArguments(PrivilegedPurgeNamespace.Action.Remove, instanceId, operationId, emptied);
        var removed = await staging.RunHelperAsync(remove);
        Assert.AreEqual(0, removed.ExitCode, removed.StandardError);
        Assert.IsEmpty(staging.Tombstones());
        await AssertHelperRefusedAsync(staging, remove, "The purge tombstone to remove is absent.");
        staging.AssertNamespaceRootOwned();
    }

    private static async Task AssertHelperRefusedAsync(RootStaging staging, IReadOnlyList<string> arguments, string reason)
    {
        var entries = staging.NamespaceEntries();
        var refused = await staging.RunHelperAsync(arguments);
        Assert.AreEqual(1, refused.ExitCode, refused.StandardError);
        StringAssert.Contains(refused.StandardError, reason, StringComparison.Ordinal);
        CollectionAssert.AreEqual(entries, staging.NamespaceEntries());
    }

    private static string[] HelperArguments(
        PrivilegedPurgeNamespace.Action action,
        Guid instanceId,
        Guid operationId,
        UnixNodeIdentity identity)
        => [.. PrivilegedPurgeNamespace.HelperArguments(action, InstallRequest.DefaultProductRoot, instanceId, operationId, identity)
            .Skip(2)];

    private async Task<RootStaging> RootStagingAsync()
    {
        if (NativeLinux.getuid() == 0)
            Assert.Inconclusive("The real-root purge contracts run the CLI as the unprivileged runtime user; run them as a non-root user.");
        var cli = Environment.GetEnvironmentVariable(PublishedCliVariable);
        if (string.IsNullOrEmpty(cli) || !Path.IsPathFullyQualified(cli) || !File.Exists(cli))
            Assert.Inconclusive(
                $"{PublishedCliVariable} must name a published Deployment CLI, for example after 'dotnet publish " +
                "src/HVO.SkyMonitor.Deployment.Cli -r linux-x64 -c Release -o <dir>'.");
        var runner = new ProcessRunner();
        var probe = await runner.RunAsync(
            "sudo", ["-n", "unshare", "--mount", "--propagation", "private", "--", "setpriv", "--version"], CancellationToken.None);
        if (probe.ExitCode != 0)
            Assert.Inconclusive($"Non-interactive sudo with unshare and setpriv is required: {probe.StandardError.Trim()}");
        DockerDaemonIdentity daemon;
        try
        {
            daemon = await new DockerClient(runner).PreflightAsync(CancellationToken.None);
        }
        catch (InstallerException exception)
        {
            Assert.Inconclusive($"A reachable Docker daemon is required for the purge reference checks: {exception.Message}");
            throw;
        }
        return await RootStaging.CreateAsync(cli, daemon, TestContext);
    }

    /// <summary>
    /// An uninstalled instance staged in a disposable tree that records the default product root, so that once the
    /// tree is bound at <c>/var/lib/hvo-sky</c> in a private namespace it is the installation the CLI resolves.
    /// </summary>
    private sealed class RootStaging : IDisposable
    {
        private readonly string _shims;
        private readonly TestContext _context;
        private bool _claimed;

        private RootStaging(LifecycleContractTests.LifecycleFixture fixture, string cli, string shims, TestContext context)
        {
            Fixture = fixture;
            Cli = cli;
            _shims = shims;
            _context = context;
        }

        public LifecycleContractTests.LifecycleFixture Fixture { get; }
        public string Cli { get; }
        public string Namespace => PrivilegedPurgeNamespace.NamespaceRoot(Fixture.Root);
        public string InstanceRoot => Fixture.Paths.InstanceRoot;

        public static async Task<RootStaging> CreateAsync(string cli, DockerDaemonIdentity daemon, TestContext context)
        {
            var fixture = await LifecycleContractTests.LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
            var recorded = InstallationPaths.Create(InstallRequest.DefaultProductRoot, fixture.InstanceId, fixture.Manifest.Catalog.CatalogId);
            var catalog = fixture.Manifest.Catalog with { InstallRoot = recorded.CatalogRoot };
            var manifest = fixture.Manifest with
            {
                ProductRoot = recorded.ProductRoot,
                ConfigRoot = recorded.ConfigRoot,
                StateRoot = recorded.StateRoot,
                Catalog = catalog,
                DockerDaemon = daemon
            };
            var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None) with
            {
                ProductRoot = recorded.ProductRoot,
                InstanceRoot = recorded.InstanceRoot,
                ConfigRoot = recorded.ConfigRoot,
                StateRoot = recorded.StateRoot,
                Catalog = catalog,
                DockerDaemon = daemon
            };
            await SafeFileSystem.WriteJsonAtomicAsync(
                fixture.Paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
            await SafeFileSystem.WriteJsonAtomicAsync(
                fixture.Paths.ResultPath, result, DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
            var environment = Path.Combine(fixture.Paths.ConfigRoot, "compose", "instance.env");
            SafeFileSystem.WriteTextAtomic(environment, (await File.ReadAllTextAsync(environment))
                .Replace(fixture.Paths.CatalogRoot, recorded.CatalogRoot, StringComparison.Ordinal));
            var shims = Directory.CreateTempSubdirectory("hvo-purge-shims-").FullName;
            // The denied route is the CLI's real sudo invocation answered by a policy that refuses it.
            var sudo = Path.Combine(shims, "sudo");
            await File.WriteAllTextAsync(sudo, "#!/bin/sh\necho 'sudo: denied by the purge test policy' >&2\nexit 1\n");
            File.SetUnixFileMode(sudo, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return new RootStaging(fixture, cli, shims, context);
        }

        public string CreateSibling()
        {
            var sibling = Path.Combine(Namespace, Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(sibling);
            File.SetUnixFileMode(sibling, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(sibling, "sentinel"), "preserved");
            return sibling;
        }

        /// <summary>Gives the staged shared namespace the ownership internal preparation gives the real one.</summary>
        public async Task ClaimNamespaceAsync()
        {
            _claimed = true;
            foreach (var directory in SharedNamespace())
            {
                await AsRootAsync("chown", "0:0", "--", directory);
                await AsRootAsync("chmod", "0755", "--", directory);
            }
            AssertNamespaceRootOwned();
        }

        public void AssertNamespaceRootOwned()
        {
            foreach (var directory in SharedNamespace())
            {
                var identity = NativeLinux.GetNodeIdentity(directory);
                Assert.AreEqual(0u, identity.Uid, directory);
                Assert.AreEqual(0u, identity.Gid, directory);
                Assert.AreEqual((UnixFileMode)0b111_101_101, identity.Mode, directory);
            }
        }

        public Task<ProcessResult> RunCliAsync(
            string command, string? flag = null, bool denySudo = false, Guid? confirmRemovedOperationId = null)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
            List<string> environment =
            [
                $"HOME={Environment.GetEnvironmentVariable("HOME")}",
                $"PATH={(denySudo ? _shims + ":" : string.Empty)}{path}"
            ];
            foreach (var name in new[] { "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value)) environment.Add($"{name}={value}");
            }
            var instanceId = Fixture.InstanceId.ToString("D");
            List<string> arguments =
            [
                "setpriv", $"--reuid={Fixture.Uid}", $"--regid={Fixture.Gid}", "--init-groups", "--",
                "env", "-i", .. environment, Cli, "cameraagent", command,
                "--instance-id", instanceId, "--confirm-instance-id", instanceId, "--json"
            ];
            if (flag is not null) arguments.Add(flag);
            if (confirmRemovedOperationId is { } operationId)
                arguments.AddRange(["--confirm-removed-operation-id", operationId.ToString("D")]);
            return InNamespaceAsync(arguments);
        }

        public Task<ProcessResult> RunHelperAsync(IReadOnlyList<string> helperArguments)
            => InNamespaceAsync([Cli, .. helperArguments]);

        public async Task AsRootAsync(params string[] arguments)
        {
            // Only paths inside this disposable staging tree are ever handed to root.
            Assert.IsTrue(arguments.Skip(1).Where(static argument => argument.StartsWith('/'))
                .All(argument => argument.StartsWith(Fixture.Root + "/", StringComparison.Ordinal) || argument == Fixture.Root));
            var completed = await new ProcessRunner().RunAsync("sudo", ["-n", .. arguments], CancellationToken.None);
            Assert.AreEqual(0, completed.ExitCode, completed.StandardError);
        }

        public Task RemoveInstanceRootAsRootAsync()
            => AsRootAsync("rm", "-rf", "--one-file-system", "--", InstanceRoot);

        public string[] Tombstones()
            => Directory.EnumerateFileSystemEntries(Namespace, ".purge-*").Order(StringComparer.Ordinal).ToArray();

        public string[] NamespaceEntries()
            => Directory.EnumerateFileSystemEntries(Namespace).Order(StringComparer.Ordinal).ToArray();

        // Every lifecycle run opens the instance's own lock file, so the comparison covers the tree except that file.
        public string[] Inventory()
            => SafeTreeDeletion.CaptureChildInventory(Namespace, Fixture.InstanceId.ToString("D"), Fixture.Uid, Fixture.Gid)
                .Where(static node => node.RelativePath != ".deployment.lock")
                .Select(static node => $"{node.RelativePath}:{node.Inode}:{node.Mode}:{node.LinkCount}")
                .ToArray();

        public async Task<LifecycleOperationState> RetainedOperationAsync()
            => await CameraAgentLifecycleManager.ReadOperationAsync(Fixture.Paths.LifecycleStatePath, CancellationToken.None)
               ?? throw new AssertFailedException("The purge journal is missing.");

        public async Task<PurgeDeletionEvidence> ReadEvidenceAsync(Guid operationId)
        {
            await using var stream = SafeFileSystem.OpenOwnerFileRead(
                Path.Combine(Fixture.Paths.OperationsRoot, $"purge-{operationId:D}.evidence.json"));
            return await CameraAgentLifecycleManager.DeserializeRetainedAsync(
                stream, DeploymentJsonContext.Default.PurgeDeletionEvidence, "purge deletion evidence", CancellationToken.None);
        }

        public void Dispose()
        {
            try
            {
                if (_claimed)
                {
                    // Ownership returns to the runtime user only for the staged namespace directories this fixture claimed.
                    foreach (var directory in SharedNamespace())
                    {
                        var restored = new ProcessRunner().RunAsync(
                            "sudo", ["-n", "chown", $"{Fixture.Uid}:{Fixture.Gid}", "--", directory], CancellationToken.None)
                            .GetAwaiter().GetResult();
                        Assert.AreEqual(0, restored.ExitCode, restored.StandardError);
                    }
                }
            }
            finally
            {
                Directory.Delete(_shims, recursive: true);
                Fixture.Dispose();
            }
        }

        private string[] SharedNamespace() => [Fixture.Root, Namespace, Path.Combine(Fixture.Root, "catalogs")];

        private async Task<ProcessResult> InNamespaceAsync(IReadOnlyList<string> command)
        {
            var completed = await new ProcessRunner().RunAsync(
                "sudo",
                ["-n", "unshare", "--mount", "--propagation", "private", "--", "sh", "-c", NamespaceScript, "sh", Fixture.Root, .. command],
                CancellationToken.None);
            _context.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"$ {string.Join(' ', command)}\nexit {completed.ExitCode}\nstdout: {completed.StandardOutput.Trim()}\nstderr: {completed.StandardError.Trim()}"));
            return completed;
        }
    }
}
