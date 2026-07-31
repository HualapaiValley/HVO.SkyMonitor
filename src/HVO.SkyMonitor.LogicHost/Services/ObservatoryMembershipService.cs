using System.Data;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum ObservatoryMembershipMutationOutcome
{
    Applied,
    Unchanged,
    NotFoundOrDenied,
    TargetUserNotFound,
    MemberNotFound,
    LastOwner
}

internal sealed record ObservatoryMembershipMutationResult(
    ObservatoryMembershipMutationOutcome Outcome,
    ObservatoryMembershipRole? Role = null);

internal sealed record ObservatoryMembershipSummary(
    string UserId,
    string? Email,
    ObservatoryMembershipRole Role,
    DateTimeOffset AddedAtUtc);

internal sealed record ObservatoryMembershipPage(
    IReadOnlyList<ObservatoryMembershipSummary> Items,
    string? NextCursor);

internal interface IObservatoryMembershipService
{
    Task<ObservatoryMembershipRole?> GetRoleAsync(
        Guid observatoryId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<ObservatoryMembershipPage> ListAsync(
        Guid observatoryId,
        string actorUserId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<ObservatoryMembershipMutationResult> SetRoleAsync(
        Guid observatoryId,
        string actorUserId,
        string targetUserId,
        ObservatoryMembershipRole role,
        CancellationToken cancellationToken = default);

    Task<ObservatoryMembershipMutationResult> RemoveAsync(
        Guid observatoryId,
        string actorUserId,
        string targetUserId,
        CancellationToken cancellationToken = default);
}

internal sealed class ObservatoryMembershipService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<ObservatoryMembershipService>? logger = null) : IObservatoryMembershipService
{
    public async Task<ObservatoryMembershipRole?> GetRoleAsync(
        Guid observatoryId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return await dbContext.ObservatoryMemberships
            .Where(item => item.ObservatoryId == observatoryId
                && item.UserId == userId
                && item.User!.AccountType == AccountType.User)
            .Select(item => (ObservatoryMembershipRole?)item.Role)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ObservatoryMembershipPage> ListAsync(
        Guid observatoryId,
        string actorUserId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (take is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(take));
        if (!TryDecode(cursor, out MembershipCursor? cursorValue))
        {
            throw new ArgumentException("The membership cursor is invalid.", nameof(cursor));
        }
        var actorRole = await GetRoleAsync(observatoryId, actorUserId, cancellationToken).ConfigureAwait(false);
        if (actorRole != ObservatoryMembershipRole.Owner)
        {
            return new([], null);
        }

        IQueryable<ObservatoryMembership> query = dbContext.ObservatoryMemberships
            .Where(item => item.ObservatoryId == observatoryId && item.User!.AccountType == AccountType.User);
        if (cursorValue is not null)
        {
#pragma warning disable CA1309 // SQL Server performs this comparison using the indexed database collation.
            query = query.Where(item =>
                item.AddedAtUtc > cursorValue.AddedAtUtc
                || item.AddedAtUtc == cursorValue.AddedAtUtc
                && string.Compare(item.UserId, cursorValue.UserId) > 0);
#pragma warning restore CA1309
        }
        var rows = await query
            .OrderBy(item => item.AddedAtUtc)
            .ThenBy(item => item.UserId)
            .Take(take + 1)
            .Select(item => new ObservatoryMembershipSummary(
                item.UserId,
                item.User!.Email,
                item.Role,
                item.AddedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore && rows.Count > 0
            ? Encode(new MembershipCursor(rows[^1].AddedAtUtc, rows[^1].UserId))
            : null);
    }

    private static string Encode<T>(T value)
        => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value));

