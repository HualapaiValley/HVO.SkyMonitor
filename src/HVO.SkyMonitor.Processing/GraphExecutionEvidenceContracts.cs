using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Schema versions for the transport-neutral CameraAgent graph-execution evidence export contract. A receiver
/// negotiates one exact version; an unknown future version is rejected rather than partially interpreted.
/// </summary>
public static class GraphExecutionEvidenceSchemaVersions
{
    public const string V1 = "hvo-cameraagent-execution-evidence-v1";

    public const string Current = V1;

    /// <summary>Every version this build can produce and consume, most preferred first.</summary>
    public static ImmutableArray<string> Supported { get; } = [V1];

    public static bool IsSupported(string? schemaVersion)
        => schemaVersion is not null && Supported.Contains(schemaVersion, StringComparer.Ordinal);
}

/// <summary>
/// Explicit payload and cardinality limits. Both hosts enforce the same values so a producer never emits a unit a
/// conformant receiver must reject for size alone. Limits are published during version negotiation.
/// </summary>
public static class GraphExecutionEvidenceLimits
{
    /// <summary>
    /// Absolute canonical byte cap for any evidence envelope. It must be able to hold a maximum-size revision,
    /// because the CameraAgent store admits a 2 MiB canonical definition and a 2 MiB frozen plan for one
    /// revision; a smaller cap would make a legally persisted revision permanently unexportable.
    /// </summary>
    public const int MaximumEnvelopeBytes = 8 * 1024 * 1024;

    /// <summary>Canonical byte cap for a graph-execution envelope, which carries no document blob.</summary>
    public const int MaximumExecutionEnvelopeBytes = 2 * 1024 * 1024;

    /// <summary>Canonical byte cap for an artifact-availability envelope.</summary>
    public const int MaximumAvailabilityEnvelopeBytes = 1024 * 1024;

    /// <summary>
    /// Maximum canonical bytes of the content-addressed graph definition carried by a revision unit. Matches
    /// <see cref="ProcessingGraphJson.MaximumDocumentBytes"/> and the durable revision blob constraint.
    /// </summary>
    public const int MaximumDefinitionBytes = 2 * 1024 * 1024;

    /// <summary>Maximum canonical bytes of the frozen plan carried by a revision unit.</summary>
    public const int MaximumFrozenPlanBytes = 2 * 1024 * 1024;

    public const int MaximumFeedbackBytes = 256 * 1024;

    public const int MaximumResyncRequestBytes = 16 * 1024;

    public const int MaximumNegotiationBytes = 16 * 1024;

    public const int MaximumNodeCount = 128;

    public const int MaximumAttemptsPerNode = 32;

    /// <summary>Matches the durable <c>input_ordinal &lt; 512</c> constraint on a recorded node input.</summary>
    public const int MaximumInputsPerNode = 512;

    /// <summary>Matches the durable <c>output_ordinal &lt; 128</c> constraint on a recorded node output.</summary>
    public const int MaximumOutputsPerNode = 128;

    /// <summary>
    /// The aggregate per-execution budget, which is the binding limit: it is deliberately far below the
    /// arithmetic product of <see cref="MaximumNodeCount"/> and <see cref="MaximumInputsPerNode"/>, because the
    /// product describes what the durable ordinals permit per row rather than what one execution may export. The
    /// aggregate is checked after every per-node check, so an execution that satisfies every node limit can still
    /// be rejected here. The representative W6 graph declares an upper bound of 49 inputs, so this budget carries
    /// roughly forty times the measured headroom.
    /// </summary>
    public const int MaximumInputsPerExecution = 2048;

    /// <summary>
    /// The aggregate per-execution output budget. It binds before the product of <see cref="MaximumNodeCount"/>
    /// and <see cref="MaximumOutputsPerNode"/> for the same reason as
    /// <see cref="MaximumInputsPerExecution"/>; the representative W6 graph declares an upper bound of 17.
    /// </summary>
    public const int MaximumOutputsPerExecution = 1024;

    public const int MaximumAvailabilityObservations = 1024;

    public const int MaximumFactsPerFeedback = 256;

    public const int MaximumMissingRanges = 64;

    public const int MaximumResyncRanges = 64;

