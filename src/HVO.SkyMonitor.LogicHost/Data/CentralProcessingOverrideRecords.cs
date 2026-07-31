namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralProcessingOverrideVersion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public int Version { get; set; }
    public int? CloudTransmissionThresholdMillionths { get; set; }
    public bool? CentralValidationEnabled { get; set; }
    public DateTimeOffset EffectiveFromUtc { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
}
