namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralArtifactDownloadAuthorization
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? CentralArtifact { get; set; }
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public ObservatoryMembershipRole MembershipRole { get; set; }
    public long? RangeStart { get; set; }
    public long? RangeEnd { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string TokenSha256 { get; set; } = string.Empty;
}
