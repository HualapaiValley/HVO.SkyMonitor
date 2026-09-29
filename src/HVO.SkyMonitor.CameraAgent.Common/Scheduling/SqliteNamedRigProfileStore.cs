using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Scheduling;

public sealed record NamedRigSelection(string? ActiveRevisionId, string? PendingRevisionId, long Version,
    string? PendingCommandKey = null, string? PendingScheduleRevisionId = null, long? PendingScheduleVersion = null);

public sealed record NamedRigRevision(
    string RevisionId, string ProfileId, long RevisionNumber, string CameraRevisionId,
    string OpticsRevisionId, string MountRevisionId, CameraModuleDescriptor Module,
    CameraRigConfig Rig, string? SourceScheduleRevisionId);

public sealed record NamedRigCatalog(
    NamedRigSelection Selection, IReadOnlyList<NamedRigRevision> Revisions);

public sealed record NamedRigProfile(string ProfileId, string DisplayName);

public sealed record NamedEquipmentDefinition(string DefinitionId, string Kind, string DisplayName,
    long RevisionNumber, string RevisionId);

public sealed record NamedEquipmentDetail(string DefinitionId, string Kind, string DisplayName,
    long RevisionNumber, string RevisionId, object Definition);

public sealed record NamedCameraEquipment(CameraModuleDescriptor Module, SensorProfile Sensor, SensorReadoutProfile? Readout);

public sealed record NamedRigPreview(string ScheduleRevisionId, string ScheduleProfileSha256,
    string RigRevisionId, bool Valid, string? Failure, bool RuntimeVerified);

public sealed record NamedRigInventory(IReadOnlyList<NamedRigProfile> Profiles,
    IReadOnlyList<NamedEquipmentDefinition> Equipment);

public sealed record NamedRigStageReceipt(
    string ReceiptId, string RevisionId, long Version, string Disposition, bool AcknowledgedUnvalidated,
    string? ScheduleRevisionId = null);

