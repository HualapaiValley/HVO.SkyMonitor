using System.Data;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeploymentLocationAuthorityService
{
    Task<DeploymentLocationAcknowledgment> ProposeAsync(
        DeviceRegistration registration,
        DeploymentLocationSnapshot deployment,
        DeploymentLocationSourceKind sourceKind,
        string actor,
        CancellationToken cancellationToken = default);

    Task<DeploymentLocationProposalPage> ListAsync(
        string ownerUserId,
        DeploymentLocationResolutionStatus? status,
        int take,
        DeploymentLocationProposalCursor? cursor = null,
        Guid? observatoryScope = null,
        CancellationToken cancellationToken = default);

    Task<DeploymentLocationProposal?> GetAsync(
        Guid deploymentLocationId,
        string ownerUserId,
        Guid? observatoryScope = null,
        CancellationToken cancellationToken = default);

    Task<DeploymentLocationResolutionResult> ResolveAsync(
        Guid deploymentLocationId,
        string ownerUserId,
        DeploymentLocationResolutionStatus status,
        string reason,
        Guid expectedConcurrencyToken,
        Guid? observatoryScope = null,
        CancellationToken cancellationToken = default);

    Task ReconcileAsync(
        DeviceDeploymentLocationVersion deployment,
        CancellationToken cancellationToken = default);
}

