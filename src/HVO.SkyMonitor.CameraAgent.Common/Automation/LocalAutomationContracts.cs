using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>
/// Bounds, fixed labels, and reason codes of the versioned durable local automation contract. The
/// contract only ever names a registered task kind and a registered trigger kind; it can express no
/// command, script, or executable path.
/// </summary>
public static class LocalAutomationContract
{
    /// <summary>The durable schema label recorded in every revision hash.</summary>
    public const string SchemaLabel = "hvo-cameraagent-local-automation-v1";

    /// <summary>The current durable schema version of the separate automation store file.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The maximum accepted definition identifier length.</summary>
    public const int MaximumDefinitionIdLength = 64;

    /// <summary>The maximum accepted operator-facing name length.</summary>
    public const int MaximumNameLength = 96;

    /// <summary>The maximum accepted task target length.</summary>
    public const int MaximumTargetLength = 128;

    /// <summary>The maximum accepted actor identifier length.</summary>
    public const int MaximumActorLength = 256;

    /// <summary>The maximum accepted idempotency-key length.</summary>
    public const int MaximumIdempotencyKeyLength = 128;

    /// <summary>The maximum accepted operator reason length.</summary>
    public const int MaximumReasonLength = 512;

    /// <summary>
    /// The most definitions this local contract accepts. The runner evaluates every enabled
    /// definition on each tick, so the bound keeps that sweep constant-time rather than operator
    /// controlled.
    /// </summary>
    public const int MaximumDefinitions = 32;

    /// <summary>The shortest accepted periodic interval, in seconds.</summary>
    public const int MinimumPeriodicIntervalSeconds = 60;

    /// <summary>The longest accepted periodic interval, in seconds.</summary>
    public const int MaximumPeriodicIntervalSeconds = 86_400;

    /// <summary>The smallest accepted capture-relative interval, in durable captures.</summary>
    public const int MinimumCaptureInterval = 1;

    /// <summary>The largest accepted capture-relative interval, in durable captures.</summary>
    public const int MaximumCaptureInterval = 10_000;

    /// <summary>The most recent revisions one definition retains. Older revisions are dropped.</summary>
    public const int MaximumRetainedRevisions = 50;

    /// <summary>The most recent runs one definition retains. Older runs are dropped.</summary>
    public const int MaximumRetainedRuns = 200;

    /// <summary>The most recent runs a projection returns across all definitions, newest first.</summary>
    public const int MaximumProjectedRuns = 50;

    /// <summary>
    /// The revisions of removed definitions the store keeps in total. Per-definition retention only runs
    /// from that definition's own write paths, and removal frees its slot, so the orphan tail needs its
    /// own global bound.
    /// </summary>
    public const int MaximumRetainedOrphanRevisions = 200;

    /// <summary>The runs of removed definitions the store keeps in total, for the same reason.</summary>
    public const int MaximumRetainedOrphanRuns = 200;

    /// <summary>The most recent revisions of one definition a projection returns, newest first.</summary>
    public const int MaximumProjectedRevisions = 10;

    /// <summary>The most next-run calendar entries a projection returns, soonest first.</summary>
    public const int MaximumProjectedCalendarEntries = 25;

    /// <summary>
    /// How long an accepted command identifier is remembered so a retry replays rather than
    /// appending a second revision. Older command records are dropped, so the replay window is the
    /// retention window.
    /// </summary>
    public static TimeSpan IdempotencyReplayWindow => TimeSpan.FromDays(7);

    /// <summary>The reason code returned when the operator's expected version is not the current one.</summary>
    public const string ExpectedVersionConflictReasonCode = "automation.expectedVersionConflict";

    /// <summary>The reason code returned when an idempotency key is replayed with a different payload.</summary>
    public const string IdempotencyKeyConflictReasonCode = "automation.idempotencyKeyConflict";

    /// <summary>The reason code returned when the named definition does not exist.</summary>
    public const string UnknownDefinitionReasonCode = "automation.unknownDefinition";

    /// <summary>The reason code returned when accepting the definition would exceed the definition bound.</summary>
    public const string DefinitionLimitReasonCode = "automation.definitionLimit";

    /// <summary>The reason code returned when the task kind and trigger kind are not a registered pair.</summary>
    public const string UnregisteredCombinationReasonCode = "automation.unregisteredCombination";

    /// <summary>The reason code returned when the task target is not a registered target of its task kind.</summary>
    public const string UnregisteredTargetReasonCode = "automation.unregisteredTarget";

    /// <summary>The reason code returned when a command carries an unusable identity or interval.</summary>
    public const string InvalidCommandReasonCode = "automation.invalidCommand";
}

/// <summary>
/// The registered task kinds an automation definition may name. Every value maps to an existing
/// operator-triggerable local operation; there is deliberately no value that names a command,
/// script, or arbitrary endpoint.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocalAutomationTaskKind>))]
public enum LocalAutomationTaskKind
{
    /// <summary>Runs the existing on-demand acquisition of one registered environmental source.</summary>
    EnvironmentalOnDemandAcquisition
}

/// <summary>
/// The registered trigger kinds an automation definition may name. Both are evaluated by the
/// automation runner's own timer over durable state; neither participates in exposure admission,
/// acquisition timing, or the live processing slot.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocalAutomationTriggerKind>))]
public enum LocalAutomationTriggerKind
{
    /// <summary>Due once per fixed wall-clock interval measured from the definition's epoch.</summary>
    Periodic,

    /// <summary>Due once the durable capture sequence has advanced by the configured interval.</summary>
    CaptureRelative
}

