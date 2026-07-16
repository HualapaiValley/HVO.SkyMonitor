using HVO.SkyMonitor.Fleet.Contracts;

namespace HVO.SkyMonitor.LogicHost.Data;

internal enum FleetClockDiagnostic
{
    WithinTolerance,
    ClockAhead,
    ClockBehindOrDeliveryDelayed
}

internal sealed class DeviceFleetState
{
    public Guid RegistrationId { get; init; }
    public DeviceRegistration? Registration { get; init; }
    public Guid AgentInstanceId { get; set; }
    public Guid BootSessionId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public double ApparentClockOffsetSeconds { get; set; }
    public FleetClockDiagnostic ClockDiagnostic { get; set; }
    public FleetHealth ReportedHealth { get; set; }
    public bool HasStoragePressure { get; set; }
    public bool HasRequiredLaneFailure { get; set; }
    public bool HasQuarantine { get; set; }
    public string SoftwareVersion { get; set; } = string.Empty;
    public string ConfigurationSha256 { get; set; } = string.Empty;
    public string StatusFingerprint { get; set; } = string.Empty;
    public string CurrentPayloadSha256 { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public DateTimeOffset? LastSequenceGapUtc { get; set; }
    public long SequenceGapCount { get; set; }
    public DateTimeOffset? LastBootSessionChangeUtc { get; set; }
    public long BootSessionChangeCount { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

internal sealed class DeviceHeartbeatRecord
{
    public long Id { get; init; }
    public Guid RegistrationId { get; init; }
    public DeviceRegistration? Registration { get; init; }
    public Guid AgentInstanceId { get; init; }
    public Guid BootSessionId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public string PayloadSha256 { get; init; } = string.Empty;
    public string StatusFingerprint { get; init; } = string.Empty;
    public FleetHealth ReportedHealth { get; init; }
    public FleetClockDiagnostic ClockDiagnostic { get; init; }
    public bool AdvancedCurrent { get; init; }
    public bool IsSignificantSnapshot { get; init; }
    public string? SnapshotJson { get; init; }
}
