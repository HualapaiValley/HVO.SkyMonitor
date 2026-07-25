using System.Data;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IObservatoryService
{
    Task<IReadOnlyList<ObservatorySummary>> GetObservatoriesAsync(string ownerUserId, CancellationToken cancellationToken = default);

    Task<Observatory> CreateOrUpdateAsync(ObservatoryUpsertRequest request, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, string ownerUserId, CancellationToken cancellationToken = default);
}

internal sealed record ObservatoryUpsertRequest(
    Guid? Id,
    string OwnerUserId,
    string Name,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationMeters,
    string TimeZoneId,
    bool IsActive,
    double? AllowedDeploymentRadiusMeters = null,
    string? ExpectedRepresentationSha256 = null);

internal sealed class ObservatoryConcurrencyException : Exception
{
    public ObservatoryConcurrencyException()
        : base("The Observatory location changed after it was read.")
    {
    }

    public ObservatoryConcurrencyException(string message)
        : base(message)
    {
    }

    public ObservatoryConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed partial class ObservatoryService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    IDeploymentLocationAuthorityService deploymentLocationAuthority,
    ILogger<ObservatoryService>? logger = null) : IObservatoryService
{
    public async Task<IReadOnlyList<ObservatorySummary>> GetObservatoriesAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);

        return await dbContext.Observatories
            .Where(o => o.OwnerUserId == ownerUserId)
            .OrderBy(o => o.Name)
            .Select(o => new ObservatorySummary(
                o.Id,
                o.Name,
                o.LatitudeDegrees,
                o.LongitudeDegrees,
                o.ElevationMeters,
                o.TimeZoneId,
                o.AllowedDeploymentRadiusMeters,
                o.CurrentLocationVersion,
                o.CurrentLocationCanonicalSha256,
                o.IsActive,
                o.CreatedAtUtc,
                o.UpdatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Observatory> CreateOrUpdateAsync(ObservatoryUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TimeZoneId);
        var normalizedTimeZone = request.TimeZoneId.Trim();
        var candidate = AgentCore.ObservatoryLocationSnapshot.Create(
            request.Id ?? Guid.NewGuid(),
            1,
            timeProvider.GetUtcNow(),
            request.LatitudeDegrees,
            request.LongitudeDegrees,
            request.ElevationMeters,
            normalizedTimeZone,
            request.AllowedDeploymentRadiusMeters);
        var validation = candidate.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Observatory location is invalid: {validation.ReasonCode} ({validation.FieldPath}).",
                nameof(request));
        }

        var now = timeProvider.GetUtcNow();
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        Observatory entity;

        if (request.Id is { } existingId)
        {
            IQueryable<Observatory> query = isRelational
                ? dbContext.Observatories.FromSqlInterpolated($"""
                    SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {existingId} AND [OwnerUserId] = {request.OwnerUserId}
                    """)
                : dbContext.Observatories.Where(o =>
                    o.Id == existingId && o.OwnerUserId == request.OwnerUserId);
            entity = await query
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Observatory not found or access denied.");
            if (request.ExpectedRepresentationSha256 is { } expected
                && !string.Equals(
                    expected,
                    CreateRepresentationSha256(entity),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ObservatoryConcurrencyException();
            }
        }
        else
        {
            entity = new Observatory
            {
                OwnerUserId = request.OwnerUserId,
                CreatedAtUtc = now,
                LatitudeDegrees = request.LatitudeDegrees,
                LongitudeDegrees = request.LongitudeDegrees,
                ElevationMeters = request.ElevationMeters,
                TimeZoneId = normalizedTimeZone,
                AllowedDeploymentRadiusMeters = request.AllowedDeploymentRadiusMeters
            };
            await dbContext.Observatories.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        entity.Name = request.Name.Trim();
        var previousLocationVersion = entity.CurrentLocationVersion;
        var appliedLocation = await ObservatoryLocationAuthority.ApplyAsync(
            dbContext,
            entity,
            request.LatitudeDegrees,
            request.LongitudeDegrees,
            request.ElevationMeters,
            normalizedTimeZone,
            request.AllowedDeploymentRadiusMeters,
            now,
            request.OwnerUserId,
            cancellationToken).ConfigureAwait(false);
        if (previousLocationVersion.HasValue && previousLocationVersion != appliedLocation.Version)
        {
            if (logger is not null)
            {
                Log.LocationVersionCreated(logger, appliedLocation.Version, "owner-update");
            }
            var deployments = await dbContext.DeviceDeploymentLocationVersions
                .Include(item => item.Registration)
                .Where(item => item.ObservatoryId == entity.Id
                    && item.ObservatoryLocationVersionNumber == previousLocationVersion)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var currentDeployments = deployments
                .GroupBy(item => item.RegistrationId)
                .Select(group => SelectMostRecent(group.Where(item =>
                        item.Status == DeploymentLocationResolutionStatus.Acknowledged))
                    ?? SelectMostRecent(group)!)
                .ToArray();
            foreach (var deployment in deployments)
            {
                if (deployment.Status == DeploymentLocationResolutionStatus.Pending)
                {
                    deployment.Status = DeploymentLocationResolutionStatus.Rejected;
                    deployment.ReasonCode = "observatory-version-superseded";
                    deployment.ResolvedAtUtc = now;
                    deployment.ResolvedByUserId = request.OwnerUserId;
                    deployment.ConcurrencyToken = Guid.NewGuid();
                    dbContext.DeploymentLocationResolutionAudits.Add(new DeploymentLocationResolutionAudit
                    {
                        DeploymentLocation = deployment,
                        DeviceDeploymentLocationVersionId = deployment.Id,
                        RegistrationId = deployment.RegistrationId,
                        PreviousStatus = DeploymentLocationResolutionStatus.Pending,
                        NewStatus = DeploymentLocationResolutionStatus.Rejected,
                        ActorUserId = request.OwnerUserId,
                        Reason = "observatory-version-superseded",
                        OccurredAtUtc = now
                    });
                }
            }
            foreach (var deployment in currentDeployments)
            {
                var evaluation = new DeviceDeploymentLocationVersion
                {
                    Registration = deployment.Registration,
                    RegistrationId = deployment.RegistrationId,
                    DevicePublicId = deployment.DevicePublicId,
                    ObservatoryId = deployment.ObservatoryId,
                    ObservatoryLocationVersion = appliedLocation,
                    ObservatoryLocationVersionId = appliedLocation.Id,
                    ObservatoryLocationVersionNumber = appliedLocation.Version,
                    ObservatoryLocationCanonicalSha256 = appliedLocation.CanonicalSha256,
                    LocationId = deployment.LocationId,
                    Version = deployment.Version,
                    CanonicalSha256 = deployment.CanonicalSha256,
                    Source = deployment.Source,
                    SourceKind = deployment.SourceKind,
                    HorizontalAccuracyMeters = deployment.HorizontalAccuracyMeters,
                    EffectiveFromUtc = deployment.EffectiveFromUtc,
                    EffectiveUntilUtc = deployment.EffectiveUntilUtc,
                    LatitudeDegrees = deployment.LatitudeDegrees,
                    LongitudeDegrees = deployment.LongitudeDegrees,
                    ElevationMeters = deployment.ElevationMeters,
                    TimeZoneId = deployment.TimeZoneId,
                    Status = DeploymentLocationResolutionStatus.Pending,
                    ReasonCode = "observatory-location-changed",
                    ProposedAtUtc = now
                };
                dbContext.DeviceDeploymentLocationVersions.Add(evaluation);
                deployment.Registration!.LocationEvidenceState = RegistrationLocationEvidenceState.DeploymentPending;
                dbContext.DeploymentLocationResolutionAudits.Add(new DeploymentLocationResolutionAudit
                {
                    DeploymentLocation = evaluation,
                    DeviceDeploymentLocationVersionId = evaluation.Id,
                    RegistrationId = deployment.RegistrationId,
                    PreviousStatus = null,
                    NewStatus = DeploymentLocationResolutionStatus.Pending,
                    ActorUserId = request.OwnerUserId,
                    Reason = "observatory-location-changed",
                    OccurredAtUtc = now
                });
                await deploymentLocationAuthority.ReconcileAsync(evaluation, cancellationToken).ConfigureAwait(false);
            }
        }
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return entity;
    }

    internal static string CreateRepresentationSha256(Observatory observatory)
        => CreateRepresentationSha256(
            observatory.Id,
            observatory.Name,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId,
            observatory.AllowedDeploymentRadiusMeters,
            observatory.CurrentLocationVersion,
            observatory.CurrentLocationCanonicalSha256,
            observatory.IsActive);

    internal static string CreateRepresentationSha256(ObservatorySummary observatory)
        => CreateRepresentationSha256(
            observatory.Id,
            observatory.Name,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId,
            observatory.AllowedDeploymentRadiusMeters,
            observatory.CurrentLocationVersion,
            observatory.CurrentLocationCanonicalSha256,
            observatory.IsActive);

    private static string CreateRepresentationSha256(
        Guid id,
        string name,
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters,
        string timeZoneId,
        double? allowedDeploymentRadiusMeters,
        long? currentLocationVersion,
        string? currentLocationCanonicalSha256,
        bool isActive)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Id = id,
            Name = name,
            LatitudeDegrees = latitudeDegrees,
            LongitudeDegrees = longitudeDegrees,
            ElevationMeters = elevationMeters,
            TimeZoneId = timeZoneId,
            AllowedDeploymentRadiusMeters = allowedDeploymentRadiusMeters,
            CurrentLocationVersion = currentLocationVersion,
            CurrentLocationCanonicalSha256 = currentLocationCanonicalSha256,
            IsActive = isActive
        });

    private static DeviceDeploymentLocationVersion? SelectMostRecent(
        IEnumerable<DeviceDeploymentLocationVersion> deployments)
        => deployments.Aggregate<DeviceDeploymentLocationVersion, DeviceDeploymentLocationVersion?>(
            null,
            (current, candidate) => current is null
                || DeploymentLocationAuthorityService.CompareRecency(candidate, current) > 0
                    ? candidate
                    : current);

    private static partial class Log
    {
        [LoggerMessage(7403, LogLevel.Information,
            "Observatory location version created: Version={Version}, Reason={Reason}")]
        internal static partial void LocationVersionCreated(ILogger logger, long version, string reason);
    }

    public async Task<bool> DeleteAsync(Guid id, string ownerUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);

        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        IQueryable<Observatory> observatoryQuery = isRelational
            ? dbContext.Observatories.FromSqlInterpolated($"""
                SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {id} AND [OwnerUserId] = {ownerUserId}
                """)
            : dbContext.Observatories.Where(observatory =>
                observatory.Id == id && observatory.OwnerUserId == ownerUserId);
        var entity = await observatoryQuery
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        var now = timeProvider.GetUtcNow();
        IQueryable<DeviceRegistration> registrationQuery = isRelational
            ? dbContext.DeviceRegistrations.FromSqlInterpolated($"""
                SELECT * FROM [DeviceRegistrations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ObservatoryId] = {id}
                """)
            : dbContext.DeviceRegistrations.Where(registration => registration.ObservatoryId == id);
        var registrations = await registrationQuery
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        IQueryable<EnvironmentalObservationSourceRecord> environmentalSourceQuery = isRelational
            ? dbContext.EnvironmentalObservationSources.FromSqlInterpolated($"""
                SELECT * FROM [EnvironmentalObservationSources] WITH (UPDLOCK, HOLDLOCK)
                WHERE [SiteId] = {id}
                """)
            : dbContext.EnvironmentalObservationSources.Where(source => source.SiteId == id);
        var hasEnvironmentalEvidence = await environmentalSourceQuery
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (registrations.Count == 0 && !hasEnvironmentalEvidence)
        {
            var locationVersions = await dbContext.ObservatoryLocationVersions
                .Where(version => version.ObservatoryId == id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ObservatoryLocationVersions.RemoveRange(locationVersions);
            dbContext.Observatories.Remove(entity);
        }
        else
        {
            // Device registrations and exact historical rig profiles are retained as ingest evidence.
            entity.IsActive = false;
            entity.UpdatedAtUtc = now;
            foreach (var registration in registrations)
            {
                registration.Status = DeviceRegistrationStatus.Revoked;
                registration.ExpiresAtUtc = now;
                registration.RevokedReason ??= "Observatory deactivated by its owner.";
                registration.DeviceKeyHash = null;
                registration.RegistrationTokenHash = null;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }
}
