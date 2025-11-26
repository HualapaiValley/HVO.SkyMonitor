using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceCredentialValidator
{
    Task<DeviceRegistration> ValidateAsync(string deviceId, string deviceKey, CancellationToken cancellationToken = default);
}

internal sealed class DeviceCredentialValidator(ApplicationDbContext dbContext, TimeProvider timeProvider) : IDeviceCredentialValidator
{
    public async Task<DeviceRegistration> ValidateAsync(string deviceId, string deviceKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        var registration = await dbContext.DeviceRegistrations
            .Where(reg => reg.DeviceId == deviceId && reg.Status == DeviceRegistrationStatus.Active)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DeviceRegistrationException("Device is not registered or not active.");

        var computedHash = DeviceRegistrationService.ComputeSha256(deviceKey);
        if (!string.Equals(computedHash, registration.DeviceKeyHash, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("Device credentials are invalid.");
        }

        var now = timeProvider.GetUtcNow();
        if (registration.ExpiresAtUtc is { } expiresAt && expiresAt <= now)
        {
            throw new DeviceRegistrationException("Device credentials have expired. Re-run bootstrap.");
        }

        return registration;
    }
}
