using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>
/// A status derived from persisted facts, not a persisted fact itself. It carries no timestamp: the time at which
/// a device staged a revision is not recorded centrally, and none is manufactured here.
/// </summary>
internal sealed record CameraProposalDerivedStatus(string Status, string Basis);

/// <summary>
/// One retained proposal with its persisted lifecycle summary. <see cref="TerminalFact"/> is the single stored
/// Accepted, Rejected, Expired or Superseded fact; <see cref="LatestFact"/> is the most recently recorded fact of any
/// kind. Every fact keeps its own source, occurrence and recorded UTC and reason; heartbeat is not consulted.
/// </summary>
internal sealed record CameraProcessingGraphProposalSummary(
    ProcessingGraphDeliveryProposalView Proposal,
    ProcessingGraphDeliveryFactView? TerminalFact,
    ProcessingGraphDeliveryFactView? LatestFact,
    bool HasActivatedFact,
    bool HasRolledBackFact,
    CameraProposalDerivedStatus? DerivedStatus);

internal sealed partial class CameraOperationsReadService
{
    internal const string DerivedStagedStatus = "Staged";
    internal const string DerivedStagedBasis = "derived:accepted-fact-without-activated-or-rolled-back-fact";

    /// <summary>
    /// Proposals issued to every retained installation of the camera, newest first, through one bounded seek of
    /// (RegistrationId, LogicalCameraInstallationId, IssuedAtUtc, Id) per installation.
    /// </summary>
    public async Task<CameraOperationsPage<CameraProcessingGraphProposalSummary>> ListProposalsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, int take, string? cursor,
        CancellationToken cancellationToken = default)
    {
        ValidateTake(take);
        var owner = Owner(principal, observatoryId);
        if (owner is null || !await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        var position = Decode(cursor, owner, observatoryId, cameraId, "proposals", Guid.Empty);
        var facts = db.CentralProcessingGraphDeliveryFacts.AsNoTracking();
        var installations = await InstallationKeysAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false);
        var history = ProposalHistory(db, installations, position?.Time, position?.Id, take + 1);
        if (history is null) return new([], null);
        var rows = await history
            .Select(proposal => new
            {
                Proposal = new ProcessingGraphDeliveryProposalView(
                    proposal.Id, proposal.AssignmentId, proposal.RevisionId, proposal.RegistrationId,
                    proposal.LogicalCameraInstallationId, proposal.InstallationPublicId,
                    proposal.ExpectedActiveLocalRevisionId, proposal.CapabilitySnapshotSha256,
                    proposal.IssuedAtUtc, proposal.ExpiresAtUtc),
                // The filtered unique index admits at most one terminal fact per proposal.
                TerminalFact = facts
                    .Where(fact => fact.ProposalId == proposal.Id &&
                        (fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) ||
                         fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Rejected) ||
                         fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Expired) ||
                         fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded)))
                    .OrderByDescending(fact => fact.RecordedAtUtc).ThenByDescending(fact => fact.Id)
                    .Select(fact => new ProcessingGraphDeliveryFactView(
                        fact.Id, fact.ProposalId, fact.Kind, fact.Source, fact.OccurredAtUtc, fact.RecordedAtUtc,
                        fact.ReasonCode, fact.LocalRevisionId, fact.DefinitionIdentitySha256,
                        fact.SharedPlanIdentitySha256, fact.LocalPlanIdentitySha256))
                    .FirstOrDefault(),
                LatestFact = facts
                    .Where(fact => fact.ProposalId == proposal.Id)
                    .OrderByDescending(fact => fact.RecordedAtUtc).ThenByDescending(fact => fact.Id)
                    .Select(fact => new ProcessingGraphDeliveryFactView(
                        fact.Id, fact.ProposalId, fact.Kind, fact.Source, fact.OccurredAtUtc, fact.RecordedAtUtc,
                        fact.ReasonCode, fact.LocalRevisionId, fact.DefinitionIdentitySha256,
                        fact.SharedPlanIdentitySha256, fact.LocalPlanIdentitySha256))
                    .FirstOrDefault(),
                HasActivatedFact = facts.Any(fact => fact.ProposalId == proposal.Id &&
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Activated)),
                HasRolledBackFact = facts.Any(fact => fact.ProposalId == proposal.Id &&
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.RolledBack))
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        var more = Trim(rows, take);
        var items = rows.Select(row => new CameraProcessingGraphProposalSummary(
            row.Proposal,
            row.TerminalFact,
            row.LatestFact,
            row.HasActivatedFact,
            row.HasRolledBackFact,
            // Staged is a CameraAgent poll disposition, never a stored fact: accepted, then neither activated nor
            // rolled back. Label the derivation rather than presenting it as delivery history.
            row.TerminalFact?.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) &&
                !row.HasActivatedFact && !row.HasRolledBackFact
                ? new CameraProposalDerivedStatus(DerivedStagedStatus, DerivedStagedBasis)
                : null)).ToArray();
        return new(items, more
            ? Encode(new(owner, observatoryId, cameraId, "proposals", Guid.Empty, "",
                rows[^1].Proposal.IssuedAtUtc, rows[^1].Proposal.Id))
            : null);
    }

    /// <summary>
    /// Every persisted fact of one retained proposal of the camera, newest recorded first, continuing through the
    /// existing (ProposalId, RecordedAtUtc, Id) index beyond the single-proposal read's first page.
    /// </summary>
    public async Task<CameraOperationsPage<ProcessingGraphDeliveryFactView>?> ListProposalFactsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid proposalId, int take, string? cursor,
        CancellationToken cancellationToken = default)
    {
        ValidateTake(take);
        var owner = Owner(principal, observatoryId);
        if (owner is null) return null;
        var proposal = CameraProposals(owner, observatoryId, cameraId).Where(item => item.Id == proposalId);
        if (!await proposal.AnyAsync(cancellationToken).ConfigureAwait(false)) return null;
        var position = Decode(cursor, owner, observatoryId, cameraId, "facts", proposalId);
        var facts = db.CentralProcessingGraphDeliveryFacts.AsNoTracking().Where(fact => fact.ProposalId == proposalId);
        if (position is not null)
        {
            var time = position.Time;
            var id = position.Id;
            facts = facts.Where(fact => fact.RecordedAtUtc <= time && (fact.RecordedAtUtc < time || fact.Id.CompareTo(id) < 0));
        }
        var rows = await facts.OrderByDescending(fact => fact.RecordedAtUtc).ThenByDescending(fact => fact.Id)
            .Take(take + 1)
            .Select(fact => new ProcessingGraphDeliveryFactView(
                fact.Id, fact.ProposalId, fact.Kind, fact.Source, fact.OccurredAtUtc, fact.RecordedAtUtc,
                fact.ReasonCode, fact.LocalRevisionId, fact.DefinitionIdentitySha256,
                fact.SharedPlanIdentitySha256, fact.LocalPlanIdentitySha256))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!await proposal.AnyAsync(cancellationToken).ConfigureAwait(false)) return null;
        var more = Trim(rows, take);
        return new(rows, more
            ? Encode(new(owner, observatoryId, cameraId, "facts", proposalId, "", rows[^1].RecordedAtUtc, rows[^1].Id))
            : null);
    }

    /// <summary>
    /// Proposals of the given retained installations, newest first: one bounded TOP seek of
    /// (RegistrationId, LogicalCameraInstallationId, IssuedAtUtc, Id) per installation, merged by UNION ALL.
    /// </summary>
    internal static IQueryable<CentralProcessingGraphDeliveryProposal>? ProposalHistory(
        ApplicationDbContext db, IReadOnlyList<InstallationKey> installations,
        DateTimeOffset? afterTime, Guid? afterId, int limit)
    {
        var proposals = db.CentralProcessingGraphDeliveryProposals.AsNoTracking();
        if (afterTime is { } time && afterId is { } id)
            proposals = proposals.Where(item => item.IssuedAtUtc <= time &&
                (item.IssuedAtUtc < time || item.Id.CompareTo(id) < 0));
        return PerInstallation(installations, installation => proposals
                .Where(item => item.RegistrationId == installation.RegistrationId &&
                    item.LogicalCameraInstallationId == installation.Id &&
                    item.InstallationPublicId == installation.InstallationPublicId)
                .OrderByDescending(item => item.IssuedAtUtc).ThenByDescending(item => item.Id)
                .Take(limit))
            ?.OrderByDescending(item => item.IssuedAtUtc).ThenByDescending(item => item.Id)
            .Take(limit);
    }

    /// <summary>The retained installation relationship, not current registration or assignment, authorizes a proposal.</summary>
    private IQueryable<CentralProcessingGraphDeliveryProposal> CameraProposals(string owner, Guid observatoryId, Guid cameraId)
    {
        var memberships = ObservatoryMembershipAccess.ForUser(db, owner);
        return db.CentralProcessingGraphDeliveryProposals.AsNoTracking().Where(proposal =>
            proposal.RegistrationId == proposal.LogicalCameraInstallation!.RegistrationId &&
            proposal.InstallationPublicId == proposal.LogicalCameraInstallation.InstallationPublicId &&
            proposal.LogicalCameraInstallation.LogicalCameraId == cameraId &&
            proposal.LogicalCameraInstallation.LogicalCamera!.ObservatoryId == observatoryId &&
            memberships.Any(membership => membership.ObservatoryId == observatoryId));
    }
}