    /// <summary>Maximum evidence units one bounded resynchronization request may ask an origin to replay.</summary>
    public const int MaximumResyncUnits = 256;

    public const int MaximumSupportedSchemaVersions = 8;

    public const int MaximumIdentifierLength = 128;

    public const int MaximumReasonCodeLength = 128;

    public const int MaximumSoftwareVersionLength = 64;

    public const int MaximumMediaTypeLength = 128;
}

/// <summary>Stable machine-readable failure codes. A receiver never invents a code outside this list.</summary>
public static class GraphExecutionEvidenceReasonCodes
{
    public const string InvalidJson = "evidence.invalid-json";
    public const string UnsupportedSchema = "evidence.unsupported-schema";
    public const string PayloadTooLarge = "evidence.payload-too-large";
    public const string LimitExceeded = "evidence.limit-exceeded";
    public const string InvalidIdentity = "evidence.invalid-identity";
    public const string InvalidOrigin = "evidence.invalid-origin";
    public const string InvalidSequence = "evidence.invalid-sequence";
    public const string InvalidHash = "evidence.invalid-hash";
    public const string InvalidBody = "evidence.invalid-body";
    public const string InvalidTime = "evidence.invalid-time";
    public const string InvalidNode = "evidence.invalid-node";
    public const string InvalidAttempt = "evidence.invalid-attempt";
    public const string InvalidInput = "evidence.invalid-input";
    public const string InvalidOutput = "evidence.invalid-output";
    public const string InvalidAvailability = "evidence.invalid-availability";
    public const string InvalidCorrection = "evidence.invalid-correction";
    public const string InvalidAssignment = "evidence.invalid-assignment";
    public const string InvalidRedaction = "evidence.invalid-redaction";
    public const string InvalidFact = "evidence.invalid-fact";
    public const string InvalidRange = "evidence.invalid-range";
    public const string InvalidRetention = "evidence.invalid-retention";
    public const string InvalidNegotiation = "evidence.invalid-negotiation";

    /// <summary>The same origin sequence was already stored with a different canonical payload hash.</summary>
    public const string SequenceConflict = "evidence.sequence-conflict";

    /// <summary>The corrected predecessor is unknown to the receiver, so the successor cannot be ordered.</summary>
    public const string UnknownPredecessor = "evidence.unknown-predecessor";

    /// <summary>The referenced graph revision has not been received, so the execution cannot be resolved.</summary>
    public const string UnknownRevision = "evidence.unknown-revision";

    /// <summary>A contiguous prefix is missing; the origin must replay the reported ranges.</summary>
    public const string SequenceGap = "evidence.sequence-gap";
}

/// <summary>Which immutable or observational body an envelope carries. Exactly one body member is populated.</summary>
[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceBodyKind>))]
public enum ExecutionEvidenceBodyKind
{
    /// <summary>A content-addressed graph revision: canonical definition plus frozen shared/local plan.</summary>
    GraphRevision,

    /// <summary>One immutable graph execution with its node plans, attempts, inputs, and outputs.</summary>
    GraphExecution,

    /// <summary>Timestamped artifact availability observations; never an immutable production fact.</summary>
    ArtifactAvailability
}

/// <summary>How the exporting CameraAgent obtained the graph revision.</summary>
[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceRevisionOrigin>))]
public enum ExecutionEvidenceRevisionOrigin
{
    /// <summary>Compiled from local configuration with no central assignment.</summary>
    LocalOnly,

    /// <summary>Accepted from a central assignment proposal and pinned locally before use.</summary>
    CentrallyAssigned
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceExecutionClass>))]
public enum ExecutionEvidenceExecutionClass
{
    Live,
    Replay
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceExecutionStatus>))]
public enum ExecutionEvidenceExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Expired
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceNodeStatus>))]
public enum ExecutionEvidenceNodeStatus
{
    Pending,
    Running,
    Completed,
    Skipped,
    RetryableFailure,
    TerminalFailure
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceAttemptStatus>))]
public enum ExecutionEvidenceAttemptStatus
{
    Running,
    Completed,
    Skipped,
    RetryableFailure,
    TerminalFailure,
    Interrupted
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceInputKind>))]
public enum ExecutionEvidenceInputKind
{
    RawCapture,
    ProcessingOutput
}

