using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralTransientEventReadService
{
    Task<CentralTransientEventPage> ListAsync(
        ClaimsPrincipal principal,
        int take,
        string? cursor,
        CancellationToken cancellationToken);

    Task<CentralTransientEventDetail?> GetAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CentralTransientPublicationAuthority>> ListPublicationAuthoritiesAsync(
        ClaimsPrincipal principal,
        IReadOnlyCollection<Guid> centralTransientEventIds,
        CancellationToken cancellationToken);
}

internal interface ICentralTransientReviewService
{
    Task<IReadOnlySet<Guid>> ListReviewableEventIdsAsync(
        ClaimsPrincipal principal,
        IReadOnlyCollection<Guid> centralTransientEventIds,
        CancellationToken cancellationToken);

    Task<CentralTransientReviewMutationResult> ReviewAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        byte[] expectedRowVersion,
        string idempotencyKey,
        CentralTransientReviewRequest request,
        CancellationToken cancellationToken);
}

internal sealed record CentralTransientReviewRequest(
    Guid AssessmentId,
    TransientReviewDisposition Disposition,
    TransientReviewOverrideV1? Override,
    IReadOnlyList<string> ReasonCodes);

internal sealed record CentralTransientEventSummary(
    Guid CentralTransientEventId,
    Guid LatestEventVersionId,
    Guid EventId,
    string AgentId,
    DateTimeOffset EventCreatedUtc,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc,
    int Version,
    TransientEventState EventState,
    CentralTransientReviewState ReviewState,
    Guid ActiveAssessmentId,
    TransientClassification EffectiveClassification,
    TransientMeteorSeverity? EffectiveMeteorSeverity,
    int EffectiveConfidenceMillionths,
    string ETag);

internal sealed record CentralTransientPublicationAuthority(
    Guid CentralTransientEventId,
    Guid ObservatoryId,
    string ObservatoryName,
    PublicationDecisionState? CurrentState);

internal sealed record CentralTransientEventPage(
    IReadOnlyList<CentralTransientEventSummary> Items,
    string? NextCursor);

internal sealed record CentralTransientEventDetail(
    CentralTransientEventSummary Summary,
    TransientEventV1 Event);

internal sealed record CentralTransientReviewMutationResponse(
    Guid ReviewId,
    Guid EventVersionId,
    CentralTransientReviewState ReviewState,
    TransientClassification EffectiveClassification,
    TransientMeteorSeverity? EffectiveMeteorSeverity,
    int EffectiveConfidenceMillionths,
    string ETag,
    bool Replayed);

internal sealed record CentralTransientReviewMutationResult(
    CentralTransientReviewMutationStatus Status,
    CentralTransientReviewMutationResponse? Response = null);

internal enum CentralTransientReviewMutationStatus
{
    Applied,
    NotFound,
    Invalid,
    PreconditionFailed,
    IdempotencyConflict
}

internal sealed record CentralTransientEffectiveAssessment(
    TransientClassification Classification,
    TransientMeteorSeverity? MeteorSeverity,
    int ConfidenceMillionths);

internal static class CentralTransientReviewProjection
{
    public static CentralTransientEffectiveAssessment Resolve(
        TransientClassification assessmentClassification,
        TransientMeteorSeverity? assessmentMeteorSeverity,
        int assessmentConfidenceMillionths,
        TransientReviewOverrideV1? reviewOverride)
        => reviewOverride is null
            ? new(assessmentClassification, assessmentMeteorSeverity, assessmentConfidenceMillionths)
            : new(reviewOverride.Classification, reviewOverride.MeteorSeverity, reviewOverride.ConfidenceMillionths);
}

