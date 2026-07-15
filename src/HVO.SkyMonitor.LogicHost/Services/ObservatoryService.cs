using System.Data;
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
    bool IsActive);

internal sealed class ObservatoryService(ApplicationDbContext dbContext, TimeProvider timeProvider) : IObservatoryService
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

        var now = timeProvider.GetUtcNow();
        Observatory entity;

        if (request.Id is { } existingId)
        {
            entity = await dbContext.Observatories
                .Where(o => o.Id == existingId && o.OwnerUserId == request.OwnerUserId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Observatory not found or access denied.");
        }
        else
        {
            entity = new Observatory
            {
                OwnerUserId = request.OwnerUserId,
                CreatedAtUtc = now
            };
            await dbContext.Observatories.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        entity.Name = request.Name.Trim();
        entity.LatitudeDegrees = request.LatitudeDegrees;
        entity.LongitudeDegrees = request.LongitudeDegrees;
        entity.ElevationMeters = request.ElevationMeters;
        entity.TimeZoneId = request.TimeZoneId.Trim();
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity;
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
        if (registrations.Count == 0)
        {
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
