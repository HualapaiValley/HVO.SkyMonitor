using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Evidence;

/// <summary>Durable lifecycle of one sequenced evidence unit inside the local export outbox.</summary>
public enum ExecutionEvidenceUnitStatus
{
    /// <summary>Enlisted and eligible to send.</summary>
    Pending,

    /// <summary>A recoverable transport or receiver failure deferred the unit until its next attempt time.</summary>
    Retry,

    /// <summary>The receiver returned a terminal acknowledgement; the unit may be retained then pruned.</summary>
    Acknowledged,

    /// <summary>The receiver refused the unit terminally, or the local projection could not produce it.</summary>
    Quarantined,

    /// <summary>An operator explicitly disposed of a quarantined unit. Terminal and never re-sent.</summary>
    Abandoned
}

/// <summary>
/// One export origin. <c>IdentitySha256</c> is the contract's canonical origin identity, which includes the boot
/// session, so a restart creates a new origin with its own sequence space while every unit already enlisted keeps
/// the origin and sequence it was sealed with.
/// </summary>
public sealed record ExecutionEvidenceOriginRecord(
    string IdentitySha256,
    Guid OriginInstallationId,
    Guid AgentInstanceId,
    Guid BootSessionId,
    string SoftwareVersion,
    Guid? ObservatoryId,
    Guid? LogicalCameraInstallationId,
    Guid? InstallationPublicId,
    long NextSequence,
    long AcknowledgedThroughSequence,
    DateTimeOffset CreatedUtc);

/// <summary>One durable evidence unit together with the exact canonical bytes that were sealed at enlistment.</summary>
public sealed record ExecutionEvidenceUnit(
    long RecordId,
    string OriginIdentitySha256,
    long OriginSequence,
    Guid EvidenceId,
    ExecutionEvidenceBodyKind Kind,
    string UnitKey,
    ReadOnlyMemory<byte> Payload,
    string PayloadSha256,
    ExecutionEvidenceUnitStatus Status,
    int AttemptCount,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset NextAttemptUtc,
    string? ReasonCode);

/// <summary>
/// Where the bounded source sweep resumed from. <see cref="DeferredTerminalUnixMs"/> is the ordering key of the
/// oldest terminal execution the sweep saw but could not enlist because a bound refused it; it is durable so a
/// restart still detects that source retention removed evidence the exporter had not yet sealed.
/// </summary>
public sealed record ExecutionEvidenceDiscoveryCursor(
    long TerminalUnixMs,
    string ExecutionId,
    long DeferredTerminalUnixMs,
    long SourcePrunedEvents)
{
    /// <summary>The cursor of a store that has never enlisted anything.</summary>
    public static ExecutionEvidenceDiscoveryCursor Initial { get; } = new(0, string.Empty, 0, 0);
}

/// <summary>
/// One sealed evidence unit. <see cref="PayloadSha256"/> is the contract's canonical payload hash carried inside the
/// envelope, not a hash of the transport bytes: the receiver acknowledges that value, so storing anything else would
/// make every acknowledgement fail to match its own unit.
/// </summary>
public sealed record ExecutionEvidenceSealedUnit(
    Guid EvidenceId,
    ReadOnlyMemory<byte> Payload,
    string PayloadSha256);

/// <summary>One unit offered for enlistment, in the order it must receive its origin sequence.</summary>
public sealed record ExecutionEvidenceEnlistmentUnit(
    ExecutionEvidenceBodyKind Kind,
    string UnitKey,
    Func<long, ExecutionEvidenceSealedUnit> Seal);

/// <summary>Why an enlistment attempt did not enlist every offered unit.</summary>
public enum ExecutionEvidenceEnlistmentDisposition
{
    /// <summary>Every offered unit is now durable, or was already durable with identical canonical bytes.</summary>
    Enlisted,

    /// <summary>Every offered unit was already present with identical canonical bytes; nothing was written.</summary>
    Duplicate,

