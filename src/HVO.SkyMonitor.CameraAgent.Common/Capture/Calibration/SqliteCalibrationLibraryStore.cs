using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

public sealed record CalibrationLibraryBundleSnapshot(
    CalibrationLibraryBundleV1 Bundle,
    string BundleIdentitySha256,
    string PublicationState,
    string? FailureReason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record CalibrationLibraryStateSnapshot(
    CalibrationLibraryBundleSnapshot? ActiveBundle,
    long Version,
    string? LastSelectionReason,
    DateTimeOffset? LastSelectionUtc,
    string? LastReconciliationReason,
    DateTimeOffset? LastReconciliationUtc,
    DateTimeOffset UpdatedUtc);

public sealed record CalibrationLibrarySelectionResult(
    CalibrationLibraryBundleSnapshot? Bundle,
    string ReasonCode,
    long StateVersion)
{
    public bool IsSelected => Bundle is not null;
}

public sealed record CalibrationLibraryBundleCursor(DateTimeOffset CreatedUtc, string BundleId);

public sealed record CalibrationLibraryBundlePage(
    IReadOnlyList<CalibrationLibraryBundleSnapshot> Items,
    CalibrationLibraryBundleCursor? NextCursor);

public sealed record CalibrationLibraryActivationSnapshot(
    string CommandKind,
    string? FromBundleId,
    string ToBundleId,
    long StateVersion,
    DateTimeOffset ActivatedUtc,
    string? Reason);

public sealed record CalibrationLibraryOperationsStatus(
    CalibrationLibraryStateSnapshot State,
    CalibrationAcquisitionJobSnapshot? PendingAcquisition,
    long PublishedBundleCount,
    long QuarantineCount,
    CalibrationLibraryActivationSnapshot? LastActivation);

public sealed record CalibrationActiveValidationSnapshot(
    string? BundleIdentitySha256,
    string ReasonCode);

internal sealed record CalibrationReconciliationOperation(
    string EvidenceKey,
    string SourceRelativePath,
    string? QuarantineRelativePath,
    string Outcome,
    string Reason,
    string OperationState);

public sealed class CalibrationLibraryStoreConflictException : InvalidOperationException
{
    public CalibrationLibraryStoreConflictException()
    {
    }

    public CalibrationLibraryStoreConflictException(string message) : base(message)
    {
    }

    public CalibrationLibraryStoreConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
    Justification = "Injected telemetry is owned by the dependency injection container.")]