internal sealed class CentralTransientEventReadService(ApplicationDbContext dbContext)
    : ICentralTransientEventReadService
{
    public async Task<CentralTransientEventPage> ListAsync(
        ClaimsPrincipal principal,
        int take,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var isAdmin = CentralArtifactCredentialAccess.HasScope(principal, "api.admin");
        var ownerId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (!isAdmin && (string.IsNullOrWhiteSpace(ownerId) || !CentralArtifactCredentialAccess.HasOwnerCredential(principal)))
        {
            return new([], null);
        }

        var query = ApplyAccess(
            dbContext,
            dbContext.CentralTransientEventCurrent.AsNoTracking(),
            isAdmin,
            ownerId,
            CentralArtifactCredentialAccess.GetObservatoryScope(principal));
        if (!TryDecodeCursor(cursor, out var cursorValue))
        {
            throw new ArgumentException("The transient event cursor is invalid.", nameof(cursor));
        }
        if (cursorValue is not null)
        {
            query = query.Where(item => item.Event!.EventCreatedUtc < cursorValue.EventCreatedUtc ||
                item.Event.EventCreatedUtc == cursorValue.EventCreatedUtc &&
                item.CentralTransientEventId.CompareTo(cursorValue.CentralTransientEventId) < 0);
        }

        var rows = await query.OrderByDescending(item => item.Event!.EventCreatedUtc)
            .ThenByDescending(item => item.CentralTransientEventId)
            .Take(take + 1)
            .Select(item => new EventProjection(
                item.CentralTransientEventId,
                item.LatestEventVersionId,
                item.Event!.EventId,
                item.Event.AgentId,
                item.Event.EventCreatedUtc,
                item.LatestEventVersion!.FirstObservedUtc,
                item.LatestEventVersion.LastObservedUtc,
                item.LatestVersion,
                item.LatestEventVersion.State,
                item.ReviewState,
                item.ActiveAssessmentId,
                item.EffectiveClassification,
                item.EffectiveMeteorSeverity,
                item.EffectiveConfidenceMillionths,
                item.RowVersion))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        var pageRows = rows.Take(take).ToArray();
        var nextCursor = hasMore && pageRows.Length != 0
            ? EncodeCursor(pageRows[^1].EventCreatedUtc, pageRows[^1].CentralTransientEventId)
            : null;
        return new(pageRows.Select(Project).ToArray(), nextCursor);
    }

    public async Task<CentralTransientEventDetail?> GetAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var isAdmin = CentralArtifactCredentialAccess.HasScope(principal, "api.admin");
        var ownerId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (!isAdmin && (string.IsNullOrWhiteSpace(ownerId) || !CentralArtifactCredentialAccess.HasOwnerCredential(principal)))
        {
            return null;
        }

        var row = await ApplyAccess(
                dbContext,
                dbContext.CentralTransientEventCurrent.AsNoTracking(),
                isAdmin,
                ownerId,
                CentralArtifactCredentialAccess.GetObservatoryScope(principal))
            .Where(item => item.CentralTransientEventId == centralTransientEventId)
            .Select(item => new DetailProjection(
                new EventProjection(
                    item.CentralTransientEventId,
                    item.LatestEventVersionId,
                    item.Event!.EventId,
                    item.Event.AgentId,
                    item.Event.EventCreatedUtc,
                    item.LatestEventVersion!.FirstObservedUtc,
                    item.LatestEventVersion.LastObservedUtc,
                    item.LatestVersion,
                    item.LatestEventVersion.State,
                    item.ReviewState,
                    item.ActiveAssessmentId,
                    item.EffectiveClassification,
                    item.EffectiveMeteorSeverity,
                    item.EffectiveConfidenceMillionths,
                    item.RowVersion),
                item.LatestEventVersion!.CanonicalEventJson))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }
        var parsed = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(row.CanonicalEventJson));
        return parsed.Value is not null && parsed.Validation.IsValid
            ? new(Project(row.Summary), parsed.Value with
            {
                Reviews = parsed.Value.Reviews.Select(review => review with
                {
                    ReviewerIdentity = "redacted"
                }).ToArray()
            })
            : throw new InvalidOperationException("Persisted transient event history is invalid.");
    }

    public async Task<IReadOnlyList<CentralTransientPublicationAuthority>> ListPublicationAuthoritiesAsync(
        ClaimsPrincipal principal,
        IReadOnlyCollection<Guid> centralTransientEventIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(centralTransientEventIds);
        if (centralTransientEventIds.Count > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(centralTransientEventIds));
        }
        var eventIds = centralTransientEventIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        var ownerId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (eventIds.Length == 0 || string.IsNullOrWhiteSpace(ownerId)
            || !CentralArtifactCredentialAccess.HasOwnerCredential(principal))
        {
            return [];
        }
        var isAdmin = CentralArtifactCredentialAccess.HasScope(principal, "api.admin");
        var accessibleEventIds = ApplyAccess(
                dbContext,
                dbContext.CentralTransientEventCurrent.AsNoTracking(),
                isAdmin,
                ownerId,
                CentralArtifactCredentialAccess.GetObservatoryScope(principal))
            .Where(current => eventIds.Contains(current.CentralTransientEventId))
            .Select(current => current.CentralTransientEventId);
        var authorities = await dbContext.CentralTransientObservationSources.AsNoTracking()
            .Where(source => accessibleEventIds.Contains(source.Observation!.CentralTransientEventId))
            .Select(source => new
            {
                EventId = source.Observation!.CentralTransientEventId,
                source.Artifact!.Frame!.ObservatoryId
            })
            .Join(ObservatoryMembershipAccess.ForOwner(dbContext, ownerId),
                source => source.ObservatoryId,
                membership => membership.ObservatoryId,
                (source, membership) => new
                {
                    source.EventId,
                    source.ObservatoryId,
                    ObservatoryName = membership.Observatory!.Name
                })
            .Join(dbContext.CentralTransientEventCurrent.AsNoTracking(),
                source => source.EventId,
                current => current.CentralTransientEventId,
                (source, current) => new
                {
                    source.EventId,
                    source.ObservatoryId,
                    source.ObservatoryName,
                    current.LatestEventVersionId
                })
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var authorityObservatoryIds = authorities.Select(item => item.ObservatoryId).Distinct().ToArray();
        var decisions = await dbContext.PublicRecordPublicationDecisions.AsNoTracking()
            .Where(decision => decision.SubjectKind == PublicRecordSubjectKind.TransientEvent
                && decision.CentralTransientEventId != null
                && decision.SourceEventVersionId != null
                && eventIds.Contains(decision.CentralTransientEventId.Value)
                && authorityObservatoryIds.Contains(decision.AuthorityObservatoryId)
                && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                    successor.SupersedesDecisionId == decision.Id))
            .Select(decision => new
            {
                EventId = decision.CentralTransientEventId!.Value,
                decision.AuthorityObservatoryId,
                SourceEventVersionId = decision.SourceEventVersionId!.Value,
                decision.State
            })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return authorities
            .Select(authority =>
            {
                var current = decisions.SingleOrDefault(decision =>
                    decision.EventId == authority.EventId
                    && decision.AuthorityObservatoryId == authority.ObservatoryId
                    && decision.SourceEventVersionId == authority.LatestEventVersionId);
                return new CentralTransientPublicationAuthority(
                    authority.EventId,
                    authority.ObservatoryId,
                    authority.ObservatoryName,
                    current?.State);
            })
            .OrderBy(item => item.CentralTransientEventId)
            .ThenBy(item => item.ObservatoryName, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IQueryable<CentralTransientEventCurrent> ApplyAccess(
        ApplicationDbContext dbContext,
        IQueryable<CentralTransientEventCurrent> query,
        bool isAdmin,
        string? ownerId,
        Guid? observatoryScope)
    {
        if (isAdmin)
        {
            return query;
        }
        var memberships = ObservatoryMembershipAccess.ForUser(dbContext, ownerId ?? string.Empty);
        if (observatoryScope is { } scope)
        {
            memberships = memberships.Where(membership => membership.ObservatoryId == scope);
        }
        var observatories = memberships.Select(membership => membership.ObservatoryId);
        return query.Where(current =>
            dbContext.CentralTransientObservationSources.Any(source =>
                source.Observation!.CentralTransientEventId == current.CentralTransientEventId) &&
            !dbContext.CentralTransientObservationSources.Any(source =>
                source.Observation!.CentralTransientEventId == current.CentralTransientEventId &&
                !observatories.Contains(source.Artifact!.Frame!.ObservatoryId)) &&
            !dbContext.CentralTransientObservationBackgrounds.Any(background =>
                background.Observation!.CentralTransientEventId == current.CentralTransientEventId &&
                !observatories.Contains(background.Artifact!.Frame!.ObservatoryId)));
    }

    private static CentralTransientEventSummary Project(EventProjection item)
        => new(
            item.CentralTransientEventId,
            item.LatestEventVersionId,
            item.EventId,
            item.AgentId,
            item.EventCreatedUtc,
            item.FirstObservedUtc,
            item.LastObservedUtc,
            item.Version,
            item.EventState,
            item.ReviewState,
            item.ActiveAssessmentId,
            item.EffectiveClassification,
            item.EffectiveMeteorSeverity,
            item.EffectiveConfidenceMillionths,
            CentralTransientEventEtag.Create(item.RowVersion));

    private static string EncodeCursor(DateTimeOffset eventCreatedUtc, Guid eventId)
        => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
            $"{eventCreatedUtc.UtcTicks}:{eventId:N}"));

    private static bool TryDecodeCursor(string? cursor, out EventCursor? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }
        try
        {
            var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor)).Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) ||
                !Guid.TryParseExact(parts[1], "N", out var eventId))
            {
                return false;
            }
            value = new(new DateTimeOffset(ticks, TimeSpan.Zero), eventId);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private sealed record EventProjection(
        Guid CentralTransientEventId,
        Guid LatestEventVersionId,
        Guid EventId,
        string AgentId,
        DateTimeOffset EventCreatedUtc,
        DateTimeOffset FirstObservedUtc,
        DateTimeOffset LastObservedUtc,
        int Version,
        TransientEventState EventState,
        CentralTransientReviewState ReviewState,
        Guid ActiveAssessmentId,
        TransientClassification EffectiveClassification,
        TransientMeteorSeverity? EffectiveMeteorSeverity,
        int EffectiveConfidenceMillionths,
        byte[] RowVersion);

    private sealed record DetailProjection(EventProjection Summary, string CanonicalEventJson);
    private sealed record EventCursor(DateTimeOffset EventCreatedUtc, Guid CentralTransientEventId);
}

