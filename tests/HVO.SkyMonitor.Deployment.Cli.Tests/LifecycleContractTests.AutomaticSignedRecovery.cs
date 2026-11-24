using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DataRow("quiet")]
    [DataRow("later-pause")]
    [DataRow("later-resume")]
    [DataRow("lost-ack")]
    [DataRow("legacy-endpoint")]
    [DataRow("unowned-pause")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AutomaticSignedRecovery_DelayedPredecessorSettlesOnlyTheOwnedPause(string scenario)
    {
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out var candidate);
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths)
        {
            LoseAcknowledgement = scenario == "lost-ack",
            SupportsOwnedResume = scenario != "legacy-endpoint"
        };
        if (scenario == "unowned-pause") lifecycle.AcceptOwnerCommand("paused", "preexisting-owner-pause", changed: true);
        // Fleet can advance while admission is paused. A capture boundary need not equal that independent high-water.
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == candidate)
                ExecuteSql(Path.Combine(fixture.Paths.StateRoot, "raw", ".fleet", "fleet-status.db"),
                    "UPDATE fleet_status_metadata SET next_sequence = 7;");
        };

        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity)
        {
            RejectNextVerification = true,
            WaitForHealth = (timeout, token) =>
            {
                if (fixture.Runner.ActiveImageId != fixture.Manifest.Image.ImageId) return Task.CompletedTask;
                Assert.IsNotNull(timeout);
                Assert.IsGreaterThan(TimeSpan.FromMinutes(3), timeout.Value);
                waiting.TrySetResult();
                return ready.Task.WaitAsync(token);
            }
        };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        Task<LifecycleResult> RunAsync(LifecycleRequest command) => CameraAgentLifecycleManager.ExecuteAsync(
            command, fixture.Runner, _ => lifecycle, _ => owner, fixture.Uid, fixture.Gid, deadline.Token,
            error: TextWriter.Null);

        var upgrade = RunAsync(request);
        try
        {
            await waiting.Task.WaitAsync(deadline.Token);
            var atWait = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, deadline.Token))!;
            Assert.AreEqual(LifecycleOperationPhase.Restoring, atWait.Phase);
            Assert.IsNotNull(atWait.StateSnapshotRestoredUtc);
            Assert.AreEqual(scenario == "unowned-pause" ? (Guid?)null : lifecycle.PauseCommandId,
                atWait.SignedRecoveryPauseCommandId);
            Assert.AreEqual(0, lifecycle.OwnedRequests.Count);
            Assert.AreEqual(0, lifecycle.GenericResumeCalls);
            if (scenario == "later-pause") lifecycle.AcceptOwnerCommand("paused", "owner-no-op", changed: false);
            if (scenario == "later-resume") lifecycle.AcceptOwnerCommand("running", "owner-resume", changed: true);
        }
        finally
        {
            ready.TrySetResult();
            await Assert.ThrowsExactlyAsync<InstallerException>(() => upgrade);
        }

        var settled = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, deadline.Token))!;
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        Assert.AreEqual(0, lifecycle.GenericResumeCalls);
        Assert.AreEqual(7, ReadCounter(fixture.Paths, "raw/.fleet/fleet-status.db", "SELECT next_sequence FROM fleet_status_metadata;"));
        var expectedPhase = scenario switch
        {
            "quiet" => LifecycleOperationPhase.Restored,
            "lost-ack" => LifecycleOperationPhase.Restoring,
            _ => LifecycleOperationPhase.RecoveryAdmissionPreserved
        };
        Assert.AreEqual(expectedPhase, settled.Phase);
        Assert.AreEqual(scenario == "lost-ack", settled.MutationStarted);
        Assert.HasCount(scenario is "quiet" or "lost-ack" or "later-pause" ? 1 : 0, lifecycle.OwnedRequests);
        if (scenario == "legacy-endpoint")
            Assert.AreEqual("restored-predecessor-admission-preserved-unsupported-atomic-resume", settled.FailureCode);
        if (expectedPhase == LifecycleOperationPhase.RecoveryAdmissionPreserved)
            await AssertLaterLifecycleAdmittedAsync(fixture);
        if (scenario == "later-pause")
        {
            Assert.AreEqual("Paused", lifecycle.ReadBoundary().CaptureState);
            Assert.AreEqual(settled.PreMutationContinuity!.CaptureVersion, lifecycle.ReadBoundary().CaptureVersion);
            Assert.AreEqual(1, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db",
                "SELECT COUNT(*) FROM capture_control_commands WHERE idempotency_key = 'owner-no-op' AND changed = 0;"));
            await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(request with { Resume = true }));
            Assert.HasCount(1, lifecycle.OwnedRequests);
            Assert.AreEqual("Paused", lifecycle.ReadBoundary().CaptureState);
        }
        else if (scenario == "lost-ack")
        {
            Assert.IsNotNull(settled.RestoreResumeCommandId);
            Assert.AreEqual("Running", lifecycle.ReadBoundary().CaptureState);
            lifecycle.AcceptOwnerCommand("paused", "owner-after-resume", changed: true);
            var stopCount = fixture.Runner.ComposeStopCount;
            var upCount = fixture.Runner.ComposeUpCount;
            var replay = await RunAsync(request with { Resume = true });
            Assert.AreEqual("restored-previous-healthy-resume-superseded-current-admission-preserved", replay.Outcome);
            Assert.AreEqual("Paused", lifecycle.ReadBoundary().CaptureState);
            Assert.AreEqual(settled.RestoreResumeCommandId, lifecycle.OwnedRequests[1]);
            Assert.AreEqual(lifecycle.OwnedRequests[0], lifecycle.OwnedRequests[1]);
            Assert.AreEqual(stopCount, fixture.Runner.ComposeStopCount);
            Assert.AreEqual(upCount, fixture.Runner.ComposeUpCount);
        }
        else
        {
            Assert.AreEqual(scenario is "quiet" or "later-resume" ? "Running" : "Paused", lifecycle.ReadBoundary().CaptureState);
        }
        Assert.AreEqual(0, lifecycle.GenericResumeCalls);
    }

    [TestMethod]
    [DataRow("insert")]
    [DataRow("edit")]
    [DataRow("delete")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AutomaticSignedRecovery_HoldsFleetRecordChangesEvenWhenCaptureDidNotAdvance(string change)
    {
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out var candidate);
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths) { SupportsOwnedResume = true };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        var fleet = Path.Combine(fixture.Paths.StateRoot, "raw", ".fleet", "fleet-status.db");
        ExecuteSql(fleet, "INSERT INTO fleet_status_records VALUES(1, 1); UPDATE fleet_status_metadata SET next_sequence = 2;");
        byte[]? changed = null;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != candidate) return;
            ExecuteSql(fleet, change switch
            {
                "insert" => "INSERT INTO fleet_status_records VALUES(2, 2); UPDATE fleet_status_metadata SET next_sequence = 3;",
                "edit" => "UPDATE fleet_status_records SET sequence = 2;",
                "delete" => "DELETE FROM fleet_status_records;",
                _ => throw new AssertFailedException("Unexpected Fleet change.")
            });
            changed = File.ReadAllBytes(fleet);
        };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        Task<LifecycleResult> RunAsync(bool resume) => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = resume }, fixture.Runner, _ => lifecycle, _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(resume: false));
        StringAssert.Contains(failure.Message, "changed Fleet records or metadata", StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, "Automatic signed recovery is held", StringComparison.Ordinal);
        Assert.IsFalse(failure.Message.Contains("remains pending", StringComparison.Ordinal));
        var held = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(3, held.SchemaVersion);
        Assert.IsTrue(held.MutationStarted);
        Assert.IsNull(held.StateRestorePlan);
        Assert.IsNull(held.StateSnapshotRestoredUtc);
        Assert.AreEqual(candidate, fixture.Runner.ActiveImageId);
        Assert.IsFalse(fixture.Runner.Running);
        _ = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, held.StateSnapshot!, CancellationToken.None);
        var journal = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(resume: true));
        StringAssert.Contains(refused.Message, "Automatic signed recovery is held", StringComparison.Ordinal);
        CollectionAssert.AreEqual(journal, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        CollectionAssert.AreEqual(changed!, await File.ReadAllBytesAsync(fleet));
        Assert.IsEmpty(lifecycle.OwnedRequests);
        Assert.AreEqual(0, lifecycle.GenericResumeCalls);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AutomaticSignedRecovery_UnprovenBoundaryHoldsAStoppedCandidateAndItsCheckpoint(bool interrupted, bool unreadable)
    {
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out var candidate);
        using var crashRoot = new TemporaryDirectory();
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        var raw = RawIngressPath(fixture.Paths);
        byte[]? acceptedCapture = null;
        LifecycleStateSnapshot? checkpoint = null;
        byte[]? checkpointManifest = null;
        void AcceptCapture()
        {
            lifecycle.AcceptOwnerCommand("running", "owner-admits-capture", changed: true);
            ExecuteSql(raw, """
                INSERT INTO raw_capture_sequences VALUES('fixture-owner-camera', 1);
                INSERT INTO raw_capture_assignments VALUES('admitted-capture', 'admitted-artifact', 'fixture-owner-camera', 1);
                INSERT INTO raw_captures(capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
                    manifest_sha256, payload_sha256, payload_length, payload_relative_path, sidecar_relative_path, manifest_json,
                    exposure_started_unix_ms, durable_ingress_unix_ms, committed_unix_ms, state, evidence_origin)
                VALUES('admitted-capture', 'admitted-artifact', 'fixture-owner-camera', 1, printf('%064d',1), printf('%064d',2),
                    printf('%064d',3), 1, 'fixture.raw', 'fixture.json', X'7B7D', 1, 1, 1, 'committed', 'DeveloperFixture');
                """);
            acceptedCapture = File.ReadAllBytes(raw);
        }
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != candidate) return;
            var started = CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None)
                .GetAwaiter().GetResult()!;
            checkpoint = started.StateSnapshot!;
            checkpointManifest = File.ReadAllBytes(Path.Combine(
                CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, checkpoint.OperationId, checkpoint.SnapshotId), "manifest.json"));
            if (unreadable)
            {
                File.WriteAllText(raw, "Unreadable synthetic candidate store.");
                acceptedCapture = File.ReadAllBytes(raw);
            }
            else if (!interrupted)
            {
                AcceptCapture();
            }
        };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = !interrupted };
        Task<LifecycleResult> RunAsync(LifecycleRequest command) => CameraAgentLifecycleManager.ExecuteAsync(
            command, fixture.Runner, _ => lifecycle, _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null);

        if (interrupted)
        {
            var crashImages = new List<CrashImage>();
            CaptureCrashImages(fixture, crashRoot.Path, crashImages);
            try
            {
                Assert.AreEqual("completed", (await RunAsync(request)).Outcome);
            }
            finally
            {
                ResetCrashHooks();
            }
            RestoreCrashImage(fixture, crashImages.First(static image => image.Step == "journal:CandidateVerified"));
            AcceptCapture();
        }
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(interrupted ? request with { Resume = true } : request));
        StringAssert.Contains(failure.Message, "Automatic signed recovery is held", StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, unreadable ? "durable capture command and sequence could not be proved" : "later capture command",
            StringComparison.Ordinal);
        var held = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(candidate, fixture.Runner.ActiveImageId);
        Assert.IsFalse(fixture.Runner.Running);
        Assert.IsTrue(held.MutationStarted);
        Assert.AreEqual(3, held.SchemaVersion, "A stock schema-1 installer must reject a held operation.");
        Assert.AreEqual(checkpoint, held.StateSnapshot);
        Assert.IsNull(held.StateSnapshotRestoredUtc);
        Assert.IsNull(held.StateRestorePlan);
        CollectionAssert.AreEqual(acceptedCapture!, await File.ReadAllBytesAsync(raw));
        if (!unreadable)
        {
            Assert.AreEqual(1, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", "SELECT COUNT(*) FROM raw_captures WHERE capture_id = 'admitted-capture';"));
            Assert.AreEqual(1, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db",
                "SELECT COUNT(*) FROM capture_control_commands WHERE idempotency_key = 'owner-admits-capture' AND target_state = 'running';"));
        }
        var checkpointRoot = CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, checkpoint!.OperationId, checkpoint.SnapshotId);
        CollectionAssert.AreEqual(checkpointManifest!, await File.ReadAllBytesAsync(Path.Combine(checkpointRoot, "manifest.json")));
        _ = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, checkpoint, CancellationToken.None);

        var journal = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var stopped = fixture.Runner.ComposeStopCount;
        var startedCount = fixture.Runner.ComposeUpCount;
        var genericResumes = lifecycle.GenericResumeCalls;
        var repeated = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(request with { Resume = true }));
        StringAssert.Contains(repeated.Message, "Automatic signed recovery is held", StringComparison.Ordinal);
        Assert.AreEqual(stopped, fixture.Runner.ComposeStopCount);
        Assert.AreEqual(startedCount, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(genericResumes, lifecycle.GenericResumeCalls);
        Assert.IsEmpty(lifecycle.OwnedRequests);
        CollectionAssert.AreEqual(journal, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        CollectionAssert.AreEqual(acceptedCapture!, await File.ReadAllBytesAsync(raw));
    }

    private static async Task AssertLaterLifecycleAdmittedAsync(LifecycleFixture fixture)
    {
        var next = await CameraAgentLifecycleManager.BeginAsync(
            fixture.Request(LifecycleOperationKind.Backup) with { DryRun = true }, fixture.Paths,
            LifecycleOperationKind.Backup, fixture.Manifest, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Planned, next.Phase);
        using var lease = await CameraAgentLifecycleManager.AcquireControlDeploymentAsync(fixture.Paths, CancellationToken.None,
            settleCommittedRecovery: true, returnedProcess: _ => throw new AssertFailedException("Settled recovery requires no process probe."));
    }

    private static async Task<LifecycleFixture> AutomaticSignedFixtureAsync(bool fleetConfigured = true, bool localRunner = false)
    {
        var replayProfile = localRunner
            ? HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner
            : HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess;
        var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, replayProfile, recoveryState: true,
            installedImage: static image => CurrentStateContract(image) with { RawIngressSchema = "15" });
        fixture.Runner.CandidateRawIngressSchema = "15";
        fixture.Runner.CandidateLabels["io.hvo.skymonitor.minimum-compatible-revision"] = new string('6', 40);
        WriteCanonicalV15RawIngressJournal(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths),
            "INSERT INTO capture_control_state(state_key, state, version, updated_unix_ms) VALUES(1, 'running', 3, 0);");
        WriteIdentityLineage(fixture.Paths);
        if (!fleetConfigured) return fixture;
        var fleet = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, "raw/.fleet/fleet-status.db");
        SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(fleet)!);
        ExecuteSql(fleet, """
            CREATE TABLE fleet_status_metadata(metadata_key INTEGER PRIMARY KEY, next_sequence INTEGER NOT NULL, schema_version INTEGER NOT NULL);
            INSERT INTO fleet_status_metadata VALUES(1, 1, 1);
            CREATE TABLE fleet_status_records(record_id INTEGER PRIMARY KEY, sequence INTEGER NOT NULL);
            """);
        return fixture;
    }

    private sealed class AutomaticSignedLifecycleClient(InstallationPaths paths) : ICameraAgentLifecycleClient
    {
        private readonly StoreBackedLifecycleClient _reader = new(paths);
        private readonly Dictionary<Guid, LifecycleResumeReceipt> _receipts = [];
        public Guid PauseCommandId { get; } = Guid.NewGuid();
        public List<Guid> OwnedRequests { get; } = [];
        public bool SupportsOwnedResume { get; set; }
        public bool LoseAcknowledgement { get; init; }
        public Action? AfterPauseAccepted { get; set; }
        public Action? BeforeConfirmDrained { get; set; }
        public Action? BeforeCommittedResume { get; set; }
        public int CommittedCapabilityChecks { get; private set; }
        public int PauseRequests { get; private set; }
        public int GenericResumeCalls { get; private set; }
        private Guid _operationId;

        public LifecycleContinuity ReadBoundary() => _reader.ReadBoundary();

        public Task<LifecycleContinuity> PauseAndDrainAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            _operationId = operationId;
            var command = PauseRequests++ == 0 ? PauseCommandId : Guid.NewGuid();
            WriteCommand("paused", $"lifecycle:{command:D}:pause", $"installer-lifecycle:{command:D}",
                $"transactional lifecycle operation {operationId:D} pause", changed: ReadBoundary().CaptureState != "Paused");
            AfterPauseAccepted?.Invoke();
            return Task.FromResult(ReadBoundary());
        }

        public Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
        {
            BeforeConfirmDrained?.Invoke();
            return ReadContinuityAsync(verificationToken, cancellationToken);
        }

        public Task<LifecycleContinuity> ReadContinuityAsync(string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            return Task.FromResult(ReadBoundary());
        }

        public Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
        {
            GenericResumeCalls++;
            return Task.CompletedTask;
        }

        public Task<LifecycleResumeReceipt> ResumeRecoveryAsync(Guid operationId, Guid commandId, long expectedVersion,
            string verificationToken, CancellationToken cancellationToken)
            => throw new AssertFailedException("Signed automatic recovery must not dispatch a generic resume.");

        public Task VerifyOwnedUpgradeResumeAsync(string verificationToken, CancellationToken cancellationToken)
            => SupportsOwnedResume ? Task.CompletedTask : throw new InstallerException("No compatible owned resume endpoint.");

        public async Task<LifecycleResumeReceipt> ResumeOwnedUpgradeAsync(Guid operationId, Guid pauseCommandId, Guid commandId,
            long expectedVersion, string verificationToken, CancellationToken cancellationToken)
            => await TryOwnedResumeAsync(operationId, pauseCommandId, commandId, expectedVersion, verificationToken, cancellationToken)
                ?? throw new InstallerException("The atomic owned resume rejected a later accepted capture command.");

        public async Task<(bool Supported, LifecycleResumeReceipt? Receipt)> TryResumeOwnedUpgradeAsync(Guid operationId, Guid pauseCommandId, Guid commandId,
            long expectedVersion, string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            CommittedCapabilityChecks++;
            BeforeCommittedResume?.Invoke();
            if (!SupportsOwnedResume) return (false, null);
            return (true, await TryOwnedResumeAsync(operationId, pauseCommandId, commandId,
                expectedVersion, verificationToken, cancellationToken));
        }

        private async Task<LifecycleResumeReceipt?> TryOwnedResumeAsync(Guid operationId, Guid pauseCommandId, Guid commandId,
            long expectedVersion, string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            Assert.AreEqual(_operationId, operationId);
            Assert.AreEqual(PauseCommandId, pauseCommandId);
            OwnedRequests.Add(commandId);
            if (_receipts.TryGetValue(commandId, out var previous)) return previous with { Replayed = true };
            if (expectedVersion != ReadBoundary().CaptureVersion) return null;
            await using var connection = new SqliteConnection($"Data Source={RawIngressPath(paths)};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(cancellationToken);
            using var latest = connection.CreateCommand();
            latest.CommandText = "SELECT idempotency_key FROM capture_control_commands ORDER BY rowid DESC LIMIT 1;";
            if (!Equals(await latest.ExecuteScalarAsync(cancellationToken), $"lifecycle:{pauseCommandId:D}:pause"))
                return null;
            WriteCommand("running", $"lifecycle:{commandId:D}:resume-owned-upgrade", $"installer-lifecycle:{commandId:D}",
                "Automatic recovery", changed: true);
            var now = DateTimeOffset.UtcNow;
            var receipt = new LifecycleResumeReceipt("Running", ReadBoundary().CaptureVersion, true, false, now, now);
            _receipts.Add(commandId, receipt);
            if (LoseAcknowledgement) throw new HttpRequestException("Synthetic lost owned-resume acknowledgement.");
            return receipt;
        }

        public void AcceptOwnerCommand(string target, string key, bool changed)
            => WriteCommand(target, key, "authenticated-fixture-owner", "Fixture owner disposition", changed);

        private void WriteCommand(string target, string key, string actor, string reason, bool changed)
        {
            using var connection = new SqliteConnection($"Data Source={RawIngressPath(paths)};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE capture_control_state
                SET state = $target, version = version + $changed * (CASE WHEN $target = 'paused' THEN 2 ELSE 1 END),
                    updated_unix_ms = $now;
                INSERT INTO capture_control_commands (idempotency_key, target_state, actor, reason, payload_sha256,
                    status, result_state, result_version, changed, requested_unix_ms, completed_unix_ms)
                SELECT $key, $target, $actor, $reason, printf('%064d', 0), 'completed', $target, version, $changed, $now, $now
                FROM capture_control_state;
                """;
            command.Parameters.AddWithValue("$target", target);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$changed", changed);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
    }
}
