using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.ProcessingRunner.Contracts;

public sealed record ProcessingRunnerRegistrationRequest(
    string RunnerId,
    string DisplayName,
    ProcessingRunnerCapabilities Capabilities,
    int ProcessId,
    DateTimeOffset ProcessStartedUtc);

public sealed record ProcessingRunnerRegistrationResponse(
    string RunnerId,
    Guid RegistrationId,
    ProcessingRunnerRegistrationStatus Status,
    TimeSpan HeartbeatInterval,
    TimeSpan LeaseDuration,
    TimeSpan RenewalInterval,
    TimeSpan ClaimBackoff,
    IReadOnlyList<string> EligibleRecipes,
    DateTimeOffset ServerTimeUtc);

public sealed record ProcessingRunnerHeartbeatRequest(
    ProcessingRunnerWarmState WarmState,
    int AvailableSlots,
    IReadOnlyList<Guid> ActiveJobIds,
    ProcessingRunnerWarmupStages? Warmup = null);

public sealed record ProcessingRunnerHeartbeatResponse(
    ProcessingRunnerRegistrationStatus Status,
    IReadOnlyList<Guid> CancelRequestedJobIds,
    IReadOnlyList<Guid> StaleJobIds,
    DateTimeOffset ServerTimeUtc);

public sealed record ProcessingRunnerClaimRequest(
    ProcessingRunnerJobClass JobClass,
    int SlotOrdinal);

/// <summary>Immutable artifact reference plus the exact metadata the recipe kernel needs; payload bytes travel separately.</summary>
public sealed record ProcessingRunnerArtifactMetadata(
    Guid ArtifactId,
    Guid DevicePublicId,
    string ContentPath,
    long PayloadLength,
    string PayloadSha256,
    FrameArtifactRole Role,
    string Variant,
    string RecipeIdentitySha256,
    string MediaType,
    FrameLayoutDescriptor? Layout,
    DateTimeOffset CreatedUtc,
    TimeSpan Integration,
    ProcessingCompatibilityIdentity Compatibility,
    long? CaptureSequence,
    IReadOnlyList<Guid>? SourceArtifactIds,
    DateTimeOffset? ObservationStartedUtc,
    DateTimeOffset? ObservationEndedUtc,
    ProcessingCaptureConditions? Conditions,
    ProcessingProductKind ProductKind,
    string? SchemaVersion,
    string? ContentIdentitySha256,
    Guid? CaptureId,
    string? DescriptorIdentitySha256);

public sealed record ProcessingRunnerAuxiliaryInputMetadata(
    string Name,
    ProcessingAuxiliaryInputKind Kind,
    ProcessingInputSelector? Selector,
    string? SchemaVersion,
    string? IdentitySha256,
    string? CanonicalJson,
    Guid? ArtifactId,
    string? ChecksumSha256);

public sealed record ProcessingRunnerJobCorrelation(
    Guid? GraphExecutionId,
    string? GraphNodeId,
    string? TraceParent,
    string? TraceState);

/// <summary>
/// A claimed job: the lease credential, the complete execution request with payload-less artifact metadata, and
/// the job-scoped content paths the runner fetches under that lease.
/// </summary>
public sealed record ProcessingRunnerClaim(
    int ProtocolVersion,
    Guid JobId,
    ProcessingRunnerJobClass JobClass,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresUtc,
    TimeSpan RenewalInterval,
    int AttemptCount,
    int MaxAttempts,
    string RecipeName,
    JsonElement Options,
    ProcessingInputSelector Input,
    IReadOnlyList<ProcessingRunnerArtifactMetadata> Inputs,
    string OutputVariant,
    ProcessingAnnotationInput? Annotation,
    IReadOnlyList<ProcessingRunnerAuxiliaryInputMetadata>? AuxiliaryInputs,
    Guid? InputArtifactId,
    string RequestedRecipeIdentitySha256,
    string? ExpectedRecipeIdentitySha256,
    ProcessingRunnerJobCorrelation Correlation);

public sealed record ProcessingRunnerLeaseRenewalRequest(Guid LeaseToken);

public sealed record ProcessingRunnerLeaseRenewalResponse(
    DateTimeOffset LeaseExpiresUtc,
    DateTimeOffset ServerTimeUtc);

public sealed record ProcessingRunnerProductMetadata(
    FrameArtifactRole Role,
    string Variant,
    string OutputIdentitySha256,
    string MediaType,
    FrameLayoutDescriptor? Layout,
    int? PayloadOrdinal,
    long PayloadLength,
    string PayloadSha256,
    string ChecksumSha256,
    ProcessingRecipeIdentity Recipe,
    IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    IReadOnlyList<Guid> SourceArtifactIds,
    TimeSpan TotalIntegration,
    ProcessingCompatibilityIdentity Compatibility,
    ProcessingProductKind Kind,
    string? SchemaVersion,
    string? ContentIdentitySha256);

/// <summary>The outcome part of a multipart completion; product payloads are the <c>payload-{ordinal}</c> parts.</summary>
public sealed record ProcessingRunnerCompletionRequest(
    Guid LeaseToken,
    ProcessingOutcomeStatus Status,
    string? ReasonCode,
    string? Field,
    IReadOnlyList<ProcessingRunnerProductMetadata> Products,
    long InputBytes,
    TimeSpan ExecutionDuration);

public sealed record ProcessingRunnerCompletionResponse(
    ProcessingOutcomeStatus Status,
    IReadOnlyList<Guid> ArtifactIds,
    string? ReasonCode);

public sealed record ProcessingRunnerFailureRequest(
    Guid LeaseToken,
    string ReasonCode,
    bool Retryable,
    string? Message = null,
    Guid? UnavailableArtifactId = null);

public sealed record ProcessingRunnerProblem(
    string ReasonCode,
    string Message,
    int? RetryAfterMilliseconds = null);
