namespace HVO.SkyMonitor.LogicHost.Data;

internal enum CuratedPublicSurface
{
    HomeObservatory,
    HomeEvent
}

internal enum CuratedPlacementState
{
    Featured,
    Suppressed,
    Cleared
}

internal enum RegisteredUserSubscriptionKind
{
    VerifiedEvent
}

internal enum RegisteredUserNotificationKind
{
    VerifiedEventReleased
}

internal sealed class CuratedPublicPlacementDecision
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public CuratedPublicSurface Surface { get; set; }
    public CuratedPlacementState State { get; set; }
    public Guid? ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public Guid? PublicRecordId { get; set; }
    public int? DisplayOrder { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public Guid? SupersedesDecisionId { get; set; }
    public CuratedPublicPlacementDecision? SupersedesDecision { get; set; }
}

internal sealed class RegisteredUserObservatoryFollow
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

internal sealed class RegisteredUserTransientEventBookmark
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

internal sealed class RegisteredUserSubscription
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public RegisteredUserSubscriptionKind Kind { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

internal sealed class RegisteredUserNotificationPreference
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public bool InAppEnabled { get; set; } = true;
    public bool EmailEnabled { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

internal sealed class RegisteredUserNotification
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public RegisteredUserNotificationKind Kind { get; set; }
    public Guid? CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public Guid PublicRecordId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? ReadUtc { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}
