using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class SqliteRawCaptureJournal(
    string databasePath,
    int busyTimeoutSeconds,
    Action<TimeSpan>? lockWaitRecorder = null,
    IRawIngressFaultInjector? faultInjector = null,
    Action<string, bool>? transactionRecorder = null,
    Action<bool>? checkpointRecorder = null,
    CaptureDistributionOptions? distributionOptions = null,
    ICaptureLaneFaultInjector? laneFaultInjector = null,
    Func<DateTimeOffset>? utcNow = null,
    TransientDetectionOptions? transientOptions = null)
{
    internal const int CurrentSchemaVersion = 9;
    private const int CoordinateScrubbedSchemaVersion = 7;
    private const int PendingCoordinateScrubSchemaVersion = -7;
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly int _busyTimeoutSeconds = busyTimeoutSeconds;
    private readonly Action<TimeSpan>? _lockWaitRecorder = lockWaitRecorder;
    private readonly IRawIngressFaultInjector _faultInjector = faultInjector ?? new NullRawIngressFaultInjector();
    private readonly Action<string, bool>? _transactionRecorder = transactionRecorder;
    private readonly Action<bool>? _checkpointRecorder = checkpointRecorder;
    private readonly CaptureDistributionOptions _distributionOptions = distributionOptions ?? new CaptureDistributionOptions();
    private readonly ICaptureLaneFaultInjector _laneFaultInjector = laneFaultInjector ?? new NullCaptureLaneFaultInjector();
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? SystemUtcNow;
    private readonly TransientDetectionOptions _transientOptions = transientOptions ?? new TransientDetectionOptions();

    internal string DatabasePath => _databasePath;

    private static DateTimeOffset SystemUtcNow() => DateTimeOffset.UtcNow;

    internal Task InitializeAsync(CancellationToken cancellationToken)
        => InitializeAsync(DefaultLaneDefinitions, cancellationToken);

    internal async Task InitializeAsync(
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(laneDefinitions);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        EnsureDatabaseFilesArePhysical();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical();

        var version = await ExecuteScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        if (version == PendingCoordinateScrubSchemaVersion)
        {
            await CompleteCoordinateScrubAsync(
                connection, CoordinateScrubbedSchemaVersion, cancellationToken).ConfigureAwait(false);
            version = CoordinateScrubbedSchemaVersion;
            _transactionRecorder?.Invoke("migration", true);
        }
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Raw ingress schema {version} is newer than supported schema {CurrentSchemaVersion}.");
        }
        if (version < CurrentSchemaVersion)
        {
            try
            {
                await ExecuteNonQueryAsync(connection, transaction: null, "PRAGMA secure_delete = ON;", cancellationToken)
                    .ConfigureAwait(false);
                using var transaction = BeginImmediate(connection);
                _faultInjector.Inject(RawIngressFaultPoint.AfterMigrationTransactionBegan);
                if (version == 0)
                {
                    var hasLegacyRawCaptures = await HasTableAsync(
                        connection, transaction, "raw_captures", cancellationToken).ConfigureAwait(false);
                    if (hasLegacyRawCaptures && !await HasColumnAsync(
                            connection, transaction, "raw_captures", "evidence_origin", cancellationToken).ConfigureAwait(false))
                    {
                        await ExecuteNonQueryAsync(
                            connection, transaction, GalleryV6ColumnMigrationSql, cancellationToken).ConfigureAwait(false);
                    }
                    await ExecuteNonQueryAsync(connection, transaction, SchemaSql, cancellationToken).ConfigureAwait(false);
                    await ExecuteNonQueryAsync(connection, transaction, TransientSchemaSql, cancellationToken).ConfigureAwait(false);
                    if (hasLegacyRawCaptures)
                    {
                        await BackfillEvidenceOriginsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (version == 1)
                {
                    await ExecuteNonQueryAsync(connection, transaction, LaneSchemaSql, cancellationToken).ConfigureAwait(false);
                    await UpsertLaneDefinitionsAsync(connection, transaction, laneDefinitions, cancellationToken).ConfigureAwait(false);
                    await BackfillLaneWorkAsync(
                        connection, transaction, laneDefinitions, _distributionOptions, cancellationToken).ConfigureAwait(false);
                }
                if (version is 2 or 3 &&
                    await HasActiveLegacyTransientLaneAsync(
                        connection, transaction, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "Legacy custom 'transient' lane has unfinished work and cannot be adopted automatically.");
                }
                if (version is > 0 and < 4)
                {
                    await ExecuteNonQueryAsync(connection, transaction, TransientSchemaSql, cancellationToken).ConfigureAwait(false);
                }
                if (version == 4)
                {
                    await ExecuteNonQueryAsync(
                        connection, transaction, TransientCaptureWorkV5MigrationSql, cancellationToken).ConfigureAwait(false);
                }
                if (version is > 0 and < 6)
                {
                    if (!await HasColumnAsync(
                            connection, transaction, "raw_captures", "evidence_origin", cancellationToken).ConfigureAwait(false))
                    {
                        await ExecuteNonQueryAsync(
                            connection, transaction, GalleryV6ColumnMigrationSql, cancellationToken).ConfigureAwait(false);
                    }
                    await ExecuteNonQueryAsync(
                        connection, transaction, GalleryV6MigrationSql, cancellationToken).ConfigureAwait(false);
                }
                if (version == 1)
                {
                    await UpsertTransientPolicyMarkerAsync(
                        connection, transaction, cancellationToken).ConfigureAwait(false);
                }
                if (version is 1 or 2)
                {
                    await RehashCommittedManifestBytesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                }
                if (version is > 0 and < 6)
                {
                    await BackfillEvidenceOriginsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                }
                if (version < 7)
                {
                    await RedactLaneContextsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                }
                await ExecuteNonQueryAsync(
                    connection, transaction, CaptureScheduleSchemaSql, cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(
                    connection, transaction, CalibrationLibrarySchemaSql, cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    version < CoordinateScrubbedSchemaVersion
                        ? $"PRAGMA user_version = {PendingCoordinateScrubSchemaVersion};"
                        : $"PRAGMA user_version = {CurrentSchemaVersion};",
                    cancellationToken).ConfigureAwait(false);
                _faultInjector.Inject(RawIngressFaultPoint.BeforeMigrationCommit);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                if (version < CoordinateScrubbedSchemaVersion)
                {
                    await CompleteCoordinateScrubAsync(
                        connection, CurrentSchemaVersion, cancellationToken).ConfigureAwait(false);
                }
                _transactionRecorder?.Invoke("migration", true);
            }
            catch
            {
                _transactionRecorder?.Invoke("migration", false);
                throw;
            }
        }
        else if (version == CurrentSchemaVersion)
        {
            // Schema v9 is not shipped yet; keep branch-local v9 databases aligned with additive v9 corrections.
            await ExecuteNonQueryAsync(
                connection, transaction: null, CalibrationAcquisitionV9CorrectionSql, cancellationToken)
                .ConfigureAwait(false);
        }

        await SynchronizeTransientPolicyAsync(connection, laneDefinitions, cancellationToken).ConfigureAwait(false);
        await SynchronizeLaneDefinitionsAsync(connection, laneDefinitions, cancellationToken).ConfigureAwait(false);

        var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Raw ingress SQLite integrity check failed.");
        }
        if (await ExecuteScalarLongAsync(
                connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;", cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Raw ingress SQLite foreign-key validation failed.");
        }
        var schemaObjectCount = await ExecuteScalarLongAsync(connection, """
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
                'ix_transient_candidates_backlog', 'ix_transient_candidate_sources_raw',
                'transient_runtime_policy', 'transient_capture_work', 'transient_candidate_conflicts',
                 'ix_transient_capture_work_backlog', 'ix_transient_candidate_conflicts_observed',
                 'ix_transient_candidate_conflicts_candidate',
                 'capture_control_state', 'capture_control_commands',
                 'capture_schedule_revisions', 'capture_schedule_state', 'capture_schedule_activations',
                 'capture_schedule_overrides', 'capture_schedule_commands',
                 'capture_schedule_expansions', 'capture_schedule_intervals', 'capture_schedule_unavailable',
                 'capture_schedule_admissions', 'capture_schedule_override_events',
                 'ix_capture_schedule_revisions_created', 'ix_capture_schedule_overrides_active',
                 'ix_capture_schedule_expansions_lookup', 'ix_capture_schedule_intervals_bounds',
                 'calibration_library_bundles', 'calibration_library_artifacts', 'calibration_library_state',
                 'calibration_library_activations', 'calibration_library_commands',
                 'calibration_acquisition_jobs', 'calibration_library_reconciliation',
                 'ix_calibration_library_bundles_selection', 'ix_calibration_library_bundles_created',
                 'ix_calibration_library_artifacts_role', 'ix_calibration_library_activations_history',
                  'ix_calibration_acquisition_jobs_camera', 'ux_calibration_acquisition_jobs_camera_nonterminal',
                  'ix_calibration_library_reconciliation_state');
            """, cancellationToken).ConfigureAwait(false);
        if (schemaObjectCount != 60)
        {
            throw new InvalidDataException("Raw ingress SQLite schema is incomplete or drifted.");
        }
        await VerifyColumnsAsync(connection, "raw_captures", RawCaptureColumns, cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_raw_captures_gallery_time", "exposure_started_unix_ms,capture_sequence,raw_capture_row_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_raw_captures_gallery_sequence", "capture_sequence,raw_capture_row_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_raw_captures_gallery_state", "state,capture_sequence,raw_capture_row_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_raw_captures_gallery_origin", "evidence_origin,capture_sequence,raw_capture_row_id", cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_lane_definitions", LaneDefinitionColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_lane_contexts", LaneContextColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_lane_work", LaneWorkColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "transient_event_identities", TransientEventIdentityColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "transient_candidates", TransientCandidateColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "transient_candidate_sources", TransientCandidateSourceColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "transient_runtime_policy", TransientRuntimePolicyColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "transient_capture_work", TransientCaptureWorkColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "transient_candidate_conflicts", TransientCandidateConflictColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_control_state", CaptureControlStateColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_control_commands", CaptureControlCommandColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_revisions", CaptureScheduleRevisionColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_state", CaptureScheduleStateColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_activations", CaptureScheduleActivationColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_overrides", CaptureScheduleOverrideColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_commands", CaptureScheduleCommandColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_expansions", CaptureScheduleExpansionColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_intervals", CaptureScheduleIntervalColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_unavailable", CaptureScheduleUnavailableColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_admissions", CaptureScheduleAdmissionColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_schedule_override_events", CaptureScheduleOverrideEventColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "calibration_library_bundles", CalibrationLibraryBundleColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "calibration_library_artifacts", CalibrationLibraryArtifactColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "calibration_library_state", CalibrationLibraryStateColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "calibration_library_activations", CalibrationLibraryActivationColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "calibration_library_commands", CalibrationLibraryCommandColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "calibration_acquisition_jobs", CalibrationAcquisitionJobColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "calibration_library_reconciliation", CalibrationLibraryReconciliationColumns, cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_schedule_revisions_created", "created_unix_ms,revision_number", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_schedule_overrides_active", "cleared_unix_ms,end_unix_ms,mode,override_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_schedule_expansions_lookup", "revision_id,deployment_location_id,deployment_location_version,preview_start_unix_ms,preview_end_unix_ms", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_schedule_intervals_bounds", "expansion_key,start_unix_ms,end_unix_ms,ordinal", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_calibration_library_bundles_selection", "publication_state,agent_id,rig_id,rig_profile_sha256,sensor_profile_sha256,effective_from_unix_ms,effective_until_unix_ms,bundle_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_calibration_library_bundles_created", "created_unix_ms,bundle_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_calibration_library_artifacts_role", "bundle_id,role,reference_kind,source_index,ordinal", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_calibration_library_activations_history", "activated_unix_ms,activation_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_calibration_acquisition_jobs_camera", "camera_key,state,created_unix_ms,job_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ux_calibration_acquisition_jobs_camera_nonterminal", "camera_key", cancellationToken).ConfigureAwait(false);
        await VerifyPartialUniqueIndexAsync(
            connection,
            "calibration_acquisition_jobs",
            "ux_calibration_acquisition_jobs_camera_nonterminal",
            "WHERE state NOT IN ('published', 'failed', 'cancelled')",
            cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_calibration_library_reconciliation_state", "operation_state,observed_unix_ms,reconciliation_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_claim", "lane_name,state,available_unix_ms,work_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_lease", "state,lease_expires_unix_ms", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_backlog", "lane_name,state,created_unix_ms", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_raw", "raw_capture_row_id,required,state", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_ordered", "lane_name,agent_id,capture_sequence", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_transient_candidates_backlog", "state,created_unix_ms,candidate_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_transient_candidate_sources_raw", "raw_capture_row_id,candidate_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_transient_capture_work_backlog", "state,created_unix_ms,raw_capture_row_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_transient_candidate_conflicts_observed", "observed_unix_ms,conflict_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_transient_candidate_conflicts_candidate", "candidate_id,conflict_id", cancellationToken).ConfigureAwait(false);
        await VerifyConnectionSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RedactLaneContextsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const int batchSize = 256;
        var lastRawCaptureRowId = 0L;
        while (true)
        {
            var contexts = new List<(long RawCaptureRowId, byte[] Json, string Sha256)>(batchSize);
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT raw_capture_row_id, context_json, context_sha256
                    FROM capture_lane_contexts
                    WHERE raw_capture_row_id > $last
                    ORDER BY raw_capture_row_id
                    LIMIT $batch;
                    """;
                select.Parameters.AddWithValue("$last", lastRawCaptureRowId);
                select.Parameters.AddWithValue("$batch", batchSize);
                using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    contexts.Add((reader.GetInt64(0), (byte[])reader.GetValue(1), reader.GetString(2)));
                }
            }

            if (contexts.Count == 0)
            {
                return;
            }

            foreach (var context in contexts)
            {
                var redacted = CaptureLaneEnvelopeSerializer.Redact(context.Json, context.Sha256);
                if (context.Json.AsSpan().SequenceEqual(redacted.Json))
                {
                    continue;
                }
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE capture_lane_contexts
                    SET context_json = $json, context_sha256 = $sha
                    WHERE raw_capture_row_id = $raw;
                    """;
                update.Parameters.AddWithValue("$json", redacted.Json);
                update.Parameters.AddWithValue("$sha", redacted.Sha256);
                update.Parameters.AddWithValue("$raw", context.RawCaptureRowId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidDataException("Raw ingress lane-context redaction did not update exactly one row.");
                }
            }
            lastRawCaptureRowId = contexts[^1].RawCaptureRowId;
        }
    }

    private static async Task ScrubMigratedCoordinateBytesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection, transaction: null, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, transaction: null, "VACUUM;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection, transaction: null, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteCoordinateScrubAsync(
        SqliteConnection connection,
        int completedSchemaVersion,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, transaction: null, "PRAGMA secure_delete = ON;", cancellationToken)
            .ConfigureAwait(false);
        await ScrubMigratedCoordinateBytesAsync(connection, cancellationToken).ConfigureAwait(false);
        using var versionTransaction = BeginImmediate(connection);
        await ExecuteNonQueryAsync(
            connection,
            versionTransaction,
            $"PRAGMA user_version = {completedSchemaVersion};",
            cancellationToken).ConfigureAwait(false);
        await versionTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RehashCommittedManifestBytesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var manifests = new List<(long RawRowId, byte[] Json, string ManifestSha256, string DescriptorSha256)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT raw_capture_row_id, manifest_json, manifest_sha256, descriptor_sha256
                FROM raw_captures;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                manifests.Add((
                    reader.GetInt64(0),
                    await reader.GetFieldValueAsync<byte[]>(1, cancellationToken).ConfigureAwait(false),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }
        foreach (var manifest in manifests)
        {
            var parsed = CaptureContractJson.ParseManifest(manifest.Json);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } document ||
                !string.Equals(
                    CaptureContractJson.ComputeManifestSha256(document),
                    manifest.ManifestSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    CaptureContractJson.ComputeDescriptorSha256(document.Descriptor),
                    manifest.DescriptorSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Raw ingress v2 manifest evidence failed migration validation.");
            }
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE raw_captures SET manifest_sha256 = $sha WHERE raw_capture_row_id = $raw;";
            command.Parameters.AddWithValue("$sha", CaptureContractJson.ComputeManifestSha256(manifest.Json));
            command.Parameters.AddWithValue("$raw", manifest.RawRowId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task BackfillEvidenceOriginsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var origins = new List<(long RawRowId, GalleryEvidenceOrigin Origin)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT raw_capture_row_id, manifest_json, manifest_sha256, descriptor_sha256 FROM raw_captures;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var json = await reader.GetFieldValueAsync<byte[]>(1, cancellationToken).ConfigureAwait(false);
                var parsed = CaptureContractJson.ParseManifest(json);
                var manifest = parsed.Document?.Manifest;
                var origin = parsed.IsValid && manifest is not null &&
                    string.Equals(CaptureContractJson.ComputeManifestSha256(json), reader.GetString(2), StringComparison.Ordinal) &&
                    string.Equals(CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor), reader.GetString(3), StringComparison.Ordinal)
                        ? GalleryEvidenceClassifier.Classify(manifest)
                        : GalleryEvidenceOrigin.Unknown;
                origins.Add((reader.GetInt64(0), origin));
            }
        }
        foreach (var origin in origins)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE raw_captures SET evidence_origin = $origin WHERE raw_capture_row_id = $raw;";
            command.Parameters.AddWithValue("$origin", origin.Origin.ToString());
            command.Parameters.AddWithValue("$raw", origin.RawRowId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasActiveLegacyTransientLaneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM capture_lane_work
                WHERE lane_name = 'transient' AND state NOT IN ('completed', 'abandoned'));
            """;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info($table) WHERE name = $column);";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> HasTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table);";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    internal Task<RawCaptureIdentity> ReserveIdentityAsync(
        string agentId,
        Guid captureId,
        Guid artifactId,
        CancellationToken cancellationToken)
        => TrackTransactionAsync(
            "identity",
            () => ReserveIdentityCoreAsync(agentId, captureId, artifactId, cancellationToken));

    internal async Task<(bool Exists, bool EvidenceRetained)> ReadCommittedCaptureStateAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_relative_path, sidecar_relative_path FROM raw_captures WHERE capture_id = $capture_id;";
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, false);
        }
        var root = Path.GetDirectoryName(Path.GetDirectoryName(_databasePath)!)!;
        var payload = Path.GetFullPath(Path.Combine(
            root, reader.GetString(0).Replace('/', Path.DirectorySeparatorChar)));
        var sidecar = Path.GetFullPath(Path.Combine(
            root, reader.GetString(1).Replace('/', Path.DirectorySeparatorChar)));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, payload);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, sidecar);
        return (true, File.Exists(payload) && File.Exists(sidecar));
    }

    private async Task<RawCaptureIdentity> ReserveIdentityCoreAsync(
        string agentId,
        Guid captureId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT agent_id, capture_sequence, raw_artifact_id FROM raw_capture_assignments WHERE capture_id = $capture_id;";
            existing.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
            using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var existingAgent = reader.GetString(0);
                var sequence = reader.GetInt64(1);
                var existingArtifact = Guid.ParseExact(reader.GetString(2), "N");
                if (!string.Equals(existingAgent, agentId, StringComparison.Ordinal) || existingArtifact != artifactId)
                {
                    throw new RawIngressConflictException("Capture identity is already assigned to different immutable facts.");
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new RawCaptureIdentity(agentId, sequence, captureId, artifactId);
            }
        }

        long allocatedSequence;
        using (var allocate = connection.CreateCommand())
        {
            allocate.Transaction = transaction;
            allocate.CommandText = """
                INSERT INTO raw_capture_sequences(agent_id, last_sequence)
                VALUES ($agent_id, 1)
                ON CONFLICT(agent_id) DO UPDATE SET last_sequence = last_sequence + 1
                RETURNING last_sequence;
                """;
            allocate.Parameters.AddWithValue("$agent_id", agentId);
            allocatedSequence = Convert.ToInt64(await allocate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
                VALUES ($capture_id, $artifact_id, $agent_id, $capture_sequence);
                """;
            insert.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
            insert.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
            insert.Parameters.AddWithValue("$agent_id", agentId);
            insert.Parameters.AddWithValue("$capture_sequence", allocatedSequence);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RawCaptureIdentity(agentId, allocatedSequence, captureId, artifactId);
    }

    internal Task<RawIngressOutcome> CommitAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken)
        => CommitAsync(entry, null, null, DefaultLaneDefinitions, cancellationToken);

    internal Task<RawIngressOutcome> CommitAsync(
        RawIngressJournalEntry entry,
        byte[]? contextJson,
        string? contextSha256,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
        => TrackTransactionAsync(
            "commit",
            () => CommitCoreAsync(entry, contextJson, contextSha256, laneDefinitions, cancellationToken));

    private async Task<RawIngressOutcome> CommitCoreAsync(
        RawIngressJournalEntry entry,
        byte[]? contextJson,
        string? contextSha256,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        _faultInjector.Inject(RawIngressFaultPoint.AfterJournalTransactionBegan);
        var claims = await ReadClaimsAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        if (claims.Count > 0)
        {
            if (claims.Count == 1 && IsExact(claims[0], entry))
            {
                var existingRowId = await ReadRawRowIdAsync(connection, transaction, entry.CaptureId, cancellationToken).ConfigureAwait(false);
                await InsertContextAsync(
                    connection, transaction, existingRowId, contextJson, contextSha256, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RawIngressOutcome.Existing;
            }
            throw new RawIngressConflictException("Raw capture identity or path conflicts with committed immutable evidence.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertCaptureSql;
        AddEntryParameters(command, entry);
        command.Parameters.AddWithValue("$committed", _utcNow().ToUniversalTime().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var rawRowId = await ReadLastInsertRowIdAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await InsertContextAsync(
            connection, transaction, rawRowId, contextJson, contextSha256, cancellationToken).ConfigureAwait(false);
        await InsertLaneWorkAsync(
            connection, transaction, rawRowId, entry, laneDefinitions, cancellationToken).ConfigureAwait(false);
        _laneFaultInjector.Inject(CaptureLaneFaultPoint.AfterWorkRowsInserted);
        _faultInjector.Inject(RawIngressFaultPoint.AfterJournalRowInserted);
        _faultInjector.Inject(RawIngressFaultPoint.BeforeJournalTransactionCommit);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RawIngressOutcome.Committed;
    }

    internal async Task<(long Count, long Bytes, DateTimeOffset? Oldest)> ReadHeldTotalsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(payload_length), 0), MIN(durable_ingress_unix_ms) FROM raw_captures WHERE retention_hold = 1;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? oldest = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            oldest);
    }

    internal async Task<IReadOnlyList<RawIngressJournalEntry>> ReadAllAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT agent_id, capture_sequence, capture_id, raw_artifact_id,
                   descriptor_sha256, manifest_sha256, payload_sha256, payload_length,
                   payload_relative_path, sidecar_relative_path, manifest_json,
                   exposure_started_unix_ms, durable_ingress_unix_ms, state, retention_hold, evidence_origin
            FROM raw_captures
            ORDER BY agent_id, capture_sequence;
            """;
        var entries = new List<RawIngressJournalEntry>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader));
        }
        return entries;
    }

    internal async Task<IReadOnlyList<RawIngressRetentionHold>> ReadRetentionHoldsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_artifact_id, payload_relative_path, sidecar_relative_path
            FROM raw_captures
            WHERE retention_hold = 1
            ORDER BY agent_id, capture_sequence;
            """;
        var holds = new List<RawIngressRetentionHold>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            holds.Add(new RawIngressRetentionHold(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1),
                reader.GetString(2)));
        }
        return holds;
    }

    private async Task SynchronizeLaneDefinitionsAsync(
        SqliteConnection connection,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        try
        {
            using var transaction = BeginImmediate(connection);
            using (var disableDefinitions = connection.CreateCommand())
            {
                disableDefinitions.Transaction = transaction;
                disableDefinitions.CommandText =
                    "UPDATE capture_lane_definitions SET enabled = 0, updated_unix_ms = $now;";
                disableDefinitions.Parameters.AddWithValue("$now", _utcNow().ToUnixTimeMilliseconds());
                await disableDefinitions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await UpsertLaneDefinitionsAsync(connection, transaction, laneDefinitions, cancellationToken).ConfigureAwait(false);
            using (var orphaned = connection.CreateCommand())
            {
                orphaned.Transaction = transaction;
                orphaned.CommandText = """
                    SELECT w.lane_name
                    FROM capture_lane_work w
                    JOIN capture_lane_definitions d ON d.lane_name = w.lane_name
                    WHERE w.required = 1 AND w.state != 'completed' AND d.enabled = 0
                    LIMIT 1;
                    """;
                var orphanedLane = Convert.ToString(
                    await orphaned.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(orphanedLane))
                {
                    throw new InvalidOperationException(
                        $"Required capture lane '{orphanedLane}' has unfinished durable work and cannot be removed.");
                }
            }
            using (var abandonDisabled = connection.CreateCommand())
            {
                abandonDisabled.Transaction = transaction;
                abandonDisabled.CommandText = """
                UPDATE capture_lane_work
                SET state = 'abandoned', failure_reason = 'disabled', updated_unix_ms = $now,
                    lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL
                WHERE required = 0 AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')
                  AND lane_name IN (SELECT lane_name FROM capture_lane_definitions WHERE enabled = 0);
                """;
                abandonDisabled.Parameters.AddWithValue("$now", _utcNow().ToUnixTimeMilliseconds());
                await abandonDisabled.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                UPDATE raw_captures
                SET retention_hold = CASE WHEN
                    EXISTS (
                        SELECT 1 FROM capture_lane_work
                        WHERE capture_lane_work.raw_capture_row_id = raw_captures.raw_capture_row_id
                          AND ((required = 1 AND state != 'completed') OR state = 'leased'))
                    OR EXISTS (
                        SELECT 1
                        FROM transient_candidate_sources s
                        JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                        WHERE s.raw_capture_row_id = raw_captures.raw_capture_row_id
                          AND c.source_hold_released = 0)
                    OR EXISTS (
                        SELECT 1 FROM transient_capture_work
                        WHERE raw_capture_row_id = raw_captures.raw_capture_row_id
                          AND state = 'pending')
                    THEN 1 ELSE 0 END;
                """,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _transactionRecorder?.Invoke("lane-policy", true);
        }
        catch
        {
            _transactionRecorder?.Invoke("lane-policy", false);
            throw;
        }
    }

    private async Task SynchronizeTransientPolicyAsync(
        SqliteConnection connection,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        using var transaction = BeginImmediate(connection);
        string? persistedMode = null;
        bool persistedRequired = false;
        int persistedTimeoutMinutes = 0;
        using (var policy = connection.CreateCommand())
        {
            policy.Transaction = transaction;
            policy.CommandText = "SELECT mode, required, candidate_timeout_minutes FROM transient_runtime_policy WHERE policy_key = 1;";
            using var reader = await policy.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                persistedMode = reader.GetString(0);
                persistedRequired = reader.GetBoolean(1);
                persistedTimeoutMinutes = reader.GetInt32(2);
            }
        }
        var desiredLane = laneDefinitions.SingleOrDefault(static lane => lane.Name == "transient");
        var desiredMode = _transientOptions.Mode switch
        {
            TransientOperatingMode.Off => "off",
            TransientOperatingMode.Edge => "edge",
            TransientOperatingMode.Central => "central",
            TransientOperatingMode.Hybrid => "hybrid",
            _ => throw new InvalidOperationException("Transient operating mode is invalid.")
        };
        var legacyLaneExists = false;
        using (var legacy = connection.CreateCommand())
        {
            legacy.Transaction = transaction;
            legacy.CommandText = "SELECT EXISTS(SELECT 1 FROM capture_lane_definitions WHERE lane_name = 'transient');";
            legacyLaneExists = Convert.ToInt64(
                await legacy.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        long active;
        using (var activeCommand = connection.CreateCommand())
        {
            activeCommand.Transaction = transaction;
            activeCommand.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM capture_lane_work
                     WHERE lane_name = 'transient' AND state NOT IN ('completed', 'abandoned')) +
                    (SELECT COUNT(*) FROM transient_capture_work WHERE state IN ('pending', 'quarantined')) +
                    (SELECT COUNT(*) FROM transient_candidates WHERE source_hold_released = 0) +
                    (SELECT COUNT(*) FROM transient_candidate_conflicts);
                """;
            active = Convert.ToInt64(
                await activeCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        if (persistedMode is null && legacyLaneExists && active > 0)
        {
            throw new InvalidOperationException(
                "Legacy custom 'transient' lane has unfinished work and cannot be adopted automatically.");
        }
        if (active > 0 &&
            (desiredLane is null || persistedMode is not null &&
                (!string.Equals(persistedMode, desiredMode, StringComparison.Ordinal) ||
                 persistedRequired != _transientOptions.Required ||
                 persistedTimeoutMinutes != _transientOptions.CandidateTimeoutMinutes)))
        {
            throw new InvalidOperationException(
                "Transient operating mode or required policy cannot change while durable transient state is active.");
        }
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO transient_runtime_policy(
                policy_key, mode, required, candidate_timeout_minutes, updated_unix_ms)
            VALUES (1, $mode, $required, $timeout, $now)
            ON CONFLICT(policy_key) DO UPDATE SET
                mode = excluded.mode,
                required = excluded.required,
                candidate_timeout_minutes = excluded.candidate_timeout_minutes,
                updated_unix_ms = excluded.updated_unix_ms;
            """;
        upsert.Parameters.AddWithValue("$mode", desiredMode);
        upsert.Parameters.AddWithValue("$required", _transientOptions.Required ? 1 : 0);
        upsert.Parameters.AddWithValue("$timeout", _transientOptions.CandidateTimeoutMinutes);
        upsert.Parameters.AddWithValue("$now", _utcNow().ToUnixTimeMilliseconds());
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertTransientPolicyMarkerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var mode = _transientOptions.Mode switch
        {
            TransientOperatingMode.Off => "off",
            TransientOperatingMode.Edge => "edge",
            TransientOperatingMode.Central => "central",
            TransientOperatingMode.Hybrid => "hybrid",
            _ => throw new InvalidOperationException("Transient operating mode is invalid.")
        };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO transient_runtime_policy(
                policy_key, mode, required, candidate_timeout_minutes, updated_unix_ms)
            VALUES (1, $mode, $required, $timeout, $now)
            ON CONFLICT(policy_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$required", _transientOptions.Required ? 1 : 0);
        command.Parameters.AddWithValue("$timeout", _transientOptions.CandidateTimeoutMinutes);
        command.Parameters.AddWithValue("$now", _utcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertLaneDefinitionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        foreach (var lane in laneDefinitions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO capture_lane_definitions(
                    lane_name, enabled, required, ordered, policy_sha256, created_unix_ms, updated_unix_ms)
                VALUES ($lane, $enabled, $required, $ordered, $policy, $now, $now)
                ON CONFLICT(lane_name) DO UPDATE SET
                    enabled = excluded.enabled,
                    required = excluded.required,
                    ordered = excluded.ordered,
                    policy_sha256 = excluded.policy_sha256,
                    updated_unix_ms = excluded.updated_unix_ms;
                """;
            command.Parameters.AddWithValue("$lane", lane.Name);
            command.Parameters.AddWithValue("$enabled", lane.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$required", lane.Required ? 1 : 0);
            command.Parameters.AddWithValue("$ordered", lane.Ordered ? 1 : 0);
            command.Parameters.AddWithValue("$policy", lane.PolicySha256);
            command.Parameters.AddWithValue("$now", _utcNow().ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BackfillLaneWorkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CaptureDistributionOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var lane in laneDefinitions)
        {
            if (!lane.Enabled)
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO capture_lane_work(
                    raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered, state,
                    attempt_count, available_unix_ms, created_unix_ms, updated_unix_ms)
                SELECT raw_capture_row_id, $lane, agent_id, capture_sequence, $required, $ordered, 'pending',
                       0, committed_unix_ms, $now, $now
                FROM raw_captures
                WHERE state = 'committed' AND retention_hold = 1
                ON CONFLICT(raw_capture_row_id, lane_name) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$lane", lane.Name);
            command.Parameters.AddWithValue("$required", lane.Required ? 1 : 0);
            command.Parameters.AddWithValue("$ordered", lane.Ordered ? 1 : 0);
            command.Parameters.AddWithValue("$now", _utcNow().ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (!lane.Required)
            {
                await ApplyOptionalBackfillPressureAsync(
                    connection, transaction, lane, options, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyOptionalBackfillPressureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureLaneDefinition lane,
        CaptureDistributionOptions options,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH ranked AS (
                SELECT w.work_id,
                       ROW_NUMBER() OVER (ORDER BY w.agent_id, w.capture_sequence) AS pending_position,
                       SUM(r.payload_length) OVER (
                           ORDER BY w.agent_id, w.capture_sequence ROWS UNBOUNDED PRECEDING) AS cumulative_bytes,
                       r.durable_ingress_unix_ms
                FROM capture_lane_work w
                JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
                WHERE w.lane_name = $lane AND w.state = 'pending'
                  AND r.durable_ingress_unix_ms > $oldest_allowed
            )
            UPDATE capture_lane_work
            SET state = 'abandoned', failure_reason = 'optional-pressure', updated_unix_ms = $now
            WHERE (lane_name = $lane AND state = 'pending' AND EXISTS (
                       SELECT 1 FROM raw_captures r
                       WHERE r.raw_capture_row_id = capture_lane_work.raw_capture_row_id
                         AND r.durable_ingress_unix_ms <= $oldest_allowed
                   ))
               OR work_id IN (
                   SELECT work_id FROM ranked
                   WHERE pending_position > $maximum_count
                      OR cumulative_bytes > $maximum_bytes
               );
            """;
        command.Parameters.AddWithValue("$lane", lane.Name);
        command.Parameters.AddWithValue("$maximum_count", options.OptionalMaximumPendingCount);
        command.Parameters.AddWithValue("$maximum_bytes", options.OptionalMaximumPendingBytes);
        var now = _utcNow();
        command.Parameters.AddWithValue(
            "$oldest_allowed",
            now.AddMinutes(-options.OptionalMaximumOldestAgeMinutes).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        using var pressure = connection.CreateCommand();
        pressure.Transaction = transaction;
        pressure.CommandText = """
            UPDATE capture_lane_definitions
            SET pressure_state = (
                SELECT CASE
                    WHEN COUNT(*) >= $maximum_count
                      OR COALESCE(SUM(r.payload_length), 0) >= $maximum_bytes THEN 2
                    WHEN COUNT(*) * 100.0 >= $maximum_count * $recovery_percent
                      OR COALESCE(SUM(r.payload_length), 0) * 100.0 >= $maximum_bytes * $recovery_percent THEN 1
                    ELSE 0
                END
                FROM capture_lane_work w
                JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
                WHERE w.lane_name = $lane AND w.state IN ('pending', 'leased', 'retry_wait', 'quarantined')
            )
            WHERE lane_name = $lane;
            """;
        pressure.Parameters.AddWithValue("$lane", lane.Name);
        pressure.Parameters.AddWithValue("$maximum_count", options.OptionalMaximumPendingCount);
        pressure.Parameters.AddWithValue("$maximum_bytes", options.OptionalMaximumPendingBytes);
        pressure.Parameters.AddWithValue("$recovery_percent", options.PressureRecoveryPercent);
        await pressure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadRawRowIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture_id;";
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadLastInsertRowIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertContextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        byte[]? contextJson,
        string? contextSha256,
        CancellationToken cancellationToken)
    {
        if (contextJson is null || contextSha256 is null)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO capture_lane_contexts(raw_capture_row_id, context_json, context_sha256, context_source)
            VALUES ($raw, $json, $sha, 'capture')
            ON CONFLICT(raw_capture_row_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$raw", rawRowId);
        command.Parameters.AddWithValue("$json", contextJson);
        command.Parameters.AddWithValue("$sha", contextSha256);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = "SELECT context_sha256 FROM capture_lane_contexts WHERE raw_capture_row_id = $raw;";
        verify.Parameters.AddWithValue("$raw", rawRowId);
        var existing = Convert.ToString(
            await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(existing, contextSha256, StringComparison.Ordinal))
        {
            throw new RawIngressConflictException("Capture lane context differs from the committed retry context.");
        }
    }

    private async Task InsertLaneWorkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        RawIngressJournalEntry entry,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        foreach (var lane in laneDefinitions)
        {
            if (!lane.Enabled)
            {
                continue;
            }

            var abandon = !lane.Required && await IsOptionalAtHardLimitAsync(
                connection, transaction, lane.Name, entry.PayloadLength, entry.DurableIngressUtc, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO capture_lane_work(
                    raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered, state,
                    attempt_count, available_unix_ms, failure_reason, created_unix_ms, updated_unix_ms)
                VALUES ($raw, $lane, $agent_id, $capture_sequence, $required, $ordered, $state,
                        0, $available, $reason, $now, $now)
                ON CONFLICT(raw_capture_row_id, lane_name) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$raw", rawRowId);
            command.Parameters.AddWithValue("$lane", lane.Name);
            command.Parameters.AddWithValue("$agent_id", entry.AgentId);
            command.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
            command.Parameters.AddWithValue("$required", lane.Required ? 1 : 0);
            command.Parameters.AddWithValue("$ordered", lane.Ordered ? 1 : 0);
            command.Parameters.AddWithValue("$state", abandon ? "abandoned" : "pending");
            command.Parameters.AddWithValue("$available", entry.DurableIngressUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$reason", abandon ? "optional-pressure" : DBNull.Value);
            command.Parameters.AddWithValue("$now", _utcNow().ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<IReadOnlyList<(string Lane, bool Required, string State)>> ReadLaneOutcomesAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.lane_name, w.required, w.state
            FROM capture_lane_work w
            JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
            WHERE r.capture_id = $capture_id
            ORDER BY w.lane_name;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        var outcomes = new List<(string Lane, bool Required, string State)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            outcomes.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetString(2)));
        }
        return outcomes;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The query is selected from internal constant statements; lane values remain parameterized.")]
    private async Task<bool> IsOptionalAtHardLimitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string lane,
        long payloadLength,
        DateTimeOffset durableIngressUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Equals(lane, "transient", StringComparison.Ordinal)
            ? """
              SELECT
                  (SELECT COUNT(*) FROM capture_lane_work
                   WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')) +
                       (SELECT COUNT(*) FROM transient_capture_work WHERE state IN ('pending', 'quarantined')) +
                      (SELECT COUNT(*) FROM transient_candidates WHERE source_hold_released = 0),
                  (SELECT COALESCE(SUM(payload_length), 0) FROM raw_captures WHERE raw_capture_row_id IN (
                      SELECT raw_capture_row_id FROM capture_lane_work
                      WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')
                      UNION
                       SELECT raw_capture_row_id FROM transient_capture_work WHERE state IN ('pending', 'quarantined')
                      UNION
                      SELECT s.raw_capture_row_id FROM transient_candidate_sources s
                      JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                      WHERE c.source_hold_released = 0)),
                  (SELECT MIN(created_unix_ms) FROM (
                      SELECT created_unix_ms FROM capture_lane_work
                      WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')
                      UNION ALL
                       SELECT created_unix_ms FROM transient_capture_work WHERE state IN ('pending', 'quarantined')
                      UNION ALL
                      SELECT created_unix_ms FROM transient_candidates WHERE source_hold_released = 0)),
                  (SELECT COUNT(*) FROM transient_capture_work WHERE state = 'quarantined') +
                      (SELECT COUNT(*) FROM transient_candidates WHERE phase = 'quarantined') +
                      (SELECT COUNT(*) FROM transient_candidate_conflicts);
              """
            : """
              SELECT COUNT(*), COALESCE(SUM(r.payload_length), 0), MIN(r.durable_ingress_unix_ms), 0
              FROM capture_lane_work w
              JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
              WHERE w.lane_name = $lane AND w.state IN ('pending', 'leased', 'retry_wait', 'quarantined');
              """;
        command.Parameters.AddWithValue("$lane", lane);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var count = reader.GetInt64(0);
        var bytes = reader.GetInt64(1);
        var oldest = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? durableIngressUtc
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        var quarantined = reader.GetInt64(3);
        var age = _utcNow() - oldest;
        var hard = quarantined > 0 || count + 1 > _distributionOptions.OptionalMaximumPendingCount ||
                   CaptureLanePressureMath.ExceedsAfterAdding(
                       bytes, payloadLength, _distributionOptions.OptionalMaximumPendingBytes) ||
                   age >= TimeSpan.FromMinutes(_distributionOptions.OptionalMaximumOldestAgeMinutes);
        using var readPressure = connection.CreateCommand();
        readPressure.Transaction = transaction;
        readPressure.CommandText = "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = $lane;";
        readPressure.Parameters.AddWithValue("$lane", lane);
        var prior = Convert.ToInt32(
            await readPressure.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        var recovered = quarantined == 0 &&
                        count * 100 < _distributionOptions.OptionalMaximumPendingCount * _distributionOptions.PressureRecoveryPercent &&
                        !CaptureLanePressureMath.IsAtOrAbovePercentage(
                            bytes,
                            _distributionOptions.OptionalMaximumPendingBytes,
                            _distributionOptions.PressureRecoveryPercent) &&
                        age.TotalMinutes * 100 < _distributionOptions.OptionalMaximumOldestAgeMinutes * _distributionOptions.PressureRecoveryPercent;
        var warning = count * 100 >= _distributionOptions.OptionalMaximumPendingCount * _distributionOptions.PressureRecoveryPercent ||
                      CaptureLanePressureMath.IsAtOrAbovePercentage(
                          bytes,
                          _distributionOptions.OptionalMaximumPendingBytes,
                          _distributionOptions.PressureRecoveryPercent);
        var next = hard || prior == 2 && !recovered ? 2 : warning ? 1 : 0;
        using var updatePressure = connection.CreateCommand();
        updatePressure.Transaction = transaction;
        updatePressure.CommandText = "UPDATE capture_lane_definitions SET pressure_state = $pressure WHERE lane_name = $lane;";
        updatePressure.Parameters.AddWithValue("$pressure", next);
        updatePressure.Parameters.AddWithValue("$lane", lane);
        await updatePressure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return next == 2;
    }

    internal Task<RawIngressOutcome> RecoverAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken)
        => RecoverAsync(entry, DefaultLaneDefinitions, cancellationToken);

    internal Task<RawIngressOutcome> RecoverAsync(
        RawIngressJournalEntry entry,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
        => TrackTransactionAsync("recovery", () => RecoverCoreAsync(entry, laneDefinitions, cancellationToken));

    private async Task<RawIngressOutcome> RecoverCoreAsync(
        RawIngressJournalEntry entry,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var claims = await ReadClaimsAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        if (claims.Count > 0)
        {
            if (claims.Count == 1 && IsExact(claims[0], entry))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RawIngressOutcome.Existing;
            }
            throw new RawIngressConflictException("Recovered raw evidence conflicts with committed immutable identity.");
        }

        using (var existingAssignment = connection.CreateCommand())
        {
            existingAssignment.Transaction = transaction;
            existingAssignment.CommandText = """
                SELECT capture_id, raw_artifact_id, agent_id, capture_sequence
                FROM raw_capture_assignments
                WHERE capture_id = $capture_id OR raw_artifact_id = $artifact_id
                   OR (agent_id = $agent_id AND capture_sequence = $capture_sequence);
                """;
            existingAssignment.Parameters.AddWithValue("$capture_id", entry.CaptureId.ToString("N"));
            existingAssignment.Parameters.AddWithValue("$artifact_id", entry.ArtifactId.ToString("N"));
            existingAssignment.Parameters.AddWithValue("$agent_id", entry.AgentId);
            existingAssignment.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
            using var reader = await existingAssignment.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                (Guid.ParseExact(reader.GetString(0), "N") != entry.CaptureId ||
                 Guid.ParseExact(reader.GetString(1), "N") != entry.ArtifactId ||
                 !string.Equals(reader.GetString(2), entry.AgentId, StringComparison.Ordinal) ||
                 reader.GetInt64(3) != entry.CaptureSequence))
            {
                throw new RawIngressConflictException("Recovered raw evidence conflicts with its reserved identity assignment.");
            }
        }

        using (var sequence = connection.CreateCommand())
        {
            sequence.Transaction = transaction;
            sequence.CommandText = """
                INSERT INTO raw_capture_sequences(agent_id, last_sequence)
                VALUES ($agent_id, $capture_sequence)
                ON CONFLICT(agent_id) DO UPDATE SET last_sequence = MAX(last_sequence, excluded.last_sequence);
                """;
            sequence.Parameters.AddWithValue("$agent_id", entry.AgentId);
            sequence.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
            await sequence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var assignment = connection.CreateCommand())
        {
            assignment.Transaction = transaction;
            assignment.CommandText = """
                INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
                VALUES ($capture_id, $artifact_id, $agent_id, $capture_sequence)
                ON CONFLICT(capture_id) DO NOTHING;
                """;
            assignment.Parameters.AddWithValue("$capture_id", entry.CaptureId.ToString("N"));
            assignment.Parameters.AddWithValue("$artifact_id", entry.ArtifactId.ToString("N"));
            assignment.Parameters.AddWithValue("$agent_id", entry.AgentId);
            assignment.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
            await assignment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = InsertCaptureSql;
            AddEntryParameters(insert, entry);
            insert.Parameters.AddWithValue("$committed", _utcNow().ToUniversalTime().ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var recoveredRowId = await ReadLastInsertRowIdAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await InsertLaneWorkAsync(
            connection, transaction, recoveredRowId, entry, laneDefinitions, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RawIngressOutcome.Committed;
    }

    internal Task MarkEvidenceFailureAsync(
        Guid captureId,
        string state,
        string reason,
        CancellationToken cancellationToken)
        => TrackWriteAsync(
            "evidence-state",
            () => MarkEvidenceFailureCoreAsync(captureId, state, reason, cancellationToken));

    private async Task MarkEvidenceFailureCoreAsync(
        Guid captureId,
        string state,
        string reason,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE raw_captures
            SET state = $state, failure_reason = $reason, retention_hold = 1
            WHERE capture_id = $capture_id;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Raw ingress evidence failure did not identify exactly one capture.");
        }
    }

    internal Task MarkCommittedAsync(Guid captureId, CancellationToken cancellationToken)
        => TrackWriteAsync("evidence-repair", () => MarkCommittedCoreAsync(captureId, cancellationToken));

    private async Task MarkCommittedCoreAsync(Guid captureId, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE raw_captures
            SET state = 'committed', failure_reason = NULL
            WHERE capture_id = $capture_id;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Raw ingress repair did not identify exactly one capture.");
        }
    }

    internal async Task<(long QuarantineCount, long QuarantineBytes, long FailureCount)> ReadHealthTotalsAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM raw_ingress_reconciliation WHERE outcome = 'quarantined'),
                (SELECT COALESCE(SUM(observed_bytes), 0) FROM raw_ingress_reconciliation WHERE outcome = 'quarantined'),
                (SELECT COUNT(*) FROM raw_captures WHERE state != 'committed');
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    internal Task PlanQuarantineAsync(
        RawIngressPlannedQuarantine operation,
        CancellationToken cancellationToken)
        => TrackWriteAsync("reconciliation-plan", () => PlanQuarantineCoreAsync(operation, cancellationToken));

    private async Task PlanQuarantineCoreAsync(
        RawIngressPlannedQuarantine operation,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raw_ingress_reconciliation(
                evidence_key, source_relative_path, companion_relative_path, quarantine_relative_path,
                outcome, reason, operation_state, observed_bytes, observed_unix_ms, completed_unix_ms)
            VALUES ($key, $source, $companion, $quarantine, 'quarantined', $reason, 'planned', $bytes, $observed, NULL);
            """;
        command.Parameters.AddWithValue("$key", operation.EvidenceKey);
        command.Parameters.AddWithValue("$source", operation.SourceRelativePath);
        command.Parameters.AddWithValue("$companion", (object?)operation.CompanionRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$quarantine", operation.QuarantineRelativePath);
        command.Parameters.AddWithValue("$reason", operation.Reason);
        command.Parameters.AddWithValue("$bytes", operation.ObservedBytes);
        command.Parameters.AddWithValue("$observed", _utcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task PlanCleanupAsync(
        string evidenceKey,
        string sourceRelativePath,
        long observedBytes,
        CancellationToken cancellationToken)
        => TrackWriteAsync(
            "reconciliation-plan",
            () => PlanCleanupCoreAsync(evidenceKey, sourceRelativePath, observedBytes, cancellationToken));

    private async Task PlanCleanupCoreAsync(
        string evidenceKey,
        string sourceRelativePath,
        long observedBytes,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raw_ingress_reconciliation(
                evidence_key, source_relative_path, companion_relative_path, quarantine_relative_path,
                outcome, reason, operation_state, observed_bytes, observed_unix_ms, completed_unix_ms)
            VALUES ($key, $source, NULL, NULL, 'cleaned', 'stale-temporary', 'planned', $bytes, $observed, NULL);
            """;
        command.Parameters.AddWithValue("$key", evidenceKey);
        command.Parameters.AddWithValue("$source", sourceRelativePath);
        command.Parameters.AddWithValue("$bytes", observedBytes);
        command.Parameters.AddWithValue("$observed", _utcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<KeyValuePair<string, string>>> ReadPlannedCleanupsAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_key, source_relative_path
            FROM raw_ingress_reconciliation
            WHERE outcome = 'cleaned' AND operation_state = 'planned'
            ORDER BY reconciliation_id;
            """;
        var operations = new List<KeyValuePair<string, string>>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            operations.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
        }
        return operations;
    }

    internal async Task<IReadOnlyList<RawIngressPlannedQuarantine>> ReadPlannedQuarantinesAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_key, source_relative_path, companion_relative_path,
                   quarantine_relative_path, reason, observed_bytes
            FROM raw_ingress_reconciliation
            WHERE outcome = 'quarantined' AND operation_state = 'planned'
            ORDER BY reconciliation_id;
            """;
        var operations = new List<RawIngressPlannedQuarantine>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            operations.Add(new RawIngressPlannedQuarantine(
                reader.GetString(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5)));
        }
        return operations;
    }

    internal Task CompleteReconciliationAsync(string evidenceKey, CancellationToken cancellationToken)
        => TrackWriteAsync("reconciliation-complete", () => CompleteReconciliationCoreAsync(evidenceKey, cancellationToken));

    private async Task CompleteReconciliationCoreAsync(string evidenceKey, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE raw_ingress_reconciliation
            SET operation_state = 'completed', completed_unix_ms = $completed
            WHERE evidence_key = $key AND operation_state = 'planned';
            """;
        command.Parameters.AddWithValue("$key", evidenceKey);
        command.Parameters.AddWithValue("$completed", _utcNow().ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Planned raw ingress reconciliation operation was not completed exactly once.");
        }
    }

    internal async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 0)
            {
                throw new IOException("Raw ingress WAL checkpoint could not complete without a busy writer.");
            }
            _checkpointRecorder?.Invoke(true);
        }
        catch
        {
            _checkpointRecorder?.Invoke(false);
            throw;
        }
    }

    private async Task<T> TrackTransactionAsync<T>(string operation, Func<Task<T>> transaction)
    {
        try
        {
            var result = await transaction().ConfigureAwait(false);
            _transactionRecorder?.Invoke(operation, true);
            return result;
        }
        catch
        {
            _transactionRecorder?.Invoke(operation, false);
            throw;
        }
    }

    private async Task TrackWriteAsync(string operation, Func<Task> transaction)
    {
        try
        {
            await transaction().ConfigureAwait(false);
            _transactionRecorder?.Invoke(operation, true);
        }
        catch
        {
            _transactionRecorder?.Invoke(operation, false);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        EnsureDatabaseFilesArePhysical();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = _busyTimeoutSeconds
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical();
        await ExecuteNonQueryAsync(connection, null, $"PRAGMA busy_timeout = {_busyTimeoutSeconds * 1000};", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        var journalMode = await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Raw ingress SQLite journal could not enter WAL mode.");
        }
        await ExecuteNonQueryAsync(connection, null, "PRAGMA synchronous = FULL;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA wal_autocheckpoint = 1000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void EnsureDatabaseFilesArePhysical()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(_databasePath)!)!;
        RawIngressFileStore.EnsureNoSymbolicLinks(root, _databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(_databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(_databasePath, "-shm"));
    }

    private SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
            return connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        }
        finally
        {
            _lockWaitRecorder?.Invoke(System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task VerifyConnectionSettingsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var synchronous = await ExecuteScalarLongAsync(connection, "PRAGMA synchronous;", cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ExecuteScalarLongAsync(connection, "PRAGMA foreign_keys;", cancellationToken).ConfigureAwait(false);
        var busyTimeout = await ExecuteScalarLongAsync(connection, "PRAGMA busy_timeout;", cancellationToken).ConfigureAwait(false);
        var autoCheckpoint = await ExecuteScalarLongAsync(connection, "PRAGMA wal_autocheckpoint;", cancellationToken).ConfigureAwait(false);
        if (synchronous != 2 || foreignKeys != 1 || busyTimeout != _busyTimeoutSeconds * 1000L || autoCheckpoint != 1000)
        {
            throw new InvalidDataException("Raw ingress SQLite connection settings do not match the durability policy.");
        }
    }

    private static async Task<List<RawIngressJournalEntry>> ReadClaimsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RawIngressJournalEntry entry,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT agent_id, capture_sequence, capture_id, raw_artifact_id,
                   descriptor_sha256, manifest_sha256, payload_sha256, payload_length,
                   payload_relative_path, sidecar_relative_path, manifest_json,
                   exposure_started_unix_ms, durable_ingress_unix_ms, state, retention_hold, evidence_origin
            FROM raw_captures
            WHERE capture_id = $capture_id
               OR raw_artifact_id = $artifact_id
               OR descriptor_sha256 = $descriptor_sha256
               OR payload_relative_path = $payload_path
               OR sidecar_relative_path = $sidecar_path;
            """;
        AddEntryParameters(command, entry);
        var claims = new List<RawIngressJournalEntry>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claims.Add(ReadEntry(reader));
        }
        return claims;
    }

    private static RawIngressJournalEntry ReadEntry(SqliteDataReader reader)
        => new(
            reader.GetString(0), reader.GetInt64(1), Guid.ParseExact(reader.GetString(2), "N"),
            Guid.ParseExact(reader.GetString(3), "N"), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetInt64(7), reader.GetString(8), reader.GetString(9),
            (byte[])reader[10], DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)), reader.GetString(13), reader.GetBoolean(14),
            Enum.Parse<GalleryEvidenceOrigin>(reader.GetString(15)));

    private static bool IsExact(RawIngressJournalEntry left, RawIngressJournalEntry right)
        => left == right ||
           left.AgentId == right.AgentId && left.CaptureSequence == right.CaptureSequence &&
           left.CaptureId == right.CaptureId && left.ArtifactId == right.ArtifactId &&
           left.DescriptorSha256 == right.DescriptorSha256 && left.ManifestSha256 == right.ManifestSha256 &&
           left.PayloadSha256 == right.PayloadSha256 && left.PayloadLength == right.PayloadLength &&
           left.PayloadRelativePath == right.PayloadRelativePath && left.SidecarRelativePath == right.SidecarRelativePath &&
           left.ManifestJson.AsSpan().SequenceEqual(right.ManifestJson) &&
           left.ExposureStartedUtc == right.ExposureStartedUtc && left.DurableIngressUtc == right.DurableIngressUtc &&
            left.State == right.State && left.EvidenceOrigin == right.EvidenceOrigin;

    private static void AddEntryParameters(SqliteCommand command, RawIngressJournalEntry entry)
    {
        command.Parameters.AddWithValue("$capture_id", entry.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact_id", entry.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$agent_id", entry.AgentId);
        command.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
        command.Parameters.AddWithValue("$descriptor_sha256", entry.DescriptorSha256);
        command.Parameters.AddWithValue("$manifest_sha256", entry.ManifestSha256);
        command.Parameters.AddWithValue("$payload_sha256", entry.PayloadSha256);
        command.Parameters.AddWithValue("$payload_length", entry.PayloadLength);
        command.Parameters.AddWithValue("$payload_path", entry.PayloadRelativePath);
        command.Parameters.AddWithValue("$sidecar_path", entry.SidecarRelativePath);
        command.Parameters.AddWithValue("$manifest_json", entry.ManifestJson);
        command.Parameters.AddWithValue("$exposure_started", entry.ExposureStartedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$durable_ingress", entry.DurableIngressUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$state", entry.State);
        command.Parameters.AddWithValue("$evidence_origin", entry.EvidenceOrigin.ToString());
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
        => Convert.ToInt64(await ExecuteScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ExecuteScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
        => Convert.ToString(await ExecuteScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only internal constant schema and PRAGMA statements are passed to this helper.")]
    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyColumnsAsync(
        SqliteConnection connection,
        string table,
        string expected,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(name, ',') FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        var actual = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Raw ingress SQLite table '{table}' has unexpected columns.");
        }
    }

    private static async Task VerifyIndexAsync(
        SqliteConnection connection,
        string index,
        string expected,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(name, ',') FROM pragma_index_info($index);";
        command.Parameters.AddWithValue("$index", index);
        var actual = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Raw ingress SQLite index '{index}' has unexpected columns.");
        }
    }

    private static async Task VerifyPartialUniqueIndexAsync(
        SqliteConnection connection,
        string table,
        string index,
        string expectedPredicate,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "unique", partial,
                   (SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $index)
            FROM pragma_index_list($table) WHERE name = $index;
            """;
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$index", index);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt32(0) != 1 || reader.GetInt32(1) != 1 ||
            !reader.GetString(2).Contains(expectedPredicate, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Raw ingress SQLite index '{index}' is not the expected partial unique index.");
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only internal constant schema and PRAGMA statements are passed to this helper.")]
    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static readonly IReadOnlyList<CaptureLaneDefinition> DefaultLaneDefinitions =
    [
        new CaptureLaneDefinition(
            "standard",
            Enabled: true,
            Required: true,
            Ordered: true,
            PolicySha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("v1\nstandard\nTrue\nTrue\nTrue"))))
    ];

    private const string RawCaptureColumns =
        "raw_capture_row_id,capture_id,raw_artifact_id,agent_id,capture_sequence,descriptor_sha256,manifest_sha256,payload_sha256,payload_length,payload_relative_path,sidecar_relative_path,manifest_json,exposure_started_unix_ms,durable_ingress_unix_ms,committed_unix_ms,state,retention_hold,failure_reason,evidence_origin";
    private const string LaneDefinitionColumns =
        "lane_name,enabled,required,ordered,policy_sha256,pressure_state,created_unix_ms,updated_unix_ms";
    private const string LaneContextColumns =
        "raw_capture_row_id,context_json,context_sha256,context_source";
    private const string LaneWorkColumns =
        "work_id,raw_capture_row_id,lane_name,agent_id,capture_sequence,required,ordered,state,attempt_count,available_unix_ms,lease_token,lease_owner,lease_expires_unix_ms,completion_token,completed_unix_ms,failure_reason,created_unix_ms,updated_unix_ms";
    private const string TransientEventIdentityColumns =
        "event_id,agent_id,created_unix_ms";
    private const string TransientCandidateColumns =
        "candidate_id,event_id,agent_id,mode,required,reservation_identity_sha256,state,phase,candidate_payload,candidate_payload_sha256,finalization_payload,finalization_receipt_identity_sha256,submission_payload,submission_identity_sha256,acknowledgement_payload,acknowledgement_payload_sha256,source_hold_released,quarantine_reason,timeout_unix_ms,created_unix_ms,updated_unix_ms";
    private const string TransientCandidateSourceColumns =
        "candidate_id,source_ordinal,evidence_id,raw_capture_row_id,source_schema,locator_schema,locator_kind,artifact_id,artifact_role,artifact_variant,recipe_identity_sha256,checksum_sha256,observation_started_utc_ticks,observation_ended_utc_ticks,timing_quality,timing_source,timing_version";
    private const string TransientRuntimePolicyColumns =
        "policy_key,mode,required,candidate_timeout_minutes,updated_unix_ms";
    private const string TransientCaptureWorkColumns =
        "raw_capture_row_id,lane_work_id,mode,required,state,artifact_id,manifest_sha256,created_unix_ms,updated_unix_ms";
    private const string TransientCandidateConflictColumns =
        "conflict_id,candidate_id,event_id,reason,observed_unix_ms";
    private const string CaptureControlStateColumns =
        "state_key,state,version,updated_unix_ms";
    private const string CaptureControlCommandColumns =
        "idempotency_key,target_state,expected_version,actor,reason,payload_sha256,status,result_state,result_version,changed,requested_unix_ms,completed_unix_ms";
    private const string CaptureScheduleRevisionColumns =
        "revision_id,revision_number,profile_json,profile_sha256,schedule_sha256,source,actor,reason,created_unix_ms";
    private const string CaptureScheduleStateColumns =
        "state_key,active_revision_id,pending_revision_id,version,last_evaluated_unix_ms,last_decision_unix_ms,last_decision_admitted,last_decision_reason,last_decision_profile_id,last_decision_interval_id,next_transition_unix_ms,updated_unix_ms";
    private const string CaptureScheduleActivationColumns =
        "activation_id,idempotency_key,from_revision_id,to_revision_id,actor,reason,state_version,activated_unix_ms";
    private const string CaptureScheduleOverrideColumns =
        "override_id,schedule_revision_id,mode,start_unix_ms,end_unix_ms,setpoint_profile_id,one_shot,consumed_unix_ms,cleared_unix_ms,actor,reason,created_unix_ms";
    private const string CaptureScheduleCommandColumns =
        "idempotency_key,command_kind,payload_sha256,result_active_revision_id,result_pending_revision_id,result_state_version,result_last_evaluated_unix_ms,created_unix_ms,completed_unix_ms";
    private const string CaptureScheduleExpansionColumns =
        "expansion_key,expansion_sha256,revision_id,deployment_location_id,deployment_location_version,preview_start_unix_ms,preview_end_unix_ms,expansion_algorithm_version,time_zone_rule_sha256,solar_algorithm_version,created_unix_ms";
    private const string CaptureScheduleIntervalColumns =
        "expansion_key,ordinal,interval_id,source,disposition,start_unix_ms,end_unix_ms,local_date,setpoint_profile_id,solar_algorithm_version";
    private const string CaptureScheduleUnavailableColumns =
        "expansion_key,ordinal,window_id,source,local_date,start_unix_ms,end_unix_ms,reason_code,solar_algorithm_version";
    private const string CaptureScheduleAdmissionColumns =
        "admission_id,revision_id,override_id,decision_unix_ms,created_unix_ms";
    private const string CaptureScheduleOverrideEventColumns =
        "event_id,override_id,event_kind,actor,reason,occurred_unix_ms";
    private const string CalibrationLibraryBundleColumns =
        "bundle_id,bundle_identity_sha256,source,bundle_json,profile_relative_path,profile_identity_sha256,acquisition_model_identity_sha256,agent_id,rig_id,rig_profile_sha256,sensor_profile_sha256,input_layout_sha256,output_layout_sha256,minimum_gain,maximum_gain,minimum_offset,maximum_offset,minimum_light_exposure_ticks,maximum_light_exposure_ticks,minimum_temperature_c,maximum_temperature_c,effective_from_unix_ms,effective_until_unix_ms,publication_state,retention_hold,failure_reason,created_unix_ms,updated_unix_ms";
    private const string CalibrationLibraryArtifactColumns =
        "bundle_id,ordinal,artifact_id,reference_kind,role,source_index,manifest_relative_path,manifest_sha256,payload_relative_path,payload_sha256,ordered_source_artifact_ids_json,master_recipe_json";
    private const string CalibrationLibraryStateColumns =
        "state_key,active_bundle_id,version,last_selection_reason,last_selection_unix_ms,last_reconciliation_reason,last_reconciliation_unix_ms,updated_unix_ms";
    private const string CalibrationLibraryActivationColumns =
        "activation_id,idempotency_key,from_bundle_id,to_bundle_id,actor,reason,state_version,activated_unix_ms";
    private const string CalibrationLibraryCommandColumns =
        "idempotency_key,command_kind,payload_sha256,result_bundle_id,result_job_id,result_state_version,result_json,created_unix_ms,completed_unix_ms";
    private const string CalibrationAcquisitionJobColumns =
        "job_id,camera_key,idempotency_key,plan_json,plan_sha256,state,phase,attempt_count,bundle_id,failure_reason,actor,reason,created_unix_ms,updated_unix_ms,completed_unix_ms";
    private const string CalibrationLibraryReconciliationColumns =
        "reconciliation_id,evidence_key,source_relative_path,quarantine_relative_path,outcome,reason,operation_state,observed_bytes,observed_unix_ms,completed_unix_ms";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS raw_capture_sequences (
            agent_id TEXT PRIMARY KEY,
            last_sequence INTEGER NOT NULL CHECK (last_sequence >= 0)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS raw_capture_assignments (
            capture_id TEXT PRIMARY KEY,
            raw_artifact_id TEXT NOT NULL UNIQUE,
            agent_id TEXT NOT NULL,
            capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            UNIQUE (agent_id, capture_sequence)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS raw_captures (
            raw_capture_row_id INTEGER PRIMARY KEY,
            capture_id TEXT NOT NULL UNIQUE,
            raw_artifact_id TEXT NOT NULL UNIQUE,
            agent_id TEXT NOT NULL,
            capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            descriptor_sha256 TEXT NOT NULL UNIQUE CHECK (length(descriptor_sha256) = 64),
            manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            payload_length INTEGER NOT NULL CHECK (payload_length >= 0),
            payload_relative_path TEXT NOT NULL UNIQUE,
            sidecar_relative_path TEXT NOT NULL UNIQUE,
            manifest_json BLOB NOT NULL,
            exposure_started_unix_ms INTEGER NOT NULL,
            durable_ingress_unix_ms INTEGER NOT NULL,
            committed_unix_ms INTEGER NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('committed', 'missing_evidence', 'quarantined')),
            retention_hold INTEGER NOT NULL DEFAULT 1 CHECK (retention_hold IN (0, 1)),
            failure_reason TEXT,
            evidence_origin TEXT NOT NULL DEFAULT 'Unknown' CHECK (evidence_origin IN ('Unknown', 'Simulated', 'DeveloperFixture')),
            UNIQUE (agent_id, capture_sequence),
            FOREIGN KEY (capture_id) REFERENCES raw_capture_assignments(capture_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_raw_captures_discovery ON raw_captures(state, agent_id, capture_sequence);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_backlog ON raw_captures(state, durable_ingress_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_retention ON raw_captures(retention_hold, exposure_started_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_gallery_time
            ON raw_captures(exposure_started_unix_ms DESC, capture_sequence DESC, raw_capture_row_id DESC);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_gallery_sequence
            ON raw_captures(capture_sequence DESC, raw_capture_row_id DESC);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_gallery_state
            ON raw_captures(state, capture_sequence DESC, raw_capture_row_id DESC);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_gallery_origin
            ON raw_captures(evidence_origin, capture_sequence DESC, raw_capture_row_id DESC);
        CREATE TABLE IF NOT EXISTS raw_ingress_reconciliation (
            reconciliation_id INTEGER PRIMARY KEY,
            evidence_key TEXT NOT NULL UNIQUE,
            source_relative_path TEXT NOT NULL,
            companion_relative_path TEXT,
            quarantine_relative_path TEXT,
            outcome TEXT NOT NULL CHECK (outcome IN ('cleaned', 'quarantined')),
            reason TEXT NOT NULL,
            operation_state TEXT NOT NULL CHECK (operation_state IN ('planned', 'completed')),
            observed_bytes INTEGER NOT NULL DEFAULT 0,
            observed_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_definitions (
            lane_name TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
            policy_sha256 TEXT NOT NULL CHECK (length(policy_sha256) = 64),
            pressure_state INTEGER NOT NULL DEFAULT 0 CHECK (pressure_state IN (0, 1, 2)),
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_contexts (
            raw_capture_row_id INTEGER PRIMARY KEY,
            context_json BLOB NOT NULL,
            context_sha256 TEXT NOT NULL CHECK (length(context_sha256) = 64),
            context_source TEXT NOT NULL CHECK (context_source IN ('capture', 'manifest-fallback')),
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_work (
            work_id INTEGER PRIMARY KEY,
             raw_capture_row_id INTEGER NOT NULL,
             lane_name TEXT NOT NULL,
             agent_id TEXT NOT NULL,
             capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
            state TEXT NOT NULL CHECK (state IN ('pending', 'leased', 'retry_wait', 'completed', 'quarantined', 'abandoned')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            available_unix_ms INTEGER NOT NULL,
            lease_token TEXT,
            lease_owner TEXT,
            lease_expires_unix_ms INTEGER,
            completion_token TEXT,
            completed_unix_ms INTEGER,
            failure_reason TEXT,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            UNIQUE (raw_capture_row_id, lane_name),
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE,
            FOREIGN KEY (lane_name) REFERENCES capture_lane_definitions(lane_name)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_claim
            ON capture_lane_work(lane_name, state, available_unix_ms, work_id);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_lease
            ON capture_lane_work(state, lease_expires_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_backlog
            ON capture_lane_work(lane_name, state, created_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_raw
            ON capture_lane_work(raw_capture_row_id, required, state);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_ordered
            ON capture_lane_work(lane_name, agent_id, capture_sequence)
            WHERE state NOT IN ('completed', 'abandoned');
        CREATE TABLE IF NOT EXISTS capture_control_state (
            state_key INTEGER PRIMARY KEY CHECK (state_key = 1),
            state TEXT NOT NULL CHECK (state IN ('running', 'pause_requested', 'paused')),
            version INTEGER NOT NULL CHECK (version >= 0),
            updated_unix_ms INTEGER NOT NULL
        ) STRICT;
        INSERT INTO capture_control_state(state_key, state, version, updated_unix_ms)
        VALUES (1, 'running', 0, unixepoch('subsec') * 1000)
        ON CONFLICT(state_key) DO NOTHING;
        CREATE TABLE IF NOT EXISTS capture_control_commands (
            idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
            target_state TEXT NOT NULL CHECK (target_state IN ('running', 'paused')),
            expected_version INTEGER CHECK (expected_version IS NULL OR expected_version >= 0),
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            status TEXT NOT NULL CHECK (status IN ('pending', 'completed')),
            result_state TEXT CHECK (result_state IS NULL OR result_state IN ('running', 'paused')),
            result_version INTEGER CHECK (result_version IS NULL OR result_version >= 0),
            changed INTEGER NOT NULL CHECK (changed IN (0, 1)),
            requested_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER
        ) STRICT;
        """;

    private const string CaptureScheduleSchemaSql = """
        CREATE TABLE IF NOT EXISTS capture_schedule_revisions (
            revision_id TEXT PRIMARY KEY CHECK (length(revision_id) BETWEEN 1 AND 128),
            revision_number INTEGER NOT NULL UNIQUE CHECK (revision_number > 0),
            profile_json BLOB NOT NULL,
            profile_sha256 TEXT NOT NULL CHECK (length(profile_sha256) = 64),
            schedule_sha256 TEXT NOT NULL CHECK (length(schedule_sha256) = 64),
            source TEXT NOT NULL CHECK (length(source) BETWEEN 1 AND 32),
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
            created_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_capture_schedule_revisions_created
            ON capture_schedule_revisions(created_unix_ms, revision_number);
        CREATE TABLE IF NOT EXISTS capture_schedule_state (
            state_key INTEGER PRIMARY KEY CHECK (state_key = 1),
            active_revision_id TEXT NOT NULL,
            pending_revision_id TEXT,
            version INTEGER NOT NULL CHECK (version >= 0),
            last_evaluated_unix_ms INTEGER,
            last_decision_unix_ms INTEGER,
            last_decision_admitted INTEGER CHECK (last_decision_admitted IS NULL OR last_decision_admitted IN (0, 1)),
            last_decision_reason TEXT,
            last_decision_profile_id TEXT,
            last_decision_interval_id TEXT,
            next_transition_unix_ms INTEGER,
            updated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (active_revision_id) REFERENCES capture_schedule_revisions(revision_id),
            FOREIGN KEY (pending_revision_id) REFERENCES capture_schedule_revisions(revision_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_schedule_activations (
            activation_id INTEGER PRIMARY KEY,
            idempotency_key TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) BETWEEN 1 AND 128),
            from_revision_id TEXT,
            to_revision_id TEXT NOT NULL,
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
            state_version INTEGER NOT NULL CHECK (state_version > 0),
            activated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (from_revision_id) REFERENCES capture_schedule_revisions(revision_id),
            FOREIGN KEY (to_revision_id) REFERENCES capture_schedule_revisions(revision_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_schedule_overrides (
            override_id TEXT PRIMARY KEY CHECK (length(override_id) BETWEEN 1 AND 128),
            schedule_revision_id TEXT NOT NULL,
            mode TEXT NOT NULL CHECK (mode IN ('force_closed', 'force_open')),
            start_unix_ms INTEGER NOT NULL,
            end_unix_ms INTEGER NOT NULL CHECK (end_unix_ms > start_unix_ms),
            setpoint_profile_id TEXT,
            one_shot INTEGER NOT NULL CHECK (one_shot IN (0, 1)),
            consumed_unix_ms INTEGER,
            cleared_unix_ms INTEGER,
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
            created_unix_ms INTEGER NOT NULL,
            CHECK ((mode = 'force_open' AND setpoint_profile_id IS NOT NULL) OR
                   (mode = 'force_closed' AND setpoint_profile_id IS NULL)),
            CHECK (one_shot = 0 OR mode = 'force_open'),
            FOREIGN KEY (schedule_revision_id) REFERENCES capture_schedule_revisions(revision_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_capture_schedule_overrides_active
            ON capture_schedule_overrides(cleared_unix_ms, end_unix_ms, mode, override_id);
        CREATE TABLE IF NOT EXISTS capture_schedule_commands (
            idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
            command_kind TEXT NOT NULL CHECK (length(command_kind) BETWEEN 1 AND 32),
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            result_active_revision_id TEXT NOT NULL,
            result_pending_revision_id TEXT,
            result_state_version INTEGER CHECK (result_state_version IS NULL OR result_state_version >= 0),
            result_last_evaluated_unix_ms INTEGER,
            created_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER,
            FOREIGN KEY (result_active_revision_id) REFERENCES capture_schedule_revisions(revision_id),
            FOREIGN KEY (result_pending_revision_id) REFERENCES capture_schedule_revisions(revision_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_schedule_expansions (
            expansion_key TEXT PRIMARY KEY CHECK (length(expansion_key) = 64),
            expansion_sha256 TEXT NOT NULL CHECK (length(expansion_sha256) = 64),
            revision_id TEXT NOT NULL,
            deployment_location_id TEXT NOT NULL CHECK (length(deployment_location_id) BETWEEN 1 AND 128),
            deployment_location_version INTEGER NOT NULL CHECK (deployment_location_version > 0),
            preview_start_unix_ms INTEGER NOT NULL,
            preview_end_unix_ms INTEGER NOT NULL CHECK (preview_end_unix_ms > preview_start_unix_ms),
            expansion_algorithm_version TEXT NOT NULL,
            time_zone_rule_sha256 TEXT NOT NULL CHECK (length(time_zone_rule_sha256) = 64),
            solar_algorithm_version TEXT NOT NULL,
            created_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (revision_id) REFERENCES capture_schedule_revisions(revision_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_capture_schedule_expansions_lookup
            ON capture_schedule_expansions(
                revision_id, deployment_location_id, deployment_location_version,
                preview_start_unix_ms, preview_end_unix_ms);
        CREATE TABLE IF NOT EXISTS capture_schedule_intervals (
            expansion_key TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            interval_id TEXT NOT NULL,
            source TEXT NOT NULL,
            disposition TEXT NOT NULL CHECK (disposition IN ('open', 'closed')),
            start_unix_ms INTEGER NOT NULL,
            end_unix_ms INTEGER NOT NULL CHECK (end_unix_ms > start_unix_ms),
            local_date TEXT,
            setpoint_profile_id TEXT,
            solar_algorithm_version TEXT,
            PRIMARY KEY (expansion_key, ordinal),
            FOREIGN KEY (expansion_key) REFERENCES capture_schedule_expansions(expansion_key) ON DELETE CASCADE
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_capture_schedule_intervals_bounds
            ON capture_schedule_intervals(expansion_key, start_unix_ms, end_unix_ms, ordinal);
        CREATE TABLE IF NOT EXISTS capture_schedule_unavailable (
            expansion_key TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            window_id TEXT NOT NULL,
            source TEXT NOT NULL,
            local_date TEXT NOT NULL,
            start_unix_ms INTEGER NOT NULL,
            end_unix_ms INTEGER NOT NULL CHECK (end_unix_ms > start_unix_ms),
            reason_code TEXT NOT NULL,
            solar_algorithm_version TEXT NOT NULL,
            PRIMARY KEY (expansion_key, ordinal),
            FOREIGN KEY (expansion_key) REFERENCES capture_schedule_expansions(expansion_key) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_schedule_admissions (
            admission_id TEXT PRIMARY KEY CHECK (length(admission_id) BETWEEN 1 AND 128),
            revision_id TEXT NOT NULL,
            override_id TEXT,
            decision_unix_ms INTEGER NOT NULL,
            created_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (revision_id) REFERENCES capture_schedule_revisions(revision_id),
            FOREIGN KEY (override_id) REFERENCES capture_schedule_overrides(override_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_schedule_override_events (
            event_id TEXT PRIMARY KEY CHECK (length(event_id) BETWEEN 1 AND 128),
            override_id TEXT NOT NULL,
            event_kind TEXT NOT NULL CHECK (event_kind IN ('created', 'consumed', 'cleared')),
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
            occurred_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (override_id) REFERENCES capture_schedule_overrides(override_id)
        ) STRICT;
        """;

    private const string CalibrationLibrarySchemaSql = """
        CREATE TABLE IF NOT EXISTS calibration_library_bundles (
            bundle_id TEXT PRIMARY KEY CHECK (length(bundle_id) BETWEEN 1 AND 128),
            bundle_identity_sha256 TEXT NOT NULL UNIQUE CHECK (length(bundle_identity_sha256) = 64),
            source TEXT NOT NULL CHECK (source IN ('legacy-synthetic-v1', 'virtual-acquisition-v1')),
            bundle_json BLOB NOT NULL CHECK (length(bundle_json) BETWEEN 1 AND 1048576),
            profile_relative_path TEXT COLLATE NOCASE NOT NULL UNIQUE,
            profile_identity_sha256 TEXT NOT NULL CHECK (length(profile_identity_sha256) = 64),
            acquisition_model_identity_sha256 TEXT NOT NULL CHECK (length(acquisition_model_identity_sha256) = 64),
            agent_id TEXT NOT NULL CHECK (length(agent_id) BETWEEN 1 AND 128),
            rig_id TEXT NOT NULL CHECK (length(rig_id) BETWEEN 1 AND 128),
            rig_profile_sha256 TEXT NOT NULL CHECK (length(rig_profile_sha256) = 64),
            sensor_profile_sha256 TEXT NOT NULL CHECK (length(sensor_profile_sha256) = 64),
            input_layout_sha256 TEXT NOT NULL CHECK (length(input_layout_sha256) = 64),
            output_layout_sha256 TEXT NOT NULL CHECK (length(output_layout_sha256) = 64),
            minimum_gain REAL NOT NULL,
            maximum_gain REAL NOT NULL CHECK (maximum_gain >= minimum_gain),
            minimum_offset REAL,
            maximum_offset REAL,
            minimum_light_exposure_ticks INTEGER,
            maximum_light_exposure_ticks INTEGER,
            minimum_temperature_c REAL,
            maximum_temperature_c REAL,
            effective_from_unix_ms INTEGER NOT NULL,
            effective_until_unix_ms INTEGER,
            publication_state TEXT NOT NULL CHECK (publication_state IN ('published', 'incomplete', 'corrupt', 'quarantined')),
            retention_hold INTEGER NOT NULL DEFAULT 1 CHECK (retention_hold IN (0, 1)),
            failure_reason TEXT,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            CHECK ((minimum_offset IS NULL AND maximum_offset IS NULL) OR
                   (minimum_offset IS NOT NULL AND maximum_offset IS NOT NULL AND maximum_offset >= minimum_offset)),
            CHECK ((minimum_light_exposure_ticks IS NULL AND maximum_light_exposure_ticks IS NULL) OR
                   (minimum_light_exposure_ticks > 0 AND maximum_light_exposure_ticks >= minimum_light_exposure_ticks)),
            CHECK ((minimum_temperature_c IS NULL AND maximum_temperature_c IS NULL) OR
                   (minimum_temperature_c IS NOT NULL AND maximum_temperature_c IS NOT NULL AND maximum_temperature_c >= minimum_temperature_c)),
            CHECK (effective_until_unix_ms IS NULL OR effective_until_unix_ms > effective_from_unix_ms)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_calibration_library_bundles_selection
            ON calibration_library_bundles(
                publication_state, agent_id, rig_id, rig_profile_sha256, sensor_profile_sha256,
                effective_from_unix_ms, effective_until_unix_ms, bundle_id);
        CREATE INDEX IF NOT EXISTS ix_calibration_library_bundles_created
            ON calibration_library_bundles(created_unix_ms DESC, bundle_id);
        CREATE TABLE IF NOT EXISTS calibration_library_artifacts (
            bundle_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            artifact_id TEXT NOT NULL UNIQUE,
            reference_kind TEXT NOT NULL CHECK (reference_kind IN ('bias', 'dark', 'flat', 'defect')),
            role TEXT NOT NULL CHECK (role IN ('source', 'master')),
            source_index INTEGER CHECK (source_index IS NULL OR source_index BETWEEN 0 AND 2),
            manifest_relative_path TEXT COLLATE NOCASE NOT NULL UNIQUE,
            manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
            payload_relative_path TEXT COLLATE NOCASE NOT NULL UNIQUE,
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            ordered_source_artifact_ids_json BLOB NOT NULL,
            master_recipe_json BLOB,
            PRIMARY KEY (bundle_id, ordinal),
            CHECK ((role = 'source' AND source_index IS NOT NULL AND master_recipe_json IS NULL) OR
                   (role = 'master' AND source_index IS NULL)),
            FOREIGN KEY (bundle_id) REFERENCES calibration_library_bundles(bundle_id) ON DELETE CASCADE
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_calibration_library_artifacts_role
            ON calibration_library_artifacts(bundle_id, role, reference_kind, source_index, ordinal);
        CREATE TABLE IF NOT EXISTS calibration_library_state (
            state_key INTEGER PRIMARY KEY CHECK (state_key = 1),
            active_bundle_id TEXT,
            version INTEGER NOT NULL CHECK (version >= 0),
            last_selection_reason TEXT,
            last_selection_unix_ms INTEGER,
            last_reconciliation_reason TEXT,
            last_reconciliation_unix_ms INTEGER,
            updated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (active_bundle_id) REFERENCES calibration_library_bundles(bundle_id)
        ) STRICT;
        INSERT INTO calibration_library_state(state_key, active_bundle_id, version, updated_unix_ms)
        VALUES (1, NULL, 0, unixepoch('subsec') * 1000)
        ON CONFLICT(state_key) DO NOTHING;
        CREATE TABLE IF NOT EXISTS calibration_library_activations (
            activation_id INTEGER PRIMARY KEY,
            idempotency_key TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) BETWEEN 1 AND 128),
            from_bundle_id TEXT,
            to_bundle_id TEXT NOT NULL,
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
            state_version INTEGER NOT NULL CHECK (state_version > 0),
            activated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (from_bundle_id) REFERENCES calibration_library_bundles(bundle_id),
            FOREIGN KEY (to_bundle_id) REFERENCES calibration_library_bundles(bundle_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_calibration_library_activations_history
            ON calibration_library_activations(activated_unix_ms DESC, activation_id DESC);
        CREATE TABLE IF NOT EXISTS calibration_library_commands (
            idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
            command_kind TEXT NOT NULL CHECK (command_kind IN ('acquire', 'cancel', 'activate', 'rollback')),
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            result_bundle_id TEXT,
            result_job_id TEXT,
            result_state_version INTEGER CHECK (result_state_version IS NULL OR result_state_version >= 0),
            result_json BLOB NOT NULL CHECK (length(result_json) BETWEEN 1 AND 1048576),
            created_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER,
            FOREIGN KEY (result_bundle_id) REFERENCES calibration_library_bundles(bundle_id),
            FOREIGN KEY (result_job_id) REFERENCES calibration_acquisition_jobs(job_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS calibration_acquisition_jobs (
            job_id TEXT PRIMARY KEY CHECK (length(job_id) BETWEEN 1 AND 128),
            camera_key TEXT NOT NULL CHECK (length(camera_key) BETWEEN 1 AND 256),
            idempotency_key TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) BETWEEN 1 AND 128),
            plan_json BLOB NOT NULL CHECK (length(plan_json) BETWEEN 1 AND 1048576),
            plan_sha256 TEXT NOT NULL CHECK (length(plan_sha256) = 64),
            state TEXT NOT NULL CHECK (state IN ('planned', 'acquiring', 'building', 'publishing', 'published', 'failed', 'cancelled')),
            phase TEXT NOT NULL CHECK (length(phase) BETWEEN 1 AND 64),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            bundle_id TEXT,
            failure_reason TEXT,
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER,
            FOREIGN KEY (bundle_id) REFERENCES calibration_library_bundles(bundle_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_calibration_acquisition_jobs_camera
            ON calibration_acquisition_jobs(camera_key, state, created_unix_ms, job_id);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_calibration_acquisition_jobs_camera_nonterminal
            ON calibration_acquisition_jobs(camera_key)
            WHERE state NOT IN ('published', 'failed', 'cancelled');
        CREATE TABLE IF NOT EXISTS calibration_library_reconciliation (
            reconciliation_id INTEGER PRIMARY KEY,
            evidence_key TEXT NOT NULL UNIQUE CHECK (length(evidence_key) = 64),
            source_relative_path TEXT NOT NULL,
            quarantine_relative_path TEXT,
            outcome TEXT NOT NULL CHECK (outcome IN ('adopted', 'failed', 'quarantined')),
            reason TEXT NOT NULL,
            operation_state TEXT NOT NULL CHECK (operation_state IN ('planned', 'completed')),
            observed_bytes INTEGER NOT NULL DEFAULT 0 CHECK (observed_bytes >= 0),
            observed_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_calibration_library_reconciliation_state
            ON calibration_library_reconciliation(operation_state, observed_unix_ms, reconciliation_id);
        """;

    private const string CalibrationAcquisitionV9CorrectionSql = """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_calibration_acquisition_jobs_camera_nonterminal
            ON calibration_acquisition_jobs(camera_key)
            WHERE state NOT IN ('published', 'failed', 'cancelled');
        """;

    private const string LaneSchemaSql = """
        CREATE TABLE IF NOT EXISTS capture_lane_definitions (
            lane_name TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
            policy_sha256 TEXT NOT NULL CHECK (length(policy_sha256) = 64),
            pressure_state INTEGER NOT NULL DEFAULT 0 CHECK (pressure_state IN (0, 1, 2)),
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_contexts (
            raw_capture_row_id INTEGER PRIMARY KEY,
            context_json BLOB NOT NULL,
            context_sha256 TEXT NOT NULL CHECK (length(context_sha256) = 64),
            context_source TEXT NOT NULL CHECK (context_source IN ('capture', 'manifest-fallback')),
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_work (
            work_id INTEGER PRIMARY KEY,
             raw_capture_row_id INTEGER NOT NULL,
             lane_name TEXT NOT NULL,
             agent_id TEXT NOT NULL,
             capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
            state TEXT NOT NULL CHECK (state IN ('pending', 'leased', 'retry_wait', 'completed', 'quarantined', 'abandoned')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            available_unix_ms INTEGER NOT NULL,
            lease_token TEXT,
            lease_owner TEXT,
            lease_expires_unix_ms INTEGER,
            completion_token TEXT,
            completed_unix_ms INTEGER,
            failure_reason TEXT,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            UNIQUE (raw_capture_row_id, lane_name),
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE,
            FOREIGN KEY (lane_name) REFERENCES capture_lane_definitions(lane_name)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_claim
            ON capture_lane_work(lane_name, state, available_unix_ms, work_id);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_lease
            ON capture_lane_work(state, lease_expires_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_backlog
            ON capture_lane_work(lane_name, state, created_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_raw
            ON capture_lane_work(raw_capture_row_id, required, state);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_ordered
            ON capture_lane_work(lane_name, agent_id, capture_sequence)
            WHERE state NOT IN ('completed', 'abandoned');
        """;

    private const string TransientSchemaSql = """
        CREATE TABLE IF NOT EXISTS transient_runtime_policy (
            policy_key INTEGER PRIMARY KEY CHECK (policy_key = 1),
            mode TEXT NOT NULL CHECK (mode IN ('off', 'edge', 'central', 'hybrid')),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            candidate_timeout_minutes INTEGER NOT NULL CHECK (candidate_timeout_minutes > 0),
            updated_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS transient_event_identities (
            event_id TEXT PRIMARY KEY,
            agent_id TEXT NOT NULL,
            created_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS transient_candidates (
            candidate_id TEXT PRIMARY KEY,
            event_id TEXT NOT NULL,
            agent_id TEXT NOT NULL,
            mode TEXT NOT NULL CHECK (mode IN ('edge', 'hybrid')),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            reservation_identity_sha256 TEXT NOT NULL CHECK (length(reservation_identity_sha256) = 64),
            state TEXT NOT NULL CHECK (state IN ('pending', 'provisional', 'validated', 'rejected', 'needs_review')),
            phase TEXT NOT NULL CHECK (phase IN ('reserved', 'candidate_persisted', 'finalized', 'handoff_pending', 'acknowledged', 'quarantined')),
            candidate_payload BLOB,
            candidate_payload_sha256 TEXT CHECK (candidate_payload_sha256 IS NULL OR length(candidate_payload_sha256) = 64),
            finalization_payload BLOB,
            finalization_receipt_identity_sha256 TEXT CHECK (finalization_receipt_identity_sha256 IS NULL OR length(finalization_receipt_identity_sha256) = 64),
            submission_payload BLOB,
            submission_identity_sha256 TEXT CHECK (submission_identity_sha256 IS NULL OR length(submission_identity_sha256) = 64),
            acknowledgement_payload BLOB,
            acknowledgement_payload_sha256 TEXT CHECK (acknowledgement_payload_sha256 IS NULL OR length(acknowledgement_payload_sha256) = 64),
            source_hold_released INTEGER NOT NULL DEFAULT 0 CHECK (source_hold_released IN (0, 1)),
            quarantine_reason TEXT,
            timeout_unix_ms INTEGER NOT NULL,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (event_id) REFERENCES transient_event_identities(event_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS transient_candidate_sources (
            candidate_id TEXT NOT NULL,
            source_ordinal INTEGER NOT NULL CHECK (source_ordinal >= 0),
            evidence_id TEXT NOT NULL,
            raw_capture_row_id INTEGER NOT NULL,
            source_schema TEXT NOT NULL,
            locator_schema TEXT NOT NULL,
            locator_kind INTEGER NOT NULL,
            artifact_id TEXT NOT NULL,
            artifact_role INTEGER NOT NULL,
            artifact_variant TEXT NOT NULL,
            recipe_identity_sha256 TEXT NOT NULL CHECK (length(recipe_identity_sha256) = 64),
            checksum_sha256 TEXT NOT NULL CHECK (length(checksum_sha256) = 64),
            observation_started_utc_ticks INTEGER NOT NULL,
            observation_ended_utc_ticks INTEGER NOT NULL,
            timing_quality INTEGER NOT NULL,
            timing_source TEXT NOT NULL,
            timing_version TEXT NOT NULL,
            PRIMARY KEY (candidate_id, source_ordinal),
            UNIQUE (candidate_id, evidence_id),
            FOREIGN KEY (candidate_id) REFERENCES transient_candidates(candidate_id) ON DELETE CASCADE,
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS transient_capture_work (
            raw_capture_row_id INTEGER PRIMARY KEY,
            lane_work_id INTEGER NOT NULL UNIQUE,
            mode TEXT NOT NULL CHECK (mode IN ('edge', 'hybrid')),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            state TEXT NOT NULL CHECK (state IN ('pending', 'candidate_persisted', 'completed', 'quarantined', 'abandoned')),
            artifact_id TEXT NOT NULL UNIQUE,
            manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id),
            FOREIGN KEY (lane_work_id) REFERENCES capture_lane_work(work_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS transient_candidate_conflicts (
            conflict_id INTEGER PRIMARY KEY,
            candidate_id TEXT,
            event_id TEXT,
            reason TEXT NOT NULL,
            observed_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_transient_candidates_backlog
            ON transient_candidates(state, created_unix_ms, candidate_id);
        CREATE INDEX IF NOT EXISTS ix_transient_candidate_sources_raw
            ON transient_candidate_sources(raw_capture_row_id, candidate_id);
        CREATE INDEX IF NOT EXISTS ix_transient_capture_work_backlog
            ON transient_capture_work(state, created_unix_ms, raw_capture_row_id);
        CREATE INDEX IF NOT EXISTS ix_transient_candidate_conflicts_observed
            ON transient_candidate_conflicts(observed_unix_ms, conflict_id);
        CREATE INDEX IF NOT EXISTS ix_transient_candidate_conflicts_candidate
            ON transient_candidate_conflicts(candidate_id, conflict_id);
        """;

    private const string TransientCaptureWorkV5MigrationSql = """
        DROP INDEX ix_transient_capture_work_backlog;
        ALTER TABLE transient_capture_work RENAME TO transient_capture_work_v4;
        CREATE TABLE transient_capture_work (
            raw_capture_row_id INTEGER PRIMARY KEY,
            lane_work_id INTEGER NOT NULL UNIQUE,
            mode TEXT NOT NULL CHECK (mode IN ('edge', 'hybrid')),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            state TEXT NOT NULL CHECK (state IN ('pending', 'candidate_persisted', 'completed', 'quarantined', 'abandoned')),
            artifact_id TEXT NOT NULL UNIQUE,
            manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id),
            FOREIGN KEY (lane_work_id) REFERENCES capture_lane_work(work_id)
        ) STRICT;
        INSERT INTO transient_capture_work(
            raw_capture_row_id, lane_work_id, mode, required, state,
            artifact_id, manifest_sha256, created_unix_ms, updated_unix_ms)
        SELECT raw_capture_row_id, lane_work_id, mode, required, state,
               artifact_id, manifest_sha256, created_unix_ms, updated_unix_ms
        FROM transient_capture_work_v4;
        DROP TABLE transient_capture_work_v4;
        CREATE INDEX ix_transient_capture_work_backlog
            ON transient_capture_work(state, created_unix_ms, raw_capture_row_id);
        """;

    private const string GalleryV6ColumnMigrationSql = """
        ALTER TABLE raw_captures
            ADD COLUMN evidence_origin TEXT NOT NULL DEFAULT 'Unknown'
                CHECK (evidence_origin IN ('Unknown', 'Simulated', 'DeveloperFixture'));
        """;

    private const string GalleryV6MigrationSql = """
        CREATE INDEX IF NOT EXISTS ix_raw_captures_gallery_time
            ON raw_captures(exposure_started_unix_ms DESC, capture_sequence DESC, raw_capture_row_id DESC);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_gallery_sequence
            ON raw_captures(capture_sequence DESC, raw_capture_row_id DESC);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_gallery_state
            ON raw_captures(state, capture_sequence DESC, raw_capture_row_id DESC);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_gallery_origin
            ON raw_captures(evidence_origin, capture_sequence DESC, raw_capture_row_id DESC);
        CREATE TABLE IF NOT EXISTS capture_control_state (
            state_key INTEGER PRIMARY KEY CHECK (state_key = 1),
            state TEXT NOT NULL CHECK (state IN ('running', 'pause_requested', 'paused')),
            version INTEGER NOT NULL CHECK (version >= 0),
            updated_unix_ms INTEGER NOT NULL
        ) STRICT;
        INSERT INTO capture_control_state(state_key, state, version, updated_unix_ms)
        VALUES (1, 'running', 0, unixepoch('subsec') * 1000)
        ON CONFLICT(state_key) DO NOTHING;
        CREATE TABLE IF NOT EXISTS capture_control_commands (
            idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
            target_state TEXT NOT NULL CHECK (target_state IN ('running', 'paused')),
            expected_version INTEGER CHECK (expected_version IS NULL OR expected_version >= 0),
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            status TEXT NOT NULL CHECK (status IN ('pending', 'completed')),
            result_state TEXT CHECK (result_state IS NULL OR result_state IN ('running', 'paused')),
            result_version INTEGER CHECK (result_version IS NULL OR result_version >= 0),
            changed INTEGER NOT NULL CHECK (changed IN (0, 1)),
            requested_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER
        ) STRICT;
        """;

    private const string InsertCaptureSql = """
        INSERT INTO raw_captures(
            capture_id, raw_artifact_id, agent_id, capture_sequence,
            descriptor_sha256, manifest_sha256, payload_sha256, payload_length,
            payload_relative_path, sidecar_relative_path, manifest_json,
            exposure_started_unix_ms, durable_ingress_unix_ms, committed_unix_ms,
            state, retention_hold, evidence_origin)
        VALUES (
            $capture_id, $artifact_id, $agent_id, $capture_sequence,
            $descriptor_sha256, $manifest_sha256, $payload_sha256, $payload_length,
            $payload_path, $sidecar_path, $manifest_json,
            $exposure_started, $durable_ingress, $committed,
            $state, 1, $evidence_origin);
        """;
}

[Serializable]
internal sealed class RawIngressConflictException : InvalidOperationException
{
    internal RawIngressConflictException()
    {
    }

    internal RawIngressConflictException(string message)
        : base(message)
    {
    }

    internal RawIngressConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
