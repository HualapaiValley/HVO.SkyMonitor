using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed record CenteredRunSnapshot(
    CameraKey Camera,
    Guid RunId,
    string GraphRevisionId,
    CameraModuleConfig Configuration,
    CaptureLocationProvenance Location,
    CenteredSceneResources SceneResources,
    string? LinearNodeId,
    string? LinearVariant,
    CenteredCalibrationBinding? Calibration,
    string? DeviceIdentity = null,
    CenteredScheduleBinding? Schedule = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AdmissionStateVersion { get; init; }

    internal byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
}

internal sealed record CenteredCalibrationBinding(string BundleId, string BundleIdentitySha256, long StateVersion);

internal sealed record CenteredScheduleBinding(
    string Revision, string LocalProfile, string Profile, string Interval, string? Override, CaptureCadenceMode Cadence)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Scheduling.CaptureScheduleOpenSegment? OpenSegment { get; init; }
}

internal sealed record CenteredOwnerTransition(
    string AgentId, string? CameraId, Guid AttachmentId, bool RequiresLegacyQuiescence = false);

internal sealed record CenteredPendingAttempt(
    ExposureAttemptKey Key, CenteredRunSnapshot Run, string RunHash, Guid? SourceCaptureId);

internal sealed record CenteredRawEvidence(
    ArtifactManifestV2 Manifest, string ManifestSha256, CenteredRunSnapshot Run, bool IsCommitted);

internal sealed record CenteredJournalRuntime(
    CenteredRunSnapshot Run, bool IsOpen, CenteredAcquisitionHead? Head, CenteredRuntimeHolds Holds);

internal sealed partial class SqliteRawCaptureJournal
{
    private const int MaximumOpenCenteredRuns = 32;

    internal const string CenteredSchemaSql = """
        CREATE TABLE centered_acquisition_owners (
            agent_id TEXT PRIMARY KEY,
            camera_id TEXT,
            attachment_id TEXT NOT NULL,
            profile_identity TEXT,
            device_identity TEXT,
            active_flag INTEGER NOT NULL CHECK (active_flag IN (0, 1))
        );
        CREATE TABLE centered_exposure_runs (
            run_id TEXT PRIMARY KEY,
            agent_id TEXT NOT NULL,
            camera_id TEXT NOT NULL,
            snapshot_json BLOB NOT NULL,
            snapshot_sha256 TEXT NOT NULL,
            calibration_bundle_id TEXT,
            device_identity TEXT,
            linear_node_id TEXT,
            linear_variant TEXT,
            closed_reason TEXT
        );
        CREATE UNIQUE INDEX ux_centered_open_run ON centered_exposure_runs(agent_id, camera_id)
            WHERE closed_reason IS NULL;
        CREATE TABLE centered_exposure_attempts (
            attempt_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES centered_exposure_runs(run_id),
            ordinal INTEGER NOT NULL CHECK (ordinal > 0),
            source_capture_id TEXT,
            capture_id TEXT UNIQUE,
            status TEXT NOT NULL CHECK (status IN ('pending','dispatched','observed','expired')),
            requested_json BLOB,
            metering_json BLOB,
            reason TEXT,
            UNIQUE (run_id, ordinal)
        );
        CREATE INDEX ix_centered_pending_source ON centered_exposure_attempts(source_capture_id)
            WHERE status = 'pending';
        CREATE TABLE centered_exposure_products (
            revision INTEGER PRIMARY KEY AUTOINCREMENT,
            agent_id TEXT NOT NULL,
            camera_id TEXT NOT NULL,
            capture_id TEXT NOT NULL,
            execution_id TEXT NOT NULL,
            producer TEXT NOT NULL,
            schema_version TEXT NOT NULL,
            variant TEXT NOT NULL,
            output_identity TEXT,
            artifact_id TEXT,
            payload_sha256 TEXT,
            content_identity_sha256 TEXT,
            disposition TEXT NOT NULL,
            reason TEXT,
            provenance_json BLOB,
            UNIQUE (capture_id, execution_id, producer, schema_version, variant)
        );
        CREATE INDEX ix_centered_product_capture ON centered_exposure_products(capture_id, revision);
        CREATE INDEX ix_centered_product_camera ON centered_exposure_products(agent_id, camera_id, revision);
        CREATE TABLE centered_exposure_holds (
            owner_id TEXT PRIMARY KEY,
            capture_id TEXT NOT NULL,
            output_identity TEXT NOT NULL,
            bytes INTEGER NOT NULL CHECK (bytes > 0),
            expires_unix_ms INTEGER NOT NULL
        );
        CREATE INDEX ix_centered_hold_capture ON centered_exposure_holds(capture_id, expires_unix_ms);
        """;