/// <summary>Availability of an artifact's bytes as observed by the origin at a point in time.</summary>
[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceAvailabilityState>))]
public enum ExecutionEvidenceAvailabilityState
{
    Available,
    Missing,
    Quarantined
}

/// <summary>The four distinct receiver facts plus the terminal acknowledgement.</summary>
[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceFactKind>))]
public enum ExecutionEvidenceFactKind
{
    /// <summary>The receiver durably stored the exact bytes. It has not yet validated or accepted them.</summary>
    Received,

    /// <summary>The stored bytes parsed, matched their canonical payload hash, and satisfied every limit.</summary>
    Validated,

    /// <summary>The receiver committed the unit into its durable evidence record.</summary>
    Accepted,

    /// <summary>The receiver refused the unit. A reason code is always present.</summary>
    Rejected,

    /// <summary>Terminal acknowledgement: the origin may release its retention hold for this unit.</summary>
    Acknowledged
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ExecutionEvidenceNegotiationDisposition>))]
public enum ExecutionEvidenceNegotiationDisposition
{
    /// <summary>Producer and receiver share at least one schema version; <c>SelectedSchemaVersion</c> is set.</summary>
    Supported,

    /// <summary>No shared version. The producer must not send evidence; nothing is partially interpreted.</summary>
    Unsupported
}

/// <summary>
/// The exporting installation. <see cref="IdentitySha256"/> is the canonical identity over every other member and is
/// the stable key a receiver uses to order and reconcile sequences across restarts.
/// </summary>
public sealed record ExecutionEvidenceOriginV1(
    string SchemaVersion,
    Guid OriginInstallationId,
    Guid AgentInstanceId,
    Guid BootSessionId,
    string SoftwareVersion,
    string IdentitySha256,
    Guid? ObservatoryId = null,
    Guid? LogicalCameraInstallationId = null,
    Guid? InstallationPublicId = null)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-origin-v1";
}

/// <summary>Central assignment provenance for a revision accepted from a LogicHost proposal.</summary>
public sealed record ExecutionEvidenceAssignmentProvenanceV1(
    string SchemaVersion,
    Guid ProposalId,
    Guid CatalogRevisionId,
    Guid AssignmentId,
    Guid RegistrationId,
    Guid LogicalCameraInstallationId,
    Guid InstallationPublicId,
    string CapabilitySnapshotSha256,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset AcceptedAtUtc)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-assignment-v1";
}

/// <summary>
/// A content-addressed graph revision. <see cref="CanonicalDefinition"/> hashes to
/// <see cref="DefinitionIdentitySha256"/>; <see cref="FrozenPlan"/> carries the frozen shared and local plan
/// identities together with the per-node frozen plans an execution refers to.
/// </summary>
public sealed record GraphRevisionEvidenceV1(
    string SchemaVersion,
    string RevisionId,
    string Name,
    string Revision,
    ExecutionEvidenceRevisionOrigin Origin,
    string DefinitionIdentitySha256,
    string SharedPlanIdentitySha256,
    string LocalPlanIdentitySha256,
    JsonElement CanonicalDefinition,
    JsonElement FrozenPlan,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ValidatedUtc = null,
    DateTimeOffset? ActivatedUtc = null,
    DateTimeOffset? RetiredUtc = null,
    ExecutionEvidenceAssignmentProvenanceV1? Assignment = null)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-revision-v1";
}

/// <summary>
/// An artifact identity a receiver reconciles against its own object records. Image bytes are never carried; only
/// identity, checksum, length, and media type travel.
/// </summary>
/// <remarks>
/// A processing output is content-addressed: its <c>ArtifactId</c> is derived from
/// <c>OutputIdentitySha256</c> through <see cref="ProcessingIdentity.CreateArtifactId(string)"/>, and the contract
/// enforces that derivation. A raw capture carries no output identity; it reconciles through
/// <c>DescriptorSha256</c> and <c>PayloadSha256</c> instead.
/// </remarks>
public sealed record ExecutionEvidenceArtifactReferenceV1(
    string SchemaVersion,
    Guid ArtifactId,
    Guid CaptureId,
    string? OutputIdentitySha256 = null,
    FrameArtifactRole? Role = null,
    string? Variant = null,
    string? PayloadSha256 = null,
    long? PayloadLength = null,
    string? MediaType = null,
    string? DescriptorSha256 = null)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-artifact-v1";
}

