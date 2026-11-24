using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DataRow("success")]
    [DataRow("armed-replay")]
    [DataRow("snapshot-failure")]
    [DataRow("compose-failure")]
    [DataRow("quiet-candidate-failure")]
    [DataRow("changed-candidate-hold")]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LeavePausedReview_ExactUpgradePreservesAdmissionAcrossFailureAndReplay(string scenario)
    {
        using var fixture = await AutomaticSignedFixtureAsync(fleetConfigured: false);
        ExecuteSql(CameraAgentStateLayout.IdentityDatabasePath(fixture.Paths.StateRoot),
            "CREATE TABLE AspNetUsers(Id TEXT PRIMARY KEY); INSERT INTO AspNetUsers VALUES('fixture-owner');");
        using var release = CandidateRelease(fixture, out var candidate, version: "0.9.1-rc.2");
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true, FleetDisabled = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        var verifiedImage = (await ReleaseAcquirer.AcquireImageAsync(release.Folder, null,
            fixture.Manifest.DockerDaemon.Architecture, CancellationToken.None))!;
        var anchoredImage = verifiedImage with
        {
            VerifiedRelease = verifiedImage.VerifiedRelease with
            {
                ChecksumsSha256 = "d4826ad6f732ea9fe1bdc3fb0edc76f348b6ddceb2a825ee5f75e71373a374ec"
            }
        };
        Task<LifecycleResult> RunAsync(LifecycleRequest command) => CameraAgentLifecycleManager.ExecuteAsync(
            command, fixture.Runner, _ => lifecycle, _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null, acquireReleaseImage: (_, _, _, _) => Task.FromResult<AcquiredImage?>(anchoredImage));

        await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(request));
        var original = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(LifecycleOperationPhase.RecoveryAdmissionPreserved, original.Phase);
        Assert.IsNotNull(original.StateSnapshotRestoredUtc);
        var legacy = await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, original with
        {
            SchemaVersion = 1,
            Phase = LifecycleOperationPhase.Restoring,
            Status = InstallationStatus.Running,
            MutationStarted = true,
            FailureCode = "lifecycle-failed",
            SignedReleaseChecksumsSha256 = null,
            SignedRecoveryPauseCommandId = null,
            PostMutationContinuity = null
        }, CancellationToken.None);
        var raw = RawIngressPath(fixture.Paths);
        var originalRaw = await File.ReadAllBytesAsync(raw);
        var legacyBytes = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var starts = fixture.Runner.ComposeUpCount;
        var stops = fixture.Runner.ComposeStopCount;
        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(request with { Resume = true }));
        StringAssert.Contains(refused.Message, "no installer-owned retry pause", StringComparison.Ordinal);
        CollectionAssert.AreEqual(legacyBytes, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(starts, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(stops, fixture.Runner.ComposeStopCount);

        var retry = request with { Resume = true, LeavePaused = true, RecoveryOperationId = legacy.OperationId };
        var candidateStarts = 0;
        byte[]? changed = null;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != candidate) return;
            candidateStarts++;
            if (scenario == "changed-candidate-hold")
            {
                lifecycle.AcceptOwnerCommand("paused", "owner-pauses-after-candidate-start", changed: false);
                changed = File.ReadAllBytes(raw);
            }
            owner.RejectNextVerification = scenario is "quiet-candidate-failure" or "changed-candidate-hold";
        };
        if (scenario == "armed-replay")
        {
            CameraAgentLifecycleManager.JournalRecorded = state =>
            {
                if (state.SchemaVersion == 2 && state.Phase == LifecycleOperationPhase.Restoring)
                    throw new InstallerException("Interrupted after arming the leave-paused fence.");
            };
            try
            {
                await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(retry));
            }
            finally
            {
                ResetCrashHooks();
            }
            var armed = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
            Assert.AreEqual(2, armed.SchemaVersion);
            Assert.AreEqual(legacy.StateSnapshot, armed.LeavePausedOriginalSnapshot);
            Assert.AreEqual(starts, fixture.Runner.ComposeUpCount);
            Assert.AreEqual(stops, fixture.Runner.ComposeStopCount);
            CollectionAssert.AreEqual(originalRaw, await File.ReadAllBytesAsync(raw));
        }
        if (scenario == "snapshot-failure")
        {
            CameraAgentStateSnapshot.StepCompleted = step =>
            {
                if (step.StartsWith("snapshot:", StringComparison.Ordinal))
                    throw new IOException("Interrupted before recording the retry checkpoint.");
            };
        }
        if (scenario == "compose-failure") fixture.Runner.RejectComposeUp = image => image == candidate;

        try
        {
            if (scenario is "success" or "armed-replay")
            {
                var completed = await RunAsync(retry);
                Assert.AreEqual("completed-admission-preserved", completed.Outcome);
                Assert.AreEqual(candidate, completed.Image!.ImageId);
                Assert.AreEqual("Paused", completed.PreservedAdmission!.State);
                Assert.AreEqual(1, candidateStarts);
                var settled = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
                Assert.AreEqual(LifecycleOperationPhase.Completed, settled.Phase);
                Assert.AreEqual(legacy.StateSnapshot, settled.LeavePausedOriginalSnapshot);
                Assert.AreNotEqual(legacy.StateSnapshot, settled.StateSnapshot);
                var journal = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
                starts = fixture.Runner.ComposeUpCount;
                stops = fixture.Runner.ComposeStopCount;
                lifecycle.AcceptOwnerCommand("running", "owner-resumes-after-completion", changed: true);
                var replayed = await RunAsync(retry);
                Assert.AreEqual("completed-admission-preserved", replayed.Outcome);
                Assert.AreEqual("Running", replayed.PreservedAdmission!.State);
                Assert.AreEqual(starts, fixture.Runner.ComposeUpCount);
                Assert.AreEqual(stops, fixture.Runner.ComposeStopCount);
                CollectionAssert.AreEqual(journal, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
            }
            else
            {
                var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(retry));
                var afterFailure = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
                Assert.AreEqual(legacy.StateSnapshot, afterFailure.LeavePausedOriginalSnapshot);
                Assert.AreEqual(scenario is "quiet-candidate-failure" or "changed-candidate-hold" ? 1 : 0, candidateStarts);
                var journal = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
                starts = fixture.Runner.ComposeUpCount;
                stops = fixture.Runner.ComposeStopCount;
                if (scenario == "changed-candidate-hold")
                {
                    StringAssert.Contains(failure.Message, "recovery is held", StringComparison.Ordinal);
                    Assert.IsTrue(afterFailure.MutationStarted);
                    Assert.IsFalse(fixture.Runner.Running);
                    Assert.AreEqual(candidate, fixture.Runner.ActiveImageId);
                    Assert.IsNull(afterFailure.StateSnapshotRestoredUtc);
                    CollectionAssert.AreEqual(changed!, await File.ReadAllBytesAsync(raw));
                    await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(retry));
                }
                else
                {
                    Assert.AreEqual(LifecycleOperationPhase.RecoveryAdmissionPreserved, afterFailure.Phase, failure.Message);
                    Assert.IsFalse(afterFailure.MutationStarted);
                    Assert.IsTrue(fixture.Runner.Running);
                    Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
                    if (scenario == "quiet-candidate-failure")
                        Assert.IsNotNull(afterFailure.StateRestorePlan);
                    if (scenario == "compose-failure")
                    {
                        Assert.IsNotNull(afterFailure.StateSnapshot);
                        Assert.IsNull(afterFailure.StateRestorePlan);
                        Assert.IsNull(afterFailure.StateSnapshotRestoredUtc);
                    }
                    if (scenario != "quiet-candidate-failure")
                        CollectionAssert.AreEqual(originalRaw, await File.ReadAllBytesAsync(raw));
                    Assert.AreEqual(1, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db",
                        "SELECT COUNT(*) FROM capture_control_commands WHERE target_state = 'paused' AND changed = 1;"));
                    var replayed = await RunAsync(retry);
                    Assert.AreEqual("restored-predecessor-admission-preserved", replayed.Outcome);
                    Assert.AreEqual("Paused", replayed.PreservedAdmission!.State);
                    await AssertLaterLifecycleAdmittedAsync(fixture);
                }
                Assert.AreEqual(starts, fixture.Runner.ComposeUpCount);
                Assert.AreEqual(stops, fixture.Runner.ComposeStopCount);
                CollectionAssert.AreEqual(journal, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
            }
        }
        finally
        {
            ResetCrashHooks();
        }
        _ = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, legacy.StateSnapshot!, CancellationToken.None);
        Assert.AreEqual(1, lifecycle.PauseRequests, "Only original upgrade dispatched a Pause.");
        Assert.AreEqual(0, lifecycle.GenericResumeCalls);
        Assert.IsEmpty(lifecycle.OwnedRequests);
    }

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
