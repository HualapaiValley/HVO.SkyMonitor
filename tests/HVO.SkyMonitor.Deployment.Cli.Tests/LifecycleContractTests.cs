using System.Text.Json;
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
        StringAssert.Contains(result.ResumeCommand, "--owner-password-file", StringComparison.Ordinal);
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
    public async Task LifecycleControlToken_MissingMirrorIsReconciledAndMismatchIsRejected()
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
            var token = await CameraAgentLifecycleManager.GetOrCreateLifecycleControlTokenAsync(paths, CancellationToken.None);
            Assert.AreEqual("authority-token", token);
            Assert.AreEqual(token, await File.ReadAllTextAsync(mirrorPath));

            SafeFileSystem.WriteTextAtomic(mirrorPath, "different-token");
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                CameraAgentLifecycleManager.GetOrCreateLifecycleControlTokenAsync(paths, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
    public async Task UpgradeAsync_ResumeAfterCommitCompletesWithoutRollingBackCandidate()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('7', 64)}";
        var candidateImageId = $"sha256:{new string('8', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var ownerPassword = Path.Combine(fixture.Root, "owner-password");
        SafeFileSystem.WriteTextAtomic(ownerPassword, "owner-password");
        var lifecycle = new FakeLifecycleClient { RejectNextResume = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true,
            OwnerPasswordFile = ownerPassword
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(), fixture.Uid, fixture.Gid, CancellationToken.None));

        var committedManifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(candidateImageId, committedManifest.Image.ImageId);
        Assert.AreEqual(LifecycleOperationPhase.Committed, retained!.Phase);
        Assert.IsTrue(retained.MutationStarted);
        Assert.IsNotNull(retained.PreMutationContinuity);
        Assert.IsNotNull(retained.PostMutationContinuity);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Completed, retained!.Phase);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    public async Task ReinstallAsync_PreservedInstanceVerifiesOwnerBeforeCommit()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid,
            exists: false);
        var ownerPassword = Path.Combine(fixture.Root, "owner-password");
        SafeFileSystem.WriteTextAtomic(ownerPassword, "owner-password");
        var lifecycle = new FakeLifecycleClient();

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Reinstall) with { OwnerPasswordFile = ownerPassword },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Installed, result.LifecycleCondition);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsNotNull(operation!.PostMutationContinuity);
    }

    [TestMethod]
    public async Task UpgradeAsync_FailedCandidateRestoresExactOriginalRuntime()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var ownerPassword = Path.Combine(fixture.Root, "owner-password");
        SafeFileSystem.WriteTextAtomic(ownerPassword, "owner-password");
        var owner = new FakeOwnerClient { RejectNextVerification = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true,
            OwnerPasswordFile = ownerPassword
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
        var ownerPassword = Path.Combine(fixture.Root, "owner-password");
        SafeFileSystem.WriteTextAtomic(ownerPassword, "owner-password");
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true,
            OwnerPasswordFile = ownerPassword
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, manifest.Image.ImageId);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    public async Task RollbackAsync_FirstRollbackAfterLegacyUpgradeRestoresV1ComposeAndImage()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var legacy = fixture.Manifest with
        {
            ComposeTemplateVersion = "cameraagent-compose-v1",
            LifecycleControlTokenSha256 = null
        };
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath, legacy, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ResultPath, result with { ComposeTemplateVersion = "cameraagent-compose-v1" },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml"),
            "services:\n  cameraagent:\n    image: ${CAMERAAGENT_IMAGE}\n    pull_policy: never\n");
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";
        var candidateImageId = $"sha256:{new string('b', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, legacy.Image.ImmutableReference, legacy.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var ownerPassword = Path.Combine(fixture.Root, "owner-password");
        SafeFileSystem.WriteTextAtomic(ownerPassword, "owner-password");
        var lifecycle = new FakeLifecycleClient();

        await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true,
                OwnerPasswordFile = ownerPassword
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(), fixture.Uid, fixture.Gid, CancellationToken.None);

        var upgraded = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(ComposeDeployment.TemplateVersion, upgraded.ComposeTemplateVersion);
        Assert.AreEqual("cameraagent-compose-v1", upgraded.PreviousComposeTemplateVersion);

        var rollback = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback) with { OwnerPasswordFile = ownerPassword },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual(legacy.Image.ImageId, rollback.Image!.ImageId);
        var rolledBack = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual("cameraagent-compose-v1", rolledBack.ComposeTemplateVersion);
        Assert.AreEqual(legacy.Image.ImageId, fixture.Runner.ActiveImageId);
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private readonly string? previousAllowTestRoot;

        private LifecycleFixture(
            string root,
            Guid instanceId,
            InstallationPaths paths,
            InstanceManifest manifest,
            FakeRunner runner,
            uint uid,
            uint gid,
            string? previousAllowTestRoot)
        {
            Root = root;
            InstanceId = instanceId;
            Paths = paths;
            Manifest = manifest;
            Runner = runner;
            Uid = uid;
            Gid = gid;
            this.previousAllowTestRoot = previousAllowTestRoot;
        }

        public string Root { get; }
        public Guid InstanceId { get; }
        public InstallationPaths Paths { get; }
        public InstanceManifest Manifest { get; }
        public FakeRunner Runner { get; }
        public uint Uid { get; }
        public uint Gid { get; }

        public static async Task<LifecycleFixture> CreateAsync(InstanceLifecycleCondition condition)
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
            var uid = NativeLinux.getuid();
            var gid = NativeLinux.getgid();
            var daemon = new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2");
            var catalog = new CatalogInstallationIdentity(
                ProductionCatalog.CatalogId, ProductionCatalog.PackageVersion, "2", "3",
                ProductionCatalog.DatabaseSha256, ProductionCatalog.DatabaseLength, ProductionCatalog.RowCount,
                paths.CatalogRoot, new string('a', 64), "local-offline");
            var image = new ImageInstallationIdentity(
                "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('c', 64)}",
                "amd64", null, UpgradeCompatibility: "backward-compatible");
            var installationId = Guid.NewGuid();
            var applicationIdentity = instanceId;
            var manifest = new InstanceManifest(
                1, "HVO.SkyMonitor", "cameraagent-install-v1", DeploymentComponent.CameraAgent,
                instanceId, "Test Camera", applicationIdentity, $"installer-{instanceId:D}", 1,
                new string('d', 64), "owner@example.test", 0, 0, 0, "UTC", installationId, uid, gid,
                paths.ProductRoot, paths.ConfigRoot, paths.StateRoot, ComposeDeployment.TemplateVersion,
                new string('e', 64), new string('f', 64), new string('1', 64), "default", "1", "1", "active",
                new string('2', 64), ComposeDeployment.ComputeSha256(string.Empty), catalog, image, null, daemon, "backward-compatible",
                DateTimeOffset.UtcNow, LifecycleCondition: condition, Port: 5130,
                LifecycleControlTokenSha256: ComposeDeployment.ComputeSha256("lifecycle-token"));
            var result = new InstallationResult(
                1, InstallationOutcome.Installed, installationId, instanceId, applicationIdentity, "Test Camera",
                new Uri("http://127.0.0.1:5130"), "owner@example.test", "/tmp/password", paths.ProductRoot,
                paths.InstanceRoot, paths.ConfigRoot, paths.StateRoot, uid, gid, ComposeDeployment.TemplateVersion,
                0, 0, 0, "UTC", manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256,
                manifest.RigProfileName, manifest.RigProfileVersion, manifest.ScheduleSchemaVersion,
                manifest.ScheduleState, manifest.ComposeModelSha256, catalog, image, daemon, true, true,
                "owner-password-change-required", DateTimeOffset.UtcNow);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ResultPath, result, DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigRoot, "compose", "compose.yml"), "services: {}\n");
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigRoot, "compose", "instance.env"), $"CAMERAAGENT_IMAGE={image.ImageId}\n");
            await File.WriteAllTextAsync(paths.ApplicationIdentityPath, "{}\n");
            SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "installation-verification", "token"), "verification-token");
            SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "lifecycle-control", "token"), "lifecycle-token");
            SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "secrets", "LifecycleControl__Token"), "lifecycle-token");
            foreach (var directory in Directory.EnumerateDirectories(paths.InstanceRoot, "*", SearchOption.AllDirectories).Prepend(paths.InstanceRoot))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new LifecycleFixture(root, instanceId, paths, manifest, new FakeRunner(daemon), uid, gid, previous);
        }

        public LifecycleRequest Request(LifecycleOperationKind? operation)
            => new(operation, InstanceId, Root, dryRun: false, resume: false, json: false);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", previousAllowTestRoot);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class FakeRunner(DockerDaemonIdentity daemon) : IProcessRunner
    {
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
        public bool RejectNextStop { get; set; }
        public bool RejectNextBackup { get; set; }

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
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new
                {
                    OSType = "linux",
                    ID = daemon.Id,
                    Name = daemon.Name,
                    Architecture = daemon.Architecture,
                    ServerVersion = daemon.ServerVersion
                }), string.Empty));
            }
            if (arguments is ["compose", "version", ..]) return Task.FromResult(new ProcessResult(0, "v2", string.Empty));
            if (arguments is ["image", "pull", ..]) return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            if (arguments is ["image", "inspect", var inspectedReference] && candidateImageId is not null)
            {
                var isCandidate = inspectedReference == candidateReference;
                var inspectedImageId = isCandidate ? candidateImageId : initialImageId;
                var inspectedDigest = isCandidate ? candidateReference : initialReference;
                var labels = isCandidate
                    ? new Dictionary<string, string>
                    {
                        ["io.hvo.skymonitor.state-compatibility"] = "backward-compatible",
                        ["org.opencontainers.image.revision"] = new string('9', 40),
                        ["io.hvo.skymonitor.component"] = "CameraAgent",
                        ["io.hvo.skymonitor.configuration-contract"] = "cameraagent-install-v1",
                        ["io.hvo.skymonitor.catalog-contract"] = "hyg-v42-production-p3-s2"
                    }
                    : new Dictionary<string, string>();
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
                    return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
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
                    var environmentPath = arguments[arguments.ToList().IndexOf("--env-file") + 1];
                    activeImageId = File.ReadLines(environmentPath)
                        .Single(line => line.StartsWith("CAMERAAGENT_IMAGE=", StringComparison.Ordinal))["CAMERAAGENT_IMAGE=".Length..];
                    running = true;
                }
                if (arguments.Contains("restart", StringComparer.Ordinal)) running = true;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }
            if (arguments is ["container", "ls", ..]) return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            if (arguments is ["container", "logs", ..]) return Task.FromResult(new ProcessResult(0, "candidate logs", string.Empty));
            if (arguments is ["container", "inspect", _, ..] && activeImageId is not null && paths is not null)
            {
                var labels = new Dictionary<string, string>
                {
                    ["com.docker.compose.project"] = $"hvo-skymonitor-{paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last().Replace("-", string.Empty, StringComparison.Ordinal)}",
                    ["io.hvo.skymonitor.instance-id"] = paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last()
                };
                var mounts = new[]
                {
                    new { Source = Path.Combine(paths.ConfigRoot, "camera-module.json"), Destination = "/app/cameraagent.deploy.json", RW = false },
                    new { Source = paths.CatalogRoot, Destination = "/app/catalog", RW = false },
                    new { Source = Path.Combine(paths.StateRoot, "identity"), Destination = "/app/App_Data", RW = true }
                };
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
    }

    private sealed class FakeLifecycleClient : ICameraAgentLifecycleClient
    {
        public bool RejectNextResume { get; set; }

        public Task<LifecycleContinuity> PauseAndDrainAsync(
            Guid operationId,
            string verificationToken,
            CancellationToken cancellationToken)
            => Task.FromResult(new LifecycleContinuity("Paused", 42, 100, 0, 0, 0, 0, 0, 0, 0, 0));

        public Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
        {
            if (RejectNextResume)
            {
                RejectNextResume = false;
                throw new HttpRequestException("simulated lost resume acknowledgement");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOwnerClient : IOwnerBootstrapClient
    {
        public bool RejectNextVerification { get; set; }

        public Task WaitForHealthAsync(CancellationToken cancellationToken, TimeSpan? timeout = null) => Task.CompletedTask;
        public Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
            => Task.FromResult("owner-password-change-required");
        public Task VerifyInstallationAsync(
            string verificationToken,
            InstallationVerificationExpectation expectation,
            CancellationToken cancellationToken)
        {
            if (RejectNextVerification)
            {
                RejectNextVerification = false;
                throw new InstallerException("simulated candidate verification failure");
            }
            return Task.CompletedTask;
        }
    }
}
