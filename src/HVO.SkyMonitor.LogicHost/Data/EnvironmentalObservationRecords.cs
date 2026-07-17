using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Data;

internal enum EnvironmentalClockDiagnostic
{
    WithinTolerance,
    ClockAhead,
    ClockBehindOrDeliveryDelayed
}

internal sealed class EnvironmentalObservationSourceRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string IdentitySha256 { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
    public Guid SiteId { get; init; }
    public Observatory? Site { get; init; }
    public Guid? AgentId { get; init; }
    public string? RigId { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public EnvironmentalObservationSourceKind Kind { get; init; }
    public string MethodName { get; init; } = string.Empty;
    public string MethodVersion { get; init; } = string.Empty;
    public string ParametersJson { get; init; } = string.Empty;
    public string ParametersSha256 { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public ICollection<EnvironmentalObservationRecord> Observations { get; } = [];
}

internal sealed class EnvironmentalObservationRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceRecordId { get; init; }
    public EnvironmentalObservationSourceRecord? Source { get; init; }
    public Guid SiteId { get; init; }
    public Guid? AgentId { get; init; }
    public string? RigId { get; init; }
    public EnvironmentalObservationSourceKind SourceKind { get; init; }
    public string SourceIdentitySha256 { get; init; } = string.Empty;
    public Guid ObservationId { get; init; }
    public string SchemaVersion { get; init; } = string.Empty;
    public EnvironmentalObservationKind Kind { get; init; }
    public EnvironmentalObservationUnit Unit { get; init; }
    public double? NumericValue { get; init; }
    public bool? BooleanValue { get; init; }
    public EnvironmentalObservationQuality Quality { get; init; }
    public double? Uncertainty { get; init; }
    public double? SubmittedNumericValue { get; init; }
    public string? SubmittedUnit { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset? ObservedFromUtc { get; init; }
    public DateTimeOffset? ObservedThroughUtc { get; init; }
    public DateTimeOffset ValidFromUtc { get; init; }
    public DateTimeOffset ValidThroughUtc { get; init; }
    public DateTimeOffset StaleAfterUtc { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public double ApparentClockOffsetSeconds { get; init; }
    public EnvironmentalClockDiagnostic ClockDiagnostic { get; init; }
    public string PayloadSha256 { get; init; } = string.Empty;
    public ICollection<EnvironmentalObservationLineageRecord> Lineage { get; } = [];
    public ICollection<EnvironmentalObservationLineageRecord> ReferencedBy { get; } = [];
}

internal sealed class EnvironmentalObservationLineageRecord
{
    public Guid DerivedObservationRecordId { get; init; }
    public EnvironmentalObservationRecord? DerivedObservation { get; init; }
    public int Ordinal { get; init; }
    public Guid SourceObservationRecordId { get; init; }
    public EnvironmentalObservationRecord? SourceObservation { get; init; }
}
