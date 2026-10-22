using System.Security.Claims;
using System.Text.Json;
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
/// kind; <see cref="LatestLifecycleFact"/> is the most recently recorded Activated or RolledBack fact, which the
/// delivery service orders monotonically. Every fact keeps its own source, occurrence and recorded UTC and reason;
/// heartbeat is not consulted.
/// </summary>
internal sealed record CameraProcessingGraphProposalSummary(
    ProcessingGraphDeliveryProposalView Proposal,
    ProcessingGraphDeliveryFactView? TerminalFact,
    ProcessingGraphDeliveryFactView? LatestFact,
    ProcessingGraphDeliveryFactView? LatestLifecycleFact,
    CameraProposalDerivedStatus? DerivedStatus);

internal sealed partial class CameraOperationsReadService
{
    internal const string DerivedStagedStatus = "Staged";
    internal const string DerivedStagedBasis =
        "derived:accepted-fact-with-no-lifecycle-fact-or-latest-lifecycle-fact-rolled-back";

    /// <summary>
    /// Proposals issued to every retained installation of the camera, newest first, through one bounded seek of
    /// (RegistrationId, LogicalCameraInstallationId, IssuedAtUtc, Id) per installation. The page's facts are then
    /// read in one query restricted to those proposals; a correlated per-proposal TOP is avoided deliberately,
    /// because EF decorrelates it into ROW_NUMBER over every stored fact.
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
        var installations = await InstallationKeysAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false);
        var history = ProposalHistory(db, installations, position?.Time, position?.Id, take + 1);
        if (history is null) return new([], null);
        var proposals = await history
            .Select(proposal => new ProcessingGraphDeliveryProposalView(
                proposal.Id, proposal.AssignmentId, proposal.RevisionId, proposal.RegistrationId,
                proposal.LogicalCameraInstallationId, proposal.InstallationPublicId,
                proposal.ExpectedActiveLocalRevisionId, proposal.CapabilitySnapshotSha256,
                proposal.IssuedAtUtc, proposal.ExpiresAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var more = Trim(proposals, take);
        var facts = proposals.Count == 0
            ? []
            : await PageFacts(db, proposals.Select(proposal => proposal.Id).ToList())
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        // Rows arrive newest first per proposal, so the first match of each kind is the latest stored one.
        var byProposal = facts.ToLookup(fact => fact.ProposalId);
        var items = proposals.Select(proposal =>
        {
            var proposalFacts = byProposal[proposal.Id].ToArray();
            var terminal = proposalFacts.FirstOrDefault(fact => IsTerminal(fact.Kind));
            var lifecycle = proposalFacts.FirstOrDefault(fact =>
                fact.Kind is nameof(ProcessingGraphDeliveryFactKind.Activated) or
                    nameof(ProcessingGraphDeliveryFactKind.RolledBack));
            // Staged is a CameraAgent poll disposition, never a stored fact: an accepted revision that is not the
            // device's active one, i.e. never activated or rolled back since. Label the derivation explicitly.
            var staged = terminal?.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) &&
                lifecycle?.Kind is null or nameof(ProcessingGraphDeliveryFactKind.RolledBack);
            return new CameraProcessingGraphProposalSummary(
                proposal, terminal, proposalFacts.FirstOrDefault(), lifecycle,
                staged ? new CameraProposalDerivedStatus(DerivedStagedStatus, DerivedStagedBasis) : null);
        }).ToArray();
        return new(items, more
            ? Encode(new(owner, observatoryId, cameraId, "proposals", Guid.Empty, "",
                proposals[^1].IssuedAtUtc, proposals[^1].Id))
            : null);
    }

    /// <summary>
    /// Every stored fact of the given proposals, newest recorded first within each proposal, through the existing
    /// (ProposalId, RecordedAtUtc, Id) index. Rows read are the page's own facts, never the whole fact history.
    /// On SQL Server the seek is forced: on a compact fact table the optimizer otherwise prefers a clustered scan
    /// whose rows read grow with every proposal in the network.
    /// </summary>
    internal static IQueryable<ProcessingGraphDeliveryFactView> PageFacts(ApplicationDbContext db, List<Guid> proposalIds)
    {
        var facts = db.Database.IsSqlServer()
            ? db.CentralProcessingGraphDeliveryFacts.FromSqlInterpolated($"""
                SELECT fact.*
                FROM OPENJSON({JsonSerializer.Serialize(proposalIds)}) WITH ([Id] uniqueidentifier '$') AS page
                INNER JOIN [CentralProcessingGraphDeliveryFacts] AS fact
                    WITH (FORCESEEK([IX_CentralProcessingGraphDeliveryFacts_ProposalId_RecordedAtUtc_Id]([ProposalId])))
                    ON fact.[ProposalId] = page.[Id]
                """).AsNoTracking()
            : db.CentralProcessingGraphDeliveryFacts.AsNoTracking().Where(fact => proposalIds.Contains(fact.ProposalId));
        return facts
            .OrderBy(fact => fact.ProposalId)
            .ThenByDescending(fact => fact.RecordedAtUtc)
            .ThenByDescending(fact => fact.Id)
            .Select(fact => new ProcessingGraphDeliveryFactView(
                fact.Id, fact.ProposalId, fact.Kind, fact.Source, fact.OccurredAtUtc, fact.RecordedAtUtc,
                fact.ReasonCode, fact.LocalRevisionId, fact.DefinitionIdentitySha256,
                fact.SharedPlanIdentitySha256, fact.LocalPlanIdentitySha256));
    }

    /// <summary>One proposal's facts through a forced (ProposalId, RecordedAtUtc, Id) seek on SQL Server.</summary>
    internal static IQueryable<CentralProcessingGraphDeliveryFact> ProposalFacts(ApplicationDbContext db, Guid proposalId)
        => db.Database.IsSqlServer()
            ? db.CentralProcessingGraphDeliveryFacts.FromSqlInterpolated($"""
                SELECT fact.*
                FROM [CentralProcessingGraphDeliveryFacts] AS fact
                    WITH (FORCESEEK([IX_CentralProcessingGraphDeliveryFacts_ProposalId_RecordedAtUtc_Id]([ProposalId])))
                WHERE fact.[ProposalId] = {proposalId}
                """).AsNoTracking()
            : db.CentralProcessingGraphDeliveryFacts.AsNoTracking().Where(fact => fact.ProposalId == proposalId);

    /// <summary>
    /// One retained installation's proposals through a forced (RegistrationId, LogicalCameraInstallationId,
    /// IssuedAtUtc, Id) seek on SQL Server, for the same reason as <see cref="PageFacts"/>.
    /// </summary>
    private static IQueryable<CentralProcessingGraphDeliveryProposal> InstallationProposals(
        ApplicationDbContext db, InstallationKey installation)
        => db.Database.IsSqlServer()
            ? db.CentralProcessingGraphDeliveryProposals.FromSqlInterpolated($"""
                SELECT proposal.*
                FROM [CentralProcessingGraphDeliveryProposals] AS proposal
                    WITH (FORCESEEK([IX_CentralProcessingGraphDeliveryProposals_RegistrationId_LogicalCameraInstallationId_IssuedAtUtc_Id]([RegistrationId], [LogicalCameraInstallationId])))
                WHERE proposal.[RegistrationId] = {installation.RegistrationId}
                    AND proposal.[LogicalCameraInstallationId] = {installation.Id}
                """).AsNoTracking()
            : db.CentralProcessingGraphDeliveryProposals.AsNoTracking().Where(item =>
                item.RegistrationId == installation.RegistrationId && item.LogicalCameraInstallationId == installation.Id);

    private static bool IsTerminal(string kind)
        => kind is nameof(ProcessingGraphDeliveryFactKind.Accepted) or nameof(ProcessingGraphDeliveryFactKind.Rejected) or
            nameof(ProcessingGraphDeliveryFactKind.Expired) or nameof(ProcessingGraphDeliveryFactKind.Superseded);

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
        var facts = ProposalFacts(db, proposalId);
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
        => PerInstallation(installations, installation =>
            {
                var proposals = InstallationProposals(db, installation)
                    .Where(item => item.InstallationPublicId == installation.InstallationPublicId);
                if (afterTime is { } time && afterId is { } id)
                    proposals = proposals.Where(item => item.IssuedAtUtc <= time &&
                        (item.IssuedAtUtc < time || item.Id.CompareTo(id) < 0));
                return proposals.OrderByDescending(item => item.IssuedAtUtc).ThenByDescending(item => item.Id).Take(limit);
            })
            ?.OrderByDescending(item => item.IssuedAtUtc).ThenByDescending(item => item.Id)
            .Take(limit);

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
