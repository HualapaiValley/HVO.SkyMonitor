using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralTransientNotificationRetryResponse(
    Guid DispatchId,
    Guid NotificationId,
    Guid EventVersionId,
    string ETag,
    bool Replayed);

internal enum CentralTransientNotificationRetryStatus
{
    Scheduled,
    NotFound,
    Invalid,
    Ineligible,
    PreconditionFailed,
    IdempotencyConflict
}

internal sealed record CentralTransientNotificationRetryResult(
    CentralTransientNotificationRetryStatus Status,
    CentralTransientNotificationRetryResponse? Response = null);

internal interface ICentralTransientNotificationRetryService
{
    Task<CentralTransientNotificationRetryResult> RetryAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        Guid notificationId,
        byte[] expectedRowVersion,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class CentralTransientNotificationRetryService(
    ApplicationDbContext dbContext,
    ICentralTransientEventVersionAppender versionAppender,
    TimeProvider timeProvider) : ICentralTransientNotificationRetryService
{
    private const string RequestSchemaVersion = "central-transient-notification-retry-v1";

    public async Task<CentralTransientNotificationRetryResult> RetryAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        Guid notificationId,
        byte[] expectedRowVersion,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var actor = CentralArtifactCredentialAccess.GetSubject(principal);
        if (!CentralArtifactCredentialAccess.HasScope(principal, "api.admin") ||
            string.IsNullOrWhiteSpace(actor) || centralTransientEventId == Guid.Empty || notificationId == Guid.Empty ||
            expectedRowVersion.Length != 8 || idempotencyKey.Length is < 1 or > 128)
        {
            return new(CentralTransientNotificationRetryStatus.Invalid);
        }

        var canonicalRequestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            RequestSchemaVersion,
            centralTransientEventId.ToString("N"),
            notificationId.ToString("N")))));
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var acquired = await dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientEvents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {centralTransientEventId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (acquired != 1)
        {
            return new(CentralTransientNotificationRetryStatus.NotFound);
        }

        var replay = await dbContext.CentralTransientNotificationDispatches.AsNoTracking().SingleOrDefaultAsync(item =>
            item.CentralTransientEventId == centralTransientEventId &&
            item.RequestedByActorIdentity == actor && item.IdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (!string.Equals(replay.CanonicalRequestSha256, canonicalRequestSha256, StringComparison.Ordinal))
            {
                return new(CentralTransientNotificationRetryStatus.IdempotencyConflict);
            }
            var currentReplay = await dbContext.CentralTransientEventCurrent.AsNoTracking().SingleAsync(
                item => item.CentralTransientEventId == centralTransientEventId, cancellationToken).ConfigureAwait(false);
            var notification = await dbContext.CentralTransientNotifications.AsNoTracking().SingleAsync(
                item => item.NotificationId == replay.InitialNotificationId, cancellationToken).ConfigureAwait(false);
            var versionId = await dbContext.Set<CentralTransientEventVersionNotification>().AsNoTracking()
                .Where(item => item.CentralTransientEventId == centralTransientEventId &&
                    item.NotificationId == notification.NotificationId)
                .OrderBy(item => item.EventVersion!.Version)
                .Select(item => item.EventVersionId)
                .FirstAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(CentralTransientNotificationRetryStatus.Scheduled, new(
                replay.DispatchId,
                notification.NotificationId,
                versionId,
                CentralTransientEventEtag.Create(currentReplay.RowVersion),
                true));
        }

        if (await dbContext.CentralTransientPayloadReleases.AsNoTracking().AnyAsync(
                item => item.CentralTransientEventId == centralTransientEventId, cancellationToken)
            .ConfigureAwait(false))
        {
            return new(CentralTransientNotificationRetryStatus.Ineligible);
        }

        var current = await dbContext.CentralTransientEventCurrent.SingleOrDefaultAsync(
            item => item.CentralTransientEventId == centralTransientEventId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new(CentralTransientNotificationRetryStatus.NotFound);
        }
        if (!current.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return new(CentralTransientNotificationRetryStatus.PreconditionFailed);
        }
        var predecessorDispatch = await dbContext.CentralTransientNotificationDispatches.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CentralTransientEventId == centralTransientEventId &&
                item.LatestNotificationId == notificationId, cancellationToken).ConfigureAwait(false);
        if (predecessorDispatch is null)
        {
            return new(CentralTransientNotificationRetryStatus.NotFound);
        }
        if (current.ActiveAssessmentId != predecessorDispatch.AssessmentId ||
            current.LatestReviewId != predecessorDispatch.ReviewId ||
            current.ReviewState == CentralTransientReviewState.NeedsReview)
        {
            return new(CentralTransientNotificationRetryStatus.Ineligible);
        }
        if (predecessorDispatch.State != CentralTransientNotificationDispatchState.Failed ||
            string.IsNullOrWhiteSpace(predecessorDispatch.Recipient) ||
            string.Equals(predecessorDispatch.ReasonCode, "notification.ambiguous-post-fence", StringComparison.Ordinal))
        {
            return new(CentralTransientNotificationRetryStatus.Ineligible);
        }
        if (await dbContext.CentralTransientNotificationDispatches.AsNoTracking().AnyAsync(item =>
                item.SupersedesDispatchId == predecessorDispatch.DispatchId, cancellationToken).ConfigureAwait(false))
        {
            return new(CentralTransientNotificationRetryStatus.Ineligible);
        }

        var newNotificationId = Guid.NewGuid();
        var appended = await versionAppender.AppendGeneratedAsync(centralTransientEventId, previous =>
        {
            var predecessor = previous.Notifications.Single(item =>
                item.NotificationId == predecessorDispatch.LatestNotificationId);
            var createdUtc = timeProvider.GetUtcNow();
            if (createdUtc <= previous.VersionCreatedUtc || createdUtc <= predecessor.CreatedUtc)
            {
                createdUtc = new[] { previous.VersionCreatedUtc, predecessor.CreatedUtc }.Max().AddTicks(1);
            }
            return previous with
            {
                EventVersionId = Guid.NewGuid(),
                Version = previous.Version + 1,
                PreviousEventVersionId = previous.EventVersionId,
                PreviousVersionCreatedUtc = previous.VersionCreatedUtc,
                VersionCreatedUtc = createdUtc,
                Notifications = previous.Notifications.Append(new TransientNotificationV1(
                    newNotificationId,
                    createdUtc,
                    predecessorDispatch.Channel,
                    TransientNotificationState.Pending,
                    predecessorDispatch.AssessmentId,
                    null,
                    predecessor.NotificationId)).ToArray()
            };
        }, resetReviewState: false, cancellationToken).ConfigureAwait(false);

        var retryDispatch = new CentralTransientNotificationDispatch
        {
            CentralTransientEventId = centralTransientEventId,
            AssessmentId = predecessorDispatch.AssessmentId,
            ReviewId = predecessorDispatch.ReviewId,
            InitialNotificationId = newNotificationId,
            LatestNotificationId = newNotificationId,
            Channel = predecessorDispatch.Channel,
            Recipient = predecessorDispatch.Recipient,
            RecipientIdentitySha256 = predecessorDispatch.RecipientIdentitySha256,
            State = CentralTransientNotificationDispatchState.Pending,
            CreatedUtc = appended.Event.VersionCreatedUtc,
            SupersedesDispatchId = predecessorDispatch.DispatchId,
            RequestedByActorIdentity = actor,
            IdempotencyKey = idempotencyKey,
            CanonicalRequestSha256 = canonicalRequestSha256
        };
        dbContext.CentralTransientNotificationDispatches.Add(retryDispatch);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var resultRowVersion = appended.Current.RowVersion.ToArray();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(CentralTransientNotificationRetryStatus.Scheduled, new(
            retryDispatch.DispatchId,
            newNotificationId,
            appended.Event.EventVersionId,
            CentralTransientEventEtag.Create(resultRowVersion),
            false));
    }
}