    /// <summary>A bounded queue, byte, or storage limit refused the write. Nothing was enlisted and nothing dropped.</summary>
    Saturated
}

public sealed record ExecutionEvidenceEnlistmentResult(
    ExecutionEvidenceEnlistmentDisposition Disposition,
    int EnlistedCount,
    long HighestSequence,
    string? ReasonCode = null);

/// <summary>Bounded backlog facts. Every member is a counter or an age, never a payload.</summary>
public sealed record ExecutionEvidenceBacklog(
    long PendingCount,
    long PendingBytes,
    DateTimeOffset? OldestPendingUtc,
    long RetryCount,
    long QuarantinedCount,
    long AbandonedCount,
    long AcknowledgedCount,
    long TotalAttempts,
    long ConflictCount,
    long SourcePrunedEvents,
    long DatabaseBytes,
    long HighestSequence,
    long AcknowledgedThroughSequence)
{
    public static ExecutionEvidenceBacklog Empty { get; } = new(0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>Stable local reason codes for export outcomes that the contract's receiver codes do not cover.</summary>
public static class ExecutionEvidenceExportReasonCodes
{
    public const string Disabled = "export.disabled";
    public const string Initializing = "export.initializing";
    public const string Drained = "export.drained";
    public const string TransportUnavailable = "export.transport-unavailable";
    public const string TransportUnconfigured = "export.transport-unconfigured";
    public const string AuthenticationBlocked = "export.authentication-blocked";
    public const string NegotiationRequired = "export.negotiation-required";
    public const string NegotiationRejected = "export.negotiation-rejected";
    public const string BacklogSaturated = "export.backlog-saturated";
    public const string StoragePressure = "export.storage-pressure";
    public const string StorageSaturated = "export.storage-saturated";
    public const string SourcePruned = "export.source-pruned";
    public const string Quarantined = "export.quarantined";
    public const string ProjectionRejected = "export.projection-rejected";
    public const string DurableStateUnavailable = "export.durable-state-unavailable";
    public const string CycleFailed = "export.cycle-failed";
    public const string AcknowledgementPending = "export.acknowledgement-pending";
    public const string ResyncRequested = "export.resync-requested";

    /// <summary>Bounds a reason code to the durable and metric-safe length without inventing a new value.</summary>
    public static string Bound(string? value)
        => string.IsNullOrWhiteSpace(value) ? CycleFailed
            : value.Length <= 128 ? value : value[..128];
}

public enum ExecutionEvidenceTransportDisposition
{
    /// <summary>The receiver returned a well-formed response for the request that was sent.</summary>
    Completed,

    /// <summary>A recoverable failure. The same request may be retried unchanged.</summary>
    Retry,

    /// <summary>The receiver refused the credential. Retrying without operator action cannot succeed.</summary>
    AuthenticationBlocked,

    /// <summary>The receiver refused the request terminally.</summary>
    Rejected
}

public sealed record ExecutionEvidenceNegotiationTransportResult(
    ExecutionEvidenceTransportDisposition Disposition,
    string ReasonCode,
    ExecutionEvidenceNegotiationResponseV1? Response = null,
    TimeSpan? RetryAfter = null);

public sealed record ExecutionEvidenceSubmitTransportResult(
    ExecutionEvidenceTransportDisposition Disposition,
    string ReasonCode,
    ExecutionEvidenceFeedbackV1? Feedback = null,
    TimeSpan? RetryAfter = null);

/// <summary>
/// The authenticated evidence sink. It is deliberately separate from the fleet heartbeat transport and from the
/// manifest-v2 artifact upload client: an acknowledgement here never means a receiver holds artifact bytes.
/// </summary>
public interface IExecutionEvidenceTransport
{
    /// <summary>
    /// False when no central evidence endpoint or credential is configured. The exporter then performs no source
    /// sweep at all, so a standalone deployment never accumulates evidence it has nowhere to send.
    /// </summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    ValueTask<ExecutionEvidenceNegotiationTransportResult> NegotiateAsync(
        ExecutionEvidenceNegotiationRequestV1 request,
        CancellationToken cancellationToken);

    /// <summary>Submits canonical envelope bytes in ascending origin-sequence order for one origin.</summary>
    ValueTask<ExecutionEvidenceSubmitTransportResult> SubmitAsync(
        string originIdentitySha256,
        IReadOnlyList<byte[]> envelopes,
        CancellationToken cancellationToken);
}

/// <summary>The transport used when the host registered none. It never claims success and never blocks the host.</summary>
internal sealed class NullExecutionEvidenceTransport : IExecutionEvidenceTransport
{
    internal static NullExecutionEvidenceTransport Instance { get; } = new();

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<ExecutionEvidenceNegotiationTransportResult> NegotiateAsync(
        ExecutionEvidenceNegotiationRequestV1 request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new ExecutionEvidenceNegotiationTransportResult(
            ExecutionEvidenceTransportDisposition.Retry,
            ExecutionEvidenceExportReasonCodes.TransportUnconfigured));

    public ValueTask<ExecutionEvidenceSubmitTransportResult> SubmitAsync(
        string originIdentitySha256,
        IReadOnlyList<byte[]> envelopes,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new ExecutionEvidenceSubmitTransportResult(
            ExecutionEvidenceTransportDisposition.Retry,
            ExecutionEvidenceExportReasonCodes.TransportUnconfigured));
}

public enum ExecutionEvidenceExportAvailability
{
    Initializing,

    /// <summary>Draining normally, or intentionally idle with no configured sink.</summary>
    Healthy,

    /// <summary>Backlog, pressure, quarantine, or a recoverable transport failure is present and bounded.</summary>
    Degraded,

    /// <summary>Durable state could not be read or written. Local capture and processing are unaffected.</summary>
    Unhealthy,

    /// <summary>Export is switched off by configuration. No source sweep and no durable growth occur.</summary>
    Disabled
}

/// <summary>Bounded sanitized status. Every member is a counter, an age, a bounded reason code, or a state name.</summary>
public sealed record ExecutionEvidenceExportStateSnapshot(
    ExecutionEvidenceExportAvailability Availability,
    string ReasonCode,
    ExecutionEvidenceBacklog Backlog,
    DateTimeOffset? EvaluatedUtc,
    DateTimeOffset? LastAcknowledgementUtc,
    string? NegotiatedSchemaVersion,
    int InFlightRequests,
    long ResyncRequests,
    long RejectedUnits,
    long ConflictUnits,
    long DrainedUnits,
    bool StoragePressure);

public sealed class ExecutionEvidenceExportState
{
    private ExecutionEvidenceExportStateSnapshot _snapshot = new(
        ExecutionEvidenceExportAvailability.Initializing,
        ExecutionEvidenceExportReasonCodes.Initializing,
        ExecutionEvidenceBacklog.Empty,
        null,
        null,
        null,
        0,
        0,
        0,
        0,
        0,
        false);

    public ExecutionEvidenceExportStateSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal void Update(
        ExecutionEvidenceExportAvailability availability,
        string reasonCode,
        DateTimeOffset evaluatedUtc,
        ExecutionEvidenceBacklog? backlog = null,
        bool? storagePressure = null,
        string? negotiatedSchemaVersion = null,
        DateTimeOffset? lastAcknowledgementUtc = null)
    {
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with
        {
            Availability = availability,
            ReasonCode = ExecutionEvidenceExportReasonCodes.Bound(reasonCode),
            Backlog = backlog ?? current.Backlog,
            EvaluatedUtc = evaluatedUtc,
            LastAcknowledgementUtc = lastAcknowledgementUtc ?? current.LastAcknowledgementUtc,
            NegotiatedSchemaVersion = negotiatedSchemaVersion ?? current.NegotiatedSchemaVersion,
            StoragePressure = storagePressure ?? current.StoragePressure
        });
    }

    internal void RecordCounters(long resyncRequests, long rejectedUnits, long conflictUnits, long drainedUnits)
    {
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with
        {
            ResyncRequests = resyncRequests,
            RejectedUnits = rejectedUnits,
            ConflictUnits = conflictUnits,
            DrainedUnits = drainedUnits
        });
    }

    internal void RecordInFlight(int inFlight)
    {
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with { InFlightRequests = inFlight });
    }
}

