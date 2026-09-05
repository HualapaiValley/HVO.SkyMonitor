using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

/// <summary>Bounds and fixed labels of the audited local manual coordinate contract.</summary>
public static class ManualDeploymentLocationContract
{
    /// <summary>The provenance text recorded on every deployment version an operator entered locally.</summary>
    public const string SourceLabel = "local-operator-manual";

    /// <summary>The maximum accepted actor identifier length.</summary>
    public const int MaximumActorLength = 128;

    /// <summary>The maximum accepted idempotency-key length.</summary>
    public const int MaximumIdempotencyKeyLength = 128;

    /// <summary>The maximum accepted operator reason length.</summary>
    public const int MaximumReasonLength = 512;

    /// <summary>
    /// The lowest elevation a manual entry accepts. The deployment-location contract itself only
    /// requires a finite value; this narrower bound rejects an obvious keying error before it becomes
    /// an immutable version, and never restricts a value that already exists in protected history.
    /// </summary>
    public const double MinimumElevationMeters = -500d;

    /// <summary>The highest elevation a manual entry accepts, for the same reason as the lower bound.</summary>
    public const double MaximumElevationMeters = 9000d;

    /// <summary>The most recent audit entries a projection returns, newest first.</summary>
    public const int MaximumProjectedEntries = 50;

    /// <summary>
    /// The most recent audit entries the protected record retains. Older entries are dropped so an
    /// authenticated caller cannot grow the record without bound; the idempotency replay window is
    /// therefore the retained window.
    /// </summary>
    public const int MaximumRetainedEntries = 500;

    /// <summary>The reason code returned when an idempotency key is replayed with a different payload.</summary>
    public const string IdempotencyKeyConflictReasonCode = "manual.idempotencyKeyConflict";

    /// <summary>The reason code returned when the operator's expected version is not the current one.</summary>
    public const string ExpectedVersionConflictReasonCode = "manual.expectedVersionConflict";

    /// <summary>The reason code returned when the operator's expected manual sequence is not the current one.</summary>
    public const string ExpectedManualSequenceConflictReasonCode = "manual.expectedManualSequenceConflict";

    /// <summary>The reason code returned when a replayed key belongs to a record a configuration change superseded.</summary>
    public const string SupersededEntryReasonCode = "manual.supersededEntry";

    /// <summary>The reason code returned when a manual command carries an unusable actor or command identity.</summary>
    public const string InvalidCommandReasonCode = "manual.invalidCommand";
}

/// <summary>One operator-entered coordinate change requested through the audited local contract.</summary>
public sealed record ManualDeploymentLocationRequest(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationMeters,
    string TimeZoneId,
    long ExpectedVersion,
    long ExpectedManualSequence,
    string IdempotencyKey,
    string Actor,
    string? Reason);

/// <summary>The bounded disposition of one manual coordinate command.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ManualDeploymentLocationStatus>))]
public enum ManualDeploymentLocationStatus
{
    /// <summary>A new manual deployment version was recorded and activates at the next start.</summary>
    Applied,

    /// <summary>The same idempotency key and payload were already recorded; no new version was created.</summary>
    Replayed,

    /// <summary>The requested coordinates already govern this deployment, so no version was created.</summary>
    Unchanged,

    /// <summary>The expected version did not match the current one, or a key was replayed with a different payload.</summary>
    Conflict,

    /// <summary>The command failed validation and nothing durable changed.</summary>
    Invalid
}

/// <summary>One immutable audit record of an accepted manual coordinate change.</summary>
public sealed record ManualDeploymentLocationAuditEntry(
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] DateTimeOffset RecordedAtUtc,
    [property: JsonRequired] string Actor,
    [property: JsonRequired] string? Reason,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] long ExpectedVersion,
    [property: JsonRequired] double LatitudeDegrees,
    [property: JsonRequired] double LongitudeDegrees,
    [property: JsonRequired] double ElevationMeters,
    [property: JsonRequired] string TimeZoneId);

/// <summary>The manual coordinates that currently govern this deployment's next startup reconciliation.</summary>
public sealed record ManualDeploymentLocationOverride(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationMeters,
    string TimeZoneId,
    bool PendingRestart,
    DateTimeOffset RecordedAtUtc,
    string Actor,
    string? Reason);

/// <summary>The operator-facing state of the audited local manual coordinate contract.</summary>
/// <param name="ActiveVersion">
/// The version every capture this process records is stamped with. Operator statements about which
/// captures keep which version must use this, never <paramref name="KnownVersion"/>.
/// </param>
/// <param name="KnownVersion">
/// The highest version the protected history knows, including a candidate or staged version. This is
/// the concurrency token a command echoes as its expected version; it is not the capture-stamped one.
/// </param>
/// <param name="PendingVersion">
/// The version a candidate or staged snapshot already occupies, or null when none is pending.
/// </param>
/// <param name="ManualSequence">
/// The sequence of the newest recorded manual entry, or zero when none. A command echoes it so a
/// second operator cannot silently replace a pending manual entry the history has not yet versioned.
/// </param>
public sealed record ManualDeploymentLocationState(
    bool Supported,
    string LocationId,
    long ActiveVersion,
    long KnownVersion,
    long NextVersion,
    long? PendingVersion,
    long ManualSequence,
    bool CentralAcknowledgementRequired,
    bool StagedAcknowledgementPending,
    bool CandidateAwaitingAcknowledgement,
    ManualDeploymentLocationOverride? Override,
    DateTimeOffset? OverrideSupersededAtUtc,
    IReadOnlyList<ManualDeploymentLocationAuditEntry> History)
{
    /// <summary>The state a store without a manual mutation contract, or without initialized history, reports.</summary>
    public static ManualDeploymentLocationState Unsupported { get; } = new(
        Supported: false,
        LocationId: string.Empty,
        ActiveVersion: 0,
        KnownVersion: 0,
        NextVersion: 0,
        PendingVersion: null,
        ManualSequence: 0,
        CentralAcknowledgementRequired: false,
        StagedAcknowledgementPending: false,
        CandidateAwaitingAcknowledgement: false,
        Override: null,
        OverrideSupersededAtUtc: null,
        History: []);
}

/// <summary>The disposition and resulting state of one manual coordinate command.</summary>
public sealed record ManualDeploymentLocationResult(
    ManualDeploymentLocationStatus Status,
    string? ReasonCode,
    string? FieldPath,
    ManualDeploymentLocationState State);
