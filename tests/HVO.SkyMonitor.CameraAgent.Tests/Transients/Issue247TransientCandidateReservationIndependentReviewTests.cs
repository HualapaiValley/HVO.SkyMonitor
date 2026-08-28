using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class Issue247TransientCandidateReservationIndependentReviewTests
{
    [TestMethod]
    [DataRow("capture-id")]
    [DataRow("raw-artifact-id")]
    [DataRow("manifest-blob")]
    public async Task ReserveAsync_MalformedDurableSourceFormatIsAuthoritativelyQuarantined(string target)
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        var artifact = source.Locator.Artifact.ArtifactId.ToString("N");
        switch (target)
        {
            case "capture-id":
                await ExecuteAsync(fixture.Root,
                    "PRAGMA foreign_keys = OFF; UPDATE raw_captures SET capture_id = 'not-a-guid' WHERE raw_artifact_id = $artifact;",
                    ("$artifact", artifact)).ConfigureAwait(false);
                break;
            case "raw-artifact-id":
                await ExecuteAsync(fixture.Root,
                    "UPDATE raw_captures SET raw_artifact_id = 'not-a-guid' WHERE raw_artifact_id = $artifact;",
                    ("$artifact", artifact)).ConfigureAwait(false);
                break;
            case "manifest-blob":
                await ExecuteAsync(fixture.Root,
                    "UPDATE raw_captures SET manifest_json = zeroblob(3), manifest_sha256 = $sha WHERE raw_artifact_id = $artifact;",
                    ("$sha", new string('0', 64)), ("$artifact", artifact)).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.IsNotInstanceOfType<FormatException>(exception);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_event_identities WHERE event_id = '{reservation.EventId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_conflicts WHERE candidate_id = '{reservation.CandidateId:N}' AND event_id = '{reservation.EventId:N}' AND reason = 'source-evidence-invalid';")
            .ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM raw_captures WHERE retention_hold != 0;").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("exact-candidate")]
    [DataRow("conflicting-candidate")]
    [DataRow("same-agent-event")]
    [DataRow("different-agent-event")]
    [DataRow("preexisting-conflict")]
    public async Task ReserveAsync_IdentityRaceConvergesOrPreservesExactDurableConflict(string race)
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        if (race == "preexisting-conflict")
        {
            await InsertConflictAsync(fixture.Root, reservation, "preexisting-race").ConfigureAwait(false);
        }
        var injector = new CallbackFaultInjector(
            TransientCandidateFaultPoint.AfterReservationValidation,
            () => ApplyIdentityRaceAsync(fixture.Root, reservation, race).GetAwaiter().GetResult());
        using var reconstructed = fixture.ReconstructJournal(injector);

        if (race is "exact-candidate" or "same-agent-event")
        {
            var result = await reconstructed.Journal.ReserveAsync(
                reservation, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(
                race == "exact-candidate" ? injector.InvocationCount == 1 : injector.InvocationCount >= 2,
                race);
            Assert.AreEqual(
                race == "exact-candidate"
                    ? TransientCandidateReservationDisposition.Existing
                    : TransientCandidateReservationDisposition.Created,
                result.Disposition);
            Assert.AreEqual(reservation.CandidateId, result.Entry.CandidateId);
            Assert.AreEqual(reservation.EventId, result.Entry.EventId);
            CollectionAssert.AreEqual(reservation.Sources.ToArray(), result.Entry.Sources.ToArray());
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
            return;
        }

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await reconstructed.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
        var competingCandidateExists = race == "conflicting-candidate";
        Assert.AreEqual(competingCandidateExists ? 1L : 0L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_sources WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(competingCandidateExists ? 1L : 0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM raw_captures WHERE retention_hold != 0;").ConfigureAwait(false));
        var expectedReason = race switch
        {
            "conflicting-candidate" => "reservation-identity-mismatch",
            "different-agent-event" => "event-agent-conflict",
            "preexisting-conflict" => "preexisting-race",
            _ => throw new InvalidOperationException("Unsupported identity race.")
        };
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_conflicts WHERE candidate_id = '{reservation.CandidateId:N}' AND reason = '{expectedReason}';")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
        if (race == "different-agent-event")
        {
            Assert.IsGreaterThanOrEqualTo(2, injector.InvocationCount);
        }
        if (competingCandidateExists)
        {
            Assert.AreEqual("quarantined", await fixture.ScalarStringAsync(
                $"SELECT phase FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
                .ConfigureAwait(false));
        }
    }

    [TestMethod]
    [DataRow("evidence-id")]
    [DataRow("raw-row-id")]
    [DataRow("source-schema")]
    [DataRow("locator-schema")]
    [DataRow("locator-kind")]
    [DataRow("artifact-id")]
    [DataRow("role")]
    [DataRow("variant")]
    [DataRow("recipe")]
    [DataRow("checksum")]
    [DataRow("started")]
    [DataRow("ended")]
    [DataRow("timing-quality")]
    [DataRow("timing-source")]
    [DataRow("timing-version")]
    [DataRow("order")]
    public async Task ReserveAsync_CandidateSourceFactRaceDurablyRejectsWithoutChangingHolds(string field)
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var first = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var second = await fixture.AddRawSourceAsync(2, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(first, second);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var injector = new CallbackFaultInjector(
            TransientCandidateFaultPoint.AfterReservationValidation,
            () => MutateCandidateSourceAsync(fixture.Root, reservation.CandidateId, field).GetAwaiter().GetResult());
        using var reconstructed = fixture.ReconstructJournal(injector);

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await reconstructed.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.IsGreaterThanOrEqualTo(2, injector.InvocationCount, field);
        Assert.AreEqual("quarantined", await fixture.ScalarStringAsync(
            $"SELECT phase FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(2L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
        Assert.AreEqual(2L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_sources WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        var expectedReason = field switch
        {
            "raw-row-id" => "candidate-identity-conflict",
            "source-schema" or "locator-schema" or "locator-kind" => "reservation-source-invalid",
            _ => "reservation-identity-mismatch"
        };
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_conflicts WHERE candidate_id = '{reservation.CandidateId:N}' AND reason = '{expectedReason}';")
            .ConfigureAwait(false), field);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false), field);
    }

    [TestMethod]
    public async Task ReserveAsync_ProductionRetentionWaitsForValidationThenObservesCommittedHold()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("This test requires Linux FIFO semantics.");
        }
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        source = await MoveToExpiredDirectoryAsync(fixture.Root, source).ConfigureAwait(false);
        await StageSourceAsync(fixture.Root, source).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        var payloadPath = Resolve(fixture.Root, await ScalarStringAsync(
            fixture.Root, "SELECT payload_relative_path FROM raw_captures;").ConfigureAwait(false));
        var sidecarPath = Resolve(fixture.Root, await ScalarStringAsync(
            fixture.Root, "SELECT sidecar_relative_path FROM raw_captures;").ConfigureAwait(false));
        var sidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
        File.Delete(sidecarPath);
        await CreateFifoAsync(sidecarPath).ConfigureAwait(false);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var fifoConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFifo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fifoWriter = Task.Run(async () =>
        {
            using var stream = new FileStream(
                sidecarPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            fifoConnected.TrySetResult();
            await releaseFifo.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            await stream.WriteAsync(sidecar, cancellation.Token).ConfigureAwait(false);
        }, cancellation.Token);
        var reservationTask = fixture.Journal.ReserveAsync(reservation, cancellation.Token).AsTask();
        Task? retentionTask = null;
        try
        {
            await fifoConnected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var capacityEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retentionOffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = CreateRetentionService(fixture.Root, capacityEntered);
            retentionTask = Task.Run(async () =>
            {
                retentionOffered.TrySetResult();
                await service.ApplyRetentionAsync(CreateConfig(), cancellation.Token).ConfigureAwait(false);
            }, cancellation.Token);
            await retentionOffered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var writerOffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unrelatedWriter = Task.Run(async () =>
            {
                writerOffered.TrySetResult();
                return await ExecuteAsync(fixture.Root,
                    "UPDATE capture_control_state SET state = 'pause_requested', version = version + 1 WHERE state_key = 1;")
                    .ConfigureAwait(false);
            }, cancellation.Token);
            await writerOffered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(1, await unrelatedWriter.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));
            Assert.IsFalse(capacityEntered.Task.IsCompleted,
                "Production retention must remain before its capacity/read/delete path while validation owns the lifecycle lock.");
            Assert.IsTrue(File.Exists(payloadPath));
            Assert.IsTrue(File.Exists(sidecarPath));

            releaseFifo.TrySetResult();
            var result = await reservationTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(TransientCandidateReservationDisposition.Created, result.Disposition);
            await capacityEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await retentionTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(payloadPath));
            Assert.IsTrue(File.Exists(sidecarPath));
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                $"SELECT COUNT(*) FROM transient_event_identities WHERE event_id = '{reservation.EventId:N}';")
                .ConfigureAwait(false));
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                $"SELECT COUNT(*) FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
                .ConfigureAwait(false));
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                $"SELECT COUNT(*) FROM transient_candidate_sources WHERE candidate_id = '{reservation.CandidateId:N}';")
                .ConfigureAwait(false));
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual("candidate_persisted", await fixture.ScalarStringAsync(
                "SELECT state FROM transient_capture_work;").ConfigureAwait(false));
            Assert.AreEqual(0, (await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false)).PressureLevel);
            Assert.AreEqual("pause_requested", await fixture.ScalarStringAsync(
                "SELECT state FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                "SELECT version FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));
        }
        finally
        {
            releaseFifo.TrySetResult();
            cancellation.CancelAfter(TimeSpan.FromSeconds(2));
            await DrainAsync(fifoWriter, reservationTask, retentionTask).ConfigureAwait(false);
        }
    }

    private static async Task ApplyIdentityRaceAsync(
        string root,
        TransientCandidateReservation reservation,
        string race)
    {
        switch (race)
        {
            case "exact-candidate":
                await InsertCandidateAsync(root, reservation, ComputeReservationIdentity(reservation)).ConfigureAwait(false);
                break;
            case "conflicting-candidate":
                await InsertCandidateAsync(root, reservation, new string('0', 64)).ConfigureAwait(false);
                break;
            case "same-agent-event":
                await InsertEventAsync(root, reservation.EventId, reservation.AgentId).ConfigureAwait(false);
                break;
            case "different-agent-event":
                await InsertEventAsync(root, reservation.EventId, "different-agent").ConfigureAwait(false);
                break;
            case "preexisting-conflict":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(race));
        }
    }

    private static async Task InsertCandidateAsync(
        string root,
        TransientCandidateReservation reservation,
        string reservationIdentity)
    {
        await InsertEventAsync(root, reservation.EventId, reservation.AgentId).ConfigureAwait(false);
        var source = reservation.Sources.Single();
        await ExecuteAsync(root,
            """
            BEGIN IMMEDIATE;
            INSERT INTO transient_candidates(
                candidate_id, event_id, agent_id, mode, required, reservation_identity_sha256,
                state, phase, source_hold_released, timeout_unix_ms, created_unix_ms, updated_unix_ms)
            VALUES ($candidate, $event, $agent, 'edge', 0, $identity,
                'pending', 'reserved', 0, 2, 1, 1);
            INSERT INTO transient_candidate_sources(
                candidate_id, source_ordinal, evidence_id, raw_capture_row_id,
                source_schema, locator_schema, locator_kind, artifact_id, artifact_role,
                artifact_variant, recipe_identity_sha256, checksum_sha256,
                observation_started_utc_ticks, observation_ended_utc_ticks,
                timing_quality, timing_source, timing_version)
            SELECT $candidate, 0, $evidence, raw_capture_row_id,
                   $source_schema, $locator_schema, $locator_kind, $artifact, $role,
                   $variant, $recipe, $checksum, $started, $ended, $quality, $timing_source, $timing_version
            FROM raw_captures WHERE raw_artifact_id = $artifact;
            UPDATE raw_captures SET retention_hold = 1 WHERE raw_artifact_id = $artifact;
            COMMIT;
            """,
            ("$candidate", reservation.CandidateId.ToString("N")),
            ("$event", reservation.EventId.ToString("N")),
            ("$agent", reservation.AgentId),
            ("$identity", reservationIdentity),
            ("$evidence", source.EvidenceId.ToString("N")),
            ("$source_schema", source.SchemaVersion),
            ("$locator_schema", source.Locator.SchemaVersion),
            ("$locator_kind", (int)source.Locator.Kind),
            ("$artifact", source.Locator.Artifact.ArtifactId.ToString("N")),
            ("$role", (int)source.Locator.Artifact.Role),
            ("$variant", source.Locator.Artifact.Variant),
            ("$recipe", source.Locator.Artifact.RecipeIdentitySha256),
            ("$checksum", source.Locator.Artifact.ChecksumSha256),
            ("$started", source.ObservationStartedUtc.UtcTicks),
            ("$ended", source.ObservationEndedUtc.UtcTicks),
            ("$quality", (int)source.TimingQuality),
            ("$timing_source", source.TimingProvenance.Source),
            ("$timing_version", source.TimingProvenance.Version)).ConfigureAwait(false);
    }

    private static Task<int> InsertEventAsync(string root, Guid eventId, string agentId)
        => ExecuteAsync(root,
            "INSERT INTO transient_event_identities(event_id, agent_id, created_unix_ms) VALUES ($event, $agent, 1);",
            ("$event", eventId.ToString("N")), ("$agent", agentId));

    private static Task<int> InsertConflictAsync(
        string root,
        TransientCandidateReservation reservation,
        string reason)
        => ExecuteAsync(root,
            "INSERT INTO transient_candidate_conflicts(candidate_id, event_id, reason, observed_unix_ms) VALUES ($candidate, $event, $reason, 1);",
            ("$candidate", reservation.CandidateId.ToString("N")),
            ("$event", reservation.EventId.ToString("N")),
            ("$reason", reason));

    private static Task<int> MutateCandidateSourceAsync(string root, Guid candidateId, string field)
    {
        var candidate = candidateId.ToString("N");
        return field switch
        {
            "evidence-id" => ExecuteAsync(root,
                "UPDATE transient_candidate_sources SET evidence_id = $value WHERE candidate_id = $candidate AND source_ordinal = 0;",
                ("$value", Guid.NewGuid().ToString("N")), ("$candidate", candidate)),
            "raw-row-id" => ExecuteAsync(root,
                "UPDATE transient_candidate_sources SET raw_capture_row_id = (SELECT MAX(raw_capture_row_id) FROM raw_captures) WHERE candidate_id = $candidate AND source_ordinal = 0;",
                ("$candidate", candidate)),
            "source-schema" => UpdateCandidateSourceAsync(root, candidate, "source_schema", "changed-source-schema"),
            "locator-schema" => UpdateCandidateSourceAsync(root, candidate, "locator_schema", "changed-locator-schema"),
            "locator-kind" => UpdateCandidateSourceAsync(root, candidate, "locator_kind", 99),
            "artifact-id" => ExecuteAsync(root,
                "UPDATE transient_candidate_sources SET artifact_id = (SELECT raw_artifact_id FROM raw_captures ORDER BY raw_capture_row_id DESC LIMIT 1) WHERE candidate_id = $candidate AND source_ordinal = 0;",
                ("$candidate", candidate)),
            "role" => UpdateCandidateSourceAsync(root, candidate, "artifact_role", (int)FrameArtifactRole.Calibrated),
            "variant" => UpdateCandidateSourceAsync(root, candidate, "artifact_variant", "changed-variant"),
            "recipe" => UpdateCandidateSourceAsync(root, candidate, "recipe_identity_sha256", new string('A', 64)),
            "checksum" => UpdateCandidateSourceAsync(root, candidate, "checksum_sha256", new string('B', 64)),
            "started" => ExecuteAsync(root,
                "UPDATE transient_candidate_sources SET observation_started_utc_ticks = observation_started_utc_ticks + 1 WHERE candidate_id = $candidate AND source_ordinal = 0;",
                ("$candidate", candidate)),
            "ended" => ExecuteAsync(root,
                "UPDATE transient_candidate_sources SET observation_ended_utc_ticks = observation_ended_utc_ticks - 1 WHERE candidate_id = $candidate AND source_ordinal = 0;",
                ("$candidate", candidate)),
            "timing-quality" => UpdateCandidateSourceAsync(root, candidate, "timing_quality", (int)TransientTimingQuality.Estimated),
            "timing-source" => UpdateCandidateSourceAsync(root, candidate, "timing_source", "changed-source"),
            "timing-version" => UpdateCandidateSourceAsync(root, candidate, "timing_version", "changed-version"),
            "order" => ExecuteAsync(root,
                """
                UPDATE transient_candidate_sources SET source_ordinal = source_ordinal + 10 WHERE candidate_id = $candidate;
                UPDATE transient_candidate_sources SET source_ordinal = CASE source_ordinal WHEN 10 THEN 1 ELSE 0 END WHERE candidate_id = $candidate;
                """,
                ("$candidate", candidate)),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }

    private static Task<int> UpdateCandidateSourceAsync(string root, string candidate, string column, object value)
    {
        if (column is not ("source_schema" or "locator_schema" or "locator_kind" or "artifact_role" or
            "artifact_variant" or "recipe_identity_sha256" or "checksum_sha256" or "timing_quality" or
            "timing_source" or "timing_version"))
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }
        return ExecuteAsync(root,
            $"UPDATE transient_candidate_sources SET {column} = $value WHERE candidate_id = $candidate AND source_ordinal = 0;",
            ("$value", value), ("$candidate", candidate));
    }

    private static async Task<TransientSourceEvidenceReferenceV1> MoveToExpiredDirectoryAsync(
        string root,
        TransientSourceEvidenceReferenceV1 source)
    {
        var artifact = source.Locator.Artifact.ArtifactId.ToString("N");
        var oldPayload = Resolve(root, await ScalarStringAsync(
            root, "SELECT payload_relative_path FROM raw_captures WHERE raw_artifact_id = $artifact;", ("$artifact", artifact))
            .ConfigureAwait(false));
        var manifestBytes = await ReadBlobAsync(root,
            "SELECT manifest_json FROM raw_captures WHERE raw_artifact_id = $artifact;", ("$artifact", artifact))
            .ConfigureAwait(false);
        var manifest = CaptureContractJson.ParseManifest(manifestBytes).Document?.Manifest
            ?? throw new InvalidDataException("Test source manifest is invalid.");
        const string payloadRelative = "frames/2020/01/01/Raw/1.bin";
        const string sidecarRelative = "frames/2020/01/01/Raw/1.json";
        var payload = Resolve(root, payloadRelative);
        var sidecar = Resolve(root, sidecarRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        File.Move(oldPayload, payload);
        var changedManifest = manifest with { RelativeArtifactPath = payloadRelative };
        var changedBytes = CaptureContractJson.Serialize(changedManifest);
        await File.WriteAllBytesAsync(sidecar, changedBytes).ConfigureAwait(false);
        var oldSidecar = Path.ChangeExtension(oldPayload, ".json");
        File.Delete(oldSidecar);
        await ExecuteAsync(root,
            """
            UPDATE raw_captures
            SET payload_relative_path = $payload, sidecar_relative_path = $sidecar,
                manifest_json = $manifest, manifest_sha256 = $manifest_sha
            WHERE raw_artifact_id = $artifact;
            """,
            ("$payload", payloadRelative), ("$sidecar", sidecarRelative),
            ("$manifest", changedBytes), ("$manifest_sha", CaptureContractJson.ComputeManifestSha256(changedBytes)),
            ("$artifact", artifact)).ConfigureAwait(false);
        return source;
    }

    private static Task<int> StageSourceAsync(string root, TransientSourceEvidenceReferenceV1 source)
        => ExecuteAsync(root,
            """
            INSERT INTO capture_lane_work(
                raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered,
                state, attempt_count, available_unix_ms, created_unix_ms, updated_unix_ms)
            SELECT raw_capture_row_id, 'transient', agent_id, capture_sequence, 0, 1,
                   'completed', 0, durable_ingress_unix_ms, committed_unix_ms, committed_unix_ms
            FROM raw_captures WHERE raw_artifact_id = $artifact;
            INSERT INTO transient_capture_work(
                raw_capture_row_id, lane_work_id, mode, required, state, artifact_id,
                manifest_sha256, created_unix_ms, updated_unix_ms)
            SELECT r.raw_capture_row_id, w.work_id, 'edge', 0, 'pending', r.raw_artifact_id,
                   r.manifest_sha256, r.committed_unix_ms, r.committed_unix_ms
            FROM raw_captures r JOIN capture_lane_work w ON w.raw_capture_row_id = r.raw_capture_row_id
            WHERE r.raw_artifact_id = $artifact;
            """,
            ("$artifact", source.Locator.Artifact.ArtifactId.ToString("N")));

    private static RetentionBackgroundService CreateRetentionService(
        string root,
        TaskCompletionSource capacityEntered)
        => new(
            new StubConfigurationAccessor(),
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }),
            new FixedTimeProvider(new DateTimeOffset(2035, 1, 1, 12, 0, 0, TimeSpan.Zero)),
            new SqliteArtifactOutbox(),
            new SignalingCapacityProvider(capacityEntered),
            new StoragePressureState(),
            NullLogger<RetentionBackgroundService>.Instance,
            new JournalRawIngressHolds(root));

    private static CameraModuleConfig CreateConfig()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            CapturePipelineConfig.Empty);

    private static string ComputeReservationIdentity(TransientCandidateReservation reservation)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "hvo-transient-candidate-reservation-v1",
            reservation.CandidateId,
            reservation.EventId,
            reservation.AgentId,
            Mode = "edge",
            Required = false,
            Sources = reservation.Sources
        });

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Issue-specific test callers provide internal SQL and parameterize mutable values.")]
    private static async Task<int> ExecuteAsync(
        string root,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Issue-specific test callers provide internal SQL and parameterize mutable values.")]
    private static async Task<string> ScalarStringAsync(
        string root,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Issue-specific test callers provide internal SQL and parameterize mutable values.")]
    private static async Task<byte[]> ReadBlobAsync(
        string root,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        return (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<SqliteConnection> OpenAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return connection;
    }

    private static string Resolve(string root, string relativePath)
        => Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static async Task CreateFifoAsync(string path)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", path)
        {
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Unable to start mkfifo.");
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, error);
    }

    private static async Task DrainAsync(params Task?[] tasks)
    {
        foreach (var task in tasks.Where(static task => task is not null))
        {
            try
            {
                await task!.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or TimeoutException or IOException or InvalidDataException)
            {
            }
        }
    }

    private sealed class CallbackFaultInjector(
        TransientCandidateFaultPoint point,
        Action callback) : ITransientCandidateFaultInjector
    {
        private int _invocations;

        internal int InvocationCount => Volatile.Read(ref _invocations);

        public void Inject(TransientCandidateFaultPoint current)
        {
            if (current == point && Interlocked.Increment(ref _invocations) == 1)
            {
                callback();
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SignalingCapacityProvider(TaskCompletionSource entered) : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot)
        {
            entered.TrySetResult();
            return new StorageCapacity(1_000, 500);
        }
    }

    private sealed class JournalRawIngressHolds(string root) : IRawIngressRetentionHolds
    {
        private readonly SqliteRawCaptureJournal _journal = new(
            Path.Combine(root, "journal", "raw-ingress.db"), 1);

        public async ValueTask<IReadOnlyList<RawIngressRetentionHold>> GetRetentionHoldsAsync(
            string storageRoot,
            CancellationToken cancellationToken)
            => await _journal.ReadRetentionHoldsAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class StubConfigurationAccessor : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => false;

        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<CameraModuleConfig>(new NotSupportedException());
    }
}
