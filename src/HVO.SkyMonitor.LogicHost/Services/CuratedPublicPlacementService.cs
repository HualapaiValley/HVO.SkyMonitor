using System.Data;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum CuratedPlacementOutcome
{
    Applied,
    Unchanged,
    NotFoundOrDenied,
    Invalid
}

internal interface ICuratedPublicPlacementService
{
    Task<CuratedPlacementOutcome> DecideObservatoryAsync(string actorUserId, string slug, CuratedPlacementState state, int? displayOrder, string reasonCode, CancellationToken cancellationToken = default);
    Task<CuratedPlacementOutcome> DecideEventAsync(string actorUserId, Guid publicId, CuratedPlacementState state, int? displayOrder, string reasonCode, CancellationToken cancellationToken = default);
}

internal sealed class CuratedPublicPlacementService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider,
    ILogger<CuratedPublicPlacementService>? logger = null) : ICuratedPublicPlacementService
{
    public async Task<CuratedPlacementOutcome> DecideObservatoryAsync(string actorUserId, string slug, CuratedPlacementState state, int? displayOrder, string reasonCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var observatoryId = await dbContext.ObservatoryPublicationProfileVersions.AsNoTracking()
            .Where(item => item.PublicSlug == normalizedSlug && item.SupersededAtUtc == null
                && item.ProfileVisibility == ObservatoryProfileVisibility.Public && item.Observatory!.IsActive)
            .Select(item => (Guid?)item.ObservatoryId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return observatoryId is null
            ? CuratedPlacementOutcome.NotFoundOrDenied
            : await DecideAsync(actorUserId, CuratedPublicSurface.HomeObservatory, observatoryId, null, state, displayOrder, reasonCode, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CuratedPlacementOutcome> DecideEventAsync(string actorUserId, Guid publicId, CuratedPlacementState state, int? displayOrder, string reasonCode, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.PublicRecordPublicationDecisions.AsNoTracking().AnyAsync(decision =>
            decision.PublicId == publicId && decision.SubjectKind == PublicRecordSubjectKind.TransientEvent
            && decision.State == PublicationDecisionState.Released
            && !dbContext.PublicRecordPublicationDecisions.Any(successor => successor.SupersedesDecisionId == decision.Id), cancellationToken).ConfigureAwait(false);
        return !exists
            ? CuratedPlacementOutcome.NotFoundOrDenied
            : await DecideAsync(actorUserId, CuratedPublicSurface.HomeEvent, null, publicId, state, displayOrder, reasonCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CuratedPlacementOutcome> DecideAsync(string actorUserId, CuratedPublicSurface surface, Guid? observatoryId, Guid? publicRecordId, CuratedPlacementState state, int? displayOrder, string reasonCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        reasonCode = reasonCode.Trim();
        if (!Enum.IsDefined(state) || reasonCode.Length is < 1 or > 128
            || (state == CuratedPlacementState.Featured ? displayOrder is < 0 or > 999 : displayOrder is not null))
        {
            return CuratedPlacementOutcome.Invalid;
        }
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        var actor = await userManager.FindByIdAsync(actorUserId);
        if (actor is null || actor.AccountType != AccountType.User
            || !await userManager.IsInRoleAsync(actor, AuthorizationRoleNames.PlatformEditor))
        {
            return CuratedPlacementOutcome.NotFoundOrDenied;
        }
        var history = await (isRelational
                ? dbContext.CuratedPublicPlacementDecisions.FromSqlInterpolated($"""
                    SELECT * FROM [CuratedPublicPlacementDecisions] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Surface] = {surface.ToString()}
                    """)
                : dbContext.CuratedPublicPlacementDecisions.Where(item => item.Surface == surface))
            .Where(item => item.ObservatoryId == observatoryId && item.PublicRecordId == publicRecordId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var current = history.SingleOrDefault(candidate => !history.Any(item => item.SupersedesDecisionId == candidate.Id));
        if (current?.State == state && current.DisplayOrder == displayOrder)
        {
            return CuratedPlacementOutcome.Unchanged;
        }
        dbContext.CuratedPublicPlacementDecisions.Add(new CuratedPublicPlacementDecision
        {
            Surface = surface,
            State = state,
            ObservatoryId = observatoryId,
            PublicRecordId = publicRecordId,
            DisplayOrder = displayOrder,
            OccurredAtUtc = timeProvider.GetUtcNow(),
            ActorUserId = actorUserId,
            ReasonCode = reasonCode,
            SupersedesDecisionId = current?.Id
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (logger is not null)
        {
            OperatorUiAuditLog.EditorialPlacement(logger, surface.ToString(), "applied", state.ToString());
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CuratedPlacementOutcome.Applied;
    }
}
