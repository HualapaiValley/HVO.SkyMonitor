using System.Security.Cryptography;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

public enum CaptureAdmissionState
{
    Initializing,
    Running,
    PauseRequested,
    Paused,
    Unavailable
}

public sealed record CaptureAdmissionSnapshot(
    CaptureAdmissionState State,
    long Version,
    DateTimeOffset UpdatedUtc,
    bool IsInitialized);

public sealed record CaptureControlCommandResult(
    CaptureAdmissionState State,
    long Version,
    bool Changed,
    bool Replayed,
    DateTimeOffset RequestedUtc,
    DateTimeOffset CompletedUtc);

public sealed class CaptureControlValidationException : InvalidOperationException
{
    public CaptureControlValidationException()
    {
    }

    public CaptureControlValidationException(string message) : base(message)
    {
    }

    public CaptureControlValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class CaptureControlConflictException : InvalidOperationException
{
    public CaptureControlConflictException()
    {
    }

    public CaptureControlConflictException(string message) : base(message)
    {
    }

    public CaptureControlConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class CaptureAdmissionUnavailableException : InvalidOperationException
{
    public CaptureAdmissionUnavailableException()
        : base("Capture admission is unavailable because durable publication did not complete.")
    {
    }

    public CaptureAdmissionUnavailableException(string message)
        : base(message)
    {
    }

    public CaptureAdmissionUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CaptureAdmissionCoordinator : IDisposable
{
    private const int GateRunning = 1;
    private const int GateClosed = 2;
    private readonly IRawCaptureIngress _rawCaptureIngress;
    private readonly SqliteCaptureControlStore _store;
    private readonly CaptureControlTelemetry _telemetry;
    private readonly FleetRuntimeState? _fleetRuntimeState;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _drainGate = new();
    private TaskCompletionSource _runningSignal = NewSignal();
    private TaskCompletionSource? _drainedSignal;
    private CaptureAdmissionSnapshot _snapshot;
    private int _gateState;
    private int _inFlight;
    private int _failedPublicationDuringDrain;
    private bool _disposed;

    public CaptureAdmissionCoordinator(
        IRawCaptureIngress rawCaptureIngress,
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        CaptureControlTelemetry telemetry,
        FleetRuntimeState? fleetRuntimeState = null)
    {
        ArgumentNullException.ThrowIfNull(rawCaptureIngress);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(telemetry);
        _rawCaptureIngress = rawCaptureIngress;
        _telemetry = telemetry;
        _fleetRuntimeState = fleetRuntimeState;
        var configured = options.Value;
        _store = new SqliteCaptureControlStore(
            Path.Combine(Path.GetFullPath(configured.RawIngressRoot), "journal", "raw-ingress.db"),
            configured.RawIngressSqliteBusyTimeoutSeconds,
            timeProvider);
        _snapshot = new CaptureAdmissionSnapshot(
            CaptureAdmissionState.Initializing,
            0,
            timeProvider.GetUtcNow(),
            false);
    }

    public CaptureAdmissionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.IsInitialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.IsInitialized)
            {
                return;
            }

            await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var recovered = await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SetSnapshot(recovered);
            if (recovered.State == CaptureAdmissionState.Running)
            {
                OpenGate();
            }
            else
            {
                CloseGate();
            }
        }
        catch
        {
            CloseGate();
            var current = Snapshot;
            Volatile.Write(ref _snapshot, current with
            {
                State = CaptureAdmissionState.Unavailable,
                UpdatedUtc = DateTimeOffset.UtcNow,
                IsInitialized = false
            });
            throw;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    internal ValueTask<CaptureAdmissionLease> EnterAsync(CancellationToken cancellationToken)
    {
        if (TryEnter())
        {
            return ValueTask.FromResult(new CaptureAdmissionLease(this));
        }
        return WaitToEnterAsync(cancellationToken);
    }

    public Task<CaptureControlCommandResult> PauseAsync(
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteCommandAsync(
            CaptureControlTarget.Paused,
            idempotencyKey,
            expectedVersion,
            actor,
            reason,
            cancellationToken);

    public Task<CaptureControlCommandResult> ResumeAsync(
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteCommandAsync(
            CaptureControlTarget.Running,
            idempotencyKey,
            expectedVersion,
            actor,
            reason,
            cancellationToken);

    internal async Task<T> ExecuteCaptureBoundaryAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var reopen = Snapshot.State == CaptureAdmissionState.Running;
        try
        {
            Interlocked.Exchange(ref _failedPublicationDuringDrain, 0);
            CloseGate();
            await WaitForDrainAsync(CancellationToken.None).ConfigureAwait(false);
            if (Volatile.Read(ref _failedPublicationDuringDrain) != 0)
            {
                var current = Snapshot;
                SetSnapshot(current with
                {
                    State = CaptureAdmissionState.Unavailable,
                    UpdatedUtc = DateTimeOffset.UtcNow
                });
                _fleetRuntimeState?.CaptureFailed("activation-publication-failed");
                throw new CaptureAdmissionUnavailableException();
            }
            return await action(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (reopen && Snapshot.State == CaptureAdmissionState.Running)
            {
                OpenGate();
            }
            _commandGate.Release();
        }
    }

    private async Task<CaptureControlCommandResult> ExecuteCommandAsync(
        CaptureControlTarget target,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateCommand(idempotencyKey, expectedVersion, actor, reason);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prior = Snapshot;
            if (target == CaptureControlTarget.Paused)
            {
                Interlocked.Exchange(ref _failedPublicationDuringDrain, 0);
                CloseGate();
            }

            CaptureControlStoreResult started;
            try
            {
                started = await _store.BeginAsync(
                    target,
                    idempotencyKey,
                    expectedVersion,
                    actor,
                    reason,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (target == CaptureControlTarget.Paused && Snapshot.State == CaptureAdmissionState.Running)
                {
                    OpenGate();
                }
                throw;
            }

            if (started.IsReplay)
            {
                ApplyGate(prior.State);
                _telemetry.RecordAdmissionCommand(target.ToString(), "replayed");
                return ToResult(started, true);
            }

            SetSnapshot(started.Snapshot);
            if (started.IsComplete)
            {
                ApplyGate(started.Snapshot.State);
                _telemetry.RecordAdmissionCommand(target.ToString(), started.Changed ? "changed" : "no-op");
                return ToResult(started, false);
            }

            // Once PauseRequested is durable, request cancellation must not leave capture admission half-drained.
            await WaitForDrainAsync(CancellationToken.None).ConfigureAwait(false);
            if (Volatile.Read(ref _failedPublicationDuringDrain) != 0)
            {
                var unavailable = started.Snapshot with
                {
                    State = CaptureAdmissionState.Unavailable,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                SetSnapshot(unavailable);
                _fleetRuntimeState?.CaptureFailed("pause-publication-failed");
                _telemetry.RecordAdmissionCommand("Paused", "unavailable");
                throw new CaptureAdmissionUnavailableException();
            }
            var completed = await _store.CompletePauseAsync(idempotencyKey, CancellationToken.None).ConfigureAwait(false);
            SetSnapshot(completed.Snapshot);
            _telemetry.RecordAdmissionCommand("Paused", "changed");
            return ToResult(completed, false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private static CaptureControlCommandResult ToResult(CaptureControlStoreResult result, bool replayed)
        => new(
            result.Snapshot.State,
            result.Snapshot.Version,
            result.Changed,
            replayed,
            result.RequestedUtc,
            result.CompletedUtc ?? result.RequestedUtc);

    private void ApplyGate(CaptureAdmissionState state)
    {
        if (state == CaptureAdmissionState.Running)
        {
            OpenGate();
        }
        else
        {
            CloseGate();
        }
    }

    private bool TryEnter()
    {
        if (Volatile.Read(ref _gateState) != GateRunning)
        {
            return false;
        }
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _gateState) == GateRunning)
        {
            return true;
        }
        Release(published: true);
        return false;
    }

    private async ValueTask<CaptureAdmissionLease> WaitToEnterAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signal = Volatile.Read(ref _runningSignal).Task;
            if (TryEnter())
            {
                return new CaptureAdmissionLease(this);
            }
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void CloseGate()
    {
        if (Interlocked.Exchange(ref _gateState, GateClosed) == GateRunning)
        {
            Volatile.Write(ref _runningSignal, NewSignal());
        }
    }

    private void OpenGate()
    {
        // Callers open admission only after Running has committed durably.
        Volatile.Write(ref _gateState, GateRunning);
        Volatile.Read(ref _runningSignal).TrySetResult();
    }

    private Task WaitForDrainAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _inFlight) == 0)
        {
            return Task.CompletedTask;
        }
        lock (_drainGate)
        {
            if (_inFlight == 0)
            {
                return Task.CompletedTask;
            }
            _drainedSignal ??= NewSignal();
            return _drainedSignal.Task.WaitAsync(cancellationToken);
        }
    }

    private void Release(bool published)
    {
        if (!published && Volatile.Read(ref _gateState) == GateClosed)
        {
            Interlocked.Exchange(ref _failedPublicationDuringDrain, 1);
        }
        if (Interlocked.Decrement(ref _inFlight) != 0)
        {
            return;
        }
        lock (_drainGate)
        {
            _drainedSignal?.TrySetResult();
            _drainedSignal = null;
        }
    }

    private void SetSnapshot(CaptureAdmissionSnapshot snapshot)
        => Volatile.Write(ref _snapshot, snapshot);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ValidateCommand(
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new CaptureControlValidationException("Idempotency key is required and must not exceed 128 characters.");
        }
        if (expectedVersion < 0)
        {
            throw new CaptureControlValidationException("Expected version must not be negative.");
        }
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
        {
            throw new CaptureControlValidationException("Actor is required and must not exceed 128 characters.");
        }
        if (reason?.Length > 512)
        {
            throw new CaptureControlValidationException("Reason must not exceed 512 characters.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _initializeGate.Dispose();
        _commandGate.Dispose();
    }

    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The readonly lease implements IDisposable and delegates idempotent disposal to shared reference state so struct copies cannot double-release admission.")]
    internal readonly struct CaptureAdmissionLease : IDisposable
    {
        private readonly CaptureAdmissionLeaseState? _state;

        internal CaptureAdmissionLease(CaptureAdmissionCoordinator owner) => _state = new(owner);

        internal void MarkPublished() => _state?.MarkPublished();

        internal void MarkNoPublicationRequired() => _state?.MarkNoPublicationRequired();

        public void Dispose() => _state?.Dispose();
    }

    private sealed class CaptureAdmissionLeaseState(CaptureAdmissionCoordinator owner) : IDisposable
    {
        private int _published;
        private int _publicationRequired = 1;
        private int _disposed;

        internal void MarkPublished() => Volatile.Write(ref _published, 1);

        internal void MarkNoPublicationRequired() => Volatile.Write(ref _publicationRequired, 0);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(
                    Volatile.Read(ref _published) != 0 || Volatile.Read(ref _publicationRequired) == 0);
            }
        }
    }
}

internal enum CaptureControlTarget
{
    Running,
    Paused
}

internal sealed record CaptureControlStoreResult(
    CaptureAdmissionSnapshot Snapshot,
    bool Changed,
    bool IsReplay,
    bool IsComplete,
    DateTimeOffset RequestedUtc,
    DateTimeOffset? CompletedUtc);

internal sealed class SqliteCaptureControlStore(
    string databasePath,
    int busyTimeoutSeconds,
    TimeProvider timeProvider)
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly int _busyTimeoutSeconds = busyTimeoutSeconds;
    private readonly TimeProvider _timeProvider = timeProvider;

    internal async Task<CaptureAdmissionSnapshot> InitializeAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var snapshot = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var now = UtcNow();
        if (snapshot.State == CaptureAdmissionState.PauseRequested)
        {
            snapshot = await UpdateStateAsync(
                connection, transaction, CaptureAdmissionState.Paused, snapshot.Version + 1, now, cancellationToken)
                .ConfigureAwait(false);
        }
        using (var recover = connection.CreateCommand())
        {
            recover.Transaction = transaction;
            recover.CommandText = """
                UPDATE capture_control_commands
                SET status = 'completed', result_state = $state, result_version = $version,
                    changed = 1, completed_unix_ms = $completed
                WHERE status = 'pending';
                """;
            recover.Parameters.AddWithValue("$state", StoreState(snapshot.State));
            recover.Parameters.AddWithValue("$version", snapshot.Version);
            recover.Parameters.AddWithValue("$completed", now.ToUnixTimeMilliseconds());
            await recover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    internal async Task<CaptureControlStoreResult> BeginAsync(
        CaptureControlTarget target,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var payloadHash = PayloadHash(target, expectedVersion, actor, reason);
        var replay = await ReadCommandAsync(
            connection, transaction, idempotencyKey, payloadHash, target, expectedVersion, actor, reason, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay with { IsReplay = true };
        }

        var current = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (expectedVersion.HasValue && expectedVersion.Value != current.Version)
        {
            throw new CaptureControlConflictException("Capture control version does not match the expected version.");
        }

        var now = UtcNow();
        var sameTarget = target == CaptureControlTarget.Running
            ? current.State == CaptureAdmissionState.Running
            : current.State is CaptureAdmissionState.Paused or CaptureAdmissionState.PauseRequested;
        if (sameTarget)
        {
            await InsertCommandAsync(
                connection, transaction, idempotencyKey, payloadHash, target, expectedVersion, actor, reason,
                "completed", current.State, current.Version, false, now, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CaptureControlStoreResult(current, false, false, true, now, now);
        }

        var nextState = target == CaptureControlTarget.Running
            ? CaptureAdmissionState.Running
            : CaptureAdmissionState.PauseRequested;
        var next = await UpdateStateAsync(
            connection, transaction, nextState, current.Version + 1, now, cancellationToken).ConfigureAwait(false);
        var complete = target == CaptureControlTarget.Running;
        await InsertCommandAsync(
            connection, transaction, idempotencyKey, payloadHash, target, expectedVersion, actor, reason,
            complete ? "completed" : "pending", complete ? next.State : null, complete ? next.Version : null,
            true, now, complete ? now : null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CaptureControlStoreResult(next, true, false, complete, now, complete ? now : null);
    }

    internal async Task<CaptureControlStoreResult> CompletePauseAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var current = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (current.State != CaptureAdmissionState.PauseRequested)
        {
            throw new InvalidDataException("Capture pause completion requires a durable pause request.");
        }
        var now = UtcNow();
        var completed = await UpdateStateAsync(
            connection, transaction, CaptureAdmissionState.Paused, current.Version + 1, now, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset requestedUtc;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE capture_control_commands
                SET status = 'completed', result_state = 'paused', result_version = $version,
                    completed_unix_ms = $completed
                WHERE idempotency_key = $key AND status = 'pending'
                RETURNING requested_unix_ms;
                """;
            update.Parameters.AddWithValue("$version", completed.Version);
            update.Parameters.AddWithValue("$completed", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$key", idempotencyKey);
            var requested = await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (requested is null)
            {
                throw new InvalidDataException("The durable capture pause command is missing.");
            }
            requestedUtc = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(
                requested, System.Globalization.CultureInfo.InvariantCulture));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CaptureControlStoreResult(completed, true, false, true, requestedUtc, now);
    }

    private static async Task<CaptureControlStoreResult?> ReadCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string payloadHash,
        CaptureControlTarget target,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT target_state, expected_version, actor, reason, payload_sha256, status,
                   result_state, result_version, changed, requested_unix_ms, completed_unix_ms
            FROM capture_control_commands
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var exact = string.Equals(reader.GetString(0), StoreTarget(target), StringComparison.Ordinal) &&
            NullableLong(reader, 1) == expectedVersion &&
            string.Equals(reader.GetString(2), actor, StringComparison.Ordinal) &&
            NullableString(reader, 3) == reason &&
            string.Equals(reader.GetString(4), payloadHash, StringComparison.Ordinal);
        if (!exact)
        {
            throw new CaptureControlConflictException("Idempotency key is already associated with another command payload.");
        }
        if (!string.Equals(reader.GetString(5), "completed", StringComparison.Ordinal))
        {
            throw new CaptureControlConflictException("Idempotent command completion is still pending.");
        }
        var state = ParseState(reader.GetString(6));
        var version = reader.GetInt64(7);
        var requested = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9));
        var completed = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10));
        return new CaptureControlStoreResult(
            new CaptureAdmissionSnapshot(state, version, completed, true),
            reader.GetBoolean(8),
            false,
            true,
            requested,
            completed);
    }

    private static async Task InsertCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string payloadHash,
        CaptureControlTarget target,
        long? expectedVersion,
        string actor,
        string? reason,
        string status,
        CaptureAdmissionState? resultState,
        long? resultVersion,
        bool changed,
        DateTimeOffset requestedUtc,
        DateTimeOffset? completedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO capture_control_commands(
                idempotency_key, target_state, expected_version, actor, reason, payload_sha256,
                status, result_state, result_version, changed, requested_unix_ms, completed_unix_ms)
            VALUES ($key, $target, $expected, $actor, $reason, $payload,
                    $status, $result_state, $result_version, $changed, $requested, $completed);
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$target", StoreTarget(target));
        command.Parameters.AddWithValue("$expected", expectedVersion.HasValue ? expectedVersion.Value : DBNull.Value);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", payloadHash);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$result_state", resultState is null ? DBNull.Value : StoreState(resultState.Value));
        command.Parameters.AddWithValue("$result_version", resultVersion.HasValue ? resultVersion.Value : DBNull.Value);
        command.Parameters.AddWithValue("$changed", changed ? 1 : 0);
        command.Parameters.AddWithValue("$requested", requestedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$completed", completedUtc is null ? DBNull.Value : completedUtc.Value.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CaptureAdmissionSnapshot> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state, version, updated_unix_ms FROM capture_control_state WHERE state_key = 1;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Capture control state is missing.");
        }
        return new CaptureAdmissionSnapshot(
            ParseState(reader.GetString(0)),
            reader.GetInt64(1),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            true);
    }

    private static async Task<CaptureAdmissionSnapshot> UpdateStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureAdmissionState state,
        long version,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE capture_control_state
            SET state = $state, version = $version, updated_unix_ms = $updated
            WHERE state_key = 1;
            """;
        command.Parameters.AddWithValue("$state", StoreState(state));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$updated", updatedUtc.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("Capture control state update failed.");
        }
        return new CaptureAdmissionSnapshot(state, version, updatedUtc, true);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The interpolated value is a validated integer option used only in a PRAGMA statement.")]
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
            DefaultTimeout = _busyTimeoutSeconds
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutSeconds * 1000}; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        return connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
    }

    private DateTimeOffset UtcNow()
    {
        var milliseconds = _timeProvider.GetUtcNow().ToUniversalTime().ToUnixTimeMilliseconds();
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static string PayloadHash(
        CaptureControlTarget target,
        long? expectedVersion,
        string actor,
        string? reason)
    {
        var payload = string.Concat(
            StoreTarget(target), "\n",
            expectedVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null", "\n",
            actor.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", actor, "\n",
            reason is null ? "null" : string.Concat(reason.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", reason));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static long? NullableLong(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string StoreTarget(CaptureControlTarget target)
        => target == CaptureControlTarget.Running ? "running" : "paused";

    private static string StoreState(CaptureAdmissionState state)
        => state switch
        {
            CaptureAdmissionState.Running => "running",
            CaptureAdmissionState.PauseRequested => "pause_requested",
            CaptureAdmissionState.Paused => "paused",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "State cannot be persisted.")
        };

    private static CaptureAdmissionState ParseState(string state)
        => state switch
        {
            "running" => CaptureAdmissionState.Running,
            "pause_requested" => CaptureAdmissionState.PauseRequested,
            "paused" => CaptureAdmissionState.Paused,
            _ => throw new InvalidDataException("Capture control state is invalid.")
        };
}