    internal async Task ExpireCenteredRunsAsync(
        string agentId, string cameraId, string reason, bool uncertainOnly, CancellationToken cancellationToken)
    {
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        await ExpireCenteredRunsAsync(connection, transaction, agentId, cameraId, reason, uncertainOnly, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await NotifyCenteredExpiryCommittedAsync(connection, agentId).ConfigureAwait(false);
    }

    private async Task ExpireCenteredRunsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string agentId, string cameraId,
        string reason, bool uncertainOnly, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE centered_exposure_runs SET closed_reason = $reason
            WHERE closed_reason IS NULL AND agent_id = $agent AND camera_id = $camera
                AND ($uncertain_only = 0 OR EXISTS (
                    SELECT 1 FROM centered_exposure_attempts
                    WHERE run_id = centered_exposure_runs.run_id AND status = 'dispatched'));
            UPDATE centered_exposure_attempts SET status = 'expired', reason = $reason
            WHERE status IN ('pending', 'dispatched') AND run_id IN (
                SELECT run_id FROM centered_exposure_runs WHERE closed_reason IS NOT NULL
                AND agent_id = $agent AND camera_id = $camera);
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$camera", cameraId);
        command.Parameters.AddWithValue("$uncertain_only", uncertainOnly ? 1 : 0);
        command.Parameters.AddWithValue("$reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (_centeredWindowWakeup is not null)
            await ExpireCenteredCalendarAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExpireCenteredCalendarAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'processing_centered_attempt_calendar';
            """;
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 1L)
            return;
        command.CommandText = """
            UPDATE processing_centered_attempt_calendar SET expired_ticks = $unobserved, expired_reason = (
                SELECT closed_reason FROM centered_exposure_runs run
                WHERE run.run_id = processing_centered_attempt_calendar.run_id)
            WHERE expired_ticks IS NULL AND run_id IN (
                SELECT run_id FROM centered_exposure_runs WHERE closed_reason IS NOT NULL
                AND agent_id = $agent AND ($camera IS NULL OR camera_id = $camera));
            """;
        command.Parameters.AddWithValue("$unobserved", CenteredWindowRuntimeJournal.UnobservedCalendarTicks);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task StartCenteredRunAsync(
        CenteredRunSnapshot snapshot,
        ExposureAttemptKey initial,
        CancellationToken cancellationToken)
    {
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO centered_exposure_runs(run_id, agent_id, camera_id, snapshot_json, snapshot_sha256,
                calibration_bundle_id, device_identity, linear_node_id, linear_variant)
            SELECT $run, $agent, $camera, $json, $hash, $calibration, $device, $linear_node, $linear_variant
            WHERE (SELECT COUNT(*) FROM centered_exposure_runs WHERE closed_reason IS NULL) < $limit;
            """;
        var json = snapshot.Serialize();
        command.Parameters.AddWithValue("$run", snapshot.RunId.ToString("N"));
        command.Parameters.AddWithValue("$agent", snapshot.Camera.AgentId);
        command.Parameters.AddWithValue("$camera", snapshot.Camera.CameraId);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$hash", PayloadChecksum.ComputeSha256(json));
        command.Parameters.AddWithValue("$limit", MaximumOpenCenteredRuns);
        command.Parameters.AddWithValue("$calibration", (object?)snapshot.Calibration?.BundleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$device", (object?)snapshot.DeviceIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$linear_node", (object?)snapshot.LinearNodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$linear_variant", (object?)snapshot.LinearVariant ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The centered exposure run limit was reached.");
        await InsertCenteredAttemptAsync(connection, transaction, initial, null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task ExpireCenteredPendingAsync(ExposureAttemptKey attempt, CancellationToken cancellationToken)
        => ExpireCenteredTargetAsync(attempt, "pending", "metering.source-evidence-unavailable", cancellationToken);

    internal Task RefuseCenteredStartAsync(ExposureAttemptKey attempt, CancellationToken cancellationToken)
        => ExpireCenteredTargetAsync(attempt, "dispatched", "schedule.start-window-closed", cancellationToken);

    private async Task ExpireCenteredTargetAsync(
        ExposureAttemptKey attempt, string status, string reason, CancellationToken cancellationToken)
    {
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE centered_exposure_attempts
            SET status = 'expired', reason = $reason
            WHERE attempt_id = $attempt AND run_id = $run AND ordinal = $ordinal AND status = $status
              AND EXISTS (
                SELECT 1 FROM centered_exposure_runs WHERE run_id = $run AND agent_id = $agent
                    AND camera_id = $camera AND closed_reason IS NULL);
            """;
        AddAttemptParameters(command, attempt);
        command.Parameters.AddWithValue("$agent", attempt.Camera.AgentId);
        command.Parameters.AddWithValue("$camera", attempt.Camera.CameraId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$reason", reason);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new RawIngressConflictException("The centered attempt could not be retired before opening a fresh target.");
        command.CommandText = "UPDATE centered_exposure_runs SET closed_reason = $reason WHERE run_id = $run;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CenteredPendingAttempt?> ReadCenteredPendingAsync(CameraKey camera, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run.run_id, run.snapshot_sha256, attempt.attempt_id, attempt.ordinal, attempt.source_capture_id
            FROM centered_exposure_runs run
            JOIN centered_exposure_attempts attempt ON attempt.run_id = run.run_id AND attempt.status = 'pending'
            WHERE run.agent_id = $agent AND run.camera_id = $camera AND run.closed_reason IS NULL;
            """;
        command.Parameters.AddWithValue("$agent", camera.AgentId);
        command.Parameters.AddWithValue("$camera", camera.CameraId);
        ExposureAttemptKey key;
        string hash;
        Guid? sourceCaptureId;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            key = new(camera, Guid.ParseExact(reader.GetString(0), "N"), reader.GetInt64(3),
                Guid.ParseExact(reader.GetString(2), "N"));
            hash = reader.GetString(1);
            sourceCaptureId = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : Guid.ParseExact(reader.GetString(4), "N");
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The camera has more than one pending centered attempt.");
        }

        var run = await ReadCenteredRunAsync(connection, null, key.RunId, hash, cancellationToken).ConfigureAwait(false);
        return new(key, run, hash, sourceCaptureId);
    }

    internal async Task DispatchCenteredAttemptAsync(
        ExposureAttemptKey attempt,
        CaptureSetpoint settings,
        CaptureMeteringEvidence? evidence,
        string reason,
        CenteredOwnerTransition? owner,
        CancellationToken cancellationToken,
        DateTimeOffset? requestedStart = null,
        TimeSpan requestedInterval = default)
    {
        if (owner is not null && (owner.AgentId != attempt.Camera.AgentId || owner.CameraId != attempt.Camera.CameraId))
            throw new RawIngressConflictException("The dispatch belongs to a different camera attachment.");

        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE centered_exposure_attempts SET status = 'dispatched', requested_json = $requested,
                metering_json = $metering, reason = $reason
            WHERE attempt_id = $attempt AND ordinal = $ordinal AND status = 'pending'
              AND run_id = $run AND EXISTS (
                SELECT 1 FROM centered_exposure_runs WHERE run_id = $run AND agent_id = $agent
                    AND camera_id = $camera AND closed_reason IS NULL
                    AND ($attachment IS NULL OR EXISTS (
                        SELECT 1 FROM centered_acquisition_owners owner
                        WHERE owner.agent_id = $agent AND owner.camera_id = $camera AND owner.active_flag = 1
                          AND owner.attachment_id = $attachment
                          AND owner.device_identity = centered_exposure_runs.device_identity)));
            """;
        AddAttemptParameters(command, attempt);
        command.Parameters.AddWithValue("$agent", attempt.Camera.AgentId);
        command.Parameters.AddWithValue("$camera", attempt.Camera.CameraId);
        command.Parameters.AddWithValue("$attachment", (object?)owner?.AttachmentId.ToString("N") ?? DBNull.Value);
        command.Parameters.AddWithValue("$requested", JsonSerializer.SerializeToUtf8Bytes(settings));
        command.Parameters.AddWithValue("$metering", evidence is null ? DBNull.Value : JsonSerializer.SerializeToUtf8Bytes(evidence));
        command.Parameters.AddWithValue("$reason", reason);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The centered exposure attempt is no longer dispatchable.");
        if (requestedStart is { } start && _centeredWindowWakeup is not null)
        {
            await CenteredWindowRuntimeSchedule.TrackDispatchedAsync(transaction, attempt, start, requestedInterval,
                settings.Exposure, _utcNow(), cancellationToken).ConfigureAwait(false);
            _faultInjector.BeforeCenteredDispatchCommit();
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (requestedStart is not null && _centeredWindowWakeup is not null)
        {
            try
            {
                await CenteredWindowRuntimeJournal.ObserveCommittedDispatchAsync(connection, attempt, _utcNow()).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Dispatch is durable. The scheduler will observe it from a later committed snapshot.
            }
            _centeredWindowWakeup.NotifyCommitted();
        }
    }

    private async Task NotifyCenteredExpiryCommittedAsync(SqliteConnection connection, string agentId)
    {
        if (_centeredWindowWakeup is null) return;
        try
        {
            using var transaction = BeginImmediate(connection);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE processing_centered_attempt_calendar SET expired_ticks = $observed
                WHERE expired_ticks = $unobserved AND run_id IN (
                    SELECT run_id FROM centered_exposure_runs WHERE agent_id = $agent AND closed_reason IS NOT NULL);
                """;
            command.Parameters.AddWithValue("$observed", _utcNow().UtcTicks);
            command.Parameters.AddWithValue("$unobserved", CenteredWindowRuntimeJournal.UnobservedCalendarTicks);
            command.Parameters.AddWithValue("$agent", agentId);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The fence is durable. A missed observation does not rerun the fence.
        }
        _centeredWindowWakeup.NotifyCommitted();
    }

    internal async Task<CaptureSetpoint> ReadCenteredRequestAsync(
        ExposureAttemptKey attempt, Guid captureId, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCenteredRequestAsync(connection, null, attempt, captureId, allowUnbound: true, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<CaptureSetpoint> ReadCenteredRequestAsync(
        SqliteConnection connection, SqliteTransaction? transaction, ExposureAttemptKey attempt, Guid captureId,
        bool allowUnbound, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT attempt.requested_json
            FROM centered_exposure_attempts attempt
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            WHERE attempt.attempt_id = $attempt AND attempt.run_id = $run AND attempt.ordinal = $ordinal
              AND run.agent_id = $agent AND run.camera_id = $camera
              AND ((attempt.status = 'observed' AND attempt.capture_id = $capture)
                   OR ($allow_unbound = 1 AND attempt.status IN ('dispatched', 'expired') AND attempt.capture_id IS NULL
                       AND attempt.reason IS NOT 'schedule.start-window-closed'));
            """;
        AddAttemptParameters(command, attempt);
        command.Parameters.AddWithValue("$agent", attempt.Camera.AgentId);
        command.Parameters.AddWithValue("$camera", attempt.Camera.CameraId);
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$allow_unbound", allowUnbound ? 1 : 0);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        try
        {
            var request = json is null ? null : JsonSerializer.Deserialize<CaptureSetpoint>(json);
            if (request is null)
                throw new RawIngressConflictException("The centered raw capture has no retained dispatched settings request.");
            if (!request.IsValid())
                throw new RawIngressConflictException("The centered dispatched settings request is invalid.");
            return request;
        }
        catch (JsonException exception)
        {
            throw new RawIngressConflictException("The centered dispatched settings request is malformed.", exception);
        }
    }

    private static async Task VerifyCenteredRequestAsync(
        SqliteConnection connection, SqliteTransaction transaction, RawIngressJournalEntry entry, CaptureSetpoint? supplied,
        bool allowUnbound, CancellationToken cancellationToken)
    {
        if (entry.CenteredExposure is not { } binding)
            return;
        var request = await ReadCenteredRequestAsync(connection, transaction, binding.Source, entry.CaptureId,
            allowUnbound, cancellationToken).ConfigureAwait(false);
        var descriptor = ParseCenteredManifest(entry.ManifestJson).Descriptor;
        var recorded = descriptor.CenteredAcquisition?.Module?.RequestedSetpoint;
        if (request != supplied || request.Exposure != descriptor.Controls.RequestedExposure ||
            request.Gain != descriptor.Controls.RequestedGain || recorded is not null && recorded != request)
            throw new RawIngressConflictException("The centered raw capture differs from its dispatched settings request.");
    }

    private async Task BindCenteredAdmissionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RawIngressJournalEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.CenteredExposure is not { } binding) return;
        var manifest = ParseCenteredManifest(entry.ManifestJson);
        var frozen = await ReadCenteredRunAsync(connection, transaction, binding.Source.RunId, binding.FrozenRunSha256,
            cancellationToken).ConfigureAwait(false);
        if (frozen.Camera != binding.Source.Camera || frozen.GraphRevisionId != manifest.CaptureGraphRevisionId ||
            frozen.Location != manifest.Descriptor.Location)
            throw new RawIngressConflictException("The centered raw evidence differs from its frozen capture run.");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        AddAttemptParameters(command, binding.Source);
        command.CommandText = """
            UPDATE centered_exposure_attempts SET status = 'observed', capture_id = $capture
            WHERE attempt_id = $attempt AND run_id = $run AND ordinal = $ordinal
                AND status IN ('dispatched', 'expired') AND requested_json IS NOT NULL AND capture_id IS NULL
                AND reason IS NOT 'schedule.start-window-closed';
            """;
        command.Parameters.AddWithValue("$capture", entry.CaptureId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new RawIngressConflictException("The centered raw evidence has no matching dispatched attempt.");
        await InsertCenteredAttemptAsync(connection, transaction, binding.Target, entry.CaptureId, cancellationToken)
            .ConfigureAwait(false);
        if (_centeredWindowWakeup is not null)
            await CenteredWindowRuntimeJournal.MarkAdmittedAsync(transaction, binding.Source, _utcNow(), cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task NotifyCenteredAdmissionCommittedAsync(SqliteConnection connection, RawIngressJournalEntry entry)
    {
        if (_centeredWindowWakeup is null || entry.CenteredExposure is not { } binding)
            return;

        try
        {
            await CenteredWindowRuntimeJournal.ObserveCommittedAdmissionAsync(connection, binding.Source, _utcNow())
                .ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The committed raw admission keeps its outcome. The coordinator will observe it on a later snapshot.
        }
        _centeredWindowWakeup.NotifyCommitted();
    }

    private static async Task InsertCenteredAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExposureAttemptKey attempt,
        Guid? sourceCapture,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO centered_exposure_attempts(attempt_id, run_id, ordinal, source_capture_id, status, reason)
            SELECT $attempt, $run, $ordinal, $source,
                CASE WHEN closed_reason IS NULL THEN 'pending' ELSE 'expired' END, closed_reason
            FROM centered_exposure_runs WHERE run_id = $run;
            """;
        AddAttemptParameters(command, attempt);
        command.Parameters.AddWithValue("$source", (object?)sourceCapture?.ToString("N") ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new RawIngressConflictException("The centered exposure target has no owning run.");
    }

    private static void AddAttemptParameters(SqliteCommand command, ExposureAttemptKey attempt)
    {
        command.Parameters.AddWithValue("$run", attempt.RunId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
        command.Parameters.AddWithValue("$ordinal", attempt.Ordinal);
    }

    internal async Task<CenteredRawEvidence?> ReadCenteredRawAsync(ExposureKey key, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw.manifest_json, raw.manifest_sha256, raw.state = 'committed'
            FROM centered_exposure_attempts attempt
            JOIN raw_captures raw ON raw.capture_id = attempt.capture_id
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            WHERE raw.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera;
            """;
        AddExposureParameters(command, key);
        ArtifactManifestV2 manifest;
        string manifestHash;
        bool isCommitted;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            var json = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
            manifestHash = reader.GetString(1);
            isCommitted = reader.GetBoolean(2);
            if (PayloadChecksum.ComputeSha256(json) != manifestHash)
                throw new InvalidDataException("The centered raw manifest differs from its journal identity.");
            manifest = ParseCenteredManifest(json);
        }

        if (manifest.CenteredExposure is not { } binding || binding.Source.Camera != key.Camera ||
            binding.Source.RunId != key.RunId || manifest.Descriptor.Capture.CaptureId != key.CaptureId)
            throw new InvalidDataException("The centered raw manifest differs from its exposure key.");
        var run = await ReadCenteredRunAsync(connection, null, key.RunId, binding.FrozenRunSha256, cancellationToken)
            .ConfigureAwait(false);
        return new(manifest, manifestHash, run, isCommitted);
    }

    internal async Task<CenteredJournalRuntime?> ReadCenteredRuntimeAsync(
        CameraKey camera, Guid? runId, DateTimeOffset observedUtc, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite only exposes deferred transactions through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: true);
#pragma warning restore CA1849
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, snapshot_sha256, closed_reason IS NULL
            FROM centered_exposure_runs
            WHERE agent_id = $agent AND camera_id = $camera AND ($run IS NULL OR run_id = $run)
            ORDER BY rowid DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$agent", camera.AgentId);
        command.Parameters.AddWithValue("$camera", camera.CameraId);
        command.Parameters.AddWithValue("$run", runId is { } requested ? requested.ToString("N") : DBNull.Value);
        Guid selectedRunId;
        string snapshotHash;
        bool isOpen;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            selectedRunId = Guid.ParseExact(reader.GetString(0), "N");
            snapshotHash = reader.GetString(1);
            isOpen = reader.GetBoolean(2);
        }

        var run = await ReadCenteredRunAsync(connection, transaction, selectedRunId, snapshotHash, cancellationToken)
            .ConfigureAwait(false);
        if (run.Camera != camera)
            throw new InvalidDataException("The centered run snapshot differs from the requested camera.");

        command.Parameters["$run"].Value = selectedRunId.ToString("N");
        command.CommandText = """
            SELECT raw.capture_id, attempt.ordinal, raw.exposure_started_unix_ms, raw.committed_unix_ms
            FROM centered_exposure_attempts attempt
            JOIN raw_captures raw ON raw.capture_id = attempt.capture_id
            WHERE attempt.run_id = $run AND attempt.status = 'observed'
            ORDER BY attempt.ordinal DESC LIMIT 1;
            """;
        CenteredAcquisitionHead? head = null;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                head = new(new(camera, selectedRunId, Guid.ParseExact(reader.GetString(0), "N")), reader.GetInt64(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
        }

        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM centered_exposure_attempts
                    WHERE run_id = $run AND status = 'pending' AND source_capture_id IS NOT NULL),
                   COUNT(*), COALESCE(SUM(hold.bytes), 0)
            FROM centered_exposure_holds hold
            JOIN centered_exposure_attempts attempt ON attempt.capture_id = hold.capture_id
            WHERE attempt.run_id = $run AND hold.expires_unix_ms > $observed;
            """;
        command.Parameters.AddWithValue("$observed", observedUtc.ToUnixTimeMilliseconds());
        using var holdsReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await holdsReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new(run, isOpen, head, new(holdsReader.GetInt32(0), holdsReader.GetInt32(1), holdsReader.GetInt64(2)));
    }

    internal async Task<CenteredRunSnapshot> ReadCenteredRunAsync(
        CenteredExposureBindingV1 binding, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCenteredRunAsync(connection, null, binding.Source.RunId, binding.FrozenRunSha256, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<CenteredRunSnapshot> ReadCenteredRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid runId,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT snapshot_json, snapshot_sha256 FROM centered_exposure_runs WHERE run_id = $run;";
        command.Parameters.AddWithValue("$run", runId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RawIngressConflictException("The centered raw evidence has no retained run snapshot.");
        var json = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
        if (expectedHash != reader.GetString(1) || PayloadChecksum.ComputeSha256(json) != expectedHash)
            throw new RawIngressConflictException("The centered run snapshot differs from the raw binding.");
        var snapshot = JsonSerializer.Deserialize<CenteredRunSnapshot>(json);
        if (snapshot is null || snapshot.RunId != runId)
            throw new InvalidDataException("The centered exposure run snapshot is invalid.");
        return snapshot;
    }

    internal static void AddExposureParameters(SqliteCommand command, ExposureKey key)
    {
        command.Parameters.AddWithValue("$capture", key.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$run", key.RunId.ToString("N"));
        command.Parameters.AddWithValue("$agent", key.Camera.AgentId);
        command.Parameters.AddWithValue("$camera", key.Camera.CameraId);
    }

    private static ArtifactManifestV2 ParseCenteredManifest(byte[] json) =>
        CaptureContractJson.ParseManifest(json).Document?.Manifest ??
        throw new InvalidDataException("The retained raw capture manifest is invalid.");
}
