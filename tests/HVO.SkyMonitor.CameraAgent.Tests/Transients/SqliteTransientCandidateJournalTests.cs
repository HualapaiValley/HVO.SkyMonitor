using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class SqliteTransientCandidateJournalTests
{
    [TestMethod]
    public async Task RuntimeStore_EmptyInitializationCreatesCanonicalSchemaIdempotently()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var first = fixture.CreateRuntimeStore();
        var second = fixture.CreateRuntimeStore();

        await Task.WhenAll(
            first.InitializeAsync(CancellationToken.None).AsTask(),
            second.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);

        var databasePath = Path.Combine(fixture.Root, "journal", "raw-ingress.db");
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_schema
            WHERE name LIKE 'transient_worker_%' OR name LIKE 'ix_transient_worker_%'
            ORDER BY name;
            """;
        var names = new List<string>();
        using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                names.Add(reader.GetString(0));
            }
        }
        string[] expectedNames =
        [
            "ix_transient_worker_candidates_pending",
            "ix_transient_worker_frames_ready",
            "transient_worker_candidates",
            "transient_worker_frames"
        ];
        CollectionAssert.AreEqual(expectedNames, names);
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('transient_worker_frames') WHERE name = 'causal_succeeded';";
        Assert.AreEqual(1L, await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RuntimeStore_LegacyFrameSchemaIsRejectedWithoutRewrite()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var databasePath = Path.Combine(fixture.Root, "journal", "raw-ingress.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE transient_worker_frames (
                    raw_capture_row_id INTEGER PRIMARY KEY,
                    state TEXT NOT NULL CHECK (state IN ('queued', 'history', 'retry_wait', 'completed', 'quarantined')),
                    attempt_count INTEGER NOT NULL CHECK (attempt_count >= 0),
                    available_unix_ms INTEGER NOT NULL,
                    failure_reason TEXT,
                    created_unix_ms INTEGER NOT NULL,
                    updated_unix_ms INTEGER NOT NULL
                ) STRICT;
                CREATE INDEX ix_transient_worker_frames_ready
                    ON transient_worker_frames(state, available_unix_ms, raw_capture_row_id);
                INSERT INTO transient_worker_frames(
                    raw_capture_row_id, state, attempt_count, available_unix_ms,
                    failure_reason, created_unix_ms, updated_unix_ms)
                VALUES(777, 'queued', 2, 10, 'retained', 1, 2);
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();
        var filesBefore = ReadDatabaseFiles(databasePath);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.CreateRuntimeStore().InitializeAsync(CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "state-disposition", StringComparison.Ordinal);
        AssertDatabaseFilesUnchanged(filesBefore, ReadDatabaseFiles(databasePath));
        using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync().ConfigureAwait(false);
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM transient_worker_frames WHERE raw_capture_row_id = 777 AND attempt_count = 2;";
        Assert.AreEqual(1L, await read.ExecuteScalarAsync().ConfigureAwait(false));
        read.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'transient_worker_candidates';";
        Assert.AreEqual(0L, await read.ExecuteScalarAsync().ConfigureAwait(false));
        read.CommandText = "SELECT COUNT(*) FROM pragma_table_info('transient_worker_frames') WHERE name = 'causal_succeeded';";
        Assert.AreEqual(0L, await read.ExecuteScalarAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RuntimeStore_PartialOrExtendedSchemaIsRejected()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var store = fixture.CreateRuntimeStore();
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var databasePath = Path.Combine(fixture.Root, "journal", "raw-ingress.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER unexpected_transient_worker_trigger
                AFTER INSERT ON transient_worker_frames
                BEGIN
                    SELECT 1;
                END;
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();
        var filesBefore = ReadDatabaseFiles(databasePath);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.CreateRuntimeStore().InitializeAsync(CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "state-disposition", StringComparison.Ordinal);
        AssertDatabaseFilesUnchanged(filesBefore, ReadDatabaseFiles(databasePath));
        using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync().ConfigureAwait(false);
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'unexpected_transient_worker_trigger';";
        Assert.AreEqual(1L, await read.ExecuteScalarAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RuntimeStore_RestartRecoversStagedWorkAndHistoryIsNotReclaimed()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var context = await fixture.CreateLaneContextAsync(1, 100).ConfigureAwait(false);
        var handler = new TransientCaptureLaneHandler(fixture.Journal);
        Assert.AreEqual(
            CaptureLaneHandlerOutcome.Completed,
            (await handler.HandleAsync(context, CancellationToken.None).ConfigureAwait(false)).Outcome);
        var first = fixture.CreateRuntimeStore();
        await first.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        var discovered = await first.ReadNextAsync(CancellationToken.None).ConfigureAwait(false);
        var restarted = fixture.CreateRuntimeStore();
        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var recovered = await restarted.ReadNextAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(discovered);
        Assert.IsNotNull(recovered);
        Assert.AreEqual(discovered.RawCaptureRowId, recovered.RawCaptureRowId);
        Assert.AreEqual(discovered.ArtifactId, recovered.ArtifactId);
        await restarted.MarkCausalCompletionAsync(
            recovered.RawCaptureRowId, "test-history", succeeded: true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(await fixture.CreateRuntimeStore().ReadNextAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RuntimeStore_MissingImmutableEvidenceFailsWithoutLosingDurableWork()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var context = await fixture.CreateLaneContextAsync(1, 100).ConfigureAwait(false);
        var handler = new TransientCaptureLaneHandler(fixture.Journal);
        _ = await handler.HandleAsync(context, CancellationToken.None).ConfigureAwait(false);
        var store = fixture.CreateRuntimeStore();
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var work = await store.ReadNextAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(work);
        File.Delete(context.RawCapture.StoredFrame.AbsolutePath);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
            await store.LoadWindowAsync("agent", 1, [0], CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.IsNotNull(await fixture.CreateRuntimeStore().ReadNextAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RuntimeStore_IdentityBatchRollsBackCompletelyBeforeCommit()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var context = await fixture.CreateLaneContextAsync(1, 100).ConfigureAwait(false);
        _ = await new TransientCaptureLaneHandler(fixture.Journal)
            .HandleAsync(context, CancellationToken.None).ConfigureAwait(false);
        var store = fixture.CreateRuntimeStore(new ThrowingRuntimeFaultInjector(
            TransientRuntimeFaultPoint.BeforeIdentityBatchCommit));
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var frame = await store.ReadNextAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(frame);
        var allocations = CreateRuntimeAllocations(frame.RawCaptureRowId, 2);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.AllocateBatchAsync(frame.RawCaptureRowId, allocations, CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_worker_candidates;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RuntimeStore_IdentityBatchIsCompleteAndIdempotentAfterCommitInterruption()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var context = await fixture.CreateLaneContextAsync(1, 100).ConfigureAwait(false);
        _ = await new TransientCaptureLaneHandler(fixture.Journal)
            .HandleAsync(context, CancellationToken.None).ConfigureAwait(false);
        var interrupted = fixture.CreateRuntimeStore(new ThrowingRuntimeFaultInjector(
            TransientRuntimeFaultPoint.AfterIdentityBatchCommit));
        await interrupted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var frame = await interrupted.ReadNextAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(frame);
        var allocations = CreateRuntimeAllocations(frame.RawCaptureRowId, 2);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await interrupted.AllocateBatchAsync(frame.RawCaptureRowId, allocations, CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
        var recovered = await fixture.CreateRuntimeStore().AllocateBatchAsync(
            frame.RawCaptureRowId,
            CreateRuntimeAllocations(frame.RawCaptureRowId, 2),
            CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(2, recovered);
        CollectionAssert.AreEqual(
            allocations.Select(static value => value.CandidateId).ToArray(),
            recovered.Select(static value => value.CandidateId).ToArray());
    }

    [TestMethod]
    public async Task RuntimeStore_QuarantineRetainsPressureUntilExplicitRelease()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var context = await fixture.CreateLaneContextAsync(1, 100).ConfigureAwait(false);
        _ = await new TransientCaptureLaneHandler(fixture.Journal)
            .HandleAsync(context, CancellationToken.None).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            "UPDATE capture_lane_work SET state = 'completed', lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL;")
            .ConfigureAwait(false);
        var store = fixture.CreateRuntimeStore();
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var frame = await store.ReadNextAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(frame);

        await store.QuarantineAsync(
            frame.RawCaptureRowId, "test-quarantine", CancellationToken.None).ConfigureAwait(false);
        var quarantined = await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, quarantined.ActiveCount);
        Assert.AreEqual(100L, quarantined.HeldSourceBytes);
        Assert.AreEqual(2, quarantined.PressureLevel);

        var restarted = fixture.CreateRuntimeStore();
        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var restartedBacklog = await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, restartedBacklog.ActiveCount);
        Assert.AreEqual(100L, restartedBacklog.HeldSourceBytes);
        var quarantine = (await ((ITransientRuntimeManagement)restarted)
            .ReadQuarantinePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single();
        var target = new TransientRuntimeOperationTarget(
            quarantine.RawCaptureRowId, quarantine.LaneWorkId, quarantine.OuterLaneWorkId,
            quarantine.AgentId, quarantine.CaptureSequence, quarantine.CaptureId, quarantine.ArtifactId,
            quarantine.ManifestSha256, quarantine.PayloadSha256, quarantine.ProcessingProfileSha256, quarantine.Mode,
            quarantine.Required, quarantine.OuterLaneState, quarantine.WorkState, quarantine.FrameState,
            quarantine.FailureReason, quarantine.OuterLaneUpdatedUtc, quarantine.WorkUpdatedUtc,
            quarantine.FrameUpdatedUtc,
            new TransientRuntimeExternalOwnershipEvidence("d331-0821084607", new string('D', 64), true));
        Assert.AreEqual(
            TransientRuntimeOperationDisposition.Applied,
            (await ((ITransientRuntimeManagement)restarted).AbandonQuarantinedCaptureAsync(
                target, "test-abandon", "owner-id", "operator-approved-loss", CancellationToken.None)
                .ConfigureAwait(false)).Disposition);
        var released = await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, released.ActiveCount);
        Assert.AreEqual(0L, released.HeldSourceBytes);
        Assert.AreEqual(0, released.PressureLevel);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual("abandoned", await fixture.ScalarStringAsync(
            "SELECT state FROM transient_worker_frames;").ConfigureAwait(false));
        Assert.AreEqual("abandoned", await fixture.ScalarStringAsync(
            "SELECT state FROM transient_capture_work;").ConfigureAwait(false));
        Assert.IsEmpty(await restarted.LoadWindowAsync("agent", 1, [0], CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(TransientOperatingMode.Edge)]
    [DataRow(TransientOperatingMode.Hybrid)]
    public async Task RuntimeStore_RestartReconcilesCompletedDeliveryCommit(TransientOperatingMode mode)
    {
        using var fixture = await Fixture.CreateAsync(mode: mode).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("Provisional", await fixture.ScalarStringAsync(
            "SELECT candidate_state FROM transient_candidates;").ConfigureAwait(false));
        if (mode == TransientOperatingMode.Edge)
        {
            await fixture.Journal.PersistFinalizationAsync(
                reservation.CandidateId,
                reservation.EventId,
                CreateFinalization(reservation, TransientEventState.NeedsReview),
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await fixture.Journal.PersistSubmissionAsync(
                reservation.CandidateId,
                reservation.EventId,
                CreateSubmission(reservation, candidate),
                CancellationToken.None).ConfigureAwait(false);
        }
        var rawCaptureRowId = await fixture.ScalarLongAsync(
            $"SELECT raw_capture_row_id FROM raw_captures WHERE raw_artifact_id = '{source.Locator.Artifact.ArtifactId:N}';")
            .ConfigureAwait(false);
        var allocation = CreateRuntimeAllocations(rawCaptureRowId, 1)[0] with
        {
            CandidateId = reservation.CandidateId,
            EventId = reservation.EventId
        };
        var first = fixture.CreateRuntimeStore();
        await first.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await first.AllocateBatchAsync(rawCaptureRowId, [allocation], CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("pending", await fixture.ScalarStringAsync(
            "SELECT state FROM transient_worker_candidates;").ConfigureAwait(false));

        await fixture.CreateRuntimeStore().InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("completed", await fixture.ScalarStringAsync(
            "SELECT state FROM transient_worker_candidates;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RuntimeStore_NormalDiscoveryReconcilesPostCommitCompletionFailureInProcess()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        await fixture.Journal.PersistFinalizationAsync(
            reservation.CandidateId,
            reservation.EventId,
            CreateFinalization(reservation, TransientEventState.NeedsReview),
            CancellationToken.None).ConfigureAwait(false);
        var rawCaptureRowId = await fixture.ScalarLongAsync(
            $"SELECT raw_capture_row_id FROM raw_captures WHERE raw_artifact_id = '{source.Locator.Artifact.ArtifactId:N}';")
            .ConfigureAwait(false);
        var allocation = CreateRuntimeAllocations(rawCaptureRowId, 1)[0] with
        {
            CandidateId = reservation.CandidateId,
            EventId = reservation.EventId
        };
        var store = fixture.CreateRuntimeStore(new ThrowingRuntimeFaultInjector(
            TransientRuntimeFaultPoint.BeforeRuntimeCompletionCommit));
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await store.AllocateBatchAsync(rawCaptureRowId, [allocation], CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.MarkCandidateCompletedAsync(reservation.CandidateId, CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual("pending", await fixture.ScalarStringAsync(
            "SELECT state FROM transient_worker_candidates;").ConfigureAwait(false));

        Assert.IsEmpty(await store.ReadPendingCandidatesAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("completed", await fixture.ScalarStringAsync(
            "SELECT state FROM transient_worker_candidates;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task AdjacentAssociation_RequiresMeasuredTimeEndpointAndAxisAgreement()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var first = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var second = await fixture.AddRawSourceAsync(2, 100).ConfigureAwait(false);
        second = second with
        {
            ObservationStartedUtc = first.ObservationStartedUtc.AddSeconds(2),
            ObservationEndedUtc = first.ObservationEndedUtc.AddSeconds(2)
        };
        var reservation = Fixture.CreateReservation(first);
        var previous = CreateCandidate(reservation, TransientCandidateState.Provisional) with
        {
            Geometry = new TransientGeometryV1(
                first.EvidenceId,
                200,
                200,
                new TransientBoundingRegionV1(1, 1, 10, 2),
                [new TransientPointV1(1, 1), new TransientPointV1(11, 1)])
        };
        var current = previous with
        {
            CandidateId = Guid.NewGuid(),
            CenterEvidenceId = second.EvidenceId,
            ContextSources = [second]
        };
        var options = new TransientCandidateAssociationOptions();

        Assert.IsTrue(TransientWorkerService.AssociationMatches(previous, current, options));
        Assert.IsFalse(TransientWorkerService.GeometryMatches(
            previous,
            current with
            {
                Geometry = current.Geometry! with
                {
                    Polyline = [new TransientPointV1(100, 100), new TransientPointV1(100, 110)]
                }
            },
            options));
    }

    [TestMethod]
    public async Task ReserveAsync_IsIdempotentAndRejectsConflictingIdentityReuse()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);

        var created = await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var duplicate = await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateReservationDisposition.Created, created.Disposition);
        Assert.AreEqual(TransientCandidateReservationDisposition.Existing, duplicate.Disposition);
        Assert.AreEqual(created.Entry.CandidateId, duplicate.Entry.CandidateId);
        Assert.AreEqual(created.Entry.EventId, duplicate.Entry.EventId);
        Assert.AreEqual(created.Entry.State, duplicate.Entry.State);
        Assert.AreEqual(created.Entry.CreatedUtc, duplicate.Entry.CreatedUtc);
        CollectionAssert.AreEqual(reservation.Sources.ToArray(), duplicate.Entry.Sources.ToArray());
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_event_identities;").ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidate_sources;").ConfigureAwait(false));

        var conflicting = reservation with { EventId = Guid.NewGuid() };
        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.Journal.ReserveAsync(conflicting, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var otherSource = await fixture.AddRawSourceAsync(2, 100).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.Journal.ReserveAsync(
                reservation with { Sources = [otherSource] }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.Journal.ReserveAsync(
                new TransientCandidateReservation(
                    Guid.NewGuid(), reservation.EventId, "other-agent", [source]),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(
            1L,
            await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
        Assert.AreEqual(
            "quarantined",
            await fixture.ScalarStringAsync(
                "SELECT phase FROM transient_candidates WHERE candidate_id = '" + reservation.CandidateId.ToString("N") + "';")
                .ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(TransientOperatingMode.Off)]
    [DataRow(TransientOperatingMode.Central)]
    public async Task DisabledEdgeModes_RejectReservationAndRegisterNoLane(TransientOperatingMode mode)
    {
        using var fixture = await Fixture.CreateAsync(mode: mode).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await fixture.Journal.ReserveAsync(
                Fixture.CreateReservation(source), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(
            0L,
            await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM capture_lane_definitions WHERE lane_name = 'transient';").ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("wrong-agent", "source-evidence-invalid")]
    [DataRow("wrong-variant", "source-evidence-invalid")]
    [DataRow("wrong-role", "source-evidence-invalid")]
    [DataRow("wrong-recipe", "source-evidence-invalid")]
    [DataRow("wrong-start", "source-evidence-invalid")]
    [DataRow("wrong-end", "source-evidence-invalid")]
    [DataRow("duplicate-evidence", "source-identity-conflict")]
    public async Task SourceIdentityConflicts_AreDurablyQuarantined(string target, string expectedReason)
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        reservation = target switch
        {
            "wrong-agent" => reservation with { AgentId = "different-agent" },
            "wrong-variant" => reservation with
            {
                Sources =
                [
                    source with
                    {
                        Locator = source.Locator with
                        {
                            Artifact = source.Locator.Artifact with { Variant = "different-variant" }
                        }
                    }
                ]
            },
            "wrong-role" => reservation with
            {
                Sources =
                [
                    source with
                    {
                        Locator = source.Locator with
                        {
                            Artifact = source.Locator.Artifact with { Role = FrameArtifactRole.Calibrated }
                        }
                    }
                ]
            },
            "wrong-recipe" => reservation with
            {
                Sources =
                [
                    source with
                    {
                        Locator = source.Locator with
                        {
                            Artifact = source.Locator.Artifact with
                            {
                                RecipeIdentitySha256 = new string('F', 64)
                            }
                        }
                    }
                ]
            },
            "wrong-start" => reservation with
            {
                Sources = [source with { ObservationStartedUtc = source.ObservationStartedUtc.AddTicks(1) }]
            },
            "wrong-end" => reservation with
            {
                Sources = [source with { ObservationEndedUtc = source.ObservationEndedUtc.AddTicks(-1) }]
            },
            "duplicate-evidence" => reservation with { Sources = [source, source] },
            _ => throw new InvalidOperationException("Unsupported source conflict fixture.")
        };

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.Journal.ReserveAsync(
                reservation, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(
            1L,
            await fixture.ScalarLongAsync(
                $"SELECT COUNT(*) FROM transient_candidate_conflicts WHERE reason = '{expectedReason}';")
                .ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Restart_RestoresCanonicalCandidateAndEdgeFinalizationReleasesHolds()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var first = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var second = await fixture.AddRawSourceAsync(2, 200).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(first, second);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);

        var restarted = fixture.CreateJournal();
        var recovered = await restarted.ReadAsync(reservation.CandidateId, CancellationToken.None).ConfigureAwait(false);
        var resumable = await restarted.ReadResumableAsync(10, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(recovered);
        Assert.AreEqual(TransientEventState.Provisional, recovered.State);
        Assert.AreEqual(TransientCandidateWorkflowPhase.CandidatePersisted, recovered.Phase);
        Assert.IsNotNull(recovered.CandidatePayloadSha256);
        Assert.AreEqual(candidate.CandidateId, recovered.Candidate!.CandidateId);
        CollectionAssert.AreEqual(candidate.ContextSources.ToArray(), recovered.Candidate.ContextSources.ToArray());
        Assert.AreEqual(TimeSpan.FromMinutes(10), recovered.TimeoutUtc - recovered.CreatedUtc);
        Assert.HasCount(1, resumable);
        Assert.AreEqual(reservation.CandidateId, resumable[0].CandidateId);
        CollectionAssert.AreEqual(reservation.Sources.ToArray(), recovered.Sources.ToArray());
        Assert.AreEqual(2L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));

        var receipt = CreateFinalization(reservation, TransientEventState.NeedsReview);
        var terminal = await restarted.PersistFinalizationAsync(
            reservation.CandidateId,
            reservation.EventId,
            receipt,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientEventState.NeedsReview, terminal.State);
        Assert.AreEqual(TransientCandidateWorkflowPhase.Finalized, terminal.Phase);
        Assert.AreEqual(receipt.ReceiptIdentitySha256, terminal.FinalizationReceiptIdentitySha256);
        Assert.IsTrue(terminal.SourceHoldReleased);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(TransientCandidateSubmissionDisposition.Accepted)]
    [DataRow(TransientCandidateSubmissionDisposition.Retired)]
    public async Task Hybrid_ReleasesHoldOnlyAfterMatchingDurableAcknowledgement(
        TransientCandidateSubmissionDisposition disposition)
    {
        using var fixture = await Fixture.CreateAsync(mode: TransientOperatingMode.Hybrid).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        var finalization = CreateFinalization(reservation, TransientEventState.NeedsReview);
        var finalized = await fixture.Journal.PersistFinalizationAsync(
            reservation.CandidateId, reservation.EventId, finalization, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(finalized.SourceHoldReleased);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));

        var submission = CreateSubmission(reservation, candidate);
        var pending = await fixture.Journal.PersistSubmissionAsync(
            reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(TransientCandidateWorkflowPhase.HandoffPending, pending.Phase);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        var acknowledgement = new TransientCandidateSubmissionAcknowledgementV1(
            disposition == TransientCandidateSubmissionDisposition.Retired
                ? TransientCandidateSubmissionAcknowledgementV1.RetirementSchemaVersion
                : TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
            reservation.CandidateId,
            reservation.EventId,
            submission.SubmissionIdentitySha256,
            DateTimeOffset.UtcNow,
            disposition);

        var acknowledged = await fixture.Journal.AcknowledgeAsync(
            reservation.CandidateId,
            reservation.EventId,
            acknowledgement,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateWorkflowPhase.Acknowledged, acknowledged.Phase);
        Assert.IsTrue(acknowledged.SourceHoldReleased);
        Assert.AreEqual(acknowledgement, acknowledged.Acknowledgement);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Hybrid_DeliveryPageUsesStableCursorAndRejectionQuarantineRetainsHoldAndPressure()
    {
        using var fixture = await Fixture.CreateAsync(mode: TransientOperatingMode.Hybrid).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var first = Fixture.CreateReservation(source);
        var second = Fixture.CreateReservation(source);
        var notReady = Fixture.CreateReservation(source);
        foreach (var reservation in new[] { first, second, notReady })
        {
            await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
            var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
            await fixture.Journal.PersistCandidateAsync(
                reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
            if (reservation != notReady)
            {
                await fixture.Journal.PersistSubmissionAsync(
                    reservation.CandidateId,
                    reservation.EventId,
                    CreateSubmission(reservation, candidate),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        var firstPage = await fixture.Journal.ReadPendingDeliveryPageAsync(
            after: null, maximumCount: 1, CancellationToken.None).ConfigureAwait(false);
        var secondPage = await fixture.Journal.ReadPendingDeliveryPageAsync(
            firstPage.NextCursor, maximumCount: 1, CancellationToken.None).ConfigureAwait(false);
        var end = await fixture.Journal.ReadPendingDeliveryPageAsync(
            secondPage.NextCursor, maximumCount: 1, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(1, firstPage.Entries);
        Assert.HasCount(1, secondPage.Entries);
        Assert.AreNotEqual(firstPage.Entries[0].CandidateId, secondPage.Entries[0].CandidateId);
        Assert.IsEmpty(end.Entries);
        Assert.IsTrue(new[] { first.CandidateId, second.CandidateId }.Contains(firstPage.Entries[0].CandidateId));
        Assert.IsTrue(new[] { first.CandidateId, second.CandidateId }.Contains(secondPage.Entries[0].CandidateId));
        var pendingAggregate = await fixture.Journal.ReadDeliveryAggregateAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(2L, pendingAggregate.PendingCount);
        Assert.AreEqual(0L, pendingAggregate.QuarantinedCount);
        Assert.IsNotNull(pendingAggregate.OldestPendingUtc);

        var selected = firstPage.Entries[0];
        var reason = new string('R', 160);
        var quarantined = await fixture.Journal.QuarantineDeliveryAsync(
            selected.CandidateId, selected.EventId, reason, CancellationToken.None).ConfigureAwait(false);
        var retried = await fixture.Journal.QuarantineDeliveryAsync(
            selected.CandidateId, selected.EventId, reason, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateWorkflowPhase.Quarantined, quarantined.Phase);
        Assert.AreEqual(new string('R', 128), quarantined.QuarantineReason);
        Assert.AreEqual(quarantined.CandidateId, retried.CandidateId);
        Assert.AreEqual(quarantined.QuarantineReason, retried.QuarantineReason);
        Assert.IsFalse(quarantined.SourceHoldReleased);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(2L, await fixture.ScalarLongAsync(
            "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'transient';").ConfigureAwait(false));
        var quarantinedAggregate = await fixture.Journal.ReadDeliveryAggregateAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(1L, quarantinedAggregate.PendingCount);
        Assert.AreEqual(1L, quarantinedAggregate.QuarantinedCount);
        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.Journal.QuarantineDeliveryAsync(
                selected.CandidateId, selected.EventId, "different", CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Hybrid_FinalizationAfterAcknowledgementDoesNotRearmHoldOrRegressDelivery()
    {
        using var fixture = await Fixture.CreateAsync(mode: TransientOperatingMode.Hybrid).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(reservation, candidate);
        await fixture.Journal.PersistSubmissionAsync(
            reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false);
        var acknowledgement = new TransientCandidateSubmissionAcknowledgementV1(
            TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
            reservation.CandidateId,
            reservation.EventId,
            submission.SubmissionIdentitySha256,
            DateTimeOffset.UtcNow,
            TransientCandidateSubmissionDisposition.Accepted);
        await fixture.Journal.AcknowledgeAsync(
            reservation.CandidateId, reservation.EventId, acknowledgement, CancellationToken.None).ConfigureAwait(false);

        var finalized = await fixture.Journal.PersistFinalizationAsync(
            reservation.CandidateId,
            reservation.EventId,
            CreateFinalization(reservation, TransientEventState.NeedsReview),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateWorkflowPhase.Acknowledged, finalized.Phase);
        Assert.IsTrue(finalized.SourceHoldReleased);
        Assert.IsNotNull(finalized.FinalizationReceipt);
        Assert.IsNotNull(finalized.Acknowledgement);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Hybrid_FinalizationWhileHandoffPendingPreservesDeliveryAndHold()
    {
        using var fixture = await Fixture.CreateAsync(mode: TransientOperatingMode.Hybrid).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(reservation, candidate);
        await fixture.Journal.PersistSubmissionAsync(
            reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false);

        var finalized = await fixture.Journal.PersistFinalizationAsync(
            reservation.CandidateId,
            reservation.EventId,
            CreateFinalization(reservation, TransientEventState.NeedsReview),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateWorkflowPhase.HandoffPending, finalized.Phase);
        Assert.IsFalse(finalized.SourceHoldReleased);
        Assert.IsNotNull(finalized.FinalizationReceipt);
        Assert.IsNotNull(finalized.Submission);
        Assert.IsNull(finalized.Acknowledgement);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Hybrid_SubmissionMismatchAcknowledgementIsDurablyQuarantinedAndKeepsHold()
    {
        using var fixture = await Fixture.CreateAsync(mode: TransientOperatingMode.Hybrid).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(reservation, candidate);
        await fixture.Journal.PersistSubmissionAsync(
            reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false);
        var conflicting = new TransientCandidateSubmissionAcknowledgementV1(
            TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
            reservation.CandidateId,
            reservation.EventId,
            new string('F', 64),
            DateTimeOffset.UtcNow,
            TransientCandidateSubmissionDisposition.Accepted);

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.Journal.AcknowledgeAsync(
                reservation.CandidateId, reservation.EventId, conflicting, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(
            "quarantined",
            await fixture.ScalarStringAsync("SELECT phase FROM transient_candidates;").ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Hybrid_SameIdentityAcknowledgementRetryConvergesOnFirstStoredAcknowledgement()
    {
        using var fixture = await Fixture.CreateAsync(mode: TransientOperatingMode.Hybrid).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(reservation, candidate);
        await fixture.Journal.PersistSubmissionAsync(
            reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false);
        var acknowledgement = new TransientCandidateSubmissionAcknowledgementV1(
            TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
            reservation.CandidateId,
            reservation.EventId,
            submission.SubmissionIdentitySha256,
            DateTimeOffset.UtcNow,
            TransientCandidateSubmissionDisposition.Accepted);
        var first = await fixture.Journal.AcknowledgeAsync(
            reservation.CandidateId, reservation.EventId, acknowledgement, CancellationToken.None).ConfigureAwait(false);

        var retried = await fixture.Journal.AcknowledgeAsync(
            reservation.CandidateId,
            reservation.EventId,
            acknowledgement with
            {
                ReceivedAtUtc = acknowledgement.ReceivedAtUtc.AddSeconds(1),
                Disposition = TransientCandidateSubmissionDisposition.Duplicate
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(first.Acknowledgement, retried.Acknowledgement);
        Assert.AreEqual(acknowledgement, retried.Acknowledgement);
        Assert.AreEqual("acknowledged", await fixture.ScalarStringAsync("SELECT phase FROM transient_candidates;").ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Hybrid_OwnershipMismatchAcknowledgementIsDurablyQuarantinedAndKeepsHold()
    {
        using var fixture = await Fixture.CreateAsync(mode: TransientOperatingMode.Hybrid).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(reservation, candidate);
        await fixture.Journal.PersistSubmissionAsync(
            reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false);
        var wrongEventId = Guid.NewGuid();
        var acknowledgement = new TransientCandidateSubmissionAcknowledgementV1(
            TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
            reservation.CandidateId,
            wrongEventId,
            submission.SubmissionIdentitySha256,
            DateTimeOffset.UtcNow,
            TransientCandidateSubmissionDisposition.Accepted);

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.Journal.AcknowledgeAsync(
                reservation.CandidateId, wrongEventId, acknowledgement, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(
            "quarantined",
            await fixture.ScalarStringAsync("SELECT phase FROM transient_candidates;").ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DeliveryContracts_RoundTripCanonicalIdentityAndRejectMutation()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        var submission = CreateSubmission(
            reservation, CreateCandidate(reservation, TransientCandidateState.Provisional));

        var payload = TransientCandidateDeliveryJson.Serialize(submission);
        var parsed = TransientCandidateDeliveryJson.ParseSubmission(payload);
        var mutated = submission with { RequestedProcessingProfileIdentity = "different-profile" };

        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
        Assert.AreEqual(submission.SubmissionIdentitySha256, parsed.Value!.SubmissionIdentitySha256);
        Assert.IsFalse(TransientCandidateDeliveryJson.Validate(mutated).IsValid);
    }

    [TestMethod]
    public async Task ActiveState_RefusesModeOrPolicyTransition()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        await fixture.Journal.ReserveAsync(
            Fixture.CreateReservation(source), CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await fixture.ReinitializeAsync(TransientOperatingMode.Off, required: false).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await fixture.ReinitializeAsync(TransientOperatingMode.Hybrid, required: false).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await fixture.ReinitializeAsync(TransientOperatingMode.Edge, required: true).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CanonicalSchema_InitializesOwnedTransientPolicyIdempotently()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);

        await fixture.ReinitializeAsync(TransientOperatingMode.Edge, required: false).ConfigureAwait(false);

        Assert.AreEqual(12L, await fixture.ScalarLongAsync("PRAGMA user_version;").ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM pragma_table_info('transient_candidates') WHERE name = 'candidate_state';")
            .ConfigureAwait(false));
        Assert.AreEqual(
            "edge",
            await fixture.ScalarStringAsync(
                "SELECT mode FROM transient_runtime_policy WHERE policy_key = 1;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PopulatedV9Schema_DoesNotBackfillCanonicalCandidateState()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId,
            reservation.EventId,
            CreateCandidate(reservation, TransientCandidateState.Provisional),
            CancellationToken.None).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            "ALTER TABLE transient_candidates DROP COLUMN candidate_state; PRAGMA user_version = 9;")
            .ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.ReinitializeAsync(TransientOperatingMode.Edge, required: false)).ConfigureAwait(false);

        Assert.AreEqual(9L, await fixture.ScalarLongAsync("PRAGMA user_version;").ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM pragma_table_info('transient_candidates') WHERE name = 'candidate_state';")
            .ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PopulatedV3Schema_RefusesActiveLegacyTransientLaneCollision()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        await fixture.SimulateV3Async(activeLegacyWork: true).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await fixture.ReinitializeAsync(TransientOperatingMode.Edge, required: false).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(3L, await fixture.ScalarLongAsync("PRAGMA user_version;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Reservation_WaitsForRawLifecycleLockAndRejectsCorruptPhysicalEvidence()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        var gate = RawIngressLifecycleLock.ForRoot(fixture.Root);
        await gate.WaitAsync().ConfigureAwait(false);
        Task<TransientCandidateReservationResult> pending;
        try
        {
            pending = fixture.Journal.ReserveAsync(reservation, CancellationToken.None).AsTask();
            Assert.IsFalse(pending.IsCompleted);
        }
        finally
        {
            gate.Release();
        }
        await pending.ConfigureAwait(false);

        var corruptSource = await fixture.AddRawSourceAsync(2, 100).ConfigureAwait(false);
        await File.AppendAllTextAsync(fixture.ResolvePayload(corruptSource), "corrupt").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Journal.ReserveAsync(
                Fixture.CreateReservation(corruptSource), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(
            1L,
            await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidate_conflicts WHERE reason = 'source-evidence-invalid';")
                .ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Reservation_AfterCommitFaultConvergesOnRestart()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        var interrupted = fixture.CreateJournal(new ThrowingFaultInjector(
            TransientCandidateFaultPoint.AfterReservationCommit));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await interrupted.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        var recovered = await fixture.CreateJournal().ReserveAsync(
            reservation, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(TransientCandidateReservationDisposition.Existing, recovered.Disposition);
        Assert.AreEqual(TransientCandidateWorkflowPhase.IdentityAllocated, recovered.Entry.Phase);
    }

    [TestMethod]
    public async Task Reservation_BeforeCommitFaultRollsBackAndRetryCreatesOnce()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        var interrupted = fixture.CreateJournal(new ThrowingFaultInjector(
            TransientCandidateFaultPoint.BeforeReservationCommit));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await interrupted.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));

        var recovered = await fixture.CreateJournal().ReserveAsync(
            reservation, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(TransientCandidateReservationDisposition.Created, recovered.Disposition);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CandidatePayload_AfterCommitFaultRecoversCanonicalPhase()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        var interrupted = fixture.CreateJournal(new ThrowingFaultInjector(
            TransientCandidateFaultPoint.AfterCandidateCommit));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await interrupted.PersistCandidateAsync(
                reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        var recovered = await fixture.CreateJournal().ReadAsync(
            reservation.CandidateId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(TransientCandidateWorkflowPhase.CandidatePersisted, recovered!.Phase);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(TransientContractJson.Serialize(candidate))),
            recovered.CandidatePayloadSha256);
    }

    [TestMethod]
    [DataRow((int)TransientCandidateFaultPoint.BeforeStageCommit)]
    [DataRow((int)TransientCandidateFaultPoint.AfterStageCommit)]
    [DataRow((int)TransientCandidateFaultPoint.BeforeReservationCommit)]
    [DataRow((int)TransientCandidateFaultPoint.AfterReservationCommit)]
    [DataRow((int)TransientCandidateFaultPoint.BeforeCandidateCommit)]
    [DataRow((int)TransientCandidateFaultPoint.AfterCandidateCommit)]
    [DataRow((int)TransientCandidateFaultPoint.BeforeFinalizationCommit)]
    [DataRow((int)TransientCandidateFaultPoint.AfterFinalizationCommit)]
    [DataRow((int)TransientCandidateFaultPoint.BeforeSubmissionCommit)]
    [DataRow((int)TransientCandidateFaultPoint.AfterSubmissionCommit)]
    [DataRow((int)TransientCandidateFaultPoint.BeforeAcknowledgementCommit)]
    [DataRow((int)TransientCandidateFaultPoint.AfterAcknowledgementCommit)]
    public async Task DurableCandidateBoundaryFaultMatrix_RetryConvergesExactlyOnce(
        int faultPoint)
    {
        var point = (TransientCandidateFaultPoint)faultPoint;
        var hybrid = point is TransientCandidateFaultPoint.BeforeSubmissionCommit or
            TransientCandidateFaultPoint.AfterSubmissionCommit or
            TransientCandidateFaultPoint.BeforeAcknowledgementCommit or
            TransientCandidateFaultPoint.AfterAcknowledgementCommit;
        using var fixture = await Fixture.CreateAsync(
            mode: hybrid ? TransientOperatingMode.Hybrid : TransientOperatingMode.Edge).ConfigureAwait(false);
        if (point is TransientCandidateFaultPoint.BeforeStageCommit or TransientCandidateFaultPoint.AfterStageCommit)
        {
            var context = await fixture.CreateLaneContextAsync(1, 100).ConfigureAwait(false);
            using (var interrupted = fixture.ReconstructJournal(new ThrowingFaultInjector(point)))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await interrupted.Journal.StageCaptureAsync(context, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
            var committed = point == TransientCandidateFaultPoint.AfterStageCommit;
            Assert.AreEqual(committed ? 1L : 0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_capture_work;").ConfigureAwait(false));
            if (committed)
            {
                Assert.AreEqual("pending", await fixture.ScalarStringAsync(
                    "SELECT state FROM transient_capture_work;").ConfigureAwait(false));
            }
            using var reconstructed = fixture.ReconstructJournal();
            await reconstructed.Journal.StageCaptureAsync(context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_capture_work;").ConfigureAwait(false));
            Assert.AreEqual("pending", await fixture.ScalarStringAsync(
                "SELECT state FROM transient_capture_work;").ConfigureAwait(false));
            await Phase14ScenarioEvidence.RecordAsync(
                committed ? "transient-candidate-stage-after-commit" : "transient-candidate-stage-before-commit",
                $"fault-point-{point}",
                point.ToString(),
                ["stage-fault-observed", "retry-converged-one-work-row", "work-remained-pending"])
                .ConfigureAwait(false);
            return;
        }

        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        if (point is TransientCandidateFaultPoint.BeforeReservationCommit or
            TransientCandidateFaultPoint.AfterReservationCommit)
        {
            using (var interrupted = fixture.ReconstructJournal(new ThrowingFaultInjector(point)))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await interrupted.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
            var committed = point == TransientCandidateFaultPoint.AfterReservationCommit;
            Assert.AreEqual(committed ? 1L : 0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(committed ? 1L : 0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidate_sources;").ConfigureAwait(false));
            if (committed)
            {
                Assert.AreEqual("reserved", await fixture.ScalarStringAsync(
                    "SELECT phase FROM transient_candidates;").ConfigureAwait(false));
            }
            using var reconstructed = fixture.ReconstructJournal();
            var recovered = await reconstructed.Journal.ReserveAsync(
                reservation, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(TransientCandidateWorkflowPhase.IdentityAllocated, recovered.Entry.Phase);
            Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
            await Phase14ScenarioEvidence.RecordAsync(
                committed ? "transient-candidate-reservation-after-commit" : "transient-candidate-reservation-before-commit",
                $"fault-point-{point}",
                point.ToString(),
                ["reservation-fault-observed", "retry-converged-one-candidate", "identity-allocation-preserved"])
                .ConfigureAwait(false);
            return;
        }

        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        if (point is TransientCandidateFaultPoint.BeforeCandidateCommit or
            TransientCandidateFaultPoint.AfterCandidateCommit)
        {
            using (var interrupted = fixture.ReconstructJournal(new ThrowingFaultInjector(point)))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await interrupted.Journal.PersistCandidateAsync(
                        reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
            var committed = point == TransientCandidateFaultPoint.AfterCandidateCommit;
            Assert.AreEqual(committed ? "candidate_persisted" : "reserved", await fixture.ScalarStringAsync(
                "SELECT phase FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(committed ? 1L : 0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidates WHERE candidate_payload IS NOT NULL AND candidate_payload_sha256 IS NOT NULL;")
                .ConfigureAwait(false));
            using var reconstructed = fixture.ReconstructJournal();
            var recovered = await reconstructed.Journal.PersistCandidateAsync(
                reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(TransientCandidateWorkflowPhase.CandidatePersisted, recovered.Phase);
            Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
            await Phase14ScenarioEvidence.RecordAsync(
                committed ? "transient-candidate-record-after-commit" : "transient-candidate-record-before-commit",
                $"fault-point-{point}",
                point.ToString(),
                ["candidate-fault-observed", "retry-converged-one-candidate", "candidate-payload-durable"])
                .ConfigureAwait(false);
            return;
        }

        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        if (point is TransientCandidateFaultPoint.BeforeFinalizationCommit or
            TransientCandidateFaultPoint.AfterFinalizationCommit)
        {
            var receipt = CreateFinalization(reservation, TransientEventState.NeedsReview);
            using (var interrupted = fixture.ReconstructJournal(new ThrowingFaultInjector(point)))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await interrupted.Journal.PersistFinalizationAsync(
                        reservation.CandidateId, reservation.EventId, receipt, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
            var committed = point == TransientCandidateFaultPoint.AfterFinalizationCommit;
            Assert.AreEqual(committed ? "finalized" : "candidate_persisted", await fixture.ScalarStringAsync(
                "SELECT phase FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(committed ? 1L : 0L, await fixture.ScalarLongAsync(
                "SELECT source_hold_released FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(committed ? 0L : 1L, await fixture.ScalarLongAsync(
                "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
            using var reconstructed = fixture.ReconstructJournal();
            var recovered = await reconstructed.Journal.PersistFinalizationAsync(
                reservation.CandidateId, reservation.EventId, receipt, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(TransientCandidateWorkflowPhase.Finalized, recovered.Phase);
            Assert.IsTrue(recovered.SourceHoldReleased);
            Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
            await Phase14ScenarioEvidence.RecordAsync(
                committed ? "transient-candidate-finalization-after-commit" : "transient-candidate-finalization-before-commit",
                $"fault-point-{point}",
                point.ToString(),
                ["finalization-fault-observed", "retry-converged-finalized", "source-hold-released"])
                .ConfigureAwait(false);
            return;
        }

        var submission = CreateSubmission(reservation, candidate);
        if (point is TransientCandidateFaultPoint.BeforeSubmissionCommit or
            TransientCandidateFaultPoint.AfterSubmissionCommit)
        {
            using (var interrupted = fixture.ReconstructJournal(new ThrowingFaultInjector(point)))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await interrupted.Journal.PersistSubmissionAsync(
                        reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
            var committed = point == TransientCandidateFaultPoint.AfterSubmissionCommit;
            Assert.AreEqual(committed ? "handoff_pending" : "candidate_persisted", await fixture.ScalarStringAsync(
                "SELECT phase FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(committed ? 1L : 0L, await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_candidates WHERE submission_payload IS NOT NULL AND submission_identity_sha256 IS NOT NULL;")
                .ConfigureAwait(false));
            Assert.AreEqual(0L, await fixture.ScalarLongAsync(
                "SELECT source_hold_released FROM transient_candidates;").ConfigureAwait(false));
            using var reconstructed = fixture.ReconstructJournal();
            var recovered = await reconstructed.Journal.PersistSubmissionAsync(
                reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(TransientCandidateWorkflowPhase.HandoffPending, recovered.Phase);
            Assert.IsFalse(recovered.SourceHoldReleased);
            await Phase14ScenarioEvidence.RecordAsync(
                committed ? "transient-candidate-submission-after-commit" : "transient-candidate-submission-before-commit",
                $"fault-point-{point}",
                point.ToString(),
                ["submission-fault-observed", "retry-converged-handoff-pending", "source-hold-retained"])
                .ConfigureAwait(false);
            return;
        }

        await fixture.Journal.PersistSubmissionAsync(
            reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false);
        var acknowledgement = new TransientCandidateSubmissionAcknowledgementV1(
            TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
            reservation.CandidateId,
            reservation.EventId,
            submission.SubmissionIdentitySha256,
            DateTimeOffset.UtcNow,
            TransientCandidateSubmissionDisposition.Accepted);
        using (var interrupted = fixture.ReconstructJournal(new ThrowingFaultInjector(point)))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await interrupted.Journal.AcknowledgeAsync(
                    reservation.CandidateId, reservation.EventId, acknowledgement, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        var acknowledgementCommitted = point == TransientCandidateFaultPoint.AfterAcknowledgementCommit;
        Assert.AreEqual(acknowledgementCommitted ? "acknowledged" : "handoff_pending", await fixture.ScalarStringAsync(
            "SELECT phase FROM transient_candidates;").ConfigureAwait(false));
        Assert.AreEqual(acknowledgementCommitted ? 1L : 0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidates WHERE acknowledgement_payload IS NOT NULL AND acknowledgement_payload_sha256 IS NOT NULL;")
            .ConfigureAwait(false));
        Assert.AreEqual(acknowledgementCommitted ? 1L : 0L, await fixture.ScalarLongAsync(
            "SELECT source_hold_released FROM transient_candidates;").ConfigureAwait(false));
        using var reconstructedAcknowledgement = fixture.ReconstructJournal();
        var acknowledged = await reconstructedAcknowledgement.Journal.AcknowledgeAsync(
            reservation.CandidateId, reservation.EventId, acknowledgement, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(TransientCandidateWorkflowPhase.Acknowledged, acknowledged.Phase);
        Assert.IsTrue(acknowledged.SourceHoldReleased);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        await Phase14ScenarioEvidence.RecordAsync(
            acknowledgementCommitted
                ? "transient-candidate-acknowledgement-after-commit"
                : "transient-candidate-acknowledgement-before-commit",
            $"fault-point-{point}",
            point.ToString(),
            ["acknowledgement-fault-observed", "retry-converged-acknowledged", "source-hold-released"])
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Restart_RejectsCorruptCanonicalCandidatePayload()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId,
            reservation.EventId,
            CreateCandidate(reservation, TransientCandidateState.Provisional),
            CancellationToken.None).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            "UPDATE transient_candidates SET candidate_payload = X'7B7D';").ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.CreateJournal().ReadAsync(
                reservation.CandidateId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(
            "quarantined",
            await fixture.ScalarStringAsync("SELECT phase FROM transient_candidates;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ValidCandidateMutation_IsQuarantinedBeforeEdgeFinalizationCanReleaseHold()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        var mutated = candidate with { CreatedUtc = candidate.CreatedUtc.AddSeconds(1) };
        await fixture.SetCandidateBlobAsync(
            "candidate_payload", TransientContractJson.Serialize(mutated)).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.Journal.PersistFinalizationAsync(
                reservation.CandidateId,
                reservation.EventId,
                CreateFinalization(reservation, TransientEventState.NeedsReview),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(
            "quarantined",
            await fixture.ScalarStringAsync("SELECT phase FROM transient_candidates;").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("reservation")]
    [DataRow("candidate")]
    [DataRow("finalization")]
    [DataRow("submission")]
    [DataRow("acknowledgement")]
    public async Task Read_QuarantinesEveryPersistedCanonicalIdentityMismatch(string target)
    {
        using var fixture = await Fixture.CreateAsync(mode: TransientOperatingMode.Hybrid).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var candidate = CreateCandidate(reservation, TransientCandidateState.Provisional);
        await fixture.Journal.PersistCandidateAsync(
            reservation.CandidateId, reservation.EventId, candidate, CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(reservation, candidate);
        await fixture.Journal.PersistSubmissionAsync(
            reservation.CandidateId, reservation.EventId, submission, CancellationToken.None).ConfigureAwait(false);
        var finalization = CreateFinalization(reservation, TransientEventState.NeedsReview);
        await fixture.Journal.PersistFinalizationAsync(
            reservation.CandidateId, reservation.EventId, finalization, CancellationToken.None).ConfigureAwait(false);
        var acknowledgement = new TransientCandidateSubmissionAcknowledgementV1(
            TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
            reservation.CandidateId,
            reservation.EventId,
            submission.SubmissionIdentitySha256,
            DateTimeOffset.UtcNow,
            TransientCandidateSubmissionDisposition.Accepted);
        await fixture.Journal.AcknowledgeAsync(
            reservation.CandidateId, reservation.EventId, acknowledgement, CancellationToken.None).ConfigureAwait(false);

        switch (target)
        {
            case "reservation":
                await fixture.ExecuteAsync(
                    "UPDATE transient_candidate_sources SET timing_version = 'mutated-v2';").ConfigureAwait(false);
                break;
            case "candidate":
                await fixture.SetCandidateBlobAsync(
                    "candidate_payload",
                    TransientContractJson.Serialize(candidate with
                    {
                        CreatedUtc = candidate.CreatedUtc.AddSeconds(1)
                    })).ConfigureAwait(false);
                break;
            case "finalization":
                var changedReceipt = finalization with
                {
                    Event = finalization.Event with { EventVersionId = Guid.NewGuid() },
                    ReceiptIdentitySha256 = new string('0', 64)
                };
                changedReceipt = changedReceipt with
                {
                    ReceiptIdentitySha256 = TransientCandidateDeliveryJson.ComputeFinalizationIdentitySha256(changedReceipt)
                };
                await fixture.SetCandidateBlobAsync(
                    "finalization_payload",
                    TransientCandidateDeliveryJson.Serialize(changedReceipt)).ConfigureAwait(false);
                break;
            case "submission":
                var changedSubmission = submission with
                {
                    RequestedProcessingProfileIdentity = "mutated-profile-v2",
                    SubmissionIdentitySha256 = new string('0', 64)
                };
                changedSubmission = changedSubmission with
                {
                    SubmissionIdentitySha256 = TransientCandidateDeliveryJson.ComputeSubmissionIdentitySha256(changedSubmission)
                };
                await fixture.SetCandidateBlobAsync(
                    "submission_payload",
                    TransientCandidateDeliveryJson.Serialize(changedSubmission)).ConfigureAwait(false);
                break;
            case "acknowledgement":
                await fixture.SetCandidateBlobAsync(
                    "acknowledgement_payload",
                    TransientCandidateDeliveryJson.Serialize(acknowledgement with
                    {
                        ReceivedAtUtc = acknowledgement.ReceivedAtUtc.AddSeconds(1)
                    })).ConfigureAwait(false);
                break;
            default:
                Assert.Fail($"Unsupported mutation target '{target}'.");
                break;
        }

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await fixture.CreateJournal().ReadAsync(
                reservation.CandidateId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(
            "quarantined",
            await fixture.ScalarStringAsync("SELECT phase FROM transient_candidates;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LaneHandler_AcknowledgesOnlyAfterDurableTransientWorkExists()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var context = await fixture.CreateLaneContextAsync(1, 100).ConfigureAwait(false);
        var handler = new TransientCaptureLaneHandler(fixture.Journal);

        var result = await handler.HandleAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
        Assert.AreEqual(
            1L,
            await fixture.ScalarLongAsync(
                "SELECT COUNT(*) FROM transient_capture_work WHERE state = 'pending';").ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        var snapshot = await fixture.ReadTransientLaneSnapshotAsync().ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(1L, snapshot.PendingCount);
        Assert.AreEqual(100L, snapshot.PendingBytes);
    }

    [TestMethod]
    public async Task Backlog_DeduplicatesHeldBytesEnforcesLimitsAndKeepsSharedSourceHeld()
    {
        using var fixture = await Fixture.CreateAsync(new CaptureDistributionOptions
        {
            OptionalMaximumPendingCount = 2,
            OptionalMaximumPendingBytes = 150,
            OptionalMaximumOldestAgeMinutes = 60,
            PressureRecoveryPercent = 80
        }).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var first = Fixture.CreateReservation(source);
        var second = Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(first, CancellationToken.None).ConfigureAwait(false);
        await fixture.Journal.ReserveAsync(second, CancellationToken.None).ConfigureAwait(false);

        var backlog = await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(backlog.Required);
        Assert.AreEqual(2L, backlog.ActiveCount);
        Assert.AreEqual(100L, backlog.HeldSourceBytes);
        Assert.AreEqual(2L, backlog.MaximumCount);
        Assert.AreEqual(150L, backlog.MaximumHeldSourceBytes);
        Assert.AreEqual(2, backlog.PressureLevel);
        await Assert.ThrowsExactlyAsync<TransientCandidateJournalCapacityException>(async () =>
            await fixture.Journal.ReserveAsync(
                Fixture.CreateReservation(source), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        await FinalizeAsync(fixture.Journal, first).ConfigureAwait(false);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        await FinalizeAsync(fixture.Journal, second).ConfigureAwait(false);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Pressure_RemainsLatchedAtRecoveryBoundaryAndClearsBelowIt()
    {
        using var fixture = await Fixture.CreateAsync(new CaptureDistributionOptions
        {
            OptionalMaximumPendingCount = 5,
            OptionalMaximumPendingBytes = 10_000,
            OptionalMaximumOldestAgeMinutes = 60,
            PressureRecoveryPercent = 80
        }).ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservations = Enumerable.Range(0, 5)
            .Select(_ => Fixture.CreateReservation(source))
            .ToArray();
        foreach (var reservation in reservations)
        {
            await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        }
        Assert.AreEqual(2, (await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false)).PressureLevel);

        await FinalizeAsync(fixture.Journal, reservations[0]).ConfigureAwait(false);
        Assert.AreEqual(2, (await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false)).PressureLevel);
        await Assert.ThrowsExactlyAsync<TransientCandidateJournalCapacityException>(async () =>
            await fixture.Journal.ReserveAsync(
                Fixture.CreateReservation(source), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        await FinalizeAsync(fixture.Journal, reservations[1]).ConfigureAwait(false);
        Assert.AreEqual(0, (await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false)).PressureLevel);
    }

    [TestMethod]
    [DataRow(false, 2L, 200L, 20)]
    [DataRow(true, 7L, 700L, 70)]
    public async Task Backlog_UsesConfiguredRequiredOrOptionalLaneLimits(
        bool required,
        long expectedCount,
        long expectedBytes,
        int expectedAgeMinutes)
    {
        using var fixture = await Fixture.CreateAsync(
            new CaptureDistributionOptions
            {
                OptionalMaximumPendingCount = 2,
                OptionalMaximumPendingBytes = 200,
                OptionalMaximumOldestAgeMinutes = 20,
                RequiredMaximumPendingCount = 7,
                RequiredMaximumPendingBytes = 700,
                RequiredMaximumOldestAgeMinutes = 70
            },
            required).ConfigureAwait(false);

        var backlog = await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(required, backlog.Required);
        Assert.AreEqual(expectedCount, backlog.MaximumCount);
        Assert.AreEqual(expectedBytes, backlog.MaximumHeldSourceBytes);
        Assert.AreEqual(expectedAgeMinutes, backlog.MaximumOldestAgeMinutes);
    }

    private static TransientCandidateV1 CreateCandidate(
        TransientCandidateReservation reservation,
        TransientCandidateState state)
    {
        var source = reservation.Sources[0];
        return new TransientCandidateV1(
            TransientCandidateV1.CurrentSchemaVersion,
            reservation.CandidateId,
            reservation.EventId,
            reservation.AgentId,
            state,
            reservation.Sources.Max(static value => value.ObservationEndedUtc).AddSeconds(1),
            source.EvidenceId,
            reservation.Sources,
            new TransientObservationProvenanceV1(
                new string('B', 64), "calibration-v1", "mask-v1", "profile-v1"),
            new TransientObservationExtractionV1(
                reservation.CandidateId,
                new TransientExtractionProducerV1(
                    TransientExtractionProducerV1.CurrentSchemaVersion,
                    TransientExtractionProducerKind.DeterministicAlgorithm,
                    "test-extractor",
                    "v1"),
                new string('C', 64)),
            null,
            null,
            []);
    }

    private static TransientFinalizationReceiptV1 CreateFinalization(
        TransientCandidateReservation reservation,
        TransientEventState state)
    {
        var source = reservation.Sources[0];
        var observationId = Guid.NewGuid();
        var created = reservation.Sources.Max(static value => value.ObservationEndedUtc).AddSeconds(2);
        var extraction = new TransientObservationExtractionV1(
            reservation.CandidateId,
            new TransientExtractionProducerV1(
                TransientExtractionProducerV1.CurrentSchemaVersion,
                TransientExtractionProducerKind.DeterministicAlgorithm,
                "test-extractor",
                "v1"),
            new string('C', 64));
        var observation = new TransientObservationV1(
            observationId,
            0,
            source,
            [],
            new TransientObservationProvenanceV1(
                new string('B', 64), "calibration-v1", "mask-v1", "profile-v1"),
            extraction,
            new TransientGeometryV1(
                source.EvidenceId,
                10,
                10,
                new TransientBoundingRegionV1(1, 1, 2, 2),
                [new TransientPointV1(1, 1), new TransientPointV1(3, 3)]),
            new TransientFeaturesV1(
                source.EvidenceId,
                2,
                1,
                1,
                10,
                10,
                0,
                1,
                [new TransientProfileSampleV1(0, 1), new TransientProfileSampleV1(1_000_000, 1)],
                [new TransientProfileSampleV1(0, 1), new TransientProfileSampleV1(1_000_000, 1)]));
        var assessment = new TransientAssessmentV1(
            Guid.NewGuid(),
            created,
            TransientAssessmentAuthority.Authoritative,
            TransientClassification.Unknown,
            null,
            500_000,
            [],
            new TransientAssessmentProducerV1(
                TransientAssessmentProducerV1.CurrentSchemaVersion,
                TransientAssessmentProducerKind.DeterministicAlgorithm,
                "test-assessor",
                "v1"),
            new string('D', 64),
            [observationId],
            null);
        var transientEvent = new TransientEventV1(
            TransientEventV1.CurrentSchemaVersion,
            reservation.EventId,
            Guid.NewGuid(),
            1,
            null,
            null,
            reservation.AgentId,
            state,
            created,
            created,
            source.ObservationStartedUtc,
            source.ObservationEndedUtc,
            [observation],
            [assessment],
            [],
            [],
            []);
        var receipt = new TransientFinalizationReceiptV1(
            TransientFinalizationReceiptV1.CurrentSchemaVersion,
            reservation.CandidateId,
            reservation.EventId,
            transientEvent,
            new string('0', 64));
        return receipt with
        {
            ReceiptIdentitySha256 = TransientCandidateDeliveryJson.ComputeFinalizationIdentitySha256(receipt)
        };
    }

    private static TransientCandidateSubmissionEnvelopeV1 CreateSubmission(
        TransientCandidateReservation reservation,
        TransientCandidateV1 candidate)
    {
        var submission = new TransientCandidateSubmissionEnvelopeV1(
            TransientCandidateSubmissionEnvelopeV1.CurrentSchemaVersion,
            reservation.CandidateId,
            reservation.EventId,
            candidate,
            new string('E', 64),
            "profile-v1",
            new string('0', 64));
        return submission with
        {
            SubmissionIdentitySha256 = TransientCandidateDeliveryJson.ComputeSubmissionIdentitySha256(submission)
        };
    }

    private static async Task FinalizeAsync(
        SqliteTransientCandidateJournal journal,
        TransientCandidateReservation reservation)
    {
        await journal.PersistCandidateAsync(
            reservation.CandidateId,
            reservation.EventId,
            CreateCandidate(reservation, TransientCandidateState.Provisional),
            CancellationToken.None).ConfigureAwait(false);
        await journal.PersistFinalizationAsync(
            reservation.CandidateId,
            reservation.EventId,
            CreateFinalization(reservation, TransientEventState.NeedsReview),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static TransientRuntimeCandidate[] CreateRuntimeAllocations(
        long rawCaptureRowId,
        int count)
    {
        var allocatedUtc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(index => new TransientRuntimeCandidate(
                Guid.NewGuid(),
                Guid.NewGuid(),
                rawCaptureRowId,
                index,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                allocatedUtc,
                AssociationAmbiguous: false,
                AttemptCount: 0,
                allocatedUtc,
                CausalExtraction: null,
                ObservationExtraction: null,
                AssessmentExecution: null))
            .ToArray();
    }

    private static Dictionary<string, byte[]> ReadDatabaseFiles(string databasePath)
        => new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm", $"{databasePath}-journal" }
            .Where(File.Exists)
            .ToDictionary(static path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertDatabaseFilesUnchanged(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var file in expected)
        {
            CollectionAssert.AreEqual(file.Value, actual[file.Key], file.Key);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        private readonly CameraAgentHostOptions _options;
        private readonly Dictionary<Guid, SeededCapture> _captures = [];
        private readonly TransientWorkerTelemetry _telemetry = new();

        private Fixture(string root, CameraAgentHostOptions options)
        {
            Root = root;
            _options = options;
            Journal = CreateJournal();
        }

        internal string Root { get; }

        internal SqliteTransientCandidateJournal Journal { get; }

        internal static async Task<Fixture> CreateAsync(
            CaptureDistributionOptions? limits = null,
            bool required = false,
            TransientOperatingMode mode = TransientOperatingMode.Edge)
        {
            var root = Path.Combine(Path.GetTempPath(), "hvo-transient-journal-tests", Guid.NewGuid().ToString("N"));
            var options = new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1,
                CaptureDistribution = limits ?? new CaptureDistributionOptions(),
                TransientDetection = new TransientDetectionOptions
                {
                    Mode = mode,
                    Required = required
                }
            };
            var wrapped = Options.Create(options);
            var initializer = new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"),
                options.RawIngressSqliteBusyTimeoutSeconds,
                distributionOptions: options.CaptureDistribution,
                transientOptions: options.TransientDetection);
            await initializer.InitializeAsync(
                new CaptureLanePolicy(wrapped).Definitions,
                CancellationToken.None).ConfigureAwait(false);
            return new Fixture(root, options);
        }

        internal SqliteTransientCandidateJournal CreateJournal(ITransientCandidateFaultInjector? faultInjector = null)
            => new SqliteTransientCandidateJournal(
                Options.Create(_options), new FixedTimeProvider(Now), faultInjector);

        internal ReconstructedJournalScope ReconstructJournal(ITransientCandidateFaultInjector? faultInjector = null)
        {
            SqliteConnection.ClearAllPools();
            var services = new ServiceCollection();
            services.AddSingleton<IOptions<CameraAgentHostOptions>>(Options.Create(_options));
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            if (faultInjector is not null)
            {
                services.AddSingleton(faultInjector);
                services.AddSingleton<ITransientCandidateFaultInjector>(faultInjector);
            }
            else
            {
                services.AddSingleton<ITransientCandidateFaultInjector>(NullTransientCandidateFaultInjector.Instance);
            }
            services.AddSingleton<ITransientCandidateJournal, SqliteTransientCandidateJournal>();
            var provider = services.BuildServiceProvider();
            return new ReconstructedJournalScope(
                provider,
                provider.GetRequiredService<ITransientCandidateJournal>());
        }

        internal SqliteTransientRuntimeStore CreateRuntimeStore(ITransientRuntimeFaultInjector? faultInjector = null)
            => new(Options.Create(_options), new FixedTimeProvider(Now), _telemetry, faultInjector);

        internal async Task ReinitializeAsync(TransientOperatingMode mode, bool required)
        {
            var transient = new TransientDetectionOptions
            {
                Mode = mode,
                Required = required,
                CandidateTimeoutMinutes = _options.TransientDetection.CandidateTimeoutMinutes
            };
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Root,
                RawIngressSqliteBusyTimeoutSeconds = 1,
                CaptureDistribution = _options.CaptureDistribution,
                TransientDetection = transient
            });
            var initializer = new SqliteRawCaptureJournal(
                Path.Combine(Root, "journal", "raw-ingress.db"),
                1,
                distributionOptions: _options.CaptureDistribution,
                transientOptions: transient);
            await initializer.InitializeAsync(
                new CaptureLanePolicy(options).Definitions,
                CancellationToken.None).ConfigureAwait(false);
        }

        internal async Task SimulateV3Async(bool activeLegacyWork)
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            if (activeLegacyWork)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO capture_lane_work(
                        raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered,
                        state, attempt_count, available_unix_ms, created_unix_ms, updated_unix_ms)
                    SELECT raw_capture_row_id, 'transient', agent_id, capture_sequence, 0, 1,
                           'pending', 0, durable_ingress_unix_ms, committed_unix_ms, committed_unix_ms
                    FROM raw_captures LIMIT 1;
                    """;
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE transient_candidate_sources;
                DROP TABLE transient_candidates;
                DROP TABLE transient_event_identities;
                DROP TABLE transient_capture_work;
                DROP TABLE transient_candidate_conflicts;
                DROP TABLE transient_runtime_policy;
                PRAGMA user_version = 3;
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        internal string ResolvePayload(TransientSourceEvidenceReferenceV1 source)
            => Path.Combine(Root, "frames", source.Locator.Artifact.ArtifactId == Guid.Empty
                ? string.Empty
                : FindPayloadName(source.Locator.Artifact.ArtifactId));

        private string FindPayloadName(Guid artifactId)
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "journal", "raw-ingress.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_relative_path FROM raw_captures WHERE raw_artifact_id = $artifact;";
            command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
            var relative = Convert.ToString(
                command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
            return Path.GetFileName(relative);
        }

        internal async Task<TransientSourceEvidenceReferenceV1> AddRawSourceAsync(int sequence, long payloadLength)
        {
            var captureId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var payload = Enumerable.Range(0, checked((int)payloadLength)).Select(static value => (byte)value).ToArray();
            var payloadSha256 = Convert.ToHexString(SHA256.HashData(payload));
            var observed = Now.AddSeconds(-sequence - 1);
            var configuration = CreateConfiguration(checked((int)payloadLength));
            var submission = CreateSubmission(observed, payload);
            var identity = new RawCaptureIdentity("agent", sequence, captureId, artifactId);
            var descriptor = RawCaptureDescriptorFactory.Create(
                configuration, submission, identity, payloadSha256, Now);
            var payloadRelativePath = $"frames/{sequence}.bin";
            var sidecarRelativePath = $"frames/{sequence}.json";
            var manifest = new ArtifactManifestV2(
                ArtifactManifestV2.CurrentSchemaVersion,
                descriptor,
                payloadRelativePath,
                null);
            var manifestJson = CaptureContractJson.Serialize(manifest);
            var manifestSha256 = CaptureContractJson.ComputeManifestSha256(manifestJson);
            Directory.CreateDirectory(Path.Combine(Root, "frames"));
            await File.WriteAllBytesAsync(Path.Combine(Root, payloadRelativePath), payload).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(Root, sidecarRelativePath), manifestJson).ConfigureAwait(false);
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            using (var assignment = connection.CreateCommand())
            {
                assignment.Transaction = transaction;
                assignment.CommandText = """
                    INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
                    VALUES ($capture, $artifact, 'agent', $sequence);
                    """;
                assignment.Parameters.AddWithValue("$capture", captureId.ToString("N"));
                assignment.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
                assignment.Parameters.AddWithValue("$sequence", sequence);
                await assignment.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var raw = connection.CreateCommand())
            {
                raw.Transaction = transaction;
                raw.CommandText = """
                    INSERT INTO raw_captures(
                        capture_id, raw_artifact_id, agent_id, capture_sequence,
                        descriptor_sha256, manifest_sha256, payload_sha256, payload_length,
                        payload_relative_path, sidecar_relative_path, manifest_json,
                        exposure_started_unix_ms, durable_ingress_unix_ms, committed_unix_ms,
                        state, retention_hold)
                    VALUES ($capture, $artifact, 'agent', $sequence, $descriptor, $manifest,
                        $payload, $length, $payload_path, $sidecar_path, $manifest_json, $now, $now,
                        $now, 'committed', 0);
                    """;
                raw.Parameters.AddWithValue("$capture", captureId.ToString("N"));
                raw.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
                raw.Parameters.AddWithValue("$sequence", sequence);
                raw.Parameters.AddWithValue("$descriptor", CaptureContractJson.ComputeDescriptorSha256(descriptor));
                raw.Parameters.AddWithValue("$manifest", manifestSha256);
                raw.Parameters.AddWithValue("$payload", payloadSha256);
                raw.Parameters.AddWithValue("$length", payloadLength);
                raw.Parameters.AddWithValue("$payload_path", payloadRelativePath);
                raw.Parameters.AddWithValue("$sidecar_path", sidecarRelativePath);
                raw.Parameters.AddWithValue("$manifest_json", manifestJson);
                raw.Parameters.AddWithValue("$now", Now.ToUnixTimeMilliseconds());
                await raw.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await transaction.CommitAsync().ConfigureAwait(false);
            var receipt = new RawCaptureReceipt(
                RawIngressOutcome.Committed,
                manifest,
                new StoredFrameReference(
                    payloadRelativePath,
                    Path.Combine(Root, payloadRelativePath),
                    descriptor.Timing.ExposureStartedUtc,
                    FrameArtifactRole.Raw),
                manifestSha256);
            _captures[artifactId] = new SeededCapture(configuration, submission, receipt);
            return new TransientSourceEvidenceReferenceV1(
                TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                Guid.NewGuid(),
                new TransientWholeArtifactLocatorV1(
                    TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                    TransientSourceLocatorKind.WholeArtifact,
                    new TransientArtifactReferenceV1(
                        artifactId,
                        FrameArtifactRole.Raw,
                        descriptor.Artifact.Variant,
                        ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                        payloadSha256)),
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.ExposureEndedUtc,
                TransientTimingQuality.Reported,
                new TransientTimingProvenanceV1("camera", "v1"));
        }

        internal async Task<CaptureLaneHandlerContext> CreateLaneContextAsync(int sequence, long payloadLength)
        {
            var source = await AddRawSourceAsync(sequence, payloadLength).ConfigureAwait(false);
            var seeded = _captures[source.Locator.Artifact.ArtifactId];
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO capture_lane_work(
                    raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered,
                    state, attempt_count, available_unix_ms, lease_token, lease_owner,
                    lease_expires_unix_ms, created_unix_ms, updated_unix_ms)
                SELECT raw_capture_row_id, 'transient', agent_id, capture_sequence, 0, 1,
                       'leased', 1, durable_ingress_unix_ms, 'test-token', 'test-owner',
                       $expires, committed_unix_ms, committed_unix_ms
                FROM raw_captures WHERE raw_artifact_id = $artifact
                RETURNING work_id;
                """;
            command.Parameters.AddWithValue("$expires", Now.AddMinutes(1).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$artifact", source.Locator.Artifact.ArtifactId.ToString("N"));
            var workId = Convert.ToInt64(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            return new CaptureLaneHandlerContext(
                "transient",
                1,
                seeded.Configuration,
                seeded.Submission,
                seeded.Receipt,
                workId,
                "test-token");
        }

        internal async Task<CaptureLaneBacklogSnapshot> ReadTransientLaneSnapshotAsync()
        {
            var wrapped = Options.Create(_options);
            var policy = new CaptureLanePolicy(wrapped);
            var store = new SqliteCaptureLaneStore(
                Root,
                1,
                _options.CaptureDistribution,
                policy,
                new FixedTimeProvider(Now),
                new NullCaptureLaneFaultInjector());
            var state = new CaptureLaneState(new FixedTimeProvider(Now), wrapped);
            state.Update(await store.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false));
            return state.Snapshot.Lanes.Single(static lane => lane.Lane == "transient");
        }

        private static CameraModuleConfig CreateConfiguration(int width)
            => new(
                new ObservatoryLocation(35, -113, 500, "UTC"),
                new CameraModuleDescriptor("Test"),
                new CameraRigConfig(
                    new SensorProfile(
                        "test-sensor", width, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8,
                        SensorRecipeVersion: "sensor-v1"),
                    new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                    new RigOrientation(90, 0, 0),
                    new PipelineExposureProfile(
                        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
                    ProfileVersion: "rig-v1"),
                CapturePipelineConfig.Empty,
                AgentId: "agent");

        private static CaptureLoopSubmission CreateSubmission(DateTimeOffset timestamp, byte[] payload)
        {
            var frame = new CameraFrame(
                timestamp,
                payload.Length,
                1,
                CameraPixelFormat.Mono8,
                payload,
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN, "Test"),
                payload.Length);
            return new CaptureLoopSubmission(
                new CaptureRequest(
                    timestamp,
                    TimeSpan.FromSeconds(1),
                    CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null)),
                new CaptureResult(
                    frame,
                    new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                    TimeSpan.Zero,
                    CaptureMode.Still,
                    false),
                timestamp,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero);
        }

        internal static TransientCandidateReservation CreateReservation(
            params TransientSourceEvidenceReferenceV1[] sources)
            => new(Guid.NewGuid(), Guid.NewGuid(), "agent", sources);

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test callers pass only internal constant queries.")]
        internal async Task<long> ScalarLongAsync(string sql)
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test callers pass only internal constant queries.")]
        internal async Task<string> ScalarStringAsync(string sql)
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture)!;
        }

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test callers pass only internal constant statements.")]
        internal async Task ExecuteAsync(string sql)
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Column names are restricted to an internal test whitelist; payload remains parameterized.")]
        internal async Task SetCandidateBlobAsync(string column, byte[] payload)
        {
            if (column is not ("candidate_payload" or "finalization_payload" or
                "submission_payload" or "acknowledgement_payload"))
            {
                throw new ArgumentOutOfRangeException(nameof(column));
            }
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE transient_candidates SET {column} = $payload;";
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private async Task<SqliteConnection> OpenAsync()
        {
            var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return connection;
        }

        public void Dispose()
        {
            _telemetry.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }

        private sealed record SeededCapture(
            CameraModuleConfig Configuration,
            CaptureLoopSubmission Submission,
            RawCaptureReceipt Receipt);

        internal sealed class ReconstructedJournalScope(
            ServiceProvider provider,
            ITransientCandidateJournal journal) : IDisposable
        {
            internal ITransientCandidateJournal Journal { get; } = journal;

            public void Dispose() => provider.Dispose();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThrowingFaultInjector(TransientCandidateFaultPoint point) : ITransientCandidateFaultInjector
    {
        private int _thrown;

        public void Inject(TransientCandidateFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InvalidOperationException("Injected transient candidate fault.");
            }
        }
    }

    private sealed class ThrowingRuntimeFaultInjector(TransientRuntimeFaultPoint point) : ITransientRuntimeFaultInjector
    {
        private int _thrown;

        public void Inject(TransientRuntimeFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InvalidOperationException("Injected transient runtime fault.");
            }
        }
    }
}
