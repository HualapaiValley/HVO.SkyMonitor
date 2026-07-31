using System.Data;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum LogicalCameraMutationOutcome
{
    Applied,
    Unchanged,
    NotFoundOrDenied,
    Conflict,
    Invalid
}

internal sealed record LogicalCameraMutationResult(
    LogicalCameraMutationOutcome Outcome,
    Guid? LogicalCameraId = null,
    Guid? InstallationId = null);

internal interface ILogicalCameraService
{
    Task<LogicalCameraMutationResult> CreateAsync(
        Guid observatoryId,
        string actorUserId,
        string slug,
        string name,
        string description,
        CancellationToken cancellationToken = default);

    Task<LogicalCameraMutationResult> AssignInstallationAsync(
        Guid logicalCameraId,
        Guid registrationId,
        string actorUserId,
        string reasonCode,
        CancellationToken cancellationToken = default);
}

internal sealed partial class LogicalCameraService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<LogicalCameraService>? logger = null) : ILogicalCameraService
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    public async Task<LogicalCameraMutationResult> CreateAsync(
        Guid observatoryId,
        string actorUserId,
        string slug,
        string name,
        string description,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        slug = slug.Trim().ToLowerInvariant();
        name = name.Trim();
        description = description.Trim();
        if (slug.Length is < 1 or > 128 || !SlugPattern().IsMatch(slug)
            || name.Length is < 1 or > 200 || description.Length > 2000)
        {
            return new(LogicalCameraMutationOutcome.Invalid);
        }
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var observatoryExists = await (isRelational
                ? dbContext.Observatories.FromSqlInterpolated($"""
                    SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {observatoryId}
                    """)
                : dbContext.Observatories.Where(item => item.Id == observatoryId))
            .AnyAsync(item => item.IsActive, cancellationToken).ConfigureAwait(false);
        if (!observatoryExists
            || !await ObservatoryMembershipAccess.ForOwner(dbContext, actorUserId)
                .AnyAsync(item => item.ObservatoryId == observatoryId, cancellationToken).ConfigureAwait(false))
        {
            return new(LogicalCameraMutationOutcome.NotFoundOrDenied);
        }
        var existing = await dbContext.LogicalCameras.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ObservatoryId == observatoryId && item.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return string.Equals(existing.Name, name, StringComparison.Ordinal)
                && string.Equals(existing.Description, description, StringComparison.Ordinal)
                ? new(LogicalCameraMutationOutcome.Unchanged, existing.Id)
                : new(LogicalCameraMutationOutcome.Conflict, existing.Id);
        }

        var camera = new LogicalCamera
        {
            ObservatoryId = observatoryId,
            Slug = slug,
            Name = name,
            Description = description,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            CreatedByUserId = actorUserId
        };
        dbContext.LogicalCameras.Add(camera);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (logger is not null) OperatorUiAuditLog.Installation(logger, "logical-camera-create", "applied");
        return new(LogicalCameraMutationOutcome.Applied, camera.Id);
    }

    public async Task<LogicalCameraMutationResult> AssignInstallationAsync(
        Guid logicalCameraId,
        Guid registrationId,
        string actorUserId,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        reasonCode = reasonCode.Trim();
        if (reasonCode.Length is < 1 or > 128)
        {
            return new(LogicalCameraMutationOutcome.Invalid);
        }
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var camera = await (isRelational
                ? dbContext.LogicalCameras.FromSqlInterpolated($"""
                    SELECT * FROM [LogicalCameras] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {logicalCameraId}
                    """)
                : dbContext.LogicalCameras.Where(item => item.Id == logicalCameraId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (camera is null || camera.DeactivatedAtUtc is not null
            || !await HasOwnerAuthorityAsync(camera.ObservatoryId, actorUserId, cancellationToken).ConfigureAwait(false))
        {
            return new(LogicalCameraMutationOutcome.NotFoundOrDenied);
        }
        var registration = await (isRelational
                ? dbContext.DeviceRegistrations.FromSqlInterpolated($"""
                    SELECT * FROM [DeviceRegistrations] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {registrationId}
                    """)
                : dbContext.DeviceRegistrations.Where(item => item.Id == registrationId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (registration is null || registration.ObservatoryId != camera.ObservatoryId
            || registration.Status != DeviceRegistrationStatus.Active || registration.DevicePublicId is null)
        {
            return new(LogicalCameraMutationOutcome.NotFoundOrDenied);
        }
        var priorAssignment = await dbContext.LogicalCameraInstallations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RegistrationId == registrationId, cancellationToken)
            .ConfigureAwait(false);
        if (priorAssignment is not null)
        {
            return priorAssignment.LogicalCameraId == logicalCameraId && priorAssignment.RetiredAtUtc is null
                ? new(LogicalCameraMutationOutcome.Unchanged, logicalCameraId, priorAssignment.Id)
                : new(LogicalCameraMutationOutcome.Conflict, priorAssignment.LogicalCameraId, priorAssignment.Id);
        }
        var current = await (isRelational
                ? dbContext.LogicalCameraInstallations.FromSqlInterpolated($"""
                    SELECT * FROM [LogicalCameraInstallations] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [LogicalCameraId] = {logicalCameraId} AND [RetiredAtUtc] IS NULL
                    """)
                : dbContext.LogicalCameraInstallations.Where(item =>
                    item.LogicalCameraId == logicalCameraId && item.RetiredAtUtc == null))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (current is not null)
        {
            current.RetiredAtUtc = now;
            current.RetiredByUserId = actorUserId;
            current.RetirementReasonCode = "installation-replaced";
        }
        var installation = new LogicalCameraInstallation
        {
            LogicalCameraId = logicalCameraId,
            RegistrationId = registrationId,
            InstallationPublicId = registration.DevicePublicId.Value,
            AssignedAtUtc = now,
            ReplacesInstallationId = current?.Id,
            AssignedByUserId = actorUserId,
            AssignmentReasonCode = reasonCode
        };
        dbContext.LogicalCameraInstallations.Add(installation);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (logger is not null) OperatorUiAuditLog.Installation(logger, "installation-assign", "applied");
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(LogicalCameraMutationOutcome.Applied, logicalCameraId, installation.Id);
    }

    private async Task<bool> HasOwnerAuthorityAsync(
        Guid observatoryId,
        string actorUserId,
        CancellationToken cancellationToken)
        => await dbContext.Observatories.AnyAsync(item =>
                item.Id == observatoryId && item.IsActive, cancellationToken).ConfigureAwait(false)
            && await ObservatoryMembershipAccess.ForOwner(dbContext, actorUserId)
                .AnyAsync(item => item.ObservatoryId == observatoryId, cancellationToken).ConfigureAwait(false);
}
