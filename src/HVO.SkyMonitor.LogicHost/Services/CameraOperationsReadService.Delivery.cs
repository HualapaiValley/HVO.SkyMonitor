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
    /// (RegistrationId, LogicalCameraInstallationId, IssuedAtUtc, Id) per installation. Each summary then needs only
    /// three stored facts per proposal, read by bounded seeks; the complete history stays behind the fact page.
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
        var ids = proposals.Select(proposal => proposal.Id).ToList();
        var summaries = ids.Count == 0
            ? new Dictionary<Guid, SummaryFacts>()
            : await SummaryFactsAsync(ids, cancellationToken).ConfigureAwait(false);
        if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        var items = proposals.Select(proposal =>
        {
            var facts = summaries.GetValueOrDefault(proposal.Id) ?? new(null, null, null);
            // Staged is a CameraAgent poll disposition, never a stored fact: an accepted revision that is not the
            // device's active one, i.e. never activated or rolled back since. Label the derivation explicitly.
            var staged = facts.Terminal?.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) &&
                facts.Lifecycle?.Kind is null or nameof(ProcessingGraphDeliveryFactKind.RolledBack);
            return new CameraProcessingGraphProposalSummary(
                proposal, facts.Terminal, facts.Latest, facts.Lifecycle,
                staged ? new CameraProposalDerivedStatus(DerivedStagedStatus, DerivedStagedBasis) : null);
        }).ToArray();
        return new(items, more
            ? Encode(new(owner, observatoryId, cameraId, "proposals", Guid.Empty, "",
                proposals[^1].IssuedAtUtc, proposals[^1].Id))
            : null);
    }

    /// <summary>
    /// The terminal, latest and latest-lifecycle facts of each given proposal. On SQL Server these are three bounded
    /// reads: the filtered unique terminal index, and a forced (ProposalId, RecordedAtUtc, Id) TOP 1 seek per
    /// proposal for the other two. The delivery service records Retrieved once at issue and the single terminal fact
    /// before any activation or rollback, so the lifecycle seek passes at most those two rows. Other providers
    /// evaluate the same rules over the proposals' facts.
    /// </summary>
    private async Task<Dictionary<Guid, SummaryFacts>> SummaryFactsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
        {
            var facts = await ProjectFacts(db.CentralProcessingGraphDeliveryFacts.AsNoTracking()
                    .Where(fact => ids.Contains(fact.ProposalId))
                    .OrderBy(fact => fact.ProposalId).ThenByDescending(fact => fact.RecordedAtUtc).ThenByDescending(fact => fact.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return facts.GroupBy(fact => fact.ProposalId).ToDictionary(group => group.Key, group => new SummaryFacts(
                group.FirstOrDefault(fact => IsTerminal(fact.Kind)),
                group.First(),
                group.FirstOrDefault(fact => IsLifecycle(fact.Kind))));
        }
        var terminal = await ProjectFacts(TerminalFacts(db, ids)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var latest = await ProjectFacts(LatestFacts(db, ids, lifecycleOnly: false)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lifecycle = await ProjectFacts(LatestFacts(db, ids, lifecycleOnly: true)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.ToDictionary(id => id, id => new SummaryFacts(
            terminal.SingleOrDefault(fact => fact.ProposalId == id),
            latest.SingleOrDefault(fact => fact.ProposalId == id),
            lifecycle.SingleOrDefault(fact => fact.ProposalId == id)));
    }

    /// <summary>The single stored terminal fact of each proposal, through the filtered unique terminal index.</summary>
    internal static IQueryable<CentralProcessingGraphDeliveryFact> TerminalFacts(ApplicationDbContext db, List<Guid> proposalIds)
        => db.CentralProcessingGraphDeliveryFacts.FromSqlInterpolated($"""
            SELECT fact.*
            FROM OPENJSON({JsonSerializer.Serialize(proposalIds)}) WITH ([Id] uniqueidentifier '$') AS page
            INNER JOIN [CentralProcessingGraphDeliveryFacts] AS fact
                WITH (FORCESEEK([IX_CentralProcessingGraphDeliveryFacts_ProposalId]([ProposalId])))
                ON fact.[ProposalId] = page.[Id]
            WHERE fact.[Kind] IN (N'Accepted', N'Rejected', N'Expired', N'Superseded')
            """).AsNoTracking();

    /// <summary>The most recently recorded fact (or Activated/RolledBack fact) of each proposal, one TOP 1 seek each.</summary>
    internal static IQueryable<CentralProcessingGraphDeliveryFact> LatestFacts(
        ApplicationDbContext db, List<Guid> proposalIds, bool lifecycleOnly)
        => lifecycleOnly
            ? db.CentralProcessingGraphDeliveryFacts.FromSqlInterpolated($"""
                SELECT fact.*
                FROM OPENJSON({JsonSerializer.Serialize(proposalIds)}) WITH ([Id] uniqueidentifier '$') AS page
                CROSS APPLY (
                    SELECT TOP (1) candidate.*
                    FROM [CentralProcessingGraphDeliveryFacts] AS candidate
                        WITH (FORCESEEK([IX_CentralProcessingGraphDeliveryFacts_ProposalId_RecordedAtUtc_Id]([ProposalId])))
                    WHERE candidate.[ProposalId] = page.[Id] AND candidate.[Kind] IN (N'Activated', N'RolledBack')
                    ORDER BY candidate.[RecordedAtUtc] DESC, candidate.[Id] DESC) AS fact
                """).AsNoTracking()
            : db.CentralProcessingGraphDeliveryFacts.FromSqlInterpolated($"""
                SELECT fact.*
                FROM OPENJSON({JsonSerializer.Serialize(proposalIds)}) WITH ([Id] uniqueidentifier '$') AS page
                CROSS APPLY (
                    SELECT TOP (1) candidate.*
                    FROM [CentralProcessingGraphDeliveryFacts] AS candidate
                        WITH (FORCESEEK([IX_CentralProcessingGraphDeliveryFacts_ProposalId_RecordedAtUtc_Id]([ProposalId])))
                    WHERE candidate.[ProposalId] = page.[Id]
                    ORDER BY candidate.[RecordedAtUtc] DESC, candidate.[Id] DESC) AS fact
                """).AsNoTracking();

    private static IQueryable<ProcessingGraphDeliveryFactView> ProjectFacts(IQueryable<CentralProcessingGraphDeliveryFact> facts)
        => facts.Select(fact => new ProcessingGraphDeliveryFactView(
            fact.Id, fact.ProposalId, fact.Kind, fact.Source, fact.OccurredAtUtc, fact.RecordedAtUtc,
            fact.ReasonCode, fact.LocalRevisionId, fact.DefinitionIdentitySha256,
            fact.SharedPlanIdentitySha256, fact.LocalPlanIdentitySha256));

    private static bool IsLifecycle(string kind)
        => kind is nameof(ProcessingGraphDeliveryFactKind.Activated) or nameof(ProcessingGraphDeliveryFactKind.RolledBack);

    private sealed record SummaryFacts(
        ProcessingGraphDeliveryFactView? Terminal,
        ProcessingGraphDeliveryFactView? Latest,
        ProcessingGraphDeliveryFactView? Lifecycle);

    /// <summary>
    /// One proposal's facts through a forced (ProposalId, RecordedAtUtc, Id) seek on SQL Server: on a compact fact
    /// table the optimizer otherwise prefers a clustered scan whose rows read grow with every proposal in the network.
    /// </summary>
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
    /// IssuedAtUtc, Id) seek on SQL Server, for the same reason as <see cref="ProposalFacts"/>.
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
