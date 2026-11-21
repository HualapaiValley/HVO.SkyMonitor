using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LeavePausedReview_ProgramLocatorReplacementCannotChangeAdmittedPrograms(bool partialPublish)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var targets = await SeedPathProgramsAsync(fixture);
        await PathPrograms.SettleAsync(fixture.Paths, InstallRequest.DefaultProductRoot, null,
            fixture.Manifest.Image.ImageId, CancellationToken.None);
        var priorBytes = await File.ReadAllTextAsync(targets[0]);
        using var original = ReleaseFixture.Create(fixture.Root, version: "1.3.0");
        using var substitute = ReleaseFixture.Create(fixture.Root, version: "1.4.0");
        var admitted = await ReleaseAcquirer.ReadAsync(original.Folder, CancellationToken.None);
        var image = original.ConfigDigest($"linux/{ReleaseAcquirer.HostImageArchitecture()}");

        Task SettleAsync() => PathPrograms.SettleAsync(fixture.Paths, InstallRequest.DefaultProductRoot,
            original.Folder, image, CancellationToken.None, admitted);

        if (partialPublish)
        {
            var flush = NativeLinux.DirectoryFlushed;
            try
            {
                NativeLinux.DirectoryFlushed = folder =>
                {
                    var registry = Path.Combine(fixture.Paths.OperationsRoot, PathPrograms.RecordName);
                    if (folder == fixture.Paths.OperationsRoot && File.Exists(registry) &&
                        File.ReadAllText(registry).Contains("\"pendingSha256\": \"", StringComparison.Ordinal))
                        throw new InstallerException("Interrupted after publication intent.");
                };
                var interrupted = await Assert.ThrowsExactlyAsync<InstallerException>(SettleAsync);
                Assert.AreEqual("Interrupted after publication intent.", interrupted.Message);
            }
            finally
            {
                NativeLinux.DirectoryFlushed = flush;
            }
        }
        foreach (var file in Directory.EnumerateFiles(original.Folder))
            File.Delete(file);
        foreach (var file in Directory.EnumerateFiles(substitute.Folder))
            File.Copy(file, Path.Combine(original.Folder, Path.GetFileName(file)));
        var substituted = await ReleaseAcquirer.ReadAsync(original.Folder, CancellationToken.None);
        Assert.AreNotEqual(admitted.ChecksumsSha256, substituted.ChecksumsSha256);

        if (!partialPublish)
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(SettleAsync);
            Assert.AreEqual(priorBytes, await File.ReadAllTextAsync(targets[0]));
            return;
        }

        await SettleAsync();
        foreach (var target in targets)
            StringAssert.Contains(await File.ReadAllTextAsync(target), "1.3.0", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("Healthy", "Fleet heartbeat is disabled/not configured.", true)]
    [DataRow("Healthy", "Fleet heartbeat delivery is current.", false)]
    [DataRow("Degraded", "Fleet heartbeat is disabled/not configured.", false)]
    public void LeavePausedReview_FleetHeartbeatRequiresExactDisabledEvidence(string status, string description, bool expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            checks = new[] { new { name = "fleet-heartbeat", status, description } }
        }));
        Assert.AreEqual(expected, OwnerBootstrapClient.ReportsFleetDisabled(document.RootElement));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("", false)]
    [DataRow("-wal", false)]
    [DataRow("-shm", false)]
    [DataRow("-journal", false)]
    [DataRow("-wal", true)]
    public async Task LeavePausedReview_FleetStoreRequiresProvenNoFollowAbsence(string suffix, bool symlink)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        CameraAgentLifecycleManager.EnsureFleetStoresAbsent(fixture.Paths);
        var parent = Path.Combine(fixture.Paths.StateRoot, "raw", ".fleet");
        SafeFileSystem.CreateOwnerDirectory(parent);
        var node = Path.Combine(parent, "fleet-status.db" + suffix);
        if (symlink)
            File.CreateSymbolicLink(node, Path.Combine(parent, "nonexistent"));
        else
            Directory.CreateDirectory(node);
        try
        {
            Assert.ThrowsExactly<InstallerException>(() => CameraAgentLifecycleManager.EnsureFleetStoresAbsent(fixture.Paths));
        }
        finally
        {
            if (symlink) File.Delete(node);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LeavePausedReview_DurableCredentialReplacementChangesIdentityWitness()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var path = Path.Combine(fixture.Root, "identity-witness.db");
        var options = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        await using (var connection = new SqliteConnection(options.ToString()))
        {
            await connection.OpenAsync();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE AspNetUsers(Id TEXT PRIMARY KEY, PasswordHash TEXT, SecurityStamp TEXT); INSERT INTO AspNetUsers VALUES ('owner', 'opaque-original', 'original-stamp');";
            await create.ExecuteNonQueryAsync();
        }
        var before = await CameraAgentLifecycleManager.ReadIdentityFingerprintAsync(path, CancellationToken.None);
        await using (var connection = new SqliteConnection(options.ToString()))
        {
            await connection.OpenAsync();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE AspNetUsers SET PasswordHash='opaque-replacement', SecurityStamp='replaced-stamp';";
            await update.ExecuteNonQueryAsync();
        }
        var after = await CameraAgentLifecycleManager.ReadIdentityFingerprintAsync(path, CancellationToken.None);
        Assert.AreNotEqual(before, after);
        Assert.HasCount(64, after);
        File.Delete(path);
        await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.ReadIdentityFingerprintAsync(path, CancellationToken.None));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LeavePausedReview_MismatchedRestorePlanAndSameRetryReferenceAreRefused()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        using var release = ReleaseFixture.Create();
        _ = await PrepareSignedRecoveryAsync(fixture, release, "index");
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var snapshot = operation.StateSnapshot!;
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);

        var stale = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.VerifyPausedCheckpointAsync(fixture.Paths, fixture.InstanceId, snapshot, fixture.Manifest.Image,
                operation.StateRestorePlan! with { SnapshotId = Guid.NewGuid() }, CancellationToken.None));
        StringAssert.Contains(stale.Message, "restore plan does not match", StringComparison.Ordinal);
        var same = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.VerifyPausedCommitEvidenceAsync(fixture.Paths, fixture.Manifest,
                operation with
                {
                    LeavePausedOriginalSnapshot = snapshot,
                    LeavePausedOriginalRestorePlanId = snapshot.SnapshotId,
                    StateRestorePlan = null,
                    StateSnapshotRestoredUtc = null
                }, CancellationToken.None));
        StringAssert.Contains(same.Message, "separate proven", StringComparison.Ordinal);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LeavePausedReview_HeadroomGateRefusesBeforeWritingOrCallingLifecycle()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var lifecycle = new FakeLifecycleClient();
        var request = UpgradeRequest(fixture, assertMigration: false);
        CameraAgentLifecycleManager.UpgradeStorageHostOverride.Value =
            new FakeHostEnvironment { Fallback = new(fixture.Paths.ProductRoot, "test", 0) };
        try
        {
            var error = Assert.ThrowsExactly<InstallerException>(() =>
                CameraAgentLifecycleManager.EnsurePausedUpgradeStorage(fixture.Paths, request, fixture.Manifest));
            StringAssert.Contains(error.Message, "Insufficient free space", StringComparison.Ordinal);
        }
        finally
        {
            CameraAgentLifecycleManager.UpgradeStorageHostOverride.Value = null;
        }
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
        Assert.AreEqual(0, lifecycle.ResumeCount);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LeavePausedReview_ReceiptFlushCutPreservesHistoricalBytesAndOmitsOwnerCanaries()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        using var release = ReleaseFixture.Create();
        var verified = (await ReleaseAcquirer.ReadAsync(release.Folder, CancellationToken.None)) with
        {
            ChecksumsSha256 = "d4826ad6f732ea9fe1bdc3fb0edc76f348b6ddceb2a825ee5f75e71373a374ec"
        };
        var request = UpgradeRequest(fixture, assertMigration: false) with { Release = release.Folder };
        var id = Guid.NewGuid();
        var previous = new LifecycleStateSnapshot(Guid.NewGuid(), id, new string('a', 64), DateTimeOffset.UtcNow);
        var retry = new LifecycleStateSnapshot(Guid.NewGuid(), id, new string('b', 64), DateTimeOffset.UtcNow);
        var manifest = fixture.Manifest with { LastLifecycleOperationId = id, PreviousStateSnapshot = retry };
        var lifecycle = new FakeLifecycleClient
        {
            RecoveryContinuity = new LifecycleContinuity("Paused", 2, 0, 0, 0, 0, 0, 0, 0, 0, 0)
        };
        var operation = new LifecycleOperationState(2, id, LifecycleOperationKind.Upgrade, fixture.InstanceId,
            new string('c', 64), LifecycleOperationPhase.Committed, InstallationStatus.Running, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, fixture.Manifest.Image, manifest.Image, fixture.Manifest.Catalog,
            MutationStarted: true, PostMutationContinuity: CameraAgentLifecycleManager.ToBoundary(lifecycle.RecoveryContinuity),
            StateSnapshot: retry, SignedReleaseChecksumsSha256: verified.ChecksumsSha256,
            LeavePausedOriginalSnapshot: previous, LeavePausedOriginalRestorePlanId: previous.SnapshotId);
        operation = await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation, CancellationToken.None);
        var raw = CameraAgentStateLayout.RawIngressDatabasePath(fixture.Paths.StateRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        await using var database = new SqliteConnection($"Data Source={raw};Pooling=False");
        await database.OpenAsync();
        using var command = database.CreateCommand();
        command.CommandText = """
            CREATE TABLE capture_control_state(state_key INTEGER, state TEXT, version INTEGER);
            INSERT INTO capture_control_state VALUES (1, 'paused', 2);
            CREATE TABLE capture_control_commands(idempotency_key TEXT, target_state TEXT, actor TEXT, reason TEXT,
                payload_sha256 TEXT, result_version INTEGER, completed_unix_ms INTEGER, changed INTEGER, status TEXT, result_state TEXT);
            INSERT INTO capture_control_commands VALUES
                ('first-opaque-key', 'paused', 'opaque-subject', 'first-private-reason', 'first-payload', 2, 1, 1, 'completed', 'paused');
            CREATE TABLE raw_capture_sequences(agent_id TEXT, last_sequence INTEGER);
            CREATE TABLE raw_capture_assignments(capture_sequence INTEGER);
            CREATE TABLE raw_captures(capture_sequence INTEGER);
            """;
        await command.ExecuteNonQueryAsync();
        var folder = Path.Combine(fixture.Paths.OperationsRoot, "lifecycle", id.ToString("D"));
        var receipt = Path.Combine(folder, "leave-paused-receipt.json");
        var flush = NativeLinux.DirectoryFlushed;
        Task<LifecycleResult> SettleAsync() => CameraAgentLifecycleManager.SettlePausedUpgradeAsync(
            request, fixture.Paths, manifest, operation, verified, lifecycle, "lifecycle-token", CancellationToken.None);
        try
        {
            NativeLinux.DirectoryFlushed = path =>
            {
                if (path == folder && File.Exists(receipt))
                    throw new InstallerException("Interrupted after the receipt became durable.");
            };
            var interrupted = await Assert.ThrowsExactlyAsync<InstallerException>(() => SettleAsync());
            Assert.AreEqual("Interrupted after the receipt became durable.", interrupted.Message);
        }
        finally
        {
            NativeLinux.DirectoryFlushed = flush;
        }
        var originalReceipt = await File.ReadAllTextAsync(receipt);
        Assert.AreEqual(LifecycleOperationPhase.Committed, (await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None))!.Phase);
        command.CommandText = """
            INSERT INTO capture_control_commands VALUES
                ('second-opaque-key', 'paused', 'opaque-subject', 'second-private-reason', 'second-payload', 2, 2, 0, 'completed', 'paused');
            """;
        await command.ExecuteNonQueryAsync();
        var result = await SettleAsync();

        Assert.AreEqual(originalReceipt, await File.ReadAllTextAsync(receipt));
        var observation = result.PreservedAdmission!;
        Assert.AreEqual(2L, observation.LatestCommandOrder);
        Assert.AreEqual(ComposeDeployment.ComputeSha256("second-opaque-key"), observation.LatestCommandKeySha256);
        Assert.IsFalse(observation.LatestChanged);
        var output = originalReceipt + JsonSerializer.Serialize(observation);
        foreach (var canary in new[] { "first-opaque-key", "second-opaque-key", "first-private-reason", "second-private-reason", "opaque-subject" })
            Assert.IsFalse(output.Contains(canary, StringComparison.Ordinal));
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(0, lifecycle.ResumeCount);
        Assert.AreEqual(LifecycleOperationPhase.Completed, (await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None))!.Phase);
    }
}