internal sealed class CentralTransientReviewService(
    ApplicationDbContext dbContext,
    ICentralTransientEventVersionAppender versionAppender,
    TimeProvider timeProvider,
    CentralTransientLifecycleTelemetry? telemetry = null) : ICentralTransientReviewService
{
    public async Task<IReadOnlySet<Guid>> ListReviewableEventIdsAsync(
        ClaimsPrincipal principal,
        IReadOnlyCollection<Guid> centralTransientEventIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(centralTransientEventIds);
        if (centralTransientEventIds.Count > 100) throw new ArgumentOutOfRangeException(nameof(centralTransientEventIds));
        var isAdmin = CentralArtifactCredentialAccess.HasScope(principal, "api.admin");
        var ownerId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (!isAdmin && (string.IsNullOrWhiteSpace(ownerId)
            || !CentralArtifactCredentialAccess.HasOwnerCredential(principal)))
        {
            return new HashSet<Guid>();
        }
        var query = CentralTransientEventReadService.ApplyAccess(
            dbContext,
            dbContext.CentralTransientEventCurrent.AsNoTracking(),
            isAdmin,
            ownerId,
            CentralArtifactCredentialAccess.GetObservatoryScope(principal));
        if (!isAdmin)
        {
            var managedObservatories = ObservatoryMembershipAccess.ForManager(dbContext, ownerId ?? string.Empty)
                .Select(membership => membership.ObservatoryId);
            query = query.Where(current =>
                !dbContext.CentralTransientObservationSources.Any(source =>
                    source.Observation!.CentralTransientEventId == current.CentralTransientEventId
                    && !managedObservatories.Contains(source.Artifact!.Frame!.ObservatoryId))
                && !dbContext.CentralTransientObservationBackgrounds.Any(background =>
                    background.Observation!.CentralTransientEventId == current.CentralTransientEventId
                    && !managedObservatories.Contains(background.Artifact!.Frame!.ObservatoryId)));
        }
        return (await query.Where(item => centralTransientEventIds.Contains(item.CentralTransientEventId))
            .Select(item => item.CentralTransientEventId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
    }

    public async Task<CentralTransientReviewMutationResult> ReviewAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        byte[] expectedRowVersion,
        string idempotencyKey,
        CentralTransientReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var started = timeProvider.GetTimestamp();
        using var activity = telemetry?.Start("review");
        var actor = CentralArtifactCredentialAccess.GetSubject(principal);
        var isAdmin = CentralArtifactCredentialAccess.HasScope(principal, "api.admin");
        if (CentralArtifactCredentialAccess.IsSystem(principal) || string.IsNullOrWhiteSpace(actor) ||
            idempotencyKey.Length is < 1 or > 128 || expectedRowVersion.Length != 8 ||
            request.AssessmentId == Guid.Empty || request.ReasonCodes is null ||
            request.Disposition is not (TransientReviewDisposition.Confirmed or
                TransientReviewDisposition.Overridden or TransientReviewDisposition.Rejected) ||
            (request.Disposition == TransientReviewDisposition.Overridden) != (request.Override is not null))
        {
            telemetry?.RecordReview("invalid", timeProvider.GetElapsedTime(started));
            return new(CentralTransientReviewMutationStatus.Invalid);
        }
        var requestJson = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(request)).GetRawText();
        var requestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var acquired = await dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientEvents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {centralTransientEventId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (acquired != 1 || !await CanAccessAsync(principal, centralTransientEventId, isAdmin, cancellationToken)
                .ConfigureAwait(false))
        {
            return new(CentralTransientReviewMutationStatus.NotFound);
        }

        var replay = await dbContext.CentralTransientReviewMutations.AsNoTracking().SingleOrDefaultAsync(item =>
            item.CentralTransientEventId == centralTransientEventId && item.ActorIdentity == actor &&
            item.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (!string.Equals(replay.CanonicalRequestSha256, requestSha256, StringComparison.Ordinal))
            {
                telemetry?.RecordReview("conflict", timeProvider.GetElapsedTime(started));
                return new(CentralTransientReviewMutationStatus.IdempotencyConflict);
            }
            var replayCurrent = await LoadMutationResponseAsync(replay, true, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry?.RecordReview("replayed", timeProvider.GetElapsedTime(started));
            return new(CentralTransientReviewMutationStatus.Applied, replayCurrent);
        }

        if (await dbContext.CentralTransientPayloadReleases.AsNoTracking().AnyAsync(
                item => item.CentralTransientEventId == centralTransientEventId, cancellationToken)
            .ConfigureAwait(false))
        {
            telemetry?.RecordReview("invalid", timeProvider.GetElapsedTime(started));
            return new(CentralTransientReviewMutationStatus.Invalid);
        }

        var current = await dbContext.CentralTransientEventCurrent.SingleAsync(
            item => item.CentralTransientEventId == centralTransientEventId, cancellationToken).ConfigureAwait(false);
        if (!current.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            telemetry?.RecordReview("conflict", timeProvider.GetElapsedTime(started));
            return new(CentralTransientReviewMutationStatus.PreconditionFailed);
        }
        if (current.ActiveAssessmentId != request.AssessmentId)
        {
            telemetry?.RecordReview("invalid", timeProvider.GetElapsedTime(started));
            return new(CentralTransientReviewMutationStatus.Invalid);
        }
        var observatoryIds = await dbContext.CentralTransientObservationSources.AsNoTracking()
            .Where(source => source.Observation!.CentralTransientEventId == centralTransientEventId)
            .Select(source => source.Artifact!.Frame!.ObservatoryId)
            .Concat(dbContext.CentralTransientObservationBackgrounds.AsNoTracking()
                .Where(background => background.Observation!.CentralTransientEventId == centralTransientEventId)
                .Select(background => background.Artifact!.Frame!.ObservatoryId))
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var recipients = await dbContext.ObservatoryMemberships.AsNoTracking()
            .Where(membership => observatoryIds.Contains(membership.ObservatoryId)
                && membership.Role == ObservatoryMembershipRole.Owner
                && membership.User!.AccountType == AccountType.User
                && membership.User.Email != null
                && membership.User.EmailConfirmed)
            .GroupBy(membership => new { membership.UserId, membership.User!.Email })
            .Where(group => group.Count() == observatoryIds.Length)
            .Select(group => group.Key.Email!)
            .OrderBy(email => email)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        var reviewId = Guid.NewGuid();
        var notificationPlans = new List<NotificationPlan>();
        var appended = await versionAppender.AppendGeneratedAsync(centralTransientEventId, previous =>
        {
            var createdUtc = timeProvider.GetUtcNow();
            if (createdUtc <= previous.VersionCreatedUtc)
            {
                createdUtc = previous.VersionCreatedUtc.AddTicks(1);
            }
            var predecessor = previous.Reviews.LastOrDefault(item => item.AssessmentId == request.AssessmentId);
            var review = new TransientReviewV1(
                reviewId,
                createdUtc,
                actor,
                request.Disposition,
                request.AssessmentId,
                request.Override,
                request.ReasonCodes,
                predecessor?.ReviewId);
            var assessment = previous.Assessments.Single(item => item.AssessmentId == request.AssessmentId);
            var effective = CentralTransientReviewProjection.Resolve(
                assessment.Classification,
                assessment.MeteorSeverity,
                assessment.ConfidenceMillionths,
                request.Override);
            var eligible = request.Disposition is (TransientReviewDisposition.Confirmed or
                    TransientReviewDisposition.Overridden) &&
                effective.Classification == TransientClassification.Meteor &&
                effective.MeteorSeverity is TransientMeteorSeverity.Meteor or TransientMeteorSeverity.Fireball;
            if (eligible && recipients.Length > 0)
            {
                notificationPlans.AddRange(recipients.Select(recipient => new NotificationPlan(
                    new TransientNotificationV1(
                        Guid.NewGuid(), createdUtc, "email", TransientNotificationState.Pending,
                        request.AssessmentId, null, null),
                    recipient)));
            }
            else
            {
                var state = eligible ? TransientNotificationState.Failed : TransientNotificationState.Suppressed;
                var reason = eligible ? "notification.recipient-unavailable" : "notification.policy-suppressed";
                notificationPlans.Add(new NotificationPlan(new TransientNotificationV1(
                    Guid.NewGuid(), createdUtc, "email", state, request.AssessmentId, reason, null), null));
            }
            return previous with
            {
                EventVersionId = Guid.NewGuid(),
                Version = previous.Version + 1,
                PreviousEventVersionId = previous.EventVersionId,
                PreviousVersionCreatedUtc = previous.VersionCreatedUtc,
                VersionCreatedUtc = createdUtc,
                Reviews = previous.Reviews.Append(review).ToArray(),
                Notifications = previous.Notifications.Concat(notificationPlans.Select(item => item.Notification)).ToArray()
            };
        }, resetReviewState: false, cancellationToken).ConfigureAwait(false);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var resultRowVersion = appended.Current.RowVersion.ToArray();
        dbContext.CentralTransientReviewMutations.Add(new CentralTransientReviewMutationRecord
        {
            CentralTransientEventId = centralTransientEventId,
            ActorIdentity = actor,
            IdempotencyKey = idempotencyKey,
            CanonicalRequestSha256 = requestSha256,
            PreviousEventVersionId = appended.Previous!.EventVersionId,
            PreviousReviewId = appended.Previous.Reviews.LastOrDefault(item =>
                item.AssessmentId == request.AssessmentId)?.ReviewId,
            ResultEventVersionId = appended.Event.EventVersionId,
            ResultReviewId = reviewId,
            ResultRowVersion = resultRowVersion,
            RecordedAtUtc = appended.Event.VersionCreatedUtc
        });
        foreach (var plan in notificationPlans)
        {
            dbContext.CentralTransientNotificationDispatches.Add(new CentralTransientNotificationDispatch
            {
                CentralTransientEventId = centralTransientEventId,
                AssessmentId = request.AssessmentId,
                ReviewId = reviewId,
                InitialNotificationId = plan.Notification.NotificationId,
                LatestNotificationId = plan.Notification.NotificationId,
                Channel = plan.Notification.Channel,
                Recipient = plan.Recipient,
                RecipientIdentitySha256 = plan.Recipient is null
                    ? null
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        plan.Recipient.Trim().ToUpperInvariant()))),
                State = plan.Notification.State switch
                {
                    TransientNotificationState.Pending => CentralTransientNotificationDispatchState.Pending,
                    TransientNotificationState.Suppressed => CentralTransientNotificationDispatchState.Suppressed,
                    _ => CentralTransientNotificationDispatchState.Failed
                },
                CreatedUtc = plan.Notification.CreatedUtc,
                CompletedUtc = plan.Notification.State == TransientNotificationState.Pending
                    ? null
                    : plan.Notification.CreatedUtc,
                ReasonCode = plan.Notification.ReasonCode
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        telemetry?.RecordReview("applied", timeProvider.GetElapsedTime(started));
        return new(CentralTransientReviewMutationStatus.Applied, new CentralTransientReviewMutationResponse(
            reviewId,
            appended.Event.EventVersionId,
            appended.Current.ReviewState,
            appended.Current.EffectiveClassification,
            appended.Current.EffectiveMeteorSeverity,
            appended.Current.EffectiveConfidenceMillionths,
            CentralTransientEventEtag.Create(resultRowVersion),
            false));
    }

    private async Task<bool> CanAccessAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        var ownerId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (!isAdmin && (string.IsNullOrWhiteSpace(ownerId) || !CentralArtifactCredentialAccess.HasOwnerCredential(principal)))
        {
            return false;
        }
        var query = CentralTransientEventReadService.ApplyAccess(
            dbContext,
            dbContext.CentralTransientEventCurrent.AsNoTracking(),
            isAdmin,
            ownerId,
            CentralArtifactCredentialAccess.GetObservatoryScope(principal));
        if (!isAdmin)
        {
            var managedObservatories = ObservatoryMembershipAccess.ForManager(dbContext, ownerId ?? string.Empty)
                .Select(membership => membership.ObservatoryId);
            query = query.Where(current =>
                !dbContext.CentralTransientObservationSources.Any(source =>
                    source.Observation!.CentralTransientEventId == current.CentralTransientEventId
                    && !managedObservatories.Contains(source.Artifact!.Frame!.ObservatoryId))
                && !dbContext.CentralTransientObservationBackgrounds.Any(background =>
                    background.Observation!.CentralTransientEventId == current.CentralTransientEventId
                    && !managedObservatories.Contains(background.Artifact!.Frame!.ObservatoryId)));
        }
        return await query
            .AnyAsync(item => item.CentralTransientEventId == centralTransientEventId, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record NotificationPlan(TransientNotificationV1 Notification, string? Recipient);

    private async Task<CentralTransientReviewMutationResponse> LoadMutationResponseAsync(
        CentralTransientReviewMutationRecord mutation,
        bool replayed,
        CancellationToken cancellationToken)
    {
        var review = await dbContext.CentralTransientReviews.AsNoTracking().SingleAsync(
            item => item.ReviewId == mutation.ResultReviewId, cancellationToken).ConfigureAwait(false);
        var assessment = await dbContext.CentralTransientAssessments.AsNoTracking().SingleAsync(
            item => item.AssessmentId == review.AssessmentId, cancellationToken).ConfigureAwait(false);
        var state = review.Disposition switch
        {
            TransientReviewDisposition.Confirmed => CentralTransientReviewState.Reviewed,
            TransientReviewDisposition.Overridden => CentralTransientReviewState.Overridden,
            TransientReviewDisposition.Rejected => CentralTransientReviewState.Rejected,
            _ => CentralTransientReviewState.NeedsReview
        };
        var effective = CentralTransientReviewProjection.Resolve(
            assessment.Classification,
            assessment.MeteorSeverity,
            assessment.ConfidenceMillionths,
            review.OverrideClassification is { } overrideClassification
                ? new TransientReviewOverrideV1(
                    overrideClassification,
                    review.OverrideMeteorSeverity,
                    review.OverrideConfidenceMillionths!.Value)
                : null);
        return new(
            mutation.ResultReviewId,
            mutation.ResultEventVersionId,
            state,
            effective.Classification,
            effective.MeteorSeverity,
            effective.ConfidenceMillionths,
            CentralTransientEventEtag.Create(mutation.ResultRowVersion),
            replayed);
    }
}

internal static class CentralTransientEventEtag
{
    public static string Create(ReadOnlySpan<byte> rowVersion)
        => $"\"{WebEncoders.Base64UrlEncode(rowVersion)}\"";

    public static bool TryParse(string value, out byte[] rowVersion)
    {
        rowVersion = [];
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' || value.StartsWith("W/", StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            rowVersion = WebEncoders.Base64UrlDecode(value[1..^1]);
            return rowVersion.Length == 8;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