public sealed class SqliteCalibrationLibraryStore(
    IRawCaptureIngress rawCaptureIngress,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    CalibrationTelemetry? telemetry = null) : IProcessingRetentionHolds, IDisposable
{
    internal const int MaximumAcquisitionAttempts = 3;
    private readonly IRawCaptureIngress _rawCaptureIngress = rawCaptureIngress;
    private readonly string _storageRoot = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly string _databasePath = Path.Combine(
        Path.GetFullPath(options.Value.RawIngressRoot), "journal", "raw-ingress.db");
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(
            Path.GetFullPath(options.Value.RawIngressRoot), "journal", "raw-ingress.db"),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
        DefaultTimeout = options.Value.RawIngressSqliteBusyTimeoutSeconds
    }.ToString();
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly CalibrationTelemetry? _telemetry = telemetry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, PublishedBundleEvidence> _validatedEvidence =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _selectionRecordGate = new();
    private readonly object _activeValidationGate = new();
    private string? _lastRecordedSelectionReason;
    private long _lastRecordedSelectionUnixMs;
    private string? _activeValidationBundleIdentity;
    private string _activeValidationReason = CalibrationLibraryReasonCodes.Inactive;
    private int _telemetryInventoryInitialized;

    public async Task<CalibrationLibraryStateSnapshot> InitializeAsync(CancellationToken cancellationToken)
    {
        using var activity = CalibrationTelemetry.ActivitySource.StartActivity("calibration.library.initialize");
        try
        {
            await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
                using var transaction = BeginRead(connection);
                var state = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                UpdateActiveValidationIdentity(state);
                if (_telemetry is not null && Interlocked.CompareExchange(ref _telemetryInventoryInitialized, 1, 0) == 0)
                {
                    try
                    {
                        _telemetry.ReplaceInventory(
                            await ReadTelemetryInventoryAsync(connection, transaction, cancellationToken).ConfigureAwait(false));
                    }
                    catch
                    {
                        Volatile.Write(ref _telemetryInventoryInitialized, 0);
                        throw;
                    }
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _telemetry?.RecordLibraryOperation("initialize", "success");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return state;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _telemetry?.RecordFailure("initialize", "other");
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    public async Task<CalibrationAcquisitionJobSnapshot?> ReadAcquireReplayAsync(
        string idempotencyKey,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadSha256);
        ValidateCommandIdentity(idempotencyKey, payloadSha256);
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        var result = await ReadAcquireCommandAsync(
            connection, transaction, idempotencyKey, payloadSha256, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CalibrationAcquisitionJobSnapshot> PlanAcquireAsync(
        VirtualCalibrationAcquisitionPlanV1 plan,
        string idempotencyKey,
        string payloadSha256,
        string actor,
        string? reason,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadSha256);
        VirtualCalibrationAcquisitionContractJson.ValidatePlan(plan);
        ValidateCommandIdentity(idempotencyKey, payloadSha256);
        ValidateActor(actor, reason);
        var planJson = VirtualCalibrationAcquisitionContractJson.SerializePlan(plan);
        var planIdentity = Convert.ToHexString(SHA256.HashData(planJson));
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var replay = await ReadAcquireCommandAsync(
                connection, transaction, idempotencyKey, payloadSha256, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }
            if (expectedVersion is { } version &&
                (await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false)).Version != version)
            {
                throw new CalibrationLibraryStoreConflictException(
                    "The calibration library state changed after the operator read it.");
            }
            var existing = await ReadNonterminalAcquisitionJobAsync(
                connection, transaction, plan.CameraKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                throw new CalibrationLibraryStoreConflictException(
                    "A nonterminal calibration acquisition already exists for this camera.");
            }
            var now = Now();
            await ExecuteAsync(connection, transaction, """
                INSERT INTO calibration_acquisition_jobs(
                    job_id, camera_key, idempotency_key, plan_json, plan_sha256, state, phase,
                    attempt_count, bundle_id, failure_reason, actor, reason,
                    created_unix_ms, updated_unix_ms, completed_unix_ms)
                VALUES ($job, $camera, $key, $plan, $plan_sha, 'planned', 'planned',
                        0, NULL, NULL, $actor, $reason, $now, $now, NULL);
                INSERT INTO calibration_library_commands(
                    idempotency_key, command_kind, payload_sha256, result_bundle_id, result_job_id,
                    result_state_version, result_json, created_unix_ms, completed_unix_ms)
                VALUES ($key, 'acquire', $payload, NULL, $job, $version, $plan, $now, $now);
                """, cancellationToken,
                ("$job", plan.JobId),
                ("$camera", plan.CameraKey),
                ("$key", idempotencyKey),
                ("$plan", planJson),
                ("$plan_sha", planIdentity),
                ("$payload", payloadSha256),
                ("$actor", actor),
                ("$reason", (object?)reason ?? DBNull.Value),
                ("$version", expectedVersion.HasValue ? expectedVersion.Value : DBNull.Value),
                ("$now", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            var job = await ReadAcquisitionJobAsync(
                connection, transaction, plan.JobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The durable calibration acquisition job was not created.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _telemetry?.RecordAcquisitionTransition(
                "none", job.State, job.Phase, job.AttemptCount, job.FailureReason);
            return job;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new CalibrationLibraryStoreConflictException(
                "Calibration acquisition planning conflicts with durable state.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationAcquisitionJobSnapshot?> ReadPendingAcquisitionJobAsync(
        CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        string? jobId;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT job_id FROM calibration_acquisition_jobs
                WHERE state NOT IN ('published', 'failed', 'cancelled')
                ORDER BY created_unix_ms, job_id LIMIT 1;
                """;
            jobId = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        var result = jobId is null
            ? null
            : await ReadAcquisitionJobAsync(connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CalibrationAcquisitionJobSnapshot?> GetAcquisitionJobAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 128)
        {
            throw new ArgumentException("A valid calibration job identifier is required.", nameof(jobId));
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        var result = await ReadAcquisitionJobAsync(connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal async Task<bool> HasPendingCancelCommandAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM calibration_library_commands
            WHERE command_kind = 'cancel' AND result_job_id = $job AND completed_unix_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public async Task<CalibrationAcquisitionJobSnapshot> BeginAcquisitionAttemptAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadRequiredAcquisitionJobAsync(
                connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            if (current.IsTerminal)
            {
                await CompletePendingCancelCommandsAsync(
                    connection, transaction, current, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }
            var state = current.State == CalibrationAcquisitionStates.Planned
                ? CalibrationAcquisitionStates.Acquiring
                : current.State;
            var phase = current.State == CalibrationAcquisitionStates.Planned ? "sources-pending" : current.Phase;
            await ExecuteAsync(connection, transaction, """
                UPDATE calibration_acquisition_jobs
                SET state = $state, phase = $phase, updated_unix_ms = $now
                WHERE job_id = $job;
                """, cancellationToken,
                ("$state", state), ("$phase", phase), ("$now", Now().ToUnixTimeMilliseconds()), ("$job", jobId))
                .ConfigureAwait(false);
            var result = await ReadRequiredAcquisitionJobAsync(
                connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            await CompletePendingCancelCommandsAsync(
                connection, transaction, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.State, result.State, StringComparison.Ordinal))
            {
                _telemetry?.RecordAcquisitionTransition(
                    current.State, result.State, result.Phase, result.AttemptCount, result.FailureReason);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationAcquisitionJobSnapshot> RecordAcquisitionAttemptFailureAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadRequiredAcquisitionJobAsync(
                connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            if (current.IsTerminal)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }
            await ExecuteAsync(connection, transaction, """
                UPDATE calibration_acquisition_jobs
                SET attempt_count = attempt_count + 1, updated_unix_ms = $now
                WHERE job_id = $job;
                """, cancellationToken,
                ("$now", Now().ToUnixTimeMilliseconds()), ("$job", jobId)).ConfigureAwait(false);
            var result = await ReadRequiredAcquisitionJobAsync(
                connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CalibrationAcquisitionJobSnapshot> RecordAcquisitionProgressAsync(
        string jobId,
        string state,
        string phase,
        CancellationToken cancellationToken)
        => UpdateAcquisitionJobAsync(jobId, state, phase, bundleId: null, failureReason: null, terminal: false, cancellationToken);

    public Task<CalibrationAcquisitionJobSnapshot> CompleteAcquisitionAsync(
        string jobId,
        string bundleId,
        CancellationToken cancellationToken)
        => UpdateAcquisitionJobAsync(
            jobId, CalibrationAcquisitionStates.Published, "published", bundleId, failureReason: null,
            terminal: true, cancellationToken);

    public Task<CalibrationAcquisitionJobSnapshot> FailAcquisitionAsync(
        string jobId,
        string failureReason,
        CancellationToken cancellationToken)
        => UpdateAcquisitionJobAsync(
            jobId, CalibrationAcquisitionStates.Failed, "failed", bundleId: null, failureReason,
            terminal: true, cancellationToken);

    internal async Task<CalibrationAcquisitionJobSnapshot> CancelAcquisitionAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadRequiredAcquisitionJobAsync(
                connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            CalibrationAcquisitionJobSnapshot result;
            if (current.IsTerminal)
            {
                result = current;
            }
            else
            {
                ValidateAcquisitionTransition(
                    current, CalibrationAcquisitionStates.Cancelled, "cancelled",
                    bundleId: null, failureReason: null, terminal: true);
                var now = Now().ToUnixTimeMilliseconds();
                await ExecuteAsync(connection, transaction, """
                    UPDATE calibration_acquisition_jobs
                    SET state = 'cancelled', phase = 'cancelled', bundle_id = NULL,
                        failure_reason = NULL, updated_unix_ms = $now, completed_unix_ms = $now
                    WHERE job_id = $job;
                    """, cancellationToken, ("$now", now), ("$job", jobId)).ConfigureAwait(false);
                result = await ReadRequiredAcquisitionJobAsync(
                    connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            }
            await CompletePendingCancelCommandsAsync(
                connection, transaction, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.State, result.State, StringComparison.Ordinal))
            {
                _telemetry?.RecordAcquisitionTransition(
                    current.State, result.State, result.Phase, result.AttemptCount, result.FailureReason);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationAcquisitionJobSnapshot?> PrepareCancelAcquisitionAsync(
        string jobId,
        string idempotencyKey,
        long expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateCancelMutation(jobId, idempotencyKey, expectedVersion, actor, reason);
        var payloadSha256 = CancelPayloadSha256(jobId, expectedVersion, actor, reason);
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var existing = await ReadCancelCommandAsync(
                connection, transaction, idempotencyKey, payloadSha256, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing.Pending ? null : existing.Result;
            }
            var state = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (state.Version != expectedVersion)
            {
                throw new CalibrationLibraryStoreConflictException(
                    "The calibration library state changed after the operator read it.");
            }
            var job = await ReadAcquisitionJobAsync(connection, transaction, jobId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The calibration acquisition job was not found.");
            var now = Now().ToUnixTimeMilliseconds();
            await ExecuteAsync(connection, transaction, """
                INSERT INTO calibration_library_commands(
                    idempotency_key, command_kind, payload_sha256, result_bundle_id, result_job_id,
                    result_state_version, result_json, created_unix_ms, completed_unix_ms)
                VALUES ($key, 'cancel', $payload, $bundle, $job, $version, $result, $now, NULL);
                """, cancellationToken,
                ("$key", idempotencyKey),
                ("$payload", payloadSha256),
                ("$bundle", (object?)job.BundleId ?? DBNull.Value),
                ("$job", job.Plan.JobId),
                ("$version", expectedVersion),
                ("$result", JsonSerializer.SerializeToUtf8Bytes(job)),
                ("$now", now)).ConfigureAwait(false);
            if (job.IsTerminal)
            {
                await CompletePendingCancelCommandsAsync(
                    connection, transaction, job, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return job.IsTerminal ? job : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationAcquisitionJobSnapshot> CompleteCancelAcquisitionCommandAsync(
        CalibrationAcquisitionJobSnapshot result,
        string idempotencyKey,
        long expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateCancelMutation(result.Plan.JobId, idempotencyKey, expectedVersion, actor, reason);
        if (!result.IsTerminal)
        {
            throw new ArgumentException("A cancellation command requires a terminal cancellation result.", nameof(result));
        }
        var payloadSha256 = CancelPayloadSha256(result.Plan.JobId, expectedVersion, actor, reason);
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var replay = await ReadCancelCommandAsync(
                connection, transaction, idempotencyKey, payloadSha256, cancellationToken).ConfigureAwait(false);
            if (replay is null)
            {
                throw new InvalidDataException("The durable calibration cancellation request is missing.");
            }
            if (!replay.Pending)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay.Result;
            }
            var current = await ReadRequiredAcquisitionJobAsync(
                connection, transaction, result.Plan.JobId, cancellationToken).ConfigureAwait(false);
            if (current.State != result.State || current.Phase != result.Phase || current.BundleId != result.BundleId)
            {
                throw new CalibrationLibraryStoreConflictException(
                    "The calibration acquisition changed while cancellation completed.");
            }
            await CompletePendingCancelCommandsAsync(
                connection, transaction, current, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new CalibrationLibraryStoreConflictException(
                "The calibration cancellation command conflicts with durable state.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<IReadOnlyList<CalibrationReconciliationOperation>> ReadPlannedReconciliationsAsync(
        CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_key, source_relative_path, quarantine_relative_path,
                   outcome, reason, operation_state
            FROM calibration_library_reconciliation
            WHERE operation_state = 'planned' ORDER BY reconciliation_id;
            """;
        var operations = new List<CalibrationReconciliationOperation>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            operations.Add(new CalibrationReconciliationOperation(
                reader.GetString(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return operations;
    }

    internal async Task PlanReconciliationAsync(
        CalibrationReconciliationOperation operation,
        long observedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.OperationState != "planned" || operation.Outcome is not ("adopted" or "failed" or "quarantined") ||
            operation.EvidenceKey.Length != 64 || observedBytes < 0)
        {
            throw new ArgumentException("The calibration reconciliation operation is invalid.", nameof(operation));
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO calibration_library_reconciliation(
                    evidence_key, source_relative_path, quarantine_relative_path, outcome, reason,
                    operation_state, observed_bytes, observed_unix_ms, completed_unix_ms)
                VALUES ($key, $source, $quarantine, $outcome, $reason, 'planned', $bytes, $now, NULL)
                ON CONFLICT(evidence_key) DO NOTHING;
                """, cancellationToken,
                ("$key", operation.EvidenceKey),
                ("$source", operation.SourceRelativePath),
                ("$quarantine", (object?)operation.QuarantineRelativePath ?? DBNull.Value),
                ("$outcome", operation.Outcome),
                ("$reason", operation.Reason),
                ("$bytes", observedBytes),
                ("$now", Now().ToUnixTimeMilliseconds())).ConfigureAwait(false);
            var existing = await ReadReconciliationAsync(
                connection, transaction, operation.EvidenceKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The calibration reconciliation operation was not persisted.");
            if (!ReconciliationFactsMatch(existing, operation))
            {
                throw new CalibrationLibraryStoreConflictException(
                    "The calibration reconciliation identity has different durable facts.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CompleteReconciliationAsync(
        string evidenceKey,
        string reason,
        CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var changed = await ExecuteAsync(connection, transaction, """
                UPDATE calibration_library_reconciliation
                SET operation_state = 'completed', completed_unix_ms = COALESCE(completed_unix_ms, $now)
                WHERE evidence_key = $key;
                UPDATE calibration_library_state
                SET last_reconciliation_reason = $reason,
                    last_reconciliation_unix_ms = $now,
                    updated_unix_ms = $now
                WHERE state_key = 1;
                """, cancellationToken,
                ("$now", now.ToUnixTimeMilliseconds()),
                ("$key", evidenceKey),
                ("$reason", reason)).ConfigureAwait(false);
            if (changed < 2)
            {
                throw new InvalidDataException("The calibration reconciliation operation is missing.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordReconciliationResultAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (reason is not ("calibration.library.reconciled" or "calibration.library.reconciliation-failed"))
        {
            throw new ArgumentException("The calibration reconciliation result is invalid.", nameof(reason));
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction: null, """
                UPDATE calibration_library_state
                SET last_reconciliation_reason = $reason,
                    last_reconciliation_unix_ms = $now,
                    updated_unix_ms = $now
                WHERE state_key = 1;
                """, cancellationToken,
                ("$reason", reason),
                ("$now", Now().ToUnixTimeMilliseconds())).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationLibraryBundleSnapshot> AdoptPublishedBundleAsync(
        CalibrationLibraryBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        using var activity = CalibrationTelemetry.ActivitySource.StartActivity("calibration.bundle.publish");
        ArgumentNullException.ThrowIfNull(bundle);
        var validation = CalibrationLibraryContract.Validate(bundle);
        if (!validation.IsValid)
        {
            _telemetry?.RecordValidationFailure("publish", CalibrationLibraryReasonCodes.InvalidBundle);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw new ArgumentException($"The calibration bundle is invalid ({validation.FieldPath}).", nameof(bundle));
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var bundleJson = CalibrationLibraryContractJson.Serialize(bundle);
        bundle = CalibrationLibraryContractJson.Parse(bundleJson).Value
            ?? throw new InvalidDataException("The canonical calibration bundle could not be parsed.");
        var bundleIdentity = CalibrationLibraryContractJson.ComputeIdentitySha256(bundle);
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_storageRoot);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var storeGateAcquired = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            storeGateAcquired = true;
            using (var precheckConnection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var precheckTransaction = BeginRead(precheckConnection))
            {
                var preexisting = await ReadBundleAsync(
                    precheckConnection, precheckTransaction, bundle.BundleId, cancellationToken).ConfigureAwait(false);
                if (preexisting is not null &&
                    (!string.Equals(preexisting.BundleIdentitySha256, bundleIdentity, StringComparison.OrdinalIgnoreCase) ||
                     !preexisting.BundleJson.AsSpan().SequenceEqual(bundleJson)))
                {
                    throw new CalibrationLibraryStoreConflictException(
                        "The calibration bundle identifier is already assigned to different immutable facts.");
                }
                if (preexisting is null && await ReadBundleCollisionAsync(
                        precheckConnection,
                        precheckTransaction,
                        bundleIdentity,
                        bundle.ProfileRelativePath,
                        cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new CalibrationLibraryStoreConflictException(
                        "Calibration evidence is already assigned to a different immutable bundle.");
                }
                await precheckTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            var evidence = await ValidatePublishedEvidenceAsync(bundle, cancellationToken).ConfigureAwait(false);
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var existing = await ReadBundleAsync(connection, transaction, bundle.BundleId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.BundleIdentitySha256, bundleIdentity, StringComparison.OrdinalIgnoreCase) ||
                    !existing.BundleJson.AsSpan().SequenceEqual(bundleJson))
                {
                    throw new CalibrationLibraryStoreConflictException(
                        "The calibration bundle identifier is already assigned to different immutable facts.");
                }
                await ValidatePersistedArtifactsAsync(
                    connection, transaction, bundle, evidence.Artifacts, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _validatedEvidence[bundleIdentity] = evidence;
                activity?.SetStatus(ActivityStatusCode.Ok);
                return existing.Snapshot;
            }

            var collision = await ReadBundleCollisionAsync(
                connection, transaction, bundleIdentity, bundle.ProfileRelativePath, cancellationToken).ConfigureAwait(false);
            if (collision is not null)
            {
                throw new CalibrationLibraryStoreConflictException(
                    "Calibration evidence is already assigned to a different immutable bundle.");
            }
            var now = Now();
            await InsertBundleAsync(
                connection, transaction, bundle, bundleJson, bundleIdentity, now, cancellationToken).ConfigureAwait(false);
            for (var ordinal = 0; ordinal < bundle.Artifacts.Count; ordinal++)
            {
                await InsertArtifactAsync(
                    connection, transaction, bundle.BundleId, ordinal, bundle.Artifacts[ordinal], evidence.Artifacts[ordinal],
                    cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _validatedEvidence[bundleIdentity] = evidence;
            var snapshot = new CalibrationLibraryBundleSnapshot(
                bundle, bundleIdentity, "published", null, bundle.CreatedUtc, now);
            var telemetryArtifacts = bundle.Artifacts.Select((artifact, index) =>
                new CalibrationTelemetryArtifact(artifact.Kind, evidence.Artifacts[index].PayloadBytes)).ToArray();
            _telemetry?.AddPublishedBundle(
                bundle.Source,
                telemetryArtifacts,
                telemetryArtifacts.Sum(static artifact => artifact.PayloadBytes));
            activity?.SetStatus(ActivityStatusCode.Ok);
            return snapshot;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _telemetry?.RecordValidationFailure("publish", CalibrationLibraryReasonCodes.Corrupt);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            _telemetry?.RecordFailure("publish", CalibrationLibraryReasonCodes.PublicationConflict);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw new CalibrationLibraryStoreConflictException(
                "Calibration evidence conflicts with existing durable library state.", exception);
        }
        finally
        {
            if (storeGateAcquired)
            {
                _gate.Release();
            }
            lifecycleGate.Release();
        }
    }

    public async Task<CalibrationLibraryStateSnapshot> GetStateAsync(CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        var state = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    internal async Task<bool> ContainsPublishedProfileAsync(
        string profileRelativePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRelativePath);
        _ = ResolveSafePath(profileRelativePath);
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM calibration_library_bundles
                    WHERE profile_relative_path = $profile AND publication_state = 'published');
                """;
            command.Parameters.AddWithValue("$profile", profileRelativePath);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CalibrationLibraryBundleSnapshot>> GetBundlesAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        var bundleIds = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT bundle_id FROM calibration_library_bundles
                ORDER BY created_unix_ms DESC, bundle_id LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", maximumCount);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                bundleIds.Add(reader.GetString(0));
            }
        }
        var bundles = new List<CalibrationLibraryBundleSnapshot>(bundleIds.Count);
        foreach (var bundleId in bundleIds)
        {
            var persisted = await ReadBundleAsync(connection, transaction, bundleId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A durable calibration bundle disappeared during the read transaction.");
            bundles.Add(persisted.Snapshot);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return bundles;
    }

    public async Task<CalibrationLibraryBundleSnapshot?> GetBundleAsync(
        string bundleId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bundleId) || bundleId.Length > 128)
        {
            throw new ArgumentException("A valid calibration bundle identifier is required.", nameof(bundleId));
        }
        var persisted = await ReadBundleOutsideTransactionAsync(bundleId, cancellationToken).ConfigureAwait(false);
        return persisted?.Snapshot;
    }

    public async Task<CalibrationLibraryBundlePage> GetBundlePageAsync(
        int pageSize,
        CalibrationLibraryBundleCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        if (cursor is not null && (cursor.CreatedUtc.Offset != TimeSpan.Zero ||
                                   string.IsNullOrWhiteSpace(cursor.BundleId) || cursor.BundleId.Length > 128))
        {
            throw new ArgumentException("The calibration bundle cursor is invalid.", nameof(cursor));
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        var bundleIds = new List<string>(pageSize + 1);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT bundle_id FROM calibration_library_bundles
                WHERE $has_cursor = 0 OR created_unix_ms < $created OR
                      (created_unix_ms = $created AND bundle_id > $bundle)
                ORDER BY created_unix_ms DESC, bundle_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$has_cursor", cursor is null ? 0 : 1);
            command.Parameters.AddWithValue("$created", cursor?.CreatedUtc.ToUnixTimeMilliseconds() ?? 0);
            command.Parameters.AddWithValue("$bundle", cursor?.BundleId ?? string.Empty);
            command.Parameters.AddWithValue("$limit", pageSize + 1);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                bundleIds.Add(reader.GetString(0));
            }
        }
        var hasMore = bundleIds.Count > pageSize;
        if (hasMore)
        {
            bundleIds.RemoveAt(bundleIds.Count - 1);
        }
        var bundles = new List<CalibrationLibraryBundleSnapshot>(bundleIds.Count);
        foreach (var bundleId in bundleIds)
        {
            bundles.Add((await ReadBundleAsync(
                connection, transaction, bundleId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A paged calibration bundle disappeared.")).Snapshot);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var next = hasMore && bundles.Count > 0
            ? new CalibrationLibraryBundleCursor(bundles[^1].CreatedUtc, bundles[^1].Bundle.BundleId)
            : null;
        return new CalibrationLibraryBundlePage(bundles, next);
    }

    public async Task<CalibrationLibraryOperationsStatus> GetOperationsStatusAsync(
        CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        var state = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var published = await ScalarLongAsync(
            connection, transaction,
            "SELECT COUNT(*) FROM calibration_library_bundles WHERE publication_state = 'published';",
            cancellationToken).ConfigureAwait(false);
        var quarantined = await ScalarLongAsync(
            connection, transaction,
            "SELECT COUNT(*) FROM calibration_library_reconciliation WHERE outcome = 'quarantined' AND operation_state = 'completed';",
            cancellationToken).ConfigureAwait(false);
        CalibrationAcquisitionJobSnapshot? pending = null;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT job_id FROM calibration_acquisition_jobs
                WHERE state NOT IN ('published', 'failed', 'cancelled')
                ORDER BY created_unix_ms, job_id LIMIT 1;
                """;
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string jobId)
            {
                pending = await ReadAcquisitionJobAsync(
                    connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            }
        }
        CalibrationLibraryActivationSnapshot? activation = null;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT c.command_kind, a.from_bundle_id, a.to_bundle_id, a.state_version,
                       a.activated_unix_ms, a.reason
                FROM calibration_library_activations a
                JOIN calibration_library_commands c ON c.idempotency_key = a.idempotency_key
                ORDER BY a.activated_unix_ms DESC, a.activation_id DESC LIMIT 1;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                activation = new CalibrationLibraryActivationSnapshot(
                    reader.GetString(0),
                    await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5));
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CalibrationLibraryOperationsStatus(state, pending, published, quarantined, activation);
    }

    public async Task<CalibrationLibraryStateSnapshot> ActivateAsync(
        string bundleId,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => await ChangeActivationAsync(
            bundleId, idempotencyKey, expectedVersion, actor, reason, "activate", cancellationToken)
            .ConfigureAwait(false);

    public async Task<CalibrationLibraryStateSnapshot> RollbackAsync(
        string bundleId,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => await ChangeActivationAsync(
            bundleId, idempotencyKey, expectedVersion, actor, reason, "rollback", cancellationToken)
            .ConfigureAwait(false);

    private async Task<CalibrationLibraryStateSnapshot> ChangeActivationAsync(
        string bundleId,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        string commandKind,
        CancellationToken cancellationToken)
    {
        ValidateMutation(bundleId, idempotencyKey, expectedVersion, actor, reason);
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var payloadSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            command = commandKind,
            bundleId,
            expectedVersion,
            actor,
            reason
        });
        var priorReplay = await ReadCommandOutsideTransactionAsync(
            idempotencyKey, commandKind, cancellationToken).ConfigureAwait(false);
        if (priorReplay is not null)
        {
            return ValidateReplay(priorReplay, payloadSha256);
        }
        var target = await ReadBundleOutsideTransactionAsync(bundleId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The calibration bundle was not found.");
        if (!string.Equals(target.Snapshot.PublicationState, "published", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only a completely published calibration bundle can be activated.");
        }
        await ValidateEvidenceCachedAsync(target.Snapshot, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var replay = await ReadCommandAsync(
                connection, transaction, idempotencyKey, commandKind, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ValidateReplay(replay, payloadSha256);
            }

            var current = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (current.Version != expectedVersion)
            {
                throw new CalibrationLibraryStoreConflictException(
                    "The calibration library state changed after the operator read it.");
            }
            if (commandKind == "rollback" &&
                (string.Equals(current.ActiveBundle?.Bundle.BundleId, bundleId, StringComparison.Ordinal) ||
                 !await WasPreviouslyActiveAsync(
                     connection, transaction, bundleId, current.Version, cancellationToken).ConfigureAwait(false)))
            {
                throw new CalibrationLibraryStoreConflictException(
                    "Rollback requires a different previously active calibration bundle.");
            }
            var nextVersion = current.Version + 1;
            var now = Now();
            var result = current with
            {
                ActiveBundle = target.Snapshot,
                Version = nextVersion,
                UpdatedUtc = now
            };
            var resultJson = JsonSerializer.SerializeToUtf8Bytes(result);
            await ExecuteAsync(connection, transaction, """
                UPDATE calibration_library_state
                SET active_bundle_id = $bundle, version = $version, updated_unix_ms = $now
                WHERE state_key = 1;
                INSERT INTO calibration_library_activations(
                    idempotency_key, from_bundle_id, to_bundle_id, actor, reason,
                    state_version, activated_unix_ms)
                VALUES ($key, $from, $bundle, $actor, $reason, $version, $now);
                INSERT INTO calibration_library_commands(
                    idempotency_key, command_kind, payload_sha256, result_bundle_id,
                    result_state_version, result_json, created_unix_ms, completed_unix_ms)
                VALUES ($key, $command, $payload, $bundle, $version, $result, $now, $now);
                """, cancellationToken,
                ("$bundle", bundleId),
                ("$version", nextVersion),
                ("$now", now.ToUnixTimeMilliseconds()),
                ("$key", idempotencyKey),
                ("$command", commandKind),
                ("$from", (object?)current.ActiveBundle?.Bundle.BundleId ?? DBNull.Value),
                ("$actor", actor),
                ("$reason", (object?)reason ?? DBNull.Value),
                ("$payload", payloadSha256),
                ("$result", resultJson)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            ReplaceActiveValidation(result.ActiveBundle, "calibration.library.selected");
            _telemetry?.RecordActivation(commandKind, "success", result.Version);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationLibrarySelectionResult> SelectAsync(
        ReconstructionDescriptor light,
        CancellationToken cancellationToken)
    {
        using var activity = CalibrationTelemetry.ActivitySource.StartActivity("calibration.select");
        var started = Stopwatch.GetTimestamp();
        ArgumentNullException.ThrowIfNull(light);
        try
        {
            await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
            CalibrationLibraryStateSnapshot state;
            long bundleCount;
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var transaction = BeginRead(connection))
            {
                state = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                bundleCount = await ScalarLongAsync(
                    connection, transaction, "SELECT COUNT(*) FROM calibration_library_bundles WHERE publication_state = 'published';",
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            var reason = SelectReason(state.ActiveBundle, light, bundleCount);
            if (reason is null && state.ActiveBundle is { } active)
            {
                try
                {
                    await ValidateEvidenceCachedAsync(active, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    reason = CalibrationLibraryReasonCodes.Corrupt;
                    SetActiveValidation(state.ActiveBundle, reason);
                    _telemetry?.RecordValidationFailure("select", reason);
                }
            }
            var result = new CalibrationLibrarySelectionResult(
                reason is null ? state.ActiveBundle : null,
                reason ?? "calibration.library.selected",
                state.Version);
            if (result.IsSelected)
            {
                SetActiveValidation(result.Bundle, "calibration.library.selected");
            }
            var recordSelection = ShouldRecordSelection(result.ReasonCode);
            if (recordSelection)
            {
                await RecordSelectionAsync(result.ReasonCode, cancellationToken).ConfigureAwait(false);
            }
            _telemetry?.RecordSelection(
                result.ReasonCode, Stopwatch.GetElapsedTime(started), result.StateVersion, recordSelection);
            activity?.SetTag("calibration.outcome", result.IsSelected ? "selected" : "rejected");
            activity?.SetTag("calibration.reason", CalibrationTelemetry.NormalizeReason(result.ReasonCode));
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _telemetry?.RecordFailure("select", "other");
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    public async Task<string> ValidateActiveBundleAsync(CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        UpdateActiveValidationIdentity(state);
        if (state.ActiveBundle is not { } active)
        {
            SetActiveValidation(bundle: null, CalibrationLibraryReasonCodes.Inactive);
            return CalibrationLibraryReasonCodes.Inactive;
        }
        if (!string.Equals(active.PublicationState, "published", StringComparison.Ordinal))
        {
            var reason = active.PublicationState == "incomplete"
                ? CalibrationLibraryReasonCodes.Incomplete
                : CalibrationLibraryReasonCodes.Corrupt;
            SetActiveValidation(active, reason);
            return reason;
        }
        try
        {
            await ValidateEvidenceCachedAsync(active, cancellationToken).ConfigureAwait(false);
            SetActiveValidation(active, "calibration.library.selected");
            return "calibration.library.selected";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            SetActiveValidation(active, CalibrationLibraryReasonCodes.Corrupt);
            _telemetry?.RecordValidationFailure("select", CalibrationLibraryReasonCodes.Corrupt);
            return CalibrationLibraryReasonCodes.Corrupt;
        }
    }

    public CalibrationActiveValidationSnapshot ActiveBundleValidation
    {
        get
        {
            lock (_activeValidationGate)
            {
                return new CalibrationActiveValidationSnapshot(
                    _activeValidationBundleIdentity,
                    _activeValidationReason);
            }
        }
    }

    public async ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
    {
        if (!PathsEqual(_storageRoot, storageRoot))
        {
            return [];
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var holds = new List<ProcessingRetentionHold>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT a.artifact_id, a.payload_relative_path, a.manifest_relative_path
                    FROM calibration_library_artifacts a
                    JOIN calibration_library_bundles b ON b.bundle_id = a.bundle_id
                    WHERE b.retention_hold = 1
                    ORDER BY a.bundle_id, a.ordinal;
                    """;
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    _ = ResolveSafePath(reader.GetString(1));
                    _ = ResolveSafePath(reader.GetString(2));
                    holds.Add(new ProcessingRetentionHold(
                        Guid.ParseExact(reader.GetString(0), "N"),
                        reader.GetString(1),
                        reader.GetString(2)));
                }
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT bundle_identity_sha256, profile_relative_path, source
                    FROM calibration_library_bundles WHERE retention_hold = 1
                    ORDER BY bundle_id;
                    """;
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var profilePath = reader.GetString(1);
                    _ = ResolveSafePath(profilePath);
                    holds.Add(new ProcessingRetentionHold(
                        Guid.ParseExact(reader.GetString(0)[..32], "N"),
                        profilePath,
                        profilePath));
                    if (string.Equals(
                            reader.GetString(2),
                            CalibrationLibraryBundleSources.SyntheticReferencesV1,
                            StringComparison.Ordinal))
                    {
                        var separator = profilePath.LastIndexOf('/');
                        var bundlePath = string.Concat(
                            profilePath.AsSpan(0, separator + 1),
                            CalibrationLibraryEvidenceNames.BundleEnvelope);
                        _ = ResolveSafePath(bundlePath);
                        holds.Add(new ProcessingRetentionHold(
                            Guid.ParseExact(reader.GetString(0)[32..], "N"),
                            bundlePath,
                            bundlePath));
                    }
                }
            }
            return holds;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static async Task<CalibrationTelemetryInventory> ReadTelemetryInventoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var bundles = new Dictionary<(string State, string Source), long>();
        var referenceBytes = new Dictionary<(string Kind, string State), long>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT bundle_json, publication_state, source
                FROM calibration_library_bundles;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var state = reader.GetString(1);
                var source = reader.GetString(2);
                bundles[(state, source)] = bundles.GetValueOrDefault((state, source)) + 1;
                var bundle = CalibrationLibraryContractJson.Parse((byte[])reader.GetValue(0)).Value
                    ?? throw new InvalidDataException("A durable calibration bundle is invalid.");
                foreach (var artifact in bundle.Artifacts)
                {
                    var key = (artifact.Kind, state);
                    var bytes = artifact.Role == CalibrationLibraryArtifactRoles.Source
                        ? bundle.Applicability.InputLayout.ByteLength
                        : bundle.Applicability.OutputLayout.ByteLength;
                    referenceBytes[key] = checked(referenceBytes.GetValueOrDefault(key) + bytes);
                }
            }
        }
        return new CalibrationTelemetryInventory(bundles, referenceBytes);
    }

    private void UpdateActiveValidationIdentity(CalibrationLibraryStateSnapshot state)
    {
        lock (_activeValidationGate)
        {
            var identity = state.ActiveBundle?.BundleIdentitySha256;
            if (string.Equals(identity, _activeValidationBundleIdentity, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _activeValidationBundleIdentity = identity;
            _activeValidationReason = identity is null
                ? CalibrationLibraryReasonCodes.Inactive
                : "calibration.library.validation-pending";
        }
    }

    private void SetActiveValidation(CalibrationLibraryBundleSnapshot? bundle, string reason)
    {
        lock (_activeValidationGate)
        {
            if (!string.Equals(
                    bundle?.BundleIdentitySha256,
                    _activeValidationBundleIdentity,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _activeValidationReason = reason;
        }
    }

    private void ReplaceActiveValidation(CalibrationLibraryBundleSnapshot? bundle, string reason)
    {
        lock (_activeValidationGate)
        {
            _activeValidationBundleIdentity = bundle?.BundleIdentitySha256;
            _activeValidationReason = reason;
        }
    }

    private async Task<PublishedBundleEvidence> ValidatePublishedEvidenceAsync(
        CalibrationLibraryBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        var virtualPlan = bundle.Source == CalibrationLibraryBundleSources.VirtualAcquisitionV1
            ? await ReadVirtualAcquisitionPlanAsync(bundle, cancellationToken).ConfigureAwait(false)
            : null;
        var profilePath = ResolveSafePath(bundle.ProfileRelativePath);
        if (!File.Exists(profilePath))
        {
            throw new InvalidDataException("The committed calibration profile marker is missing.");
        }
        var profileEvidence = await ReadStableFileAsync(profilePath, cancellationToken).ConfigureAwait(false);
        var profileJson = profileEvidence.Bytes;
        if (!string.Equals(
                PayloadChecksum.ComputeSha256(profileJson), bundle.ProfileIdentitySha256,
                StringComparison.OrdinalIgnoreCase) || ReferenceCalibrationProfileJson.Parse(profileJson) is not { } profile)
        {
            throw new InvalidDataException("The committed calibration profile marker is invalid.");
        }

        ValidateProfile(profile, bundle);
        var evidence = new List<PublishedArtifactEvidence>(bundle.Artifacts.Count);
        var files = new List<EvidenceFileFingerprint>(2 + bundle.Artifacts.Count * 2);
        if (bundle.Source == CalibrationLibraryBundleSources.SyntheticReferencesV1)
        {
            var separator = bundle.ProfileRelativePath.LastIndexOf('/');
            var bundleRelativePath = string.Concat(
                bundle.ProfileRelativePath.AsSpan(0, separator + 1),
                CalibrationLibraryEvidenceNames.BundleEnvelope);
            var bundleEvidence = await ReadStableFileAsync(
                ResolveSafePath(bundleRelativePath), cancellationToken).ConfigureAwait(false);
            if (!bundleEvidence.Bytes.AsSpan().SequenceEqual(CalibrationLibraryContractJson.Serialize(bundle)))
            {
                throw new InvalidDataException("The canonical synthetic calibration bundle envelope is invalid.");
            }
            files.Add(bundleEvidence.Fingerprint);
        }
        files.Add(profileEvidence.Fingerprint);
        foreach (var artifact in bundle.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = ResolveSafePath(artifact.ManifestRelativePath);
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException("A committed calibration manifest is missing.");
            }
            var manifestEvidence = await ReadStableFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var manifestJson = manifestEvidence.Bytes;
            var parsed = CaptureContractJson.ParseManifest(manifestJson);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest ||
                manifest.Descriptor.Artifact.ArtifactId != artifact.ArtifactId ||
                !string.Equals(
                    manifest.Descriptor.Artifact.ChecksumSha256, artifact.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                manifest.Descriptor.Controls.EffectiveExposure != artifact.Exposure ||
                manifest.Descriptor.Controls.EffectiveGain != artifact.Gain ||
                manifest.Descriptor.Controls.EffectiveOffset != artifact.Offset ||
                manifest.Descriptor.Controls.EffectiveTemperatureC != artifact.TemperatureC ||
                !RequestedControlsMatch(bundle.Source, artifact, manifest.Descriptor.Controls) ||
                !string.Equals(
                    manifest.Descriptor.Capture.AgentId,
                    bundle.Applicability.AgentId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.Descriptor.Capture.RigId,
                    bundle.Applicability.RigId,
                    StringComparison.Ordinal) ||
                !string.Equals(manifest.Descriptor.Artifact.Variant, artifact.Kind, StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.RelativeArtifactPath,
                    PayloadPathForManifest(artifact.ManifestRelativePath),
                    StringComparison.Ordinal) ||
                !ManifestRoleMatches(bundle.Source, artifact.Role, manifest.Descriptor.Artifact.Role) ||
                !CaptureSequenceMatches(
                    bundle, artifact, manifest.Descriptor.Capture.CaptureSequence) ||
                !string.Equals(
                    manifest.Descriptor.Profiles.Rig.Sha256,
                    bundle.Applicability.RigProfileSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    manifest.Descriptor.Profiles.Sensor.Sha256,
                    bundle.Applicability.SensorProfileSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !AcquisitionModelMatchesLight(bundle, manifest.Descriptor.Profiles.Calibration) ||
                !string.Equals(
                    manifest.Descriptor.Profiles.Calibration.Sha256,
                    bundle.AcquisitionModelIdentitySha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !ManifestLayoutMatches(bundle, artifact, manifest.Descriptor.Layout) ||
                !manifest.Descriptor.Artifact.SourceArtifactIds.SequenceEqual(artifact.OrderedSourceArtifactIds) ||
                !RecipeMatches(bundle, artifact, manifest.Descriptor.Artifact.Recipe))
            {
                throw new InvalidDataException("A committed calibration manifest conflicts with its library envelope.");
            }
            var payloadPath = ResolveSafePath(manifest.RelativeArtifactPath);
            if (!File.Exists(payloadPath))
            {
                throw new InvalidDataException("A committed calibration payload is missing.");
            }
            var payloadEvidence = await ReadStableFileAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            var payload = payloadEvidence.Bytes;
            if (!string.Equals(PayloadChecksum.ComputeSha256(payload), artifact.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
                !FrameReconstructor.TryReconstruct(manifest.Descriptor, payload, out _).IsValid ||
                bundle.Source == CalibrationLibraryBundleSources.VirtualAcquisitionV1 &&
                artifact.Role == CalibrationLibraryArtifactRoles.Source &&
                !ValidNativeSourcePayload(payload, bundle.Applicability.InputLayout))
            {
                throw new InvalidDataException("A committed calibration payload is corrupt.");
            }
            var manifestSha256 = PayloadChecksum.ComputeSha256(manifestJson);
            evidence.Add(new PublishedArtifactEvidence(
                manifest.RelativeArtifactPath, manifestSha256, payloadEvidence.Fingerprint.Length));
            files.Add(manifestEvidence.Fingerprint);
            files.Add(payloadEvidence.Fingerprint);
        }

        if (bundle.Source == CalibrationLibraryBundleSources.VirtualAcquisitionV1)
        {
            await ValidateVirtualMasterEvidenceAsync(
                bundle,
                profile,
                virtualPlan ?? throw new InvalidDataException("The virtual acquisition plan is missing."),
                cancellationToken).ConfigureAwait(false);
        }

        return new PublishedBundleEvidence(evidence, files, Now().ToUnixTimeMilliseconds());
    }

    private static void ValidateProfile(
        ReferenceCalibrationProfileV1 profile,
        CalibrationLibraryBundleV1 bundle)
    {
        if (profile.References is null || profile.References.Any(static reference => reference is null))
        {
            throw new InvalidDataException("The calibration profile has invalid reference entries.");
        }
        var applicability = bundle.Applicability;
        var masters = bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master);
        if (!string.Equals(
                profile.SchemaVersion,
                ReferenceCalibrationProfileV1.CurrentSchemaVersion,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(profile.ProfileId) || profile.ProfileId.Length > 128 ||
            string.IsNullOrWhiteSpace(profile.ProfileVersion) || profile.ProfileVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(profile.Source) || profile.Source.Length > 512 ||
            profile.Width != applicability.OutputLayout.Width ||
            profile.Height != applicability.OutputLayout.Height ||
            profile.PixelFormat != applicability.OutputLayout.PixelFormat ||
            profile.FlatNormalizationAdu <= 0 ||
            profile.MinimumGain != applicability.MinimumGain ||
            profile.MaximumGain != applicability.MaximumGain ||
            profile.MinimumTemperatureC != applicability.MinimumTemperatureC ||
            profile.MaximumTemperatureC != applicability.MaximumTemperatureC ||
            profile.EffectiveFromUtc != applicability.EffectiveFromUtc ||
            profile.EffectiveUntilUtc != applicability.EffectiveUntilUtc ||
            profile.References.Count != CalibrationReferenceKinds.All.Count ||
            profile.References.Select(static reference => reference.Kind).Distinct(StringComparer.Ordinal).Count() !=
                CalibrationReferenceKinds.All.Count ||
            profile.References.Select(static reference => reference.ArtifactId).Distinct().Count() !=
                CalibrationReferenceKinds.All.Count ||
            profile.References.Any(reference => InvalidProfileReference(profile, reference)) ||
            masters.Any(master => !profile.References.Any(reference =>
                reference.Kind == master.Kind &&
                reference.ArtifactId == master.ArtifactId &&
                string.Equals(reference.PayloadSha256, master.PayloadSha256, StringComparison.OrdinalIgnoreCase) &&
                reference.Exposure == master.Exposure &&
                reference.Gain == master.Gain &&
                reference.TemperatureC == master.TemperatureC)))
        {
            throw new InvalidDataException("The calibration profile conflicts with its library envelope.");
        }
    }

    private static bool InvalidProfileReference(
        ReferenceCalibrationProfileV1 profile,
        CalibrationReferenceDescriptorV1 reference)
        => !CalibrationReferenceKinds.All.Contains(reference.Kind, StringComparer.Ordinal) ||
           reference.ArtifactId == Guid.Empty || reference.Exposure <= TimeSpan.Zero ||
           !double.IsFinite(reference.Gain) || reference.Gain < 0 ||
           reference.TemperatureC is not { } temperature || !double.IsFinite(temperature) ||
           string.IsNullOrWhiteSpace(reference.PayloadSha256) || reference.PayloadSha256.Length != 64 ||
           !reference.PayloadSha256.All(Uri.IsHexDigit) ||
           temperature < profile.MinimumTemperatureC || temperature > profile.MaximumTemperatureC ||
           reference.Gain < profile.MinimumGain || reference.Gain > profile.MaximumGain;

    private static bool ManifestLayoutMatches(
        CalibrationLibraryBundleV1 bundle,
        CalibrationLibraryArtifactV1 artifact,
        FrameLayoutDescriptor actual)
    {
        var expected = artifact.Role == CalibrationLibraryArtifactRoles.Source
            ? bundle.Applicability.InputLayout
            : bundle.Applicability.OutputLayout;
        return NormalizeCompleteLayout(expected) == NormalizeCompleteLayout(actual);
    }

    private static FrameLayoutDescriptor NormalizeCompleteLayout(FrameLayoutDescriptor layout)
        => layout.SampleDepthBits == layout.ContainerDepthBits
            ? layout with
            {
                StoredCodeTransform = layout.StoredCodeTransform ?? FrameStoredCodeTransform.IdentityV1,
                LevelCodeSpace = layout.LevelCodeSpace ?? FrameLevelCodeSpace.StoredContainer
            }
            : layout;

    private static bool ManifestRoleMatches(string bundleSource, string role, FrameArtifactRole manifestRole)
        => bundleSource switch
        {
            CalibrationLibraryBundleSources.SyntheticReferencesV1 =>
                role == CalibrationLibraryArtifactRoles.Master && manifestRole == FrameArtifactRole.Raw,
            CalibrationLibraryBundleSources.VirtualAcquisitionV1 =>
                role == CalibrationLibraryArtifactRoles.Source && manifestRole == FrameArtifactRole.Raw ||
                role == CalibrationLibraryArtifactRoles.Master && manifestRole == FrameArtifactRole.Combined,
            _ => false
        };

    private static bool RequestedControlsMatch(
        string bundleSource,
        CalibrationLibraryArtifactV1 artifact,
        CaptureControlDescriptor controls)
        => bundleSource is CalibrationLibraryBundleSources.SyntheticReferencesV1 or
               CalibrationLibraryBundleSources.VirtualAcquisitionV1 &&
            controls.RequestedExposure == artifact.Exposure &&
            controls.RequestedGain == artifact.Gain &&
            controls.RequestedOffset == artifact.Offset &&
            controls.TemperatureSetpointC == artifact.TemperatureC;

    private static bool CaptureSequenceMatches(
        CalibrationLibraryBundleV1 bundle,
        CalibrationLibraryArtifactV1 artifact,
        long actual)
    {
        if (bundle.Source == CalibrationLibraryBundleSources.SyntheticReferencesV1)
        {
            var publicationIdentity = SyntheticPublicationIdentity(bundle);
            if (publicationIdentity is null)
            {
                return false;
            }
            var syntheticHash = SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{publicationIdentity}:sequence:{artifact.Kind}"));
            const ulong syntheticReservedStart = (ulong)long.MaxValue / 2;
            var offset = BinaryPrimitives.ReadUInt64BigEndian(syntheticHash) % syntheticReservedStart;
            return actual == checked((long)(syntheticReservedStart + offset));
        }
        if (bundle.Source != CalibrationLibraryBundleSources.VirtualAcquisitionV1)
        {
            return true;
        }
        var jobId = bundle.BundleId["bundle-".Length..];
        var kindIndex = -1;
        for (var index = 0; index < CalibrationReferenceKinds.All.Count; index++)
        {
            if (string.Equals(CalibrationReferenceKinds.All[index], artifact.Kind, StringComparison.Ordinal))
            {
                kindIndex = index;
                break;
            }
        }
        if (kindIndex < 0)
        {
            return false;
        }
        var ordinal = artifact.Role == CalibrationLibraryArtifactRoles.Source && artifact.SourceIndex is { } sourceIndex
            ? kindIndex * CalibrationMasterBuilder.RequiredSourceCount + sourceIndex + 1
            : artifact.Role == CalibrationLibraryArtifactRoles.Master ? 13 + kindIndex : -1;
        if (ordinal is < 1 or > 16)
        {
            return false;
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{jobId}:capture-sequence"));
        const ulong reservedStart = (ulong)long.MaxValue / 2;
        const ulong blockSize = 16;
        var blockCount = reservedStart / blockSize;
        var block = BinaryPrimitives.ReadUInt64BigEndian(hash) % blockCount;
        return actual == checked((long)(reservedStart + block * blockSize + (uint)(ordinal - 1)));
    }

    private static bool RecipeMatches(
        CalibrationLibraryBundleV1 bundle,
        CalibrationLibraryArtifactV1 artifact,
        RecipeIdentityDescriptor actual)
    {
        if (artifact.Role == CalibrationLibraryArtifactRoles.Master)
        {
            if (artifact.MasterBuildRecipe is { } expected)
            {
                return RecipeIdentityMatches(expected, actual);
            }
            if (bundle.Source == CalibrationLibraryBundleSources.SyntheticReferencesV1)
            {
                var syntheticReference = RecipeIdentityDescriptor.Create(
                    "synthetic-calibration-reference",
                    "1.0.0",
                    SyntheticCalibrationReferenceGenerator.AlgorithmVersion,
                    CaptureContractJson.SerializeToElement(new
                    {
                        schemaVersion = SyntheticCalibrationModelV1.CurrentSchemaVersion,
                        modelIdentitySha256 = bundle.AcquisitionModelIdentitySha256,
                        referenceKind = artifact.Kind,
                        generator = SyntheticCalibrationReferenceGenerator.AlgorithmVersion
                    }));
                return RecipeIdentityMatches(syntheticReference, actual);
            }
            return false;
        }
        if (artifact.MasterBuildRecipe is not null)
        {
            return false;
        }
        if (bundle.Source != CalibrationLibraryBundleSources.VirtualAcquisitionV1 || artifact.SourceIndex is not { } sourceIndex)
        {
            return true;
        }
        var expectedSource = RecipeIdentityDescriptor.Create(
            "virtual-calibration-source",
            "1.0.0",
            VirtualCalibrationSourceGenerator.AlgorithmVersion,
            JsonSerializer.SerializeToElement(new
            {
                schemaVersion = VirtualCalibrationSourceModelV1.CurrentSchemaVersion,
                sourceModelIdentitySha256 = bundle.AcquisitionModelIdentitySha256,
                referenceKind = artifact.Kind,
                sourceIndex
            }));
        return RecipeIdentityMatches(expectedSource, actual);
    }

    private static bool RecipeIdentityMatches(RecipeIdentityDescriptor expected, RecipeIdentityDescriptor actual)
        => string.Equals(expected.Name, actual.Name, StringComparison.Ordinal) &&
           string.Equals(expected.SemanticVersion, actual.SemanticVersion, StringComparison.Ordinal) &&
           string.Equals(expected.ImplementationVersion, actual.ImplementationVersion, StringComparison.Ordinal) &&
           string.Equals(expected.OptionsSha256, actual.OptionsSha256, StringComparison.OrdinalIgnoreCase);

    private static bool ValidNativeSourcePayload(ReadOnlySpan<byte> payload, FrameLayoutDescriptor layout)
    {
        var nativeMaximum = (1u << layout.SampleDepthBits) - 1u;
        for (var y = 0; y < layout.Height; y++)
        {
            for (var x = 0; x < layout.Width; x++)
            {
                var offset = checked(y * layout.StrideBytes + x * 2);
                var stored = (ushort)(payload[offset] | payload[offset + 1] << 8);
                if (stored > nativeMaximum)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private async Task ValidateVirtualMasterEvidenceAsync(
        CalibrationLibraryBundleV1 bundle,
        ReferenceCalibrationProfileV1 profile,
        VirtualCalibrationAcquisitionPlanV1 plan,
        CancellationToken cancellationToken)
    {
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceArtifacts = bundle.Artifacts
                .Where(artifact => artifact.Role == CalibrationLibraryArtifactRoles.Source && artifact.Kind == kind)
                .OrderBy(static artifact => artifact.SourceIndex)
                .ToArray();
            var masterArtifact = bundle.Artifacts.Single(
                artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master && artifact.Kind == kind);
            if (sourceArtifacts.Length != CalibrationMasterBuilder.RequiredSourceCount ||
                sourceArtifacts.Where((artifact, index) =>
                    artifact.SourceIndex != index ||
                    artifact.Exposure != plan.ExposureFor(kind) ||
                    artifact.Gain != plan.Gain ||
                    artifact.Offset != plan.Offset ||
                    artifact.TemperatureC != plan.TemperatureC ||
                    !string.Equals(
                        artifact.ManifestRelativePath,
                        $"calibration/virtual/{plan.JobId}/sources/{kind}-{index}.json",
                        StringComparison.Ordinal)).Any() ||
                !string.Equals(
                    masterArtifact.ManifestRelativePath,
                    $"calibration/virtual/{plan.JobId}/masters/{kind}.json",
                    StringComparison.Ordinal) ||
                masterArtifact.Exposure != plan.ExposureFor(kind) ||
                masterArtifact.Gain != plan.Gain ||
                masterArtifact.Offset != plan.Offset ||
                masterArtifact.TemperatureC != plan.TemperatureC)
            {
                throw new InvalidDataException("Virtual calibration artifact paths or source indexes are invalid.");
            }
            var sourceFrames = new List<CalibrationSourceFrame>(sourceArtifacts.Length);
            foreach (var sourceArtifact in sourceArtifacts)
            {
                var source = await ReadStableFileAsync(
                    ResolveSafePath(PayloadPathForManifest(sourceArtifact.ManifestRelativePath)), cancellationToken)
                    .ConfigureAwait(false);
                var generated = VirtualCalibrationSourceGenerator.Generate(
                    ToVirtualSourceKind(kind),
                    sourceArtifact.SourceIndex ?? throw new InvalidDataException("A virtual source index is missing."),
                    plan.InputLayout,
                    plan.ExposureFor(kind),
                    plan.Gain,
                    plan.Offset,
                    plan.TemperatureC,
                    plan.SourceModel,
                    cancellationToken);
                if (!source.Bytes.AsSpan().SequenceEqual(generated.PixelData.Span))
                {
                    throw new InvalidDataException(
                        "Virtual calibration source evidence is not reproducible from its durable plan.");
                }
                sourceFrames.Add(new CalibrationSourceFrame(bundle.Applicability.InputLayout, source.Bytes));
            }
            var rebuilt = kind == CalibrationReferenceKinds.Defect
                ? CalibrationMasterBuilder.BuildDefectMask(sourceFrames, cancellationToken)
                : CalibrationMasterBuilder.BuildMedian(sourceFrames, cancellationToken);
            if (!string.Equals(
                    PayloadChecksum.ComputeSha256(rebuilt.PixelData.Span),
                    masterArtifact.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                kind == CalibrationReferenceKinds.Flat &&
                CalibrationMasterBuilder.CalculateFlatNormalization(rebuilt.PixelData.Span) != profile.FlatNormalizationAdu)
            {
                throw new InvalidDataException("Virtual calibration master evidence is not reproducible from its sources.");
            }
        }
    }

    private async Task<VirtualCalibrationAcquisitionPlanV1> ReadVirtualAcquisitionPlanAsync(
        CalibrationLibraryBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        const string prefix = "bundle-";
        if (!bundle.BundleId.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The virtual calibration bundle identifier is invalid.");
        }
        var jobId = bundle.BundleId[prefix.Length..];
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT plan_json FROM calibration_acquisition_jobs WHERE job_id = $job;";
        command.Parameters.AddWithValue("$job", jobId);
        var planJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]
            ?? throw new InvalidDataException("The virtual calibration bundle has no durable acquisition plan.");
        var plan = VirtualCalibrationAcquisitionContractJson.ParsePlan(planJson);
        var expectedApplicability = new CalibrationApplicabilityV1(
            plan.AgentId,
            plan.RigId,
            plan.RigProfile.Sha256,
            plan.SensorProfile.Sha256,
            plan.InputLayout,
            plan.OutputLayout,
            plan.Gain,
            plan.Gain,
            plan.Offset,
            plan.Offset,
            plan.ApplicableLightExposure,
            plan.ApplicableLightExposure,
            plan.TemperatureC,
            plan.TemperatureC,
            plan.EffectiveFromUtc,
            plan.EffectiveUntilUtc);
        if (!string.Equals(bundle.BundleId, $"bundle-{plan.JobId}", StringComparison.Ordinal) ||
            bundle.CreatedUtc != plan.CreatedUtc ||
            !string.Equals(
                bundle.ProfileRelativePath,
                $"calibration/virtual/{plan.JobId}/reference-calibration-profile.json",
                StringComparison.Ordinal) ||
            !string.Equals(
                bundle.AcquisitionModelIdentitySha256,
                plan.SourceModelIdentitySha256,
                StringComparison.OrdinalIgnoreCase) ||
            bundle.Applicability != expectedApplicability)
        {
            throw new InvalidDataException("The virtual calibration bundle conflicts with its durable acquisition plan.");
        }
        return plan;
    }

    private static VirtualCalibrationSourceKind ToVirtualSourceKind(string kind)
        => kind switch
        {
            CalibrationReferenceKinds.Bias => VirtualCalibrationSourceKind.Bias,
            CalibrationReferenceKinds.Dark => VirtualCalibrationSourceKind.Dark,
            CalibrationReferenceKinds.Flat => VirtualCalibrationSourceKind.Flat,
            CalibrationReferenceKinds.Defect => VirtualCalibrationSourceKind.Defect,
            _ => throw new InvalidDataException("The virtual calibration reference kind is invalid.")
        };

    private static string PayloadPathForManifest(string manifestPath)
        => string.Concat(manifestPath.AsSpan(0, manifestPath.Length - ".json".Length), ".bin");

    private async Task ValidateEvidenceCachedAsync(
        CalibrationLibraryBundleSnapshot bundle,
        CancellationToken cancellationToken)
    {
        if (_validatedEvidence.TryGetValue(bundle.BundleIdentitySha256, out var cached) &&
            Now().ToUnixTimeMilliseconds() - cached.ValidatedUnixMs < TimeSpan.FromMinutes(10).TotalMilliseconds &&
            cached.Files.All(FingerprintMatches))
        {
            return;
        }
        var evidence = await ValidatePublishedEvidenceAsync(bundle.Bundle, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        await ValidatePersistedArtifactsAsync(
            connection, transaction, bundle.Bundle, evidence.Artifacts, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _validatedEvidence[bundle.BundleIdentitySha256] = evidence;
    }

    private bool FingerprintMatches(EvidenceFileFingerprint fingerprint)
    {
        var path = ResolveSafePath(fingerprint.RelativePath);
        var info = new FileInfo(path);
        return info.Exists && info.Length == fingerprint.Length &&
               info.LastWriteTimeUtc.Ticks == fingerprint.LastWriteUtcTicks;
    }

    private async Task<StableEvidenceFile> ReadStableFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var before = new FileInfo(path);
        if (!before.Exists || before.Length > int.MaxValue)
        {
            throw new InvalidDataException("Calibration evidence is missing or too large.");
        }
        var bytes = new byte[checked((int)before.Length)];
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        var after = new FileInfo(path);
        if (!after.Exists || before.Length != after.Length ||
            before.LastWriteTimeUtc != after.LastWriteTimeUtc)
        {
            throw new InvalidDataException("Calibration evidence changed while it was being validated.");
        }
        return new StableEvidenceFile(bytes, new EvidenceFileFingerprint(
            Path.GetRelativePath(_storageRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
            after.Length,
            after.LastWriteTimeUtc.Ticks));
    }

    private static string? SelectReason(
        CalibrationLibraryBundleSnapshot? active,
        ReconstructionDescriptor light,
        long publishedBundleCount)
    {
        if (active is null)
        {
            return publishedBundleCount == 0
                ? CalibrationLibraryReasonCodes.Missing
                : CalibrationLibraryReasonCodes.Inactive;
        }
        if (!string.Equals(active.PublicationState, "published", StringComparison.Ordinal))
        {
            return active.PublicationState switch
            {
                "incomplete" => CalibrationLibraryReasonCodes.Incomplete,
                "corrupt" => CalibrationLibraryReasonCodes.Corrupt,
                _ => CalibrationLibraryReasonCodes.Inactive
            };
        }
        var applicability = active.Bundle.Applicability;
        if (!string.Equals(applicability.AgentId, light.Capture.AgentId, StringComparison.Ordinal) ||
            !string.Equals(applicability.RigId, light.Capture.RigId, StringComparison.Ordinal) ||
            !string.Equals(applicability.RigProfileSha256, light.Profiles.Rig.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(applicability.SensorProfileSha256, light.Profiles.Sensor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return CalibrationLibraryReasonCodes.IncompatibleIdentity;
        }
        if (!AcquisitionModelMatchesLight(active.Bundle, light.Profiles.Calibration))
        {
            return CalibrationLibraryReasonCodes.IncompatibleIdentity;
        }
        if (!ReadoutMatches(applicability.InputLayout, light.Layout))
        {
            return CalibrationLibraryReasonCodes.IncompatibleReadout;
        }
        if (!CodeSpaceMatches(applicability.InputLayout, light.Layout))
        {
            return CalibrationLibraryReasonCodes.IncompatibleCodeSpace;
        }
        if (light.Controls.EffectiveGain < applicability.MinimumGain ||
            light.Controls.EffectiveGain > applicability.MaximumGain ||
            !NullableRangeMatches(
                light.Controls.EffectiveOffset, applicability.MinimumOffset, applicability.MaximumOffset) ||
            !NullableRangeMatches(
                light.Controls.EffectiveTemperatureC,
                applicability.MinimumTemperatureC,
                applicability.MaximumTemperatureC))
        {
            return CalibrationLibraryReasonCodes.IncompatibleConditions;
        }
        if (applicability.MinimumLightExposure is { } minimumExposure &&
            (light.Controls.EffectiveExposure < minimumExposure ||
             light.Controls.EffectiveExposure > applicability.MaximumLightExposure!.Value))
        {
            return CalibrationLibraryReasonCodes.IncompatibleExposure;
        }
        var observed = light.Timing.ExposureStartedUtc;
        if (observed < applicability.EffectiveFromUtc ||
            applicability.EffectiveUntilUtc is { } until && observed >= until)
        {
            return CalibrationLibraryReasonCodes.Stale;
        }
        return null;
    }

    private static bool AcquisitionModelMatchesLight(
        CalibrationLibraryBundleV1 bundle,
        ProfileIdentityDescriptor calibrationProfile)
    {
        var expected = bundle.Source switch
        {
            CalibrationLibraryBundleSources.SyntheticReferencesV1 =>
                ("synthetic-calibration-model", SyntheticCalibrationModelV1.CurrentSchemaVersion),
            CalibrationLibraryBundleSources.VirtualAcquisitionV1 =>
                ("virtual-calibration-source-model", VirtualCalibrationSourceModelV1.CurrentSchemaVersion),
            _ => (string.Empty, string.Empty)
        };
        return string.Equals(calibrationProfile.Name, expected.Item1, StringComparison.Ordinal) &&
               string.Equals(calibrationProfile.Version, expected.Item2, StringComparison.Ordinal) &&
               string.Equals(
                   calibrationProfile.Sha256,
                   bundle.AcquisitionModelIdentitySha256,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? SyntheticPublicationIdentity(CalibrationLibraryBundleV1 bundle)
    {
        var segments = bundle.ProfileRelativePath.Split('/');
        var identity = segments.Length >= 2 ? segments[^2] : null;
        return identity is { Length: 64 } && identity.All(Uri.IsHexDigit) ? identity : null;
    }

    private static bool ReadoutMatches(FrameLayoutDescriptor expected, FrameLayoutDescriptor actual)
        => expected.Width == actual.Width && expected.Height == actual.Height &&
           expected.StrideBytes == actual.StrideBytes && expected.PixelFormat == actual.PixelFormat &&
           expected.CfaPattern == actual.CfaPattern && expected.Readout == actual.Readout;

    private static bool CodeSpaceMatches(FrameLayoutDescriptor expected, FrameLayoutDescriptor actual)
        => expected.ByteOrder == actual.ByteOrder && expected.SampleDepthBits == actual.SampleDepthBits &&
           expected.ContainerDepthBits == actual.ContainerDepthBits && expected.Packing == actual.Packing &&
           expected.BlackLevel == actual.BlackLevel && expected.WhiteLevel == actual.WhiteLevel &&
           expected.ByteLength == actual.ByteLength && expected.StoredCodeTransform == actual.StoredCodeTransform &&
           expected.LevelCodeSpace == actual.LevelCodeSpace;

    private static bool NullableRangeMatches(double? value, double? minimum, double? maximum)
        => minimum is null
            ? value is null
            : value is { } actual && actual >= minimum.Value && actual <= maximum!.Value;

    private async Task RecordSelectionAsync(string reasonCode, CancellationToken cancellationToken)
    {
        var now = Now();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction: null, """
            UPDATE calibration_library_state
            SET last_selection_reason = $reason, last_selection_unix_ms = $now
            WHERE state_key = 1;
            """, cancellationToken,
            ("$reason", reasonCode),
            ("$now", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
    }

    private bool ShouldRecordSelection(string reasonCode)
    {
        var now = Now().ToUnixTimeMilliseconds();
        lock (_selectionRecordGate)
        {
            if (string.Equals(_lastRecordedSelectionReason, reasonCode, StringComparison.Ordinal) &&
                now - _lastRecordedSelectionUnixMs < 60_000)
            {
                return false;
            }
            _lastRecordedSelectionReason = reasonCode;
            _lastRecordedSelectionUnixMs = now;
            return true;
        }
    }

    private async Task<PersistedBundle?> ReadBundleOutsideTransactionAsync(
        string bundleId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        var bundle = await ReadBundleAsync(connection, transaction, bundleId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return bundle;
    }

    private static async Task InsertBundleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CalibrationLibraryBundleV1 bundle,
        byte[] bundleJson,
        string bundleIdentity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var applicability = bundle.Applicability;
        await ExecuteAsync(connection, transaction, """
            INSERT INTO calibration_library_bundles(
                bundle_id, bundle_identity_sha256, source, bundle_json, profile_relative_path,
                profile_identity_sha256, acquisition_model_identity_sha256, agent_id, rig_id,
                rig_profile_sha256, sensor_profile_sha256, input_layout_sha256, output_layout_sha256,
                minimum_gain, maximum_gain, minimum_offset, maximum_offset,
                minimum_light_exposure_ticks, maximum_light_exposure_ticks,
                minimum_temperature_c, maximum_temperature_c, effective_from_unix_ms,
                effective_until_unix_ms, publication_state, retention_hold, failure_reason,
                created_unix_ms, updated_unix_ms)
            VALUES (
                $id, $identity, $source, $json, $profile_path, $profile_identity, $model_identity,
                $agent, $rig, $rig_profile, $sensor_profile, $input_layout, $output_layout,
                $minimum_gain, $maximum_gain, $minimum_offset, $maximum_offset,
                $minimum_exposure, $maximum_exposure, $minimum_temperature, $maximum_temperature,
                $effective_from, $effective_until, 'published', 1, NULL, $created, $updated);
            """, cancellationToken,
            ("$id", bundle.BundleId),
            ("$identity", bundleIdentity),
            ("$source", bundle.Source),
            ("$json", bundleJson),
            ("$profile_path", bundle.ProfileRelativePath),
            ("$profile_identity", bundle.ProfileIdentitySha256),
            ("$model_identity", bundle.AcquisitionModelIdentitySha256),
            ("$agent", applicability.AgentId),
            ("$rig", applicability.RigId),
            ("$rig_profile", applicability.RigProfileSha256),
            ("$sensor_profile", applicability.SensorProfileSha256),
            ("$input_layout", CaptureContractJson.ComputeCanonicalJsonSha256(applicability.InputLayout)),
            ("$output_layout", CaptureContractJson.ComputeCanonicalJsonSha256(applicability.OutputLayout)),
            ("$minimum_gain", applicability.MinimumGain),
            ("$maximum_gain", applicability.MaximumGain),
            ("$minimum_offset", DbValue(applicability.MinimumOffset)),
            ("$maximum_offset", DbValue(applicability.MaximumOffset)),
            ("$minimum_exposure", DbValue(applicability.MinimumLightExposure?.Ticks)),
            ("$maximum_exposure", DbValue(applicability.MaximumLightExposure?.Ticks)),
            ("$minimum_temperature", DbValue(applicability.MinimumTemperatureC)),
            ("$maximum_temperature", DbValue(applicability.MaximumTemperatureC)),
            ("$effective_from", applicability.EffectiveFromUtc.ToUnixTimeMilliseconds()),
            ("$effective_until", DbValue(applicability.EffectiveUntilUtc?.ToUnixTimeMilliseconds())),
            ("$created", bundle.CreatedUtc.ToUnixTimeMilliseconds()),
            ("$updated", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
    }

    private static Task<int> InsertArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bundleId,
        int ordinal,
        CalibrationLibraryArtifactV1 artifact,
        PublishedArtifactEvidence evidence,
        CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, """
            INSERT INTO calibration_library_artifacts(
                bundle_id, ordinal, artifact_id, reference_kind, role, source_index,
                manifest_relative_path, manifest_sha256, payload_relative_path, payload_sha256,
                ordered_source_artifact_ids_json, master_recipe_json)
            VALUES ($bundle, $ordinal, $artifact, $kind, $role, $source_index,
                    $manifest, $manifest_sha, $payload, $sha, $sources, $recipe);
            """, cancellationToken,
            ("$bundle", bundleId),
            ("$ordinal", ordinal),
            ("$artifact", artifact.ArtifactId.ToString("N")),
            ("$kind", artifact.Kind),
            ("$role", artifact.Role),
            ("$source_index", DbValue(artifact.SourceIndex)),
            ("$manifest", artifact.ManifestRelativePath),
            ("$manifest_sha", evidence.ManifestSha256),
            ("$payload", evidence.PayloadRelativePath),
            ("$sha", artifact.PayloadSha256),
            ("$sources", JsonSerializer.SerializeToUtf8Bytes(artifact.OrderedSourceArtifactIds)),
            ("$recipe", artifact.MasterBuildRecipe is null
                ? DBNull.Value
                : JsonSerializer.SerializeToUtf8Bytes(artifact.MasterBuildRecipe)));

    private static async Task ValidatePersistedArtifactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CalibrationLibraryBundleV1 bundle,
        IReadOnlyList<PublishedArtifactEvidence> evidence,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ordinal, artifact_id, reference_kind, role, source_index,
                   manifest_relative_path, manifest_sha256, payload_relative_path, payload_sha256,
                   ordered_source_artifact_ids_json, master_recipe_json
            FROM calibration_library_artifacts WHERE bundle_id = $bundle ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$bundle", bundle.BundleId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ordinal = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ordinal >= bundle.Artifacts.Count)
            {
                throw new InvalidDataException("Persisted calibration artifact rows conflict with immutable evidence.");
            }
            var artifact = bundle.Artifacts[ordinal];
            var expectedSources = JsonSerializer.SerializeToUtf8Bytes(artifact.OrderedSourceArtifactIds);
            var expectedRecipe = artifact.MasterBuildRecipe is null
                ? null
                : JsonSerializer.SerializeToUtf8Bytes(artifact.MasterBuildRecipe);
            if (reader.GetInt32(0) != ordinal ||
                !string.Equals(reader.GetString(1), artifact.ArtifactId.ToString("N"), StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), artifact.Kind, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(3), artifact.Role, StringComparison.Ordinal) ||
                ReadNullableInt32(reader, 4) != artifact.SourceIndex ||
                !string.Equals(reader.GetString(5), artifact.ManifestRelativePath, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(6), evidence[ordinal].ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reader.GetString(7), evidence[ordinal].PayloadRelativePath, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(8), artifact.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
                !((byte[])reader.GetValue(9)).AsSpan().SequenceEqual(expectedSources) ||
                !NullableBytesEqual(reader, 10, expectedRecipe))
            {
                throw new InvalidDataException("Persisted calibration artifact rows conflict with immutable evidence.");
            }
            ordinal++;
        }
        if (ordinal != bundle.Artifacts.Count)
        {
            throw new InvalidDataException("Persisted calibration artifact rows are incomplete.");
        }
    }

    private static async Task ValidatePersistedArtifactEnvelopeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CalibrationLibraryBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ordinal, artifact_id, reference_kind, role, source_index,
                   manifest_relative_path, payload_sha256,
                   ordered_source_artifact_ids_json, master_recipe_json
            FROM calibration_library_artifacts WHERE bundle_id = $bundle ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$bundle", bundle.BundleId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ordinal = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ordinal >= bundle.Artifacts.Count)
            {
                throw new InvalidDataException("Persisted calibration artifact rows are inconsistent.");
            }
            var artifact = bundle.Artifacts[ordinal];
            var expectedSources = JsonSerializer.SerializeToUtf8Bytes(artifact.OrderedSourceArtifactIds);
            var expectedRecipe = artifact.MasterBuildRecipe is null
                ? null
                : JsonSerializer.SerializeToUtf8Bytes(artifact.MasterBuildRecipe);
            if (reader.GetInt32(0) != ordinal ||
                !string.Equals(reader.GetString(1), artifact.ArtifactId.ToString("N"), StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), artifact.Kind, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(3), artifact.Role, StringComparison.Ordinal) ||
                ReadNullableInt32(reader, 4) != artifact.SourceIndex ||
                !string.Equals(reader.GetString(5), artifact.ManifestRelativePath, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(6), artifact.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
                !((byte[])reader.GetValue(7)).AsSpan().SequenceEqual(expectedSources) ||
                !NullableBytesEqual(reader, 8, expectedRecipe))
            {
                throw new InvalidDataException("Persisted calibration artifact rows are inconsistent.");
            }
            ordinal++;
        }
        if (ordinal != bundle.Artifacts.Count)
        {
            throw new InvalidDataException("Persisted calibration artifact rows are incomplete.");
        }
    }

    private static async Task<CalibrationLibraryStateSnapshot> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT active_bundle_id, version, last_selection_reason, last_selection_unix_ms,
                   last_reconciliation_reason, last_reconciliation_unix_ms, updated_unix_ms
            FROM calibration_library_state WHERE state_key = 1;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The calibration library state row is missing.");
        }
        var activeId = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(0);
        var version = reader.GetInt64(1);
        var lastSelectionReason = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(2);
        DateTimeOffset? lastSelectionUtc = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
        var lastReconciliationReason = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(4);
        DateTimeOffset? lastReconciliationUtc = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));
        var updatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6));
        await reader.DisposeAsync().ConfigureAwait(false);
        var active = activeId is null
            ? null
            : (await ReadBundleAsync(connection, transaction, activeId, cancellationToken).ConfigureAwait(false))?.Snapshot
                ?? throw new InvalidDataException("The active calibration bundle is missing.");
        return new CalibrationLibraryStateSnapshot(
            active, version, lastSelectionReason, lastSelectionUtc,
            lastReconciliationReason, lastReconciliationUtc, updatedUtc);
    }

    private static async Task<PersistedBundle?> ReadBundleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bundleId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT bundle_json, bundle_identity_sha256, publication_state, failure_reason,
                   created_unix_ms, updated_unix_ms, source, profile_relative_path,
                   profile_identity_sha256, acquisition_model_identity_sha256, agent_id, rig_id,
                   rig_profile_sha256, sensor_profile_sha256, input_layout_sha256, output_layout_sha256,
                   minimum_gain, maximum_gain, minimum_offset, maximum_offset,
                   minimum_light_exposure_ticks, maximum_light_exposure_ticks,
                   minimum_temperature_c, maximum_temperature_c, effective_from_unix_ms,
                   effective_until_unix_ms
            FROM calibration_library_bundles WHERE bundle_id = $id;
            """;
        command.Parameters.AddWithValue("$id", bundleId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var bundleJson = (byte[])reader.GetValue(0);
        var identity = reader.GetString(1);
        var publicationState = reader.GetString(2);
        var failureReason = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(3);
        var created = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));
        var parsed = CalibrationLibraryContractJson.Parse(bundleJson);
        if (!parsed.Validation.IsValid || parsed.Value is not { } bundle ||
            !string.Equals(bundle.BundleId, bundleId, StringComparison.Ordinal) ||
            !string.Equals(
                CalibrationLibraryContractJson.ComputeIdentitySha256(bundle), identity,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(6), bundle.Source, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(7), bundle.ProfileRelativePath, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(8), bundle.ProfileIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(9), bundle.AcquisitionModelIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(10), bundle.Applicability.AgentId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(11), bundle.Applicability.RigId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(12), bundle.Applicability.RigProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(13), bundle.Applicability.SensorProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(14), CaptureContractJson.ComputeCanonicalJsonSha256(bundle.Applicability.InputLayout), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(15), CaptureContractJson.ComputeCanonicalJsonSha256(bundle.Applicability.OutputLayout), StringComparison.OrdinalIgnoreCase) ||
            reader.GetDouble(16) != bundle.Applicability.MinimumGain || reader.GetDouble(17) != bundle.Applicability.MaximumGain ||
            ReadNullableDouble(reader, 18) != bundle.Applicability.MinimumOffset ||
            ReadNullableDouble(reader, 19) != bundle.Applicability.MaximumOffset ||
            ReadNullableInt64(reader, 20) != bundle.Applicability.MinimumLightExposure?.Ticks ||
            ReadNullableInt64(reader, 21) != bundle.Applicability.MaximumLightExposure?.Ticks ||
            ReadNullableDouble(reader, 22) != bundle.Applicability.MinimumTemperatureC ||
            ReadNullableDouble(reader, 23) != bundle.Applicability.MaximumTemperatureC ||
            reader.GetInt64(24) != bundle.Applicability.EffectiveFromUtc.ToUnixTimeMilliseconds() ||
            ReadNullableInt64(reader, 25) != bundle.Applicability.EffectiveUntilUtc?.ToUnixTimeMilliseconds() ||
            created.ToUnixTimeMilliseconds() != bundle.CreatedUtc.ToUnixTimeMilliseconds())
        {
            throw new InvalidDataException("A durable calibration bundle failed validation.");
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        await ValidatePersistedArtifactEnvelopeAsync(
            connection, transaction, bundle, cancellationToken).ConfigureAwait(false);
        return new PersistedBundle(
            new CalibrationLibraryBundleSnapshot(bundle, identity, publicationState, failureReason, created, updated),
            bundleJson);
    }

    private static async Task<string?> ReadBundleCollisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string identity,
        string profileRelativePath,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT bundle_id FROM calibration_library_bundles
            WHERE bundle_identity_sha256 = $identity OR profile_relative_path = $profile COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue("$profile", profileRelativePath);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string bundleId
            ? bundleId
            : null;
    }

    private static async Task<PersistedCommand?> ReadCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string expectedCommandKind,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_sha256, command_kind, result_bundle_id, result_state_version,
                   result_json, completed_unix_ms
            FROM calibration_library_commands WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (!string.Equals(reader.GetString(1), expectedCommandKind, StringComparison.Ordinal))
        {
            throw new CalibrationLibraryStoreConflictException(
                "The calibration command idempotency key is assigned to a different command kind.");
        }
        if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ||
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ||
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ||
            await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("A durable calibration command result is incomplete.");
        }
        var resultBundleId = reader.GetString(2);
        var result = JsonSerializer.Deserialize<CalibrationLibraryStateSnapshot>((byte[])reader.GetValue(4))
            ?? throw new InvalidDataException("A durable calibration command result is invalid JSON.");
        if (result.Version != reader.GetInt64(3) || result.ActiveBundle is not { } active ||
            !string.Equals(active.Bundle.BundleId, resultBundleId, StringComparison.Ordinal) ||
            !CalibrationLibraryContract.Validate(active.Bundle).IsValid ||
            !string.Equals(
                CalibrationLibraryContractJson.ComputeIdentitySha256(active.Bundle),
                active.BundleIdentitySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A durable calibration command result is inconsistent.");
        }
        var payloadSha256 = reader.GetString(0);
        await reader.DisposeAsync().ConfigureAwait(false);
        var persistedBundle = await ReadBundleAsync(
            connection, transaction, resultBundleId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("A durable calibration command references a missing bundle.");
        if (!string.Equals(
                persistedBundle.BundleIdentitySha256,
                active.BundleIdentitySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A durable calibration command references a different bundle revision.");
        }
        return new PersistedCommand(payloadSha256, result);
    }

    private static async Task<bool> WasPreviouslyActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bundleId,
        long currentVersion,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM calibration_library_activations
            WHERE to_bundle_id = $bundle AND state_version < $version;
            """;
        command.Parameters.AddWithValue("$bundle", bundleId);
        command.Parameters.AddWithValue("$version", currentVersion);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private async Task<CalibrationAcquisitionJobSnapshot> UpdateAcquisitionJobAsync(
        string jobId,
        string state,
        string phase,
        string? bundleId,
        string? failureReason,
        bool terminal,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 128 ||
            string.IsNullOrWhiteSpace(phase) || phase.Length > 64 ||
            failureReason?.Length > 512)
        {
            throw new ArgumentException("The calibration acquisition update is invalid.", nameof(jobId));
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadRequiredAcquisitionJobAsync(
                connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            if (current.IsTerminal)
            {
                if (!string.Equals(current.State, state, StringComparison.Ordinal) ||
                    !string.Equals(current.Phase, phase, StringComparison.Ordinal) ||
                    !string.Equals(current.BundleId, bundleId, StringComparison.Ordinal) ||
                    !string.Equals(current.FailureReason, failureReason, StringComparison.Ordinal))
                {
                    throw new CalibrationLibraryStoreConflictException(
                        "The terminal calibration acquisition job cannot be changed.");
                }
                await CompletePendingCancelCommandsAsync(
                    connection, transaction, current, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }
            ValidateAcquisitionTransition(current, state, phase, bundleId, failureReason, terminal);
            var now = Now();
            await ExecuteAsync(connection, transaction, """
                UPDATE calibration_acquisition_jobs
                SET state = $state, phase = $phase, bundle_id = $bundle,
                    failure_reason = $failure, updated_unix_ms = $now,
                    completed_unix_ms = CASE WHEN $terminal = 1 THEN $now ELSE NULL END
                WHERE job_id = $job;
                """, cancellationToken,
                ("$state", state),
                ("$phase", phase),
                ("$bundle", (object?)bundleId ?? DBNull.Value),
                ("$failure", (object?)failureReason ?? DBNull.Value),
                ("$now", now.ToUnixTimeMilliseconds()),
                ("$terminal", terminal ? 1 : 0),
                ("$job", jobId)).ConfigureAwait(false);
            var result = await ReadRequiredAcquisitionJobAsync(
                connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            await CompletePendingCancelCommandsAsync(
                connection, transaction, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.State, result.State, StringComparison.Ordinal))
            {
                _telemetry?.RecordAcquisitionTransition(
                    current.State, result.State, result.Phase, result.AttemptCount, result.FailureReason);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateAcquisitionTransition(
        CalibrationAcquisitionJobSnapshot current,
        string state,
        string phase,
        string? bundleId,
        string? failureReason,
        bool terminal)
    {
        if (state is CalibrationAcquisitionStates.Failed or CalibrationAcquisitionStates.Cancelled)
        {
            if (!terminal || bundleId is not null ||
                state == CalibrationAcquisitionStates.Failed && string.IsNullOrWhiteSpace(failureReason) ||
                state == CalibrationAcquisitionStates.Cancelled && failureReason is not null)
            {
                throw new InvalidOperationException("The terminal calibration acquisition transition is invalid.");
            }
            return;
        }
        var currentRank = AcquisitionStateRank(current.State);
        var targetRank = AcquisitionStateRank(state);
        if (terminal != (state == CalibrationAcquisitionStates.Published) ||
            targetRank < currentRank || targetRank > currentRank + 1 ||
            PhaseRank(phase) != PhaseRank(current.Phase) + 1 ||
            state == CalibrationAcquisitionStates.Published && string.IsNullOrWhiteSpace(bundleId) ||
            state != CalibrationAcquisitionStates.Published && bundleId is not null || failureReason is not null)
        {
            throw new InvalidOperationException("The calibration acquisition phase transition is invalid.");
        }
    }

    private static int AcquisitionStateRank(string state)
        => state switch
        {
            CalibrationAcquisitionStates.Planned => 0,
            CalibrationAcquisitionStates.Acquiring => 1,
            CalibrationAcquisitionStates.Building => 2,
            CalibrationAcquisitionStates.Publishing => 3,
            CalibrationAcquisitionStates.Published => 4,
            _ => throw new InvalidDataException("The durable calibration acquisition state is invalid.")
        };

    private static int PhaseRank(string phase)
    {
        if (phase == "planned") return 0;
        if (phase == "sources-pending") return 1;
        if (phase == "masters-pending") return 14;
        if (phase == "masters-built") return 15;
        if (phase == "profile-published") return 20;
        if (phase == "published") return 21;
        if (phase is "failed" or "cancelled") return 22;
        var kinds = CalibrationReferenceKinds.All;
        for (var kindIndex = 0; kindIndex < kinds.Count; kindIndex++)
        {
            for (var sourceIndex = 0; sourceIndex < 3; sourceIndex++)
            {
                if (phase == $"source-{kinds[kindIndex]}-{sourceIndex}")
                {
                    return 2 + kindIndex * 3 + sourceIndex;
                }
            }
            if (phase == $"master-{kinds[kindIndex]}")
            {
                return 16 + kindIndex;
            }
        }
        throw new InvalidDataException("The durable calibration acquisition phase is invalid.");
    }

    private static async Task<CalibrationAcquisitionJobSnapshot?> ReadAcquireCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT command_kind, payload_sha256, result_job_id, result_json, completed_unix_ms
            FROM calibration_library_commands WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (!string.Equals(reader.GetString(0), "acquire", StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), payloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CalibrationLibraryStoreConflictException(
                "The calibration command idempotency key has different durable content.");
        }
        if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ||
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ||
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The durable calibration acquire command is incomplete.");
        }
        var jobId = reader.GetString(2);
        var resultJson = (byte[])reader.GetValue(3);
        await reader.DisposeAsync().ConfigureAwait(false);
        var job = await ReadAcquisitionJobAsync(connection, transaction, jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The durable calibration acquire command references a missing job.");
        if (!resultJson.AsSpan().SequenceEqual(VirtualCalibrationAcquisitionContractJson.SerializePlan(job.Plan)))
        {
            throw new InvalidDataException("The durable calibration acquire command references a different plan.");
        }
        return job;
    }

    private static async Task<PersistedCancelCommand?> ReadCancelCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT command_kind, payload_sha256, result_job_id, result_json, completed_unix_ms
            FROM calibration_library_commands WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (!string.Equals(reader.GetString(0), "cancel", StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), payloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CalibrationLibraryStoreConflictException(
                "The calibration command idempotency key has different durable content.");
        }
        if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ||
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The durable calibration cancellation command is incomplete.");
        }
        var jobId = reader.GetString(2);
        var result = JsonSerializer.Deserialize<CalibrationAcquisitionJobSnapshot>((byte[])reader.GetValue(3))
            ?? throw new InvalidDataException("The durable calibration cancellation result is invalid JSON.");
        var pending = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false);
        await reader.DisposeAsync().ConfigureAwait(false);
        var current = await ReadAcquisitionJobAsync(
            connection, transaction, jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The durable calibration cancellation references a missing job.");
        if (result.Plan.JobId != current.Plan.JobId || !pending &&
            (result.State != current.State || result.Phase != current.Phase ||
             result.BundleId != current.BundleId || !current.IsTerminal))
        {
            throw new InvalidDataException("The durable calibration cancellation result is inconsistent.");
        }
        return new PersistedCancelCommand(pending, current);
    }

    private async Task CompletePendingCancelCommandsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CalibrationAcquisitionJobSnapshot result,
        CancellationToken cancellationToken)
    {
        if (!result.IsTerminal)
        {
            return;
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE calibration_library_commands
            SET result_bundle_id = $bundle, result_json = $result, completed_unix_ms = $now
            WHERE command_kind = 'cancel' AND result_job_id = $job AND completed_unix_ms IS NULL;
            """, cancellationToken,
            ("$bundle", (object?)result.BundleId ?? DBNull.Value),
            ("$result", JsonSerializer.SerializeToUtf8Bytes(result)),
            ("$now", Now().ToUnixTimeMilliseconds()),
            ("$job", result.Plan.JobId)).ConfigureAwait(false);
    }

    private static async Task<CalibrationAcquisitionJobSnapshot?> ReadNonterminalAcquisitionJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string cameraKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT job_id FROM calibration_acquisition_jobs
            WHERE camera_key = $camera AND state NOT IN ('published', 'failed', 'cancelled')
            ORDER BY created_unix_ms, job_id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$camera", cameraKey);
        var jobId = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return jobId is null
            ? null
            : await ReadAcquisitionJobAsync(connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CalibrationAcquisitionJobSnapshot> ReadRequiredAcquisitionJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        CancellationToken cancellationToken)
        => await ReadAcquisitionJobAsync(connection, transaction, jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The calibration acquisition job was not found.");

    private static async Task<CalibrationAcquisitionJobSnapshot?> ReadAcquisitionJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT camera_key, plan_json, plan_sha256, state, phase, attempt_count, bundle_id,
                   failure_reason, actor, reason, created_unix_ms, updated_unix_ms, completed_unix_ms,
                   idempotency_key
            FROM calibration_acquisition_jobs WHERE job_id = $job;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var planJson = (byte[])reader.GetValue(1);
        var plan = VirtualCalibrationAcquisitionContractJson.ParsePlan(planJson);
        var planIdentity = reader.GetString(2);
        var state = reader.GetString(3);
        var phase = reader.GetString(4);
        var bundleId = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6);
        var failure = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7);
        var reason = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(9);
        var actor = reader.GetString(8);
        var idempotencyKey = reader.GetString(13);
        var attemptCount = reader.GetInt32(5);
        var created = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10));
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11));
        DateTimeOffset? completed = await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12));
        var terminal = state is CalibrationAcquisitionStates.Published or CalibrationAcquisitionStates.Failed or
            CalibrationAcquisitionStates.Cancelled;
        var phaseRank = PhaseRank(phase);
        if (!string.Equals(plan.JobId, jobId, StringComparison.Ordinal) ||
            !string.Equals(plan.CameraKey, reader.GetString(0), StringComparison.Ordinal) ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(planJson)), planIdentity, StringComparison.OrdinalIgnoreCase) ||
            terminal != completed.HasValue || attemptCount is < 0 or > MaximumAcquisitionAttempts ||
            !StateMatchesPhase(state, phaseRank) ||
            state == CalibrationAcquisitionStates.Published && bundleId is null ||
            state != CalibrationAcquisitionStates.Published && bundleId is not null ||
            state == CalibrationAcquisitionStates.Failed && string.IsNullOrWhiteSpace(failure) ||
            state != CalibrationAcquisitionStates.Failed && failure is not null)
        {
            throw new InvalidDataException("The durable calibration acquisition job failed validation.");
        }
        _ = state is CalibrationAcquisitionStates.Failed or CalibrationAcquisitionStates.Cancelled
            ? 0
            : AcquisitionStateRank(state);
        await reader.DisposeAsync().ConfigureAwait(false);
        using (var replay = connection.CreateCommand())
        {
            replay.Transaction = transaction;
            replay.CommandText = """
                SELECT command_kind, payload_sha256, result_job_id, result_json, completed_unix_ms,
                       result_state_version
                FROM calibration_library_commands WHERE idempotency_key = $key;
                """;
            replay.Parameters.AddWithValue("$key", idempotencyKey);
            using var replayReader = await replay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await replayReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The durable calibration acquisition command linkage is missing.");
            }
            long? expectedVersion = await replayReader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                ? null
                : replayReader.GetInt64(5);
            var request = new VirtualCalibrationAcquisitionRequestV1(
                VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
                idempotencyKey,
                plan.Gain,
                plan.Offset,
                plan.TemperatureC,
                plan.BiasExposure,
                plan.DarkExposure,
                plan.FlatExposure,
                plan.DefectExposure,
                plan.ApplicableLightExposure,
                plan.EffectiveFromUtc,
                plan.EffectiveUntilUtc,
                plan.SourceModel,
                actor,
                reason,
                expectedVersion);
            var requestIdentity = VirtualCalibrationAcquisitionContractJson.ComputeRequestIdentitySha256(request);
            if (
                !string.Equals(replayReader.GetString(0), "acquire", StringComparison.Ordinal) ||
                !string.Equals(replayReader.GetString(1), requestIdentity, StringComparison.OrdinalIgnoreCase) ||
                await replayReader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ||
                !string.Equals(replayReader.GetString(2), jobId, StringComparison.Ordinal) ||
                await replayReader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ||
                !((byte[])replayReader.GetValue(3)).AsSpan().SequenceEqual(planJson) ||
                await replayReader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The durable calibration acquisition command linkage is invalid.");
            }
        }
        return new CalibrationAcquisitionJobSnapshot(
            plan, planIdentity, state, phase, attemptCount, bundleId, failure,
            actor, reason, created, updated, completed);
    }

    private static bool StateMatchesPhase(string state, int phaseRank)
        => state switch
        {
            CalibrationAcquisitionStates.Planned => phaseRank == 0,
            CalibrationAcquisitionStates.Acquiring => phaseRank is >= 1 and <= 13,
            CalibrationAcquisitionStates.Building => phaseRank is 14 or 15,
            CalibrationAcquisitionStates.Publishing => phaseRank is >= 16 and <= 20,
            CalibrationAcquisitionStates.Published => phaseRank == 21,
            CalibrationAcquisitionStates.Failed or CalibrationAcquisitionStates.Cancelled => phaseRank == 22,
            _ => false
        };

    private static void ValidateCommandIdentity(string idempotencyKey, string payloadSha256)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            idempotencyKey.Any(static character => char.IsControl(character)) ||
            payloadSha256.Length != 64 || !payloadSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The calibration command identity is invalid.");
        }
    }

    private static void ValidateActor(string actor, string? reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128 || reason?.Length > 512)
        {
            throw new ArgumentException("The calibration acquisition actor or reason is invalid.");
        }
    }

    private static async Task<CalibrationReconciliationOperation?> ReadReconciliationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_relative_path, quarantine_relative_path, outcome, reason, operation_state
            FROM calibration_library_reconciliation WHERE evidence_key = $key;
            """;
        command.Parameters.AddWithValue("$key", evidenceKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CalibrationReconciliationOperation(
                evidenceKey,
                reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4))
            : null;
    }

    private static bool ReconciliationFactsMatch(
        CalibrationReconciliationOperation existing,
        CalibrationReconciliationOperation requested)
        => string.Equals(existing.EvidenceKey, requested.EvidenceKey, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(existing.SourceRelativePath, requested.SourceRelativePath, StringComparison.Ordinal) &&
           string.Equals(existing.QuarantineRelativePath, requested.QuarantineRelativePath, StringComparison.Ordinal) &&
           string.Equals(existing.Outcome, requested.Outcome, StringComparison.Ordinal) &&
           string.Equals(existing.Reason, requested.Reason, StringComparison.Ordinal);

    private async Task<PersistedCommand?> ReadCommandOutsideTransactionAsync(
        string idempotencyKey,
        string expectedCommandKind,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginRead(connection);
        var command = await ReadCommandAsync(
            connection, transaction, idempotencyKey, expectedCommandKind, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return command;
    }

    private static CalibrationLibraryStateSnapshot ValidateReplay(
        PersistedCommand replay,
        string payloadSha256)
    {
        if (!string.Equals(replay.PayloadSha256, payloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CalibrationLibraryStoreConflictException(
                "The calibration command idempotency key has different durable content.");
        }
        return replay.Result;
    }

    private static void ValidateMutation(
        string bundleId,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason)
    {
        if (string.IsNullOrWhiteSpace(bundleId) || bundleId.Length > 128)
        {
            throw new ArgumentException("A valid calibration bundle identifier is required.", nameof(bundleId));
        }
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            idempotencyKey.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("A valid idempotency key is required.", nameof(idempotencyKey));
        }
        if (expectedVersion is null)
        {
            throw new ArgumentException("The expected durable state version is required.", nameof(expectedVersion));
        }
        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
        {
            throw new ArgumentException("A valid actor is required.", nameof(actor));
        }
        if (reason?.Length > 512)
        {
            throw new ArgumentException("The reason is too long.", nameof(reason));
        }
    }

    private static void ValidateCancelMutation(
        string jobId,
        string idempotencyKey,
        long expectedVersion,
        string actor,
        string? reason)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 128 || expectedVersion < 0)
        {
            throw new ArgumentException("The calibration cancellation command is invalid.", nameof(jobId));
        }
        ValidateCommandIdentity(idempotencyKey, new string('0', 64));
        ValidateActor(actor, reason);
    }

    private static string CancelPayloadSha256(
        string jobId,
        long expectedVersion,
        string actor,
        string? reason)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            command = "cancel",
            jobId,
            expectedVersion,
            actor,
            reason
        });

    private string ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Calibration evidence paths must be relative to CameraAgent storage.");
        }
        var path = Path.GetFullPath(Path.Combine(_storageRoot, relativePath));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(_storageRoot), Path.DirectorySeparatorChar);
        if (!path.StartsWith(
                prefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Calibration evidence path escapes CameraAgent storage.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_storageRoot, path);
        return path;
    }

    private DateTimeOffset Now()
        => DateTimeOffset.FromUnixTimeMilliseconds(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        EnsureDatabaseFilesArePhysical();
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void EnsureDatabaseFilesArePhysical()
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(_storageRoot, _databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_storageRoot, string.Concat(_databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(_storageRoot, string.Concat(_databasePath, "-shm"));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant SQL statements are passed to this helper.")]
    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant SQL statements are passed to this helper.")]
    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.")]
    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
        => connection.BeginTransaction(deferred: false);

    private static SqliteTransaction BeginRead(SqliteConnection connection)
        => connection.BeginTransaction(deferred: true);

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static object DbValue<T>(T? value) where T : struct
        => value.HasValue ? value.Value : DBNull.Value;

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static double? ReadNullableDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static bool NullableBytesEqual(SqliteDataReader reader, int ordinal, byte[]? expected)
        => reader.IsDBNull(ordinal)
            ? expected is null
            : expected is not null && ((byte[])reader.GetValue(ordinal)).AsSpan().SequenceEqual(expected);

    private sealed record PublishedArtifactEvidence(
        string PayloadRelativePath,
        string ManifestSha256,
        long PayloadBytes);
    private sealed record EvidenceFileFingerprint(string RelativePath, long Length, long LastWriteUtcTicks);
    private sealed record StableEvidenceFile(byte[] Bytes, EvidenceFileFingerprint Fingerprint);
    private sealed record PublishedBundleEvidence(
        IReadOnlyList<PublishedArtifactEvidence> Artifacts,
        IReadOnlyList<EvidenceFileFingerprint> Files,
        long ValidatedUnixMs);
    private sealed record PersistedBundle(CalibrationLibraryBundleSnapshot Snapshot, byte[] BundleJson)
    {
        public string BundleIdentitySha256 => Snapshot.BundleIdentitySha256;
    }
    private sealed record PersistedCommand(string PayloadSha256, CalibrationLibraryStateSnapshot Result);

    private sealed record PersistedCancelCommand(bool Pending, CalibrationAcquisitionJobSnapshot Result);
}
