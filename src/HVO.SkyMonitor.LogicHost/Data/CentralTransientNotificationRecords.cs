using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Data;

internal enum CentralTransientNotificationDispatchState
{
    Pending,
    Fenced,
    Sent,
    Failed,
    Suppressed
}

internal sealed class CentralTransientNotificationRecord
{
    public Guid NotificationId { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string Channel { get; set; } = string.Empty;
    public TransientNotificationState State { get; set; }
    public Guid AssessmentId { get; set; }
    public CentralTransientAssessmentRecord? Assessment { get; set; }
    public string? ReasonCode { get; set; }
    public Guid? SupersedesNotificationId { get; set; }
    public DateTimeOffset? SupersedesNotificationCreatedUtc { get; set; }
    public CentralTransientNotificationRecord? SupersedesNotification { get; set; }
}

internal sealed class CentralTransientEventVersionNotification
{
    public Guid CentralTransientEventId { get; set; }
    public Guid EventVersionId { get; set; }
    public CentralTransientEventVersionRecord? EventVersion { get; set; }
    public int Ordinal { get; set; }
    public Guid NotificationId { get; set; }
    public CentralTransientNotificationRecord? Notification { get; set; }
}

internal sealed class CentralTransientNotificationDispatch
{
    public Guid DispatchId { get; init; } = Guid.NewGuid();
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid ReviewId { get; set; }
    public CentralTransientReviewRecord? Review { get; set; }
    public Guid InitialNotificationId { get; set; }
    public CentralTransientNotificationRecord? InitialNotification { get; set; }
    public Guid LatestNotificationId { get; set; }
    public CentralTransientNotificationRecord? LatestNotification { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string? Recipient { get; set; }
    public string? RecipientIdentitySha256 { get; set; }
    public CentralTransientNotificationDispatchState State { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? FencedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? ReasonCode { get; set; }
    public Guid? SupersedesDispatchId { get; set; }
    public CentralTransientNotificationDispatch? SupersedesDispatch { get; set; }
    public string? RequestedByActorIdentity { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CanonicalRequestSha256 { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