    private static bool TryDecode<T>(string? cursor, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            value = JsonSerializer.Deserialize<T>(WebEncoders.Base64UrlDecode(cursor));
            return value is not null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record MembershipCursor(DateTimeOffset AddedAtUtc, string UserId);

    public Task<ObservatoryMembershipMutationResult> SetRoleAsync(
        Guid observatoryId,
        string actorUserId,
        string targetUserId,
        ObservatoryMembershipRole role,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        return MutateAsync(observatoryId, actorUserId, targetUserId, role, cancellationToken);
    }

    public Task<ObservatoryMembershipMutationResult> RemoveAsync(
        Guid observatoryId,
        string actorUserId,
        string targetUserId,
        CancellationToken cancellationToken = default)
        => MutateAsync(observatoryId, actorUserId, targetUserId, null, cancellationToken);

    private async Task<ObservatoryMembershipMutationResult> MutateAsync(
        Guid observatoryId,
        string actorUserId,
        string targetUserId,
        ObservatoryMembershipRole? requestedRole,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;

        IQueryable<Observatory> observatoryQuery = isRelational
            ? dbContext.Observatories.FromSqlInterpolated($"""
                SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {observatoryId}
                """)
            : dbContext.Observatories.Where(item => item.Id == observatoryId);
        if (!await observatoryQuery.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(ObservatoryMembershipMutationOutcome.NotFoundOrDenied);
        }

        IQueryable<ApplicationUser> usersQuery = isRelational
            ? dbContext.Users.FromSqlInterpolated($"""
                SELECT * FROM [AspNetUsers] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] IN ({actorUserId}, {targetUserId})
                ORDER BY [Id]
                """)
            : dbContext.Users.Where(item => item.Id == actorUserId || item.Id == targetUserId);
        var users = await usersQuery.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!users.Any(item => item.Id == actorUserId && item.AccountType == AccountType.User))
        {
            return new(ObservatoryMembershipMutationOutcome.NotFoundOrDenied);
        }
        if (!users.Any(item => item.Id == targetUserId && item.AccountType == AccountType.User))
        {
            return new(ObservatoryMembershipMutationOutcome.TargetUserNotFound);
        }

        IQueryable<ObservatoryMembership> membershipQuery = isRelational
            ? dbContext.ObservatoryMemberships.FromSqlInterpolated($"""
                SELECT * FROM [ObservatoryMemberships] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ObservatoryId] = {observatoryId}
                """)
            : dbContext.ObservatoryMemberships.Where(item => item.ObservatoryId == observatoryId);
        var memberships = await membershipQuery.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!memberships.Any(item => item.UserId == actorUserId && item.Role == ObservatoryMembershipRole.Owner))
        {
            return new(ObservatoryMembershipMutationOutcome.NotFoundOrDenied);
        }

        var current = memberships.SingleOrDefault(item => item.UserId == targetUserId);
        var previousRole = current?.Role;
        if (!requestedRole.HasValue)
        {
            if (current is null)
            {
                return new(ObservatoryMembershipMutationOutcome.MemberNotFound);
            }
            if (current.Role == ObservatoryMembershipRole.Owner
                && memberships.Count(item => item.Role == ObservatoryMembershipRole.Owner) == 1)
            {
                return new(ObservatoryMembershipMutationOutcome.LastOwner, current.Role);
            }
            dbContext.ObservatoryMemberships.Remove(current);
        }
        else if (current?.Role == requestedRole.Value)
        {
            return new(ObservatoryMembershipMutationOutcome.Unchanged, current.Role);
        }
        else if (current?.Role == ObservatoryMembershipRole.Owner
            && memberships.Count(item => item.Role == ObservatoryMembershipRole.Owner) == 1)
        {
            return new(ObservatoryMembershipMutationOutcome.LastOwner, current.Role);
        }
        else if (current is null)
        {
            current = new ObservatoryMembership
            {
                ObservatoryId = observatoryId,
                UserId = targetUserId,
                Role = requestedRole.Value,
                AddedAtUtc = timeProvider.GetUtcNow()
            };
            dbContext.ObservatoryMemberships.Add(current);
        }
        else
        {
            current.Role = requestedRole.Value;
        }

        var action = !requestedRole.HasValue
            ? ObservatoryMembershipAuditAction.Removed
            : previousRole.HasValue
                ? ObservatoryMembershipAuditAction.RoleChanged
                : ObservatoryMembershipAuditAction.Granted;
        dbContext.ObservatoryMembershipAudits.Add(new ObservatoryMembershipAudit
        {
            ObservatoryId = observatoryId,
            TargetUserId = targetUserId,
            ActorUserId = actorUserId,
            Action = action,
            PreviousRole = action == ObservatoryMembershipAuditAction.Granted ? null : previousRole,
            NewRole = requestedRole,
            ReasonCode = action switch
            {
                ObservatoryMembershipAuditAction.Granted => "membership-granted",
                ObservatoryMembershipAuditAction.RoleChanged => "role-changed",
                _ => "membership-removed"
            },
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (logger is not null)
        {
            OperatorUiAuditLog.Membership(logger, action.ToString(), "applied", requestedRole?.ToString() ?? "none");
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(ObservatoryMembershipMutationOutcome.Applied, requestedRole);
    }
}

internal static class ObservatoryMembershipAccess
{
    internal static IQueryable<ObservatoryMembership> ForUser(
        ApplicationDbContext dbContext,
        string userId)
        => dbContext.ObservatoryMemberships.Where(membership =>
            membership.UserId == userId && membership.User!.AccountType == AccountType.User);

    internal static IQueryable<ObservatoryMembership> ForManager(
        ApplicationDbContext dbContext,
        string userId)
        => ForUser(dbContext, userId).Where(membership =>
            membership.Role == ObservatoryMembershipRole.Manager
            || membership.Role == ObservatoryMembershipRole.Owner);

    internal static IQueryable<ObservatoryMembership> ForOwner(
        ApplicationDbContext dbContext,
        string userId)
        => ForUser(dbContext, userId).Where(membership => membership.Role == ObservatoryMembershipRole.Owner);
}
