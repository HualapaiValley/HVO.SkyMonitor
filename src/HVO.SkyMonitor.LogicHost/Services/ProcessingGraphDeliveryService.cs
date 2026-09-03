using System.Collections.Immutable;
using System.Data;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IProcessingGraphDeliveryService
{
    Task<ProcessingGraphProposalPollResponseV1> PullAsync(
        DeviceRegistration registration,
        ProcessingGraphProposalPollRequestV1 request,
        CancellationToken cancellationToken);

    Task<ProcessingGraphFactAcknowledgementV1> AcknowledgeAsync(
        DeviceRegistration registration,
        ProcessingGraphDeliveryFactV1 fact,
        CancellationToken cancellationToken);
}

internal sealed partial class ProcessingGraphDeliveryService(
    ApplicationDbContext dbContext,
    ProcessingGraphCatalogService catalog,
    TimeProvider timeProvider,
    ProcessingGraphCatalogTelemetry telemetry,
    ILogger<ProcessingGraphDeliveryService> logger) : IProcessingGraphDeliveryService
{
    private static readonly TimeSpan ProposalLifetime = TimeSpan.FromHours(1);

    public async Task<ProcessingGraphProposalPollResponseV1> PullAsync(
        DeviceRegistration registration,
        ProcessingGraphProposalPollRequestV1 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(request);
        var started = timeProvider.GetTimestamp();
        using var activity = ProcessingGraphCatalogTelemetry.ActivitySource.StartActivity("processing-graph.pull");
        if (!string.Equals(request.SchemaVersion, ProcessingGraphDeliverySchemaVersions.Current, StringComparison.Ordinal) ||
            !string.Equals(request.AgentId, registration.DeviceId, StringComparison.Ordinal) ||
            !HasValidActiveIdentity(request) ||
            request.Capabilities is null || !request.Capabilities.HasValidIdentity() ||
            request.Capabilities.StepAliases.Length > 256 || request.Capabilities.CapabilityLabels.Length > 256)
        {
            throw new ArgumentException("The processing graph proposal poll is invalid.", nameof(request));
        }

        var now = timeProvider.GetUtcNow();
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var installation = await dbContext.LogicalCameraInstallations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RegistrationId == registration.Id && item.RetiredAtUtc == null,
                cancellationToken).ConfigureAwait(false);
        if (dbContext.Database.IsSqlServer() && installation is not null)
        {
            _ = await CentralProcessingGraphPullLock.AcquireAsync(
                dbContext, installation.Id, cancellationToken).ConfigureAwait(false);
        }
        var assignment = installation is null
            ? null
            : await catalog.ResolveAsync(
                CentralProcessingGraphTargetHost.Edge,
                registration.ObservatoryId,
                installation.LogicalCameraId,
                now,
                cancellationToken).ConfigureAwait(false);
        var revision = assignment?.Revision;
        ProcessingGraphDefinition? definition = null;
        string? incompatibilityReason = null;
        if (revision is not null)
        {
            definition = ParseDefinition(revision.DefinitionJson);
            // Stable step aliases are ordinal identities: the CameraAgent canonicalizes and matches them
            // case-sensitively, so a case-variant alias must surface as unavailable here rather than as a
            // proposal the agent rejects for identity mismatch.
            var aliases = request.Capabilities.StepAliases.ToHashSet(StringComparer.Ordinal);
            if (definition.Nodes.Any(node => node.Enabled && !aliases.Contains(node.StepAlias)))
            {
                incompatibilityReason = "step-alias-unavailable";
            }
            else
            {
                var compiled = ProcessingGraphCompiler.Compile(
                    definition,
                    new(ProcessingGraphHosts.CameraAgent, request.Capabilities.CapabilityLabels));
                if (!compiled.IsValid || !string.Equals(
                        compiled.Plan!.PlanIdentitySha256,
                        revision.EdgePlanIdentitySha256,
                        StringComparison.Ordinal))
                {
                    incompatibilityReason = "capability-unavailable";
                }
            }
        }
        var stale = await dbContext.CentralProcessingGraphDeliveryProposals
            .Where(item => item.RegistrationId == registration.Id &&
                item.ExpiresAtUtc <= now &&
                !item.Facts.Any(fact => fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) ||
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Rejected) ||
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Expired) ||
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded)))
            .OrderBy(item => item.ExpiresAtUtc)
            .ThenBy(item => item.Id)
            .Take(64)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var expiredProposal in stale)
        {
            if (await CanAppendTerminalFactAsync(expiredProposal.Id, cancellationToken).ConfigureAwait(false))
            {
                AppendCentralFact(
                    expiredProposal,
                    ProcessingGraphDeliveryFactKind.Expired,
                    Max(now, expiredProposal.ExpiresAtUtc),
                    "proposal-expired");
            }
        }

        var supersededCandidates = dbContext.CentralProcessingGraphDeliveryProposals
            .Where(item => item.RegistrationId == registration.Id && item.ExpiresAtUtc > now &&
                !item.Facts.Any(fact => fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) ||
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Rejected) ||
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Expired) ||
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded)));
        if (installation is not null && assignment is not null && revision is not null &&
            incompatibilityReason is null)
        {
            supersededCandidates = supersededCandidates.Where(item =>
                item.LogicalCameraInstallationId != installation.Id ||
                item.AssignmentId != assignment.Id ||
                item.CapabilitySnapshotSha256 != request.Capabilities.IdentitySha256 ||
                item.ExpectedActiveLocalRevisionId != request.ActiveLocalRevisionId);
        }
        var supersededReason = installation is null
            ? "installation-unavailable"
            : assignment is null || revision is null
                ? "assignment-unavailable"
                : incompatibilityReason ?? "effective-delivery-tuple-changed";
        var superseded = await supersededCandidates
            .OrderBy(item => item.IssuedAtUtc)
            .ThenBy(item => item.Id)
            .Take(64)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var pending in superseded)
        {
            if (await CanAppendTerminalFactAsync(pending.Id, cancellationToken).ConfigureAwait(false))
            {
                AppendCentralFact(
                    pending,
                    ProcessingGraphDeliveryFactKind.Superseded,
                    Max(now, pending.IssuedAtUtc),
                    supersededReason);
            }
        }

        if (installation is null)
        {
            await PersistAsync().ConfigureAwait(false);
            return Complete(ProcessingGraphProposalPollDisposition.NoAssignment, "installation-unavailable", null);
        }
        if (assignment is null || revision is null || definition is null)
        {
            await PersistAsync().ConfigureAwait(false);
            return Complete(ProcessingGraphProposalPollDisposition.NoAssignment, "assignment-unavailable", null);
        }
        if (incompatibilityReason is not null)
        {
            await PersistAsync().ConfigureAwait(false);
            return Complete(ProcessingGraphProposalPollDisposition.Incompatible, incompatibilityReason, null);
        }

        var current = request.ActiveLocalRevisionId is not null &&
            await dbContext.CentralProcessingGraphDeliveryProposals.AsNoTracking()
                .AnyAsync(item => item.AssignmentId == assignment.Id &&
                    item.RevisionId == revision.Id &&
                    item.RegistrationId == registration.Id &&
                    item.LogicalCameraInstallationId == installation.Id &&
                    item.CapabilitySnapshotSha256 == request.Capabilities.IdentitySha256 &&
                    !item.Facts.Any(fact => fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded)) &&
                    item.Facts.Any(fact => fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) &&
                        fact.LocalRevisionId == request.ActiveLocalRevisionId &&
                        fact.DefinitionIdentitySha256 == request.ActiveDefinitionIdentitySha256 &&
                        fact.SharedPlanIdentitySha256 == request.ActiveSharedPlanIdentitySha256 &&
                        fact.DefinitionIdentitySha256 == revision.DefinitionIdentitySha256 &&
                        fact.SharedPlanIdentitySha256 == revision.EdgePlanIdentitySha256),
                    cancellationToken).ConfigureAwait(false);
        if (current)
        {
            await PersistAsync().ConfigureAwait(false);
            return Complete(ProcessingGraphProposalPollDisposition.Current, "assignment-current", null);
        }

        var staged = await dbContext.CentralProcessingGraphDeliveryProposals.AsNoTracking()
            .AnyAsync(item => item.AssignmentId == assignment.Id &&
                item.RevisionId == revision.Id &&
                item.RegistrationId == registration.Id &&
                item.LogicalCameraInstallationId == installation.Id &&
                item.CapabilitySnapshotSha256 == request.Capabilities.IdentitySha256 &&
                item.ExpectedActiveLocalRevisionId == request.ActiveLocalRevisionId &&
                !item.Facts.Any(fact => fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded)) &&
                item.Facts.Any(fact => fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) &&
                    fact.LocalRevisionId != request.ActiveLocalRevisionId &&
                    fact.DefinitionIdentitySha256 == revision.DefinitionIdentitySha256 &&
                    fact.SharedPlanIdentitySha256 == revision.EdgePlanIdentitySha256),
                cancellationToken).ConfigureAwait(false);
        if (staged)
        {
            await PersistAsync().ConfigureAwait(false);
            return Complete(ProcessingGraphProposalPollDisposition.Staged, "assignment-staged", null);
        }

        var rejected = await dbContext.CentralProcessingGraphDeliveryProposals.AsNoTracking()
            .Where(item => item.AssignmentId == assignment.Id &&
                item.RegistrationId == registration.Id &&
                item.LogicalCameraInstallationId == installation.Id &&
                item.CapabilitySnapshotSha256 == request.Capabilities.IdentitySha256 &&
                item.ExpectedActiveLocalRevisionId == request.ActiveLocalRevisionId)
            .SelectMany(item => item.Facts)
            .AnyAsync(fact => fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Rejected), cancellationToken)
            .ConfigureAwait(false);
        if (rejected)
        {
            await PersistAsync().ConfigureAwait(false);
            return Complete(ProcessingGraphProposalPollDisposition.Incompatible, "assignment-rejected", null);
        }

        var existing = await dbContext.CentralProcessingGraphDeliveryProposals
            .Where(item => item.AssignmentId == assignment.Id &&
                item.RegistrationId == registration.Id &&
                item.LogicalCameraInstallationId == installation.Id && item.ExpiresAtUtc > now &&
                item.CapabilitySnapshotSha256 == request.Capabilities.IdentitySha256 &&
                item.ExpectedActiveLocalRevisionId == request.ActiveLocalRevisionId &&
                !item.Facts.Any(fact => fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) ||
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Rejected) ||
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Expired) ||
                    fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded)))
            .OrderByDescending(item => item.IssuedAtUtc)
            .ThenBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        CentralProcessingGraphDeliveryProposal proposal;
        if (existing is not null)
        {
            proposal = existing;
        }
        else
        {
            proposal = new CentralProcessingGraphDeliveryProposal
            {
                AssignmentId = assignment.Id,
                RevisionId = assignment.RevisionId,
                RegistrationId = registration.Id,
                LogicalCameraInstallationId = installation.Id,
                InstallationPublicId = installation.InstallationPublicId,
                ExpectedActiveLocalRevisionId = request.ActiveLocalRevisionId,
                CapabilitySnapshotSha256 = request.Capabilities.IdentitySha256,
                IssuedAtUtc = now,
                ExpiresAtUtc = now + ProposalLifetime
            };
            dbContext.CentralProcessingGraphDeliveryProposals.Add(proposal);
            AppendCentralFact(proposal, ProcessingGraphDeliveryFactKind.Retrieved, now, "agent-pull");
        }
        await PersistAsync().ConfigureAwait(false);
        Log.Proposal(logger, proposal.Id, assignment.Id, registration.Id);
        return Complete(ProcessingGraphProposalPollDisposition.Proposed, "proposal-available", new(
            ProcessingGraphDeliverySchemaVersions.Current,
            proposal.Id,
            revision.Id,
            assignment.Id,
            registration.Id,
            installation.Id,
            installation.InstallationPublicId,
            proposal.ExpectedActiveLocalRevisionId,
            proposal.CapabilitySnapshotSha256,
            revision.DefinitionIdentitySha256,
            revision.EdgePlanIdentitySha256!,
            definition,
            proposal.IssuedAtUtc,
            proposal.ExpiresAtUtc));

        async Task PersistAsync()
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        async Task<bool> CanAppendTerminalFactAsync(Guid proposalId, CancellationToken token)
        {
            if (dbContext.Database.IsSqlServer())
            {
                _ = await CentralProcessingGraphProposalLock.AcquireAsync(dbContext, proposalId, token)
                    .ConfigureAwait(false);
            }
            return !await dbContext.CentralProcessingGraphDeliveryFacts.AsNoTracking()
                .AnyAsync(fact => fact.ProposalId == proposalId &&
                    (fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) ||
                     fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Rejected) ||
                     fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Expired) ||
                     fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded)), token)
                .ConfigureAwait(false);
        }

        ProcessingGraphProposalPollResponseV1 Complete(
            ProcessingGraphProposalPollDisposition disposition,
            string reason,
            ProcessingGraphDeliveryProposalV1? deliveryProposal)
        {
            telemetry.Record("pull", disposition.ToString(), timeProvider.GetElapsedTime(started));
            activity?.SetTag("processing_graph.outcome", disposition.ToString());
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return new(ProcessingGraphDeliverySchemaVersions.Current, disposition, reason, now, deliveryProposal);
        }
    }

    public async Task<ProcessingGraphFactAcknowledgementV1> AcknowledgeAsync(
        DeviceRegistration registration,
        ProcessingGraphDeliveryFactV1 fact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(fact);
        var started = timeProvider.GetTimestamp();
        using var activity = ProcessingGraphCatalogTelemetry.ActivitySource.StartActivity("processing-graph.acknowledge");
        if (!string.Equals(fact.SchemaVersion, ProcessingGraphDeliverySchemaVersions.Current, StringComparison.Ordinal) ||
            fact.FactId == Guid.Empty || fact.ProposalId == Guid.Empty || !Enum.IsDefined(fact.Kind) ||
            fact.Kind is ProcessingGraphDeliveryFactKind.Retrieved or ProcessingGraphDeliveryFactKind.Superseded ||
            fact.OccurredAtUtc.Offset != TimeSpan.Zero || fact.ReasonCode?.Length > 128 ||
            !IsOptionalSha256(fact.LocalRevisionId) || !IsOptionalSha256(fact.DefinitionIdentitySha256) ||
            !IsOptionalSha256(fact.SharedPlanIdentitySha256) || !IsOptionalSha256(fact.LocalPlanIdentitySha256))
        {
            throw new ArgumentException("The processing graph delivery fact is invalid.", nameof(fact));
        }
        var now = timeProvider.GetUtcNow();
        if (fact.OccurredAtUtc > now.AddMinutes(5))
        {
            throw new ArgumentException("The processing graph delivery fact timestamp is invalid.", nameof(fact));
        }

        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (dbContext.Database.IsSqlServer())
        {
            _ = await CentralProcessingGraphProposalLock.AcquireAsync(
                dbContext, fact.ProposalId, cancellationToken).ConfigureAwait(false);
        }
        var proposal = await dbContext.CentralProcessingGraphDeliveryProposals
            .Include(item => item.Revision)
            .SingleOrDefaultAsync(item => item.Id == fact.ProposalId && item.RegistrationId == registration.Id,
                cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The processing graph delivery proposal was not found.");
        var duplicateId = await dbContext.CentralProcessingGraphDeliveryFacts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == fact.FactId, cancellationToken).ConfigureAwait(false);
        if (duplicateId is not null)
        {
            if (!FactMatches(duplicateId, fact))
            {
                throw new InvalidOperationException("The delivery fact identity has conflicting content.");
            }
            await CommitAsync().ConfigureAwait(false);
            return Complete(ProcessingGraphFactAcknowledgementDisposition.Duplicate);
        }
        var singletonKind = fact.Kind is ProcessingGraphDeliveryFactKind.Accepted or
            ProcessingGraphDeliveryFactKind.Rejected or ProcessingGraphDeliveryFactKind.Expired;
        var settlement = await dbContext.CentralProcessingGraphDeliveryFacts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProposalId == proposal.Id &&
                (item.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) ||
                 item.Kind == nameof(ProcessingGraphDeliveryFactKind.Rejected) ||
                 item.Kind == nameof(ProcessingGraphDeliveryFactKind.Expired) ||
                 item.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded)), cancellationToken)
            .ConfigureAwait(false);
        if (settlement?.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded))
        {
            await CommitAsync().ConfigureAwait(false);
            return Complete(ProcessingGraphFactAcknowledgementDisposition.Superseded);
        }
        if (singletonKind && settlement is not null)
        {
            if (fact.Kind == ProcessingGraphDeliveryFactKind.Expired &&
                settlement.Kind == nameof(ProcessingGraphDeliveryFactKind.Expired) &&
                string.Equals(settlement.Source, "LogicHost", StringComparison.Ordinal))
            {
                await CommitAsync().ConfigureAwait(false);
                return Complete(ProcessingGraphFactAcknowledgementDisposition.Duplicate);
            }
            if (!FactMatches(settlement, fact))
            {
                throw new InvalidOperationException("The proposal already contains a conflicting delivery fact.");
            }
            await CommitAsync().ConfigureAwait(false);
            return Complete(ProcessingGraphFactAcknowledgementDisposition.Duplicate);
        }
        var accepted = settlement?.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted)
            ? settlement
            : await dbContext.CentralProcessingGraphDeliveryFacts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProposalId == proposal.Id &&
                    item.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted), cancellationToken)
                .ConfigureAwait(false);
        var retrieved = await dbContext.CentralProcessingGraphDeliveryFacts.AsNoTracking()
            .AnyAsync(item => item.ProposalId == proposal.Id &&
                item.Kind == nameof(ProcessingGraphDeliveryFactKind.Retrieved), cancellationToken)
            .ConfigureAwait(false);
        var latestLifecycle = fact.Kind is ProcessingGraphDeliveryFactKind.Activated or
            ProcessingGraphDeliveryFactKind.RolledBack
                ? await ReadLatestLifecycleFactAsync(proposal.Id, cancellationToken).ConfigureAwait(false)
                : null;
        ValidateTransition(
            proposal,
            fact,
            new(retrieved, accepted, latestLifecycle));
        var recordedAt = latestLifecycle is not null && now <= latestLifecycle.RecordedAtUtc
            ? latestLifecycle.RecordedAtUtc.AddTicks(1)
            : now;
        dbContext.CentralProcessingGraphDeliveryFacts.Add(new()
        {
            Id = fact.FactId,
            ProposalId = proposal.Id,
            Kind = fact.Kind.ToString(),
            OccurredAtUtc = fact.OccurredAtUtc,
            RecordedAtUtc = recordedAt,
            Source = "CameraAgent",
            LocalRevisionId = fact.LocalRevisionId,
            DefinitionIdentitySha256 = fact.DefinitionIdentitySha256,
            SharedPlanIdentitySha256 = fact.SharedPlanIdentitySha256,
            LocalPlanIdentitySha256 = fact.LocalPlanIdentitySha256,
            ReasonCode = fact.ReasonCode
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await CommitAsync().ConfigureAwait(false);
        Log.Fact(logger, fact.Kind.ToString(), proposal.Id, fact.FactId);
        return Complete(ProcessingGraphFactAcknowledgementDisposition.Recorded);

        ProcessingGraphFactAcknowledgementV1 Complete(ProcessingGraphFactAcknowledgementDisposition disposition)
        {
            telemetry.Record("acknowledge", disposition.ToString(), timeProvider.GetElapsedTime(started));
            activity?.SetTag("processing_graph.fact_kind", fact.Kind.ToString());
            activity?.SetTag("processing_graph.acknowledgement_disposition", disposition.ToString());
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return new(ProcessingGraphDeliverySchemaVersions.Current, fact.FactId, disposition, now);
        }

        async Task CommitAsync()
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static void ValidateTransition(
        CentralProcessingGraphDeliveryProposal proposal,
        ProcessingGraphDeliveryFactV1 fact,
        ProcessingGraphTransitionState state)
    {
        if (!state.Retrieved)
        {
            throw new InvalidOperationException("The proposal has not been retrieved.");
        }
        if (fact.OccurredAtUtc < proposal.IssuedAtUtc)
        {
            throw new InvalidOperationException("The proposal fact predates issuance.");
        }
        switch (fact.Kind)
        {
            case ProcessingGraphDeliveryFactKind.Accepted:
                if (fact.OccurredAtUtc > proposal.ExpiresAtUtc || state.Accepted is not null || proposal.Revision is null ||
                    !string.Equals(fact.DefinitionIdentitySha256, proposal.Revision.DefinitionIdentitySha256,
                        StringComparison.Ordinal) ||
                    !string.Equals(fact.SharedPlanIdentitySha256, proposal.Revision.EdgePlanIdentitySha256,
                        StringComparison.Ordinal) ||
                    !IsSha256(fact.LocalRevisionId) || !IsSha256(fact.LocalPlanIdentitySha256))
                {
                    throw new InvalidOperationException("The proposal acceptance evidence is invalid.");
                }
                break;
            case ProcessingGraphDeliveryFactKind.Rejected:
                if (fact.OccurredAtUtc > proposal.ExpiresAtUtc ||
                    state.Accepted is not null ||
                    string.IsNullOrWhiteSpace(fact.ReasonCode))
                {
                    throw new InvalidOperationException("The proposal terminal evidence is invalid.");
                }
                break;
            case ProcessingGraphDeliveryFactKind.Expired:
                if (fact.OccurredAtUtc < proposal.ExpiresAtUtc ||
                    state.Accepted is not null ||
                    string.IsNullOrWhiteSpace(fact.ReasonCode))
                {
                    throw new InvalidOperationException("The proposal expiry evidence is invalid.");
                }
                break;
            case ProcessingGraphDeliveryFactKind.Activated:
                if (state.Accepted is null ||
                    state.LatestLifecycle is { } predecessor &&
                    (predecessor.Kind != nameof(ProcessingGraphDeliveryFactKind.RolledBack) ||
                     fact.OccurredAtUtc < predecessor.OccurredAtUtc) ||
                    !MatchesAcceptedIdentity(state.Accepted, fact))
                {
                    throw new InvalidOperationException("The proposal activation evidence is invalid.");
                }
                break;
            case ProcessingGraphDeliveryFactKind.RolledBack:
                if (state.Accepted is null || state.LatestLifecycle is not { } activation ||
                    activation.Kind != nameof(ProcessingGraphDeliveryFactKind.Activated) ||
                    fact.OccurredAtUtc < activation.OccurredAtUtc ||
                    !MatchesAcceptedIdentity(state.Accepted, fact))
                {
                    throw new InvalidOperationException("The proposal rollback evidence is invalid.");
                }
                break;
            default:
                throw new InvalidOperationException("The delivery fact kind is not agent-reportable.");
        }
    }

    private static bool MatchesAcceptedIdentity(
        CentralProcessingGraphDeliveryFact accepted,
        ProcessingGraphDeliveryFactV1 fact)
    {
        return fact.OccurredAtUtc >= accepted.OccurredAtUtc &&
            string.Equals(fact.LocalRevisionId, accepted.LocalRevisionId, StringComparison.Ordinal) &&
            string.Equals(fact.DefinitionIdentitySha256, accepted.DefinitionIdentitySha256, StringComparison.Ordinal) &&
            string.Equals(fact.SharedPlanIdentitySha256, accepted.SharedPlanIdentitySha256, StringComparison.Ordinal) &&
            string.Equals(fact.LocalPlanIdentitySha256, accepted.LocalPlanIdentitySha256, StringComparison.Ordinal);
    }

    private static bool FactMatches(
        CentralProcessingGraphDeliveryFact stored,
        ProcessingGraphDeliveryFactV1 fact)
        => stored.ProposalId == fact.ProposalId && stored.Kind == fact.Kind.ToString() &&
           stored.OccurredAtUtc == fact.OccurredAtUtc && stored.LocalRevisionId == fact.LocalRevisionId &&
           stored.DefinitionIdentitySha256 == fact.DefinitionIdentitySha256 &&
           stored.SharedPlanIdentitySha256 == fact.SharedPlanIdentitySha256 &&
           stored.LocalPlanIdentitySha256 == fact.LocalPlanIdentitySha256 && stored.ReasonCode == fact.ReasonCode;

    private Task<CentralProcessingGraphDeliveryFact?> ReadLatestLifecycleFactAsync(
        Guid proposalId,
        CancellationToken cancellationToken)
        => dbContext.CentralProcessingGraphDeliveryFacts.AsNoTracking()
            .Where(item => item.ProposalId == proposalId &&
                (item.Kind == nameof(ProcessingGraphDeliveryFactKind.Activated) ||
                 item.Kind == nameof(ProcessingGraphDeliveryFactKind.RolledBack)))
            .OrderByDescending(item => item.RecordedAtUtc)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private void AppendCentralFact(
        CentralProcessingGraphDeliveryProposal proposal,
        ProcessingGraphDeliveryFactKind kind,
        DateTimeOffset occurredAtUtc,
        string reasonCode)
    {
        if (proposal.Facts.Any(item => item.Kind == kind.ToString()))
        {
            return;
        }
        var fact = new CentralProcessingGraphDeliveryFact
        {
            ProposalId = proposal.Id,
            Kind = kind.ToString(),
            OccurredAtUtc = occurredAtUtc,
            RecordedAtUtc = occurredAtUtc,
            Source = "LogicHost",
            ReasonCode = reasonCode
        };
        proposal.Facts.Add(fact);
        dbContext.CentralProcessingGraphDeliveryFacts.Add(fact);
    }

    private static ProcessingGraphDefinition ParseDefinition(string json)
    {
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(json));
        return parsed.IsValid
            ? parsed.Definition!
            : throw new InvalidDataException("The assigned processing graph definition is invalid.");
    }

    private static bool IsOptionalSha256(string? value) => value is null || IsSha256(value);

    private static bool HasValidActiveIdentity(ProcessingGraphProposalPollRequestV1 request)
        => request.ActiveLocalRevisionId is null && request.ActiveDefinitionIdentitySha256 is null &&
               request.ActiveSharedPlanIdentitySha256 is null ||
           IsSha256(request.ActiveLocalRevisionId) && IsSha256(request.ActiveDefinitionIdentitySha256) &&
               IsSha256(request.ActiveSharedPlanIdentitySha256);

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
        => left >= right ? left : right;

    internal sealed record ProcessingGraphTransitionState(
        bool Retrieved,
        CentralProcessingGraphDeliveryFact? Accepted,
        CentralProcessingGraphDeliveryFact? LatestLifecycle);

    private static partial class Log
    {
        [LoggerMessage(2192, LogLevel.Information,
            "Processing graph proposal issued: ProposalId={ProposalId}, AssignmentId={AssignmentId}, RegistrationId={RegistrationId}")]
        internal static partial void Proposal(
            ILogger logger,
            Guid proposalId,
            Guid assignmentId,
            Guid registrationId);

        [LoggerMessage(2193, LogLevel.Information,
            "Processing graph delivery fact {Kind} acknowledged: ProposalId={ProposalId}, FactId={FactId}")]
        internal static partial void Fact(ILogger logger, string kind, Guid proposalId, Guid factId);
    }
}

internal static class CentralProcessingGraphPullLock
{
    internal static Task<int> AcquireAsync(
        ApplicationDbContext dbContext,
        Guid installationId,
        CancellationToken cancellationToken)
        => dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [LogicalCameraInstallations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {installationId}")
            .SingleOrDefaultAsync(cancellationToken);
}

internal static class CentralProcessingGraphProposalLock
{
    internal static Task<int> AcquireAsync(
        ApplicationDbContext dbContext,
        Guid proposalId,
        CancellationToken cancellationToken)
        => dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralProcessingGraphDeliveryProposals] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {proposalId}")
            .SingleOrDefaultAsync(cancellationToken);
}
