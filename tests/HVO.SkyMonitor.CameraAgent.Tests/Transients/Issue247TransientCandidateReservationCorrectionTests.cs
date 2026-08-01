using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class Issue247TransientCandidateReservationCorrectionTests
{
    [TestMethod]
    public async Task ReserveAsync_BlockedPhysicalReadDoesNotHoldWriterAndStillFencesLifecycleMutation()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("This test requires Linux FIFO semantics.");
        }
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        var sidecarPath = Path.ChangeExtension(fixture.ResolvePayload(source), ".json");
        var sidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
        File.Delete(sidecarPath);
        await CreateFifoAsync(sidecarPath).ConfigureAwait(false);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fifoWriter = Task.Run(async () =>
        {
            using var stream = new FileStream(
                sidecarPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            connected.TrySetResult();
            await release.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            await stream.WriteAsync(sidecar, cancellation.Token).ConfigureAwait(false);
        }, cancellation.Token);
        var reservationTask = fixture.Journal.ReserveAsync(reservation, cancellation.Token).AsTask();
        Task? lifecycleMutation = null;
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var unrelatedWriter = fixture.ExecuteAsync(
                "UPDATE capture_control_state SET version = version + 1 WHERE state_key = 1;");
            await unrelatedWriter.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            lifecycleMutation = Task.Run(async () =>
            {
                var gate = RawIngressLifecycleLock.ForRoot(fixture.Root);
                await gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    File.Delete(fixture.ResolvePayload(source));
                }
                finally
                {
                    gate.Release();
                }
            }, cancellation.Token);
            await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
            Assert.IsFalse(lifecycleMutation.IsCompleted, "Retention-style mutation must remain lifecycle-fenced.");

            release.TrySetResult();
            var result = await reservationTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(TransientCandidateReservationDisposition.Created, result.Disposition);
            await lifecycleMutation.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
            cancellation.CancelAfter(TimeSpan.FromSeconds(2));
            await DrainAsync(fifoWriter, reservationTask, lifecycleMutation).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow((int)TransientCandidateFaultPoint.BeforeReservationValidation)]
    [DataRow((int)TransientCandidateFaultPoint.AfterReservationValidation)]
    [DataRow((int)TransientCandidateFaultPoint.BeforeReservationCommit)]
    [DataRow((int)TransientCandidateFaultPoint.AfterReservationCommit)]
    public async Task ReserveAsync_FaultBoundaryFreshReconstructionConverges(
        int faultPointValue)
    {
        var faultPoint = (TransientCandidateFaultPoint)faultPointValue;
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var sources = new TransientSourceEvidenceReferenceV1[5];
        for (var index = 0; index < sources.Length; index++)
        {
            sources[index] = await fixture.AddRawSourceAsync(index + 1, 100).ConfigureAwait(false);
            await StageSourceAsync(fixture.Root, sources[index]).ConfigureAwait(false);
        }
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(sources);
        var expectedReservationHash = ComputeReservationIdentity(reservation);
        using (var interrupted = fixture.ReconstructJournal(new ThrowingFaultInjector(faultPoint)))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await interrupted.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
        }

        using var reconstructed = fixture.ReconstructJournal();
        var recovered = await reconstructed.Journal.ReserveAsync(
            reservation, CancellationToken.None).ConfigureAwait(false);
        var committedBeforeFailure = faultPoint == TransientCandidateFaultPoint.AfterReservationCommit;
        Assert.AreEqual(
            committedBeforeFailure
                ? TransientCandidateReservationDisposition.Existing
                : TransientCandidateReservationDisposition.Created,
            recovered.Disposition);
        Assert.AreEqual(reservation.CandidateId, recovered.Entry.CandidateId);
        Assert.AreEqual(reservation.EventId, recovered.Entry.EventId);
        Assert.AreEqual(reservation.AgentId, recovered.Entry.AgentId);
        Assert.AreEqual(TransientEventState.Pending, recovered.Entry.State);
        Assert.AreEqual(TransientCandidateWorkflowPhase.IdentityAllocated, recovered.Entry.Phase);
        Assert.AreEqual(expectedReservationHash, recovered.Entry.ReservationIdentitySha256);
        CollectionAssert.AreEqual(sources, recovered.Entry.Sources.ToArray());
        var durable = await reconstructed.Journal.ReadAsync(
            reservation.CandidateId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(durable);
        Assert.AreEqual(reservation.CandidateId, durable.CandidateId);
        Assert.AreEqual(reservation.EventId, durable.EventId);
        Assert.AreEqual(expectedReservationHash, durable.ReservationIdentitySha256);
        Assert.AreEqual(TransientEventState.Pending, durable.State);
        Assert.AreEqual(TransientCandidateWorkflowPhase.IdentityAllocated, durable.Phase);
        CollectionAssert.AreEqual(sources, durable.Sources.ToArray());
        Assert.AreEqual(reservation.EventId.ToString("N"), await fixture.ScalarStringAsync(
            $"SELECT event_id FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(expectedReservationHash, await fixture.ScalarStringAsync(
            $"SELECT reservation_identity_sha256 FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual("pending", await fixture.ScalarStringAsync(
            $"SELECT state FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual("reserved", await fixture.ScalarStringAsync(
            $"SELECT phase FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
        Assert.AreEqual(5L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_sources;").ConfigureAwait(false));
        Assert.AreEqual(5L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
        Assert.AreEqual(5L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_capture_work WHERE state = 'candidate_persisted';")
            .ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
        var backlog = await reconstructed.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, backlog.ActiveCount);
        Assert.AreEqual(500L, backlog.HeldSourceBytes);
        Assert.AreEqual(0, backlog.PressureLevel);
        foreach (var source in sources)
        {
            Assert.IsTrue(File.Exists(fixture.ResolvePayload(source)));
            Assert.IsTrue(File.Exists(Path.ChangeExtension(fixture.ResolvePayload(source), ".json")));
        }
    }

    [TestMethod]
    public async Task ReserveAsync_EmitsOnlyBoundedTransactionActivitiesWithoutTags()
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var validSource = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var corruptSource = await fixture.AddRawSourceAsync(2, 100).ConfigureAwait(false);
        var invalidSource = await fixture.AddRawSourceAsync(3, 100).ConfigureAwait(false);
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TransientWorkerTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        await fixture.Journal.ReserveAsync(
            SqliteTransientCandidateJournalTests.Fixture.CreateReservation(validSource),
            CancellationToken.None).ConfigureAwait(false);
        await File.AppendAllTextAsync(fixture.ResolvePayload(corruptSource), "corrupt").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Journal.ReserveAsync(
                SqliteTransientCandidateJournalTests.Fixture.CreateReservation(corruptSource),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.Journal.ReserveAsync(
                SqliteTransientCandidateJournalTests.Fixture.CreateReservation(invalidSource, invalidSource),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.HasCount(5, activities);
        Assert.HasCount(2, activities.Where(activity =>
            activity.OperationName == "transient-candidate.reserve.snapshot"));
        Assert.HasCount(3, activities.Where(activity =>
            activity.OperationName == "transient-candidate.reserve.immediate"));
        Assert.IsTrue(activities.All(activity => activity.OperationName is
            "transient-candidate.reserve.snapshot" or "transient-candidate.reserve.immediate"));
        foreach (var activity in activities)
        {
            Assert.IsGreaterThan(TimeSpan.Zero, activity.Duration);
            Assert.IsEmpty(activity.TagObjects.ToArray());
            Assert.IsEmpty(activity.Events.ToArray());
        }
    }

    [TestMethod]
    [DataRow("raw-row-identity")]
    [DataRow("raw-row-disappearance")]
    [DataRow("state")]
    [DataRow("capture")]
    [DataRow("artifact")]
    [DataRow("agent")]
    [DataRow("sequence")]
    [DataRow("payload-path")]
    [DataRow("sidecar-path")]
    [DataRow("length")]
    [DataRow("payload-checksum")]
    [DataRow("descriptor-checksum")]
    [DataRow("manifest-checksum")]
    [DataRow("manifest-version")]
    [DataRow("manifest-bytes")]
    public async Task ReserveAsync_AfterValidationRawFactRaceNeverCommitsStaleSource(
        string mutation)
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        var injector = new CallbackFaultInjector(
            TransientCandidateFaultPoint.AfterReservationValidation,
            () => MutateRawFactAsync(fixture, source, mutation).GetAwaiter().GetResult());
        using var reconstructed = fixture.ReconstructJournal(injector);

        Exception? failure = null;
        TransientCandidateReservationResult? result = null;
        try
        {
            result = await reconstructed.Journal.ReserveAsync(
                reservation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or TransientCandidateIdentityConflictException)
        {
            failure = exception;
        }

        var converges = mutation is "raw-row-identity" or "capture" or "sequence" or
            "payload-path" or "sidecar-path" or "manifest-bytes";
        if (converges)
        {
            Assert.IsGreaterThanOrEqualTo(2, injector.InvocationCount, mutation);
            Assert.IsNull(failure);
            Assert.AreEqual(TransientCandidateReservationDisposition.Created, result!.Disposition);
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidate_sources s JOIN raw_captures r ON r.raw_capture_row_id = s.raw_capture_row_id WHERE r.retention_hold = 1;")
                .ConfigureAwait(false));
            Assert.AreEqual(0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
        }
        else
        {
            Assert.IsNotNull(failure, mutation);
            Assert.AreEqual(0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false), mutation);
            Assert.AreEqual(0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidate_sources;").ConfigureAwait(false), mutation);
            Assert.AreEqual(0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM raw_captures WHERE retention_hold != 0;").ConfigureAwait(false), mutation);
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                $"SELECT COUNT(*) FROM transient_candidate_conflicts WHERE candidate_id = '{reservation.CandidateId:N}' AND reason = 'source-evidence-invalid';")
                .ConfigureAwait(false), mutation);
        }
    }

    [TestMethod]
    [DataRow("event")]
    [DataRow("candidate")]
    [DataRow("conflict")]
    public async Task ReserveAsync_AfterValidationIdentityRaceConvergesOrDurablyRejects(string mutation)
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        var injector = new CallbackFaultInjector(
            TransientCandidateFaultPoint.AfterReservationValidation,
            () => MutateIdentityAsync(fixture.Root, reservation, mutation).GetAwaiter().GetResult());
        using var reconstructed = fixture.ReconstructJournal(injector);

        if (mutation == "event")
        {
            var result = await reconstructed.Journal.ReserveAsync(
                reservation, CancellationToken.None).ConfigureAwait(false);
            Assert.IsGreaterThanOrEqualTo(2, injector.InvocationCount);
            Assert.AreEqual(TransientCandidateReservationDisposition.Created, result.Disposition);
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
            return;
        }

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await reconstructed.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_sources WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM raw_captures WHERE retention_hold != 0;").ConfigureAwait(false));
        var expectedReason = mutation == "candidate" ? "reservation-source-invalid" : "test-race";
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_conflicts WHERE candidate_id = '{reservation.CandidateId:N}' AND reason = '{expectedReason}';")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReserveAsync_AfterValidationSourceOrderRaceDurablyRejectsWithoutNewHoldMutation()
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var first = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var second = await fixture.AddRawSourceAsync(2, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(first, second);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var injector = new CallbackFaultInjector(
            TransientCandidateFaultPoint.AfterReservationValidation,
            () => fixture.ExecuteAsync($"""
                UPDATE transient_candidate_sources SET source_ordinal = source_ordinal + 10
                WHERE candidate_id = '{reservation.CandidateId:N}';
                UPDATE transient_candidate_sources SET source_ordinal = CASE source_ordinal WHEN 10 THEN 1 ELSE 0 END
                WHERE candidate_id = '{reservation.CandidateId:N}';
                """).GetAwaiter().GetResult());
        using var reconstructed = fixture.ReconstructJournal(injector);

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await reconstructed.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.IsGreaterThanOrEqualTo(2, injector.InvocationCount);
        Assert.AreEqual("quarantined", await fixture.ScalarStringAsync(
            $"SELECT phase FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(2L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_conflicts WHERE candidate_id = '{reservation.CandidateId:N}' AND reason = 'reservation-identity-mismatch';")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReserveAsync_AfterValidationHoldChangeIsFreshlyReconciledInImmediateTransaction()
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        var injector = new CallbackFaultInjector(
            TransientCandidateFaultPoint.AfterReservationValidation,
            () => fixture.ExecuteAsync("UPDATE raw_captures SET retention_hold = 1;").GetAwaiter().GetResult());
        using var reconstructed = fixture.ReconstructJournal(injector);

        var result = await reconstructed.Journal.ReserveAsync(
            reservation, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateReservationDisposition.Created, result.Disposition);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_sources;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReserveAsync_AfterValidationStagedWorkChangeUsesFreshCapacityAndTransitionFacts()
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        var artifact = source.Locator.Artifact.ArtifactId.ToString("N");
        var injector = new CallbackFaultInjector(
            TransientCandidateFaultPoint.AfterReservationValidation,
            () => ExecuteAsync(fixture.Root,
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
                FROM raw_captures r
                JOIN capture_lane_work w ON w.raw_capture_row_id = r.raw_capture_row_id
                WHERE r.raw_artifact_id = $artifact;
                """,
                ("$artifact", artifact)).GetAwaiter().GetResult());
        using var reconstructed = fixture.ReconstructJournal(injector);

        var result = await reconstructed.Journal.ReserveAsync(
            reservation, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateReservationDisposition.Created, result.Disposition);
        Assert.AreEqual("candidate_persisted", await fixture.ScalarStringAsync(
            "SELECT state FROM transient_capture_work;").ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReserveAsync_AfterValidationCapacityConflictChangeRejectsBeforeCandidateMutation()
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        var injector = new CallbackFaultInjector(
            TransientCandidateFaultPoint.AfterReservationValidation,
            () => ExecuteAsync(fixture.Root,
                "INSERT INTO transient_candidate_conflicts(candidate_id, event_id, reason, observed_unix_ms) VALUES ($candidate, $event, 'other-race', 1);",
                ("$candidate", Guid.NewGuid().ToString("N")),
                ("$event", Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult());
        using var reconstructed = fixture.ReconstructJournal(injector);

        await Assert.ThrowsExactlyAsync<TransientCandidateJournalCapacityException>(async () =>
            await reconstructed.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_sources WHERE candidate_id = '{reservation.CandidateId:N}';")
            .ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM raw_captures WHERE retention_hold != 0;").ConfigureAwait(false));
    }

    private static async Task MutateRawFactAsync(
        SqliteTransientCandidateJournalTests.Fixture fixture,
        TransientSourceEvidenceReferenceV1 source,
        string mutation)
    {
        var artifact = source.Locator.Artifact.ArtifactId.ToString("N");
        var payload = fixture.ResolvePayload(source);
        var sidecarPath = Path.ChangeExtension(payload, ".json");
        var sidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
        switch (mutation)
        {
            case "raw-row-identity":
                await ExecuteAsync(fixture.Root,
                    "UPDATE raw_captures SET raw_capture_row_id = raw_capture_row_id + 100 WHERE raw_artifact_id = $artifact;",
                    ("$artifact", artifact)).ConfigureAwait(false);
                break;
            case "raw-row-disappearance":
                await ExecuteAsync(fixture.Root,
                    "DELETE FROM raw_captures WHERE raw_artifact_id = $artifact;",
                    ("$artifact", artifact)).ConfigureAwait(false);
                break;
            case "state":
                await UpdateAsync(fixture.Root, artifact, "state", "missing_evidence").ConfigureAwait(false);
                break;
            case "capture":
                var captureManifest = CaptureContractJson.ParseManifest(sidecar).Document?.Manifest
                    ?? throw new InvalidDataException("Test source manifest is invalid.");
                var changedCaptureId = Guid.NewGuid();
                var changedCaptureManifest = captureManifest with
                {
                    Descriptor = captureManifest.Descriptor with
                    {
                        Capture = captureManifest.Descriptor.Capture with { CaptureId = changedCaptureId }
                    }
                };
                var changedCaptureBytes = CaptureContractJson.Serialize(changedCaptureManifest);
                await File.WriteAllBytesAsync(sidecarPath, changedCaptureBytes).ConfigureAwait(false);
                await ExecuteAsync(fixture.Root,
                    """
                    PRAGMA defer_foreign_keys = ON;
                    BEGIN IMMEDIATE;
                    UPDATE raw_captures
                    SET capture_id = $value, descriptor_sha256 = $descriptor,
                        manifest_json = $manifest, manifest_sha256 = $manifest_sha
                    WHERE raw_artifact_id = $artifact;
                    UPDATE raw_capture_assignments SET capture_id = $value WHERE raw_artifact_id = $artifact;
                    COMMIT;
                    """,
                    ("$value", changedCaptureId.ToString("N")),
                    ("$descriptor", CaptureContractJson.ComputeDescriptorSha256(changedCaptureManifest.Descriptor)),
                    ("$manifest", changedCaptureBytes),
                    ("$manifest_sha", CaptureContractJson.ComputeManifestSha256(changedCaptureBytes)),
                    ("$artifact", artifact)).ConfigureAwait(false);
                break;
            case "artifact":
                await UpdateAsync(fixture.Root, artifact, "raw_artifact_id", Guid.NewGuid().ToString("N")).ConfigureAwait(false);
                break;
            case "agent":
                await UpdateAsync(fixture.Root, artifact, "agent_id", "different-agent").ConfigureAwait(false);
                break;
            case "sequence":
                var sequenceManifest = CaptureContractJson.ParseManifest(sidecar).Document?.Manifest
                    ?? throw new InvalidDataException("Test source manifest is invalid.");
                var changedSequenceManifest = sequenceManifest with
                {
                    Descriptor = sequenceManifest.Descriptor with
                    {
                        Capture = sequenceManifest.Descriptor.Capture with
                        {
                            CaptureSequence = sequenceManifest.Descriptor.Capture.CaptureSequence + 100
                        }
                    }
                };
                var changedSequenceBytes = CaptureContractJson.Serialize(changedSequenceManifest);
                await File.WriteAllBytesAsync(sidecarPath, changedSequenceBytes).ConfigureAwait(false);
                await ExecuteAsync(fixture.Root,
                    """
                    BEGIN IMMEDIATE;
                    UPDATE raw_captures
                    SET capture_sequence = capture_sequence + 100, descriptor_sha256 = $descriptor,
                        manifest_json = $manifest, manifest_sha256 = $manifest_sha
                    WHERE raw_artifact_id = $artifact;
                    UPDATE raw_capture_assignments SET capture_sequence = capture_sequence + 100
                    WHERE raw_artifact_id = $artifact;
                    COMMIT;
                    """,
                    ("$descriptor", CaptureContractJson.ComputeDescriptorSha256(changedSequenceManifest.Descriptor)),
                    ("$manifest", changedSequenceBytes),
                    ("$manifest_sha", CaptureContractJson.ComputeManifestSha256(changedSequenceBytes)),
                    ("$artifact", artifact)).ConfigureAwait(false);
                break;
            case "payload-path":
                var payloadManifest = CaptureContractJson.ParseManifest(sidecar).Document?.Manifest
                    ?? throw new InvalidDataException("Test source manifest is invalid.");
                const string changedPayloadRelative = "frames/changed-payload.bin";
                var changedPayloadPath = Path.Combine(fixture.Root, "frames", "changed-payload.bin");
                File.Move(payload, changedPayloadPath);
                var changedPayloadManifest = payloadManifest with { RelativeArtifactPath = changedPayloadRelative };
                var changedPayloadBytes = CaptureContractJson.Serialize(changedPayloadManifest);
                await File.WriteAllBytesAsync(sidecarPath, changedPayloadBytes).ConfigureAwait(false);
                await ExecuteAsync(fixture.Root,
                    """
                    UPDATE raw_captures
                    SET payload_relative_path = $path, manifest_json = $manifest, manifest_sha256 = $manifest_sha
                    WHERE raw_artifact_id = $artifact;
                    """,
                    ("$path", changedPayloadRelative),
                    ("$manifest", changedPayloadBytes),
                    ("$manifest_sha", CaptureContractJson.ComputeManifestSha256(changedPayloadBytes)),
                    ("$artifact", artifact)).ConfigureAwait(false);
                break;
            case "sidecar-path":
                const string changedSidecarRelative = "frames/changed-sidecar.json";
                File.Move(sidecarPath, Path.Combine(fixture.Root, "frames", "changed-sidecar.json"));
                await UpdateAsync(fixture.Root, artifact, "sidecar_relative_path", changedSidecarRelative).ConfigureAwait(false);
                break;
            case "length":
                await ExecuteAsync(fixture.Root,
                    "UPDATE raw_captures SET payload_length = payload_length + 1 WHERE raw_artifact_id = $artifact;",
                    ("$artifact", artifact)).ConfigureAwait(false);
                break;
            case "payload-checksum":
                await UpdateAsync(fixture.Root, artifact, "payload_sha256", new string('0', 64)).ConfigureAwait(false);
                break;
            case "descriptor-checksum":
                await UpdateAsync(fixture.Root, artifact, "descriptor_sha256", new string('0', 64)).ConfigureAwait(false);
                break;
            case "manifest-checksum":
                await UpdateAsync(fixture.Root, artifact, "manifest_sha256", new string('0', 64)).ConfigureAwait(false);
                break;
            case "manifest-version":
                {
                    var node = JsonNode.Parse(sidecar)!.AsObject();
                    node["schemaVersion"] = "hvo-artifact-manifest-v999";
                    var changed = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
                    await File.WriteAllBytesAsync(sidecarPath, changed).ConfigureAwait(false);
                    await ExecuteAsync(fixture.Root,
                        "UPDATE raw_captures SET manifest_json = $json, manifest_sha256 = $sha WHERE raw_artifact_id = $artifact;",
                        ("$json", changed), ("$sha", CaptureContractJson.ComputeManifestSha256(changed)), ("$artifact", artifact))
                        .ConfigureAwait(false);
                    break;
                }
            case "manifest-bytes":
                {
                    byte[] changed = [.. sidecar, (byte)'\n'];
                    await File.WriteAllBytesAsync(sidecarPath, changed).ConfigureAwait(false);
                    await ExecuteAsync(fixture.Root,
                        "UPDATE raw_captures SET manifest_json = $json, manifest_sha256 = $sha WHERE raw_artifact_id = $artifact;",
                        ("$json", changed), ("$sha", CaptureContractJson.ComputeManifestSha256(changed)), ("$artifact", artifact))
                        .ConfigureAwait(false);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static Task StageSourceAsync(
        string root,
        TransientSourceEvidenceReferenceV1 source)
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
            FROM raw_captures r
            JOIN capture_lane_work w ON w.raw_capture_row_id = r.raw_capture_row_id
            WHERE r.raw_artifact_id = $artifact;
            """,
            ("$artifact", source.Locator.Artifact.ArtifactId.ToString("N")));

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

    private static async Task MutateIdentityAsync(
        string root,
        TransientCandidateReservation reservation,
        string mutation)
    {
        if (mutation == "conflict")
        {
            await ExecuteAsync(root,
                "INSERT INTO transient_candidate_conflicts(candidate_id, event_id, reason, observed_unix_ms) VALUES ($candidate, $event, 'test-race', 1);",
                ("$candidate", reservation.CandidateId.ToString("N")),
                ("$event", reservation.EventId.ToString("N"))).ConfigureAwait(false);
            return;
        }
        await ExecuteAsync(root,
            "INSERT INTO transient_event_identities(event_id, agent_id, created_unix_ms) VALUES ($event, $agent, 1);",
            ("$event", reservation.EventId.ToString("N")), ("$agent", reservation.AgentId)).ConfigureAwait(false);
        if (mutation == "candidate")
        {
            await ExecuteAsync(root,
                """
                INSERT INTO transient_candidates(
                    candidate_id, event_id, agent_id, mode, required, reservation_identity_sha256,
                    state, phase, source_hold_released, timeout_unix_ms, created_unix_ms, updated_unix_ms)
                VALUES ($candidate, $event, $agent, 'edge', 0, $identity,
                    'pending', 'reserved', 0, 2, 1, 1);
                """,
                ("$candidate", reservation.CandidateId.ToString("N")),
                ("$event", reservation.EventId.ToString("N")),
                ("$agent", reservation.AgentId),
                ("$identity", new string('0', 64))).ConfigureAwait(false);
        }
    }

    private static Task UpdateAsync(string root, string artifact, string column, object value)
    {
        if (column is not ("state" or "capture_id" or "raw_artifact_id" or "agent_id" or
            "payload_relative_path" or "sidecar_relative_path" or "payload_sha256" or
            "descriptor_sha256" or "manifest_sha256"))
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }
        return ExecuteAsync(root,
            $"UPDATE raw_captures SET {column} = $value WHERE raw_artifact_id = $artifact;",
            ("$value", value), ("$artifact", artifact));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Issue-specific test callers provide only internal SQL and parameterize values.")]
    private static async Task ExecuteAsync(
        string root,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task CreateFifoAsync(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("mkfifo", path)
        {
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Unable to start mkfifo.");
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mkfifo failed: {error}");
        }
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

    private sealed class ThrowingFaultInjector(TransientCandidateFaultPoint point) : ITransientCandidateFaultInjector
    {
        private int _injected;

        public void Inject(TransientCandidateFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _injected, 1) == 0)
            {
                throw new InvalidOperationException("Injected issue #247 fault.");
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
}
