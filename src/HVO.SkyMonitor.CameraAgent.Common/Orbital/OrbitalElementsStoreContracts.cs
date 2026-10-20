using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// Durable orbital-element store configuration. The root is a dedicated directory, normally
/// <c>{RawIngressRoot}/.orbital-elements/</c>, that only one store instance may own at a time.
/// </summary>
public sealed record OrbitalElementsStoreSettings(string RootDirectory, TimeSpan MinimumAttemptInterval,
    int MaximumRetainedSnapshots, TimeSpan MinimumRetention, int MaximumQuarantinedPayloads = 4);

public enum OrbitalAttemptTrigger { Scheduled, Manual }

public enum OrbitalAttemptOutcome { InFlight, Activated, UnchangedPayload, ProviderHalted, TransportFailed, Quarantined, StorageFailed, Interrupted }

public enum OrbitalAdmissionStatus { Admitted, Halted, FloorNotElapsed, AttemptInFlight }

/// <summary>An admitted attempt carries the durable attempt identity that every completion call must quote.</summary>
public sealed record OrbitalAttemptAdmission(OrbitalAdmissionStatus Status, string? AttemptId,
    DateTimeOffset? NextPermittedAttemptUtc, OrbitalProviderHalt? Halt);

/// <summary>
/// Durable provider state. Times are UTC. <see cref="NextPermittedAttemptUtc"/> is the floor from the last
/// outbound attempt and is null only before the first attempt; a halt is reported separately and does not move it.
/// </summary>
public sealed record OrbitalProviderState(long Revision, string? CurrentManifestSha256, string? CurrentPayloadSha256,
    int? CurrentRecordCount, int? CurrentObjectCount, DateTimeOffset? CurrentMinimumEpochUtc, DateTimeOffset? CurrentMaximumEpochUtc,
    DateTimeOffset? LastAttemptUtc, OrbitalAttemptOutcome? LastAttemptOutcome, string? LastAttemptRunKey, string? LastAttemptReasonCode,
    DateTimeOffset? LastRetrievalSuccessUtc, DateTimeOffset? LastActivationUtc, DateTimeOffset? NextPermittedAttemptUtc,
    OrbitalProviderHalt? Halt, OrbitalCandidateQuarantine? Quarantine);

public sealed record OrbitalPublication(string ManifestSha256, string PayloadSha256, bool PayloadUnchanged,
    int RecordCount, int ObjectCount);

public sealed record OrbitalResumeRequest(DateTimeOffset ExpectedHaltSinceUtc, string IdempotencyKey, string Actor, string Reason);

public enum OrbitalCommandStatus { Applied, Unchanged, Conflict, Invalid }

public sealed record OrbitalCommandResult(OrbitalCommandStatus Status, string? ReasonCode, OrbitalProviderState State);

public sealed record OrbitalRetentionResult(int Retired, int FilesDeleted, int Protected, int Refused);

/// <summary>
/// Durable CelesTrak GP acquisition state: provider policy (halt, floor, single flight), immutable publication,
/// atomic activation with append-only history, bounded quarantine evidence and tombstoned retention.
/// </summary>
public interface IOrbitalElementsStore
{
    /// <summary>Creates or verifies the schema (fail closed), creates the installation seed once, reconciles and clears staging.</summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Deterministic, in [0, 30 min), from the persisted installation seed; throws before initialization.</summary>
    TimeSpan PhaseOffset { get; }

    ValueTask<OrbitalProviderState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Halt check, floor check, single flight, then commits an InFlight attempt row and last_attempt = now BEFORE returning Admitted.</summary>
    ValueTask<OrbitalAttemptAdmission> TryBeginAttemptAsync(string runKey, OrbitalAttemptTrigger trigger, CancellationToken cancellationToken);

    /// <summary>
    /// Records a provider-wide halt and completes the attempt as ProviderHalted. <paramref name="httpStatusCode"/> is
    /// the non-200 status from 100 to 599, or null when the status line was outside that range
    /// (<c>orbital.invalid-http-status</c>). Not cancellable: a lost halt would permit another request.
    /// </summary>
    ValueTask RecordHaltAsync(string attemptId, int? httpStatusCode, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Durably records that the InFlight attempt received a valid HTTP 200 response at <paramref name="retrievedUtc"/>
    /// and advances the last successful retrieval, before the response is normalized. This is the attempt's provider
    /// outcome: an attempt interrupted after it is settled as a plain interruption rather than a fail-safe halt. Not
    /// cancellable. Throws <see cref="InvalidOperationException"/> when the attempt is not in flight or already has one.
    /// </summary>
    ValueTask RecordRetrievalAsync(string attemptId, DateTimeOffset retrievedUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Settles a cancelled InFlight attempt as Interrupted. When no provider outcome was recorded the request may have
    /// reached the provider, so it is settled as <c>orbital.provider-outcome-unknown</c> and a provider-wide halt with no
    /// HTTP status is recorded unless a halt is already in force; returns true in that case. After a recorded
    /// retrieval it is settled as <c>orbital.attempt-cancelled</c> and returns false. Not cancellable. Throws
    /// <see cref="InvalidOperationException"/> when the attempt is unknown or already terminal.
    /// </summary>
    ValueTask<bool> RecordInterruptedAsync(string attemptId, CancellationToken cancellationToken);

    ValueTask RecordTransportFailureAsync(string attemptId, string reasonCode, string detail, CancellationToken cancellationToken);

    /// <summary>Retains the newest MaximumQuarantinedPayloads raw candidates under quarantine/; never activates them.</summary>
    ValueTask RecordQuarantineAsync(string attemptId, ReadOnlyMemory<byte> rawBytes, string? rawSha256,
        string reasonCode, string detail, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes raw, payload and manifest immutably, then atomically activates (kind activated / unchanged-payload).
    /// Storage failure: records StorageFailed when possible, keeps last-known-good, throws OrbitalStorageException.
    /// </summary>
    ValueTask<OrbitalPublication> PublishAndActivateAsync(string attemptId, OrbitalSnapshot candidate,
        ReadOnlyMemory<byte> rawBytes, CancellationToken cancellationToken);

    ValueTask<OrbitalCommandResult> ResumeProviderAsync(OrbitalResumeRequest request, CancellationToken cancellationToken);

    ValueTask<OrbitalRetentionResult> ApplyRetentionAsync(CancellationToken cancellationToken);

    /// <summary>Holds a product/assessment reference; false when the manifest is unknown or already retired.</summary>
    ValueTask<bool> AddReferenceAsync(string manifestSha256, string holder, CancellationToken cancellationToken);

    /// <summary>Releases a reference; false when the holder held none.</summary>
    ValueTask<bool> RemoveReferenceAsync(string manifestSha256, string holder, CancellationToken cancellationToken);
}