/// <summary>
/// Wake-up accelerator only. A missed signal delays a drain by at most one poll interval and never loses durable
/// work, because every eligible unit is discoverable from SQLite alone.
/// </summary>
public sealed class ExecutionEvidenceExportWakeup
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void Signal() => _channel.Writer.TryWrite(true);

    internal async ValueTask WaitAsync(TimeSpan pollInterval, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(pollInterval, timeProvider, timeout.Token);
        var wake = _channel.Reader.ReadAsync(timeout.Token).AsTask();
        var completed = await Task.WhenAny(delay, wake).ConfigureAwait(false);
        await timeout.CancelAsync().ConfigureAwait(false);
        if (completed == wake)
        {
            await wake.ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Metrics for the export lane. Every dimension is a bounded status or reason value; no identity, sequence, or
/// payload value is ever used as a tag.
/// </summary>
public sealed class ExecutionEvidenceExportTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.ExecutionEvidenceExport";
    public const string ActivitySourceName = MeterName;

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _operations;
    private readonly Counter<long> _units;
    private readonly Counter<long> _bytes;
    private readonly Histogram<double> _duration;

    public ExecutionEvidenceExportTelemetry(ExecutionEvidenceExportState state, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _operations = _meter.CreateCounter<long>("hvo.cameraagent.evidence_export.operations", "operation");
        _units = _meter.CreateCounter<long>("hvo.cameraagent.evidence_export.units", "unit");
        _bytes = _meter.CreateCounter<long>("hvo.cameraagent.evidence_export.payload", "By");
        _duration = _meter.CreateHistogram<double>("hvo.cameraagent.evidence_export.operation.duration", "ms");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.pending",
            () => state.Snapshot.Backlog.PendingCount,
            "unit");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.pending.bytes",
            () => state.Snapshot.Backlog.PendingBytes,
            "By");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.oldest.age",
            () => state.Snapshot.Backlog.OldestPendingUtc is { } oldest
                ? Math.Max(0, (timeProvider.GetUtcNow() - oldest).TotalSeconds)
                : 0,
            "s");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.attempts",
            () => state.Snapshot.Backlog.TotalAttempts,
            "attempt");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.quarantined",
            () => state.Snapshot.Backlog.QuarantinedCount,
            "unit");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.conflicts",
            () => state.Snapshot.ConflictUnits,
            "unit");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.rejects",
            () => state.Snapshot.RejectedUnits,
            "unit");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.drained",
            () => state.Snapshot.DrainedUnits,
            "unit");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.resync_requests",
            () => state.Snapshot.ResyncRequests,
            "request");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.in_flight",
            () => state.Snapshot.InFlightRequests,
            "request");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.evidence_export.storage.bytes",
            () => state.Snapshot.Backlog.DatabaseBytes,
            "By");
    }

    internal void Record(string operation, string outcome, TimeSpan duration, long units = 0, long payloadBytes = 0)
    {
        TagList tags = default;
        tags.Add("operation", operation);
        tags.Add("outcome", outcome);
        _operations.Add(1, tags);
        _duration.Record(duration.TotalMilliseconds, tags);
        if (units > 0)
        {
            _units.Add(units, tags);
        }
        if (payloadBytes > 0)
        {
            _bytes.Add(payloadBytes, tags);
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }
}
