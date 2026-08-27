using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class RawCaptureIngressTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task InitializeAsync_EmptyDatabaseCreatesCanonicalV11Idempotently()
    {
        var root = CreateRoot();
        try
        {
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);

            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(11L, await ScalarLongAsync(connection, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual(63L, await ScalarLongAsync(connection, """
                SELECT COUNT(*) FROM sqlite_master
                WHERE name IN (
                    'raw_capture_sequences', 'raw_capture_assignments', 'raw_captures', 'raw_ingress_reconciliation',
                    'ix_raw_captures_discovery', 'ix_raw_captures_backlog', 'ix_raw_captures_retention',
                    'ix_raw_captures_gallery_time', 'ix_raw_captures_gallery_sequence',
                    'ix_raw_captures_gallery_state', 'ix_raw_captures_gallery_origin',
                    'capture_lane_definitions', 'capture_lane_contexts', 'capture_lane_work',
                    'ix_capture_lane_work_claim', 'ix_capture_lane_work_lease',
                    'ix_capture_lane_work_backlog', 'ix_capture_lane_work_raw', 'ix_capture_lane_work_ordered',
                    'transient_event_identities', 'transient_candidates', 'transient_candidate_sources',
                    'ix_transient_candidates_backlog', 'ix_transient_candidates_operator',
                    'ix_transient_candidate_sources_raw', 'transient_runtime_policy', 'transient_capture_work',
                    'transient_candidate_conflicts', 'transient_runtime_operations',
                    'ix_transient_runtime_operations_target', 'ix_transient_capture_work_backlog',
                    'ix_transient_candidate_conflicts_observed', 'ix_transient_candidate_conflicts_candidate',
                    'capture_control_state', 'capture_control_commands', 'capture_schedule_revisions',
                    'capture_schedule_state', 'capture_schedule_activations', 'capture_schedule_overrides',
                    'capture_schedule_commands', 'capture_schedule_expansions', 'capture_schedule_intervals',
                    'capture_schedule_unavailable', 'capture_schedule_admissions', 'capture_schedule_override_events',
                    'ix_capture_schedule_revisions_created', 'ix_capture_schedule_overrides_active',
                    'ix_capture_schedule_expansions_lookup', 'ix_capture_schedule_intervals_bounds',
                    'calibration_library_bundles', 'calibration_library_artifacts', 'calibration_library_state',
                    'calibration_library_activations', 'calibration_library_commands', 'calibration_acquisition_jobs',
                    'calibration_library_reconciliation', 'ix_calibration_library_bundles_selection',
                    'ix_calibration_library_bundles_created', 'ix_calibration_library_artifacts_role',
                    'ix_calibration_library_activations_history', 'ix_calibration_acquisition_jobs_camera',
                    'ux_calibration_acquisition_jobs_camera_nonterminal',
                    'ix_calibration_library_reconciliation_state');
                """).ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM capture_lane_definitions WHERE lane_name = 'standard';")
                .ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM transient_runtime_policy WHERE policy_key = 1;")
                .ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM capture_control_state WHERE state_key = 1 AND state = 'running' AND version = 0;")
                .ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM calibration_library_state WHERE state_key = 1 AND active_bundle_id IS NULL AND version = 0;")
                .ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow((int)RawIngressFaultPoint.AfterMigrationTransactionBegan)]
    [DataRow((int)RawIngressFaultPoint.BeforeMigrationCommit)]
    public async Task InitializeAsync_WhenCanonicalCreationIsInterrupted_RollsBackAndRetries(
        int faultPointValue)
    {
        var faultPoint = (RawIngressFaultPoint)faultPointValue;
        var root = CreateRoot();
        try
        {
            var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
            var interrupted = new SqliteRawCaptureJournal(
                databasePath, 1, faultInjector: new OneShotFaultInjector(faultPoint));

            await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(() =>
                interrupted.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);

            using (var rolledBack = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(0L, await ScalarLongAsync(rolledBack, "PRAGMA user_version;").ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarLongAsync(
                    rolledBack, "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';")
                    .ConfigureAwait(false));
            }
            await new SqliteRawCaptureJournal(databasePath, 1)
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var retried = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(11L, await ScalarLongAsync(retried, "PRAGMA user_version;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow(-7)]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(10)]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The data rows provide only fixed schema-version integers.")]
    public async Task InitializeAsync_WhenPopulatedSchemaVersionIsUnsupported_FailsWithoutMutation(int version)
    {
        var root = CreateRoot();
        try
        {
            var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
            var journal = new SqliteRawCaptureJournal(databasePath, 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var setup = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = setup.CreateCommand();
                command.CommandText = $"""
                    CREATE TABLE unsupported_schema_sentinel(value TEXT NOT NULL);
                    INSERT INTO unsupported_schema_sentinel(value) VALUES ('unchanged');
                    PRAGMA user_version = {version};
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                journal.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);

            StringAssert.Contains(exception.Message, "archive or remove", StringComparison.Ordinal);
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(version, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual("unchanged", await ScalarStringAsync(
                verify, "SELECT value FROM unsupported_schema_sentinel;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenCurrentSchemaDrifts_FailsBeforePolicyMutation()
    {
        var root = CreateRoot();
        try
        {
            var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
            var journal = new SqliteRawCaptureJournal(databasePath, 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var setup = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = setup.CreateCommand();
                command.CommandText = """
                    UPDATE capture_lane_definitions
                    SET policy_sha256 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA';
                    UPDATE transient_runtime_policy SET mode = 'central';
                    DROP INDEX ix_raw_captures_gallery_origin;
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                journal.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);

            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                await ScalarStringAsync(
                    verify, "SELECT policy_sha256 FROM capture_lane_definitions WHERE lane_name = 'standard';")
                    .ConfigureAwait(false));
            Assert.AreEqual("central", await ScalarStringAsync(
                verify, "SELECT mode FROM transient_runtime_policy WHERE policy_key = 1;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(
                verify, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_raw_captures_gallery_origin';")
                .ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_PublishesV2EvidenceBeforeWalCommitAndIsIdempotent()
    {
        var root = CreateRoot();
        try
        {
            var state = new RawIngressState(TimeProvider.System);
            using var ingress = CreateIngress(root, state);
            var location = DeploymentLocationSnapshot.Create(
                "siding-spring-synthetic", 1, "synthetic test", null,
                DateTimeOffset.UnixEpoch, null, -31.2733, 149.0700, 1165, "Australia/Sydney");
            var configuration = CreateConfiguration() with { DeploymentLocation = location };
            var submission = CreateSubmission(Timestamp(2), [1, 2, 3, 4]);

            var first = await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false);
            var duplicate = await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(first);
            Assert.IsNotNull(duplicate);
            Assert.AreEqual(RawIngressOutcome.Committed, first.Outcome);
            Assert.AreEqual(RawIngressOutcome.Existing, duplicate.Outcome);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(first.StoredFrame.AbsolutePath).ConfigureAwait(false));
            var sidecarPath = Path.ChangeExtension(first.StoredFrame.AbsolutePath, ".json");
            var parsed = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false));
            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            Assert.AreEqual(first.Manifest.IdempotencyKey, parsed.Document!.Manifest!.IdempotencyKey);
            Assert.AreEqual(location.ToProvenance(), first.Manifest.Descriptor.Location);
            Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
            Assert.AreEqual(1, state.Snapshot.PendingCount);
            Assert.AreEqual(4, state.Snapshot.PendingBytes);

            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual("wal", await ScalarStringAsync(connection, "PRAGMA journal_mode;").ConfigureAwait(false));
            Assert.AreEqual(2L, await ScalarLongAsync(connection, "PRAGMA synchronous;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "PRAGMA foreign_keys;").ConfigureAwait(false));
            Assert.AreEqual(11L, await ScalarLongAsync(connection, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            var contextJson = await ScalarBytesAsync(
                connection, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false);
            var contextText = System.Text.Encoding.UTF8.GetString(contextJson);
            Assert.IsFalse(contextText.Contains("-31.2733", StringComparison.Ordinal));
            Assert.IsFalse(contextText.Contains("149.07", StringComparison.Ordinal));
            Assert.IsFalse(contextText.Contains("Australia/Sydney", StringComparison.Ordinal));
            var contextSha256 = await ScalarStringAsync(
                connection, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false);
            var envelope = CaptureLaneEnvelopeSerializer.Deserialize(contextJson, contextSha256);
            Assert.IsNull(envelope.Configuration.DeploymentLocation);
            Assert.IsTrue(envelope.Configuration.DeploymentLocationRedacted);
            Assert.AreEqual(new ObservatoryLocation(0, 0, 0, "UTC"), envelope.Configuration.Observatory);

            using var browser = new FileSystemFrameStorageService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSystemFrameStorageService>.Instance);
            var listed = browser.List(root, DateOnly.FromDateTime(first.Manifest.Descriptor.Timing.ExposureStartedUtc.UtcDateTime), FrameArtifactRole.Raw, 10);
            Assert.HasCount(1, listed);
            Assert.AreEqual(first.Manifest.Descriptor.Artifact.ArtifactId, parsed.Document.Manifest.Descriptor.Artifact.ArtifactId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_BacklogOverlayUpdatesTotalsWithoutReplacingAcceptingBase()
    {
        var root = CreateRoot();
        try
        {
            var state = new RawIngressState(TimeProvider.System);
            using var ingress = CreateIngress(root, state);
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            state.SetProjectedSceneBacklog(2);

            await ingress.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(3), [1, 2, 3, 4]), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
            Assert.AreEqual("projected-scene-backlog", state.Snapshot.Reason);
            Assert.AreEqual(1, state.Snapshot.PendingCount);
            state.SetProjectedSceneBacklog(0);
            Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
            Assert.AreEqual("accepting", state.Snapshot.Reason);
            Assert.AreEqual(1, state.Snapshot.PendingCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task HeldStateRefresh_BacklogDrainRestoresOriginalBaseStatus()
    {
        var root = CreateRoot();
        try
        {
            var state = new RawIngressState(TimeProvider.System);
            using var ingress = CreateIngress(root, state);
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            state.Set(RawIngressAvailability.Degraded, "index-projection-failed");
            state.SetProjectedSceneBacklog(3);
            var configuration = CreateConfiguration();
            var receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(Timestamp(4), [4, 4, 4, 4]), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var lease = await ingress.ClaimAsync(
                new CaptureLaneDefinition("standard", true, true, true, new string('A', 64)),
                "test-owner", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await ingress.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);

            state.SetProjectedSceneBacklog(0);

            Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
            Assert.AreEqual("index-projection-failed", state.Snapshot.Reason);
            Assert.AreEqual(1, state.Snapshot.PendingCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_EnrichedCycleEvidenceIsDurableAndDuplicateIsIdempotent()
    {
        var root = CreateRoot();
        try
        {
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            var submission = CreateEnrichedSubmission(Timestamp(2).AddMinutes(10), [1, 2, 3, 4]);
            var expected = submission.CycleEvidence;

            var first = await ingress.AcceptAsync(
                CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(first);
            Assert.AreEqual(RawIngressOutcome.Committed, first.Outcome);
            Assert.AreEqual(expected, first.Manifest.Descriptor.CycleEvidence);
            Assert.IsNotNull(first.Manifest.Descriptor.Timing.SetpointAppliedUtc);
            Assert.IsTrue(submission.Request.RequestedStartUtc < expected!.ModuleCallStartedUtc);
            Assert.IsTrue(submission.Request.RequestedStartUtc < first.Manifest.Descriptor.Timing.ExposureStartedUtc);

            var sidecarPath = Path.ChangeExtension(first.StoredFrame.AbsolutePath, ".json");
            var sidecarBefore = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            var sidecar = CaptureContractJson.ParseManifest(sidecarBefore);
            Assert.IsTrue(sidecar.IsValid, sidecar.Validation.ReasonCode);
            Assert.AreEqual(expected, sidecar.Document!.Manifest!.Descriptor.CycleEvidence);

            byte[] journalBefore;
            byte[] contextBefore;
            string contextShaBefore;
            string descriptorShaBefore;
            string manifestShaBefore;
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                journalBefore = await ScalarBytesAsync(
                    connection, "SELECT manifest_json FROM raw_captures;").ConfigureAwait(false);
                contextBefore = await ScalarBytesAsync(
                    connection, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false);
                contextShaBefore = await ScalarStringAsync(
                    connection, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false);
                descriptorShaBefore = await ScalarStringAsync(
                    connection, "SELECT descriptor_sha256 FROM raw_captures;").ConfigureAwait(false);
                manifestShaBefore = await ScalarStringAsync(
                    connection, "SELECT manifest_sha256 FROM raw_captures;").ConfigureAwait(false);
            }
            var journal = CaptureContractJson.ParseManifest(journalBefore);
            Assert.IsTrue(journal.IsValid, journal.Validation.ReasonCode);
            Assert.AreEqual(expected, journal.Document!.Manifest!.Descriptor.CycleEvidence);
            var envelope = CaptureLaneEnvelopeSerializer.Deserialize(contextBefore, contextShaBefore);
            Assert.IsNull(envelope.Submission.Result.Frame);
            Assert.AreEqual(expected, envelope.Submission.CycleEvidence);

            var duplicate = await ingress.AcceptAsync(
                CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(duplicate);
            Assert.AreEqual(RawIngressOutcome.Existing, duplicate.Outcome);
            Assert.AreEqual(expected, duplicate.Manifest.Descriptor.CycleEvidence);
            var finalValidation = duplicate.Manifest.Validate();
            Assert.IsTrue(finalValidation.IsValid, finalValidation.ReasonCode);
            CollectionAssert.AreEqual(sidecarBefore, await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false));
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            CollectionAssert.AreEqual(journalBefore, await ScalarBytesAsync(
                verify, "SELECT manifest_json FROM raw_captures;").ConfigureAwait(false));
            CollectionAssert.AreEqual(contextBefore, await ScalarBytesAsync(
                verify, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false));
            Assert.AreEqual(contextShaBefore, await ScalarStringAsync(
                verify, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false));
            Assert.AreEqual(descriptorShaBefore, await ScalarStringAsync(
                verify, "SELECT descriptor_sha256 FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(manifestShaBefore, await ScalarStringAsync(
                verify, "SELECT manifest_sha256 FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_LegacyRetryWithEnrichedEvidenceKeepsImmutableLegacyEvidence()
    {
        var root = CreateRoot();
        try
        {
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            var enriched = CreateEnrichedSubmission(Timestamp(2).AddMinutes(20), [5, 6, 7, 8]);
            Assert.IsNotNull(enriched.CycleEvidence);
            Assert.IsNotNull(enriched.Result.AcquisitionTiming!.SetpointAppliedUtc);
            var legacy = enriched with
            {
                CycleEvidence = null,
                Result = enriched.Result with
                {
                    AcquisitionTiming = enriched.Result.AcquisitionTiming! with { SetpointAppliedUtc = null }
                }
            };
            var first = await ingress.AcceptAsync(
                CreateConfiguration(), legacy, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(first);
            Assert.AreEqual(RawIngressOutcome.Committed, first.Outcome);
            Assert.IsNull(first.Manifest.Descriptor.CycleEvidence);
            Assert.IsNull(first.Manifest.Descriptor.Timing.SetpointAppliedUtc);
            var sidecarPath = Path.ChangeExtension(first.StoredFrame.AbsolutePath, ".json");
            var sidecarBefore = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            var legacySidecar = CaptureContractJson.ParseManifest(sidecarBefore);
            Assert.IsTrue(legacySidecar.IsValid, legacySidecar.Validation.ReasonCode);
            Assert.IsNull(legacySidecar.Document!.Manifest!.Descriptor.CycleEvidence);
            Assert.IsNull(legacySidecar.Document.Manifest.Descriptor.Timing.SetpointAppliedUtc);
            byte[] journalBefore;
            byte[] contextBefore;
            string contextShaBefore;
            string descriptorShaBefore;
            string manifestShaBefore;
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                journalBefore = await ScalarBytesAsync(
                    connection, "SELECT manifest_json FROM raw_captures;").ConfigureAwait(false);
                contextBefore = await ScalarBytesAsync(
                    connection, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false);
                contextShaBefore = await ScalarStringAsync(
                    connection, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false);
                descriptorShaBefore = await ScalarStringAsync(
                    connection, "SELECT descriptor_sha256 FROM raw_captures;").ConfigureAwait(false);
                manifestShaBefore = await ScalarStringAsync(
                    connection, "SELECT manifest_sha256 FROM raw_captures;").ConfigureAwait(false);
            }

            var retry = await ingress.AcceptAsync(
                CreateConfiguration(), enriched, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(retry);
            Assert.AreEqual(RawIngressOutcome.Existing, retry.Outcome);
            Assert.IsNull(retry.Manifest.Descriptor.CycleEvidence);
            Assert.IsNull(retry.Manifest.Descriptor.Timing.SetpointAppliedUtc);
            var retryValidation = retry.Manifest.Validate();
            Assert.IsTrue(retryValidation.IsValid, retryValidation.ReasonCode);
            var sidecarAfter = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            CollectionAssert.AreEqual(sidecarBefore, sidecarAfter);
            var finalSidecar = CaptureContractJson.ParseManifest(sidecarAfter);
            Assert.IsTrue(finalSidecar.IsValid, finalSidecar.Validation.ReasonCode);
            Assert.IsNull(finalSidecar.Document!.Manifest!.Descriptor.CycleEvidence);
            Assert.IsNull(finalSidecar.Document.Manifest.Descriptor.Timing.SetpointAppliedUtc);
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            CollectionAssert.AreEqual(journalBefore, await ScalarBytesAsync(
                verify, "SELECT manifest_json FROM raw_captures;").ConfigureAwait(false));
            CollectionAssert.AreEqual(contextBefore, await ScalarBytesAsync(
                verify, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false));
            Assert.AreEqual(contextShaBefore, await ScalarStringAsync(
                verify, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false));
            Assert.AreEqual(descriptorShaBefore, await ScalarStringAsync(
                verify, "SELECT descriptor_sha256 FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(manifestShaBefore, await ScalarStringAsync(
                verify, "SELECT manifest_sha256 FROM raw_captures;").ConfigureAwait(false));
            var envelope = CaptureLaneEnvelopeSerializer.Deserialize(contextBefore, contextShaBefore);
            Assert.IsNull(envelope.Submission.CycleEvidence);
            Assert.IsNull(envelope.Submission.Result.AcquisitionTiming!.SetpointAppliedUtc);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_PersistsAuthoritativeLowerDepthLayoutWithoutInference()
    {
        var root = CreateRoot();
        try
        {
            var payload = new byte[8];
            var layout = CreateLowerDepthLayout() with
            {
                Readout = new FrameReadoutDescriptor(
                    4, 4, 0, 0, 4, 4, 2, 2, FrameBinningAlgorithm.DigitalAverageV1, null, null)
            };
            var submission = WithFrameLayout(CreateSubmission(Timestamp(3), payload), layout);
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));

            var receipt = await ingress.AcceptAsync(
                CreateMono16Configuration(), submission, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(receipt);
            Assert.AreEqual(layout, receipt.Manifest.Descriptor.Layout);
            var sidecar = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(
                Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")).ConfigureAwait(false));
            Assert.IsTrue(sidecar.IsValid, sidecar.Validation.ReasonCode);
            Assert.AreEqual(CaptureManifestCompleteness.Complete, sidecar.Document!.Completeness);
            Assert.AreEqual(layout, sidecar.Document.Manifest!.Descriptor.Layout);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_RetryDoesNotRewriteLegacyLayoutMissingAdditiveFacts()
    {
        var root = CreateRoot();
        try
        {
            var payload = new byte[8];
            var completeLayout = CreateLowerDepthLayout() with
            {
                Readout = new FrameReadoutDescriptor(
                    2, 2, 0, 0, 2, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null)
            };
            var legacyLayout = completeLayout with
            {
                SampleDepthBits = 16,
                Readout = null,
                StoredCodeTransform = null,
                LevelCodeSpace = null
            };
            var legacySubmission = WithFrameLayout(CreateSubmission(Timestamp(4), payload), legacyLayout);
            var enrichedSubmission = WithFrameLayout(CreateSubmission(Timestamp(4), payload), completeLayout);
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));

            var first = await ingress.AcceptAsync(
                CreateMono16Configuration(), legacySubmission, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(first);
            var sidecarPath = Path.ChangeExtension(first.StoredFrame.AbsolutePath, ".json");
            var sidecarBefore = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);

            var retry = await ingress.AcceptAsync(
                CreateMono16Configuration(), enrichedSubmission, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(retry);
            Assert.AreEqual(RawIngressOutcome.Existing, retry.Outcome);
            Assert.IsNull(retry.Manifest.Descriptor.Layout.StoredCodeTransform);
            Assert.IsNull(retry.Manifest.Descriptor.Layout.LevelCodeSpace);
            Assert.IsNull(retry.Manifest.Descriptor.Layout.Readout);
            CollectionAssert.AreEqual(sidecarBefore, await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void DescriptorFactory_RejectsFrameAndAuthoritativeLayoutMismatch()
    {
        var submission = WithFrameLayout(
            CreateSubmission(Timestamp(5), new byte[8]),
            CreateLowerDepthLayout() with { Width = 1 });

        Assert.ThrowsExactly<InvalidOperationException>(() => RawCaptureDescriptorFactory.Create(
            CreateMono16Configuration(),
            submission,
            new RawCaptureIdentity("agent-94", 1, Guid.NewGuid(), Guid.NewGuid()),
            new string('A', 64),
            Timestamp(6)));
    }

    [TestMethod]
    public async Task AcceptAsync_FailedOrInvalidCycleEvidenceDoesNotPublishDurableSuccess()
    {
        var root = CreateRoot();
        try
        {
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            var enriched = CreateEnrichedSubmission(Timestamp(2).AddMinutes(30), [9, 10, 11, 12]);
            var invalid = enriched with
            {
                CycleEvidence = enriched.CycleEvidence! with
                {
                    Decision = enriched.CycleEvidence.Decision with { ActiveGain = 2 }
                }
            };

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await ingress.AcceptAsync(
                    CreateConfiguration(), invalid, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var failed = enriched with { Result = enriched.Result with { Frame = null } };
            Assert.IsNull(await ingress.AcceptAsync(
                CreateConfiguration(), failed, CancellationToken.None).ConfigureAwait(false));

            Assert.IsFalse(Directory.Exists(Path.Combine(root, "frames")) &&
                Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.json", SearchOption.AllDirectories).Any());
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM capture_lane_contexts;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_CameraNativeSetpointFailureEvidenceIsCommitted()
    {
        var root = CreateRoot();
        try
        {
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            var submission = CreateCameraNativeSetpointFailureSubmission(
                Timestamp(2).AddMinutes(40),
                [13, 14, 15, 16]);

            var receipt = await ingress.AcceptAsync(
                CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(receipt);
            Assert.AreEqual(RawIngressOutcome.Committed, receipt.Outcome);
            var evidence = receipt.Manifest.Descriptor.CycleEvidence;
            Assert.IsNotNull(evidence);
            Assert.AreEqual(AutomaticControlOwnership.CameraNative, evidence.ExposureControl);
            Assert.AreEqual(AutomaticControlOwnership.Disabled, evidence.GainControl);
            Assert.AreNotEqual(evidence.Decision.ActiveExposure, evidence.Decision.DecidedExposure);
            Assert.AreEqual(evidence.Decision.ActiveGain, evidence.Decision.DecidedGain);
            Assert.AreEqual(CaptureControlDecisionReason.SetpointApplicationFailed, evidence.Decision.Reason);
            Assert.IsNull(evidence.Decision.SetpointAppliedUtc);
            Assert.IsNull(receipt.Manifest.Descriptor.Timing.SetpointAppliedUtc);
            var validation = receipt.Manifest.Validate();
            Assert.IsTrue(validation.IsValid, validation.ReasonCode);

            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual("committed", await ScalarStringAsync(
                connection, "SELECT state FROM raw_captures;").ConfigureAwait(false));
            var persisted = CaptureContractJson.ParseManifest(await ScalarBytesAsync(
                connection, "SELECT manifest_json FROM raw_captures;").ConfigureAwait(false));
            Assert.IsTrue(persisted.IsValid, persisted.Validation.ReasonCode);
            Assert.AreEqual(evidence, persisted.Document!.Manifest!.Descriptor.CycleEvidence);
            Assert.IsNull(persisted.Document.Manifest.Descriptor.Timing.SetpointAppliedUtc);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Restart_AllocatesIncreasingPerAgentSequence()
    {
        var root = CreateRoot();
        try
        {
            var configuration = CreateConfiguration();
            RawCaptureReceipt first;
            using (var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                first = (await ingress.AcceptAsync(
                    configuration,
                    CreateSubmission(Timestamp(3), [1, 1, 1, 1]),
                    CancellationToken.None).ConfigureAwait(false))!;
            }
            using var restarted = CreateIngress(root, new RawIngressState(TimeProvider.System));
            var second = await restarted.AcceptAsync(
                configuration,
                CreateSubmission(Timestamp(3).AddSeconds(1), [2, 2, 2, 2]),
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(second);
            Assert.AreEqual(first.Manifest.Descriptor.Capture.CaptureSequence + 1, second.Manifest.Descriptor.Capture.CaptureSequence);
            Assert.AreNotEqual(first.Manifest.Descriptor.Capture.CaptureId, second.Manifest.Descriptor.Capture.CaptureId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_WhenCapacityCannotFitNextPayload_RefusesWithoutCommittedSuccess()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 10
            });
            var state = new RawIngressState(TimeProvider.System);
            using var ingress = new RawCaptureIngress(
                options,
                new FixedCapacityProvider(13),
                state,
                TimeProvider.System,
                new RawIngressTelemetry(state),
                NullLogger<RawCaptureIngress>.Instance,
                new NullRawIngressFaultInjector());

            await Assert.ThrowsExactlyAsync<IOException>(async () => await ingress.AcceptAsync(
                CreateConfiguration(),
                CreateSubmission(Timestamp(4), [1, 2, 3, 4]),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            Assert.AreEqual("capacity-exhausted", state.Snapshot.Reason);
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_RecoversValidUnjournaledPairAndRepairsMissingIndex()
    {
        var root = CreateRoot();
        try
        {
            RawCaptureReceipt accepted;
            using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                accepted = (await original.AcceptAsync(
                    CreateConfiguration(),
                    CreateSubmission(Timestamp(5), [5, 5, 5, 5]),
                    CancellationToken.None).ConfigureAwait(false))!;
            }
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty })
            {
                File.Delete(Path.Combine(root, "journal", string.Concat("raw-ingress.db", suffix)));
            }
            var indexPath = Path.Combine(root, "index", "frames_2026-07-14.jsonl");
            File.Delete(indexPath);
            var recoveryUtc = new DateTimeOffset(2030, 1, 2, 8, 9, 10, TimeSpan.FromHours(5));
            var timeProvider = new FixedTimeProvider(recoveryUtc);
            var state = new RawIngressState(timeProvider);
            using var recovered = CreateIngress(root, state, timeProvider: timeProvider);

            await recovered.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
            Assert.AreEqual(1, state.Snapshot.PendingCount);
            Assert.IsTrue(File.Exists(indexPath));
            StringAssert.Contains(await File.ReadAllTextAsync(indexPath).ConfigureAwait(false),
                accepted.Manifest.Descriptor.Artifact.ArtifactId.ToString(), StringComparison.OrdinalIgnoreCase);
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(
                recoveryUtc.ToUniversalTime().ToUnixTimeMilliseconds(),
                await ScalarLongAsync(connection, "SELECT committed_unix_ms FROM raw_captures;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_CleansTemporaryAndQuarantinesPublishedPartialEvidence()
    {
        var root = CreateRoot();
        try
        {
            var rawDirectory = Path.Combine(root, "frames", "2026", "07", "14", "Raw");
            Directory.CreateDirectory(rawDirectory);
            var temporaryPath = Path.Combine(rawDirectory, "capture.bin.temporary.tmp");
            var orphanPath = Path.Combine(rawDirectory, "capture.bin");
            await File.WriteAllBytesAsync(temporaryPath, [1, 2]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(orphanPath, [3, 4]).ConfigureAwait(false);
            var state = new RawIngressState(TimeProvider.System);
            using (var ingress = CreateIngress(root, state))
            {
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(File.Exists(temporaryPath));
                Assert.IsFalse(File.Exists(orphanPath));
                Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
                Assert.AreEqual(1, state.Snapshot.QuarantineCount);
                Assert.AreEqual(2, state.Snapshot.QuarantineBytes);
                Assert.HasCount(1, Directory.EnumerateFiles(Path.Combine(root, "quarantine"), "capture.bin", SearchOption.AllDirectories));
                using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
                Assert.AreEqual(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_ingress_reconciliation;").ConfigureAwait(false));
            }
            var restartedState = new RawIngressState(TimeProvider.System);
            using var restarted = CreateIngress(root, restartedState);
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(RawIngressAvailability.Degraded, restartedState.Snapshot.Availability);
            Assert.AreEqual(1L, restartedState.Snapshot.QuarantineCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenCommittedPayloadIsMissing_RemainsUnhealthyAndRefusesStartup()
    {
        var root = CreateRoot();
        try
        {
            string payloadPath;
            using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                var accepted = await original.AcceptAsync(
                    CreateConfiguration(),
                    CreateSubmission(Timestamp(6), [6, 6, 6, 6]),
                    CancellationToken.None).ConfigureAwait(false);
                payloadPath = accepted!.StoredFrame.AbsolutePath;
            }
            SqliteConnection.ClearAllPools();
            File.Delete(payloadPath);
            var state = new RawIngressState(TimeProvider.System);
            using var restarted = CreateIngress(root, state);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual("missing_evidence", await ScalarStringAsync(
                connection, "SELECT state FROM raw_captures;").ConfigureAwait(false));
            await connection.CloseAsync().ConfigureAwait(false);
            await File.WriteAllBytesAsync(payloadPath, [6, 6, 6, 6]).ConfigureAwait(false);
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
            using var repaired = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual("committed", await ScalarStringAsync(
                repaired, "SELECT state FROM raw_captures;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenSchemaIsNewer_FailsClosedWithoutModification()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 12;";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var state = new RawIngressState(TimeProvider.System);
            using var telemetry = new RawIngressTelemetry(state);
            using var ingress = new RawCaptureIngress(
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    RawIngressReserveBytes = 0,
                    RawIngressSqliteBusyTimeoutSeconds = 1
                }),
                new FixedCapacityProvider(long.MaxValue),
                state,
                TimeProvider.System,
                telemetry,
                NullLogger<RawCaptureIngress>.Instance,
                new NullRawIngressFaultInjector());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(12L, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            Assert.AreEqual(0L, telemetry.CheckpointCount);
            Assert.AreEqual(0L, telemetry.CheckpointFailureCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_FaultAtEveryPublicationAndCommitBoundary_ConvergesToOneCapture()
    {
        foreach (var point in Enum.GetValues<RawIngressFaultPoint>())
        {
            var root = CreateRoot();
            try
            {
                var state = new RawIngressState(TimeProvider.System);
                var faultInjector = new OneShotFaultInjector(point);
                using var ingress = CreateIngress(root, state, faultInjector);
                var configuration = CreateConfiguration();
                var submission = CreateEnrichedSubmission(Timestamp(7), [7, 7, 7, 7]);

                await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(async () =>
                    await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false),
                    $"Fault point {point} did not interrupt ingress.").ConfigureAwait(false);
                if (point == RawIngressFaultPoint.AfterJournalCommit)
                {
                    Assert.AreEqual(1L, state.Snapshot.PendingCount);
                    Assert.AreEqual(4L, state.Snapshot.PendingBytes);
                }
                var recovered = await ingress.AcceptAsync(
                    configuration, submission, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(recovered, point.ToString());
                Assert.IsTrue(File.Exists(recovered.StoredFrame.AbsolutePath), point.ToString());
                Assert.IsTrue(File.Exists(Path.ChangeExtension(recovered.StoredFrame.AbsolutePath, ".json")), point.ToString());
                var validation = recovered.Manifest.Validate();
                Assert.IsTrue(validation.IsValid, $"{point}: {validation.ReasonCode}");
                Assert.IsNotNull(recovered.Manifest.Descriptor.CycleEvidence, point.ToString());
                Assert.IsNotNull(recovered.Manifest.Descriptor.Timing.SetpointAppliedUtc, point.ToString());
                var payloadPublishedUtc = faultInjector.PayloadPublishedUtc;
                Assert.IsNotNull(payloadPublishedUtc, point.ToString());
                Assert.IsTrue(
                    payloadPublishedUtc.GetValueOrDefault() <= recovered.Manifest.Descriptor.Timing.DurableIngressUtc,
                    $"{point}: payload publication must not follow durable ingress.");
                using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
                Assert.AreEqual(1L, await ScalarLongAsync(
                    connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false), point.ToString());
                var scenarioId = point switch
                {
                    RawIngressFaultPoint.AfterMigrationTransactionBegan => "raw-boundary-migration-transaction-began",
                    RawIngressFaultPoint.BeforeMigrationCommit => "raw-boundary-before-migration-commit",
                    RawIngressFaultPoint.ValidationCompleted => "raw-boundary-validation-completed",
                    RawIngressFaultPoint.PayloadDirectorySynced => "raw-boundary-payload-directory-sync",
                    RawIngressFaultPoint.SidecarWritten => "raw-boundary-sidecar-written",
                    RawIngressFaultPoint.SidecarFlushed => "raw-boundary-sidecar-flushed",
                    RawIngressFaultPoint.SidecarPublished => "raw-boundary-sidecar-published",
                    RawIngressFaultPoint.BeforeJournalCommit => "raw-boundary-before-journal-transaction",
                    RawIngressFaultPoint.AfterJournalTransactionBegan => "raw-boundary-journal-transaction-began",
                    RawIngressFaultPoint.AfterJournalRowInserted => "raw-boundary-journal-row-inserted",
                    RawIngressFaultPoint.BeforeIndexProjection => "raw-boundary-before-index-projection",
                    RawIngressFaultPoint.AfterIndexProjection => "raw-boundary-after-index-projection",
                    _ => null
                };
                if (scenarioId is not null)
                {
                    await Phase14ScenarioEvidence.RecordAsync(
                        scenarioId,
                        $"fault-point-{point}",
                        point.ToString(),
                        ["fault-observed", "retry-converged-once", "payload-and-sidecar-published", "manifest-valid"])
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }
    }

    [TestMethod]
    public async Task PublicationState_DistinguishesBeforeAndAfterJournalCommitFaults()
    {
        foreach (var (point, expected) in new[]
                 {
                     (RawIngressFaultPoint.SidecarPublished, RawCapturePublicationState.Unknown),
                     (RawIngressFaultPoint.SidecarDirectorySynced, RawCapturePublicationState.Unknown),
                     (RawIngressFaultPoint.BeforeJournalCommit, RawCapturePublicationState.Unknown),
                     (RawIngressFaultPoint.AfterJournalCommit, RawCapturePublicationState.Committed),
                     (RawIngressFaultPoint.BeforeIndexProjection, RawCapturePublicationState.Committed),
                     (RawIngressFaultPoint.BeforeWakeUpNotification, RawCapturePublicationState.Committed)
                 })
        {
            var root = CreateRoot();
            try
            {
                using var ingress = CreateIngress(
                    root, new RawIngressState(TimeProvider.System), new OneShotFaultInjector(point));
                var configuration = CreateConfiguration();
                var submission = CreateEnrichedSubmission(Timestamp(8), [8, 8, 8, 8]);
                await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(async () =>
                    await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);

                var state = await ((IRawCaptureIngress)ingress).GetPublicationStateAsync(
                    configuration, submission, CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(expected, state, point.ToString());
            }
            finally
            {
                DeleteRoot(root);
            }
        }
    }

    [TestMethod]
    public async Task OwnedStageKeys_AcceptsValidManifestReferenceFromNewerStageSchema()
    {
        var root = CreateRoot();
        try
        {
            var stageKey = new string('A', 64);
            var submission = CreateSubmission(Timestamp(8).AddMinutes(1), [1, 2, 3, 4]);
            var frame = submission.Result.Frame!;
            submission = submission with
            {
                Result = submission.Result with
                {
                    Frame = frame with
                    {
                        Metadata = frame.Metadata with
                        {
                            Scene = new SceneProvenance(
                                new string('B', 64), "rig-v1", "catalog", "1", new string('C', 64),
                                "EquidistantFisheye", "projection-v1", "astronomy-v1", "sensor-v1",
                                ProjectedSceneStageSchemaVersion: "projected-scene-stage-v2",
                                ProjectedSceneStageKey: stageKey)
                        }
                    }
                }
            };
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            await ingress.AcceptAsync(CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false);

            var owned = await ingress.GetOwnedStageKeysAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(owned.Contains(stageKey));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_SameCaptureIdentityWithDifferentBytes_IsConflictWithoutOverwrite()
    {
        var root = CreateRoot();
        try
        {
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            var configuration = CreateConfiguration();
            var timestamp = Timestamp(8);
            var accepted = await ingress.AcceptAsync(
                configuration,
                CreateSubmission(timestamp, [1, 1, 1, 1]),
                CancellationToken.None).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<RawIngressConflictException>(async () =>
                await ingress.AcceptAsync(
                    configuration,
                    CreateSubmission(timestamp, [2, 2, 2, 2]),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var changedConfiguration = configuration with
            {
                Rig = configuration.Rig with { ProfileVersion = "rig-v2" }
            };
            await Assert.ThrowsExactlyAsync<RawIngressConflictException>(async () =>
                await ingress.AcceptAsync(
                    changedConfiguration,
                    CreateSubmission(timestamp, [1, 1, 1, 1]),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            CollectionAssert.AreEqual(
                new byte[] { 1, 1, 1, 1 },
                await File.ReadAllBytesAsync(accepted!.StoredFrame.AbsolutePath).ConfigureAwait(false));
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_WhenWriterLockExceedsBusyTimeout_FailsWithoutFalseCommitThenRecovers()
    {
        var root = CreateRoot();
        try
        {
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var lockConnection = await OpenJournalAsync(root).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
            using var lockTransaction = lockConnection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
            var submission = CreateSubmission(Timestamp(9), [9, 9, 9, 9]);

            await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
                await ingress.AcceptAsync(
                    CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await lockTransaction.RollbackAsync().ConfigureAwait(false);
            var recovered = await ingress.AcceptAsync(
                CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(recovered);
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenRootIsSymbolicLink_FailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var parent = Path.Combine(Path.GetTempPath(), "hvo-raw-ingress-tests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "target");
        var root = Path.Combine(parent, "root-link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(root, target);
        try
        {
            var state = new RawIngressState(TimeProvider.System);
            using var ingress = CreateIngress(root, state);

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            Assert.IsFalse(File.Exists(Path.Combine(target, "journal", "raw-ingress.db")));
        }
        finally
        {
            Directory.Delete(root);
            Directory.Delete(target, recursive: true);
            Directory.Delete(parent, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_UnclaimedSidecarCannotQuarantineCommittedPayload()
    {
        var root = CreateRoot();
        try
        {
            RawCaptureReceipt accepted;
            using (var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                accepted = (await ingress.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(11), [11, 11, 11, 11]),
                    CancellationToken.None).ConfigureAwait(false))!;
            }
            var descriptor = accepted.Manifest.Descriptor with
            {
                Capture = accepted.Manifest.Descriptor.Capture with
                {
                    CaptureSequence = 2,
                    CaptureId = Guid.NewGuid()
                },
                Artifact = accepted.Manifest.Descriptor.Artifact with { ArtifactId = Guid.NewGuid() }
            };
            var conflicting = accepted.Manifest with { Descriptor = descriptor };
            var extraSidecar = Path.Combine(Path.GetDirectoryName(accepted.StoredFrame.AbsolutePath)!, "conflicting.json");
            await File.WriteAllBytesAsync(extraSidecar, CaptureContractJson.Serialize(conflicting)).ConfigureAwait(false);
            var state = new RawIngressState(TimeProvider.System);
            using var restarted = CreateIngress(root, state);

            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(accepted.StoredFrame.AbsolutePath));
            Assert.IsFalse(File.Exists(extraSidecar));
            Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual("committed", await ScalarStringAsync(connection, "SELECT state FROM raw_captures;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenAnotherIngressOwnsRoot_FailsClosed()
    {
        var root = CreateRoot();
        try
        {
            using var owner = CreateIngress(root, new RawIngressState(TimeProvider.System));
            await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var contenderState = new RawIngressState(TimeProvider.System);
            using var contender = CreateIngress(root, contenderState);

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await contender.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(RawIngressAvailability.Unhealthy, contenderState.Snapshot.Availability);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_SidecarWithoutPayload_IsQuarantinedWithoutNormalWork()
    {
        var root = CreateRoot();
        try
        {
            string sidecarPath;
            using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                var accepted = await original.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(12), [12, 12, 12, 12]),
                    CancellationToken.None).ConfigureAwait(false);
                sidecarPath = Path.ChangeExtension(accepted!.StoredFrame.AbsolutePath, ".json");
                File.Delete(accepted.StoredFrame.AbsolutePath);
            }
            SqliteConnection.ClearAllPools();
            foreach (var suffix in DatabaseSuffixes)
            {
                File.Delete(Path.Combine(root, "journal", string.Concat("raw-ingress.db", suffix)));
            }
            var state = new RawIngressState(TimeProvider.System);
            using var restarted = CreateIngress(root, state);

            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(sidecarPath));
            Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
            Assert.AreEqual(0L, state.Snapshot.PendingCount);
            Assert.AreEqual(1L, state.Snapshot.QuarantineCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_WhenCapacityProbeFails_RefusesWithoutStoredSuccess()
    {
        var root = CreateRoot();
        try
        {
            var state = new RawIngressState(TimeProvider.System);
            using var telemetry = new RawIngressTelemetry(state);
            using var ingress = new RawCaptureIngress(
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 }),
                new ThrowingCapacityProvider(),
                state,
                TimeProvider.System,
                telemetry,
                NullLogger<RawCaptureIngress>.Instance,
                new NullRawIngressFaultInjector());

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await ingress.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(13), [13, 13, 13, 13]),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual("capacity-probe-failed", state.Snapshot.Reason);
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_WhenCanceledWaitingForLifecycleLock_ReleasesAcceptGateAndPreservesHealth()
    {
        var root = CreateRoot();
        try
        {
            var state = new RawIngressState(TimeProvider.System);
            using var ingress = CreateIngress(root, state);
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var lifecycleGate = RawIngressLifecycleLock.ForRoot(root);
            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                    await ingress.AcceptAsync(
                        CreateConfiguration(), CreateSubmission(Timestamp(12).AddMinutes(1), [1, 2, 3, 4]), canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Release();
            }

            var accepted = await ingress.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(12).AddMinutes(1), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(accepted);
            Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_WhenCapacityFailsAfterCommit_PreservesDurableBacklogTotals()
    {
        var root = CreateRoot();
        try
        {
            var capacity = new MutableCapacityProvider(long.MaxValue);
            var state = new RawIngressState(TimeProvider.System);
            using var ingress = new RawCaptureIngress(
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 }),
                capacity,
                state,
                TimeProvider.System,
                new RawIngressTelemetry(state),
                NullLogger<RawCaptureIngress>.Instance,
                new NullRawIngressFaultInjector());
            await ingress.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(12).AddMinutes(2), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false);
            capacity.AvailableBytes = 0;

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await ingress.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(12).AddMinutes(3), [5, 6, 7, 8]), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(1L, state.Snapshot.PendingCount);
            Assert.AreEqual(4L, state.Snapshot.PendingBytes);
            Assert.AreEqual("capacity-exhausted", state.Snapshot.Reason);
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await ingress.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(12).AddMinutes(3), [5, 6, 7, 8]), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            capacity.AvailableBytes = long.MaxValue;
            var recovered = await ingress.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(12).AddMinutes(3), [5, 6, 7, 8]), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(recovered);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenDescendantDirectoryIsSymbolicLink_FailsBeforeFollowingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = CreateRoot();
        var target = string.Concat(root, "-target");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frames"));
            Directory.CreateDirectory(target);
            var marker = Path.Combine(target, "marker.bin");
            await File.WriteAllBytesAsync(marker, [1]).ConfigureAwait(false);
            Directory.CreateSymbolicLink(Path.Combine(root, "frames", "2026"), target);
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(marker));
        }
        finally
        {
            DeleteRoot(root);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenOrphanConflictsWithReservedAssignment_QuarantinesEvidence()
    {
        var root = CreateRoot();
        try
        {
            RawCaptureReceipt accepted;
            using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                accepted = (await original.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(12).AddMinutes(4), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false))!;
            }
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM raw_captures;";
                await delete.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var sidecarPath = Path.ChangeExtension(accepted.StoredFrame.AbsolutePath, ".json");
            var conflicting = accepted.Manifest with
            {
                Descriptor = accepted.Manifest.Descriptor with
                {
                    Capture = accepted.Manifest.Descriptor.Capture with { AgentId = "conflicting-agent" }
                }
            };
            await File.WriteAllBytesAsync(sidecarPath, CaptureContractJson.Serialize(conflicting)).ConfigureAwait(false);
            var state = new RawIngressState(TimeProvider.System);
            using var restarted = CreateIngress(root, state);

            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
            Assert.AreEqual(1L, state.Snapshot.QuarantineCount);
            Assert.IsFalse(File.Exists(accepted.StoredFrame.AbsolutePath));
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_ResumesPlannedSplitQuarantine()
    {
        var root = CreateRoot();
        try
        {
            using (var initialized = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                await initialized.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            var rawDirectory = Path.Combine(root, "frames", "2026", "07", "14", "Raw");
            Directory.CreateDirectory(rawDirectory);
            var payloadPath = Path.Combine(rawDirectory, "interrupted.bin");
            var sidecarPath = Path.Combine(rawDirectory, "interrupted.json");
            await File.WriteAllBytesAsync(payloadPath, [1, 2]).ConfigureAwait(false);
            await File.WriteAllTextAsync(sidecarPath, "{}").ConfigureAwait(false);
            var quarantineRelative = "quarantine/20260714/resume-test";
            var operation = new RawIngressPlannedQuarantine(
                "quarantine:resume-test",
                Path.GetRelativePath(root, payloadPath).Replace(Path.DirectorySeparatorChar, '/'),
                Path.GetRelativePath(root, sidecarPath).Replace(Path.DirectorySeparatorChar, '/'),
                quarantineRelative,
                "test-interruption",
                4);
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.PlanQuarantineAsync(operation, CancellationToken.None).ConfigureAwait(false);
            var quarantineDirectory = Path.Combine(root, "quarantine", "20260714", "resume-test");
            Directory.CreateDirectory(quarantineDirectory);
            File.Move(payloadPath, Path.Combine(quarantineDirectory, Path.GetFileName(payloadPath)));
            var state = new RawIngressState(TimeProvider.System);
            using var restarted = CreateIngress(root, state);

            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(Path.Combine(quarantineDirectory, "interrupted.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(quarantineDirectory, "interrupted.json")));
            Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual("completed", await ScalarStringAsync(
                verify, "SELECT operation_state FROM raw_ingress_reconciliation WHERE evidence_key = 'quarantine:resume-test';").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenJournalFileIsSymbolicLink_FailsWithoutFollowingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = CreateRoot();
        var target = string.Concat(root, "-database-target");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            await File.WriteAllBytesAsync(target, [1, 2, 3]).ConfigureAwait(false);
            File.CreateSymbolicLink(Path.Combine(root, "journal", "raw-ingress.db"), target);
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(target).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
            File.Delete(target);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_UsesExposureStartForCanonicalEvidencePath()
    {
        var root = CreateRoot();
        try
        {
            var exposureStarted = Timestamp(12).AddMinutes(10);
            var frameTimestamp = exposureStarted.AddSeconds(2);
            var frame = new CameraFrame(
                frameTimestamp, 2, 2, CameraPixelFormat.Mono8, new byte[] { 1, 2, 3, 4 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN, "Test"), 2);
            var submission = new CaptureLoopSubmission(
                new CaptureRequest(exposureStarted, TimeSpan.FromSeconds(1), CaptureMode.Still),
                new CaptureResult(
                    frame,
                    new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                    TimeSpan.Zero,
                    CaptureMode.Still,
                    false)
                {
                    AcquisitionTiming = new CaptureAcquisitionTiming(exposureStarted, exposureStarted.AddSeconds(1), frameTimestamp)
                },
                exposureStarted,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero);
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));

            var receipt = await ingress.AcceptAsync(CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(receipt);
            StringAssert.Contains(receipt.StoredFrame.RelativePath, "2026-07-14_12-10-00.000Z", StringComparison.Ordinal);
            Assert.AreEqual(exposureStarted, receipt.StoredFrame.TimestampUtc);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenLockFileIsSymbolicLink_FailsWithoutFollowingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = CreateRoot();
        var target = string.Concat(root, "-lock-target");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            await File.WriteAllBytesAsync(target, [4, 5, 6]).ConfigureAwait(false);
            File.CreateSymbolicLink(Path.Combine(root, "journal", "raw-ingress.lock"), target);
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(target).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
            File.Delete(target);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenLegacyShapedSidecarHasInvalidFacts_QuarantinesPair()
    {
        var root = CreateRoot();
        try
        {
            var rawDirectory = Path.Combine(root, "frames", "2026", "07", "14", "Raw");
            Directory.CreateDirectory(rawDirectory);
            var payloadPath = Path.Combine(rawDirectory, "malformed.bin");
            var sidecarPath = Path.Combine(rawDirectory, "malformed.json");
            await File.WriteAllBytesAsync(payloadPath, [1, 2]).ConfigureAwait(false);
            await File.WriteAllTextAsync(sidecarPath, """
                {"artifactId":null,"role":"Raw","timestampUtc":"invalid","width":0,"height":2,"pixelFormat":"Mono8"}
                """).ConfigureAwait(false);
            var state = new RawIngressState(TimeProvider.System);
            using var ingress = CreateIngress(root, state);

            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
            Assert.AreEqual(1L, state.Snapshot.QuarantineCount);
            Assert.IsFalse(File.Exists(payloadPath));
            Assert.IsFalse(File.Exists(sidecarPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenVersionZeroContainsObjects_FailsWithoutCompletingSchema()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE raw_capture_sequences (
                        agent_id TEXT PRIMARY KEY,
                        last_sequence INTEGER NOT NULL CHECK (last_sequence >= 0)
                    ) STRICT;
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ingress.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);

            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('raw_capture_sequences','raw_capture_assignments','raw_captures');").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenV6ContainsCoordinates_FailsWithoutScrubbing()
    {
        var root = CreateRoot();
        try
        {
            var location = DeploymentLocationSnapshot.Create(
                "siding-spring-synthetic", 1, "test", null,
                DateTimeOffset.UnixEpoch, null, -31.2733, 149.0700, 1165, "Australia/Sydney");
            var legacyObservatory = new ObservatoryLocation(12.345678, -98.765432, 543.21, "Pacific/Nauru");
            var configuration = CreateConfiguration() with
            {
                Observatory = legacyObservatory,
                DeploymentLocation = location
            };
            var submission = CreateSubmission(Timestamp(2), [1, 2, 3, 4]);
            using (var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                _ = await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false);
            }
            var lightweight = submission with
            {
                Result = submission.Result with { Frame = null, Artifacts = null }
            };
            var legacyJson = JsonSerializer.SerializeToUtf8Bytes(
                new CaptureLaneEnvelope(configuration, lightweight),
                WebJson);
            var legacyText = System.Text.Encoding.UTF8.GetString(legacyJson);
            Assert.IsTrue(legacyText.Contains("12.345678", StringComparison.Ordinal));
            Assert.IsTrue(legacyText.Contains("-98.765432", StringComparison.Ordinal));
            Assert.IsTrue(legacyText.Contains("Pacific/Nauru", StringComparison.Ordinal));
            var legacySha = Convert.ToHexString(SHA256.HashData(legacyJson));
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_lane_contexts SET context_json = $json, context_sha256 = $sha;
                    PRAGMA user_version = 6;
                    """;
                command.Parameters.AddWithValue("$json", legacyJson);
                command.Parameters.AddWithValue("$sha", legacySha);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var migrated = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    migrated.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            }
            using (var verify = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(6L, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
                var context = await ScalarBytesAsync(
                    verify, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false);
                var text = System.Text.Encoding.UTF8.GetString(context);
                Assert.IsTrue(text.Contains("12.345678", StringComparison.Ordinal));
                Assert.IsTrue(text.Contains("-98.765432", StringComparison.Ordinal));
                Assert.IsTrue(text.Contains("Pacific/Nauru", StringComparison.Ordinal));
                var migratedSha = await ScalarStringAsync(
                    verify, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false);
                Assert.IsFalse(CaptureLaneEnvelopeSerializer.Deserialize(context, migratedSha)
                    .Configuration.DeploymentLocationRedacted);
            }
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = Path.Combine(root, "journal", string.Concat("raw-ingress.db", suffix));
                if (!File.Exists(path))
                {
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                Assert.IsTrue(
                    bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("12.345678")) >= 0 ||
                    bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("-98.765432")) >= 0 ||
                    bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("Pacific/Nauru")) >= 0);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenV7IsPopulated_FailsWithoutRecreatingScheduleSchema()
    {
        var root = CreateRoot();
        try
        {
            using (var initialized = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                await initialized.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_control_state
                    SET state = 'paused', version = 7, updated_unix_ms = 1234
                    WHERE state_key = 1;
                    DROP TABLE capture_schedule_commands;
                    DROP TABLE capture_schedule_overrides;
                    DROP TABLE capture_schedule_activations;
                    DROP TABLE capture_schedule_state;
                    DROP TABLE capture_schedule_revisions;
                    PRAGMA user_version = 7;
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var migrated = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    migrated.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            }
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(7L, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual(7L, await ScalarLongAsync(
                verify, "SELECT version FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(verify, """
                SELECT COUNT(*) FROM sqlite_master
                WHERE name IN (
                    'capture_schedule_revisions', 'capture_schedule_state',
                    'capture_schedule_activations', 'capture_schedule_overrides',
                    'capture_schedule_commands');
                """).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenV8IsPopulated_FailsWithoutRecreatingCalibrationSchema()
    {
        var root = CreateRoot();
        try
        {
            RawCaptureReceipt receipt;
            using (var initialized = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                receipt = (await initialized.AcceptAsync(
                    CreateConfiguration(),
                    CreateSubmission(Timestamp(13), [13, 13, 13, 13]),
                    CancellationToken.None).ConfigureAwait(false))!;
            }
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_control_state
                    SET state = 'paused', version = 5, updated_unix_ms = 1234
                    WHERE state_key = 1;
                    DROP TABLE calibration_library_commands;
                    DROP TABLE calibration_library_activations;
                    DROP TABLE calibration_library_state;
                    DROP TABLE calibration_library_artifacts;
                    DROP TABLE calibration_acquisition_jobs;
                    DROP TABLE calibration_library_reconciliation;
                    DROP TABLE calibration_library_bundles;
                    PRAGMA user_version = 8;
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var migrated = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    migrated.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            }

            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(8L, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual(receipt.Manifest.Descriptor.Capture.CaptureId.ToString("N"),
                await ScalarStringAsync(verify, "SELECT capture_id FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(receipt.CommittedManifestSha256,
                await ScalarStringAsync(verify, "SELECT manifest_sha256 FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(5L, await ScalarLongAsync(
                verify, "SELECT version FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(verify, """
                SELECT COUNT(*) FROM sqlite_master
                WHERE name LIKE 'calibration_library_%'
                   OR name LIKE 'calibration_acquisition_%'
                   OR name LIKE 'ix_calibration_%';
                """).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenPopulatedVersionZeroLacksGallerySchema_FailsWithoutMutation()
    {
        var root = CreateRoot();
        try
        {
            var location = DeploymentLocationSnapshot.Create(
                "siding-spring-synthetic", 1, "test", null,
                DateTimeOffset.UnixEpoch, null, -31.2733, 149.0700, 1165, "Australia/Sydney");
            var legacyObservatory = new ObservatoryLocation(12.345678, -98.765432, 543.21, "Pacific/Nauru");
            var configuration = CreateConfiguration() with
            {
                Observatory = legacyObservatory,
                DeploymentLocation = location
            };
            var submission = CreateSubmission(Timestamp(2), [1, 2, 3, 4]);
            RawCaptureReceipt receipt;
            using (var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                receipt = (await ingress.AcceptAsync(
                    configuration,
                    submission,
                    CancellationToken.None).ConfigureAwait(false))!;
            }
            var legacyJson = JsonSerializer.SerializeToUtf8Bytes(
                new CaptureLaneEnvelope(configuration, submission with
                {
                    Result = submission.Result with { Frame = null, Artifacts = null }
                }),
                WebJson);
            var legacyText = System.Text.Encoding.UTF8.GetString(legacyJson);
            Assert.IsTrue(legacyText.Contains("12.345678", StringComparison.Ordinal));
            Assert.IsTrue(legacyText.Contains("-98.765432", StringComparison.Ordinal));
            Assert.IsTrue(legacyText.Contains("Pacific/Nauru", StringComparison.Ordinal));
            var legacySha = Convert.ToHexString(SHA256.HashData(legacyJson));
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP INDEX ix_raw_captures_gallery_origin;
                    DROP INDEX ix_raw_captures_gallery_state;
                    DROP INDEX ix_raw_captures_gallery_sequence;
                    DROP INDEX ix_raw_captures_gallery_time;
                    ALTER TABLE raw_captures DROP COLUMN evidence_origin;
                    UPDATE capture_lane_contexts SET context_json = $json, context_sha256 = $sha;
                    PRAGMA user_version = 0;
                    """;
                command.Parameters.AddWithValue("$json", legacyJson);
                command.Parameters.AddWithValue("$sha", legacySha);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var migrated = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    migrated.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            }

            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual(receipt.Manifest.Descriptor.Capture.CaptureId.ToString("N"),
                await ScalarStringAsync(verify, "SELECT capture_id FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(receipt.CommittedManifestSha256,
                await ScalarStringAsync(verify, "SELECT manifest_sha256 FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(
                verify,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_raw_captures_gallery_%';")
                .ConfigureAwait(false));
            var context = await ScalarBytesAsync(
                verify, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false);
            var contextSha = await ScalarStringAsync(
                verify, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false);
            Assert.IsFalse(CaptureLaneEnvelopeSerializer.Deserialize(context, contextSha)
                .Configuration.DeploymentLocationRedacted);
            var contextText = System.Text.Encoding.UTF8.GetString(context);
            Assert.IsTrue(contextText.Contains("12.345678", StringComparison.Ordinal));
            Assert.IsTrue(contextText.Contains("-98.765432", StringComparison.Ordinal));
            Assert.IsTrue(contextText.Contains("Pacific/Nauru", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenV2OrPendingNegativeContainsEvidence_FailsWithoutRehashing()
    {
        var root = CreateRoot();
        try
        {
            RawCaptureReceipt receipt;
            using (var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                receipt = (await ingress.AcceptAsync(
                    CreateConfiguration(),
                    CreateSubmission(Timestamp(2), [1, 2, 3, 4]),
                    CancellationToken.None).ConfigureAwait(false))!;
            }
            var sidecarPath = Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json");
            var canonical = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            var reformatted = System.Text.Encoding.UTF8.GetBytes(
                $"\n{System.Text.Encoding.UTF8.GetString(canonical)}");
            await File.WriteAllBytesAsync(sidecarPath, reformatted).ConfigureAwait(false);
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE raw_captures
                    SET manifest_json = $manifest, manifest_sha256 = $canonical_hash;
                    PRAGMA user_version = 2;
                    """;
                command.Parameters.AddWithValue("$manifest", reformatted);
                command.Parameters.AddWithValue(
                    "$canonical_hash",
                    CaptureContractJson.ComputeManifestSha256(receipt.Manifest));
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var state = new RawIngressState(TimeProvider.System);
            using (var migrated = CreateIngress(root, state))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    migrated.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            }

            using (var verify = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(2L, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
                Assert.AreEqual(
                    CaptureContractJson.ComputeManifestSha256(receipt.Manifest),
                    await ScalarStringAsync(verify, "SELECT manifest_sha256 FROM raw_captures;").ConfigureAwait(false));
                Assert.AreEqual("committed", await ScalarStringAsync(verify, "SELECT state FROM raw_captures;").ConfigureAwait(false));
            }
            using (var interrupted = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = interrupted.CreateCommand();
                command.CommandText = "PRAGMA user_version = -7;";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var resumed = CreateIngress(root, state))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    resumed.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            }
            using (var verify = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(-7L, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
                Assert.AreEqual(
                    CaptureContractJson.ComputeManifestSha256(receipt.Manifest),
                    await ScalarStringAsync(verify, "SELECT manifest_sha256 FROM raw_captures;").ConfigureAwait(false));
            }
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            Assert.IsTrue(File.Exists(receipt.StoredFrame.AbsolutePath));
            Assert.IsTrue(File.Exists(sidecarPath));

            var altered = receipt.Manifest with
            {
                Descriptor = receipt.Manifest.Descriptor with
                {
                    Capture = receipt.Manifest.Descriptor.Capture with
                    {
                        CaptureSequence = receipt.Manifest.Descriptor.Capture.CaptureSequence + 1
                    }
                }
            };
            var alteredJson = CaptureContractJson.Serialize(altered);
            await File.WriteAllBytesAsync(sidecarPath, alteredJson).ConfigureAwait(false);
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE raw_captures
                    SET manifest_json = $manifest, manifest_sha256 = $old_canonical_hash;
                    PRAGMA user_version = 2;
                    """;
                command.Parameters.AddWithValue("$manifest", alteredJson);
                command.Parameters.AddWithValue(
                    "$old_canonical_hash",
                    CaptureContractJson.ComputeManifestSha256(receipt.Manifest));
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using var rejected = CreateIngress(root, new RawIngressState(TimeProvider.System));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await rejected.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WhenDatabaseIsCorrupt_FailsClosed()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            await File.WriteAllTextAsync(Path.Combine(root, "journal", "raw-ingress.db"), "not-a-sqlite-database").ConfigureAwait(false);
            var state = new RawIngressState(TimeProvider.System);
            using var ingress = CreateIngress(root, state);

            await Assert.ThrowsAsync<SqliteException>(async () =>
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow("raw-ingress.lock")]
    [DataRow("raw-ingress.db")]
    public async Task InitializeAsync_WhenJournalEntryIsDanglingSymbolicLink_DoesNotCreateTarget(string fileName)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = CreateRoot();
        var target = string.Concat(root, "-dangling-target");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            File.CreateSymbolicLink(Path.Combine(root, "journal", fileName), target);
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(target));
        }
        finally
        {
            DeleteRoot(root);
            File.Delete(target);
        }
    }

    [TestMethod]
    public async Task ProcessKill_AtRawCommitBoundaries_RestartConvergesOnce()
    {
        byte[] payload = [10, 20, 30, 40];
        foreach (var point in new[]
                 {
                      RawIngressFaultPoint.PayloadPartiallyWritten,
                      RawIngressFaultPoint.PayloadWritten,
                      RawIngressFaultPoint.PayloadFlushed,
                      RawIngressFaultPoint.PayloadPublished,
                      RawIngressFaultPoint.BeforeJournalTransactionCommit,
                      RawIngressFaultPoint.AfterJournalCommit,
                      RawIngressFaultPoint.BeforeWakeUpNotification
                 })
        {
            var root = CreateRoot();
            try
            {
                var crash = await RunCrashChildAsync(root, point).ConfigureAwait(false);
                Assert.AreNotEqual(0, crash.ExitCode, point.ToString());
                StringAssert.Contains(
                    crash.Output,
                    $"Injected raw ingress process termination at {point}.",
                    point.ToString());
                Assert.IsFalse(File.Exists(GetNotificationMarker(root)), point.ToString());
                var temporaryFiles = Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).ToArray();
                var publishedPayloads = Directory.EnumerateFiles(
                    Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories).ToArray();
                var publishedSidecars = Directory.EnumerateFiles(
                    Path.Combine(root, "frames"), "*.json", SearchOption.AllDirectories).ToArray();
                long preRestartJournalRows;
                using (var crashedJournal = await OpenJournalAsync(root).ConfigureAwait(false))
                {
                    preRestartJournalRows = await ScalarLongAsync(
                        crashedJournal, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false);
                }
                var expected = GetExpectedBoundaryState(point);
                Assert.AreEqual(expected.TemporaryFiles, temporaryFiles.Length, point.ToString());
                Assert.AreEqual(expected.PublishedPayloads, publishedPayloads.Length, point.ToString());
                Assert.AreEqual(expected.PublishedSidecars, publishedSidecars.Length, point.ToString());
                Assert.AreEqual(expected.JournalRows, preRestartJournalRows, point.ToString());
                if (point == RawIngressFaultPoint.PayloadPartiallyWritten)
                {
                    Assert.HasCount(1, temporaryFiles);
                    var partial = await File.ReadAllBytesAsync(temporaryFiles[0]).ConfigureAwait(false);
                    Assert.AreEqual(2, partial.Length);
                    CollectionAssert.AreEqual(payload[..partial.Length], partial);
                    Assert.IsEmpty(publishedPayloads);
                    Assert.IsEmpty(publishedSidecars);
                    Assert.AreEqual(0L, preRestartJournalRows);
                }
                if (point == RawIngressFaultPoint.AfterJournalCommit)
                {
                    Assert.AreEqual(1L, preRestartJournalRows);
                }
                var state = new RawIngressState(TimeProvider.System);
                using var restarted = CreateIngress(root, state);
                var receipt = await restarted.AcceptAsync(
                    CreateConfiguration(),
                    CreateSubmission(Timestamp(10), payload),
                    CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(receipt, point.ToString());
                Assert.AreEqual(expected.RecoveryOutcome, receipt.Outcome, point.ToString());
                CollectionAssert.AreEqual(
                    payload,
                    await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath).ConfigureAwait(false),
                    point.ToString());
                Assert.IsEmpty(
                    Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).ToArray(),
                    point.ToString());
                Assert.HasCount(
                    2,
                    Directory.EnumerateFiles(Path.Combine(root, "frames"), "*", SearchOption.AllDirectories).ToArray(),
                    point.ToString());
                long postRestartJournalRows;
                using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
                postRestartJournalRows = await ScalarLongAsync(
                    connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false);
                Assert.AreEqual(1L, postRestartJournalRows, point.ToString());
                var recoveredManifestSha256 = Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(
                        Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")).ConfigureAwait(false)));
                Assert.AreEqual(receipt.CommittedManifestSha256, recoveredManifestSha256, point.ToString());
                var scenarioId = point switch
                {
                    RawIngressFaultPoint.PayloadPartiallyWritten => "raw-boundary-payload-partially-written",
                    RawIngressFaultPoint.PayloadWritten => "raw-boundary-payload-written",
                    RawIngressFaultPoint.PayloadFlushed => "raw-boundary-payload-flushed",
                    RawIngressFaultPoint.PayloadPublished => "raw-boundary-payload-published",
                    RawIngressFaultPoint.BeforeJournalTransactionCommit => "raw-boundary-before-journal-commit",
                    RawIngressFaultPoint.AfterJournalCommit => "raw-boundary-after-journal-commit",
                    RawIngressFaultPoint.BeforeWakeUpNotification => "raw-boundary-before-wakeup",
                    _ => throw new InvalidOperationException($"Unmapped raw ingress process-kill boundary {point}.")
                };
                await Phase14ScenarioEvidence.RecordAsync(
                    scenarioId,
                    $"fault-point-{point}",
                    point.ToString(),
                    ["process-termination-observed", "boundary-state-matched", "restart-converged-once", "manifest-checksum-matched"])
                    .ConfigureAwait(false);
                var aliasScenarioId = point switch
                {
                    RawIngressFaultPoint.PayloadPartiallyWritten => "raw-payload-write-crash",
                    RawIngressFaultPoint.PayloadPublished => "payload-published-before-journal-crash",
                    RawIngressFaultPoint.AfterJournalCommit => "journal-committed-before-wakeup-crash",
                    _ => null
                };
                if (aliasScenarioId is not null)
                {
                    await Phase14ScenarioEvidence.RecordAsync(
                        aliasScenarioId,
                        $"fault-point-{point}",
                        null,
                        ["process-termination-observed", "durable-boundary-state-matched", "restart-converged-once"])
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }
    }

    [TestMethod]
    public async Task ProcessKillTimeout_KillsAndReapsChild()
    {
        var root = CreateRoot();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await RunCrashChildAsync(
                    root,
                    RawIngressFaultPoint.PayloadWritten,
                    TimeSpan.FromMilliseconds(250),
                    hang: true).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RawIngressCrashChild()
    {
        var root = Environment.GetEnvironmentVariable("HVO_RAW_INGRESS_CRASH_ROOT");
        var pointValue = Environment.GetEnvironmentVariable("HVO_RAW_INGRESS_CRASH_POINT");
        if (string.IsNullOrWhiteSpace(root) || !Enum.TryParse<RawIngressFaultPoint>(pointValue, out var point))
        {
            return;
        }
        if (string.Equals(Environment.GetEnvironmentVariable("HVO_RAW_INGRESS_CRASH_HANG"), "true", StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }
        using var ingress = CreateIngress(
            root,
            new RawIngressState(TimeProvider.System),
            new ProcessKillFaultInjector(point));
        var context = new CaptureHostContext(
            CreateConfiguration(),
            ingress,
            new MarkerCaptureDistributor(GetNotificationMarker(root)));
        await context.PublishAsync(
            CreateSubmission(Timestamp(10), [10, 20, 30, 40]),
            CancellationToken.None).ConfigureAwait(false);
        Assert.Fail("The injected process-kill boundary was not reached.");
    }

    private static RawCaptureIngress CreateIngress(
        string root,
        RawIngressState state,
        IRawIngressFaultInjector? faultInjector = null,
        TimeProvider? timeProvider = null)
    {
        return new RawCaptureIngress(
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 1
            }),
            new FixedCapacityProvider(long.MaxValue),
            state,
            timeProvider ?? TimeProvider.System,
            new RawIngressTelemetry(state),
            NullLogger<RawCaptureIngress>.Instance,
            faultInjector ?? new NullRawIngressFaultInjector());
    }

    private static CameraModuleConfig CreateConfiguration()
        => new(
            new ObservatoryLocation(35, -113, 500, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("test-sensor", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8, SensorRecipeVersion: "sensor-v1"),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
                ProfileVersion: "rig-v1"),
            CapturePipelineConfig.Empty,
            AgentId: "agent-94");

    private static CameraModuleConfig CreateMono16Configuration()
    {
        var configuration = CreateConfiguration();
        return configuration with
        {
            Rig = configuration.Rig with
            {
                Sensor = configuration.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16, StrideBytes = 4 }
            }
        };
    }

    private static FrameLayoutDescriptor CreateLowerDepthLayout()
        => new(
            2,
            2,
            4,
            CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian,
            10,
            16,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            0,
            1023,
            8)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample
        };

    private static CaptureLoopSubmission WithFrameLayout(
        CaptureLoopSubmission submission,
        FrameLayoutDescriptor layout)
    {
        var frame = submission.Result.Frame! with
        {
            PixelFormat = layout.PixelFormat,
            StrideBytes = layout.StrideBytes,
            Layout = layout
        };
        return submission with { Result = submission.Result with { Frame = frame } };
    }

    private static CaptureLoopSubmission CreateSubmission(DateTimeOffset timestamp, byte[] payload)
    {
        var frame = new CameraFrame(
            timestamp,
            2,
            2,
            CameraPixelFormat.Mono8,
            payload,
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN, "Test"),
            2);
        return new CaptureLoopSubmission(
            new CaptureRequest(timestamp, TimeSpan.FromSeconds(1), CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null)),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null), TimeSpan.Zero, CaptureMode.Still, false),
            timestamp,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);
    }

    private static CaptureLoopSubmission CreateEnrichedSubmission(DateTimeOffset exposureStartedUtc, byte[] payload)
    {
        var readoutCompletedUtc = exposureStartedUtc.AddSeconds(1);
        var exposure = TimeSpan.FromSeconds(1);
        var gain = 1d;
        var frame = new CameraFrame(
            readoutCompletedUtc,
            2,
            2,
            CameraPixelFormat.Mono8,
            payload,
            new FrameMetadata(exposure, gain, double.NaN, "Test"),
            2);
        var request = new CaptureRequest(
            exposureStartedUtc.AddSeconds(-2),
            TimeSpan.FromSeconds(1),
            CaptureMode.Still,
            new CaptureSetpoint(exposure, gain, null, null));
        var result = new CaptureResult(
            frame,
            new CaptureSetpoint(exposure, gain, null, null),
            TimeSpan.Zero,
            CaptureMode.Still,
            false)
        {
            AcquisitionTiming = new CaptureAcquisitionTiming(
                exposureStartedUtc,
                exposureStartedUtc.AddMilliseconds(800),
                readoutCompletedUtc)
            {
                SetpointAppliedUtc = exposureStartedUtc.AddMilliseconds(-500)
            }
        };
        return new CaptureLoopSubmission(
            request,
            result,
            exposureStartedUtc.AddSeconds(-1),
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero)
        {
            CycleEvidence = new CaptureCycleEvidence(
                CaptureCadenceMode.MinimumStartInterval,
                CaptureStartReason.DeadlineReached,
                AutomaticControlOwnership.Disabled,
                AutomaticControlOwnership.Disabled,
                null,
                exposureStartedUtc.AddSeconds(-1),
                TimeSpan.FromMilliseconds(500),
                null,
                new CaptureControlDecisionEvidence(
                    readoutCompletedUtc.AddMilliseconds(100),
                    readoutCompletedUtc.AddMilliseconds(200),
                    exposure,
                    gain,
                    exposure,
                    gain,
                    CaptureControlDecisionReason.Disabled),
                readoutCompletedUtc.AddMilliseconds(300))
        };
    }

    private static CaptureLoopSubmission CreateCameraNativeSetpointFailureSubmission(
        DateTimeOffset exposureStartedUtc,
        byte[] payload)
    {
        var readoutCompletedUtc = exposureStartedUtc.AddSeconds(1);
        var activeExposure = TimeSpan.FromSeconds(1);
        var decidedExposure = TimeSpan.FromSeconds(2);
        const double gain = 1;
        var frame = new CameraFrame(
            readoutCompletedUtc,
            2,
            2,
            CameraPixelFormat.Mono8,
            payload,
            new FrameMetadata(activeExposure, gain, double.NaN, "Test"),
            2);
        var request = new CaptureRequest(
            exposureStartedUtc.AddSeconds(-2),
            TimeSpan.FromSeconds(1),
            CaptureMode.Still,
            new CaptureSetpoint(activeExposure, gain, null, null));
        var result = new CaptureResult(
            frame,
            new CaptureSetpoint(decidedExposure, gain, null, null),
            TimeSpan.Zero,
            CaptureMode.Still,
            false)
        {
            AcquisitionTiming = new CaptureAcquisitionTiming(
                exposureStartedUtc,
                exposureStartedUtc.AddMilliseconds(800),
                readoutCompletedUtc)
        };
        return new CaptureLoopSubmission(
            request,
            result,
            exposureStartedUtc.AddSeconds(-1),
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero)
        {
            CycleEvidence = new CaptureCycleEvidence(
                CaptureCadenceMode.Continuous,
                CaptureStartReason.Initial,
                AutomaticControlOwnership.CameraNative,
                AutomaticControlOwnership.Disabled,
                null,
                exposureStartedUtc.AddSeconds(-1),
                null,
                null,
                new CaptureControlDecisionEvidence(
                    readoutCompletedUtc.AddMilliseconds(100),
                    readoutCompletedUtc.AddMilliseconds(200),
                    activeExposure,
                    gain,
                    decidedExposure,
                    gain,
                    CaptureControlDecisionReason.SetpointApplicationFailed),
                readoutCompletedUtc.AddMilliseconds(300))
        };
    }

    private static async Task<SqliteConnection> OpenJournalAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<(int ExitCode, string Output)> RunCrashChildAsync(
        string root,
        RawIngressFaultPoint point,
        TimeSpan? timeoutAfter = null,
        bool hang = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(typeof(RawCaptureIngressTests).Assembly.Location);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add($"FullyQualifiedName={typeof(RawCaptureIngressTests).FullName}.{nameof(RawIngressCrashChild)}");
        startInfo.Environment["HVO_RAW_INGRESS_CRASH_ROOT"] = root;
        startInfo.Environment["HVO_RAW_INGRESS_CRASH_POINT"] = point.ToString();
        startInfo.Environment["HVO_RAW_INGRESS_CRASH_HANG"] = hang ? "true" : "false";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start raw ingress crash child.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(timeoutAfter ?? TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            Assert.IsTrue(process.HasExited);
            throw;
        }
        return (process.ExitCode, string.Concat(
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false)));
    }

    private static (int TemporaryFiles, int PublishedPayloads, int PublishedSidecars, long JournalRows, RawIngressOutcome RecoveryOutcome)
        GetExpectedBoundaryState(RawIngressFaultPoint point)
        => point switch
        {
            RawIngressFaultPoint.PayloadPartiallyWritten or
            RawIngressFaultPoint.PayloadWritten or
            RawIngressFaultPoint.PayloadFlushed => (1, 0, 0, 0, RawIngressOutcome.Committed),
            RawIngressFaultPoint.PayloadPublished => (0, 1, 0, 0, RawIngressOutcome.Committed),
            RawIngressFaultPoint.BeforeJournalTransactionCommit => (0, 1, 1, 0, RawIngressOutcome.Existing),
            RawIngressFaultPoint.AfterJournalCommit or
            RawIngressFaultPoint.BeforeWakeUpNotification => (0, 1, 1, 1, RawIngressOutcome.Existing),
            _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Unexpected raw ingress fault point.")
        };

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Tests pass only fixed SQL assertions.")]
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Tests pass only fixed SQL assertions.")]
    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Tests pass only fixed SQL assertions.")]
    private static async Task<byte[]> ScalarBytesAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static DateTimeOffset Timestamp(int hour)
        => new(2026, 7, 14, hour, 0, 0, TimeSpan.Zero);

    private static string GetNotificationMarker(string root)
        => Path.Combine(root, "capture-distributor-notified");

    private static string CreateRoot()
        => Path.Combine(Path.GetTempPath(), "hvo-raw-ingress-tests", Guid.NewGuid().ToString("N"));

    private static readonly string[] DatabaseSuffixes = [string.Empty, "-wal", "-shm"];

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedCapacityProvider(long availableBytes) : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot) => new(long.MaxValue, availableBytes);
    }

    private sealed class MutableCapacityProvider(long availableBytes) : IStorageCapacityProvider
    {
        public long AvailableBytes { get; set; } = availableBytes;

        public StorageCapacity GetCapacity(string storageRoot) => new(long.MaxValue, AvailableBytes);
    }

    private sealed class ThrowingCapacityProvider : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot) => throw new IOException("Injected capacity probe failure.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class OneShotFaultInjector(RawIngressFaultPoint target) : IRawIngressFaultInjector
    {
        private int _injected;

        public DateTimeOffset? PayloadPublishedUtc { get; private set; }

        public bool IsEnabled(RawIngressFaultPoint point) => point == target;

        public void Inject(RawIngressFaultPoint point)
        {
            if (point == RawIngressFaultPoint.PayloadPublished && PayloadPublishedUtc is null)
            {
                PayloadPublishedUtc = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            if (point == target && Interlocked.Exchange(ref _injected, 1) == 0)
            {
                throw new InjectedRawIngressFaultException();
            }
        }
    }

    private sealed class ProcessKillFaultInjector(RawIngressFaultPoint target) : IRawIngressFaultInjector
    {
        public bool IsEnabled(RawIngressFaultPoint point) => point == target;

        public void Inject(RawIngressFaultPoint point)
        {
            if (point == target)
            {
                var message = $"Injected raw ingress process termination at {point}.";
                Console.Error.WriteLine(message);
                Console.Error.Flush();
                Environment.FailFast(message);
            }
        }
    }

    private sealed class MarkerCaptureDistributor(string markerPath) : ICaptureDistributor
    {
        public void NotifyCommittedCapture()
            => File.WriteAllText(markerPath, "notified");

        public ValueTask ProcessEphemeralAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class InjectedRawIngressFaultException : Exception
    {
        public InjectedRawIngressFaultException()
        {
        }

        public InjectedRawIngressFaultException(string message)
            : base(message)
        {
        }

        public InjectedRawIngressFaultException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
