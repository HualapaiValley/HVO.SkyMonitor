namespace HVO.SkyMonitor.LogicHost.Data;

internal enum ObservatoryInvitationDispositionAction
{
    Accepted,
    Declined,
    Revoked,
    Expired
}

internal sealed class ObservatoryInvitation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public string TargetUserId { get; set; } = string.Empty;
    public string TargetEmailSha256 { get; set; } = string.Empty;
    public ObservatoryMembershipRole OfferedRole { get; set; }
    public string InvitedByUserId { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string AcceptanceTokenSha256 { get; set; } = string.Empty;
    public string CanonicalSha256 { get; set; } = string.Empty;
    public ICollection<ObservatoryInvitationDisposition> Dispositions { get; } = [];
}

internal sealed class ObservatoryInvitationDisposition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid InvitationId { get; set; }
    public ObservatoryInvitation? Invitation { get; set; }
    public ObservatoryInvitationDispositionAction Action { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
}
