using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// The durable CelesTrak GP acquisition authority for one CameraAgent: a STRICT WAL SQLite ledger plus immutable,
/// content-addressed raw, payload and manifest files under one exclusively locked root.
/// </summary>
/// <remarks>
/// Every operation initializes the store on first use, so an operator read that arrives before the hosted
/// refresh job starts still sees a verified store. Provider policy (halt, floor and single flight) is enforced
/// here rather than by callers. A halt is recorded even when cancellation is requested, because losing it would
/// let a later attempt reach a provider that has already refused service. For the same reason an attempt whose
/// provider outcome was never durably recorded (neither a halt, a transport failure nor a valid 200 retrieval) is
/// treated as possibly refused: interrupting it, here or by restart reconciliation, records a provider-wide halt
/// with no HTTP status (<c>orbital.provider-outcome-unknown</c>) unless a halt is already in force.
/// </remarks>
public sealed partial class SqliteOrbitalElementsStore : IOrbitalElementsStore, IDisposable
{
    /// <summary>The CelesTrak usage floor: no configuration may admit outbound attempts more often than this.</summary>
    public static readonly TimeSpan MinimumProviderAttemptInterval = TimeSpan.FromHours(2);

    internal const string ProviderKey = "celestrak";
    internal const int MaximumRawBytes = 32 * 1024 * 1024;
    internal const int MaximumCodeLength = 128;
    internal const int MaximumTextLength = 512;
    internal const string DatabaseFileName = "orbital-elements.db";
    internal const string LockFileName = "orbital-elements.lock";
    internal const string RawDirectoryName = "raw";
    internal const string PayloadDirectoryName = "payloads";
    internal const string ManifestDirectoryName = "manifests";
    internal const string QuarantineDirectoryName = "quarantine";
    internal const string StagingDirectoryName = "staging";

    /// <summary>An attempt that may have reached the provider stopped before its outcome was durably recorded.</summary>
    internal const string ProviderOutcomeUnknownReasonCode = "orbital.provider-outcome-unknown";

    /// <summary>The provider status line was outside 100..599, so the halt records no HTTP status.</summary>
    internal const string InvalidHttpStatusReasonCode = "orbital.invalid-http-status";

    /// <summary>The attempt was cancelled after its valid 200 retrieval was recorded; no halt is implied.</summary>
    internal const string AttemptCancelledReasonCode = "orbital.attempt-cancelled";

    internal const string ProviderOutcomeUnknownHaltReason =
        "orbital.provider-outcome-unknown: no valid HTTP status recorded; a request may have reached the provider but its outcome was not recorded; confirm provider access before resuming";
    private const string PhaseDomain = "hvo-orbital-refresh-phase-v1|";
    private const long PhaseWindowSeconds = 30 * 60;
    private static readonly TimeSpan MaximumAttemptInterval = TimeSpan.FromDays(31);
    private static readonly TimeSpan MaximumMinimumRetention = TimeSpan.FromDays(3650);

