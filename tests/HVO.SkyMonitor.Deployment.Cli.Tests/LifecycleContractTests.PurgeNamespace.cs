using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The purge contracts for a <c>cameraagents</c> namespace the runtime user cannot write. The fixture namespace is made
/// read-only so that the unprivileged rename and removal meet a real kernel permission denial; the simulated
/// privileged helper parses the exact arguments the CLI hands to <c>sudo</c> and applies them through the production
/// helper core, temporarily standing in for root's write access. The root ownership, <c>SUDO_UID</c> binding, and
/// namespace walk of the real helper are proven separately by the Manual real-root contracts.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode ReadOnlyNamespace =
        UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_ReadOnlyNamespaceHandsOnlyRenameAndRemovalToBoundHelper()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        var sibling = Path.Combine(Namespace(fixture), Guid.NewGuid().ToString("D"));
        WithWritableNamespace(fixture, () =>
        {
            Directory.CreateDirectory(sibling);
            File.SetUnixFileMode(sibling, OwnerDirectory);
            File.WriteAllText(Path.Combine(sibling, "sentinel"), "preserved");
        });
        var original = NativeLinux.GetNodeIdentity(fixture.Paths.InstanceRoot);
        var commands = new List<PrivilegedPurgeNamespace.Command>();
        fixture.Runner.Sudo = SimulatedHelper(fixture, commands);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
        Assert.IsEmpty(Tombstones(fixture));
        Assert.AreEqual("preserved", await File.ReadAllTextAsync(Path.Combine(sibling, "sentinel")));
        Assert.AreEqual(ReadOnlyNamespace, File.GetUnixFileMode(Namespace(fixture)));
        Assert.HasCount(2, commands);
        Assert.AreEqual(PrivilegedPurgeNamespace.Action.Quarantine, commands[0].Action);
        Assert.AreEqual(PrivilegedPurgeNamespace.Action.Remove, commands[1].Action);
        foreach (var command in commands)
        {
            Assert.AreEqual(fixture.InstanceId, command.InstanceId);
            Assert.AreEqual(result.OperationId, command.OperationId);
            Assert.AreEqual(original.Inode, command.Inode);
            Assert.AreEqual(original.DeviceMajor, command.DeviceMajor);
            Assert.AreEqual(original.DeviceMinor, command.DeviceMinor);
            Assert.AreEqual(fixture.Uid, command.Uid);
            Assert.AreEqual(fixture.Gid, command.Gid);
        }
        var evidence = await ReadEvidenceAsync(fixture, result.OperationId!.Value);
        Assert.IsNull(evidence.Settlement);
        Assert.IsNull(evidence.SettledUtc);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_DeniedPrivilegeLeavesTreeIntactAndResumeCompletes()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        var inventory = Inventory(fixture);
        fixture.Runner.Sudo = static (_, _) =>
            Task.FromResult(new ProcessResult(1, string.Empty, "sudo: helper denied by policy"));

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(failure.Message, "rerun the same command with --resume", StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, "Privileged purge quarantine failed: sudo: helper denied by policy", StringComparison.Ordinal);
        CollectionAssert.AreEqual(inventory, Inventory(fixture));
        Assert.IsEmpty(Tombstones(fixture));
        var operation = await RetainedOperationAsync(fixture);
        Assert.AreEqual(LifecycleOperationPhase.Mutating, operation.Phase);
        Assert.IsTrue(operation.MutationStarted);
        var evidenceBytes = await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId));
        Assert.AreEqual(
            NativeLinux.GetNodeIdentity(Namespace(fixture)).Inode,
            (await ReadEvidenceAsync(fixture, operation.OperationId)).Namespace?.Inode);

        fixture.Runner.Sudo = SimulatedHelper(fixture);
        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(operation.OperationId, result.OperationId);
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
        Assert.IsEmpty(Tombstones(fixture));
        // The retry continued under the evidence its first attempt recorded rather than replacing it.
        CollectionAssert.AreEqual(evidenceBytes, await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId)));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_HelperReportingSuccessWithoutQuarantineIsRefused()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        var inventory = Inventory(fixture);
        fixture.Runner.Sudo = static (_, _) => Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(failure.Message, "did not produce the authenticated tombstone", StringComparison.Ordinal);
        CollectionAssert.AreEqual(inventory, Inventory(fixture));
        Assert.IsEmpty(Tombstones(fixture));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_InstanceRootSubstitutedBeforeHelperIsNeverQuarantined()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        var displaced = Path.Combine(Namespace(fixture), "displaced");
        var helper = SimulatedHelper(fixture);
        fixture.Runner.Sudo = (arguments, cancellationToken) =>
        {
            // The authenticated root is swapped for a same-owner directory between authentication and escalation.
            WithWritableNamespace(fixture, () =>
            {
                Directory.Move(fixture.Paths.InstanceRoot, displaced);
                Directory.CreateDirectory(fixture.Paths.InstanceRoot);
                File.SetUnixFileMode(fixture.Paths.InstanceRoot, OwnerDirectory);
                File.WriteAllText(Path.Combine(fixture.Paths.InstanceRoot, "substitute"), "foreign");
            });
            return helper(arguments, cancellationToken);
        };

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(failure.Message, "does not match the authenticated purge identity", StringComparison.Ordinal);
        Assert.AreEqual("foreign", await File.ReadAllTextAsync(Path.Combine(fixture.Paths.InstanceRoot, "substitute")));
        Assert.IsTrue(File.Exists(Path.Combine(displaced, "instance-manifest.json")));
        Assert.IsEmpty(Tombstones(fixture));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_ResumeRefusesRootThatIsNotTheEvidencedTree()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        fixture.Runner.Sudo = static (_, _) => Task.FromResult(new ProcessResult(1, string.Empty, "denied"));
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        // An identical copy published at the instance name is a different tree than the one the purge evidenced.
        await ReplaceWithCopyAsync(fixture);
        var inventory = Inventory(fixture);
        fixture.Runner.Sudo = SimulatedHelper(fixture);

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(failure.Message, "not the tree the retained purge evidenced", StringComparison.Ordinal);
        Assert.AreEqual(1, fixture.Runner.SudoCount);
        CollectionAssert.AreEqual(inventory, Inventory(fixture));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_CancellationBeforeHelperCompletesLeavesResumableTree()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        var inventory = Inventory(fixture);
        using var cancellation = new CancellationTokenSource();
        fixture.Runner.Sudo = async (_, cancellationToken) =>
        {
            await cancellation.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(0, string.Empty, string.Empty);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, cancellation.Token));

        CollectionAssert.AreEqual(inventory, Inventory(fixture));
        Assert.AreEqual(LifecycleOperationPhase.Mutating, (await RetainedOperationAsync(fixture)).Phase);
        fixture.Runner.Sudo = SimulatedHelper(fixture);
        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", result.Outcome);
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_InterruptionAfterPrivilegedRenameResumesFromTombstone()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        using var cancellation = new CancellationTokenSource();
        var helper = SimulatedHelper(fixture);
        fixture.Runner.Sudo = async (arguments, cancellationToken) =>
        {
            // The helper commits the rename, then the CLI is interrupted before it observes the result.
            var result = await helper(arguments, cancellationToken);
            Assert.AreEqual(0, result.ExitCode);
            await cancellation.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, cancellation.Token));

        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
        Assert.HasCount(1, Tombstones(fixture));
        Assert.AreEqual(LifecycleOperationPhase.Mutating, (await RetainedOperationAsync(fixture)).Phase);
        fixture.Runner.Sudo = SimulatedHelper(fixture);
        var resumed = await CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", resumed.Outcome);
        Assert.IsEmpty(Tombstones(fixture));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_DeniedTombstoneRemovalResumesEmptiedCommittedTombstone()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        var helper = SimulatedHelper(fixture);
        fixture.Runner.Sudo = (arguments, cancellationToken) => arguments[4] == "remove"
            ? Task.FromResult(new ProcessResult(1, string.Empty, "sudo: removal denied"))
            : helper(arguments, cancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(failure.Message, "Purge stopped at its quarantined tombstone", StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, "Privileged purge removal failed: sudo: removal denied", StringComparison.Ordinal);
        var tombstone = Assert.ContainsSingle(Tombstones(fixture));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(tombstone));
        Assert.AreEqual(LifecycleOperationPhase.Committed, (await RetainedOperationAsync(fixture)).Phase);

        fixture.Runner.Sudo = SimulatedHelper(fixture);
        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.IsEmpty(Tombstones(fixture));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_CommittedPurgeNeverAdoptsReappearedRoot()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        var helper = SimulatedHelper(fixture);
        fixture.Runner.Sudo = (arguments, cancellationToken) => arguments[4] == "remove"
            ? Task.FromResult(new ProcessResult(1, string.Empty, "denied"))
            : helper(arguments, cancellationToken);
        var copy = await CopyInstanceRootAsync(fixture);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        WithWritableNamespace(fixture, () => Directory.Move(copy, fixture.Paths.InstanceRoot));
        var inventory = Inventory(fixture);

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(failure.Message, "cannot adopt an instance root that reappeared", StringComparison.Ordinal);
        CollectionAssert.AreEqual(inventory, Inventory(fixture));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_EntryPlantedInTombstoneStopsBoundedCleanup()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        var outside = Path.Combine(fixture.Root, "outside-sentinel");
        await File.WriteAllTextAsync(outside, "outside");
        var helper = SimulatedHelper(fixture);
        fixture.Runner.Sudo = async (arguments, cancellationToken) =>
        {
            var result = await helper(arguments, cancellationToken);
            if (arguments[4] == "quarantine")
                File.CreateSymbolicLink(Path.Combine(Assert.ContainsSingle(Tombstones(fixture)), "zz-escape"), outside);
            return result;
        };

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(failure.Message, "'zz-escape' could not be opened without following links", StringComparison.Ordinal);
        var resumeFailure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(resumeFailure.Message, "'zz-escape' could not be opened without following links", StringComparison.Ordinal);
        Assert.AreEqual("outside", await File.ReadAllTextAsync(outside));
        var tombstone = Assert.ContainsSingle(Tombstones(fixture));
        Assert.IsNotNull(new FileInfo(Path.Combine(tombstone, "zz-escape")).LinkTarget);
        Assert.AreEqual(1, fixture.Runner.SudoCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_AlreadyAbsentTreeSettlesTruthfullyOnce()
    {
        using var fixture = await StrandedPurgeFixtureAsync();
        var sudoCount = fixture.Runner.SudoCount;
        var operation = await RetainedOperationAsync(fixture);
        var namespaceEntries = Directory.EnumerateFileSystemEntries(Namespace(fixture)).Order().ToArray();
        var journal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);
        var evidenceBytes = await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId));

        // A missing name alone never proves removal: without the operator's disposition nothing is recorded.
        var unconfirmed = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(unconfirmed.Message, "its absence does not prove removal", StringComparison.Ordinal);
        StringAssert.Contains(
            unconfirmed.Message, $"--confirm-removed-operation-id {operation.OperationId:D}", StringComparison.Ordinal);
        Assert.AreEqual(journal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
        CollectionAssert.AreEqual(evidenceBytes, await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId)));

        var flushed = new List<UnixNodeIdentity>();
        CameraAgentLifecycleManager.NamespaceFlushOverride.Value = handle =>
        {
            flushed.Add(NativeLinux.GetOpenNodeIdentity(handle, Namespace(fixture)));
            NativeLinux.FlushOpenDirectory(handle);
        };
        LifecycleResult result;
        try
        {
            result = await CameraAgentLifecycleManager.ExecuteAsync(
                ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);
        }
        finally
        {
            CameraAgentLifecycleManager.NamespaceFlushOverride.Value = null;
        }

        Assert.AreEqual(CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement, result.Outcome);
        Assert.AreEqual(operation.OperationId, result.OperationId);
        Assert.AreEqual(sudoCount, fixture.Runner.SudoCount);
        Assert.AreEqual(NativeLinux.GetNodeIdentity(Namespace(fixture)).Inode, Assert.ContainsSingle(flushed).Inode);
        CollectionAssert.AreEqual(namespaceEntries, Directory.EnumerateFileSystemEntries(Namespace(fixture)).Order().ToArray());
        var evidence = await ReadEvidenceAsync(fixture, operation.OperationId);
        Assert.AreEqual(CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement, evidence.Settlement);
        Assert.IsNotNull(evidence.SettledUtc);
        Assert.AreEqual(InstallationStatus.Completed, (await RetainedOperationAsync(fixture)).Status);

        var second = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        Assert.AreEqual("No matching interrupted purge operation exists.", second.Message);
        Assert.AreEqual(evidence.SettledUtc, (await ReadEvidenceAsync(fixture, operation.OperationId)).SettledUtc);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_RelocatedTreeIsNeverSettledAsRemoved()
    {
        using var fixture = await DeniedPurgeFixtureAsync();
        var operation = await RetainedOperationAsync(fixture);
        var relocated = Path.Combine(Namespace(fixture), "relocated");
        WithWritableNamespace(fixture, () => Directory.Move(fixture.Paths.InstanceRoot, relocated));
        fixture.Runner.Sudo = static (_, _) => throw new AssertFailedException("A relocated purge must not escalate.");
        var journal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);
        var evidenceBytes = await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId));

        // The disposition cannot override what the CLI can see: the evidenced tree under another name is not removed.
        var inNamespace = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(inNamespace.Message, "still exists in its namespace under another name", StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(relocated, "instance-manifest.json")));

        // Moved out of the namespace, the tree is indistinguishable from a removed one, so only the disposition naming
        // this operation settles it, and settlement never touches the relocated tree.
        var outside = Path.Combine(fixture.Root, "relocated-outside");
        WithWritableNamespace(fixture, () => Directory.Move(relocated, outside));
        var outsideInventory = SafeTreeDeletion.CaptureChildInventory(fixture.Root, "relocated-outside", fixture.Uid, fixture.Gid);
        var unconfirmed = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        var otherOperation = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true, ConfirmRemovedOperationId = Guid.NewGuid() },
            fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(unconfirmed.Message, "its absence does not prove removal", StringComparison.Ordinal);
        StringAssert.Contains(otherOperation.Message, "does not name the interrupted purge", StringComparison.Ordinal);
        Assert.AreEqual(journal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
        CollectionAssert.AreEqual(evidenceBytes, await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId)));

        var settled = await CameraAgentLifecycleManager.ExecuteAsync(
            ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual(CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement, settled.Outcome);
        CollectionAssert.AreEqual(
            outsideInventory.ToArray(),
            SafeTreeDeletion.CaptureChildInventory(fixture.Root, "relocated-outside", fixture.Uid, fixture.Gid).ToArray());
        Assert.IsFalse(Path.Exists(fixture.Paths.InstanceRoot));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_EvidenceWithoutNamespaceIdentitySettlesOnlyByDisposition()
    {
        using var fixture = await DeniedPurgeFixtureAsync();
        var operation = await RetainedOperationAsync(fixture);
        // Evidence recorded before the namespace identity existed carries none.
        await WriteEvidenceAsync(fixture, await ReadEvidenceAsync(fixture, operation.OperationId) with { Namespace = null });
        var relocated = Path.Combine(Namespace(fixture), "relocated");
        WithWritableNamespace(fixture, () => Directory.Move(fixture.Paths.InstanceRoot, relocated));
        fixture.Runner.Sudo = static (_, _) => throw new AssertFailedException("A legacy purge settlement must not escalate.");

        var inNamespace = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(inNamespace.Message, "still exists in its namespace under another name", StringComparison.Ordinal);

        WithWritableNamespace(fixture, () =>
        {
            SafeFileSystem.MakeTreeOwnerWritable(relocated);
            Directory.Delete(relocated, recursive: true);
        });
        var unconfirmed = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(unconfirmed.Message, "its absence does not prove removal", StringComparison.Ordinal);
        Assert.IsNull((await ReadEvidenceAsync(fixture, operation.OperationId)).Settlement);

        var settled = await CameraAgentLifecycleManager.ExecuteAsync(
            ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual(CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement, settled.Outcome);
        var evidence = await ReadEvidenceAsync(fixture, operation.OperationId);
        Assert.IsNull(evidence.Namespace);
        Assert.AreEqual(CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement, evidence.Settlement);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_FailedNamespaceFlushLeavesSettlementResumable()
    {
        using var fixture = await StrandedPurgeFixtureAsync();
        var operation = await RetainedOperationAsync(fixture);
        var journal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);
        var evidenceBytes = await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId));
        CameraAgentLifecycleManager.NamespaceFlushOverride.Value = static _ =>
            throw new InstallerException("An authenticated directory could not be flushed to disk.");
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
            StringAssert.Contains(failure.Message, "could not be flushed to disk", StringComparison.Ordinal);
        }
        finally
        {
            CameraAgentLifecycleManager.NamespaceFlushOverride.Value = null;
        }

        Assert.AreEqual(journal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
        CollectionAssert.AreEqual(evidenceBytes, await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId)));
        var retained = await RetainedOperationAsync(fixture);
        Assert.AreEqual(LifecycleOperationPhase.Mutating, retained.Phase);
        Assert.AreEqual(InstallationStatus.Running, retained.Status);

        var settled = await CameraAgentLifecycleManager.ExecuteAsync(
            ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual(CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement, settled.Outcome);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_CommittedTombstoneRemovedOutOfBandCompletesOnlyAfterNamespaceFlush()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        var helper = SimulatedHelper(fixture);
        fixture.Runner.Sudo = (arguments, cancellationToken) => arguments[4] == "remove"
            ? Task.FromResult(new ProcessResult(1, string.Empty, "denied"))
            : helper(arguments, cancellationToken);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        var operation = await RetainedOperationAsync(fixture);
        Assert.AreEqual(LifecycleOperationPhase.Committed, operation.Phase);
        var tombstone = Assert.ContainsSingle(Tombstones(fixture));
        WithWritableNamespace(fixture, () => Directory.Delete(tombstone));
        fixture.Runner.Sudo = static (_, _) => throw new AssertFailedException("A committed purge without a tombstone must not escalate.");
        var journal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);

        // A committed deletion is the CLI's own, so it needs no disposition and refuses one.
        var disposition = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(disposition.Message, "refuses --confirm-removed-operation-id", StringComparison.Ordinal);
        CameraAgentLifecycleManager.NamespaceFlushOverride.Value = static _ =>
            throw new InstallerException("An authenticated directory could not be flushed to disk.");
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
            StringAssert.Contains(failure.Message, "could not be flushed to disk", StringComparison.Ordinal);
        }
        finally
        {
            CameraAgentLifecycleManager.NamespaceFlushOverride.Value = null;
        }
        Assert.AreEqual(journal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(InstallationStatus.Completed, (await RetainedOperationAsync(fixture)).Status);
        Assert.IsNull((await ReadEvidenceAsync(fixture, operation.OperationId)).Settlement);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_DispositionIsRefusedWhileTheTreeOrItsTombstoneExists()
    {
        using var fixture = await DeniedPurgeFixtureAsync();
        var operation = await RetainedOperationAsync(fixture);
        var inventory = Inventory(fixture);

        var rootPresent = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(rootPresent.Message, "The instance root exists, so it was not removed", StringComparison.Ordinal);
        CollectionAssert.AreEqual(inventory, Inventory(fixture));
        var tombstone = Path.Combine(Namespace(fixture), PrivilegedPurgeNamespace.TombstoneName(fixture.InstanceId, operation.OperationId));
        WithWritableNamespace(fixture, () => Directory.Move(fixture.Paths.InstanceRoot, tombstone));

        var tombstonePresent = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(tombstonePresent.Message, "refuses --confirm-removed-operation-id", StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(tombstone, "instance-manifest.json")));
        Assert.AreEqual(1, fixture.Runner.SudoCount);
        Assert.IsNull((await ReadEvidenceAsync(fixture, operation.OperationId)).Settlement);
        Assert.AreEqual(LifecycleOperationPhase.Mutating, (await RetainedOperationAsync(fixture)).Phase);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_RetryBeforeQuarantineAdoptsOnlyItsUnchangedEvidence()
    {
        var cases = new (string Name, Func<LifecycleFixture, Guid, Task> Arrange, string Message)[]
        {
            ("planted file", static (fixture, _) =>
                File.WriteAllTextAsync(Path.Combine(fixture.Paths.InstanceRoot, "planted"), "foreign"),
                "not the tree the retained purge evidenced"),
            ("replaced descendant", static (fixture, _) =>
            {
                var descendant = EvidencedDescendant(fixture);
                var replacement = descendant + ".replacement";
                File.Copy(descendant, replacement);
                File.Move(replacement, descendant, overwrite: true);
                return Task.CompletedTask;
            }, "not the tree the retained purge evidenced"),
            ("missing descendant", static (fixture, _) =>
            {
                File.Delete(EvidencedDescendant(fixture));
                return Task.CompletedTask;
            }, "not the tree the retained purge evidenced"),
            ("foreign host", static async (fixture, operationId) =>
                await WriteEvidenceAsync(fixture, await ReadEvidenceAsync(fixture, operationId) with
                {
                    HostIdentitySha256 = new string('0', 64)
                }), "belongs to a different host"),
            ("other schema", static async (fixture, operationId) =>
            {
                var evidence = await ReadEvidenceAsync(fixture, operationId);
                await WriteEvidenceAsync(fixture, evidence with { SchemaVersion = evidence.SchemaVersion + 1 });
            }, "does not match its retained operation"),
            ("recorded settlement", static async (fixture, operationId) =>
                await WriteEvidenceAsync(fixture, await ReadEvidenceAsync(fixture, operationId) with
                {
                    Settlement = CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement,
                    SettledUtc = DateTimeOffset.UtcNow
                }), "not the tree the retained purge evidenced"),
            ("other namespace", static async (fixture, operationId) =>
            {
                var evidence = await ReadEvidenceAsync(fixture, operationId);
                await WriteEvidenceAsync(fixture, evidence with
                {
                    Namespace = evidence.Namespace! with { Inode = evidence.Namespace.Inode + 1 }
                });
            }, "not the tree the retained purge evidenced")
        };
        foreach (var (name, arrange, message) in cases)
        {
            using var fixture = await DeniedPurgeFixtureAsync();
            var operation = await RetainedOperationAsync(fixture);
            await arrange(fixture, operation.OperationId);
            var inventory = Inventory(fixture);
            var journal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);
            var evidenceBytes = await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId));
            fixture.Runner.Sudo = SimulatedHelper(fixture);

            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None),
                name);

            StringAssert.Contains(failure.Message, message, StringComparison.Ordinal, name);
            CollectionAssert.AreEqual(inventory, Inventory(fixture), name);
            Assert.AreEqual(journal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath), name);
            CollectionAssert.AreEqual(
                evidenceBytes, await File.ReadAllBytesAsync(EvidencePath(fixture, operation.OperationId)), name);
            Assert.AreEqual(1, fixture.Runner.SudoCount, name);
            Assert.IsEmpty(Tombstones(fixture), name);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_SettlementRecordedBeforeCompletionIsReusedUnchanged()
    {
        using var fixture = await StrandedPurgeFixtureAsync();
        var operation = await RetainedOperationAsync(fixture);
        var settledUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var evidence = await ReadEvidenceAsync(fixture, operation.OperationId);
        await WriteEvidenceAsync(fixture, evidence with
        {
            Settlement = CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement,
            SettledUtc = settledUtc
        });

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual(CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement, result.Outcome);
        Assert.AreEqual(settledUtc, (await ReadEvidenceAsync(fixture, operation.OperationId)).SettledUtc);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_AlreadyAbsentSettlementRefusesOccupiedOrAmbiguousNamespace()
    {
        var cases = new (string Name, Func<LifecycleFixture, Task> Arrange, string Message)[]
        {
            ("dangling symlink", static fixture =>
            {
                WithWritableNamespace(fixture, () => File.CreateSymbolicLink(fixture.Paths.InstanceRoot, "/nonexistent-hvo-target"));
                return Task.CompletedTask;
            }, "An entry occupies the purged instance name"),
            ("regular file", static fixture =>
            {
                WithWritableNamespace(fixture, () => File.WriteAllText(fixture.Paths.InstanceRoot, "foreign"));
                return Task.CompletedTask;
            }, "An entry occupies the purged instance name"),
            ("other tombstone", static fixture =>
            {
                WithWritableNamespace(fixture, () => Directory.CreateDirectory(Path.Combine(
                    Namespace(fixture), PrivilegedPurgeNamespace.TombstoneName(fixture.InstanceId, Guid.NewGuid()))));
                return Task.CompletedTask;
            }, "Another purge tombstone of this instance exists"),
            ("substituted namespace", static fixture =>
            {
                // A same-owner, same-mode replacement on the same filesystem is still not the evidenced directory.
                Directory.Move(Namespace(fixture), Namespace(fixture) + "-displaced");
                Directory.CreateDirectory(Namespace(fixture));
                File.SetUnixFileMode(Namespace(fixture), ReadOnlyNamespace);
                return Task.CompletedTask;
            }, "not the directory that held the evidenced tree"),
            ("missing evidence", static async fixture =>
            {
                var operation = await RetainedOperationAsync(fixture);
                File.Delete(EvidencePath(fixture, operation.OperationId));
            }, "could not be opened without following links"),
            ("foreign host", static async fixture =>
            {
                var operation = await RetainedOperationAsync(fixture);
                await WriteEvidenceAsync(fixture, await ReadEvidenceAsync(fixture, operation.OperationId) with
                {
                    HostIdentitySha256 = new string('0', 64)
                });
            }, "belongs to a different host"),
            ("other device", static async fixture =>
            {
                var operation = await RetainedOperationAsync(fixture);
                var evidence = await ReadEvidenceAsync(fixture, operation.OperationId);
                await WriteEvidenceAsync(fixture, evidence with
                {
                    Tree = evidence.Tree.Select(static node => node.RelativePath == "."
                        ? node with { DeviceMajor = node.DeviceMajor + 1 }
                        : node).ToArray()
                });
            }, "not on the filesystem that held the evidenced tree"),
            ("unknown settlement", static async fixture =>
            {
                var operation = await RetainedOperationAsync(fixture);
                await WriteEvidenceAsync(fixture, await ReadEvidenceAsync(fixture, operation.OperationId) with
                {
                    Settlement = "deleted",
                    SettledUtc = DateTimeOffset.UtcNow
                });
            }, "does not match its retained operation"),
            ("settlement without time", static async fixture =>
            {
                var operation = await RetainedOperationAsync(fixture);
                await WriteEvidenceAsync(fixture, await ReadEvidenceAsync(fixture, operation.OperationId) with
                {
                    Settlement = CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement
                });
            }, "does not match its retained operation"),
            ("docker reference", static fixture =>
            {
                fixture.Runner.ConfigureRuntime(
                    fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
                    fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid, exists: false);
                fixture.Runner.ListOwnedOrphan = true;
                return Task.CompletedTask;
            }, "a local container still references the instance"),
            ("other request", static async fixture =>
            {
                var operation = await RetainedOperationAsync(fixture);
                await CameraAgentLifecycleManager.RecordAsync(
                    fixture.Paths, operation with { RequestSha256 = new string('0', 64) }, CancellationToken.None);
            }, "No matching interrupted purge operation exists."),
            ("no journal", static fixture =>
            {
                File.Delete(fixture.Paths.LifecycleStatePath);
                return Task.CompletedTask;
            }, "No matching interrupted purge operation exists.")
        };
        foreach (var (name, arrange, message) in cases)
        {
            using var fixture = await StrandedPurgeFixtureAsync();
            // Every refusal holds even with the operator's disposition, which never overrides a provable contradiction.
            var operation = await RetainedOperationAsync(fixture);
            await arrange(fixture);
            var journal = File.Exists(fixture.Paths.LifecycleStatePath)
                ? await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath)
                : null;

            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                ConfirmedResume(fixture, operation), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None),
                name);

            StringAssert.Contains(failure.Message, message, StringComparison.Ordinal, name);
            Assert.AreEqual(journal, File.Exists(fixture.Paths.LifecycleStatePath)
                ? await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath)
                : null, name);
            foreach (var evidencePath in Directory.EnumerateFiles(fixture.Paths.OperationsRoot, "purge-*.evidence.json"))
            {
                var evidence = await ReadEvidenceAsync(evidencePath);
                Assert.IsTrue(evidence.Settlement is null || name.Contains("settlement", StringComparison.Ordinal), name);
            }
            // The fixture's cleanup follows links, so a planted link is removed before it runs.
            if (new FileInfo(fixture.Paths.InstanceRoot).LinkTarget is not null)
                WithWritableNamespace(fixture, () => File.Delete(fixture.Paths.InstanceRoot));
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_SettlementContradictedByRetainedTombstoneIsRefused()
    {
        using var fixture = await ReadOnlyNamespaceFixtureAsync();
        fixture.Runner.Sudo = static (_, _) => Task.FromResult(new ProcessResult(1, string.Empty, "denied"));
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        var operation = await RetainedOperationAsync(fixture);
        var tombstone = Path.Combine(Namespace(fixture), PrivilegedPurgeNamespace.TombstoneName(fixture.InstanceId, operation.OperationId));
        WithWritableNamespace(fixture, () => Directory.Move(fixture.Paths.InstanceRoot, tombstone));
        await WriteEvidenceAsync(fixture, await ReadEvidenceAsync(fixture, operation.OperationId) with
        {
            Settlement = CameraAgentLifecycleManager.PurgeAlreadyAbsentSettlement,
            SettledUtc = DateTimeOffset.UtcNow
        });

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(failure.Message, "settlement that contradicts the retained tree", StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(tombstone, "instance-manifest.json")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResumePurge_MissingOperationsRootIsRefusedWithoutCreatingIt()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        SafeFileSystem.MakeTreeOwnerWritable(fixture.Paths.InstanceRoot);
        Directory.Delete(fixture.Paths.InstanceRoot, recursive: true);
        Directory.Delete(fixture.Paths.OperationsRoot, recursive: true);

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture) with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual("No matching interrupted purge operation exists.", failure.Message);
        Assert.IsFalse(Directory.Exists(fixture.Paths.OperationsRoot));
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void PrivilegedPurgeHelper_AppliesOnlyIdentityBoundNamespaceMutations()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-purge-helper-{Guid.NewGuid():N}");
        var instanceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var instance = Path.Combine(parent, instanceId.ToString("D"));
        var tombstone = Path.Combine(parent, PrivilegedPurgeNamespace.TombstoneName(instanceId, operationId));
        Directory.CreateDirectory(Path.Combine(instance, "nested"));
        File.SetUnixFileMode(instance, OwnerDirectory);
        File.SetUnixFileMode(Path.Combine(instance, "nested"), OwnerDirectory);
        File.WriteAllText(Path.Combine(instance, "file"), "value");
        try
        {
            using var handle = NativeLinux.OpenDirectoryNoFollow(parent);
            var identity = NativeLinux.GetNodeIdentity(instance);
            var quarantine = HelperCommand(PrivilegedPurgeNamespace.Action.Quarantine, instanceId, operationId, identity);

            AssertRefused(quarantine with { Inode = identity.Inode + 1 }, "does not match the authenticated purge identity");
            AssertRefused(quarantine with { Uid = identity.Uid + 1 }, "does not match the authenticated purge identity");
            AssertRefused(quarantine with { Gid = identity.Gid + 1 }, "does not match the authenticated purge identity");
            AssertRefused(quarantine with { DeviceMinor = identity.DeviceMinor + 1 }, "does not match the authenticated purge identity");
            AssertRefused(quarantine with { InstanceId = Guid.NewGuid() }, "instance root to quarantine is absent");
            File.SetUnixFileMode(instance, OwnerDirectory | UnixFileMode.GroupWrite);
            AssertRefused(quarantine, "does not match the authenticated purge identity");
            File.SetUnixFileMode(instance, OwnerDirectory);
            Directory.CreateDirectory(tombstone);
            AssertRefused(quarantine, "purge tombstone already exists");
            Directory.Delete(tombstone);
            Assert.IsTrue(Directory.Exists(instance));

            PrivilegedPurgeNamespace.Apply(quarantine, handle);
            Assert.IsFalse(Directory.Exists(instance));
            Assert.AreEqual(identity.Inode, NativeLinux.GetNodeIdentity(tombstone).Inode);

            var remove = quarantine with { Action = PrivilegedPurgeNamespace.Action.Remove };
            AssertRefused(remove, "still contains directories");
            Directory.Delete(Path.Combine(tombstone, "nested"));
            AssertRefused(remove, "could not be removed through its authenticated parent");
            File.Delete(Path.Combine(tombstone, "file"));
            AssertRefused(remove with { Inode = identity.Inode + 1 }, "does not match the authenticated purge identity");
            File.SetUnixFileMode(parent, ReadOnlyNamespace);
            try
            {
                AssertRefused(remove, "could not be removed through its authenticated parent");
            }
            finally
            {
                File.SetUnixFileMode(parent, OwnerDirectory);
            }

            PrivilegedPurgeNamespace.Apply(remove, handle);
            Assert.IsFalse(Directory.Exists(tombstone));
            AssertRefused(remove, "purge tombstone to remove is absent");

            void AssertRefused(PrivilegedPurgeNamespace.Command command, string message)
            {
                var failure = Assert.ThrowsExactly<InstallerException>(() => PrivilegedPurgeNamespace.Apply(command, handle));
                StringAssert.Contains(failure.Message, message, StringComparison.Ordinal);
            }
        }
        finally
        {
            File.SetUnixFileMode(parent, OwnerDirectory);
            Directory.Delete(parent, recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void PrivilegedPurgeHelper_RefusesSymbolicLinkAtInstanceName()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-purge-helper-{Guid.NewGuid():N}");
        var target = Path.Combine(parent, "target");
        var instanceId = Guid.NewGuid();
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, OwnerDirectory);
        File.CreateSymbolicLink(Path.Combine(parent, instanceId.ToString("D")), target);
        try
        {
            using var handle = NativeLinux.OpenDirectoryNoFollow(parent);
            var identity = NativeLinux.GetNodeIdentity(target);
            var command = HelperCommand(PrivilegedPurgeNamespace.Action.Quarantine, instanceId, Guid.NewGuid(), identity);

            var failure = Assert.ThrowsExactly<InstallerException>(() => PrivilegedPurgeNamespace.Apply(command, handle));

            StringAssert.Contains(failure.Message, "does not match the authenticated purge identity", StringComparison.Ordinal);
            Assert.IsNotNull(new FileInfo(Path.Combine(parent, instanceId.ToString("D"))).LinkTarget);
            Assert.IsTrue(Directory.Exists(target));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [TestMethod]
    public void PrivilegedPurgeHelper_ParsesOnlyTheExactBoundCommand()
    {
        var instanceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var identity = new UnixNodeIdentity(1000, 1001, OwnerDirectory, 0x4000, 2, 4242, 8, 1, 77);
        var arguments = PrivilegedPurgeNamespace.HelperArguments(
            PrivilegedPurgeNamespace.Action.Remove, InstallRequest.DefaultProductRoot, instanceId, operationId, identity);
        Assert.AreEqual("--", arguments[0]);
        Assert.AreEqual($"/proc/{Environment.ProcessId}/exe", arguments[1]);
        var helper = arguments.Skip(2).ToArray();

        Assert.IsTrue(PrivilegedPurgeNamespace.TryParse(helper, InstallRequest.DefaultProductRoot, out var command));
        Assert.AreEqual(
            new PrivilegedPurgeNamespace.Command(PrivilegedPurgeNamespace.Action.Remove, instanceId, operationId, 1000, 1001, 4242, 8, 1),
            command);
        Assert.AreEqual(PrivilegedPurgeNamespace.TombstoneName(instanceId, operationId), command.TombstoneName);

        foreach (var (index, value) in new (int, string)[]
                 {
                     (0, "internal-prepare"), (2, "delete"), (4, "/tmp/hvo-sky"), (4, InstallRequest.DefaultProductRoot + "/"),
                     (6, instanceId.ToString("N")), (6, Guid.Empty.ToString("D")), (8, "../../etc"), (10, "0"), (10, "+1000"),
                     (10, " 1000"), (10, "-1"), (12, "1001 "), (14, "0x1092"), (16, "8.0"), (18, "4294967296"), (1, "--action=remove"),
                     (5, "--operation-id")
                 })
        {
            var changed = (string[])helper.Clone();
            changed[index] = value;
            Assert.IsFalse(PrivilegedPurgeNamespace.TryParse(changed, InstallRequest.DefaultProductRoot, out _), $"{index}={value}");
        }
        Assert.IsFalse(PrivilegedPurgeNamespace.TryParse([.. helper, "--force"], InstallRequest.DefaultProductRoot, out _));
        Assert.IsFalse(PrivilegedPurgeNamespace.TryParse(helper[..^1], InstallRequest.DefaultProductRoot, out _));
        Assert.AreEqual(
            PrivilegedPurgeNamespace.NamespaceRoot(InstallRequest.DefaultProductRoot),
            "/" + string.Join('/', PrivilegedPurgeNamespace.NamespaceComponents(ProductionProductRoot.Default)
                .Select(static component => component.Name)));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void PrivilegedPurgeHelper_RefusesInvocationWithoutRoot()
    {
        if (NativeLinux.getuid() == 0) Assert.Inconclusive("The non-root refusal is observable only from a non-root test user.");
        var identity = new UnixNodeIdentity(NativeLinux.getuid(), NativeLinux.getgid(), OwnerDirectory, 0x4000, 2, 4242, 8, 1, 77);
        var arguments = PrivilegedPurgeNamespace.HelperArguments(
            PrivilegedPurgeNamespace.Action.Quarantine, InstallRequest.DefaultProductRoot, Guid.NewGuid(), Guid.NewGuid(), identity);

        Assert.AreEqual(2, PrivilegedPurgeNamespace.RunInternal(arguments.Skip(2).ToArray()));
    }

    private static PrivilegedPurgeNamespace.Command HelperCommand(
        PrivilegedPurgeNamespace.Action action,
        Guid instanceId,
        Guid operationId,
        UnixNodeIdentity identity)
        => new(action, instanceId, operationId, identity.Uid, identity.Gid, identity.Inode, identity.DeviceMajor, identity.DeviceMinor);

    private static async Task<LifecycleFixture> ReadOnlyNamespaceFixtureAsync()
    {
        if (NativeLinux.getuid() == 0)
            Assert.Inconclusive("A read-only namespace denies writes only to a non-root test user; run the Unit selection as a non-root user.");
        var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        File.SetUnixFileMode(Namespace(fixture), ReadOnlyNamespace);
        return fixture;
    }

    /// <summary>
    /// A purge whose quarantine was denied: journalled as <c>Purge / Mutating / Running</c> with its deletion evidence
    /// and its instance root intact.
    /// </summary>
    private static async Task<LifecycleFixture> DeniedPurgeFixtureAsync()
    {
        var fixture = await ReadOnlyNamespaceFixtureAsync();
        fixture.Runner.Sudo = static (_, _) => Task.FromResult(new ProcessResult(1, string.Empty, "denied"));
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        var operation = await RetainedOperationAsync(fixture);
        Assert.AreEqual(LifecycleOperationPhase.Mutating, operation.Phase);
        Assert.AreEqual(InstallationStatus.Running, operation.Status);
        return fixture;
    }

    /// <summary>
    /// Produces the field incident's stranded state: a denied purge whose instance root was then removed outside the
    /// CLI.
    /// </summary>
    private static async Task<LifecycleFixture> StrandedPurgeFixtureAsync()
    {
        var fixture = await DeniedPurgeFixtureAsync();
        WithWritableNamespace(fixture, () =>
        {
            SafeFileSystem.MakeTreeOwnerWritable(fixture.Paths.InstanceRoot);
            Directory.Delete(fixture.Paths.InstanceRoot, recursive: true);
        });
        fixture.Runner.Sudo = static (_, _) => throw new AssertFailedException("A stranded purge must not escalate.");
        return fixture;
    }

    private static LifecycleRequest PurgeRequest(LifecycleFixture fixture)
        => fixture.Request(LifecycleOperationKind.Purge) with { ConfirmationInstanceId = fixture.InstanceId };

    private static LifecycleRequest ConfirmedResume(LifecycleFixture fixture, LifecycleOperationState operation)
        => PurgeRequest(fixture) with { Resume = true, ConfirmRemovedOperationId = operation.OperationId };

    // A regular file the purge evidenced, other than the ownership marker and the lifecycle lock.
    private static string EvidencedDescendant(LifecycleFixture fixture)
        => Directory.EnumerateFiles(fixture.Paths.InstanceRoot, "*", SearchOption.AllDirectories)
            .Where(path => path != fixture.Paths.ManifestPath && Path.GetFileName(path) != ".deployment.lock")
            .Order(StringComparer.Ordinal)
            .First();

    private static string Namespace(LifecycleFixture fixture) => PrivilegedPurgeNamespace.NamespaceRoot(fixture.Paths.ProductRoot);

    private static string[] Tombstones(LifecycleFixture fixture)
        => Directory.EnumerateFileSystemEntries(Namespace(fixture), ".purge-*").Order(StringComparer.Ordinal).ToArray();

    private static string EvidencePath(LifecycleFixture fixture, Guid operationId)
        => Path.Combine(fixture.Paths.OperationsRoot, $"purge-{operationId:D}.evidence.json");

    // Every lifecycle run opens the instance's own lock file, so the comparison covers the tree except that file.
    private static string[] Inventory(LifecycleFixture fixture)
        => SafeTreeDeletion.CaptureChildInventory(
                Namespace(fixture), fixture.InstanceId.ToString("D"), fixture.Uid, fixture.Gid)
            .Where(static node => node.RelativePath != ".deployment.lock")
            .Select(static node => $"{node.RelativePath}:{node.Inode}:{node.Mode}:{node.LinkCount}")
            .ToArray();

    private static async Task<LifecycleOperationState> RetainedOperationAsync(LifecycleFixture fixture)
        => await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None)
           ?? throw new AssertFailedException("The purge journal is missing.");

    private static Task<PurgeDeletionEvidence> ReadEvidenceAsync(LifecycleFixture fixture, Guid operationId)
        => ReadEvidenceAsync(EvidencePath(fixture, operationId));

    private static async Task<PurgeDeletionEvidence> ReadEvidenceAsync(string path)
    {
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        return await CameraAgentLifecycleManager.DeserializeRetainedAsync(
            stream, DeploymentJsonContext.Default.PurgeDeletionEvidence, "purge deletion evidence", CancellationToken.None);
    }

    private static Task WriteEvidenceAsync(LifecycleFixture fixture, PurgeDeletionEvidence evidence)
        => SafeFileSystem.WriteJsonAtomicAsync(
            EvidencePath(fixture, evidence.OperationId), evidence,
            DeploymentJsonContext.Default.PurgeDeletionEvidence, CancellationToken.None);

    private static void WithWritableNamespace(LifecycleFixture fixture, Action action)
    {
        var mode = File.GetUnixFileMode(Namespace(fixture));
        File.SetUnixFileMode(Namespace(fixture), OwnerDirectory);
        try
        {
            action();
        }
        finally
        {
            File.SetUnixFileMode(Namespace(fixture), mode);
        }
    }

    private static async Task<string> CopyInstanceRootAsync(LifecycleFixture fixture)
    {
        var copy = Path.Combine(fixture.Root, $"instance-copy-{Guid.NewGuid():N}");
        var copied = await new ProcessRunner().RunAsync(
            "cp", ["-a", "--", fixture.Paths.InstanceRoot, copy], CancellationToken.None);
        Assert.AreEqual(0, copied.ExitCode, copied.StandardError);
        // The copy is staged beside the instance root so that a later rename into the instance name stays on its mount.
        var staged = Path.Combine(Namespace(fixture), $".copy-{Guid.NewGuid():N}");
        WithWritableNamespace(fixture, () => Directory.Move(copy, staged));
        return staged;
    }

    private static async Task ReplaceWithCopyAsync(LifecycleFixture fixture)
    {
        var copy = await CopyInstanceRootAsync(fixture);
        WithWritableNamespace(fixture, () =>
        {
            SafeFileSystem.MakeTreeOwnerWritable(fixture.Paths.InstanceRoot);
            Directory.Delete(fixture.Paths.InstanceRoot, recursive: true);
            Directory.Move(copy, fixture.Paths.InstanceRoot);
        });
    }

    /// <summary>
    /// The privileged helper as <c>sudo</c> would run it: the argument vector is the exact one the CLI built, parsed by
    /// the production parser with only the product root mapped from the fixture to the default root it is pinned to,
    /// and applied by the production helper core while the read-only namespace is briefly writable as it is to root.
    /// </summary>
    private static Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> SimulatedHelper(
        LifecycleFixture fixture,
        List<PrivilegedPurgeNamespace.Command>? commands = null)
        => (arguments, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("--", arguments[0]);
            Assert.AreEqual($"/proc/{Environment.ProcessId}/exe", arguments[1]);
            Assert.AreEqual(PrivilegedPurgeNamespace.CommandName, arguments[2]);
            Assert.AreEqual("--product-root", arguments[5]);
            Assert.AreEqual(fixture.Paths.ProductRoot, arguments[6]);
            var helper = arguments.Skip(2).ToArray();
            helper[4] = InstallRequest.DefaultProductRoot;
            Assert.IsTrue(PrivilegedPurgeNamespace.TryParse(helper, InstallRequest.DefaultProductRoot, out var command));
            commands?.Add(command);
            try
            {
                WithWritableNamespace(fixture, () =>
                {
                    using var parent = NativeLinux.OpenDirectoryNoFollow(Namespace(fixture));
                    PrivilegedPurgeNamespace.Apply(command, parent);
                });
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }
            catch (InstallerException exception)
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, exception.Message));
            }
        };
}
