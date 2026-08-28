namespace HVO.SkyMonitor.LogicHost.Data;

internal enum ObservatoryMembershipRole
{
    Viewer,
    Manager,
    Owner
}

internal enum ObservatoryMembershipAuditAction
{
    Granted,
    RoleChanged,
    Removed
}

internal sealed class ObservatoryMembership
{
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public ObservatoryMembershipRole Role { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}

internal sealed class ObservatoryMembershipAudit
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public string TargetUserId { get; set; } = string.Empty;
    public string? ActorUserId { get; set; }
    public ObservatoryMembershipAuditAction Action { get; set; }
    public ObservatoryMembershipRole? PreviousRole { get; set; }
    public ObservatoryMembershipRole? NewRole { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}