    private readonly OrbitalElementsStoreSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqliteOrbitalElementsStore> _logger;
    private readonly IOrbitalStorageFaultInjector _faults;
    private readonly string _rootPath;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Dispose drains the gate rather than disposing it, so a racing operation fails as disposed instead of on a disposed semaphore.")]
    private readonly SemaphoreSlim _gate = new(1, 1);

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "No wait handle is ever materialized; a late pin or unpin must not fail on a disposed semaphore.")]
    private readonly SemaphoreSlim _pinGate = new(1, 1);

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "No wait handle is ever materialized; the reader may still release a load lease during disposal.")]
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private readonly object _pinSync = new();
    private readonly Dictionary<string, int> _pins = new(StringComparer.Ordinal);
    private PhysicalRoot? _root;
    private FileStream? _lock;
    private string? _connectionString;
    private TimeSpan _phaseOffset;
    private volatile bool _initialized;
    private volatile bool _disposed;
    private volatile string? _activeManifestSha256;
    private long _retentionGeneration;

    public SqliteOrbitalElementsStore(
        OrbitalElementsStoreSettings settings,
        TimeProvider timeProvider,
        ILogger<SqliteOrbitalElementsStore> logger)
        : this(settings, timeProvider, logger, NullOrbitalStorageFaultInjector.Instance)
    {
    }

    internal SqliteOrbitalElementsStore(
        OrbitalElementsStoreSettings settings,
        TimeProvider timeProvider,
        ILogger<SqliteOrbitalElementsStore> logger,
        IOrbitalStorageFaultInjector faults)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(faults);
        ValidateSettings(settings);
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
        _faults = faults;
        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.RootDirectory));
    }

    /// <inheritdoc />
    public TimeSpan PhaseOffset => _initialized
        ? _phaseOffset
        : throw new InvalidOperationException("The orbital elements store has not been initialized.");

    internal bool IsInitialized => _initialized;

    /// <summary>The active manifest identity as committed by this owner; null before the first activation.</summary>
    internal string? ActiveManifestSha256 => _activeManifestSha256;

    /// <summary>Advances whenever a snapshot is tombstoned, so readers revalidate cached exact identities.</summary>
    internal long RetentionGeneration => Interlocked.Read(ref _retentionGeneration);

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        => await ExecuteGatedAsync(static (_, _) => ValueTask.FromResult(true), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<OrbitalProviderState> GetStateAsync(CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteUngatedAsync(
            (connection, token) => ReadStateAsync(connection, null, token), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OrbitalAttemptAdmission> TryBeginAttemptAsync(
        string runKey,
        OrbitalAttemptTrigger trigger,
        CancellationToken cancellationToken)
    {
        RequireText(runKey, MaximumCodeLength, nameof(runKey));
        if (!Enum.IsDefined(trigger))
        {
            throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown orbital attempt trigger.");
        }
        return await ExecuteGatedAsync(async (connection, token) =>
        {
            using var transaction = BeginImmediate(connection);
            var policy = await ReadPolicyAsync(connection, transaction, token).ConfigureAwait(false);
            var now = Truncate(Now());
            DateTimeOffset? floor = policy.LastAttemptStartedUnixMs is { } last
                ? FromUnixMilliseconds(last) + _settings.MinimumAttemptInterval
                : null;
            if (policy.Halt is { } halt)
            {
                return new OrbitalAttemptAdmission(OrbitalAdmissionStatus.Halted, null, floor, halt);
            }
            if (floor is { } next && now < next)
            {
                return new OrbitalAttemptAdmission(OrbitalAdmissionStatus.FloorNotElapsed, null, next, null);
            }
            if (policy.InFlightAttemptId is not null)
            {
                return new OrbitalAttemptAdmission(OrbitalAdmissionStatus.AttemptInFlight, null, floor, null);
            }
            var attemptId = Guid.NewGuid().ToString("N");
            var sequence = policy.AttemptSequence + 1;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO orbital_attempts(attempt_sequence, attempt_id, run_key, trigger_kind, started_unix_ms, outcome)
                VALUES($sequence, $attempt, $run, $trigger, $now, 'InFlight');
                """, token,
                ("$sequence", sequence), ("$attempt", attemptId), ("$run", runKey),
                ("$trigger", trigger.ToString()), ("$now", ToUnixMilliseconds(now))).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                UPDATE orbital_provider_state
                SET attempt_sequence = $sequence, last_attempt_started_unix_ms = $now, revision = revision + 1
                WHERE provider = 'celestrak';
                """, token, ("$sequence", sequence), ("$now", ToUnixMilliseconds(now))).ConfigureAwait(false);
            // The ledger row and the floor are durable before any outbound request can be made.
            await transaction.CommitAsync(token).ConfigureAwait(false);
            AttemptAdmitted(_logger, attemptId, trigger.ToString());
            return new OrbitalAttemptAdmission(
                OrbitalAdmissionStatus.Admitted, attemptId, now + _settings.MinimumAttemptInterval, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordHaltAsync(
        string attemptId,
        int? httpStatusCode,
        string reason,
        CancellationToken cancellationToken)
    {
        RequireAttemptId(attemptId);
        if (httpStatusCode is < 100 or > 599 or 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(httpStatusCode), httpStatusCode, "A provider halt requires a non-200 HTTP status from 100 to 599, or none.");
        }
        ArgumentNullException.ThrowIfNull(reason);
        // Bound first: a reason made only of control characters becomes blank and takes the default.
        var boundedReason = BoundText(reason);
        var normalizedReason = string.IsNullOrWhiteSpace(boundedReason)
            ? httpStatusCode is { } defaultStatus
                ? string.Create(CultureInfo.InvariantCulture, $"provider returned HTTP {defaultStatus}")
                : string.Concat(InvalidHttpStatusReasonCode, ": no valid HTTP status recorded; the provider status line was outside 100-599")
            : boundedReason;
        var attemptReasonCode = httpStatusCode is { } status
            ? string.Create(CultureInfo.InvariantCulture, $"orbital.provider-http-{status}")
            : InvalidHttpStatusReasonCode;
        object statusValue = httpStatusCode.HasValue ? httpStatusCode.Value : DBNull.Value;
        // Deliberately not cancellable: a lost halt would permit another request to a refusing provider.
        _ = cancellationToken;
        await ExecuteGatedAsync(async (connection, token) =>
        {
            using var transaction = BeginImmediate(connection);
            await RequireInFlightAsync(connection, transaction, attemptId, token).ConfigureAwait(false);
            var now = ToUnixMilliseconds(Truncate(Now()));
            await CompleteAttemptAsync(connection, transaction, attemptId, OrbitalAttemptOutcome.ProviderHalted,
                attemptReasonCode, normalizedReason, now, token, httpStatus: httpStatusCode).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                UPDATE orbital_provider_state
                SET halt_since_unix_ms = $now, halt_http_status = $status, halt_reason = $reason, revision = revision + 1
                WHERE provider = 'celestrak';
                """, token, ("$now", now), ("$status", statusValue), ("$reason", normalizedReason)).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            if (httpStatusCode is { } logged)
            {
                ProviderHalted(_logger, logged, attemptId);
            }
            else
            {
                ProviderHaltedInvalidStatus(_logger, attemptId);
            }
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordRetrievalAsync(
        string attemptId,
        DateTimeOffset retrievedUtc,
        CancellationToken cancellationToken)
    {
        RequireAttemptId(attemptId);
        // Deliberately not cancellable: this record is what distinguishes a received response from an unknown outcome.
        _ = cancellationToken;
        await ExecuteGatedAsync(async (connection, token) =>
        {
            using var transaction = BeginImmediate(connection);
            await RequireInFlightAsync(connection, transaction, attemptId, token).ConfigureAwait(false);
            var ticks = retrievedUtc.UtcTicks;
            var changed = await ExecuteAsync(connection, transaction, """
                UPDATE orbital_attempts SET retrieved_utc_ticks = $ticks
                WHERE attempt_id = $attempt AND outcome = 'InFlight' AND retrieved_utc_ticks IS NULL;
                """, token, ("$ticks", ticks), ("$attempt", attemptId)).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException("The orbital attempt already has a recorded retrieval.");
            }
            await ExecuteAsync(connection, transaction, """
                UPDATE orbital_provider_state
                SET last_retrieval_success_utc_ticks = $ticks, revision = revision + 1
                WHERE provider = 'celestrak';
                """, token, ("$ticks", ticks)).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RecordInterruptedAsync(string attemptId, CancellationToken cancellationToken)
    {
        RequireAttemptId(attemptId);
        // Deliberately not cancellable: it is called after cancellation, and a lost fail-safe halt would permit
        // another request to a provider that may have refused service.
        _ = cancellationToken;
        return await ExecuteGatedAsync(async (connection, token) =>
        {
            using var transaction = BeginImmediate(connection);
            await RequireInFlightAsync(connection, transaction, attemptId, token).ConfigureAwait(false);
            var retrieved = await ScalarLongAsync(connection, transaction,
                "SELECT retrieved_utc_ticks IS NOT NULL FROM orbital_attempts WHERE attempt_id = $attempt;", token,
                ("$attempt", attemptId)).ConfigureAwait(false) != 0;
            var now = ToUnixMilliseconds(Truncate(Now()));
            var haltRecorded = false;
            if (retrieved)
            {
                await CompleteAttemptAsync(connection, transaction, attemptId, OrbitalAttemptOutcome.Interrupted,
                    AttemptCancelledReasonCode,
                    "The attempt was cancelled after a valid provider response was recorded.", now, token)
                    .ConfigureAwait(false);
            }
            else
            {
                await CompleteAttemptAsync(connection, transaction, attemptId, OrbitalAttemptOutcome.Interrupted,
                    ProviderOutcomeUnknownReasonCode,
                    "The attempt was cancelled after the request may have reached the provider and before its outcome was recorded.",
                    now, token).ConfigureAwait(false);
                haltRecorded = await ExecuteAsync(connection, transaction, """
                    UPDATE orbital_provider_state
                    SET halt_since_unix_ms = $now, halt_http_status = NULL, halt_reason = $reason
                    WHERE provider = 'celestrak' AND halt_since_unix_ms IS NULL;
                    """, token, ("$now", now), ("$reason", ProviderOutcomeUnknownHaltReason)).ConfigureAwait(false) > 0;
            }
            await BumpRevisionAsync(connection, transaction, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            if (retrieved)
            {
                AttemptInterrupted(_logger, attemptId);
            }
            else
            {
                ProviderOutcomeUnknown(_logger, attemptId, haltRecorded);
            }
            return !retrieved;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordTransportFailureAsync(
        string attemptId,
        string reasonCode,
        string detail,
        CancellationToken cancellationToken)
    {
        RequireAttemptId(attemptId);
        RequireReasonCode(reasonCode);
        ArgumentNullException.ThrowIfNull(detail);
        var boundedDetail = BoundText(detail);
        await ExecuteGatedAsync(async (connection, token) =>
        {
            using var transaction = BeginImmediate(connection);
            await RequireInFlightAsync(connection, transaction, attemptId, token).ConfigureAwait(false);
            await CompleteAttemptAsync(connection, transaction, attemptId, OrbitalAttemptOutcome.TransportFailed,
                reasonCode, boundedDetail, ToUnixMilliseconds(Truncate(Now())), token).ConfigureAwait(false);
            await BumpRevisionAsync(connection, transaction, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            TransportFailed(_logger, reasonCode, attemptId);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The idempotency key is checked first: the same key and payload replays as <see cref="OrbitalCommandStatus.Unchanged"/>
    /// even after the halt is cleared, and the same key with another payload is a conflict. A new key with no halt in
    /// force is a conflict (<c>orbital.no-halt</c>), not a silent success. Resume never moves the attempt floor.
    /// </remarks>
    public async ValueTask<OrbitalCommandResult> ResumeProviderAsync(
        OrbitalResumeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteGatedAsync(async (connection, token) =>
        {
            var invalid = ValidateResume(request);
            if (invalid is not null)
            {
                return new OrbitalCommandResult(OrbitalCommandStatus.Invalid, invalid,
                    await ReadStateAsync(connection, null, token).ConfigureAwait(false));
            }
            var payloadSha256 = ResumePayloadSha256(request);
            using var transaction = BeginImmediate(connection);
            var recorded = await ScalarStringAsync(connection, transaction,
                "SELECT payload_sha256 FROM orbital_commands WHERE idempotency_key = $key;", token,
                ("$key", request.IdempotencyKey)).ConfigureAwait(false);
            if (recorded is not null)
            {
                var replay = string.Equals(recorded, payloadSha256, StringComparison.Ordinal);
                return new OrbitalCommandResult(
                    replay ? OrbitalCommandStatus.Unchanged : OrbitalCommandStatus.Conflict,
                    replay ? "orbital.resume-replayed" : "orbital.idempotency-conflict",
                    await ReadStateAsync(connection, transaction, token).ConfigureAwait(false));
            }
            var policy = await ReadPolicyAsync(connection, transaction, token).ConfigureAwait(false);
            if (policy.Halt is not { } halt)
            {
                return new OrbitalCommandResult(OrbitalCommandStatus.Conflict, "orbital.no-halt",
                    await ReadStateAsync(connection, transaction, token).ConfigureAwait(false));
            }
            if (halt.SinceUtc.UtcTicks != request.ExpectedHaltSinceUtc.UtcTicks)
            {
                return new OrbitalCommandResult(OrbitalCommandStatus.Conflict, "orbital.halt-mismatch",
                    await ReadStateAsync(connection, transaction, token).ConfigureAwait(false));
            }
            await ExecuteAsync(connection, transaction, """
                UPDATE orbital_provider_state
                SET halt_since_unix_ms = NULL, halt_http_status = NULL, halt_reason = NULL, revision = revision + 1
                WHERE provider = 'celestrak';
                """, token).ConfigureAwait(false);
            var revision = await ScalarLongAsync(connection, transaction,
                "SELECT revision FROM orbital_provider_state WHERE provider = 'celestrak';", token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO orbital_commands(idempotency_key, command_kind, payload_sha256, halt_since_unix_ms,
                    halt_http_status, actor, reason, revision, recorded_unix_ms)
                VALUES($key, 'resume', $payload, $since, $status, $actor, $reason, $revision, $now);
                """, token,
                ("$key", request.IdempotencyKey), ("$payload", payloadSha256),
                ("$since", ToUnixMilliseconds(halt.SinceUtc)),
                ("$status", halt.HttpStatusCode.HasValue ? halt.HttpStatusCode.Value : DBNull.Value),
                ("$actor", request.Actor), ("$reason", request.Reason), ("$revision", revision),
                ("$now", ToUnixMilliseconds(Truncate(Now())))).ConfigureAwait(false);
            var state = await ReadStateAsync(connection, transaction, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            if (halt.HttpStatusCode is { } resumedStatus)
            {
                ProviderResumed(_logger, resumedStatus, state.Revision);
            }
            else
            {
                ProviderResumedWithoutStatus(_logger, state.Revision);
            }
            return new OrbitalCommandResult(OrbitalCommandStatus.Applied, "orbital.resumed", state);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> AddReferenceAsync(string manifestSha256, string holder, CancellationToken cancellationToken)
    {
        RequireSha256(manifestSha256, nameof(manifestSha256));
        RequireText(holder, MaximumCodeLength, nameof(holder));
        return await ExecuteGatedAsync(async (connection, token) =>
        {
            using var transaction = BeginImmediate(connection);
            var state = await ScalarStringAsync(connection, transaction,
                "SELECT state FROM orbital_snapshots WHERE manifest_sha256 = $sha;", token,
                ("$sha", manifestSha256)).ConfigureAwait(false);
            if (!string.Equals(state, "published", StringComparison.Ordinal))
            {
                return false;
            }
            await ExecuteAsync(connection, transaction, """
                INSERT OR IGNORE INTO orbital_snapshot_references(manifest_sha256, holder, created_unix_ms)
                VALUES($sha, $holder, $now);
                """, token, ("$sha", manifestSha256), ("$holder", holder),
                ("$now", ToUnixMilliseconds(Truncate(Now())))).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveReferenceAsync(string manifestSha256, string holder, CancellationToken cancellationToken)
    {
        RequireSha256(manifestSha256, nameof(manifestSha256));
        RequireText(holder, MaximumCodeLength, nameof(holder));
        return await ExecuteGatedAsync(async (connection, token) =>
            await ExecuteAsync(connection, null,
                "DELETE FROM orbital_snapshot_references WHERE manifest_sha256 = $sha AND holder = $holder;", token,
                ("$sha", manifestSha256), ("$holder", holder)).ConfigureAwait(false) == 1,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Draining the gate means the root lock is released only after any admitted operation has finished.
        _gate.Wait();
        try
        {
            ReleaseLock();
            _initialized = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Initializes on first use; later calls cost only a volatile read.</summary>
    internal async ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Holds a snapshot against retirement while it is loaded. Pinning and tombstoning are mutually exclusive.</summary>
    internal async ValueTask<OrbitalSnapshotPin> PinAsync(string manifestSha256, CancellationToken cancellationToken)
    {
        await _pinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_pinSync)
            {
                _pins[manifestSha256] = _pins.GetValueOrDefault(manifestSha256) + 1;
            }
        }
        finally
        {
            _pinGate.Release();
        }
        return new OrbitalSnapshotPin(this, manifestSha256);
    }

    internal bool IsPinned(string manifestSha256)
    {
        lock (_pinSync)
        {
            return _pins.ContainsKey(manifestSha256);
        }
    }

    internal void Unpin(string manifestSha256)
    {
        lock (_pinSync)
        {
            if (_pins.TryGetValue(manifestSha256, out var count))
            {
                if (count <= 1)
                {
                    _pins.Remove(manifestSha256);
                }
                else
                {
                    _pins[manifestSha256] = count - 1;
                }
            }
        }
    }

    /// <summary>Serializes snapshot materialization so concurrent cache misses do not each load the payload.</summary>
    internal async ValueTask<IDisposable> AcquireLoadLeaseAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new LoadLease(_loadGate);
    }

    /// <summary>The committed row for an exact manifest identity, read without the write gate.</summary>
    internal async ValueTask<OrbitalStoredSnapshot?> FindSnapshotAsync(string manifestSha256, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await ExecuteUngatedAsync(async (connection, token) =>
        {
            using var command = CreateCommand(connection, null, """
                SELECT state, manifest_file_sha256, manifest_length_bytes, payload_sha256, payload_length_bytes
                FROM orbital_snapshots WHERE manifest_sha256 = $sha;
                """, ("$sha", manifestSha256));
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }
            return new OrbitalStoredSnapshot(
                manifestSha256,
                string.Equals(reader.GetString(0), "retired", StringComparison.Ordinal),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt64(4));
        }, cancellationToken).ConfigureAwait(false);
    }

    internal string ManifestPath(string manifestSha256) => FilePath(ManifestDirectoryName, manifestSha256);

    internal string PayloadPath(string payloadSha256) => FilePath(PayloadDirectoryName, payloadSha256);

    internal string RawPath(string rawSha256) => FilePath(RawDirectoryName, rawSha256);

    internal string QuarantinePath(string rawSha256) => FilePath(QuarantineDirectoryName, rawSha256);

    /// <summary>Re-proves that no link has been introduced between the root and an orbital file before it is touched.</summary>
    internal void VerifyContained(string absolutePath, string operation)
        => (_root ?? throw new InvalidOperationException("The orbital elements store has not been initialized."))
            .Verify(absolutePath, operation);

    private string FilePath(string directory, string sha256)
        => Path.Combine(_rootPath, directory, sha256 + ".json");

    private async ValueTask<T> ExecuteGatedAsync<T>(
        Func<SqliteConnection, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureInitializedCoreAsync(cancellationToken).ConfigureAwait(false);
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await operation(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (TranslateFault(exception, cancellationToken) is { } translated)
        {
            throw translated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<T> ExecuteUngatedAsync<T>(
        Func<SqliteConnection, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await operation(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (TranslateFault(exception, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Database and filesystem faults become one stable <see cref="OrbitalStorageException"/>; a cancelled
    /// filesystem primitive becomes cancellation again. Fail-closed schema, argument and state errors pass through.
    /// </summary>
    private static Exception? TranslateFault(Exception exception, CancellationToken cancellationToken)
        => exception switch
        {
            OrbitalStorageException => null,
            FileSystemFaultException { Kind: FileSystemFaultKind.Cancelled } fault
                => new OperationCanceledException("The orbital storage operation was cancelled.", fault, cancellationToken),
            SqliteException or IOException or UnauthorizedAccessException
                => new OrbitalStorageException(ClassifyFault(exception), "The orbital elements store could not complete durably.", exception),
            _ => null
        };

    internal static string ClassifyFault(Exception exception)
    {
        if (exception is OrbitalStorageException storage)
        {
            return storage.ReasonCode;
        }
        if (exception is SqliteException)
        {
            return "orbital.storage-database";
        }
        return FileSystemFaultException.From("orbital-store", string.Empty, exception).Kind switch
        {
            FileSystemFaultKind.NoSpace => "orbital.storage-no-space",
            FileSystemFaultKind.Containment => "orbital.storage-containment",
            FileSystemFaultKind.PermissionDenied => "orbital.storage-permission",
            _ => OrbitalStorageException.DefaultReasonCode
        };
    }

    private async ValueTask EnsureInitializedCoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }
        try
        {
            AcquireRoot();
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var reconciliation = await EnsureSchemaAndReconcileLedgerAsync(connection, cancellationToken).ConfigureAwait(false);
            if (reconciliation.UnknownOutcomeAttemptId is { } unknown)
            {
                ProviderOutcomeUnknown(_logger, unknown, reconciliation.HaltRecorded);
            }
            var seed = await ReadSeedAsync(connection, cancellationToken).ConfigureAwait(false);
            _phaseOffset = ComputePhaseOffset(seed);
            _activeManifestSha256 = await ScalarStringAsync(connection, null,
                "SELECT active_manifest_sha256 FROM orbital_provider_state WHERE provider = 'celestrak';",
                cancellationToken).ConfigureAwait(false);
            var orphans = await ReconcileFilesAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
            StoreInitialized(_logger, reconciliation.Interrupted, orphans);
        }
        catch
        {
            ReleaseLock();
            throw;
        }
    }

    /// <summary>Creates the layout and takes the exclusive root lock that makes this the only owner.</summary>
    private void AcquireRoot()
    {
        if (_lock is not null && _root is not null)
        {
            return;
        }
        Directory.CreateDirectory(_rootPath);
        var root = PhysicalRoot.Open(_rootPath);
        var lockPath = root.Resolve(LockFileName);
        try
        {
            _lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException exception) when (exception is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new OrbitalStorageException("orbital.storage-locked",
                "Another orbital elements store already owns this root.", exception);
        }
        foreach (var directory in new[] { RawDirectoryName, PayloadDirectoryName, ManifestDirectoryName, QuarantineDirectoryName, StagingDirectoryName })
        {
            AtomicPublisher.EnsureDirectory(root, root.Resolve(directory));
        }
        _root = root;
    }

    private void ReleaseLock()
    {
        _lock?.Dispose();
        _lock = null;
        _root = null;
    }

    private static void ValidateSettings(OrbitalElementsStoreSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.RootDirectory) || !Path.IsPathFullyQualified(settings.RootDirectory))
        {
            throw new ArgumentException("The orbital elements root must be a fully qualified directory.", nameof(settings));
        }
        if (settings.MinimumAttemptInterval < MinimumProviderAttemptInterval || settings.MinimumAttemptInterval > MaximumAttemptInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.MinimumAttemptInterval,
                "The orbital attempt interval must be at least two hours and at most 31 days.");
        }
        if (settings.MaximumRetainedSnapshots is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.MaximumRetainedSnapshots,
                "Between 1 and 1024 orbital snapshots may be retained.");
        }
        if (settings.MinimumRetention < TimeSpan.Zero || settings.MinimumRetention > MaximumMinimumRetention)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.MinimumRetention,
                "The minimum orbital snapshot retention must be from zero to 3650 days.");
        }
        if (settings.MaximumQuarantinedPayloads is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.MaximumQuarantinedPayloads,
                "From 0 to 64 quarantined orbital candidates may be retained.");
        }
    }

    /// <summary>SHA-256 over the domain-separated seed, read as a big-endian integer, reduced into [0, 30 min).</summary>
    internal static TimeSpan ComputePhaseOffset(ReadOnlySpan<byte> seed)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(PhaseDomain + Convert.ToHexString(seed)));
        var value = BinaryPrimitives.ReadUInt64BigEndian(digest);
        return TimeSpan.FromSeconds((long)(value % PhaseWindowSeconds));
    }

    private static async ValueTask<byte[]> ReadSeedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, null, "SELECT seed FROM orbital_installation WHERE installation_key = 1;");
        var seed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        return seed is { Length: 32 }
            ? seed
            : throw new InvalidDataException("The orbital installation seed is missing or malformed.");
    }

    private async ValueTask<OrbitalProviderState> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT p.revision, p.active_manifest_sha256, s.payload_sha256, s.record_count, s.object_count,
                s.minimum_epoch_utc_ticks, s.maximum_epoch_utc_ticks, p.last_attempt_started_unix_ms,
                a.outcome, a.run_key, a.reason_code, p.last_retrieval_success_utc_ticks, p.last_activation_unix_ms,
                p.halt_since_unix_ms, p.halt_http_status, p.halt_reason,
                p.quarantine_observed_unix_ms, p.quarantine_raw_sha256, p.quarantine_reason
            FROM orbital_provider_state AS p
            LEFT JOIN orbital_snapshots AS s ON s.manifest_sha256 = p.active_manifest_sha256
            LEFT JOIN orbital_attempts AS a ON a.attempt_sequence = p.attempt_sequence
            WHERE p.provider = 'celestrak';
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The orbital provider state row is missing.");
        }
        var lastAttempt = NullableLong(reader, 7);
        return new OrbitalProviderState(
            reader.GetInt64(0),
            NullableString(reader, 1),
            NullableString(reader, 2),
            NullableInt(reader, 3),
            NullableInt(reader, 4),
            NullableTicks(reader, 5),
            NullableTicks(reader, 6),
            lastAttempt is { } attempted ? FromUnixMilliseconds(attempted) : null,
            NullableString(reader, 8) is { } outcome ? ParseOutcome(outcome) : null,
            NullableString(reader, 9),
            NullableString(reader, 10),
            NullableTicks(reader, 11),
            NullableLong(reader, 12) is { } activated ? FromUnixMilliseconds(activated) : null,
            lastAttempt is { } floor ? FromUnixMilliseconds(floor) + _settings.MinimumAttemptInterval : null,
            NullableLong(reader, 13) is { } since
                ? new OrbitalProviderHalt(FromUnixMilliseconds(since), NullableInt(reader, 14), reader.GetString(15))
                : null,
            NullableLong(reader, 16) is { } observed
                ? new OrbitalCandidateQuarantine(FromUnixMilliseconds(observed), NullableString(reader, 17), reader.GetString(18))
                : null);
    }

    /// <summary>The restart settlement: interrupted attempts, the attempt whose outcome was unknown, and whether it halted.</summary>
    private readonly record struct LedgerReconciliation(int Interrupted, string? UnknownOutcomeAttemptId, bool HaltRecorded);

    private sealed record ProviderPolicy(long AttemptSequence, long? LastAttemptStartedUnixMs, OrbitalProviderHalt? Halt,
        string? InFlightAttemptId);

    private static async ValueTask<ProviderPolicy> ReadPolicyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT attempt_sequence, last_attempt_started_unix_ms, halt_since_unix_ms, halt_http_status, halt_reason,
                (SELECT attempt_id FROM orbital_attempts WHERE outcome = 'InFlight')
            FROM orbital_provider_state WHERE provider = 'celestrak';
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The orbital provider state row is missing.");
        }
        return new ProviderPolicy(
            reader.GetInt64(0),
            NullableLong(reader, 1),
            NullableLong(reader, 2) is { } since
                ? new OrbitalProviderHalt(FromUnixMilliseconds(since), NullableInt(reader, 3), reader.GetString(4))
                : null,
            NullableString(reader, 5));
    }

    private static async ValueTask RequireInFlightAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        CancellationToken cancellationToken)
    {
        var outcome = await ScalarStringAsync(connection, transaction,
            "SELECT outcome FROM orbital_attempts WHERE attempt_id = $attempt;", cancellationToken,
            ("$attempt", attemptId)).ConfigureAwait(false);
        if (outcome is null)
        {
            throw new InvalidOperationException("The orbital attempt is unknown.");
        }
        if (!string.Equals(outcome, nameof(OrbitalAttemptOutcome.InFlight), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The orbital attempt has already completed.");
        }
    }

    private static async ValueTask CompleteAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        OrbitalAttemptOutcome outcome,
        string reasonCode,
        string? detail,
        long completedUnixMs,
        CancellationToken cancellationToken,
        int? httpStatus = null,
        string? manifestSha256 = null,
        string? rawSha256 = null,
        long? rawLengthBytes = null,
        bool quarantineRetained = false)
    {
        var changed = await ExecuteAsync(connection, transaction, """
            UPDATE orbital_attempts
            SET outcome = $outcome, completed_unix_ms = $now, reason_code = $reason, detail = $detail,
                http_status = $status, manifest_sha256 = $manifest, raw_sha256 = $raw, raw_length_bytes = $length,
                quarantine_retained = $retained
            WHERE attempt_id = $attempt AND outcome = 'InFlight';
            """, cancellationToken,
            ("$outcome", outcome.ToString()), ("$now", completedUnixMs), ("$reason", reasonCode),
            ("$detail", (object?)detail ?? DBNull.Value), ("$status", httpStatus.HasValue ? httpStatus.Value : DBNull.Value),
            ("$manifest", (object?)manifestSha256 ?? DBNull.Value), ("$raw", (object?)rawSha256 ?? DBNull.Value),
            ("$length", rawLengthBytes.HasValue ? rawLengthBytes.Value : DBNull.Value), ("$retained", quarantineRetained ? 1 : 0),
            ("$attempt", attemptId)).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException("The orbital attempt has already completed.");
        }
    }

    private static async ValueTask BumpRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
        => await ExecuteAsync(connection, transaction,
            "UPDATE orbital_provider_state SET revision = revision + 1 WHERE provider = 'celestrak';",
            cancellationToken).ConfigureAwait(false);

    private static string? ValidateResume(OrbitalResumeRequest request)
    {
        if (request.ExpectedHaltSinceUtc.Offset != TimeSpan.Zero)
        {
            return "orbital.resume-invalid-halt-time";
        }
        if (!IsBoundedText(request.IdempotencyKey, MaximumCodeLength))
        {
            return "orbital.resume-invalid-idempotency-key";
        }
        if (!IsBoundedText(request.Actor, MaximumCodeLength))
        {
            return "orbital.resume-invalid-actor";
        }
        return IsBoundedText(request.Reason, MaximumTextLength) ? null : "orbital.resume-invalid-reason";
    }

    /// <summary>Length-prefixed, so no actor/reason split can collide with another.</summary>
    private static string ResumePayloadSha256(OrbitalResumeRequest request)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"resume|{request.ExpectedHaltSinceUtc.UtcTicks}|{request.Actor.Length}:{request.Actor}|{request.Reason.Length}:{request.Reason}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void RequireAttemptId(string attemptId)
    {
        ArgumentNullException.ThrowIfNull(attemptId);
        if (attemptId.Length != 32 || !attemptId.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("An orbital attempt identity is 32 lowercase hexadecimal characters.", nameof(attemptId));
        }
    }

    internal static void RequireReasonCode(string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(reasonCode);
        if (reasonCode.Length is 0 or > MaximumCodeLength ||
            !reasonCode.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':'))
        {
            throw new ArgumentException("An orbital reason code is 1 to 128 characters of [A-Za-z0-9._:-].", nameof(reasonCode));
        }
    }

    private static void RequireSha256(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!OrbitalContract.IsSha256(value))
        {
            throw new ArgumentException("An orbital identity is 64 uppercase hexadecimal characters.", parameterName);
        }
    }

    private static void RequireText(string value, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsBoundedText(value, maximum))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The value must be non-blank, at most {maximum} characters and free of control characters."),
                parameterName);
        }
    }

    private static bool IsBoundedText(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    /// <summary>Replaces control characters and trims to the stored bound without splitting a surrogate pair.</summary>
    internal static string BoundText(string value)
    {
        var sanitized = string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = char.IsControl(source[index]) ? ' ' : source[index];
            }
        });
        if (sanitized.Length <= MaximumTextLength)
        {
            return sanitized;
        }
        var length = char.IsHighSurrogate(sanitized[MaximumTextLength - 1]) ? MaximumTextLength - 1 : MaximumTextLength;
        return sanitized[..length];
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static DateTimeOffset Truncate(DateTimeOffset value)
        => new(value.UtcDateTime.AddTicks(-(value.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond)), TimeSpan.Zero);

    private static long ToUnixMilliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnixMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static OrbitalAttemptOutcome ParseOutcome(string value)
        => Enum.TryParse<OrbitalAttemptOutcome>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException("Orbital storage contains an unknown attempt outcome.");

    private static string? NullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? NullableLong(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static int? NullableInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTimeOffset? NullableTicks(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : new DateTimeOffset(reader.GetInt64(ordinal), TimeSpan.Zero);

    private sealed class LoadLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

/// <summary>The committed identity, lifecycle and file bindings of one stored snapshot.</summary>
internal sealed record OrbitalStoredSnapshot(string ManifestSha256, bool IsRetired, string ManifestFileSha256,
    long ManifestLengthBytes, string PayloadSha256, long PayloadLengthBytes);

/// <summary>Releases a loading pin exactly once.</summary>
internal sealed class OrbitalSnapshotPin : IDisposable
{
    private readonly SqliteOrbitalElementsStore _store;
    private int _released;

    internal OrbitalSnapshotPin(SqliteOrbitalElementsStore store, string manifestSha256)
    {
        _store = store;
        ManifestSha256 = manifestSha256;
    }

    internal string ManifestSha256 { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _store.Unpin(ManifestSha256);
        }
    }
}
