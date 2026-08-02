using System.Data;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record PublicRecordSubject(
    PublicRecordSubjectKind Kind,
    Guid SubjectId,
    Guid? SourceEventVersionId = null);

internal enum PublicRecordPublicationOutcome
{
    Applied,
    Unchanged,
    NotFoundOrDenied,
    Conflict,
    Invalid
}

internal sealed record PublicRecordPublicationResult(
    PublicRecordPublicationOutcome Outcome,
    Guid? DecisionId = null,
    Guid? PublicId = null);

internal interface IPublicRecordPublicationService
{
    Task<PublicRecordPublicationResult> DecideAsync(
        Guid authorityObservatoryId,
        string actorUserId,
        PublicRecordSubject subject,
        PublicationDecisionState state,
        string projectionSchemaVersion,
        string reasonCode,
        CancellationToken cancellationToken = default);
}

internal sealed class PublicRecordPublicationService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<PublicRecordPublicationService>? logger = null) : IPublicRecordPublicationService
{
    public async Task<PublicRecordPublicationResult> DecideAsync(
        Guid authorityObservatoryId,
        string actorUserId,
        PublicRecordSubject subject,
        PublicationDecisionState state,
        string projectionSchemaVersion,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentNullException.ThrowIfNull(subject);
        projectionSchemaVersion = projectionSchemaVersion.Trim();
        reasonCode = reasonCode.Trim();
        if (!Enum.IsDefined(subject.Kind) || subject.SubjectId == Guid.Empty || !Enum.IsDefined(state)
            || projectionSchemaVersion.Length is < 1 or > 32 || reasonCode.Length is < 1 or > 128
            || (subject.Kind == PublicRecordSubjectKind.TransientEvent) != subject.SourceEventVersionId.HasValue)
        {
            return new(PublicRecordPublicationOutcome.Invalid);
        }
        var isRelational = dbContext.Database.IsRelational();
        var holdTarget = isRelational && state == PublicationDecisionState.Released
            ? subject.Kind switch
            {
                PublicRecordSubjectKind.Artifact => await CentralTransientPayloadHoldFence.ReadArtifactAsync(
                    dbContext, subject.SubjectId, cancellationToken).ConfigureAwait(false),
                PublicRecordSubjectKind.TransientDerivative => await CentralTransientPayloadHoldFence.ReadDerivativeAsync(
                    dbContext, subject.SubjectId, cancellationToken).ConfigureAwait(false),
                _ => null
            }
            : null;
        await using var holdScope = holdTarget is null
            ? null
            : await CentralTransientPayloadHoldFence.AcquireAsync(
                dbContext, [holdTarget], cancellationToken).ConfigureAwait(false);
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (isRelational && state == PublicationDecisionState.Released && holdScope is not null)
        {
            try
            {
                await CentralTransientPayloadHoldFence.ValidateAsync(
                    dbContext, holdScope.Targets, cancellationToken).ConfigureAwait(false);
            }
            catch (CentralTransientPayloadHoldRejectedException)
            {
                return new(PublicRecordPublicationOutcome.NotFoundOrDenied);
            }
        }
        if (!await ObservatoryMembershipAccess.ForOwner(dbContext, actorUserId)
            .AnyAsync(item => item.ObservatoryId == authorityObservatoryId, cancellationToken).ConfigureAwait(false)
            || !await SubjectBelongsToAuthorityAsync(authorityObservatoryId, subject, state, cancellationToken)
                .ConfigureAwait(false))
        {
            return new(PublicRecordPublicationOutcome.NotFoundOrDenied);
        }
        if (isRelational && subject.Kind == PublicRecordSubjectKind.TransientEvent)
        {
            _ = await dbContext.CentralTransientEvents.FromSqlInterpolated($"""
                    SELECT * FROM [CentralTransientEvents] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {subject.SubjectId}
                    """)
                .AsNoTracking()
                .Select(item => item.Id)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        var history = await CurrentSubjectHistory(isRelational, authorityObservatoryId, subject)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var current = history.SingleOrDefault(candidate => !history.Any(item =>
            item.SupersedesDecisionId == candidate.Id));
        if (current?.State == state)
        {
            return new(PublicRecordPublicationOutcome.Unchanged, current.Id, current.PublicId);
        }
        if (state == PublicationDecisionState.Withdrawn && current?.State != PublicationDecisionState.Released)
        {
            return new(PublicRecordPublicationOutcome.Conflict);
        }

        var publicId = current?.PublicId ?? await FindExistingPublicIdAsync(subject, cancellationToken)
            .ConfigureAwait(false) ?? Guid.NewGuid();
        var decision = new PublicRecordPublicationDecision
        {
            PublicId = publicId,
            AuthorityObservatoryId = authorityObservatoryId,
            SubjectKind = subject.Kind,
            State = state,
            LogicalCameraId = subject.Kind == PublicRecordSubjectKind.LogicalCamera ? subject.SubjectId : null,
            CentralArtifactId = subject.Kind == PublicRecordSubjectKind.Artifact ? subject.SubjectId : null,
            CentralTransientEventId = subject.Kind == PublicRecordSubjectKind.TransientEvent ? subject.SubjectId : null,
            SourceEventVersionId = subject.SourceEventVersionId,
            CentralTransientDerivativeId = subject.Kind == PublicRecordSubjectKind.TransientDerivative
                ? subject.SubjectId
                : null,
            ProjectionSchemaVersion = projectionSchemaVersion,
            OccurredAtUtc = timeProvider.GetUtcNow(),
            ActorUserId = actorUserId,
            ReasonCode = reasonCode,
            SupersedesDecisionId = current?.Id
        };
        dbContext.PublicRecordPublicationDecisions.Add(decision);
        if (subject.Kind == PublicRecordSubjectKind.TransientEvent
            && state == PublicationDecisionState.Released)
        {
            await QueueRegisteredUserNotificationsAsync(
                subject.SubjectId,
                publicId,
                cancellationToken).ConfigureAwait(false);
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (logger is not null)
        {
            OperatorUiAuditLog.Publication(logger, subject.Kind.ToString(), "applied", state.ToString());
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(PublicRecordPublicationOutcome.Applied, decision.Id, decision.PublicId);
    }

    private async Task QueueRegisteredUserNotificationsAsync(
        Guid centralTransientEventId,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        var deduplicationKey = $"verified-event:{publicId:N}";
        var userIds = await dbContext.RegisteredUserSubscriptions.AsNoTracking()
            .Where(subscription => subscription.Kind == RegisteredUserSubscriptionKind.VerifiedEvent
                && subscription.User!.AccountType == AccountType.User
                && !dbContext.RegisteredUserNotificationPreferences.Any(preference =>
                    preference.UserId == subscription.UserId && !preference.InAppEnabled)
                && !dbContext.RegisteredUserNotifications.Any(notification =>
                    notification.UserId == subscription.UserId
                    && notification.DeduplicationKey == deduplicationKey))
            .Select(subscription => subscription.UserId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        foreach (var userId in userIds)
        {
            dbContext.RegisteredUserNotifications.Add(new RegisteredUserNotification
            {
                UserId = userId,
                Kind = RegisteredUserNotificationKind.VerifiedEventReleased,
                CentralTransientEventId = centralTransientEventId,
                PublicRecordId = publicId,
                Title = "A verified sky event was released",
                CreatedUtc = now,
                DeduplicationKey = deduplicationKey
            });
        }
    }

    private Task<Guid?> FindExistingPublicIdAsync(
        PublicRecordSubject subject,
        CancellationToken cancellationToken)
        => dbContext.PublicRecordPublicationDecisions.AsNoTracking()
            .Where(item => item.SubjectKind == subject.Kind
                && item.LogicalCameraId == (subject.Kind == PublicRecordSubjectKind.LogicalCamera
                    ? subject.SubjectId
                    : null)
                && item.CentralArtifactId == (subject.Kind == PublicRecordSubjectKind.Artifact
                    ? subject.SubjectId
                    : null)
                && item.CentralTransientEventId == (subject.Kind == PublicRecordSubjectKind.TransientEvent
                    ? subject.SubjectId
                    : null)
                && item.SourceEventVersionId == subject.SourceEventVersionId
                && item.CentralTransientDerivativeId == (subject.Kind == PublicRecordSubjectKind.TransientDerivative
                    ? subject.SubjectId
                    : null))
            .OrderBy(item => item.OccurredAtUtc)
            .Select(item => (Guid?)item.PublicId)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<bool> SubjectBelongsToAuthorityAsync(
        Guid observatoryId,
        PublicRecordSubject subject,
        PublicationDecisionState state,
        CancellationToken cancellationToken)
        => subject.Kind switch
        {
            PublicRecordSubjectKind.LogicalCamera => await dbContext.LogicalCameras.AnyAsync(item =>
                item.Id == subject.SubjectId && item.ObservatoryId == observatoryId
                && (state == PublicationDecisionState.Withdrawn || item.DeactivatedAtUtc == null), cancellationToken)
                .ConfigureAwait(false),
            PublicRecordSubjectKind.Artifact => await dbContext.CentralArtifacts.AnyAsync(item =>
                item.Id == subject.SubjectId
                && item.Frame!.ObservatoryId == observatoryId
                && (state == PublicationDecisionState.Withdrawn
                    || item.ObjectState == CentralArtifactObjectState.Available
                    && item.ReconstructionState == CentralReconstructionState.Complete
                    && (item.Role == FrameArtifactRole.Preview || item.Role == FrameArtifactRole.AnnotatedPreview)
                    && (item.MediaType == "image/jpeg" || item.MediaType == "image/png"
                        || item.MediaType == "image/webp")), cancellationToken).ConfigureAwait(false),
            PublicRecordSubjectKind.TransientEvent => await dbContext.Observatories.AnyAsync(observatory =>
                observatory.Id == observatoryId && observatory.IsActive, cancellationToken).ConfigureAwait(false)
                && await dbContext.CentralTransientEventCurrent.AnyAsync(current =>
                current.CentralTransientEventId == subject.SubjectId
                && (state == PublicationDecisionState.Withdrawn
                    || current.LatestEventVersionId == subject.SourceEventVersionId
                    && (current.ReviewState == CentralTransientReviewState.Reviewed
                        || current.ReviewState == CentralTransientReviewState.Overridden)
                    && current.ActiveAssessment!.Authority == TransientAssessmentAuthority.Authoritative)
                && dbContext.CentralTransientObservationSources.Any(source =>
                    source.Observation!.CentralTransientEventId == subject.SubjectId
                    && source.Artifact!.Frame!.ObservatoryId == observatoryId), cancellationToken)
                .ConfigureAwait(false),
            PublicRecordSubjectKind.TransientDerivative => await dbContext.CentralTransientDerivatives.AnyAsync(item =>
                item.DerivativeId == subject.SubjectId
                && (state == PublicationDecisionState.Withdrawn
                    || item.OutputIntent!.ObjectState == CentralArtifactObjectState.Available)
                && item.Sources.Any(source => dbContext.CentralArtifacts.Any(artifact =>
                    artifact.Id == source.CentralArtifactId
                    && artifact.Frame!.ObservatoryId == observatoryId)), cancellationToken).ConfigureAwait(false),
            _ => false
        };

    private IQueryable<PublicRecordPublicationDecision> CurrentSubjectHistory(
        bool isRelational,
        Guid observatoryId,
        PublicRecordSubject subject)
    {
        var query = isRelational
            ? dbContext.PublicRecordPublicationDecisions.FromSqlInterpolated($"""
                SELECT * FROM [PublicRecordPublicationDecisions] WITH (UPDLOCK, HOLDLOCK)
                WHERE [AuthorityObservatoryId] = {observatoryId}
                """)
            : dbContext.PublicRecordPublicationDecisions.Where(item =>
                item.AuthorityObservatoryId == observatoryId);
        return query.Where(item => item.SubjectKind == subject.Kind
            && item.LogicalCameraId == (subject.Kind == PublicRecordSubjectKind.LogicalCamera
                ? subject.SubjectId
                : null)
            && item.CentralArtifactId == (subject.Kind == PublicRecordSubjectKind.Artifact
                ? subject.SubjectId
                : null)
            && item.CentralTransientEventId == (subject.Kind == PublicRecordSubjectKind.TransientEvent
                ? subject.SubjectId
                : null)
            && item.SourceEventVersionId == subject.SourceEventVersionId
            && item.CentralTransientDerivativeId == (subject.Kind == PublicRecordSubjectKind.TransientDerivative
                ? subject.SubjectId
                : null));
    }
}