/// <summary>The bounded disposition of one automation definition command.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocalAutomationCommandStatus>))]
public enum LocalAutomationCommandStatus
{
    /// <summary>A new immutable revision was recorded.</summary>
    Applied,

    /// <summary>The same idempotency key and payload were already recorded; nothing new was written.</summary>
    Replayed,

    /// <summary>The requested definition already matches the stored one, so no revision was created.</summary>
    Unchanged,

    /// <summary>The expected version did not match, or a key was replayed with a different payload.</summary>
    Conflict,

    /// <summary>The named definition does not exist.</summary>
    NotFound,

    /// <summary>The command failed validation and nothing durable changed.</summary>
    Invalid
}

/// <summary>The bounded terminal disposition of one recorded automation run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocalAutomationRunOutcome>))]
public enum LocalAutomationRunOutcome
{
    /// <summary>The run is claimed and executing in this process.</summary>
    Running,

    /// <summary>The registered task completed and produced its intended effect.</summary>
    Succeeded,

    /// <summary>The registered task declined the occurrence without failing, such as a coalesced source.</summary>
    Skipped,

    /// <summary>The registered task failed. The definition stays enabled and the next occurrence is retried.</summary>
    Failed,

    /// <summary>One or more occurrences elapsed while the CameraAgent was not running.</summary>
    Missed,

    /// <summary>The process stopped while the run was claimed; restart recovery settled it.</summary>
    Interrupted
}

/// <summary>
/// One versioned automation definition. The revision hash covers exactly these fields, so an
/// operator can compare a recorded run against the definition text that produced it.
/// </summary>
public sealed record LocalAutomationDefinition(
    [property: JsonRequired] string DefinitionId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] LocalAutomationTaskKind TaskKind,
    [property: JsonRequired] string TaskTarget,
    [property: JsonRequired] LocalAutomationTriggerKind TriggerKind,
    [property: JsonRequired] int TriggerInterval,
    [property: JsonRequired] DateTimeOffset TriggerEpochUtc);

/// <summary>One immutable recorded revision of a definition, including its removal.</summary>
public sealed record LocalAutomationRevision(
    long Version,
    string RevisionSha256,
    LocalAutomationDefinition Definition,
    bool Removed,
    DateTimeOffset RecordedAtUtc,
    string Actor,
    string? Reason);

/// <summary>One durable run-history record.</summary>
public sealed record LocalAutomationRun(
    long Sequence,
    string DefinitionId,
    string RunKey,
    string RevisionSha256,
    LocalAutomationTriggerKind TriggerKind,
    DateTimeOffset ScheduledForUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    LocalAutomationRunOutcome Outcome,
    string Detail,
    long? ObservedCaptureSequence);

/// <summary>The operator-facing state of one definition, its concurrency token, and its history.</summary>
public sealed record LocalAutomationDefinitionState(
    LocalAutomationDefinition Definition,
    long Version,
    string RevisionSha256,
    DateTimeOffset UpdatedAtUtc,
    string Actor,
    string? Reason,
    DateTimeOffset? NextRunUtc,
    long? NextRunCaptureSequence,
    LocalAutomationRun? LastRun,
    IReadOnlyList<LocalAutomationRevision> History);

/// <summary>One entry of the next-run calendar over the enabled wall-clock definitions.</summary>
public sealed record LocalAutomationCalendarEntry(
    string DefinitionId,
    string Name,
    LocalAutomationTaskKind TaskKind,
    string TaskTarget,
    DateTimeOffset DueUtc);

/// <summary>One registered task kind, the triggers it accepts, and the targets it can name.</summary>
public sealed record LocalAutomationTaskDescriptor(
    LocalAutomationTaskKind TaskKind,
    string Description,
    IReadOnlyList<LocalAutomationTriggerKind> CompatibleTriggers,
    IReadOnlyList<string> Targets,
    bool Available,
    string? UnavailableReason);

/// <summary>The complete operator projection of the local automation contract.</summary>
public sealed record LocalAutomationOperatorState(
    long StoreVersion,
    DateTimeOffset ReadAtUtc,
    long? ObservedCaptureSequence,
    IReadOnlyList<LocalAutomationTaskDescriptor> Registry,
    IReadOnlyList<LocalAutomationDefinitionState> Definitions,
    IReadOnlyList<LocalAutomationCalendarEntry> Calendar,
    IReadOnlyList<LocalAutomationRun> Runs)
{
    /// <summary>The state a store reports before any definition or run exists.</summary>
    public static LocalAutomationOperatorState Empty { get; } = new(
        StoreVersion: 0,
        ReadAtUtc: DateTimeOffset.UnixEpoch,
        ObservedCaptureSequence: null,
        Registry: [],
        Definitions: [],
        Calendar: [],
        Runs: []);
}

/// <summary>One operator command that creates or replaces a definition.</summary>
public sealed record LocalAutomationSaveRequest(
    string DefinitionId,
    string Name,
    bool Enabled,
    LocalAutomationTaskKind TaskKind,
    string TaskTarget,
    LocalAutomationTriggerKind TriggerKind,
    int TriggerInterval,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string? Reason);

/// <summary>One operator command that removes a definition and retains its recorded history.</summary>
public sealed record LocalAutomationRemoveRequest(
    string DefinitionId,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string? Reason);

/// <summary>The disposition and resulting state of one automation definition command.</summary>
public sealed record LocalAutomationCommandResult(
    LocalAutomationCommandStatus Status,
    string? ReasonCode,
    string? FieldPath,
    LocalAutomationOperatorState State);
