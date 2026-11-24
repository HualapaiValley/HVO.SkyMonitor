using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("config")]
    [DataRow("manifest")]
    [DataRow("index")]
    public async Task SignedRestoreOnly_UsesTheOriginalArchiveAndSettlesWithoutRestart(string digestKind)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        using var release = ReleaseFixture.Create();
        var request = await PrepareSignedRecoveryAsync(fixture, release, digestKind);
        var lifecycle = new FakeLifecycleClient();
        var snapshot = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        await ModifyLiveSchemaAsync(fixture.Paths, "CREATE TABLE predecessor_lazy_component(value);");

        var result = await ExecuteRestoreAsync(fixture, request, lifecycle);
        var journalBytes = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var repeated = await ExecuteRestoreAsync(fixture, request with { ImageSource = ReleaseImageSource.Archive }, lifecycle);
        var recovered = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);

        Assert.AreEqual("restored-previous-healthy-admission-resumed", result.Outcome);
        Assert.AreEqual(result.Outcome, repeated.Outcome);
        Assert.AreEqual(fixture.Manifest.Image, result.Image);
        Assert.AreEqual(LifecycleOperationPhase.Restored, recovered!.Phase);
        Assert.AreEqual(InstallationStatus.Failed, recovered.Status);
        Assert.IsFalse(recovered.MutationStarted);
        Assert.AreEqual(snapshot!.StateSnapshot, recovered.StateSnapshot);
        Assert.AreEqual(snapshot.StateSnapshotRestoredUtc, recovered.StateSnapshotRestoredUtc);
        Assert.AreEqual(snapshot.StateSnapshot!.SnapshotId, recovered.StateRestorePlan!.SnapshotId);
        CollectionAssert.AreEqual(CameraAgentStateLayout.SqliteStores.Order(StringComparer.Ordinal).ToArray(),
            recovered.StateRestorePlan.CurrentLocations.ToArray());
        Assert.AreEqual(1, lifecycle.RecoveryResumeCalls);
        Assert.AreEqual(1, lifecycle.OwnedRecoveryResumeCalls);
        CollectionAssert.AreEqual(journalBytes, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SignedRestoreOnly_DistinguishesFleetProgressFromRawCaptureProgress(bool laterCapture)
    {
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out var candidate);
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        var fleet = "raw/.fleet/fleet-status.db";
        var sql = "INSERT INTO fleet_status_records VALUES(1, 6); UPDATE fleet_status_metadata SET next_sequence = 7;";
        var fleetFile = Path.Combine(fixture.Paths.StateRoot, fleet);
        if (laterCapture) ExecuteSql(fleetFile, sql);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        Task<LifecycleResult> RunAsync(LifecycleRequest command) => CameraAgentLifecycleManager.ExecuteAsync(
            command, fixture.Runner, _ => lifecycle, _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(request));
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(LifecycleOperationPhase.RecoveryAdmissionPreserved, operation.Phase);
        Assert.AreEqual(candidate, operation.CandidateImage!.ImageId);
        // Model an interrupted ordinary recovery whose checkpoint is restored and predecessor is ready.
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            SchemaVersion = 1,
            Phase = LifecycleOperationPhase.Restoring,
            Status = InstallationStatus.Running,
            MutationStarted = true
        }, CancellationToken.None);
        lifecycle.SupportsOwnedResume = true;
        if (laterCapture)
            ExecuteSql(RawIngressPath(fixture.Paths), "INSERT INTO raw_capture_sequences VALUES('fixture-owner-camera', 1);");
        else
            ExecuteSql(fleetFile, sql);
        Assert.AreEqual(6, lifecycle.ReadBoundary().CaptureSequence);
        var restore = request with { Resume = true, RestoreOnly = true, RecoveryOperationId = operation.OperationId };
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var starts = fixture.Runner.ComposeUpCount;

        if (laterCapture)
        {
            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(restore));
            StringAssert.Contains(failure.Message, "raw capture progress", StringComparison.Ordinal);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
            Assert.IsEmpty(lifecycle.OwnedRequests);
            Assert.AreEqual("Paused", lifecycle.ReadBoundary().CaptureState);
        }
        else
        {
            Assert.AreEqual("restored-previous-healthy-admission-resumed", (await RunAsync(restore)).Outcome);
            Assert.AreEqual("Running", lifecycle.ReadBoundary().CaptureState);
            Assert.AreEqual(1, ReadCounter(fixture.Paths, fleet, "SELECT COUNT(*) FROM fleet_status_records WHERE sequence = 6;"));
            Assert.HasCount(1, lifecycle.OwnedRequests);
        }
        Assert.AreEqual(starts, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(1, lifecycle.PauseRequests);
        Assert.AreEqual(0, lifecycle.GenericResumeCalls);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("coherent-release-edit", "original release manifest and checksums")]
    [DataRow("missing-historical-anchor", "original release manifest and checksums")]
    [DataRow("moved-release", "original request")]
    [DataRow("candidate-labels", "candidate identity")]
    [DataRow("candidate-compose", "staged Compose")]
    [DataRow("unrestored", "already-restored checkpoint")]
    [DataRow("damaged-snapshot", "snapshot manifest differs")]
    [DataRow("missing-restore-plan", "completed restore plan")]
    [DataRow("wrong-restore-plan-id", "completed restore plan")]
    [DataRow("empty-current-stores", "required store inventory")]
    [DataRow("missing-fixed-store", "required store inventory")]
    [DataRow("foreign-snapshot-pause", "no longer owned")]
    [DataRow("newer-operator-command", "no longer owned")]
    [DataRow("legacy-predecessor", "lacks atomic signed restore-only admission")]
    [DataRow("different-live-schema", "live schema differs from the checkpoint")]
    [DataRow("different-processing-marker", "live schema differs from the checkpoint")]
    [DataRow("new-acquisition", "operation boundary")]
    [DataRow("different-fleet-marker", "live schema differs from the checkpoint")]
    [DataRow("candidate-runtime", "original image running and healthy")]
    [DataRow("missing-lock", "original deployment locks")]
    public async Task SignedRestoreOnly_RefusesBeforeRecoveryMutation(string fault, string message)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        using var release = ReleaseFixture.Create();
        var request = await PrepareSignedRecoveryAsync(fixture, release, "index", foreignPause: fault == "foreign-snapshot-pause",
            withFleet: fault == "different-fleet-marker");
        var lifecycle = new FakeLifecycleClient();
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var snapshot = operation.StateSnapshot!;
        var operationRoot = Path.Combine(fixture.Paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
        switch (fault)
        {
            case "coherent-release-edit":
                release.RewriteText(text => text + "\n");
                break;
            case "missing-historical-anchor":
                await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with { SignedReleaseChecksumsSha256 = null }, CancellationToken.None);
                break;
            case "moved-release":
                var moved = Path.Combine(fixture.Root, "moved-release");
                Directory.CreateDirectory(moved);
                foreach (var file in Directory.EnumerateFiles(release.Folder))
                    File.Copy(file, Path.Combine(moved, Path.GetFileName(file)));
                request = request with { Release = moved };
                break;
            case "candidate-labels":
                await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
                {
                    CandidateImage = operation.CandidateImage! with { SourceRevision = new string('a', 40) }
                }, CancellationToken.None);
                break;
            case "candidate-compose":
                SafeFileSystem.WriteTextAtomic(Path.Combine(operationRoot, "candidate-compose.yml"), "services: changed");
                break;
            case "unrestored":
                await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with { StateSnapshotRestoredUtc = null }, CancellationToken.None);
                break;
            case "damaged-snapshot":
                var snapshotRoot = CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, operation.OperationId, snapshot.SnapshotId);
                SafeFileSystem.WriteTextAtomic(Path.Combine(snapshotRoot, "manifest.json"), "{}");
                break;
            case "missing-restore-plan":
            case "wrong-restore-plan-id":
            case "empty-current-stores":
            case "missing-fixed-store":
                var plan = operation.StateRestorePlan!;
                await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
                {
                    StateRestorePlan = fault switch
                    {
                        "missing-restore-plan" => null,
                        "wrong-restore-plan-id" => plan with { SnapshotId = Guid.NewGuid() },
                        "empty-current-stores" => plan with { CurrentLocations = [] },
                        _ => plan with
                        {
                            CurrentLocations = plan.CurrentLocations.Where(
                                static store => store != CameraAgentStateLayout.SqliteStores[0]).ToArray()
                        }
                    }
                }, CancellationToken.None);
                break;
            case "newer-operator-command":
                await WritePauseReceiptAsync(fixture.Paths, operation, foreign: true, insertOnly: true);
                break;
            case "legacy-predecessor":
                lifecycle.SupportsOwnedUpgradeResume = false;
                break;
            case "different-live-schema":
                await ModifyLiveSchemaAsync(fixture.Paths, "ALTER TABLE sentinel RENAME TO replaced_store;");
                break;
            case "different-processing-marker":
                await ModifyLiveSchemaAsync(fixture.Paths, "UPDATE capture_processing_schema SET version = 10;");
                break;
            case "new-acquisition":
                // A higher Fleet sequence can hide this raw reservation in the legacy combined wire field.
                await ModifyLiveSchemaAsync(fixture.Paths, "INSERT INTO raw_capture_assignments(capture_sequence) VALUES(1);");
                break;
            case "different-fleet-marker":
                ExecuteSql(Path.Combine(fixture.Paths.StateRoot, "raw", ".fleet", "fleet-status.db"),
                    "UPDATE fleet_status_metadata SET schema_version = 2;");
                break;
            case "candidate-runtime":
                fixture.Runner.UseCandidateRuntime();
                break;
            case "missing-lock":
                File.Delete(Path.Combine(fixture.Paths.InstanceRoot, ".deployment.lock"));
                break;
        }

        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));

        StringAssert.Contains(failure.Message, message, StringComparison.Ordinal);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(0, lifecycle.RecoveryResumeCalls);
        Assert.AreEqual(0, lifecycle.OwnedRecoveryResumeCalls);
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SignedRestoreOnly_RefusesArmedLeavePausedJournalEvenWithAtomicEndpoint()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        using var release = ReleaseFixture.Create();
        var request = await PrepareSignedRecoveryAsync(fixture, release, "index");
        var lifecycle = new FakeLifecycleClient { SupportsOwnedUpgradeResume = true };
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            SchemaVersion = 2,
            LeavePausedOriginalSnapshot = operation.StateSnapshot,
            LeavePausedOriginalRestorePlanId = operation.StateSnapshot!.SnapshotId,
            SignedReleaseChecksumsSha256 = "d4826ad6f732ea9fe1bdc3fb0edc76f348b6ddceb2a825ee5f75e71373a374ec"
        }, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));

        StringAssert.Contains(failure.Message, "requires the original --resume --leave-paused", StringComparison.Ordinal);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(0, lifecycle.RecoveryResumeCalls);
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SignedRestoreOnly_WritesHostLimitLogOnlyAfterAtomicAdmission(bool supported)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        using var release = ReleaseFixture.Create();
        var request = await PrepareSignedRecoveryAsync(fixture, release, "manifest");
        var lifecycle = new FakeLifecycleClient { SupportsOwnedUpgradeResume = supported };
        fixture.Runner.MemoryLimit = false;
        var log = InstallLogPath(fixture, request.RecoveryOperationId!.Value);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);

        if (!supported)
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));
            Assert.IsFalse(File.Exists(log));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
            Assert.AreEqual(0, lifecycle.OwnedRecoveryResumeCalls);
        }
        else
        {
            await ExecuteRestoreAsync(fixture, request, lifecycle);
            AssertRecoveryWarning(await File.ReadAllTextAsync(log), [MemoryLimitUnsupported], "the install log", "RESTORE-ONLY RECOVERY");
            Assert.AreEqual(1, lifecycle.OwnedRecoveryResumeCalls);
        }
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    private static async Task ModifyLiveSchemaAsync(InstallationPaths paths, string statement)
    {
        var raw = CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);
        await using var connection = new SqliteConnection($"Data Source={raw};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The test passes fixed schema statements.
        command.CommandText = statement;
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<LifecycleRequest> PrepareSignedRecoveryAsync(LifecycleFixture fixture, ReleaseFixture release,
        string digestKind, bool foreignPause = false, bool withFleet = false)
    {
        var legacy = await fixture.PrepareRestoreAsync();
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var selected = (await ReleaseAcquirer.AcquireImageAsync(release.Folder, null,
            fixture.Manifest.DockerDaemon.Architecture, CancellationToken.None))!;
        var id = digestKind switch
        {
            "config" => selected.ImageReference,
            "manifest" => release.ManifestDigest(selected.Image.Platform),
            "index" => release.Digest,
            _ => throw new ArgumentOutOfRangeException(nameof(digestKind))
        };
        var request = legacy with
        {
            ImageReference = null,
            Release = release.Folder,
            NoDownload = false,
            MigrationBackwardCompatible = false
        };
        var snapshotRequest = request with
        {
            ImageReference = selected.ImageReference,
            ImageArchiveSha256 = selected.ArchiveSha256
        };
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            id, id, fixture.Uid, fixture.Gid);
        var root = Path.Combine(fixture.Paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
        var previousEnvironment = await File.ReadAllTextAsync(Path.Combine(root, "previous.env"));
        var previousCompose = await File.ReadAllTextAsync(Path.Combine(root, "previous-compose.yml"));
        SafeFileSystem.WriteTextAtomic(Path.Combine(root, "candidate.env"),
            CameraAgentLifecycleManager.ReplaceEnvironmentValue(previousEnvironment, "CAMERAAGENT_IMAGE", id));
        SafeFileSystem.WriteTextAtomic(Path.Combine(root, "candidate-compose.yml"),
            CameraAgentContainerResources.WithLimits(previousCompose, CameraAgentContainerResources.Default, CameraAgentContainerResources.Legacy));
        using var productLock = OperationLock.Acquire(Path.Combine(fixture.Paths.OperationsRoot, "deployment.lock"));
        using var instanceLock = OperationLock.Acquire(Path.Combine(fixture.Paths.InstanceRoot, ".deployment.lock"));
        await WritePauseReceiptAsync(fixture.Paths, operation, foreignPause);
        if (withFleet)
        {
            var fleet = Path.Combine(fixture.Paths.StateRoot, "raw", ".fleet", "fleet-status.db");
            SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(fleet)!);
            ExecuteSql(fleet, "CREATE TABLE fleet_status_metadata(metadata_key INTEGER PRIMARY KEY, schema_version INTEGER); INSERT INTO fleet_status_metadata VALUES(1,1);");
        }
        var snapshot = await CameraAgentStateSnapshot.CreateAsync(fixture.Paths, fixture.InstanceId, operation.OperationId,
            fixture.Manifest.Image, CameraAgentStateLayout.SqliteStores, CancellationToken.None);
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            CandidateImage = selected.ArchivedImage! with { ImageId = id, ImmutableReference = id },
            RequestSha256 = snapshotRequest.ComputeRequestSha256(),
            SignedReleaseChecksumsSha256 = selected.VerifiedRelease.ChecksumsSha256,
            StateSnapshotRequired = true,
            StateSnapshot = snapshot,
            StateRestorePlan = await CameraAgentStateSnapshot.PlanRestoreAsync(fixture.Paths, fixture.InstanceId, snapshot,
                CameraAgentStateLayout.SqliteStores, CancellationToken.None),
            StateSnapshotRestoredUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        return request;
    }

    private static async Task WritePauseReceiptAsync(InstallationPaths paths, LifecycleOperationState operation, bool foreign, bool insertOnly = false)
    {
        var database = CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        using var execute = connection.CreateCommand();
        if (!insertOnly)
        {
            execute.CommandText = """
                CREATE TABLE capture_control_commands(
                  idempotency_key TEXT, actor TEXT, reason TEXT, target_state TEXT, expected_version INTEGER,
                  status TEXT, result_state TEXT, result_version INTEGER, changed INTEGER,
                  requested_unix_ms INTEGER, completed_unix_ms INTEGER, payload_sha256 TEXT DEFAULT 'fixture');
                CREATE TABLE capture_control_state(state_key INTEGER, state TEXT, version INTEGER);
                CREATE TABLE raw_capture_sequences(agent_id TEXT, last_sequence INTEGER);
                CREATE TABLE raw_capture_assignments(capture_sequence INTEGER);
                CREATE TABLE raw_captures(capture_sequence INTEGER);
                CREATE TABLE capture_processing_schema(schema_key INTEGER, version INTEGER);
                INSERT INTO capture_processing_schema VALUES(1, 9);
                """;
            await execute.ExecuteNonQueryAsync();
        }
        execute.CommandText = """
            INSERT INTO capture_control_commands(idempotency_key, actor, reason, target_state, expected_version,
                status, result_state, result_version, changed, requested_unix_ms, completed_unix_ms)
            VALUES($key, $actor, $reason, 'paused', NULL, 'completed', 'paused', $version, 1, $time, $time);
            INSERT INTO capture_control_state SELECT 1, 'paused', $version WHERE NOT EXISTS(SELECT 1 FROM capture_control_state);
            """;
        var command = Guid.NewGuid();
        execute.Parameters.AddWithValue("$key", $"lifecycle:{command:D}:pause");
        execute.Parameters.AddWithValue("$actor", $"installer-lifecycle:{command:D}");
        execute.Parameters.AddWithValue("$reason", foreign ? "A different operation paused capture." :
            $"transactional lifecycle operation {operation.OperationId:D} pause");
        execute.Parameters.AddWithValue("$version", operation.PreMutationContinuity!.CaptureVersion);
        execute.Parameters.AddWithValue("$time", operation.PreMutationContinuity.RecordedUtc.ToUnixTimeMilliseconds());
        await execute.ExecuteNonQueryAsync();
    }
}
