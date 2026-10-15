using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record ExecutionEvidenceBindingRequest(Guid ObservatoryId, Guid RegistrationId,
    Guid LogicalCameraInstallationId, Guid OriginInstallationId, Guid AgentInstanceId);

internal sealed record ExecutionEvidenceBindingView(Guid Id, Guid ObservatoryId, Guid RegistrationId,
    Guid LogicalCameraInstallationId, Guid InstallationPublicId, Guid OriginInstallationId, Guid AgentInstanceId,
    DateTimeOffset CreatedAtUtc);

internal sealed record ExecutionEvidenceStatusView(Guid BindingId, long StoredBytes, int UnitCount,
    int OriginCount, int ConflictCount, int ObservationCount, bool StoragePressure,
    long MaximumStoredBytes, int MaximumUnits, int MaximumOrigins, int MaximumConflicts, int MaximumObservations,
    int OriginsWithGaps, DateTimeOffset? OldestGapDetectedAtUtc, int DueArtifactChecks, bool DueArtifactChecksTruncated, DateTimeOffset? OldestDueArtifactCheckAtUtc);

internal sealed record ExecutionEvidenceOriginView(long Id, string IdentitySha256, Guid BootSessionId,
    string SoftwareVersion, DateTimeOffset FirstReceivedAtUtc, long HighestSeenSequence, long ContiguousThroughSequence);

internal sealed record ExecutionEvidenceUnitView(long Id, long OriginId, long OriginSequence, Guid EvidenceId,
    string PayloadSha256, string Kind, string? RevisionId, Guid? ExecutionId, Guid? CaptureId,
    DateTimeOffset ProducedAtUtc, DateTimeOffset ReceivedAtUtc, bool Accepted, string? RejectionReasonCode);

internal sealed record ExecutionEvidenceObservationView(long Id, long ArtifactReferenceId, string State,
    string ReasonCode, DateTimeOffset ObservedAtUtc, string? ObjectGeneration);
