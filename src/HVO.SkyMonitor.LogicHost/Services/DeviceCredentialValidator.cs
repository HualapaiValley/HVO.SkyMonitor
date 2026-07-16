using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

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

        var computedHash = DeviceRegistrationService.ComputeSha256(deviceKey);
        var registration = await dbContext.DeviceRegistrations
            .Where(reg => reg.DeviceId == deviceId && reg.Status == DeviceRegistrationStatus.Active)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (registration is null)
        {
            var credentialOwner = await FindCredentialOwnerAsync(computedHash, cancellationToken).ConfigureAwait(false);
            throw new DeviceRegistrationException(
                "Device is not registered or not active.",
                credentialOwner is null ? "registration-invalid" : "cross-agent-credential",
                credentialOwnerRegistrationId: credentialOwner);
        }

        if (!FixedTimeEquals(computedHash, registration.DeviceKeyHash))
        {
            var credentialOwner = await FindCredentialOwnerAsync(computedHash, cancellationToken).ConfigureAwait(false);
            throw new DeviceRegistrationException(
                "Device credentials are invalid.",
                credentialOwner is null ? "invalid-credential" : "cross-agent-credential",
                registration.Id,
                credentialOwner);
        }

        var now = timeProvider.GetUtcNow();
        if (registration.ExpiresAtUtc is { } expiresAt && expiresAt <= now)
        {
            throw new DeviceRegistrationException("Device credentials have expired. Re-run bootstrap.");
        }

        return registration;
    }

    private Task<Guid?> FindCredentialOwnerAsync(string computedHash, CancellationToken cancellationToken)
        => dbContext.DeviceRegistrations
            .AsNoTracking()
            .Where(candidate => candidate.Status == DeviceRegistrationStatus.Active && candidate.DeviceKeyHash == computedHash)
            .OrderBy(candidate => candidate.Id)
            .Select(static candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static bool FixedTimeEquals(string computedHash, string? storedHash)
    {
        if (storedHash is null || computedHash.Length != storedHash.Length || !storedHash.All(Uri.IsHexDigit))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(computedHash),
            Convert.FromHexString(storedHash));
    }
}
