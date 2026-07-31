using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceRegistrationReadService
{
    Task<IReadOnlyList<DeviceRegistrationSummary>> GetRegistrationsAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default);
}

internal sealed record DeviceRegistrationSummary(
    Guid RegistrationId,
    string DeviceId,
    Guid ObservatoryId,
    string FriendlyName,
    string ObservatoryName,
    double ObservatoryLatitudeDegrees,
    double ObservatoryLongitudeDegrees,
    double ObservatoryElevationMeters,
    string ObservatoryTimeZoneId,
    string OwnerUserId,
    string OwnerDisplayName,
    string? OwnerEmail,
    DateTimeOffset? OwnerConfirmedAtUtc,
    string OwnerConfirmationMethod,
    DeviceRegistrationStatus Status,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset? ActivatedAtUtc,
    int? CurrentRigProfileVersion,
    string? CurrentRigProfileHash,
    DateTimeOffset? CurrentRigProfileUpdatedAtUtc);

internal sealed class DeviceRegistrationReadService(ApplicationDbContext dbContext) : IDeviceRegistrationReadService
{
    public async Task<IReadOnlyList<DeviceRegistrationSummary>> GetRegistrationsAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);

        var ownedObservatories = ObservatoryMembershipAccess.ForOwner(dbContext, ownerUserId)
            .Select(membership => membership.ObservatoryId);
        return await dbContext.DeviceRegistrations
            .Where(registration => ownedObservatories.Contains(registration.ObservatoryId))
            .OrderByDescending(registration => registration.IssuedAtUtc)
            .Select(registration => new DeviceRegistrationSummary(
                registration.Id,
                registration.DeviceId,
                registration.ObservatoryId,
                registration.FriendlyName,
                registration.ObservatoryName,
                registration.ObservatoryLatitudeDegrees,
                registration.ObservatoryLongitudeDegrees,
                registration.ObservatoryElevationMeters,
                registration.ObservatoryTimeZoneId,
                registration.OwnerUserId,
                registration.OwnerDisplayName,
                registration.OwnerEmail,
                registration.OwnerConfirmedAtUtc,
                registration.OwnerConfirmationMethod,
                registration.Status,
                registration.IssuedAtUtc,
                registration.ExpiresAtUtc,
                registration.LastSeenUtc,
                registration.ActivatedAtUtc,
                registration.CurrentRigProfileVersion,
                registration.CurrentRigProfileHash,
                registration.CurrentRigProfileUpdatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
