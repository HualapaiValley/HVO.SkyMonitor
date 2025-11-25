using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceRegistrationReadService
{
    Task<IReadOnlyList<DeviceRegistrationSummary>> GetRegistrationsAsync(CancellationToken cancellationToken = default);
}

internal sealed record DeviceRegistrationSummary(
    Guid RegistrationId,
    string DeviceId,
    Guid ObservatoryId,
    string FriendlyName,
    DeviceRegistrationStatus Status,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset? ActivatedAtUtc);

internal sealed class DeviceRegistrationReadService(ApplicationDbContext dbContext) : IDeviceRegistrationReadService
{
    public async Task<IReadOnlyList<DeviceRegistrationSummary>> GetRegistrationsAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.DeviceRegistrations
            .OrderByDescending(registration => registration.IssuedAtUtc)
            .Select(registration => new DeviceRegistrationSummary(
                registration.Id,
                registration.DeviceId,
                registration.ObservatoryId,
                registration.FriendlyName,
                registration.Status,
                registration.IssuedAtUtc,
                registration.ExpiresAtUtc,
                registration.LastSeenUtc,
                registration.ActivatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