public sealed record ExecutionEvidenceInputV1(
    string SchemaVersion,
    int Ordinal,
    int WindowPosition,
    ExecutionEvidenceInputKind Kind,
    ExecutionEvidenceArtifactReferenceV1 Artifact)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-input-v1";
}

public sealed record ExecutionEvidenceOutputV1(
    string SchemaVersion,
    int Ordinal,
    ExecutionEvidenceArtifactReferenceV1 Artifact)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-output-v1";
}

/// <summary>One node attempt. Retries appear as additional attempts; an attempt is never rewritten.</summary>
public sealed record ExecutionEvidenceAttemptV1(
    string SchemaVersion,
    int AttemptNumber,
    ExecutionEvidenceAttemptStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc = null,
    ProcessingOutcomeStatus? Outcome = null,
    string? ReasonCode = null,
    long? DurationTicks = null,
    string? LeaseOwner = null)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-attempt-v1";
}

/// <summary>
/// One frozen node plan and everything the origin durably recorded for it. <see cref="Required"/> distinguishes a
/// required node from an optional node whose skip or failure does not fail the execution.
/// </summary>
public sealed record ExecutionEvidenceNodeV1(
    string SchemaVersion,
    string NodeId,
    bool Required,
    string PlanSha256,
    ExecutionEvidenceNodeStatus Status,
    ImmutableArray<ExecutionEvidenceInputV1> Inputs,
    ImmutableArray<ExecutionEvidenceAttemptV1> Attempts,
    ImmutableArray<ExecutionEvidenceOutputV1> Outputs,
    string? ReasonCode = null,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-node-v1";
}

/// <summary>
/// One immutable graph execution. Every member is a production fact recorded once; artifact availability is
/// reported separately because it changes over time.
/// </summary>
public sealed record GraphExecutionEvidenceV1(
    string SchemaVersion,
    Guid ExecutionId,
    ExecutionEvidenceExecutionClass ExecutionClass,
    ExecutionEvidenceExecutionStatus Status,
    Guid CaptureId,
    Guid PrimaryArtifactId,
    string GraphRevisionId,
    string DefinitionIdentitySha256,
    string SharedPlanIdentitySha256,
    string LocalPlanIdentitySha256,
    string TriggerKind,
    ImmutableArray<ExecutionEvidenceNodeV1> Nodes,
    DateTimeOffset AcceptedUtc,
    DateTimeOffset AvailableUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset MaximumAgeUtc,
    bool CancellationRequested,
    int AttemptCount,
    string? TriggerReference = null,
    int Priority = 0,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null,
    string? FailureReasonCode = null)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-execution-v1";
}

/// <summary>A timestamped observation of one artifact's local availability.</summary>
public sealed record ArtifactAvailabilityObservationV1(
    string SchemaVersion,
    ExecutionEvidenceArtifactReferenceV1 Artifact,
    ExecutionEvidenceAvailabilityState State,
    DateTimeOffset ObservedAtUtc,
    string? ReasonCode = null)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-observation-v1";
}

/// <summary>
/// A sequenced batch of availability observations. A later report supersedes an earlier observation of the same
/// artifact; it never corrects an immutable production fact.
/// </summary>
public sealed record ArtifactAvailabilityReportV1(
    string SchemaVersion,
    Guid ExecutionId,
    ImmutableArray<ArtifactAvailabilityObservationV1> Observations)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-availability-v1";
}

/// <summary>
/// Append-only successor metadata. The corrected unit is retained; a correction never replaces terminal evidence
/// in place and always carries a strictly greater origin sequence.
/// </summary>
public sealed record ExecutionEvidenceCorrectionV1(
    string SchemaVersion,
    Guid CorrectsEvidenceId,
    long CorrectsOriginSequence,
    string ReasonCode)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-correction-v1";
}