/// <summary>Durable named equipment catalog with schedule-bound selection.</summary>
public sealed class SqliteNamedRigProfileStore(
    IRawCaptureIngress ingress, IOptions<CameraAgentHostOptions> options, TimeProvider timeProvider,
    SqliteCaptureScheduleStore? scheduleStore = null,
    CaptureScheduleRuntimeCoordinator? scheduleRuntime = null)
{
    private static readonly JsonSerializerOptions EquipmentJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ScheduleJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(Path.GetFullPath(options.Value.RawIngressRoot), "journal", "raw-ingress.db"),
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
        DefaultTimeout = options.Value.RawIngressSqliteBusyTimeoutSeconds
    }.ToString();
    private string? _pendingRuntimeFailure;
    private string? _activeRuntimeFailure;
    private NamedRigSelection? _startupPendingSelection;

    public string? PendingRuntimeFailure => Volatile.Read(ref _pendingRuntimeFailure);

    public string? ActiveRuntimeFailure => Volatile.Read(ref _activeRuntimeFailure);

    public NamedRigSelection? StartupPendingSelection => Volatile.Read(ref _startupPendingSelection);

    public void SnapshotStartupPendingSelection(NamedRigSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Volatile.Write(ref _startupPendingSelection, selection.PendingRevisionId is null ? null : selection);
    }

    public void ReportActiveRuntimeFailure(string? reason) =>
        Volatile.Write(ref _activeRuntimeFailure, reason);

    public void ReportPendingRuntimeFailure(string? reason) =>
        Volatile.Write(ref _pendingRuntimeFailure, reason);

    public async Task<NamedRigCatalog> GetAsync(CancellationToken cancellationToken)
    {
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = await ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CaptureScheduleRevisionSnapshot?> ReconcileAtStartupAsync(
        CameraModuleConfig fileConfiguration, ICameraModuleFactory moduleFactory, CancellationToken cancellationToken,
        Action<CameraModuleConfig>? validateCandidate = null, bool deferModuleInitialization = false,
        NamedRigSelection? expectedSelection = null)
    {
        ArgumentNullException.ThrowIfNull(moduleFactory);
        var catalog = await GetAsync(cancellationToken).ConfigureAwait(false);
        if (expectedSelection is not null &&
            (catalog.Selection.PendingRevisionId != expectedSelection.PendingRevisionId ||
             catalog.Selection.PendingCommandKey != expectedSelection.PendingCommandKey ||
             catalog.Selection.PendingScheduleRevisionId != expectedSelection.PendingScheduleRevisionId))
            throw new CaptureScheduleStoreConflictException("The startup named rig command changed before reconciliation.");
        if (catalog.Selection.PendingRevisionId is not { } pendingId) return null;
        if (scheduleStore is null) throw new InvalidOperationException("Schedule authority is unavailable.");
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var revision = await ReadRevisionAsync(connection, null, pendingId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Pending named rig revision is missing.");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_json FROM named_rig_selection_commands WHERE idempotency_key = $key AND command_kind = 'stage';";
        command.Parameters.AddWithValue("$key", catalog.Selection.PendingCommandKey!);
        var bytes = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]
            ?? throw new InvalidDataException("Pending named rig has no stage command.");
        var receipt = JsonSerializer.Deserialize<NamedRigStageReceipt>(bytes)
            ?? throw new InvalidDataException("Pending named rig receipt is invalid.");
        if (receipt.RevisionId != pendingId || receipt.ScheduleRevisionId != catalog.Selection.PendingScheduleRevisionId)
            throw new InvalidDataException("Pending named rig stage identity differs from selection.");
        var schedule = await scheduleStore.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (schedule.PendingRevision?.RevisionId != receipt.ScheduleRevisionId ||
            schedule.Version != catalog.Selection.PendingScheduleVersion)
            throw new CaptureScheduleStoreConflictException("Pending named rig schedule changed before reconciliation.");
        var effective = await scheduleStore.GetRevisionAsync(receipt.ScheduleRevisionId!, cancellationToken).ConfigureAwait(false);
        if (!SqliteCaptureScheduleStore.SameEquipment(effective.Profile, revision.Module, revision.Rig))
            throw new InvalidDataException("Pending named rig and effective profile differ.");
        var candidate = effective.Profile.ApplyTo(fileConfiguration);
        validateCandidate?.Invoke(candidate);
        if (!deferModuleInitialization)
        {
            var module = moduleFactory.Create(candidate.ModuleType);
            try
            {
                await module.InitializeAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
        }
        return effective;
    }

    public async Task CommitInitializedAsync(string pendingId, CameraModuleConfig initializedConfiguration, CancellationToken cancellationToken)
    {
        var catalog = await GetAsync(cancellationToken).ConfigureAwait(false);
        if (catalog.Selection.PendingRevisionId != pendingId || scheduleStore is null)
            throw new CaptureScheduleStoreConflictException("The pending named rig changed before capture initialization.");
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_json FROM named_rig_selection_commands WHERE idempotency_key = $key AND command_kind = 'stage';";
        command.Parameters.AddWithValue("$key", catalog.Selection.PendingCommandKey!);
        var bytes = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]
            ?? throw new InvalidDataException("Pending named rig has no stage command.");
        var receipt = JsonSerializer.Deserialize<NamedRigStageReceipt>(bytes)
            ?? throw new InvalidDataException("Pending named rig receipt is invalid.");
        var scheduleRevisionId = receipt.ScheduleRevisionId
            ?? throw new InvalidDataException("Pending named rig has no effective profile.");
        if (receipt.RevisionId != pendingId || scheduleRevisionId != catalog.Selection.PendingScheduleRevisionId)
            throw new CaptureScheduleStoreConflictException("Pending named rig stage identity changed.");
        var revision = await ReadRevisionAsync(connection, null, pendingId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Pending named rig revision is missing.");
        var effective = await scheduleStore.GetRevisionAsync(scheduleRevisionId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(effective.ProfileSha256,
            LocalCaptureProfileContract.ComputeEffectiveSha256(initializedConfiguration), StringComparison.OrdinalIgnoreCase))
            throw new CaptureScheduleStoreConflictException("Initialized camera configuration differs from the pending profile.");
        if (!SqliteCaptureScheduleStore.SameEquipment(effective.Profile, revision.Module, revision.Rig))
            throw new InvalidDataException("Pending named rig and effective profile differ.");
        var schedule = await scheduleStore.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (schedule.PendingRevision?.RevisionId != effective.RevisionId ||
            schedule.Version != catalog.Selection.PendingScheduleVersion)
            throw new CaptureScheduleStoreConflictException("The staged effective profile is no longer pending.");
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var changed = await ExecuteAsync(connection, transaction, """
            UPDATE named_rig_selection SET active_revision_id = $id, pending_revision_id = NULL,
                pending_command_key = NULL, pending_schedule_revision_id = NULL, pending_schedule_version = NULL,
                version = version + 1, updated_unix_ms = $now
            WHERE state_key = 1 AND pending_revision_id = $id AND pending_command_key = $key
                AND pending_schedule_revision_id = $schedule AND pending_schedule_version = $scheduleVersion AND version = $version;
            """, cancellationToken, ("$id", pendingId), ("$key", catalog.Selection.PendingCommandKey!),
            ("$schedule", scheduleRevisionId), ("$scheduleVersion", schedule.Version),
            ("$now", now), ("$version", catalog.Selection.Version)).ConfigureAwait(false);
        if (changed != 1) throw new CaptureScheduleStoreConflictException("Named rig selection changed during reconciliation.");
        // Keep operator IDs stable; each transfer gets its own event identity even on a return trip.
        await ExecuteAsync(connection, transaction, """
            INSERT INTO capture_schedule_override_events(event_id, override_id, event_kind, actor, reason, occurred_unix_ms)
            SELECT 'rig-override:' || lower(hex(randomblob(16))), override_id, 'created', 'system',
                'transferred from revision ' || schedule_revision_id || ' to ' || $to, $now
            FROM capture_schedule_overrides
            WHERE schedule_revision_id = $from AND cleared_unix_ms IS NULL AND end_unix_ms > $now;
            UPDATE capture_schedule_overrides SET schedule_revision_id = $to
            WHERE schedule_revision_id = $from AND cleared_unix_ms IS NULL AND end_unix_ms > $now;
            """, cancellationToken, ("$from", schedule.ActiveRevision.RevisionId),
            ("$to", effective.RevisionId), ("$now", now)).ConfigureAwait(false);
        changed = await ExecuteAsync(connection, transaction, """
            UPDATE capture_schedule_state SET active_revision_id = $id, pending_revision_id = NULL,
                version = version + 1, updated_unix_ms = $now
            WHERE state_key = 1 AND pending_revision_id = $id AND version = $version;
            """, cancellationToken, ("$id", effective.RevisionId), ("$now", now), ("$version", schedule.Version)).ConfigureAwait(false);
        if (changed != 1) throw new CaptureScheduleStoreConflictException("Schedule changed during reconciliation.");
        await ExecuteAsync(connection, transaction, """
            INSERT INTO capture_schedule_activations(idempotency_key, from_revision_id, to_revision_id,
                actor, reason, state_version, activated_unix_ms)
            VALUES ($key, $from, $to, 'system', 'named rig startup reconciliation', $version, $now);
            """, cancellationToken, ("$key", $"named-rig-reconcile:{receipt.ReceiptId}"),
            ("$from", schedule.ActiveRevision.RevisionId), ("$to", effective.RevisionId),
            ("$version", schedule.Version + 1), ("$now", now)).ConfigureAwait(false);
        var activationKey = $"named-rig-activate:{receipt.ReceiptId}";
        var activationReceipt = new NamedRigStageReceipt(Guid.NewGuid().ToString("N"), pendingId,
            catalog.Selection.Version + 1, "active", receipt.AcknowledgedUnvalidated, scheduleRevisionId);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO named_rig_selection_commands VALUES ($key, 'activate', $version, $target, $actor, $hash, $ack, $result, $now, $now);
            INSERT INTO named_rig_selection_receipts VALUES ($receipt, $key, $from, $target, 'active', $next, $now, $stage);
            """, cancellationToken, ("$key", activationKey), ("$version", catalog.Selection.Version),
            ("$target", pendingId), ("$actor", "system"), ("$hash", Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { pendingId, catalog.Selection.PendingCommandKey, scheduleRevisionId, schedule.Version })))),
            ("$ack", receipt.AcknowledgedUnvalidated ? 1 : 0),
            ("$result", JsonSerializer.SerializeToUtf8Bytes(activationReceipt)), ("$now", now),
            ("$receipt", activationReceipt.ReceiptId),
            ("$stage", catalog.Selection.PendingCommandKey!),
            ("$from", (object?)catalog.Selection.ActiveRevisionId ?? DBNull.Value),
            ("$next", activationReceipt.Version)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _pendingRuntimeFailure, null);
    }

    public async Task<NamedRigStageReceipt> CancelPendingAsync(string revisionId, long expectedVersion,
        string idempotencyKey, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revisionId) || revisionId.Length > 128 ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 128 || expectedVersion < 0)
            throw new ArgumentException("Invalid named rig cancellation command.");
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { revisionId, expectedVersion, actor })));
        using (var replay = connection.CreateCommand())
        {
            replay.Transaction = transaction;
            replay.CommandText = "SELECT command_kind, request_sha256, result_json FROM named_rig_selection_commands WHERE idempotency_key = $key;";
            replay.Parameters.AddWithValue("$key", idempotencyKey);
            using var reader = await replay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) != "cancel" || reader.GetString(1) != requestHash)
                    throw new CaptureScheduleStoreConflictException("The idempotency key has different content.");
                var receipt = JsonSerializer.Deserialize<NamedRigStageReceipt>(
                    await reader.GetFieldValueAsync<byte[]>(2, cancellationToken).ConfigureAwait(false))
                    ?? throw new InvalidDataException("The cancellation receipt is invalid.");
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return receipt;
            }
        }
        var selection = await ReadSelectionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (selection.Version != expectedVersion || selection.PendingRevisionId != revisionId)
            throw new CaptureScheduleStoreConflictException("The pending named rig selection has changed.");
        var scheduleId = selection.PendingScheduleRevisionId!;
        var stageKey = selection.PendingCommandKey!;
        var scheduleVersion = selection.PendingScheduleVersion!.Value;
        using (var stage = connection.CreateCommand())
        {
            stage.Transaction = transaction;
            stage.CommandText = "SELECT result_json FROM named_rig_selection_commands WHERE idempotency_key = $key AND command_kind = 'stage';";
            stage.Parameters.AddWithValue("$key", stageKey);
            var bytes = await stage.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]
                ?? throw new InvalidDataException("Pending named rig has no stage command.");
            var receipt = JsonSerializer.Deserialize<NamedRigStageReceipt>(bytes);
            if (receipt?.RevisionId != revisionId || receipt.ScheduleRevisionId != scheduleId)
                throw new InvalidDataException("Pending named rig stage identity differs from selection.");
        }
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var changed = await ExecuteAsync(connection, transaction, """
            UPDATE capture_schedule_state SET pending_revision_id = NULL, version = version + 1, updated_unix_ms = $now
            WHERE state_key = 1 AND pending_revision_id = $schedule AND version = $version;
            """, cancellationToken, ("$now", now), ("$schedule", scheduleId), ("$version", scheduleVersion)).ConfigureAwait(false);
        if (changed != 1) throw new CaptureScheduleStoreConflictException("The pending schedule profile has changed.");
        changed = await ExecuteAsync(connection, transaction, """
            UPDATE named_rig_selection SET pending_revision_id = NULL, pending_command_key = NULL,
                pending_schedule_revision_id = NULL, pending_schedule_version = NULL,
                version = version + 1, updated_unix_ms = $now
            WHERE state_key = 1 AND pending_revision_id = $id AND pending_command_key = $stage
                AND pending_schedule_revision_id = $schedule AND pending_schedule_version = $scheduleVersion AND version = $version;
            """, cancellationToken, ("$now", now), ("$id", revisionId), ("$stage", stageKey),
            ("$schedule", scheduleId), ("$scheduleVersion", scheduleVersion), ("$version", expectedVersion)).ConfigureAwait(false);
        if (changed != 1) throw new CaptureScheduleStoreConflictException("The pending named rig selection has changed.");
        var result = new NamedRigStageReceipt(Guid.NewGuid().ToString("N"), revisionId,
            expectedVersion + 1, "cancelled", true, scheduleId);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO named_rig_selection_commands VALUES ($key, 'cancel', $version, $id, $actor, $hash, 1, $result, $now, $now);
            INSERT INTO named_rig_selection_receipts VALUES ($receipt, $key, $from, $id, 'cancelled', $next, $now, $stage);
            """, cancellationToken, ("$key", idempotencyKey), ("$version", expectedVersion),
            ("$id", revisionId), ("$actor", actor), ("$hash", requestHash),
            ("$result", JsonSerializer.SerializeToUtf8Bytes(result)), ("$now", now),
            ("$receipt", result.ReceiptId), ("$from", (object?)selection.ActiveRevisionId ?? DBNull.Value),
            ("$stage", stageKey),
            ("$next", result.Version)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _pendingRuntimeFailure, null);
        return result;
    }

    public async Task<NamedRigCatalog> ImportActiveAsync(
        CaptureScheduleRevisionSnapshot active, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(active);
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (existing.Selection.PendingRevisionId is not null)
            throw new CaptureScheduleStoreConflictException("A named rig is awaiting restart.");
        if (existing.Selection.ActiveRevisionId is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        // Never trust the caller's object over the persisted schedule identity.
        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT profile_json, profile_sha256 FROM capture_schedule_revisions WHERE revision_id = $id AND revision_number = $number;";
            verify.Parameters.AddWithValue("$id", active.RevisionId);
            verify.Parameters.AddWithValue("$number", active.RevisionNumber);
            using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The installed schedule profile identity does not match its persisted content.");
            var bytes = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
            var persisted = JsonSerializer.Deserialize<LocalCaptureProfileDefinition>(bytes, ScheduleJson)
                ?? throw new InvalidDataException("The installed schedule profile is invalid.");
            var hash = reader.GetString(1);
            if (!string.Equals(LocalCaptureProfileContract.ComputePersistedRevisionSha256(persisted), hash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(LocalCaptureProfileContract.ComputeEffectiveSha256(persisted.NormalizePersistedRevisionForRead()),
                    active.ProfileSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(LocalCaptureProfileContract.ComputeEffectiveSha256(active.Profile), active.ProfileSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The installed schedule profile identity does not match its persisted content.");
            }
        }
        var module = active.Profile.Module;
        var rig = active.Profile.Rig;
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var profileId = Guid.NewGuid().ToString("N");
        var camera = await InsertEquipmentAsync(connection, transaction, "camera", rig.Sensor.Name,
            new NamedCameraEquipment(module, rig.Sensor, rig.Readout), now, cancellationToken).ConfigureAwait(false);
        var optics = await InsertEquipmentAsync(connection, transaction, "optics", "Installed optics",
            rig.Optics, now, cancellationToken).ConfigureAwait(false);
        var mount = await InsertEquipmentAsync(connection, transaction, "mount", "Installed mount",
            rig.Orientation, now, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "INSERT INTO named_rig_profiles VALUES ($id, 'Installed rig', $now, $now);",
            cancellationToken, ("$id", profileId), ("$now", now)).ConfigureAwait(false);
        var revisionId = Guid.NewGuid().ToString("N");
        var rigBytes = JsonSerializer.SerializeToUtf8Bytes(new InstalledRig(module, rig));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO named_rig_revisions VALUES ($id, $profile, 1, $camera, $optics, $mount, $json, $hash, $source, $now);
            """, cancellationToken, ("$id", revisionId), ("$profile", profileId),
            ("$camera", camera), ("$optics", optics), ("$mount", mount), ("$json", rigBytes),
            ("$hash", Convert.ToHexString(SHA256.HashData(rigBytes))),
            ("$source", active.RevisionId), ("$now", now)).ConfigureAwait(false);
        var changed = await ExecuteAsync(connection, transaction, """
            UPDATE named_rig_selection SET active_revision_id = $id, version = version + 1, updated_unix_ms = $now
            WHERE state_key = 1 AND active_revision_id IS NULL;
            """, cancellationToken, ("$id", revisionId), ("$now", now)).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException("The named rig bootstrap was changed concurrently.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<NamedRigStageReceipt> StageAsync(
        string revisionId, long expectedVersion, string idempotencyKey, string actor,
        bool acknowledgeUnvalidated, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revisionId) || revisionId.Length > 128 ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 128 || expectedVersion < 0)
        {
            throw new ArgumentException("Invalid named rig selection command.");
        }
        if (scheduleStore is null) throw new InvalidOperationException("Schedule authority is unavailable.");
        var schedule = await scheduleStore.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { revisionId, expectedVersion, actor, acknowledgeUnvalidated })));
        using (var replay = connection.CreateCommand())
        {
            replay.Transaction = transaction;
            replay.CommandText = "SELECT command_kind, request_sha256, result_json FROM named_rig_selection_commands WHERE idempotency_key = $key;";
            replay.Parameters.AddWithValue("$key", idempotencyKey);
            using var reader = await replay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) != "stage" ||
                    !string.Equals(requestHash, reader.GetString(1), StringComparison.Ordinal))
                {
                    throw new CaptureScheduleStoreConflictException("The idempotency key has different content.");
                }
                var replayReceipt = JsonSerializer.Deserialize<NamedRigStageReceipt>(
                    await reader.GetFieldValueAsync<byte[]>(2, cancellationToken).ConfigureAwait(false))
                    ?? throw new InvalidDataException("The selection receipt is invalid.");
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replayReceipt;
            }
        }
        var catalog = await ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (catalog.Selection.Version != expectedVersion)
        {
            throw new CaptureScheduleStoreConflictException("The named rig selection version has changed.");
        }
        var target = await ReadRevisionAsync(connection, transaction, revisionId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            throw new KeyNotFoundException("The named rig revision was not found.");
        }
        if (!acknowledgeUnvalidated) throw new ArgumentException("Runtime validation requires acknowledgement.");
        if (catalog.Selection.PendingRevisionId is not null)
            throw new CaptureScheduleStoreConflictException("A named rig is already awaiting restart.");
        if (schedule.PendingRevision is not null)
            throw new CaptureScheduleStoreConflictException("Resolve the pending schedule draft before staging a named rig.");
        var current = schedule.ActiveRevision.Profile;
        var profile = current with
        {
            Module = target.Module,
            Rig = current.Rig with
            {
                Sensor = target.Rig.Sensor,
                Readout = target.Rig.Readout,
                Optics = target.Rig.Optics,
                Orientation = target.Rig.Orientation
            }
        };
        var validation = LocalCaptureProfileContract.Validate(profile);
        if (!validation.IsValid) throw new CaptureProfileCompatibilityException($"Invalid effective profile: {validation.FieldPath}");
        if (target.Rig.Readout is not null) _ = SensorReadoutResolver.Resolve(target.Rig.Sensor, target.Rig.Readout);
        // The runtime preflight includes file-contract, registered-module and graph checks.
        // The active revision must still be the one used to build the candidate.
        if (scheduleRuntime is not null && scheduleRuntime.Snapshot?.Revision.RevisionId != schedule.ActiveRevision.RevisionId)
            throw new CaptureScheduleStoreConflictException("The active runtime profile is unavailable or changed during staging.");
        if (scheduleRuntime is not null) _ = scheduleRuntime.Preview(profile, 1);
        var hash = LocalCaptureProfileContract.ComputeSha256(profile);
        var number = await ScalarLongAsync(connection, transaction,
            "SELECT COALESCE(MAX(revision_number), 0) + 1 FROM capture_schedule_revisions;", cancellationToken).ConfigureAwait(false);
        var scheduleId = $"profile-{number:D8}-{hash[..12]}";
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO capture_schedule_revisions(revision_id, revision_number, profile_json, profile_sha256,
                schedule_sha256, source, actor, reason, created_unix_ms)
            VALUES ($id, $number, $json, $hash, $schedule, 'named-rig-stage', $actor, 'restart required', $now);
            """, cancellationToken, ("$id", scheduleId), ("$number", number),
            ("$json", System.Text.Encoding.UTF8.GetBytes(CaptureContractJson.SerializeToElement(profile).GetRawText())),
            ("$hash", hash), ("$schedule", CaptureScheduleContract.ComputeSha256(profile.Schedule)),
            ("$actor", actor), ("$now", now)).ConfigureAwait(false);
        var changed = await ExecuteAsync(connection, transaction, """
            UPDATE capture_schedule_state SET pending_revision_id = $id, version = version + 1, updated_unix_ms = $now
            WHERE state_key = 1 AND version = $version;
            """, cancellationToken, ("$id", scheduleId), ("$now", now), ("$version", schedule.Version)).ConfigureAwait(false);
        if (changed != 1) throw new CaptureScheduleStoreConflictException("Schedule changed during staging.");
        changed = await ExecuteAsync(connection, transaction, """
            UPDATE named_rig_selection SET pending_revision_id = $id, pending_command_key = $key,
                pending_schedule_revision_id = $schedule, pending_schedule_version = $scheduleVersion,
                version = version + 1, updated_unix_ms = $now
            WHERE state_key = 1 AND pending_revision_id IS NULL AND version = $version;
            """, cancellationToken, ("$id", revisionId), ("$key", idempotencyKey),
            ("$schedule", scheduleId), ("$scheduleVersion", schedule.Version + 1),
            ("$now", now), ("$version", expectedVersion)).ConfigureAwait(false);
        if (changed != 1) throw new CaptureScheduleStoreConflictException("Selection changed during staging.");
        var receipt = new NamedRigStageReceipt(Guid.NewGuid().ToString("N"), revisionId,
            expectedVersion + 1, "restart_required", true, scheduleId);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO named_rig_selection_commands VALUES ($key, 'stage', $version, $target, $actor, $hash, 1, $result, $now, $now);
            INSERT INTO named_rig_selection_receipts VALUES ($receipt, $key, $from, $target, 'restart_required', $next, $now, $key);
            """, cancellationToken, ("$key", idempotencyKey), ("$version", expectedVersion),
            ("$target", revisionId), ("$actor", actor), ("$hash", requestHash),
            ("$result", JsonSerializer.SerializeToUtf8Bytes(receipt)), ("$now", now),
            ("$receipt", receipt.ReceiptId), ("$from", (object?)catalog.Selection.ActiveRevisionId ?? DBNull.Value),
            ("$next", receipt.Version)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed internal SQL statements are passed.")]
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
    }

    public async Task<NamedRigRevision> ComposeAsync(string profileId, string cameraRevisionId,
        string opticsRevisionId, string mountRevisionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(cameraRevisionId) ||
            string.IsNullOrWhiteSpace(opticsRevisionId) || string.IsNullOrWhiteSpace(mountRevisionId))
            throw new ArgumentException("Rig composition requires a profile and three equipment revisions.");
        if (scheduleStore is null) throw new InvalidOperationException("Schedule authority is unavailable.");
        var schedule = await scheduleStore.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        async Task<T> ReadEquipmentAsync<T>(string revisionId, string kind)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT r.definition_json, r.definition_sha256 FROM named_equipment_revisions r
                JOIN named_equipment_definitions d ON d.definition_id = r.definition_id
                WHERE r.revision_id = $revision AND d.kind = $kind;
                """;
            command.Parameters.AddWithValue("$revision", revisionId);
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new KeyNotFoundException("Equipment revision was not found for its kind.");
            var bytes = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reader.GetString(1), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Equipment revision content hash differs.");
            return JsonSerializer.Deserialize<T>(bytes, EquipmentJson)
                ?? throw new InvalidDataException("Equipment revision content is invalid.");
        }
        var camera = await ReadEquipmentAsync<NamedCameraEquipment>(cameraRevisionId, "camera").ConfigureAwait(false);
        var optics = await ReadEquipmentAsync<OpticsProfile>(opticsRevisionId, "optics").ConfigureAwait(false);
        var mount = await ReadEquipmentAsync<RigOrientation>(mountRevisionId, "mount").ConfigureAwait(false);
        if (camera.Module is null || string.IsNullOrWhiteSpace(camera.Module.Type) || camera.Sensor is null ||
            optics is null || mount is null)
            throw new ArgumentException("Equipment revision is incomplete.");
        var rig = schedule.ActiveRevision.Profile.Rig with
        {
            Sensor = camera.Sensor,
            Readout = camera.Readout,
            Optics = optics,
            Orientation = mount
        };
        var candidate = schedule.ActiveRevision.Profile with { Module = camera.Module, Rig = rig };
        var validation = LocalCaptureProfileContract.Validate(candidate);
        if (!validation.IsValid) throw new ArgumentException($"Rig composition is invalid ({validation.FieldPath}).");
        if (camera.Readout is not null) _ = SensorReadoutResolver.Resolve(camera.Sensor, camera.Readout);
        // Composing is a draft operation; the schedule may change before this transaction commits.
        // Preview always reads the current schedule and no draft becomes capture authority.
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        using var next = connection.CreateCommand();
        next.Transaction = transaction;
        next.CommandText = "SELECT COALESCE(MAX(revision_number), 0) + 1 FROM named_rig_revisions WHERE profile_id = $profile;";
        next.Parameters.AddWithValue("$profile", profileId);
        var number = (long)(await next.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 1L);
        var id = Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new InstalledRig(camera.Module, rig));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO named_rig_revisions VALUES ($id, $profile, $number, $camera, $optics, $mount, $json, $hash, NULL, $now);
            """, cancellationToken, ("$id", id), ("$profile", profileId), ("$number", number),
            ("$camera", cameraRevisionId), ("$optics", opticsRevisionId), ("$mount", mountRevisionId),
            ("$json", bytes), ("$hash", Convert.ToHexString(SHA256.HashData(bytes))), ("$now", now)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new NamedRigRevision(id, profileId, number, cameraRevisionId, opticsRevisionId,
            mountRevisionId, camera.Module, rig, null);
    }

    public async Task<NamedRigPreview> PreviewAsync(string revisionId, CancellationToken cancellationToken)
    {
        if (scheduleStore is null) throw new InvalidOperationException("Schedule authority is unavailable.");
        var schedule = await scheduleStore.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var revision = await ReadRevisionAsync(connection, null, revisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Rig revision was not found.");
        var current = schedule.ActiveRevision.Profile;
        var rig = current.Rig with
        {
            Sensor = revision.Rig.Sensor,
            Readout = revision.Rig.Readout,
            Optics = revision.Rig.Optics,
            Orientation = revision.Rig.Orientation
        };
        var candidate = current with { Module = revision.Module, Rig = rig };
        var validation = LocalCaptureProfileContract.Validate(candidate);
        string? failure = validation.IsValid ? null : validation.FieldPath;
        if (failure is null)
        {
            try
            {
                if (revision.Rig.Readout is not null)
                    _ = SensorReadoutResolver.Resolve(revision.Rig.Sensor, revision.Rig.Readout);
                var envelope = candidate.Rig.Pipeline.Envelope;
                if (envelope is not null && candidate.Schedule.SetpointProfiles.Any(value =>
                    value.Exposure < envelope.MinExposure || value.Exposure > envelope.MaxExposure ||
                    value.Gain < envelope.MinGain || value.Gain > envelope.MaxGain))
                    failure = "rig.pipeline.envelope";
                var response = candidate.Rig.Sensor.SimulationResponse;
                if (response is not null && candidate.Schedule.SetpointProfiles.Any(value =>
                    value.Gain < response.MinimumGainControl || value.Gain > response.MaximumGainControl))
                    failure = "rig.sensor.simulationResponse";
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                failure = "rig.readout";
            }
        }
        return new NamedRigPreview(schedule.ActiveRevision.RevisionId, schedule.ActiveRevision.ProfileSha256,
            revisionId, failure is null, failure, RuntimeVerified: false);
    }

    public async Task<NamedRigInventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var profiles = new List<NamedRigProfile>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT profile_id, display_name FROM named_rig_profiles ORDER BY display_name LIMIT 100;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                profiles.Add(new NamedRigProfile(reader.GetString(0), reader.GetString(1)));
            }
        }
        var equipment = new List<NamedEquipmentDefinition>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT d.definition_id, d.kind, d.display_name, r.revision_number, r.revision_id
                FROM named_equipment_definitions d JOIN named_equipment_revisions r ON r.definition_id = d.definition_id
                WHERE r.revision_number = (SELECT MAX(latest.revision_number) FROM named_equipment_revisions latest
                    WHERE latest.definition_id = d.definition_id)
                ORDER BY d.kind, d.display_name LIMIT 100;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                equipment.Add(new NamedEquipmentDefinition(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetInt64(3), reader.GetString(4)));
            }
        }
        return new NamedRigInventory(profiles, equipment);
    }

    public async Task<NamedEquipmentDetail> GetEquipmentAsync(string revisionId, CancellationToken cancellationToken)
    {
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.definition_id, d.kind, d.display_name, r.revision_number, r.revision_id,
                   r.definition_json, r.definition_sha256
            FROM named_equipment_revisions r JOIN named_equipment_definitions d ON d.definition_id = r.definition_id
            WHERE r.revision_id = $revision;
            """;
        command.Parameters.AddWithValue("$revision", revisionId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("Equipment revision was not found.");
        var bytes = await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reader.GetString(6), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Equipment revision content hash differs.");
        var kind = reader.GetString(1);
        object definition = kind switch
        {
            "camera" => RedactCamera(JsonSerializer.Deserialize<NamedCameraEquipment>(bytes, EquipmentJson)
                ?? throw new InvalidDataException("Invalid camera definition.")),
            "optics" => JsonSerializer.Deserialize<OpticsProfile>(bytes, EquipmentJson)
                ?? throw new InvalidDataException("Invalid optics definition."),
            "mount" => JsonSerializer.Deserialize<RigOrientation>(bytes, EquipmentJson)
                ?? throw new InvalidDataException("Invalid mount definition."),
            _ => throw new InvalidDataException("Invalid equipment kind.")
        };
        return new NamedEquipmentDetail(reader.GetString(0), kind, reader.GetString(2), reader.GetInt64(3),
            reader.GetString(4), definition);
    }

    private static object RedactCamera(NamedCameraEquipment camera) => new
    {
        Module = new { camera.Module.Type },
        camera.Sensor,
        camera.Readout
    };

    public async Task<NamedEquipmentDefinition> SaveEquipmentAsync(string? definitionId, string kind,
        string displayName, JsonElement definition, CancellationToken cancellationToken,
        string? basisRevisionId = null, string? expectedRevisionId = null)
    {
        if (kind is not ("camera" or "optics" or "mount") || !ValidName(displayName) ||
            definition.ValueKind != JsonValueKind.Object ||
            JsonSerializer.SerializeToUtf8Bytes(definition).Length > 1048576 ||
            (definitionId is null && (basisRevisionId is not null || expectedRevisionId is not null)) ||
            (definitionId is not null && (string.IsNullOrWhiteSpace(basisRevisionId) || string.IsNullOrWhiteSpace(expectedRevisionId))) ||
            (kind == "camera" && definitionId is not null &&
             (!definition.TryGetProperty("module", out var module) || module.ValueKind != JsonValueKind.Object ||
              module.EnumerateObject().Any(property => property.Name.Equals("options", StringComparison.OrdinalIgnoreCase)))))
        {
            throw new ArgumentException("Invalid equipment definition.");
        }
        object typed;
        try
        {
            typed = kind switch
            {
                "camera" => definition.Deserialize<NamedCameraEquipment>(EquipmentJson)!,
                "optics" => definition.Deserialize<OpticsProfile>(EquipmentJson)!,
                _ => definition.Deserialize<RigOrientation>(EquipmentJson)!
            };
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Equipment definition is invalid.", nameof(definition), exception);
        }
        if (typed is null || typed is NamedCameraEquipment { Module: null } or NamedCameraEquipment { Sensor: null } ||
            typed is NamedCameraEquipment camera && string.IsNullOrWhiteSpace(camera.Module.Type))
            throw new ArgumentException("Equipment definition is incomplete.");
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var id = definitionId ?? Guid.NewGuid().ToString("N");
        if (definitionId is not null)
        {
            using var basis = connection.CreateCommand();
            basis.Transaction = transaction;
            basis.CommandText = """
                SELECT r.definition_json, r.definition_sha256 FROM named_equipment_revisions r
                JOIN named_equipment_definitions d ON d.definition_id = r.definition_id
                WHERE r.revision_id = $basis AND d.definition_id = $id AND d.kind = $kind
                  AND (SELECT revision_id FROM named_equipment_revisions WHERE definition_id = $id
                       ORDER BY revision_number DESC LIMIT 1) = $expected;
                """;
            basis.Parameters.AddWithValue("$basis", basisRevisionId);
            basis.Parameters.AddWithValue("$expected", expectedRevisionId);
            basis.Parameters.AddWithValue("$id", id);
            basis.Parameters.AddWithValue("$kind", kind);
            using var reader = await basis.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CaptureScheduleStoreConflictException("Equipment basis or expected revision has changed.");
            var basisBytes = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(basisBytes)), reader.GetString(1), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Equipment basis content hash differs.");
            if (kind == "camera")
            {
                var original = JsonSerializer.Deserialize<NamedCameraEquipment>(basisBytes, EquipmentJson)
                    ?? throw new InvalidDataException("Equipment basis is invalid.");
                var revised = (NamedCameraEquipment)typed;
                if (revised.Module.Type != original.Module.Type)
                    throw new ArgumentException("Camera module type cannot change during revision.");
                typed = revised with { Module = revised.Module with { Options = original.Module.Options } };
            }
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(typed);
        if (bytes.Length > 1048576) throw new ArgumentException("Equipment definition exceeds the schema limit.");
        if (definitionId is null)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO named_equipment_definitions VALUES ($id, $kind, $name, $now, $now);",
                cancellationToken, ("$id", id), ("$kind", kind), ("$name", displayName.Trim()), ("$now", now)).ConfigureAwait(false);
        }
        else
        {
            var updated = await ExecuteAsync(connection, transaction,
                """
                UPDATE named_equipment_definitions SET display_name = $name, updated_unix_ms = $now
                WHERE definition_id = $id AND kind = $kind AND definition_id NOT IN
                (SELECT e.definition_id FROM named_equipment_revisions e JOIN named_rig_revisions r
                 ON e.revision_id IN (r.camera_revision_id, r.optics_revision_id, r.mount_revision_id)
                 WHERE r.source_schedule_revision_id IS NOT NULL);
                """,
                cancellationToken, ("$name", displayName.Trim()), ("$now", now), ("$id", id), ("$kind", kind)).ConfigureAwait(false);
            if (updated != 1) throw new CaptureScheduleStoreConflictException("Equipment is missing or installed and immutable.");
        }
        long next;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COALESCE(MAX(revision_number), 0) + 1 FROM named_equipment_revisions WHERE definition_id = $id;";
            command.Parameters.AddWithValue("$id", id);
            next = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 1L);
        }
        var revisionId = Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, transaction,
            "INSERT INTO named_equipment_revisions VALUES ($revision, $id, $number, $json, $hash, $now);",
            cancellationToken, ("$revision", revisionId), ("$id", id), ("$number", next),
            ("$json", bytes), ("$hash", Convert.ToHexString(SHA256.HashData(bytes))), ("$now", now)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new NamedEquipmentDefinition(id, kind, displayName.Trim(), next, revisionId);
    }

    public async Task<NamedRigProfile> SaveProfileNameAsync(string? profileId, string displayName,
        CancellationToken cancellationToken)
    {
        if (!ValidName(displayName)) throw new ArgumentException("Invalid rig profile name.");
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var id = profileId ?? Guid.NewGuid().ToString("N");
        if (profileId is null)
        {
            await ExecuteAsync(connection, null, "INSERT INTO named_rig_profiles VALUES ($id, $name, $now, $now);",
                cancellationToken, ("$id", id), ("$name", displayName.Trim()), ("$now", now)).ConfigureAwait(false);
        }
        else
        {
            var changed = await ExecuteAsync(connection, null,
                "UPDATE named_rig_profiles SET display_name = $name, updated_unix_ms = $now WHERE profile_id = $id AND profile_id NOT IN (SELECT profile_id FROM named_rig_revisions WHERE source_schedule_revision_id IS NOT NULL);",
                cancellationToken, ("$id", id), ("$name", displayName.Trim()), ("$now", now)).ConfigureAwait(false);
            if (changed != 1) throw new CaptureScheduleStoreConflictException("Rig profile is missing or installed and immutable.");
        }
        return new NamedRigProfile(id, displayName.Trim());
    }

    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 128;

    private static async Task<string> InsertEquipmentAsync<T>(SqliteConnection connection, SqliteTransaction transaction,
        string kind, string name, T value, long now, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        var revision = Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO named_equipment_definitions VALUES ($id, $kind, $name, $now, $now);
            INSERT INTO named_equipment_revisions VALUES ($revision, $id, 1, $json, $hash, $now);
            """, token, ("$id", id), ("$kind", kind), ("$name", name), ("$now", now),
            ("$revision", revision), ("$json", bytes),
            ("$hash", Convert.ToHexString(SHA256.HashData(bytes)))).ConfigureAwait(false);
        return revision;
    }

    private static async Task<NamedRigCatalog> ReadAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        var selection = await ReadSelectionAsync(connection, transaction, token).ConfigureAwait(false);
        var revisionIds = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT revision_id FROM named_rig_revisions ORDER BY created_unix_ms DESC LIMIT 100;";
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                revisionIds.Add(reader.GetString(0));
        }
        var revisions = new List<NamedRigRevision>(revisionIds.Count);
        foreach (var id in revisionIds)
            revisions.Add((await ReadRevisionAsync(connection, transaction, id, token).ConfigureAwait(false))!);
        foreach (var id in new[] { selection.ActiveRevisionId, selection.PendingRevisionId })
        {
            if (id is not null && !revisionIds.Contains(id, StringComparer.Ordinal))
                revisions.Add(await ReadRevisionAsync(connection, transaction, id, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Selected named rig revision is missing."));
        }
        return new NamedRigCatalog(selection, revisions);
    }

    private static async Task<NamedRigSelection> ReadSelectionAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT active_revision_id, pending_revision_id, version, pending_command_key, pending_schedule_revision_id, pending_schedule_version FROM named_rig_selection WHERE state_key = 1;";
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                throw new InvalidDataException("Named rig selection is missing.");
            }
            return new NamedRigSelection(
                await reader.IsDBNullAsync(0, token).ConfigureAwait(false) ? null : reader.GetString(0),
                await reader.IsDBNullAsync(1, token).ConfigureAwait(false) ? null : reader.GetString(1), reader.GetInt64(2),
                await reader.IsDBNullAsync(3, token).ConfigureAwait(false) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, token).ConfigureAwait(false) ? null : reader.GetString(4),
                await reader.IsDBNullAsync(5, token).ConfigureAwait(false) ? null : reader.GetInt64(5));
        }
    }

    private static async Task<NamedRigRevision?> ReadRevisionAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string revisionId, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision_id, profile_id, revision_number, camera_revision_id, optics_revision_id,
                   mount_revision_id, rig_json, rig_sha256, source_schedule_revision_id
            FROM named_rig_revisions WHERE revision_id = $id;
            """;
        command.Parameters.AddWithValue("$id", revisionId);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var bytes = await reader.GetFieldValueAsync<byte[]>(6, token).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reader.GetString(7), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Named rig revision content hash differs.");
        var rig = JsonSerializer.Deserialize<InstalledRig>(bytes)
            ?? throw new InvalidDataException("Named rig revision content is invalid.");
        return new NamedRigRevision(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), rig.Module, rig.Rig,
            await reader.IsDBNullAsync(8, token).ConfigureAwait(false) ? null : reader.GetString(8));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(_connectionString);
        await Sqlite.SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
            connection, async (configuredConnection, cancellationToken) =>
        {
            using var command = configuredConnection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "PRAGMA synchronous = FULL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        return connection;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed internal SQL statements are passed; all values are parameterized.")]
    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private sealed record InstalledRig(CameraModuleDescriptor Module, CameraRigConfig Rig);
}
