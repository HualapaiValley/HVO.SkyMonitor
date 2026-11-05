using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record ProcessingGraphDeliveryProposalView(
    Guid Id,
    Guid AssignmentId,
    Guid RevisionId,
    Guid RegistrationId,
    Guid LogicalCameraInstallationId,
    Guid InstallationPublicId,
    string? ExpectedActiveLocalRevisionId,
    string CapabilitySnapshotSha256,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed record ProcessingGraphDeliveryFactView(
    Guid Id,
    Guid ProposalId,
    string Kind,
    string Source,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string? ReasonCode,
    string? LocalRevisionId,
    string? DefinitionIdentitySha256,
    string? SharedPlanIdentitySha256,
    string? LocalPlanIdentitySha256);

internal sealed record ProcessingGraphProposalReadResult(
    ProcessingGraphDeliveryProposalView Proposal,
    IReadOnlyList<ProcessingGraphDeliveryFactView> Facts,
    bool FactsAreTruncated);

internal interface IProcessingGraphProposalReadService
{
    Task<ProcessingGraphProposalReadResult?> GetAsync(
        ClaimsPrincipal principal,
        Guid observatoryId,
        Guid logicalCameraId,
        Guid installationId,
        Guid proposalId,
        int take = 50,
        CancellationToken cancellationToken = default);
}

internal sealed class ProcessingGraphProposalReadService(ApplicationDbContext dbContext)
    : IProcessingGraphProposalReadService
{
    internal const int MaximumFacts = 50;

    public async Task<ProcessingGraphProposalReadResult?> GetAsync(
        ClaimsPrincipal principal,
        Guid observatoryId,
        Guid logicalCameraId,
        Guid installationId,
        Guid proposalId,
        int take = MaximumFacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, MaximumFacts);
        cancellationToken.ThrowIfCancellationRequested();
        var actorId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (!CentralArtifactCredentialAccess.HasOwnerCredential(principal) ||
            string.IsNullOrWhiteSpace(actorId) ||
            !CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, observatoryId))
        {
            return null;
        }

        var memberships = ObservatoryMembershipAccess.ForUser(dbContext, actorId);
        // InstallationId is the retained CENTRAL row identity. Neither retirement nor a replacement
        // changes its camera relationship; current registration/assignment/catalog authority is irrelevant.
        var authorized = dbContext.CentralProcessingGraphDeliveryProposals.AsNoTracking()
            .Where(proposal => proposal.Id == proposalId &&
                proposal.LogicalCameraInstallationId == installationId &&
                proposal.RegistrationId == proposal.LogicalCameraInstallation!.RegistrationId &&
                proposal.InstallationPublicId == proposal.LogicalCameraInstallation.InstallationPublicId &&
                proposal.LogicalCameraInstallation!.LogicalCameraId == logicalCameraId &&
                proposal.LogicalCameraInstallation.LogicalCamera!.ObservatoryId == observatoryId &&
                memberships.Any(membership => membership.ObservatoryId == observatoryId));
        var view = await authorized.TagWith("proposal-read:proposal")
            .Select(proposal => new ProcessingGraphDeliveryProposalView(
                proposal.Id, proposal.AssignmentId, proposal.RevisionId, proposal.RegistrationId,
                proposal.LogicalCameraInstallationId, proposal.InstallationPublicId,
                proposal.ExpectedActiveLocalRevisionId, proposal.CapabilitySnapshotSha256,
                proposal.IssuedAtUtc, proposal.ExpiresAtUtc))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            return null;
        }

        // Materialize the covered pagination keys before reading payload columns. Keeping TOP above
        // noncovering key lookups lets SQL Server's ordered prefetch read beyond the sentinel.
        var selectedIds = await dbContext.CentralProcessingGraphDeliveryFacts.AsNoTracking()
            .TagWith("proposal-read:fact-keys")
            .Where(fact => fact.ProposalId == proposalId)
            .OrderByDescending(fact => fact.RecordedAtUtc).ThenByDescending(fact => fact.Id)
            .Take(take + 1).Select(fact => fact.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var payload = await dbContext.CentralProcessingGraphDeliveryFacts.AsNoTracking()
            .TagWith("proposal-read:fact-payload")
            .Where(fact => fact.ProposalId == proposalId && EF.Parameter(selectedIds).Contains(fact.Id))
            .Select(fact => new ProcessingGraphDeliveryFactView(
                fact.Id, fact.ProposalId, fact.Kind, fact.Source, fact.OccurredAtUtc, fact.RecordedAtUtc,
                fact.ReasonCode, fact.LocalRevisionId, fact.DefinitionIdentitySha256,
                fact.SharedPlanIdentitySha256, fact.LocalPlanIdentitySha256))
            .ToDictionaryAsync(fact => fact.Id, cancellationToken).ConfigureAwait(false);
        // A membership removal during the multi-command read must conceal the proposal as well as facts.
        if (!await authorized.TagWith("proposal-read:reauthorize").AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        // Facts are immutable. Preserve the database's key order (including SQL GUID ordering),
        // and never refill from later history if a selected payload is unexpectedly absent.
        var facts = selectedIds.Take(take).Where(payload.ContainsKey).Select(id => payload[id]).ToList();
        return new(view, facts, selectedIds.Count > take);
    }
}