/// <summary>
/// Which operator-identifying members the origin replaced with a stable non-reversible token before hashing. The
/// policy travels with the payload so a receiver can tell a redacted value from a literal one.
/// </summary>
public sealed record ExecutionEvidenceRedactionPolicyV1(
    string SchemaVersion,
    bool RedactTriggerReferences,
    bool RedactLeaseOwners)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-redaction-v1";

    /// <summary>No redaction applied.</summary>
    public static ExecutionEvidenceRedactionPolicyV1 None { get; } =
        new(CurrentSchemaVersion, RedactTriggerReferences: false, RedactLeaseOwners: false);

    /// <summary>Replace every operator-identifying member with its redaction token.</summary>
    public static ExecutionEvidenceRedactionPolicyV1 OperatorIdentity { get; } =
        new(CurrentSchemaVersion, RedactTriggerReferences: true, RedactLeaseOwners: true);
}

/// <summary>
/// One sequenced, content-addressed evidence unit. <see cref="OriginSequence"/> is stable and strictly increasing
/// per origin identity; <see cref="PayloadSha256"/> is the canonical hash over this envelope with the hash member
/// itself excluded, which makes duplicate detection and conflict detection exact.
/// </summary>
public sealed record ExecutionEvidenceEnvelopeV1(
    string SchemaVersion,
    Guid EvidenceId,
    ExecutionEvidenceOriginV1 Origin,
    long OriginSequence,
    DateTimeOffset ProducedAtUtc,
    ExecutionEvidenceBodyKind Kind,
    string PayloadSha256,
    ExecutionEvidenceRedactionPolicyV1 Redaction,
    GraphRevisionEvidenceV1? GraphRevision = null,
    GraphExecutionEvidenceV1? Execution = null,
    ArtifactAvailabilityReportV1? Availability = null,
    ExecutionEvidenceCorrectionV1? Correction = null)
{
    public const string CurrentSchemaVersion = GraphExecutionEvidenceSchemaVersions.V1;
}

/// <summary>An inclusive origin-sequence range. Used for gap reporting and bounded resynchronization.</summary>
public sealed record ExecutionEvidenceSequenceRangeV1(
    string SchemaVersion,
    long FromSequence,
    long ToSequence)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-range-v1";
}

/// <summary>
/// One receiver fact about one evidence unit. <see cref="Duplicate"/> marks an idempotent repeat of a fact the
/// receiver already recorded for the same sequence and identical payload hash; a differing payload hash for the
/// same sequence is a <see cref="ExecutionEvidenceFactKind.Rejected"/> fact carrying
/// <see cref="GraphExecutionEvidenceReasonCodes.SequenceConflict"/> and the stored hash.
/// </summary>
public sealed record ExecutionEvidenceFactV1(
    string SchemaVersion,
    ExecutionEvidenceFactKind Kind,
    Guid EvidenceId,
    long OriginSequence,
    string PayloadSha256,
    DateTimeOffset OccurredAtUtc,
    bool Duplicate = false,
    string? ReasonCode = null,
    string? FieldPath = null,
    string? StoredPayloadSha256 = null)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-fact-v1";
}

/// <summary>
/// How long the receiver keeps acknowledgements available for re-read, and the highest sequence whose terminal
/// acknowledgement the origin may rely on when releasing its own retention hold.
/// </summary>
public sealed record ExecutionEvidenceRetentionV1(
    string SchemaVersion,
    long AcknowledgedThroughSequence,
    DateTimeOffset AcknowledgementsRetainedUntilUtc,
    int MaximumRetainedAcknowledgements)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-retention-v1";
}

/// <summary>
/// The receiver's bounded response for one origin: per-unit facts, the contiguous accepted prefix, the bounded
/// missing ranges that drive resynchronization, and acknowledgement retention.
/// </summary>
public sealed record ExecutionEvidenceFeedbackV1(
    string SchemaVersion,
    string OriginIdentitySha256,
    DateTimeOffset ServerTimeUtc,
    long ContiguousThroughSequence,
    ImmutableArray<ExecutionEvidenceSequenceRangeV1> MissingRanges,
    ImmutableArray<ExecutionEvidenceFactV1> Facts,
    ExecutionEvidenceRetentionV1 Retention,
    bool MissingRangesTruncated = false)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-feedback-v1";
}

