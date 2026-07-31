namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class LogicalCamera
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeactivatedAtUtc { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
    public ICollection<LogicalCameraInstallation> Installations { get; } = [];
}

internal sealed class LogicalCameraInstallation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid LogicalCameraId { get; set; }
    public LogicalCamera? LogicalCamera { get; set; }
    public Guid RegistrationId { get; set; }
    public DeviceRegistration? Registration { get; set; }
    public Guid InstallationPublicId { get; set; }
    public DateTimeOffset AssignedAtUtc { get; set; }
    public DateTimeOffset? RetiredAtUtc { get; set; }
    public Guid? ReplacesInstallationId { get; set; }
    public LogicalCameraInstallation? ReplacesInstallation { get; set; }
    public string AssignedByUserId { get; set; } = string.Empty;
    public string? RetiredByUserId { get; set; }
    public string AssignmentReasonCode { get; set; } = string.Empty;
    public string? RetirementReasonCode { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
