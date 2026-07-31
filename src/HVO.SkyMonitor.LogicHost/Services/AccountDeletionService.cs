using System.Data;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum AccountDeletionOutcome
{
    Deleted,
    LastOwner,
    Failed
}

internal sealed record AccountDeletionResult(
    AccountDeletionOutcome Outcome,
    IdentityResult? IdentityResult = null);

internal interface IAccountDeletionService
{
    Task<AccountDeletionResult> DeleteAsync(
        ApplicationUser user,
        CancellationToken cancellationToken = default);
}

internal sealed class AccountDeletionService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider) : IAccountDeletionService
{
    public async Task<AccountDeletionResult> DeleteAsync(
        ApplicationUser user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;

        var observatories = isRelational
            ? await dbContext.Observatories.FromSqlInterpolated($"""
                SELECT observatory.*
                FROM [Observatories] AS observatory WITH (UPDLOCK, HOLDLOCK)
                WHERE EXISTS (
                    SELECT 1
                    FROM [ObservatoryMemberships] AS target
                    WHERE target.[UserId] = {user.Id}
                      AND target.[ObservatoryId] = observatory.[Id])
                ORDER BY observatory.[Id]
                """).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await dbContext.Observatories
                .Where(observatory => dbContext.ObservatoryMemberships.Any(target =>
                    target.UserId == user.Id && target.ObservatoryId == observatory.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var memberships = isRelational
            ? await dbContext.ObservatoryMemberships.FromSqlInterpolated($"""
                SELECT membership.*
                FROM [ObservatoryMemberships] AS membership WITH (UPDLOCK, HOLDLOCK)
                WHERE EXISTS (
                    SELECT 1
                    FROM [ObservatoryMemberships] AS target WITH (UPDLOCK, HOLDLOCK)
                    WHERE target.[UserId] = {user.Id}
                      AND target.[ObservatoryId] = membership.[ObservatoryId])
                ORDER BY membership.[ObservatoryId], membership.[UserId]
                """).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await dbContext.ObservatoryMemberships
                .Where(membership => dbContext.ObservatoryMemberships.Any(target =>
                    target.UserId == user.Id && target.ObservatoryId == membership.ObservatoryId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var targets = memberships.Where(membership => membership.UserId == user.Id).ToArray();
        if (targets.Any(target => target.Role == ObservatoryMembershipRole.Owner
            && observatories.Any(observatory => observatory.Id == target.ObservatoryId && observatory.IsActive)
            && memberships.Count(membership => membership.ObservatoryId == target.ObservatoryId
                && membership.Role == ObservatoryMembershipRole.Owner) == 1))
        {
            return new(AccountDeletionOutcome.LastOwner);
        }

        var occurredAtUtc = timeProvider.GetUtcNow();
        foreach (var membership in targets)
        {
            dbContext.ObservatoryMembershipAudits.Add(new ObservatoryMembershipAudit
            {
                ObservatoryId = membership.ObservatoryId,
                TargetUserId = user.Id,
                ActorUserId = user.Id,
                Action = ObservatoryMembershipAuditAction.Removed,
                PreviousRole = membership.Role,
                ReasonCode = "account-deleted",
                OccurredAtUtc = occurredAtUtc
            });
        }
        dbContext.ObservatoryMemberships.RemoveRange(targets);

        var identityResult = await userManager.DeleteAsync(user).ConfigureAwait(false);
        if (!identityResult.Succeeded)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            dbContext.ChangeTracker.Clear();
            return new(AccountDeletionOutcome.Failed, identityResult);
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(AccountDeletionOutcome.Deleted, identityResult);
    }
}