/// <summary>
/// A bounded request for an origin to re-send specific sequences. Both the range count and the total unit count
/// are bounded, so a large gap resynchronizes over several requests instead of one unbounded replay.
/// </summary>
public sealed record ExecutionEvidenceResyncRequestV1(
    string SchemaVersion,
    string OriginIdentitySha256,
    ImmutableArray<ExecutionEvidenceSequenceRangeV1> Ranges,
    int MaximumUnits,
    string ReasonCode)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-resync-v1";
}

/// <summary>The negotiated limits a producer must respect. Values mirror <see cref="GraphExecutionEvidenceLimits"/>.</summary>
public sealed record ExecutionEvidenceLimitsV1(
    string SchemaVersion,
    int MaximumEnvelopeBytes,
    int MaximumExecutionEnvelopeBytes,
    int MaximumAvailabilityEnvelopeBytes,
    int MaximumDefinitionBytes,
    int MaximumFrozenPlanBytes,
    int MaximumNodeCount,
    int MaximumAttemptsPerNode,
    int MaximumInputsPerNode,
    int MaximumOutputsPerNode,
    int MaximumInputsPerExecution,
    int MaximumOutputsPerExecution,
    int MaximumAvailabilityObservations,
    int MaximumFactsPerFeedback,
    int MaximumMissingRanges,
    int MaximumResyncRanges,
    int MaximumResyncUnits)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-limits-v1";

    public static ExecutionEvidenceLimitsV1 Current { get; } = new(
        CurrentSchemaVersion,
        GraphExecutionEvidenceLimits.MaximumEnvelopeBytes,
        GraphExecutionEvidenceLimits.MaximumExecutionEnvelopeBytes,
        GraphExecutionEvidenceLimits.MaximumAvailabilityEnvelopeBytes,
        GraphExecutionEvidenceLimits.MaximumDefinitionBytes,
        GraphExecutionEvidenceLimits.MaximumFrozenPlanBytes,
        GraphExecutionEvidenceLimits.MaximumNodeCount,
        GraphExecutionEvidenceLimits.MaximumAttemptsPerNode,
        GraphExecutionEvidenceLimits.MaximumInputsPerNode,
        GraphExecutionEvidenceLimits.MaximumOutputsPerNode,
        GraphExecutionEvidenceLimits.MaximumInputsPerExecution,
        GraphExecutionEvidenceLimits.MaximumOutputsPerExecution,
        GraphExecutionEvidenceLimits.MaximumAvailabilityObservations,
        GraphExecutionEvidenceLimits.MaximumFactsPerFeedback,
        GraphExecutionEvidenceLimits.MaximumMissingRanges,
        GraphExecutionEvidenceLimits.MaximumResyncRanges,
        GraphExecutionEvidenceLimits.MaximumResyncUnits);
}

public sealed record ExecutionEvidenceNegotiationRequestV1(
    string SchemaVersion,
    ExecutionEvidenceOriginV1 Origin,
    ImmutableArray<string> SupportedSchemaVersions)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-negotiation-request-v1";
}

public sealed record ExecutionEvidenceNegotiationResponseV1(
    string SchemaVersion,
    ExecutionEvidenceNegotiationDisposition Disposition,
    ImmutableArray<string> SupportedSchemaVersions,
    ExecutionEvidenceLimitsV1 Limits,
    DateTimeOffset ServerTimeUtc,
    string? SelectedSchemaVersion = null,
    string? ReasonCode = null)
{
    public const string CurrentSchemaVersion = "hvo-cameraagent-execution-evidence-negotiation-response-v1";
}

/// <summary>Stable validation failure and contract-relative field path.</summary>
public readonly record struct ExecutionEvidenceValidationResult(
    bool IsValid,
    string? ReasonCode,
    string? FieldPath)
{
    public static ExecutionEvidenceValidationResult Success => new(true, null, null);

    public static ExecutionEvidenceValidationResult Failure(string reasonCode, string fieldPath)
        => new(false, reasonCode, fieldPath);
}

/// <summary>Strict parse result: either a validated contract or a bounded non-throwing failure.</summary>
public sealed record ExecutionEvidenceParseResult<T>(
    T? Value,
    ExecutionEvidenceValidationResult Validation)
    where T : class;
