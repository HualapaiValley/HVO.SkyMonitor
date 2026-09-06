using System.Text.Json;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class LifecycleContractTests
{
    [TestMethod]
    public async Task StatusAsync_ReportsRetainedIdentityWithoutMutation()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var request = fixture.Request(operation: null);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("status", result.Outcome);
        Assert.AreEqual(fixture.InstanceId, result.InstanceId);
        Assert.AreEqual(fixture.Manifest.Image, result.Image);
        Assert.IsFalse(result.Running);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    public async Task UpgradeAsync_SignedReleaseWithoutThisHostArchitecture_FailsBeforeAnyMutation()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var other = DistributionAcquirer.HostImageArchitecture() == "amd64" ? "arm64" : "amd64";
        using var release = SignedImageReleaseFixture.Create(
            fixture.Root,
            $"sha256:{new string('b', 64)}",
            SignedImageReleaseFixture.ContractLabels,
            publishedArchitectures: [other]);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageManifest = release.ManifestPath,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.ExecuteAsync(
                request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None,
                release.CreateAcquirer));

        StringAssert.Contains(exception.Message, "does not support this host", StringComparison.Ordinal);
        // The release is resolved before the instance is touched, so no lifecycle operation is journaled.
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Paths.DeploymentStateRoot, "image-distribution.json")));
    }

    [TestMethod]
    public async Task UninstallAsync_AlreadyUninstalled_IsIdempotentAndPreservesState()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        var stateSentinel = Path.Combine(fixture.Paths.StateRoot, "sentinel");
        await File.WriteAllTextAsync(stateSentinel, "preserved");
        var request = fixture.Request(LifecycleOperationKind.Uninstall);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.IsTrue(File.Exists(stateSentinel));
        Assert.IsTrue(File.Exists(fixture.Paths.ManifestPath));
        Assert.IsNotNull(result.ResumeCommand);
        Assert.AreEqual($"hvo-skymonitor cameraagent reinstall --instance-id {fixture.InstanceId:D}", result.ResumeCommand);
    }

    [TestMethod]
    public async Task UninstallAsync_InstalledInstanceDrainsAndPreservesState()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        var stateSentinel = Path.Combine(fixture.Paths.StateRoot, "sentinel");
        await File.WriteAllTextAsync(stateSentinel, "preserved");
        var lifecycle = new FakeLifecycleClient();

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, result.LifecycleCondition);
        Assert.IsTrue(File.Exists(stateSentinel));
        Assert.IsNull(fixture.Runner.ActiveImageId);
        Assert.AreEqual(1, lifecycle.PauseCount);
        Assert.AreEqual(1, fixture.Runner.ComposeDownCount);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Completed, operation!.Phase);
        Assert.IsFalse(operation.MutationStarted);
    }

    [TestMethod]
    public async Task UninstallAsync_OwnershipDriftIsRejectedBeforeComposeDown()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.OmitOwnershipLabel = true;

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => new FakeLifecycleClient(), null,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(0, fixture.Runner.ComposeDownCount);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        var retained = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(InstanceLifecycleCondition.Installed, retained.LifecycleCondition);
    }

    [TestMethod]
    public async Task UninstallAsync_OwnedOrphanIsRejectedBeforeManifestCommit()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.RetainOwnedOrphanAfterDown = true;

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => new FakeLifecycleClient(), null,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(InstanceLifecycleCondition.Installed, retained.LifecycleCondition);
        Assert.AreEqual(1, fixture.Runner.ComposeDownCount);
    }

    [TestMethod]
    public async Task PurgeAsync_InstalledInstance_IsRejectedBeforeDeletion()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var request = fixture.Request(LifecycleOperationKind.Purge) with
        {
            ConfirmationInstanceId = fixture.InstanceId
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(Directory.Exists(fixture.Paths.InstanceRoot));
    }

    [TestMethod]
    public async Task PurgeAsync_UninstalledInstanceDeletesOnlySelectedSibling()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        var sibling = Path.Combine(fixture.Paths.ProductRoot, "cameraagents", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(sibling);
        File.SetUnixFileMode(sibling, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(sibling, "sentinel"), "preserved");
        var request = fixture.Request(LifecycleOperationKind.Purge) with { ConfirmationInstanceId = fixture.InstanceId };

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
        Assert.IsTrue(File.Exists(Path.Combine(sibling, "sentinel")));
    }

    [TestMethod]
    public async Task PurgeAsync_InterruptedTombstoneResumesDeletionWithoutRepublishing()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        var request = fixture.Request(LifecycleOperationKind.Purge) with { ConfirmationInstanceId = fixture.InstanceId };
        var operation = await CameraAgentLifecycleManager.BeginAsync(
            request, fixture.Paths, LifecycleOperationKind.Purge, fixture.Manifest, CancellationToken.None);
        var parent = Path.GetDirectoryName(fixture.Paths.InstanceRoot)!;
        var childName = Path.GetFileName(fixture.Paths.InstanceRoot);
        var inventory = SafeTreeDeletion.CaptureChildInventory(parent, childName, fixture.Uid, fixture.Gid);
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(fixture.Paths.OperationsRoot, $"purge-{operation.OperationId:D}.evidence.json"),
            new PurgeDeletionEvidence(
                DeploymentSchemaVersions.LifecycleOperation, operation.OperationId, operation.RequestSha256,
                LocalHostIdentity.ReadSha256(), fixture.Manifest, inventory),
            DeploymentJsonContext.Default.PurgeDeletionEvidence, CancellationToken.None);
        operation = await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            MutationStarted = true,
            Phase = LifecycleOperationPhase.Mutating
        }, CancellationToken.None);
        var identity = SafeTreeDeletion.ValidateChild(parent, childName, fixture.Uid, fixture.Gid);
        var tombstoneName = $".purge-{fixture.InstanceId:D}-{operation.OperationId:D}";
        SafeTreeDeletion.RenameChild(parent, childName, tombstoneName, identity);
        File.Delete(Path.Combine(parent, tombstoneName, "instance-manifest.json"));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
        Assert.IsFalse(Directory.EnumerateDirectories(parent, ".purge-*", SearchOption.TopDirectoryOnly).Any());
    }

    [TestMethod]
    public void OperationLock_AcquisitionHonorsCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-lifecycle-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operation.lock");
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsExactly<OperationCanceledException>(() => OperationLock.Acquire(
                path, TimeSpan.FromSeconds(5), cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SafeTreeDeletion_RemovesOnlyAuthenticatedChild()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-safe-delete-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(Path.Combine(child, "nested"));
        File.SetUnixFileMode(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Path.Combine(child, "nested"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(child, "nested", "value"), "value");
        try
        {
            SafeTreeDeletion.DeleteChild(parent, "child", NativeLinux.getuid(), NativeLinux.getgid());

            Assert.IsFalse(Directory.Exists(child));
            Assert.IsTrue(Directory.Exists(parent));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    public void SafeTreeDeletion_RejectsSymbolicLinkEntry()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-safe-delete-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        File.CreateSymbolicLink(Path.Combine(child, "escape"), "/etc/passwd");
        try
        {
            Assert.ThrowsExactly<InstallerException>(() =>
                SafeTreeDeletion.DeleteChild(parent, "child", NativeLinux.getuid(), NativeLinux.getgid()));
            Assert.IsTrue(Directory.Exists(child));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    public void SafeTreeDeletion_RenameAndDeleteRemainBoundToValidatedIdentity()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-safe-rename-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        File.SetUnixFileMode(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var identity = SafeTreeDeletion.ValidateChild(parent, "child", NativeLinux.getuid(), NativeLinux.getgid());
            SafeTreeDeletion.RenameChild(parent, "child", "tombstone", identity);
            SafeTreeDeletion.DeleteChild(parent, "tombstone", NativeLinux.getuid(), NativeLinux.getgid(), identity);

            Assert.IsFalse(Directory.Exists(Path.Combine(parent, "tombstone")));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    public void SafeTreeDeletion_RejectsGroupWritableDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-safe-mode-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        File.SetUnixFileMode(child,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        try
        {
            Assert.ThrowsExactly<InstallerException>(() =>
                SafeTreeDeletion.ValidateChild(parent, "child", NativeLinux.getuid(), NativeLinux.getgid()));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    public async Task LifecycleControlToken_MissingOrMismatchedMirrorIsRejectedWithoutMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-lifecycle-token-{Guid.NewGuid():N}");
        var paths = InstallationPaths.Create(root, Guid.NewGuid(), ProductionCatalog.CatalogId);
        Directory.CreateDirectory(Path.Combine(paths.ConfigRoot, "lifecycle-control"));
        Directory.CreateDirectory(Path.Combine(paths.ConfigRoot, "secrets"));
        var tokenPath = Path.Combine(paths.ConfigRoot, "lifecycle-control", "token");
        var mirrorPath = Path.Combine(paths.ConfigRoot, "secrets", "LifecycleControl__Token");
        SafeFileSystem.WriteTextAtomic(tokenPath, "authority-token");
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                CameraAgentLifecycleManager.ReadLifecycleControlTokenAsync(paths, CancellationToken.None));
            Assert.IsFalse(File.Exists(mirrorPath));

            SafeFileSystem.WriteTextAtomic(mirrorPath, "authority-token");
            var token = await CameraAgentLifecycleManager.ReadLifecycleControlTokenAsync(paths, CancellationToken.None);
            Assert.AreEqual("authority-token", token);

            SafeFileSystem.WriteTextAtomic(mirrorPath, "different-token");
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                CameraAgentLifecycleManager.ReadLifecycleControlTokenAsync(paths, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DataRow("lifecycle-mirror")]
    [DataRow("catalog-selection")]
    [DataRow("installation-verification")]
    public async Task LifecycleOperation_IncompleteCanonicalSecretsFailBeforeDockerOrJournalMutation(string missingSecret)
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            missingSecret == "installation-verification"
                ? InstanceLifecycleCondition.Uninstalled
                : InstanceLifecycleCondition.Installed);
        var path = missingSecret switch
        {
            "lifecycle-mirror" => Path.Combine(fixture.Paths.ConfigRoot, "secrets", "LifecycleControl__Token"),
            "catalog-selection" => Path.Combine(fixture.Paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion"),
            _ => Path.Combine(fixture.Paths.ConfigRoot, "installation-verification", "token")
        };
        File.Delete(path);
        var operation = missingSecret == "installation-verification"
            ? LifecycleOperationKind.Reinstall
            : LifecycleOperationKind.Uninstall;

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(operation), fixture.Runner, null, null,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(exception.Message, "credential", StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(0, fixture.Runner.InvocationCount);
    }

    [TestMethod]
    public async Task ReadOperationAsync_InvalidJournalIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-invalid-operation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operation.json");
        SafeFileSystem.WriteTextAtomic(path, "{}");
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                CameraAgentLifecycleManager.ReadOperationAsync(path, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void CatalogGarbageCollect_MutationRequiresExplicitVersion()
    {
        var request = new LifecycleRequest(
            LifecycleOperationKind.CatalogGarbageCollect,
            instanceId: null,
            InstallRequest.DefaultProductRoot,
            dryRun: false,
            resume: false,
            json: false);

        Assert.ThrowsExactly<InstallUsageException>(request.Validate);
    }

    [TestMethod]
    public void LifecycleRequest_PasswordFreeReceiptHashRemainsStable()
    {
        var request = new LifecycleRequest(
            LifecycleOperationKind.Uninstall,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            InstallRequest.DefaultProductRoot,
            dryRun: false,
            resume: false,
            json: false);

        Assert.AreEqual("9c011238407c7dee794989a757c89a4dc2ae6fceb8905f546293eb26e201478e", request.ComputeRequestSha256());
    }

    [TestMethod]
    public async Task CatalogGarbageCollect_InterruptedDeletionRequiresExactResumeRequest()
    {
        var previous = Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT");
        Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", "1");
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-gc-{Guid.NewGuid():N}");
        var paths = InstallationPaths.Create(root, Guid.Empty, ProductionCatalog.CatalogId);
        var versionsRoot = Path.Combine(paths.CatalogRoot, "versions");
        var version = "hyg-v4.2-p3-s2-r2";
        var operationId = Guid.NewGuid();
        var tombstoneName = $".gc-{version}-{operationId:N}";
        var tombstone = Path.Combine(versionsRoot, tombstoneName);
        Directory.CreateDirectory(tombstone);
        Directory.CreateDirectory(paths.OperationsRoot);
        Directory.CreateDirectory(Path.Combine(versionsRoot, ProductionCatalog.PackageVersion));
        Directory.CreateSymbolicLink(Path.Combine(paths.CatalogRoot, "current"), $"versions/{ProductionCatalog.PackageVersion}");
        await File.WriteAllTextAsync(Path.Combine(tombstone, "remaining"), "remaining");
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var uid = NativeLinux.getuid();
        var gid = NativeLinux.getgid();
        var inventory = SafeTreeDeletion.CaptureChildInventory(versionsRoot, tombstoneName, uid, gid);
        var daemon = new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2");
        var request = new LifecycleRequest(
            LifecycleOperationKind.CatalogGarbageCollect, null, root, dryRun: false, resume: true, json: false)
        {
            CatalogVersion = version
        };
        var catalog = new CatalogInstallationIdentity(
            ProductionCatalog.CatalogId, version, "2", "3", new string('a', 64), 1, 1,
            paths.CatalogRoot, new string('b', 64), "test");
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(paths.OperationsRoot, $"catalog-gc-{operationId:N}.json"),
            new CatalogGarbageCollectionEvidence(
                DeploymentSchemaVersions.LifecycleOperation, operationId, request.ComputeRequestSha256(),
                LocalHostIdentity.ReadSha256(), daemon, version, catalog, inventory),
            DeploymentJsonContext.Default.CatalogGarbageCollectionEvidence,
            CancellationToken.None);
        try
        {
            var wrongRequest = request with { CatalogVersion = "hyg-v4.2-p3-s2-r3" };
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                wrongRequest, new FakeRunner(daemon), null, null, uid, gid, CancellationToken.None));
            Assert.IsTrue(Directory.Exists(tombstone));

            File.Delete(Path.Combine(tombstone, "remaining"));
            var result = await CameraAgentLifecycleManager.ExecuteAsync(
                request, new FakeRunner(daemon), null, null, uid, gid, CancellationToken.None);

            Assert.AreEqual(operationId, result.OperationId);
            Assert.IsFalse(Directory.Exists(tombstone));

            var postDeleteOperationId = Guid.NewGuid();
            await SafeFileSystem.WriteJsonAtomicAsync(
                Path.Combine(paths.OperationsRoot, $"catalog-gc-{postDeleteOperationId:N}.json"),
                new CatalogGarbageCollectionEvidence(
                    DeploymentSchemaVersions.LifecycleOperation, postDeleteOperationId, request.ComputeRequestSha256(),
                    LocalHostIdentity.ReadSha256(), daemon, version, catalog, inventory, DeletionCommitted: true),
                DeploymentJsonContext.Default.CatalogGarbageCollectionEvidence,
                CancellationToken.None);

            result = await CameraAgentLifecycleManager.ExecuteAsync(
                request, new FakeRunner(daemon), null, null, uid, gid, CancellationToken.None);
            Assert.AreEqual(postDeleteOperationId, result.OperationId);

            var preTombstoneOperationId = Guid.NewGuid();
            var original = Path.Combine(versionsRoot, version);
            Directory.CreateDirectory(original);
            File.SetUnixFileMode(original, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(original, "remaining"), "remaining");
            var preTombstoneInventory = SafeTreeDeletion.CaptureChildInventory(versionsRoot, version, uid, gid);
            await SafeFileSystem.WriteJsonAtomicAsync(
                Path.Combine(paths.OperationsRoot, $"catalog-gc-{preTombstoneOperationId:N}.json"),
                new CatalogGarbageCollectionEvidence(
                    DeploymentSchemaVersions.LifecycleOperation, preTombstoneOperationId, request.ComputeRequestSha256(),
                    LocalHostIdentity.ReadSha256(), daemon, version, catalog, preTombstoneInventory),
                DeploymentJsonContext.Default.CatalogGarbageCollectionEvidence,
                CancellationToken.None);

            result = await CameraAgentLifecycleManager.ExecuteAsync(
                request, new FakeRunner(daemon), null, null, uid, gid, CancellationToken.None);
            Assert.AreEqual(preTombstoneOperationId, result.OperationId);
            Assert.IsFalse(Directory.Exists(original));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task CatalogInstall_ResumeWithoutRetainedOperationIsRejectedBeforeAcquisition()
    {
        var previous = Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT");
        Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", "1");
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-install-{Guid.NewGuid():N}");
        var daemon = new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2");
        var request = new LifecycleRequest(
            LifecycleOperationKind.CatalogInstall, null, root, dryRun: false, resume: true, json: false)
        {
            CatalogBundle = Path.Combine(root, "missing-bundle")
        };
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                request, new FakeRunner(daemon), null, null, NativeLinux.getuid(), NativeLinux.getgid(), CancellationToken.None));
            StringAssert.Contains(exception.Message, "No matching incomplete", StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void CatalogSelectionResume_AllowsOnlyMatchingMutatedCandidate()
    {
        var instanceId = Guid.NewGuid();
        var request = new LifecycleRequest(
            LifecycleOperationKind.CatalogSelect, instanceId, InstallRequest.DefaultProductRoot,
            dryRun: false, resume: true, json: false)
        {
            CatalogVersion = "hyg-v4.2-p3-s2-r2"
        };
        var image = new ImageInstallationIdentity(
            "registry", $"cameraagent@sha256:{new string('a', 64)}", $"sha256:{new string('b', 64)}", "amd64", null);
        var original = new CatalogInstallationIdentity(
            ProductionCatalog.CatalogId, ProductionCatalog.PackageVersion, "2", "3", new string('c', 64), 1, 1,
            "/catalog", new string('d', 64), "test");
        var candidate = original with { PackageVersion = request.CatalogVersion };
        var operation = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.CatalogSelect,
            instanceId, request.ComputeRequestSha256(), LifecycleOperationPhase.Mutating, InstallationStatus.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, image, CandidateCatalog: candidate,
            OriginalCatalog: original, MutationStarted: true);

        Assert.AreEqual(candidate.PackageVersion,
            CameraAgentLifecycleManager.ResumableCatalogSelectionVersion(request, operation));
        Assert.IsNull(CameraAgentLifecycleManager.ResumableCatalogSelectionVersion(request with { Resume = false }, operation));
        Assert.IsNull(CameraAgentLifecycleManager.ResumableCatalogSelectionVersion(
            request, operation with { Kind = LifecycleOperationKind.Upgrade }));
        Assert.IsNull(CameraAgentLifecycleManager.ResumableCatalogSelectionVersion(
            request, operation with { Status = InstallationStatus.Completed }));
    }

    [TestMethod]
    public async Task CatalogSelectAndRollback_CurrentContractsConverge()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, seedCatalogSelection: false);
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrEmpty(bundle)) Assert.Inconclusive("Set HVO_PRODUCTION_CATALOG_BUNDLE to run catalog transitions.");
        var selected = CatalogInstaller.Install(bundle, fixture.Paths.CatalogRoot, Guid.NewGuid());
        var selectedReferenceRoot = Path.Combine(fixture.Paths.CatalogReferencesRoot, selected.PackageVersion);
        Directory.CreateDirectory(selectedReferenceRoot);
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(selectedReferenceRoot, "installed.json"), selected,
            DeploymentJsonContext.Default.CatalogInstallationIdentity, CancellationToken.None);

        const string previousVersion = "hyg-v4.2-p3-s2-r2";
        var selectedVersionRoot = Path.Combine(fixture.Paths.CatalogRoot, "versions", selected.PackageVersion);
        var previousVersionRoot = Path.Combine(fixture.Paths.CatalogRoot, "versions", previousVersion);
        Directory.CreateDirectory(previousVersionRoot);
        foreach (var path in Directory.EnumerateFiles(selectedVersionRoot))
            File.Copy(path, Path.Combine(previousVersionRoot, Path.GetFileName(path)));
        var previousManifestPath = Path.Combine(previousVersionRoot, "manifest.json");
        File.SetUnixFileMode(previousManifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var previousManifestJson = await File.ReadAllTextAsync(previousManifestPath);
        await File.WriteAllTextAsync(
            previousManifestPath, previousManifestJson.Replace(selected.PackageVersion, previousVersion, StringComparison.Ordinal));
        var previousSnapshot = CatalogSnapshotResolver.Resolve(
            ProductionCatalog.ResolverOptions(fixture.Paths.CatalogRoot, previousVersion));
        var previous = ProductionCatalog.ToIdentity(previousSnapshot, fixture.Paths.CatalogRoot);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath, fixture.Manifest with { Catalog = previous },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ResultPath, result with { Catalog = previous },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        SafeFileSystem.WriteTextAtomic(
            Path.Combine(fixture.Paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion"), previous.PackageVersion);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);

        var selectedResult = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.CatalogSelect) with { CatalogVersion = selected.PackageVersion },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual(selected.PackageVersion, selectedResult.Catalog!.PackageVersion);

        var rollbackResult = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.CatalogRollback), fixture.Runner,
            _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual(previous.PackageVersion, rollbackResult.Catalog!.PackageVersion);
        Assert.AreEqual(2, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(2, fixture.Runner.ComposeRestartCount);
    }

    [TestMethod]
    public async Task RecoverySnapshot_ForeignCandidateIsRejectedWithoutCanonicalOverwrite()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var candidate = fixture.Manifest.Image with
        {
            ImmutableReference = $"cameraagent@sha256:{new string('7', 64)}",
            ImageId = $"sha256:{new string('6', 64)}"
        };
        var operation = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.Upgrade,
            fixture.InstanceId, new string('5', 64), LifecycleOperationPhase.Mutating, InstallationStatus.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, fixture.Manifest.Image, candidate,
            fixture.Manifest.Catalog, MutationStarted: true);
        var root = Path.Combine(fixture.Paths.OperationsRoot, "snapshot-test");
        Directory.CreateDirectory(root);
        var snapshotManifestPath = Path.Combine(root, "manifest.json");
        var snapshotResultPath = Path.Combine(root, "result.json");
        await SafeFileSystem.WriteJsonAtomicAsync(
            snapshotManifestPath, fixture.Manifest with { Image = candidate },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            snapshotResultPath, result with { Image = candidate },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        var canonicalManifest = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        var canonicalResult = await File.ReadAllTextAsync(fixture.Paths.ResultPath);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ReadRecoverySnapshotAsync(
            fixture.Paths, operation, snapshotManifestPath, snapshotResultPath, CancellationToken.None));

        Assert.AreEqual(canonicalManifest, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.AreEqual(canonicalResult, await File.ReadAllTextAsync(fixture.Paths.ResultPath));
    }

    [TestMethod]
    public async Task UpgradeAsync_ResumeAfterCommitCompletesWithoutRollingBackCandidate()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('7', 64)}";
        var candidateImageId = $"sha256:{new string('8', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient { RejectNextResume = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        var committedManifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(candidateImageId, committedManifest.Image.ImageId);
        Assert.AreEqual(LifecycleOperationPhase.Committed, retained!.Phase);
        Assert.IsTrue(retained.MutationStarted);
        Assert.IsNotNull(retained.PreMutationContinuity);
        Assert.IsNotNull(retained.PostMutationContinuity);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        // One durable pause per operation; the post-mutation boundary only confirms it.
        Assert.AreEqual(1, lifecycle.PauseCount);
        Assert.AreEqual(1, lifecycle.ConfirmCount);
        retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Completed, retained!.Phase);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    public async Task UpgradeAsync_SignedReleaseResumeAfterLostAcknowledgementRetainsReleaseRecord()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        var candidateImageId = $"sha256:{new string('9', 64)}";
        using var release = SignedImageReleaseFixture.Create(fixture.Root, candidateImageId, CandidateContractLabels());
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateImageId, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient { RejectNextResume = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageManifest = release.ManifestPath,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        var evidencePath = Path.Combine(fixture.Paths.DeploymentStateRoot, "image-distribution.json");

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, release.CreateAcquirer));

        // The record is settled with the durable commit, before the resume whose acknowledgement was lost.
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Committed, retained!.Phase);
        Assert.AreEqual(candidateImageId, await ReadEvidenceImageIdAsync(evidencePath));

        // A failure between the commit and the record write leaves no record; the resumed run must repair it rather
        // than return completed while the instance runs a release the retained evidence does not name.
        File.Delete(evidencePath);
        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, release.CreateAcquirer);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(candidateImageId, result.Image!.ImageId);
        Assert.AreEqual(candidateImageId, await ReadEvidenceImageIdAsync(evidencePath));
        retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Completed, retained!.Phase);
    }

    [TestMethod]
    public async Task UpgradeAsync_ImageReferenceUpgradeOfSignedInstanceWithdrawsReleaseRecord()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        var signedImageId = $"sha256:{new string('9', 64)}";
        using var release = SignedImageReleaseFixture.Create(fixture.Root, signedImageId, CandidateContractLabels());
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            signedImageId, signedImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        var evidencePath = Path.Combine(fixture.Paths.DeploymentStateRoot, "image-distribution.json");

        var signed = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageManifest = release.ManifestPath,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, release.CreateAcquirer);
        Assert.AreEqual("completed", signed.Outcome);
        Assert.AreEqual(signedImageId, await ReadEvidenceImageIdAsync(evidencePath));

        // Move the instance to an operator-supplied image that no signed release named.
        var operatorReference = $"ghcr.io/example/cameraagent@sha256:{new string('c', 64)}";
        var operatorImageId = $"sha256:{new string('d', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, signedImageId, signedImageId,
            operatorReference, operatorImageId, fixture.Uid, fixture.Gid);
        var moved = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = operatorReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", moved.Outcome);
        Assert.AreEqual(operatorImageId, moved.Image!.ImageId);
        Assert.IsFalse(File.Exists(evidencePath), "The superseded release record survived an upgrade to an image no signed release named.");
    }

    [TestMethod]
    public async Task UpgradeAsync_SignedReleaseRefusedBeforeMutationLeavesInstanceOperableWithoutResume()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        var candidateImageId = $"sha256:{new string('9', 64)}";
        // The signed compatibility record contradicts the minimum compatible revision the fake daemon reports for
        // the candidate image, so the label-agreement gate refuses the release after the journal is opened and
        // before any mutation, exactly the refusal the installer campaign pins.
        var contradicting = CandidateContractLabels();
        contradicting["io.hvo.skymonitor.minimum-compatible-revision"] = new string('6', 40);
        using var release = SignedImageReleaseFixture.Create(fixture.Root, candidateImageId, contradicting);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateImageId, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        var evidencePath = Path.Combine(fixture.Paths.DeploymentStateRoot, "image-distribution.json");
        var manifestBefore = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        var refused = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageManifest = release.ManifestPath,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            refused, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, release.CreateAcquirer));

        StringAssert.Contains(exception.Message, "minimum compatible revision", StringComparison.Ordinal);
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(manifestBefore, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.IsFalse(File.Exists(evidencePath), "A refused release wrote a release record.");
        // The refusal is journalled as terminal: nothing ran, so nothing reports itself as running.
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationKind.Upgrade, retained!.Kind);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained.FailureCode);
        Assert.AreEqual(LifecycleOperationPhase.Prepared, retained.Phase);
        Assert.IsFalse(retained.MutationStarted);
        StringAssert.Contains(retained.FailureMessage!, "minimum compatible revision", StringComparison.Ordinal);
        var refusedJournal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);

        // Resuming the refused command is refused in turn: there is no incomplete operation to resume.
        var resumeRefusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            refused with { Resume = true }, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, release.CreateAcquirer));
        StringAssert.Contains(resumeRefusal.Message, "No incomplete matching lifecycle operation exists", StringComparison.Ordinal);
        Assert.AreEqual(refusedJournal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
        // Nor does a --resume of a different kind find anything to continue.
        var uninstallResumeRefusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall) with { Resume = true }, fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(uninstallResumeRefusal.Message, "No incomplete matching lifecycle operation exists", StringComparison.Ordinal);
        Assert.AreEqual(refusedJournal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));

        // A different upgrade proceeds without --resume and supersedes the refused entry.
        var operatorReference = $"ghcr.io/example/cameraagent@sha256:{new string('c', 64)}";
        var operatorImageId = $"sha256:{new string('d', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            operatorReference, operatorImageId, fixture.Uid, fixture.Gid);
        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = operatorReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", upgraded.Outcome);
        Assert.AreEqual(operatorImageId, upgraded.Image!.ImageId);
        var superseding = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreNotEqual(retained.OperationId, superseding!.OperationId);
        Assert.AreEqual(LifecycleOperationPhase.Completed, superseding.Phase);
    }

    [TestMethod]
    public async Task RollbackAsync_ProceedsWithoutResumeAfterRefusedUpgrade()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", upgraded.Outcome);

        // Upgrading to the image that is already active is refused after the journal is opened and before any
        // mutation, the same window the label-agreement gate occupies.
        var refused = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            refused, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(exception.Message, "already active", StringComparison.Ordinal);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained.FailureCode);
        Assert.IsFalse(retained.MutationStarted);
        Assert.AreEqual(1, lifecycle.PauseCount);

        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", rolledBack.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, rolledBack.Image!.ImageId);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(2, lifecycle.PauseCount);
    }

    [TestMethod]
    public async Task UninstallAsync_ProceedsWithoutResumeAfterRefusedUpgrade()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        var refused = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = fixture.Manifest.Image.ImmutableReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            refused, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained!.FailureCode);

        var uninstalled = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", uninstalled.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, uninstalled.LifecycleCondition);
        var completed = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationKind.Uninstall, completed!.Kind);
        Assert.AreEqual(LifecycleOperationPhase.Completed, completed.Phase);
    }

    [TestMethod]
    public async Task SettleRefusedOperation_ResumedMutatedOperationRestoredBeforeFailureIsNotRefused()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        // The operation as BeginAsync returned it on --resume: it had started mutating before the interruption.
        var begun = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.Upgrade,
            fixture.InstanceId, new string('5', 64), LifecycleOperationPhase.Mutating, InstallationStatus.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, fixture.Manifest.Image,
            fixture.Manifest.Image with { ImageId = $"sha256:{new string('6', 64)}" }, fixture.Manifest.Catalog,
            MutationStarted: true);
        // The journal after the resumed run restored the original runtime and cleared the flag, immediately before
        // its next mutation record: the only window in which a failure reaches the wrapper on such an operation.
        var restored = begun with { Phase = LifecycleOperationPhase.Prepared, MutationStarted = false };
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, restored, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        var journal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);

        await CameraAgentLifecycleManager.SettleRefusedOperationAsync(
            fixture.Paths, begun, new IOException("simulated recovery-record write failure"));

        // The journal's current flag is not a history of mutation: the record keeps its recovery semantics.
        Assert.AreEqual(journal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Running, retained!.Status);
        Assert.IsNull(retained.FailureCode);

        // The same journal state behind an operation that never mutated is a refusal.
        await CameraAgentLifecycleManager.SettleRefusedOperationAsync(
            fixture.Paths, restored, new InstallerException("simulated pre-mutation refusal"));
        retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained.FailureCode);
        Assert.IsFalse(retained.MutationStarted);

        // A record that is not the begun operation, or is no longer running, is never touched.
        var journalAfterRefusal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);
        await CameraAgentLifecycleManager.SettleRefusedOperationAsync(
            fixture.Paths, restored with { OperationId = Guid.NewGuid() }, new InstallerException("other"));
        await CameraAgentLifecycleManager.SettleRefusedOperationAsync(
            fixture.Paths, restored, new InstallerException("already failed"));
        Assert.AreEqual(journalAfterRefusal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    public async Task RollbackAsync_ProceedsWithoutResumeAfterLegacyPreMutationRefusalJournal()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", upgraded.Outcome);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        // The journal an earlier release left behind for a label-agreement refusal: running, unmutated, no code.
        var legacyRequest = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = $"ghcr.io/example/cameraagent@sha256:{new string('e', 64)}",
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        var legacy = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.Upgrade,
            fixture.InstanceId, legacyRequest.ComputeRequestSha256(), LifecycleOperationPhase.Prepared, InstallationStatus.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, manifest.Image, null, manifest.Catalog);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, legacy, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);

        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", rolledBack.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        var superseding = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreNotEqual(legacy.OperationId, superseding!.OperationId);
        Assert.AreEqual(LifecycleOperationKind.Rollback, superseding.Kind);

        // --resume of the matching request still continues an unmutated incomplete record: crash-between-records
        // recovery keeps its operation ID.
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, legacy with { OriginalImage = fixture.Manifest.Image },
            DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        var manifestNow = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var resumed = await CameraAgentLifecycleManager.BeginAsync(
            legacyRequest with { Resume = true }, fixture.Paths, LifecycleOperationKind.Upgrade, manifestNow, CancellationToken.None);
        Assert.AreEqual(legacy.OperationId, resumed.OperationId);
    }

    [TestMethod]
    public async Task UpgradeAsync_SettlementFailureNeverReplacesTheRefusal()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        var candidateImageId = $"sha256:{new string('9', 64)}";
        var contradicting = CandidateContractLabels();
        contradicting["io.hvo.skymonitor.minimum-compatible-revision"] = new string('6', 40);
        using var release = SignedImageReleaseFixture.Create(fixture.Root, candidateImageId, contradicting);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateImageId, candidateImageId, fixture.Uid, fixture.Gid);
        // Once the journal has been opened, replace it with bytes the settlement cannot read, so the settlement's
        // own failure competes with the gate's refusal. (The owner-directory normalisation inside the atomic
        // writer defeats a read-only directory as the fault, so the fault is the retained bytes themselves.)
        const string corrupted = "{";
        fixture.Runner.OnCandidateInspect = () => File.WriteAllText(fixture.Paths.LifecycleStatePath, corrupted);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageManifest = release.ManifestPath,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, release.CreateAcquirer));

        StringAssert.Contains(exception.Message, "minimum compatible revision", StringComparison.Ordinal);
        // The settlement failed and changed nothing; the journal is exactly what the fault left.
        Assert.AreEqual(corrupted, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    public async Task UpgradeAsync_CancellationBeforeMutationIsNotRefusedAndDoesNotBlock()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.OnCandidateInspect = () => throw new OperationCanceledException("simulated cancellation during preparation");
        var lifecycle = new FakeLifecycleClient();
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Running, retained!.Status);
        Assert.IsNull(retained.FailureCode);
        Assert.IsFalse(retained.MutationStarted);

        // The cancelled, unmutated operation is resumable and blocks nothing else.
        fixture.Runner.OnCandidateInspect = null;
        var uninstalled = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", uninstalled.Outcome);
    }

    [TestMethod]
    public async Task CatalogGarbageCollection_RefusedSelectionHoldsNoReference()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        const string candidateVersion = "hyg-v4.2-p3-s2-r2";
        var candidate = fixture.Manifest.Catalog with { PackageVersion = candidateVersion };
        var refused = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.CatalogSelect,
            fixture.InstanceId, new string('5', 64), LifecycleOperationPhase.CandidateValidated, InstallationStatus.Failed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, fixture.Manifest.Image, null, fixture.Manifest.Catalog, candidate,
            FailureCode: CameraAgentLifecycleManager.RefusedFailureCode, FailureMessage: "refused");
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, refused, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);

        Assert.IsFalse(await CatalogLifecycleManager.IsReferencedAsync(fixture.Paths, candidateVersion, CancellationToken.None));

        // The same record while it was still running did hold the reference.
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, refused with { Status = InstallationStatus.Running, FailureCode = null, FailureMessage = null },
            DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        Assert.IsTrue(await CatalogLifecycleManager.IsReferencedAsync(fixture.Paths, candidateVersion, CancellationToken.None));
    }

    [TestMethod]
    public async Task ReadOperationAsync_RefusedRecordThatClaimsMutationIsRejected()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var refused = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.Upgrade,
            fixture.InstanceId, new string('5', 64), LifecycleOperationPhase.Prepared, InstallationStatus.Failed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, fixture.Manifest.Image, null, fixture.Manifest.Catalog,
            FailureCode: CameraAgentLifecycleManager.RefusedFailureCode, FailureMessage: "refused");
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, refused, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        Assert.IsNotNull(await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None));

        foreach (var impossible in new[]
                 {
                     refused with { MutationStarted = true },
                     refused with { Status = InstallationStatus.Running }
                 })
        {
            await SafeFileSystem.WriteJsonAtomicAsync(
                fixture.Paths.LifecycleStatePath, impossible, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None));
        }
    }

    /// <summary>
    /// The signed compatibility record must agree with the labels the fake daemon reports for the candidate image,
    /// whose revision differs from the fixture's default, or the label-agreement gate refuses the release.
    /// </summary>
    private static Dictionary<string, string> CandidateContractLabels()
        => new(SignedImageReleaseFixture.ContractLabels, StringComparer.Ordinal)
        {
            ["org.opencontainers.image.revision"] = new string('9', 40)
        };

    private static async Task<string?> ReadEvidenceImageIdAsync(string evidencePath)
    {
        using var evidence = JsonDocument.Parse(await File.ReadAllBytesAsync(evidencePath));
        return evidence.RootElement.GetProperty("imageId").GetString();
    }

    [TestMethod]
    public async Task ReinstallAsync_PreservedInstanceVerifiesOwnerBeforeCommit()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid,
            exists: false);
        var lifecycle = new FakeLifecycleClient();

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Reinstall),
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Installed, result.LifecycleCondition);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsNotNull(operation!.PostMutationContinuity);
    }

    [TestMethod]
    public async Task ReinstallAsync_DaemonReplacementIsRejectedBeforeComposeUp()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid,
            exists: false);
        fixture.Runner.DaemonDriftAtInfoCall = 3;

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Reinstall), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);
        Assert.IsNull(fixture.Runner.ActiveImageId);
    }

    [TestMethod]
    public async Task UpgradeAsync_DaemonReplacementIsRejectedBeforeRestart()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('d', 64)}";
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, $"sha256:{new string('e', 64)}", fixture.Uid, fixture.Gid);
        fixture.Runner.DriftDaemonAfterComposeUp = true;

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            }, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(0, fixture.Runner.ComposeRestartCount);
    }

    [TestMethod]
    public async Task UpgradeAsync_FailedCandidateRestoresExactOriginalRuntime()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, manifest.Image.ImageId);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId, failure.ToString());
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    public async Task UpgradeAsync_OwnerStateRegressionRestoresExactOriginalRuntime()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { CurrentOwnerBootstrapState = "owner-ready" };
        owner.VerificationStates.Enqueue("owner-password-change-required");
        owner.VerificationStates.Enqueue("owner-ready");
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    public async Task UpgradeAsync_OwnerCompletesPasswordReplacementDuringMutation_Converges()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        owner.VerificationStates.Enqueue("owner-ready");
        owner.VerificationStates.Enqueue("owner-ready");
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(candidateImageId, result.Image!.ImageId);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
    }

    [TestMethod]
    public async Task UpgradeAsync_OwnerStateRegressesAfterCandidateRestart_RestoresOriginalRuntime()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        owner.VerificationStates.Enqueue("owner-ready");
        owner.VerificationStates.Enqueue("owner-password-change-required");
        owner.VerificationStates.Enqueue("owner-ready");
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
    }

    [TestMethod]
    public async Task UpgradeAsync_InterruptedMutationPersistsRestoredOwnerProgressionBeforeRetry()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath,
            retained! with
            {
                Phase = LifecycleOperationPhase.Mutating,
                Status = InstallationStatus.Running,
                MutationStarted = true,
                FailureCode = null,
                FailureMessage = null,
                ExpectedOwnerBootstrapState = "owner-password-change-required"
            },
            DeploymentJsonContext.Default.LifecycleOperationState,
            CancellationToken.None);
        owner.VerificationStates.Enqueue("owner-ready");
        owner.VerificationStates.Enqueue("owner-password-change-required");
        owner.VerificationStates.Enqueue("owner-ready");

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
    }

    [TestMethod]
    public async Task UpgradeAsync_PreparedOwnerStateChangeIsRejectedWithoutOverwritingSnapshot()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.RejectNextStop = true;
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { CurrentOwnerBootstrapState = "owner-ready" };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));
        owner.CurrentOwnerBootstrapState = "owner-password-change-required";

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
    }

    [TestMethod]
    public async Task UpgradeAsync_FailedLocalRunnerCandidateCapturesBothContainerLogsBeforeRollback()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(
            fixture.Paths,
            fixture.Manifest.Image.ImmutableReference,
            fixture.Manifest.Image.ImageId,
            candidateReference,
            candidateImageId,
            fixture.Uid,
            fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner,
            _ => new FakeLifecycleClient(),
            _ => owner,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None));

        var compact = fixture.InstanceId.ToString("N");
        var cameraAgentContainer = $"hvo-skymonitor-{compact}";
        var replayRunnerContainer = $"{cameraAgentContainer}-replay";
        CollectionAssert.AreEqual(
            new[] { cameraAgentContainer, replayRunnerContainer },
            fixture.Runner.LoggedContainers.ToArray());
        var firstRecoveryUp = fixture.Runner.Events.FindLastIndex(static value => value == "compose-up");
        Assert.IsGreaterThan(fixture.Runner.Events.IndexOf($"logs:{cameraAgentContainer}"), firstRecoveryUp);
        Assert.IsGreaterThan(fixture.Runner.Events.IndexOf($"logs:{replayRunnerContainer}"), firstRecoveryUp);

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath,
            CancellationToken.None);
        var diagnostics = await File.ReadAllTextAsync(Path.Combine(
            fixture.Paths.OperationsRoot,
            "lifecycle",
            retained!.OperationId.ToString("D"),
            "candidate-diagnostics.txt"));
        StringAssert.Contains(diagnostics, $"container={cameraAgentContainer}", StringComparison.Ordinal);
        StringAssert.Contains(diagnostics, $"container={replayRunnerContainer}", StringComparison.Ordinal);
        StringAssert.Contains(diagnostics, $"{replayRunnerContainer} startup", StringComparison.Ordinal);
        StringAssert.Contains(diagnostics, $"{replayRunnerContainer} failure", StringComparison.Ordinal);
        Assert.IsFalse(diagnostics.Contains("runner-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("stop")]
    [DataRow("backup")]
    public async Task UpgradeAsync_InterruptedPreRecreateBoundaryRestoresOriginalRuntime(string failurePoint)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('d', 64)}";
        var candidateImageId = $"sha256:{new string('e', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.RejectNextStop = failurePoint == "stop";
        fixture.Runner.RejectNextBackup = failurePoint == "backup";
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, manifest.Image.ImageId);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    [DataRow("cameraagent-compose-v1")]
    [DataRow("cameraagent-compose-v3")]
    public async Task ExecuteAsync_NonCanonicalComposeIsRejectedBeforeDockerOrJournalMutation(string composeVersion)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath, fixture.Manifest with { ComposeTemplateVersion = composeVersion },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ResultPath, result with { ComposeTemplateVersion = composeVersion },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        var retainedManifest = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        var retainedResult = await File.ReadAllTextAsync(fixture.Paths.ResultPath);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(operation: null), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(0, fixture.Runner.InvocationCount);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(retainedManifest, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.AreEqual(retainedResult, await File.ReadAllTextAsync(fixture.Paths.ResultPath));
    }

    [TestMethod]
    [DataRow("cameraagent-compose-v1")]
    [DataRow("cameraagent-compose-v4")]
    public async Task ExecuteAsync_UnsupportedPreviousComposePropertyIsRejectedBeforeDockerOrJournalMutation(
        string previousComposeVersion)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var json = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        await File.WriteAllTextAsync(
            fixture.Paths.ManifestPath,
            json.Replace("{\n", $"{{\n  \"previousComposeTemplateVersion\": \"{previousComposeVersion}\",\n", StringComparison.Ordinal));

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(operation: null), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(0, fixture.Runner.InvocationCount);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    public async Task StatusAsync_CurrentPreviousComposePropertyIsAcceptedWithoutCompatibilityBranch()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var json = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        await File.WriteAllTextAsync(
            fixture.Paths.ManifestPath,
            json.Replace("{\n", "{\n  \"previousComposeTemplateVersion\": \"cameraagent-compose-v2\",\n", StringComparison.Ordinal));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(operation: null), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("status", result.Outcome);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    public async Task RollbackAsync_CurrentComposeRestoresPreviousImageAndExactCompose()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var originalCompose = await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml"));
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";
        var candidateImageId = $"sha256:{new string('b', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();

        await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        var upgraded = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(ComposeDeployment.TemplateVersion, upgraded.ComposeTemplateVersion);
        Assert.AreEqual(fixture.Manifest.Image, upgraded.PreviousImage);
        var retainedRollbackCompose = Path.Combine(fixture.Paths.DeploymentStateRoot, "rollback", "previous-compose.yml");
        await File.WriteAllTextAsync(retainedRollbackCompose, "services: {}\nnetworks:\n  foreign: {}\n");

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback),
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(candidateImageId, fixture.Runner.ActiveImageId);
        // The rollback was refused before any mutation, so its journal is terminal and the repaired rollback runs
        // as an ordinary command rather than a --resume.
        var refused = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationKind.Rollback, refused!.Kind);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, refused.FailureCode);
        Assert.IsFalse(refused.MutationStarted);
        await File.WriteAllTextAsync(retainedRollbackCompose, originalCompose);

        var rollback = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback),
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual(fixture.Manifest.Image.ImageId, rollback.Image!.ImageId);
        var rolledBack = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(ComposeDeployment.TemplateVersion, rolledBack.ComposeTemplateVersion);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(originalCompose, await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml")));
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private readonly string? previousAllowTestRoot;

        private LifecycleFixture(
            string root,
            Guid instanceId,
            Guid applicationIdentity,
            InstallationPaths paths,
            InstanceManifest manifest,
            FakeRunner runner,
            uint uid,
            uint gid,
            string? previousAllowTestRoot)
        {
            Root = root;
            InstanceId = instanceId;
            ApplicationIdentity = applicationIdentity;
            Paths = paths;
            Manifest = manifest;
            Runner = runner;
            Uid = uid;
            Gid = gid;
            this.previousAllowTestRoot = previousAllowTestRoot;
        }

        public string Root { get; }
        public Guid InstanceId { get; }
        public Guid ApplicationIdentity { get; }
        public InstallationPaths Paths { get; }
        public InstanceManifest Manifest { get; }
        public FakeRunner Runner { get; }
        public uint Uid { get; }
        public uint Gid { get; }

        public static async Task<LifecycleFixture> CreateAsync(
            InstanceLifecycleCondition condition,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile replayProfile =
                HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess,
            bool seedCatalogSelection = true)
        {
            var previous = Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT");
            Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", "1");
            var root = Path.Combine(Path.GetTempPath(), $"hvo-lifecycle-{Guid.NewGuid():N}");
            var instanceId = Guid.NewGuid();
            var paths = InstallationPaths.Create(root, instanceId, ProductionCatalog.CatalogId);
            foreach (var directory in new[]
            {
                paths.ProductRoot, Path.Combine(paths.ProductRoot, "cameraagents"), paths.InstanceRoot,
                paths.ConfigRoot, paths.StateRoot, paths.DeploymentStateRoot,
                Path.Combine(paths.ConfigRoot, "compose"), paths.OperationsRoot
            }) Directory.CreateDirectory(directory);
            // The state preflight reads the selected catalog manifest before any lifecycle mutation, so the fixture
            // publishes the same manifest-version and catalog-identity pointer a real installation exposes. Tests
            // that install a real bundle into this root opt out because the adopted root needs its lineage binding.
            if (seedCatalogSelection)
            {
                var catalogVersionRoot = Path.Combine(paths.CatalogRoot, "versions", ProductionCatalog.PackageVersion);
                Directory.CreateDirectory(catalogVersionRoot);
                await File.WriteAllTextAsync(
                    Path.Combine(catalogVersionRoot, "manifest.json"),
                    "{\"manifestVersion\":2,\"catalog\":{\"id\":\"" + ProductionCatalog.CatalogId + "\"}}",
                    CancellationToken.None);
                Directory.CreateSymbolicLink(
                    Path.Combine(paths.CatalogRoot, "current"), $"versions/{ProductionCatalog.PackageVersion}");
            }
            var uid = NativeLinux.getuid();
            var gid = NativeLinux.getgid();
            var daemon = new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2");
            var catalog = new CatalogInstallationIdentity(
                ProductionCatalog.CatalogId, ProductionCatalog.PackageVersion, "2", "3",
                ProductionCatalog.DatabaseSha256, ProductionCatalog.DatabaseLength, ProductionCatalog.RowCount,
                paths.CatalogRoot, new string('a', 64), "local-offline");
            var localRunner = replayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner;
            var composeTemplateVersion = localRunner
                ? ComposeDeployment.LocalRunnerTemplateVersion
                : ComposeDeployment.TemplateVersion;
            var image = new ImageInstallationIdentity(
                "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('c', 64)}",
                "amd64", null, UpgradeCompatibility: "backward-compatible", SourceRevision: new string('8', 40),
                Component: "CameraAgent", ConfigurationContract: "cameraagent-install-v1",
                CatalogContract: "hyg-v42-production-p3-s2",
                ReplayRunnerContract: localRunner ? "local-replay-runner-v1" : null);
            var installationId = Guid.NewGuid();
            // The bound application identity is deliberately distinct from the instance id so that
            // lifecycle verification cannot pass by comparing the wrong identity.
            var applicationIdentity = Guid.NewGuid();
            const string composeModel = "services: {}\n";
            var manifest = new InstanceManifest(
                1, "HVO.SkyMonitor", "cameraagent-install-v1", DeploymentComponent.CameraAgent,
                instanceId, "Test Camera", applicationIdentity, $"installer-{instanceId:D}", 1,
                new string('d', 64), "owner@example.test", 0, 0, 0, "UTC", installationId, uid, gid,
                paths.ProductRoot, paths.ConfigRoot, paths.StateRoot, composeTemplateVersion,
                new string('e', 64), new string('f', 64), new string('1', 64), "default", "1", "1", "active",
                ComposeDeployment.ComputeSha256("verification-token"), ComposeDeployment.ComputeSha256(composeModel),
                catalog, image, null, daemon, "backward-compatible",
                DateTimeOffset.UtcNow, LifecycleCondition: condition, Port: 5130,
                LifecycleControlTokenSha256: ComposeDeployment.ComputeSha256("lifecycle-token"),
                ReplayProfile: replayProfile);
            var result = new InstallationResult(
                1, InstallationOutcome.Installed, installationId, instanceId, applicationIdentity, "Test Camera",
                new Uri("http://127.0.0.1:5130"), "owner@example.test", "/tmp/password", paths.ProductRoot,
                paths.InstanceRoot, paths.ConfigRoot, paths.StateRoot, uid, gid, composeTemplateVersion,
                0, 0, 0, "UTC", manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256,
                manifest.RigProfileName, manifest.RigProfileVersion, manifest.ScheduleSchemaVersion,
                manifest.ScheduleState, manifest.ComposeModelSha256, catalog, image, daemon, true, true,
                "owner-password-change-required", DateTimeOffset.UtcNow, replayProfile);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ResultPath, result, DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigRoot, "compose", "compose.yml"), composeModel);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigRoot, "compose", "instance.env"), $"CAMERAAGENT_IMAGE={image.ImageId}\n");
            await File.WriteAllTextAsync(paths.ApplicationIdentityPath, "{}\n");
            SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "installation-verification", "token"), "verification-token");
            SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "lifecycle-control", "token"), "lifecycle-token");
            SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "secrets", "LifecycleControl__Token"), "lifecycle-token");
            SafeFileSystem.WriteTextAtomic(
                Path.Combine(paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion"), catalog.PackageVersion);
            foreach (var directory in Directory.EnumerateDirectories(paths.InstanceRoot, "*", SearchOption.AllDirectories).Prepend(paths.InstanceRoot))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new LifecycleFixture(root, instanceId, applicationIdentity, paths, manifest, new FakeRunner(daemon, localRunner), uid, gid, previous);
        }

        public LifecycleRequest Request(LifecycleOperationKind? operation)
            => new(operation, InstanceId, Root, dryRun: false, resume: false, json: false);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", previousAllowTestRoot);
            if (Directory.Exists(Root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(Root);
                Directory.Delete(Root, true);
            }
        }
    }

    private sealed class FakeRunner(DockerDaemonIdentity daemon, bool localRunner = false) : IProcessRunner
    {
        private static readonly string[] ReplayRunnerEntrypoint =
            ["/app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner"];
        private static readonly string[] DroppedCapabilities = ["ALL"];
        private static readonly string[] ReplayRunnerSecurityOptions = ["no-new-privileges:true"];
        private InstallationPaths? paths;
        private string? initialReference;
        private string? initialImageId;
        private string? activeImageId;
        private string? candidateReference;
        private string? candidateImageId;
        private uint uid;
        private uint gid;
        private bool running;

        public string? ActiveImageId => activeImageId;
        public int InvocationCount { get; private set; }
        public int ComposeDownCount { get; private set; }
        public int ComposeUpCount { get; private set; }
        public int ComposeRestartCount { get; private set; }
        public int? DaemonDriftAtInfoCall { get; set; }
        public bool DriftDaemonAfterComposeUp { get; set; }
        public bool RetainOwnedOrphanAfterDown { get; set; }
        public bool OmitOwnershipLabel { get; set; }
        public bool RejectNextStop { get; set; }
        public bool RejectNextBackup { get; set; }
        public Action? OnCandidateInspect { get; set; }
        public List<string> Events { get; } = [];
        public List<string> LoggedContainers { get; } = [];

        public void ConfigureRuntime(
            InstallationPaths installationPaths,
            string immutableInitialReference,
            string initialImageId,
            string immutableCandidateReference,
            string immutableCandidateImageId,
            uint runtimeUid,
            uint runtimeGid,
            bool exists = true)
        {
            paths = installationPaths;
            initialReference = immutableInitialReference;
            this.initialImageId = initialImageId;
            activeImageId = exists ? initialImageId : null;
            candidateReference = immutableCandidateReference;
            candidateImageId = immutableCandidateImageId;
            uid = runtimeUid;
            gid = runtimeGid;
            running = exists;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "The synchronous read is confined to a deterministic in-memory process-runner test double.")]
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (fileName == "tar")
            {
                if (RejectNextBackup && arguments.Contains("--create", StringComparer.Ordinal))
                {
                    RejectNextBackup = false;
                    return Task.FromResult(new ProcessResult(1, string.Empty, "simulated backup interruption"));
                }
                return new ProcessRunner().RunAsync(fileName, arguments, cancellationToken);
            }
            Assert.AreEqual("docker", fileName);
            if (arguments is ["context", "inspect", ..])
                return Task.FromResult(new ProcessResult(0, "unix:///var/run/docker.sock", string.Empty));
            if (arguments.Count >= 2 && arguments[0] == "--host") arguments = arguments.Skip(2).ToArray();
            if (arguments is ["info", ..])
            {
                infoCallCount++;
                var currentDaemon = (DaemonDriftAtInfoCall is { } driftAt && infoCallCount >= driftAt) ||
                                    DriftDaemonAfterComposeUp && ComposeUpCount > 0
                    ? daemon with { Id = "replacement-daemon" }
                    : daemon;
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new
                {
                    OSType = "linux",
                    ID = currentDaemon.Id,
                    Name = currentDaemon.Name,
                    Architecture = currentDaemon.Architecture,
                    ServerVersion = currentDaemon.ServerVersion
                }), string.Empty));
            }
            if (arguments is ["compose", "version", ..]) return Task.FromResult(new ProcessResult(0, "v2", string.Empty));
            if (arguments is ["image", "pull", ..]) return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            if (arguments is ["image", "load", ..] && candidateImageId is not null)
                return Task.FromResult(new ProcessResult(0, $"Loaded image ID: {candidateImageId}\n", string.Empty));
            if (arguments is ["image", "inspect", var inspectedReference] && candidateImageId is not null)
            {
                var isCandidate = inspectedReference == candidateReference;
                if (isCandidate) OnCandidateInspect?.Invoke();
                var inspectedImageId = isCandidate ? candidateImageId : initialImageId;
                var inspectedDigest = isCandidate ? candidateReference : initialReference;
                var labels = new Dictionary<string, string>
                {
                    ["io.hvo.skymonitor.state-compatibility"] = "cameraagent-state-v2",
                    ["io.hvo.skymonitor.minimum-compatible-revision"] = new string('7', 40),
                    ["io.hvo.skymonitor.identity-migration"] = "20260827053715_InitialIdentity",
                    ["io.hvo.skymonitor.raw-ingress-schema"] = "12",
                    ["io.hvo.skymonitor.catalog-manifest-version"] = "2",
                    ["org.opencontainers.image.revision"] = isCandidate ? new string('9', 40) : new string('8', 40),
                    ["io.hvo.skymonitor.component"] = "CameraAgent",
                    ["io.hvo.skymonitor.configuration-contract"] = "cameraagent-install-v1",
                    ["io.hvo.skymonitor.catalog-contract"] = "hyg-v42-production-p3-s2"
                };
                if (localRunner)
                {
                    labels["io.hvo.skymonitor.replay-runner-contract"] = "local-replay-runner-v1";
                }
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = inspectedImageId,
                        Architecture = daemon.Architecture,
                        Os = "linux",
                        RepoDigests = new[] { inspectedDigest },
                        Config = new { Labels = labels }
                    }
                }), string.Empty));
            }
            if (arguments is ["compose", ..])
            {
                if (arguments.Contains("config", StringComparer.Ordinal))
                {
                    var composePath = arguments[arguments.ToList().IndexOf("--file") + 1];
                    return Task.FromResult(new ProcessResult(0, File.ReadAllText(composePath), string.Empty));
                }
                if (arguments.Contains("stop", StringComparer.Ordinal))
                {
                    if (RejectNextStop)
                    {
                        RejectNextStop = false;
                        return Task.FromResult(new ProcessResult(1, string.Empty, "simulated stop interruption"));
                    }
                    running = false;
                }
                if (arguments.Contains("up", StringComparer.Ordinal))
                {
                    ComposeUpCount++;
                    Events.Add("compose-up");
                    var environmentPath = arguments[arguments.ToList().IndexOf("--env-file") + 1];
                    activeImageId = File.ReadLines(environmentPath)
                        .Single(line => line.StartsWith("CAMERAAGENT_IMAGE=", StringComparison.Ordinal))["CAMERAAGENT_IMAGE=".Length..];
                    running = true;
                }
                if (arguments.Contains("restart", StringComparer.Ordinal)) running = true;
                if (arguments.Contains("down", StringComparer.Ordinal))
                {
                    ComposeDownCount++;
                    activeImageId = null;
                    running = false;
                }
                if (arguments.Contains("restart", StringComparer.Ordinal)) ComposeRestartCount++;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }
            if (arguments is ["container", "ls", ..])
            {
                var listed = RetainOwnedOrphanAfterDown && ComposeDownCount > 0 ? "owned-orphan\n" : string.Empty;
                return Task.FromResult(new ProcessResult(0, listed, string.Empty));
            }
            if (arguments is ["container", "inspect", "owned-orphan", ..] && paths is not null)
            {
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Config = new
                        {
                            Labels = new Dictionary<string, string>
                            {
                                ["io.hvo.skymonitor.instance-id"] = Path.GetFileName(paths.InstanceRoot)
                            }
                        },
                        Mounts = Array.Empty<object>()
                    }
                }), string.Empty));
            }
            if (arguments is ["container", "logs", "--tail", "200", var loggedContainer])
            {
                LoggedContainers.Add(loggedContainer);
                Events.Add($"logs:{loggedContainer}");
                return Task.FromResult(new ProcessResult(
                    0,
                    $"{loggedContainer} startup\n{loggedContainer} failure token=runner-secret",
                    string.Empty));
            }
            if (arguments is ["container", "inspect", _, ..] && activeImageId is not null && paths is not null)
            {
                var containerName = arguments[2];
                var labels = new Dictionary<string, string>
                {
                    ["com.docker.compose.project"] = $"hvo-skymonitor-{paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last().Replace("-", string.Empty, StringComparison.Ordinal)}"
                };
                if (!OmitOwnershipLabel)
                    labels["io.hvo.skymonitor.instance-id"] = paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last();
                if (localRunner && containerName.EndsWith("-replay", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                    {
                        new
                        {
                            State = new { Running = running, Health = new { Status = "healthy" } },
                            Image = activeImageId,
                            Config = new
                            {
                                User = $"{uid}:{gid}",
                                Labels = labels,
                                Entrypoint = ReplayRunnerEntrypoint
                            },
                            HostConfig = new
                            {
                                ReadonlyRootfs = true,
                                Privileged = false,
                                CapDrop = DroppedCapabilities,
                                SecurityOpt = ReplayRunnerSecurityOptions,
                                NetworkMode = "none",
                                Tmpfs = new Dictionary<string, string>
                                {
                                    ["/tmp"] = "rw,nosuid,nodev,noexec,mode=1777,size=67108864"
                                }
                            },
                            Mounts = new[]
                            {
                                new
                                {
                                    Type = "bind",
                                    Source = Path.Combine(paths.ConfigRoot, "secrets", "replay-runner-auth-key"),
                                    Destination = "/run/hvo-secrets/replay-runner-auth-key",
                                    RW = false
                                },
                                new
                                {
                                    Type = "bind",
                                    Source = Path.Combine(paths.StateRoot, "replay-runner"),
                                    Destination = "/run/hvo-replay",
                                    RW = true
                                },
                            }
                        }
                    }), string.Empty));
                }
                var mounts = new List<object>
                {
                    new { Source = Path.Combine(paths.ConfigRoot, "camera-module.json"), Destination = "/app/cameraagent.deploy.json", RW = false },
                    new { Source = paths.CatalogRoot, Destination = "/app/catalog", RW = false },
                    new { Source = Path.Combine(paths.StateRoot, "identity"), Destination = "/app/App_Data", RW = true }
                };
                if (localRunner)
                {
                    mounts.Add(new
                    {
                        Source = Path.Combine(paths.StateRoot, "replay-runner"),
                        Destination = "/run/hvo-replay",
                        RW = true
                    });
                }
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        State = new { Running = running, Health = new { Status = "healthy" } },
                        Image = activeImageId,
                        Config = new { User = $"{uid}:{gid}", Labels = labels },
                        HostConfig = new { ReadonlyRootfs = true, Privileged = false },
                        Mounts = mounts
                    }
                }), string.Empty));
            }
            if (arguments is ["container", "inspect", _, ..])
                return Task.FromResult(new ProcessResult(1, string.Empty, $"Error response from daemon: No such container: {arguments[2]}"));
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }

        private int infoCallCount;
    }

    private sealed class FakeLifecycleClient : ICameraAgentLifecycleClient
    {
        public bool RejectNextResume { get; set; }
        public int PauseCount { get; private set; }
        public int ConfirmCount { get; private set; }

        public Task<LifecycleContinuity> PauseAndDrainAsync(
            Guid operationId,
            string verificationToken,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            PauseCount++;
            return Task.FromResult(new LifecycleContinuity("Paused", 42, 100, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        public Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            ConfirmCount++;
            return Task.FromResult(new LifecycleContinuity("Paused", 42, 100, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        public Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            if (RejectNextResume)
            {
                RejectNextResume = false;
                throw new HttpRequestException("simulated lost resume acknowledgement");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOwnerClient(Guid expectedAgentId) : IOwnerBootstrapClient
    {
        public bool RejectNextVerification { get; set; }
        public string CurrentOwnerBootstrapState { get; set; } = "owner-password-change-required";
        public Queue<string> VerificationStates { get; } = new();

        public Task WaitForHealthAsync(CancellationToken cancellationToken, TimeSpan? timeout = null) => Task.CompletedTask;
        public Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
            => Task.FromResult("owner-password-change-required");
        public Task<string> ReadInstallationStateAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(CurrentOwnerBootstrapState);
        public Task<string> VerifyInstallationAsync(
            string verificationToken,
            InstallationVerificationExpectation expectation,
            CancellationToken cancellationToken)
        {
            if (RejectNextVerification)
            {
                RejectNextVerification = false;
                throw new InstallerException("simulated candidate verification failure");
            }
            if (expectation.AgentId != expectedAgentId.ToString("D"))
            {
                throw new InstallerException("simulated application identity mismatch");
            }
            var currentState = VerificationStates.TryDequeue(out var state)
                ? state
                : CurrentOwnerBootstrapState;
            if (!OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
                    expectation.OwnerBootstrapState,
                    currentState,
                    expectation.AllowCompletedPasswordReplacement))
            {
                throw new InstallerException("simulated owner bootstrap state mismatch");
            }
            return Task.FromResult(currentState);
        }
    }
}
