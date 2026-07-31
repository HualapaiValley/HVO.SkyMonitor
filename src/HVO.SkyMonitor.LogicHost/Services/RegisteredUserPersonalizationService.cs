using System.Data;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum PersonalizationMutationOutcome { Applied, Unchanged, NotFoundOrDenied, Invalid }
internal sealed record RegisteredUserPreference(bool InAppEnabled, bool EmailEnabled);
internal sealed record RegisteredUserNotificationSummary(Guid Id, RegisteredUserNotificationKind Kind, Guid PublicRecordId, string Title, DateTimeOffset CreatedUtc, bool IsRead);
internal sealed record FollowedObservatorySummary(string Slug, string DisplayName, DateTimeOffset FollowedUtc);

internal interface IRegisteredUserPersonalizationService
{
    Task<PersonalizationMutationOutcome> FollowAsync(string userId, string publicSlug, CancellationToken cancellationToken = default);
    Task<PersonalizationMutationOutcome> UnfollowAsync(string userId, string publicSlug, CancellationToken cancellationToken = default);
    Task<bool> IsFollowingAsync(string userId, string publicSlug, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FollowedObservatorySummary>> ListFollowedObservatoriesAsync(string userId, int take, CancellationToken cancellationToken = default);
    Task<PersonalizationMutationOutcome> BookmarkAsync(string userId, Guid publicEventId, CancellationToken cancellationToken = default);
    Task<PersonalizationMutationOutcome> UnbookmarkAsync(string userId, Guid publicEventId, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<Guid>> ListBookmarkPublicIdsAsync(string userId, CancellationToken cancellationToken = default);
    Task<PersonalizationMutationOutcome> SetVerifiedEventSubscriptionAsync(string userId, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> HasVerifiedEventSubscriptionAsync(string userId, CancellationToken cancellationToken = default);
    Task<RegisteredUserPreference> GetPreferenceAsync(string userId, CancellationToken cancellationToken = default);
    Task<PersonalizationMutationOutcome> SetPreferenceAsync(string userId, RegisteredUserPreference preference, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegisteredUserNotificationSummary>> ListNotificationsAsync(string userId, int take, CancellationToken cancellationToken = default);
    Task<PersonalizationMutationOutcome> MarkNotificationReadAsync(string userId, Guid notificationId, CancellationToken cancellationToken = default);
}

internal sealed class RegisteredUserPersonalizationService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<RegisteredUserPersonalizationService>? logger = null)
    : IRegisteredUserPersonalizationService
{
    public async Task<PersonalizationMutationOutcome> FollowAsync(string userId, string publicSlug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(publicSlug)) return PersonalizationMutationOutcome.Invalid;
        await using var transaction = await BeginMutationAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsHumanLockedAsync(userId, cancellationToken).ConfigureAwait(false)) return PersonalizationMutationOutcome.NotFoundOrDenied;
        var normalizedSlug = publicSlug.Trim().ToLowerInvariant();
        var observatoryId = await dbContext.ObservatoryPublicationProfileVersions.AsNoTracking()
            .Where(item => item.PublicSlug == normalizedSlug && item.SupersededAtUtc == null
                && item.ProfileVisibility == ObservatoryProfileVisibility.Public && item.Observatory!.IsActive)
            .Select(item => (Guid?)item.ObservatoryId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (observatoryId is null) return PersonalizationMutationOutcome.NotFoundOrDenied;
        if (await dbContext.RegisteredUserObservatoryFollows.AnyAsync(item => item.UserId == userId && item.ObservatoryId == observatoryId, cancellationToken).ConfigureAwait(false)) return PersonalizationMutationOutcome.Unchanged;
        dbContext.RegisteredUserObservatoryFollows.Add(new RegisteredUserObservatoryFollow { UserId = userId, ObservatoryId = observatoryId.Value, CreatedUtc = timeProvider.GetUtcNow() });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (logger is not null) OperatorUiAuditLog.PersonalPreference(logger, "follow", "applied");
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PersonalizationMutationOutcome.Applied;
    }

    public async Task<PersonalizationMutationOutcome> UnfollowAsync(string userId, string publicSlug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(publicSlug)) return PersonalizationMutationOutcome.Invalid;
        await using var transaction = await BeginMutationAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsHumanLockedAsync(userId, cancellationToken).ConfigureAwait(false)) return PersonalizationMutationOutcome.NotFoundOrDenied;
        var normalizedSlug = publicSlug.Trim().ToLowerInvariant();
        var observatoryId = await dbContext.ObservatoryPublicationProfileVersions.AsNoTracking()
            .Where(item => item.PublicSlug == normalizedSlug && item.SupersededAtUtc == null)
            .Select(item => (Guid?)item.ObservatoryId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (observatoryId is null) return PersonalizationMutationOutcome.Unchanged;
        var row = await dbContext.RegisteredUserObservatoryFollows.SingleOrDefaultAsync(
            item => item.UserId == userId && item.ObservatoryId == observatoryId.Value,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return PersonalizationMutationOutcome.Unchanged;
        dbContext.RegisteredUserObservatoryFollows.Remove(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PersonalizationMutationOutcome.Applied;
    }

    public async Task<bool> IsFollowingAsync(string userId, string publicSlug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(publicSlug)) return false;
        var normalizedSlug = publicSlug.Trim().ToLowerInvariant();
        return await dbContext.RegisteredUserObservatoryFollows.AsNoTracking().AnyAsync(follow =>
            follow.UserId == userId
            && dbContext.ObservatoryPublicationProfileVersions.Any(profile =>
                profile.ObservatoryId == follow.ObservatoryId
                && profile.PublicSlug == normalizedSlug
                && profile.SupersededAtUtc == null), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FollowedObservatorySummary>> ListFollowedObservatoriesAsync(
        string userId,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        return await dbContext.RegisteredUserObservatoryFollows.AsNoTracking()
            .Where(follow => follow.UserId == userId)
            .Join(dbContext.ObservatoryPublicationProfileVersions.AsNoTracking().Where(profile =>
                    profile.SupersededAtUtc == null
                    && profile.ProfileVisibility == ObservatoryProfileVisibility.Public
                    && profile.Observatory!.IsActive),
                follow => follow.ObservatoryId,
                profile => profile.ObservatoryId,
                (follow, profile) => new { Follow = follow, Profile = profile })
            .OrderByDescending(item => item.Follow.CreatedUtc)
            .ThenBy(item => item.Profile.PublicSlug)
            .Take(take)
            .Select(item => new FollowedObservatorySummary(
                item.Profile.PublicSlug,
                item.Profile.PublicDisplayName,
                item.Follow.CreatedUtc))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersonalizationMutationOutcome> BookmarkAsync(string userId, Guid publicEventId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsHumanLockedAsync(userId, cancellationToken).ConfigureAwait(false)) return PersonalizationMutationOutcome.NotFoundOrDenied;
        var eventId = await dbContext.PublicRecordPublicationDecisions.AsNoTracking().Where(decision => decision.PublicId == publicEventId
                && decision.SubjectKind == PublicRecordSubjectKind.TransientEvent && decision.State == PublicationDecisionState.Released
                && !dbContext.PublicRecordPublicationDecisions.Any(successor => successor.SupersedesDecisionId == decision.Id))
            .Select(decision => decision.CentralTransientEventId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (eventId is null) return PersonalizationMutationOutcome.NotFoundOrDenied;
        if (await dbContext.RegisteredUserTransientEventBookmarks.AnyAsync(item => item.UserId == userId && item.CentralTransientEventId == eventId, cancellationToken).ConfigureAwait(false)) return PersonalizationMutationOutcome.Unchanged;
        dbContext.RegisteredUserTransientEventBookmarks.Add(new RegisteredUserTransientEventBookmark { UserId = userId, CentralTransientEventId = eventId.Value, CreatedUtc = timeProvider.GetUtcNow() });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PersonalizationMutationOutcome.Applied;
    }

    public async Task<PersonalizationMutationOutcome> UnbookmarkAsync(string userId, Guid publicEventId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsHumanLockedAsync(userId, cancellationToken).ConfigureAwait(false)) return PersonalizationMutationOutcome.NotFoundOrDenied;
        var eventId = await dbContext.PublicRecordPublicationDecisions.AsNoTracking()
            .Where(decision => decision.PublicId == publicEventId
                && decision.SubjectKind == PublicRecordSubjectKind.TransientEvent)
            .Select(decision => decision.CentralTransientEventId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (eventId is null) return PersonalizationMutationOutcome.Unchanged;
        var row = await dbContext.RegisteredUserTransientEventBookmarks.SingleOrDefaultAsync(
            item => item.UserId == userId && item.CentralTransientEventId == eventId.Value,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return PersonalizationMutationOutcome.Unchanged;
        dbContext.RegisteredUserTransientEventBookmarks.Remove(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PersonalizationMutationOutcome.Applied;
    }

    public async Task<IReadOnlySet<Guid>> ListBookmarkPublicIdsAsync(string userId, CancellationToken cancellationToken = default)
        => (await dbContext.RegisteredUserTransientEventBookmarks.AsNoTracking()
            .Where(bookmark => bookmark.UserId == userId)
            .Join(dbContext.PublicRecordPublicationDecisions.AsNoTracking().Where(decision =>
                    decision.SubjectKind == PublicRecordSubjectKind.TransientEvent
                    && decision.State == PublicationDecisionState.Released
                    && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                        successor.SupersedesDecisionId == decision.Id)),
                bookmark => bookmark.CentralTransientEventId,
                decision => decision.CentralTransientEventId,
                (_, decision) => decision.PublicId)
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

    public async Task<PersonalizationMutationOutcome> SetVerifiedEventSubscriptionAsync(string userId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsHumanLockedAsync(userId, cancellationToken).ConfigureAwait(false)) return PersonalizationMutationOutcome.NotFoundOrDenied;
        var row = await dbContext.RegisteredUserSubscriptions.SingleOrDefaultAsync(item => item.UserId == userId && item.Kind == RegisteredUserSubscriptionKind.VerifiedEvent, cancellationToken).ConfigureAwait(false);
        if (enabled == (row is not null)) return PersonalizationMutationOutcome.Unchanged;
        if (enabled) dbContext.RegisteredUserSubscriptions.Add(new RegisteredUserSubscription { UserId = userId, Kind = RegisteredUserSubscriptionKind.VerifiedEvent, CreatedUtc = timeProvider.GetUtcNow() });
        else dbContext.RegisteredUserSubscriptions.Remove(row!);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PersonalizationMutationOutcome.Applied;
    }

    public Task<bool> HasVerifiedEventSubscriptionAsync(string userId, CancellationToken cancellationToken = default)
        => dbContext.RegisteredUserSubscriptions.AsNoTracking().AnyAsync(
            item => item.UserId == userId && item.Kind == RegisteredUserSubscriptionKind.VerifiedEvent,
            cancellationToken);

    public async Task<RegisteredUserPreference> GetPreferenceAsync(string userId, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.RegisteredUserNotificationPreferences.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken).ConfigureAwait(false);
        return row is null ? new(true, false) : new(row.InAppEnabled, row.EmailEnabled);
    }

    public async Task<PersonalizationMutationOutcome> SetPreferenceAsync(string userId, RegisteredUserPreference preference, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsHumanLockedAsync(userId, cancellationToken).ConfigureAwait(false)) return PersonalizationMutationOutcome.NotFoundOrDenied;
        var row = await dbContext.RegisteredUserNotificationPreferences.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (row is not null && row.InAppEnabled == preference.InAppEnabled && row.EmailEnabled == preference.EmailEnabled) return PersonalizationMutationOutcome.Unchanged;
        if (row is null)
        {
            row = new RegisteredUserNotificationPreference { UserId = userId };
            dbContext.RegisteredUserNotificationPreferences.Add(row);
        }
        row.InAppEnabled = preference.InAppEnabled;
        row.EmailEnabled = preference.EmailEnabled;
        row.UpdatedUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PersonalizationMutationOutcome.Applied;
    }

    public async Task<IReadOnlyList<RegisteredUserNotificationSummary>> ListNotificationsAsync(string userId, int take, CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        return await dbContext.RegisteredUserNotifications.AsNoTracking().Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).Take(take)
            .Select(item => new RegisteredUserNotificationSummary(item.Id, item.Kind, item.PublicRecordId, item.Title, item.CreatedUtc, item.ReadUtc != null))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersonalizationMutationOutcome> MarkNotificationReadAsync(string userId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsHumanLockedAsync(userId, cancellationToken).ConfigureAwait(false)) return PersonalizationMutationOutcome.NotFoundOrDenied;
        var row = await dbContext.RegisteredUserNotifications.SingleOrDefaultAsync(
            item => item.Id == notificationId && item.UserId == userId,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return PersonalizationMutationOutcome.NotFoundOrDenied;
        if (row.ReadUtc is not null) return PersonalizationMutationOutcome.Unchanged;
        row.ReadUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PersonalizationMutationOutcome.Applied;
    }

    private async Task<IDbContextTransaction?> BeginMutationAsync(CancellationToken cancellationToken)
        => dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;

    private Task<bool> IsHumanLockedAsync(string userId, CancellationToken cancellationToken)
        => (dbContext.Database.IsRelational()
                ? dbContext.Users.FromSqlInterpolated($"""
                    SELECT * FROM [AspNetUsers] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {userId}
                    """)
                : dbContext.Users.Where(item => item.Id == userId))
            .AnyAsync(item => item.AccountType == AccountType.User, cancellationToken);
}