internal sealed partial class DeploymentLocationAuthorityService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ICentralDerivativeJobScheduler? derivativeJobScheduler = null,
    DeploymentLocationTelemetry? telemetry = null,
    ILogger<DeploymentLocationAuthorityService>? logger = null) : IDeploymentLocationAuthorityService
{
    private const double EarthRadiusMeters = 6_371_008.8;

    public async Task<DeploymentLocationAcknowledgment> ProposeAsync(
        DeviceRegistration registration,
        DeploymentLocationSnapshot deployment,
        DeploymentLocationSourceKind sourceKind,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = DeploymentLocationTelemetry.ActivitySource.StartActivity(
            "deployment-location.bootstrap.resolve");
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!Enum.IsDefined(sourceKind))
        {
            throw new DeviceRegistrationException("Deployment location source kind is invalid.");
        }
        var validation = deployment.Validate();
        if (!validation.IsValid)
        {
            throw new DeviceRegistrationException(
                $"Deployment location is invalid: {validation.ReasonCode} ({validation.FieldPath}).");
        }

        var isRelational = dbContext.Database.IsRelational();
        var ownsTransaction = isRelational && dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false)
            : null;
        Observatory observatory;
        if (ownsTransaction)
        {
            observatory = await dbContext.Observatories.FromSqlInterpolated($"""
                SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {registration.ObservatoryId}
                """).SingleAsync(cancellationToken).ConfigureAwait(false);
            registration = await dbContext.DeviceRegistrations.FromSqlInterpolated($"""
                SELECT * FROM [DeviceRegistrations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {registration.Id}
                """).SingleAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            observatory = registration.Observatory
                ?? await dbContext.Observatories.SingleAsync(
                    item => item.Id == registration.ObservatoryId,
                    cancellationToken).ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow();
        var observatoryVersion = await ObservatoryLocationAuthority.EnsureCurrentVersionAsync(
            dbContext, observatory, now, actor, cancellationToken).ConfigureAwait(false);
        var priorEvaluations = await dbContext.DeviceDeploymentLocationVersions
            .Where(item => item.RegistrationId == registration.Id
                && item.LocationId == deployment.LocationId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var conflictingIdentity = priorEvaluations.FirstOrDefault(item => item.Version == deployment.Version
            && (!string.Equals(item.CanonicalSha256, deployment.CanonicalSha256, StringComparison.OrdinalIgnoreCase)
                || item.SourceKind != sourceKind));
        if (conflictingIdentity is not null)
        {
            throw new DeviceRegistrationException(
                "The deployment location identity already exists with different facts or source classification.");
        }
        ValidateHistoryOrder(priorEvaluations, deployment);
        foreach (var superseded in priorEvaluations.Where(item =>
                     item.Status == DeploymentLocationResolutionStatus.Pending
                     && item.Version < deployment.Version))
        {
            superseded.Status = DeploymentLocationResolutionStatus.Rejected;
            superseded.ReasonCode = "deployment-version-superseded";
            superseded.ResolvedAtUtc = now;
            superseded.ResolvedByUserId = actor;
            superseded.ConcurrencyToken = Guid.NewGuid();
            dbContext.DeploymentLocationResolutionAudits.Add(new DeploymentLocationResolutionAudit
            {
                DeploymentLocation = superseded,
                DeviceDeploymentLocationVersionId = superseded.Id,
                RegistrationId = superseded.RegistrationId,
                PreviousStatus = DeploymentLocationResolutionStatus.Pending,
                NewStatus = DeploymentLocationResolutionStatus.Rejected,
                ActorUserId = actor,
                Reason = "deployment-version-superseded",
                OccurredAtUtc = now
            });
        }
        var existing = await dbContext.DeviceDeploymentLocationVersions
            .Include(item => item.ObservatoryLocationVersion)
            .SingleOrDefaultAsync(item =>
                item.RegistrationId == registration.Id &&
                item.LocationId == deployment.LocationId &&
                item.Version == deployment.Version &&
                item.ObservatoryLocationVersionId == observatoryVersion.Id,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.DevicePublicId ??= registration.DevicePublicId;
            await ApplyRegistrationStateIfCurrentAsync(registration, existing, cancellationToken).ConfigureAwait(false);
            await ReconcileCaptureLocationsAsync(existing, cancellationToken).ConfigureAwait(false);
            if (!isRelational || transaction is not null)
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            RecordOperation("bootstrap.resolve", "existing", ExistingReason(existing), existing.SourceKind,
                existing.Status, started, activity, VersionRelation(existing));
            return ToAcknowledgment(existing);
        }

        var (status, reasonCode) = Evaluate(observatoryVersion, deployment, sourceKind);
        DateTimeOffset? resolvedAtUtc = status == DeploymentLocationResolutionStatus.Pending ? null : now;
        var entity = new DeviceDeploymentLocationVersion
        {
            Registration = registration,
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId,
            ObservatoryId = registration.ObservatoryId,
            ObservatoryLocationVersion = observatoryVersion,
            ObservatoryLocationVersionId = observatoryVersion.Id,
            ObservatoryLocationVersionNumber = observatoryVersion.Version,
            ObservatoryLocationCanonicalSha256 = observatoryVersion.CanonicalSha256,
            LocationId = deployment.LocationId,
            Version = deployment.Version,
            CanonicalSha256 = deployment.CanonicalSha256,
            Source = deployment.Source,
            SourceKind = sourceKind,
            HorizontalAccuracyMeters = deployment.HorizontalAccuracyMeters,
            EffectiveFromUtc = deployment.EffectiveFromUtc,
            EffectiveUntilUtc = deployment.EffectiveUntilUtc,
            LatitudeDegrees = deployment.LatitudeDegrees,
            LongitudeDegrees = deployment.LongitudeDegrees,
            ElevationMeters = deployment.ElevationMeters,
            TimeZoneId = deployment.TimeZoneId,
            Status = status,
            ReasonCode = reasonCode,
            ProposedAtUtc = now,
            ResolvedAtUtc = resolvedAtUtc,
            ResolvedByUserId = resolvedAtUtc.HasValue ? actor : null
        };
        dbContext.DeploymentLocationResolutionAudits.Add(new DeploymentLocationResolutionAudit
        {
            DeploymentLocation = entity,
            DeviceDeploymentLocationVersionId = entity.Id,
            RegistrationId = registration.Id,
            PreviousStatus = null,
            NewStatus = status,
            ActorUserId = actor,
            Reason = reasonCode,
            OccurredAtUtc = now
        });
        dbContext.DeviceDeploymentLocationVersions.Add(entity);
        await ApplyRegistrationStateIfCurrentAsync(registration, entity, cancellationToken).ConfigureAwait(false);
        await ReconcileCaptureLocationsAsync(entity, cancellationToken).ConfigureAwait(false);
        if (!isRelational || transaction is not null)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        RecordOperation("bootstrap.resolve", "created", reasonCode, sourceKind, status, started, activity, "current");
        return ToAcknowledgment(entity);
    }

    public async Task<DeploymentLocationProposalPage> ListAsync(
        string ownerUserId,
        DeploymentLocationResolutionStatus? status,
        int take,
        DeploymentLocationProposalCursor? cursor = null,
        Guid? observatoryScope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        if (take is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }
        var observatories = ObservatoryMembershipAccess.ForUser(dbContext, ownerUserId)
            .Select(membership => membership.ObservatoryId);
        var query = dbContext.DeviceDeploymentLocationVersions.AsNoTracking()
            .Include(item => item.Registration)
            .Include(item => item.ObservatoryLocationVersion)
            .Where(item => observatories.Contains(item.ObservatoryId));
        if (observatoryScope is { } scope)
        {
            query = query.Where(item => item.ObservatoryId == scope);
        }
        if (status.HasValue)
        {
            query = query.Where(item => item.Status == status.Value);
        }
        if (cursor is not null)
        {
            query = query.Where(item => item.ProposedAtUtc < cursor.ProposedAtUtc
                || item.ProposedAtUtc == cursor.ProposedAtUtc && item.Id.CompareTo(cursor.Id) < 0);
        }
        var entities = await query.OrderByDescending(item => item.ProposedAtUtc).ThenByDescending(item => item.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = entities.Count > take;
        if (hasMore)
        {
            entities.RemoveAt(entities.Count - 1);
        }
        var proposals = entities.Select(ToProposal).ToArray();
        var nextCursor = hasMore && entities.Count > 0
            ? new DeploymentLocationProposalCursor(entities[^1].ProposedAtUtc, entities[^1].Id)
            : null;
        return new DeploymentLocationProposalPage(proposals, nextCursor);
    }

    public async Task<DeploymentLocationProposal?> GetAsync(
        Guid deploymentLocationId,
        string ownerUserId,
        Guid? observatoryScope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        var observatories = ObservatoryMembershipAccess.ForUser(dbContext, ownerUserId)
            .Select(membership => membership.ObservatoryId);
        var entity = await dbContext.DeviceDeploymentLocationVersions.AsNoTracking()
            .Include(item => item.Registration)
            .Include(item => item.ObservatoryLocationVersion)
            .SingleOrDefaultAsync(item => item.Id == deploymentLocationId
                && observatories.Contains(item.ObservatoryId)
                && (observatoryScope == null || item.ObservatoryId == observatoryScope), cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToProposal(entity);
    }

    public async Task<DeploymentLocationResolutionResult> ResolveAsync(
        Guid deploymentLocationId,
        string ownerUserId,
        DeploymentLocationResolutionStatus status,
        string reason,
        Guid expectedConcurrencyToken,
        Guid? observatoryScope = null,
        CancellationToken cancellationToken = default)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = DeploymentLocationTelemetry.ActivitySource.StartActivity(
            "deployment-location.operator-resolve");
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        reason = reason.Trim();
        if (reason.Length > 128)
        {
            throw new ArgumentException("Resolution reason must not exceed 128 characters.", nameof(reason));
        }
        if (expectedConcurrencyToken == Guid.Empty)
        {
            throw new ArgumentException("A current concurrency token is required.", nameof(expectedConcurrencyToken));
        }
        if (status == DeploymentLocationResolutionStatus.Pending)
        {
            throw new ArgumentException("Owner resolution must acknowledge or reject the deployment.", nameof(status));
        }

        var isRelational = dbContext.Database.IsRelational();
        var authorizedObservatories = ObservatoryMembershipAccess.ForManager(dbContext, ownerUserId)
            .Select(membership => membership.ObservatoryId);
        var identity = await dbContext.DeviceDeploymentLocationVersions.AsNoTracking()
            .Where(item => item.Id == deploymentLocationId
                && authorizedObservatories.Contains(item.ObservatoryId)
                && (observatoryScope == null || item.ObservatoryId == observatoryScope))
            .Select(item => new { item.RegistrationId, item.ObservatoryId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            RecordOperation("operator-resolve", "not-found", "not-found", DeploymentLocationSourceKind.Unspecified,
                status, started, activity);
            return new DeploymentLocationResolutionResult(DeploymentLocationMutationStatus.NotFound, null);
        }
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        DeviceRegistration registration;
        DeviceDeploymentLocationVersion entity;
        if (isRelational)
        {
            _ = await dbContext.Observatories.FromSqlInterpolated($"""
                SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {identity.ObservatoryId}
                """).SingleAsync(cancellationToken).ConfigureAwait(false);
            registration = await dbContext.DeviceRegistrations.FromSqlInterpolated($"""
                SELECT * FROM [DeviceRegistrations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {identity.RegistrationId}
                """).SingleAsync(cancellationToken).ConfigureAwait(false);
            if (!await ObservatoryMembershipAccess.ForManager(dbContext, ownerUserId)
                .AnyAsync(item => item.ObservatoryId == identity.ObservatoryId, cancellationToken)
                .ConfigureAwait(false))
            {
                return new DeploymentLocationResolutionResult(DeploymentLocationMutationStatus.NotFound, null);
            }
            entity = await dbContext.DeviceDeploymentLocationVersions.FromSqlInterpolated($"""
                    SELECT * FROM [DeviceDeploymentLocationVersions] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {deploymentLocationId}
                    """)
                .Include(item => item.ObservatoryLocationVersion)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            entity = await dbContext.DeviceDeploymentLocationVersions
                .Include(item => item.Registration)
                .Include(item => item.ObservatoryLocationVersion)
                .SingleAsync(item => item.Id == deploymentLocationId, cancellationToken).ConfigureAwait(false);
            registration = entity.Registration!;
        }
        var currentObservatoryVersionId = await dbContext.ObservatoryLocationVersions
            .Where(item => item.ObservatoryId == entity.ObservatoryId && item.SupersededAtUtc == null)
            .Select(item => item.Id)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (entity.ObservatoryLocationVersionId != currentObservatoryVersionId)
        {
            RecordOperation("operator-resolve", "stale", "observatory-version-stale", entity.SourceKind,
                entity.Status, started, activity, "superseded");
            return new DeploymentLocationResolutionResult(DeploymentLocationMutationStatus.StaleAuthority, null);
        }
        if (entity.ConcurrencyToken != expectedConcurrencyToken)
        {
            RecordOperation("operator-resolve", "stale", "stale-etag", entity.SourceKind, entity.Status,
                started, activity);
            return new DeploymentLocationResolutionResult(DeploymentLocationMutationStatus.PreconditionFailed, null);
        }
        if (entity.Status == status && string.Equals(entity.ReasonCode, reason, StringComparison.Ordinal))
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            RecordOperation("operator-resolve", "replayed", ResolutionReason(entity.Status), entity.SourceKind, entity.Status,
                started, activity);
            return new DeploymentLocationResolutionResult(
                DeploymentLocationMutationStatus.Applied,
                ToProposal(entity));
        }
        if (entity.Status != DeploymentLocationResolutionStatus.Pending)
        {
            RecordOperation("operator-resolve", "invalid-transition", "already-resolved", entity.SourceKind,
                entity.Status, started, activity);
            return new DeploymentLocationResolutionResult(DeploymentLocationMutationStatus.InvalidTransition, null);
        }

        var now = timeProvider.GetUtcNow();
        var previous = entity.Status;
        entity.Status = status;
        entity.ReasonCode = reason;
        entity.ResolvedAtUtc = now;
        entity.ResolvedByUserId = ownerUserId;
        entity.ConcurrencyToken = Guid.NewGuid();
        dbContext.DeploymentLocationResolutionAudits.Add(new DeploymentLocationResolutionAudit
        {
            DeploymentLocation = entity,
            DeviceDeploymentLocationVersionId = entity.Id,
            RegistrationId = entity.RegistrationId,
            PreviousStatus = previous,
            NewStatus = status,
            ActorUserId = ownerUserId,
            Reason = reason,
            OccurredAtUtc = now
        });
        await ApplyRegistrationStateIfCurrentAsync(registration, entity, cancellationToken).ConfigureAwait(false);
        await ReconcileCaptureLocationsAsync(entity, cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            RecordOperation("operator-resolve", "stale", "concurrency-conflict", entity.SourceKind, entity.Status,
                started, activity);
            return new DeploymentLocationResolutionResult(DeploymentLocationMutationStatus.PreconditionFailed, null);
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        RecordOperation("operator-resolve", "applied", ResolutionReason(entity.Status), entity.SourceKind, entity.Status,
            started, activity);
        return new DeploymentLocationResolutionResult(
            DeploymentLocationMutationStatus.Applied,
            ToProposal(entity));
    }

    public Task ReconcileAsync(
        DeviceDeploymentLocationVersion deployment,
        CancellationToken cancellationToken = default)
        => ReconcileCaptureLocationsAsync(deployment, cancellationToken);

    private async Task ReconcileCaptureLocationsAsync(
        DeviceDeploymentLocationVersion deployment,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = DeploymentLocationTelemetry.ActivitySource.StartActivity(
            "deployment-location.reconcile");
        try
        {
            var observatoryVersion = deployment.ObservatoryLocationVersion
                ?? await dbContext.ObservatoryLocationVersions.SingleAsync(
                    item => item.Id == deployment.ObservatoryLocationVersionId,
                    cancellationToken).ConfigureAwait(false);
            var captures = await dbContext.CentralCaptureLocations
                .Include(item => item.CentralFrame)!.ThenInclude(frame => frame!.Artifacts)
                .Where(item => item.CentralFrame!.RegistrationId == deployment.RegistrationId
                    && item.LocationId == deployment.LocationId
                    && item.Version == deployment.Version
                    && item.CentralFrame.CapturedAtUtc >= observatoryVersion.EffectiveFromUtc
                    && (observatoryVersion.SupersededAtUtc == null
                        || item.CentralFrame.CapturedAtUtc < observatoryVersion.SupersededAtUtc))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var capture in captures)
            {
                var exactMatch = AppliesAt(deployment, capture.CentralFrame!.CapturedAtUtc)
                    && LocationMatches(deployment, capture);
                capture.DeviceDeploymentLocationVersionId = exactMatch ? deployment.Id : null;
                capture.DeploymentLocation = exactMatch ? deployment : null;
                capture.CentralFrame!.LocationEvidenceState = exactMatch
                    && deployment.Status == DeploymentLocationResolutionStatus.Acknowledged
                        ? CentralCaptureLocationEvidenceState.ReportedResolved
                        : CentralCaptureLocationEvidenceState.Mismatch;
                if (derivativeJobScheduler is null)
                {
                    continue;
                }
                foreach (var artifact in capture.CentralFrame.Artifacts.Where(item =>
                             item.ObjectState == CentralArtifactObjectState.Available
                             && item.ReconstructionState == CentralReconstructionState.Complete))
                {
                    await derivativeJobScheduler.EnsureRequiredJobsAsync(
                        artifact, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                }
            }
            var outcome = captures.Count == 0 ? "no-captures" : "reconciled";
            var reason = deployment.Status == DeploymentLocationResolutionStatus.Pending
                ? deployment.ReasonCode ?? ExistingReason(deployment)
                : ResolutionReason(deployment.Status);
            var versionRelation = VersionRelation(deployment);
            activity?.SetTag("deployment.outcome", outcome);
            activity?.SetTag("deployment.reason", reason);
            activity?.SetTag("deployment.source_kind", deployment.SourceKind.ToString());
            activity?.SetTag("deployment.status", deployment.Status.ToString());
            activity?.SetTag("deployment.version_relation", versionRelation);
            telemetry?.RecordOperation(
                "reconcile", outcome, reason, "capture", timeProvider.GetElapsedTime(started));
            if (logger is not null)
            {
                Log.ReconciliationCompleted(
                    logger, outcome, reason, deployment.SourceKind.ToString(), versionRelation, captures.Count);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            const string reason = "persistence-failure";
            var versionRelation = VersionRelation(deployment);
            activity?.SetTag("deployment.outcome", "failed");
            activity?.SetTag("deployment.reason", reason);
            activity?.SetTag("deployment.source_kind", deployment.SourceKind.ToString());
            activity?.SetTag("deployment.status", deployment.Status.ToString());
            activity?.SetTag("deployment.version_relation", versionRelation);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
            telemetry?.RecordOperation(
                "reconcile", "failed", reason, "capture", timeProvider.GetElapsedTime(started));
            if (logger is not null)
            {
                Log.ReconciliationFailed(
                    logger, exception, reason, deployment.SourceKind.ToString(), versionRelation);
            }
            throw;
        }
    }

    private void RecordOperation(
        string operation,
        string outcome,
        string reason,
        DeploymentLocationSourceKind sourceKind,
        DeploymentLocationResolutionStatus status,
        long started,
        System.Diagnostics.Activity? activity,
        string versionRelation = "current")
    {
        activity?.SetTag("deployment.outcome", outcome);
        activity?.SetTag("deployment.reason", reason);
        activity?.SetTag("deployment.source_kind", sourceKind.ToString());
        activity?.SetTag("deployment.status", status.ToString());
        activity?.SetTag("deployment.version_relation", versionRelation);
        telemetry?.RecordOperation(operation, outcome, reason, "deployment", timeProvider.GetElapsedTime(started));
        if (logger is not null)
        {
            Log.OperationCompleted(
                logger, operation, outcome, reason, sourceKind.ToString(), status.ToString(), versionRelation);
        }
    }

    private static string ResolutionReason(DeploymentLocationResolutionStatus status)
        => status == DeploymentLocationResolutionStatus.Acknowledged ? "owner-acknowledged" : "owner-rejected";

    private static string ExistingReason(DeviceDeploymentLocationVersion deployment)
        => deployment.Status switch
        {
            DeploymentLocationResolutionStatus.Pending => "existing-pending",
            DeploymentLocationResolutionStatus.Acknowledged => "existing-acknowledged",
            DeploymentLocationResolutionStatus.Rejected => "existing-rejected",
            _ => "existing-unknown"
        };

    private static string VersionRelation(DeviceDeploymentLocationVersion deployment)
        => deployment.ObservatoryLocationVersion?.SupersededAtUtc is null ? "current" : "superseded";

    private static bool LocationMatches(
        DeviceDeploymentLocationVersion deployment,
        CentralCaptureLocation capture)
        => deployment.LocationId == capture.LocationId
            && deployment.Version == capture.Version
            && string.Equals(deployment.Source, capture.Source, StringComparison.Ordinal)
            && deployment.HorizontalAccuracyMeters == capture.HorizontalAccuracyMeters
            && deployment.EffectiveFromUtc == capture.EffectiveFromUtc
            && deployment.EffectiveUntilUtc == capture.EffectiveUntilUtc;

    internal static bool AppliesAt(
        DeviceDeploymentLocationVersion deployment,
        DateTimeOffset capturedAtUtc)
    {
        capturedAtUtc = capturedAtUtc.ToUniversalTime();
        return capturedAtUtc >= deployment.EffectiveFromUtc
            && (deployment.EffectiveUntilUtc is null || capturedAtUtc < deployment.EffectiveUntilUtc.Value);
    }

    private static void ValidateHistoryOrder(
        IReadOnlyCollection<DeviceDeploymentLocationVersion> existing,
        DeploymentLocationSnapshot deployment)
    {
        var versions = existing
            .GroupBy(item => item.Version)
            .Select(group => group.First())
            .OrderBy(item => item.Version)
            .ToArray();
        var predecessor = versions.LastOrDefault(item => item.Version < deployment.Version);
        if (predecessor is not null && (predecessor.EffectiveFromUtc >= deployment.EffectiveFromUtc
            || predecessor.EffectiveUntilUtc is { } predecessorUntil
                && predecessorUntil > deployment.EffectiveFromUtc))
        {
            throw new DeviceRegistrationException(
                "The deployment location effective interval conflicts with its predecessor.");
        }
        var successor = versions.FirstOrDefault(item => item.Version > deployment.Version);
        if (successor is not null && (deployment.EffectiveFromUtc >= successor.EffectiveFromUtc
            || deployment.EffectiveUntilUtc is { } deploymentUntil
                && deploymentUntil > successor.EffectiveFromUtc))
        {
            throw new DeviceRegistrationException(
                "The deployment location effective interval conflicts with its successor.");
        }
    }

    private static (DeploymentLocationResolutionStatus Status, string ReasonCode) Evaluate(
        ObservatoryLocationVersion observatory,
        DeploymentLocationSnapshot deployment,
        DeploymentLocationSourceKind sourceKind)
    {
        if (!string.Equals(deployment.TimeZoneId, observatory.TimeZoneId, StringComparison.Ordinal))
        {
            return (DeploymentLocationResolutionStatus.Pending, "timezone-mismatch");
        }
        var distanceMeters = DistanceMeters(
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            deployment.LatitudeDegrees,
            deployment.LongitudeDegrees);
        if (sourceKind == DeploymentLocationSourceKind.Inherited)
        {
            return distanceMeters == 0 && deployment.ElevationMeters == observatory.ElevationMeters
                ? (DeploymentLocationResolutionStatus.Acknowledged, "inherited-observatory-location")
                : (DeploymentLocationResolutionStatus.Pending, "inherited-observatory-mismatch");
        }
        if (observatory.AllowedDeploymentRadiusMeters is null)
        {
            return (DeploymentLocationResolutionStatus.Pending, "boundary-unconfigured");
        }
        return distanceMeters <= observatory.AllowedDeploymentRadiusMeters.Value
            ? (DeploymentLocationResolutionStatus.Acknowledged, "within-observatory-boundary")
            : (DeploymentLocationResolutionStatus.Pending, "outside-observatory-boundary");
    }

    private static double DistanceMeters(double firstLatitude, double firstLongitude, double secondLatitude, double secondLongitude)
    {
        static double Radians(double degrees) => degrees * Math.PI / 180d;
        var firstLatitudeRadians = Radians(firstLatitude);
        var secondLatitudeRadians = Radians(secondLatitude);
        var latitudeDelta = Radians(secondLatitude - firstLatitude);
        var longitudeDelta = Radians(secondLongitude - firstLongitude);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
            Math.Cos(firstLatitudeRadians) * Math.Cos(secondLatitudeRadians) *
            Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static void ApplyRegistrationState(
        DeviceRegistration registration,
        DeploymentLocationResolutionStatus status)
        => registration.LocationEvidenceState = status switch
        {
            DeploymentLocationResolutionStatus.Pending => RegistrationLocationEvidenceState.DeploymentPending,
            DeploymentLocationResolutionStatus.Acknowledged => RegistrationLocationEvidenceState.DeploymentAcknowledged,
            DeploymentLocationResolutionStatus.Rejected => RegistrationLocationEvidenceState.DeploymentRejected,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private async Task ApplyRegistrationStateIfCurrentAsync(
        DeviceRegistration registration,
        DeviceDeploymentLocationVersion deployment,
        CancellationToken cancellationToken)
    {
        var latestPersisted = await dbContext.DeviceDeploymentLocationVersions
            .Where(item => item.RegistrationId == registration.Id)
            .OrderByDescending(item => item.EffectiveFromUtc)
            .ThenByDescending(item => item.Version)
            .ThenByDescending(item => item.ProposedAtUtc)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (latestPersisted is null || CompareRecency(deployment, latestPersisted) >= 0)
        {
            ApplyRegistrationState(registration, deployment.Status);
        }
    }

    internal static int CompareRecency(
        DeviceDeploymentLocationVersion first,
        DeviceDeploymentLocationVersion second)
    {
        var comparison = first.EffectiveFromUtc.CompareTo(second.EffectiveFromUtc);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = first.Version.CompareTo(second.Version);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = first.ProposedAtUtc.CompareTo(second.ProposedAtUtc);
        return comparison != 0 ? comparison : first.Id.CompareTo(second.Id);
    }

    private static DeploymentLocationAcknowledgment ToAcknowledgment(DeviceDeploymentLocationVersion entity)
    {
        var observatory = entity.ObservatoryLocationVersion
            ?? throw new InvalidOperationException("The Observatory location version was not loaded.");
        var acknowledgment = new DeploymentLocationAcknowledgment(
            ObservatoryLocationAuthority.ToSnapshot(observatory),
            new DeploymentLocationSnapshot(
                entity.LocationId,
                entity.Version,
                entity.CanonicalSha256,
                entity.Source,
                entity.HorizontalAccuracyMeters,
                entity.EffectiveFromUtc,
                entity.EffectiveUntilUtc,
                entity.LatitudeDegrees,
                entity.LongitudeDegrees,
                entity.ElevationMeters,
                entity.TimeZoneId),
            entity.SourceKind,
            entity.Status,
            entity.ReasonCode,
            entity.ProposedAtUtc,
            entity.ResolvedAtUtc);
        var validation = acknowledgment.Validate();
        return validation.IsValid
            ? acknowledgment
            : throw new InvalidOperationException(
                $"Persisted deployment acknowledgement is invalid: {validation.ReasonCode} ({validation.FieldPath}).");
    }

    private static DeploymentLocationProposal ToProposal(DeviceDeploymentLocationVersion entity)
    {
        var registration = entity.Registration
            ?? throw new InvalidOperationException("The deployment registration was not loaded.");
        var observatory = entity.ObservatoryLocationVersion
            ?? throw new InvalidOperationException("The Observatory location version was not loaded.");
        return new DeploymentLocationProposal(
            entity.Id,
            registration.Id,
            registration.DeviceId,
            registration.FriendlyName,
            entity.ObservatoryId,
            registration.ObservatoryName,
            ObservatoryLocationAuthority.ToSnapshot(observatory),
            new DeploymentLocationSnapshot(
                entity.LocationId,
                entity.Version,
                entity.CanonicalSha256,
                entity.Source,
                entity.HorizontalAccuracyMeters,
                entity.EffectiveFromUtc,
                entity.EffectiveUntilUtc,
                entity.LatitudeDegrees,
                entity.LongitudeDegrees,
                entity.ElevationMeters,
                entity.TimeZoneId),
            entity.SourceKind,
            entity.Status,
            entity.ReasonCode,
            entity.ProposedAtUtc,
            entity.ResolvedAtUtc,
            entity.ConcurrencyToken);
    }

    private static partial class Log
    {
        [LoggerMessage(7401, LogLevel.Information,
            "Deployment location operation completed: Operation={Operation}, Outcome={Outcome}, Reason={Reason}, SourceKind={SourceKind}, Status={Status}, VersionRelation={VersionRelation}")]
        internal static partial void OperationCompleted(
            ILogger logger,
            string operation,
            string outcome,
            string reason,
            string sourceKind,
            string status,
            string versionRelation);

        [LoggerMessage(7402, LogLevel.Information,
            "Deployment location reconciliation completed: Outcome={Outcome}, Reason={Reason}, SourceKind={SourceKind}, VersionRelation={VersionRelation}, CaptureCount={CaptureCount}")]
        internal static partial void ReconciliationCompleted(
            ILogger logger,
            string outcome,
            string reason,
            string sourceKind,
            string versionRelation,
            int captureCount);

        [LoggerMessage(7406, LogLevel.Error,
            "Deployment location reconciliation failed: Reason={Reason}, SourceKind={SourceKind}, VersionRelation={VersionRelation}")]
        internal static partial void ReconciliationFailed(
            ILogger logger,
            Exception exception,
            string reason,
            string sourceKind,
            string versionRelation);
    }
}

internal sealed record DeploymentLocationProposal(
    Guid Id,
    Guid RegistrationId,
    string DeviceId,
    string FriendlyName,
    Guid ObservatoryId,
    string ObservatoryName,
    ObservatoryLocationSnapshot ObservatoryLocation,
    DeploymentLocationSnapshot DeploymentLocation,
    DeploymentLocationSourceKind SourceKind,
    DeploymentLocationResolutionStatus Status,
    string? ReasonCode,
    DateTimeOffset ProposedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    Guid ConcurrencyToken);

internal sealed record DeploymentLocationProposalPage(
    IReadOnlyList<DeploymentLocationProposal> Proposals,
    DeploymentLocationProposalCursor? NextCursor);

internal sealed record DeploymentLocationProposalCursor(DateTimeOffset ProposedAtUtc, Guid Id);

internal sealed record DeploymentLocationResolutionResult(
    DeploymentLocationMutationStatus Status,
    DeploymentLocationProposal? Proposal);

internal enum DeploymentLocationMutationStatus
{
    Applied,
    NotFound,
    PreconditionFailed,
    InvalidTransition,
    StaleAuthority
}

internal static class DeploymentLocationEtag
{
    public static string Create(Guid concurrencyToken)
        => $"\"{WebEncoders.Base64UrlEncode(concurrencyToken.ToByteArray())}\"";

    public static bool TryParse(string value, out Guid concurrencyToken)
    {
        concurrencyToken = Guid.Empty;
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' || value.StartsWith("W/", StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(value[1..^1]);
            if (bytes.Length != 16)
            {
                return false;
            }
            concurrencyToken = new Guid(bytes);
            return concurrencyToken != Guid.Empty;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
