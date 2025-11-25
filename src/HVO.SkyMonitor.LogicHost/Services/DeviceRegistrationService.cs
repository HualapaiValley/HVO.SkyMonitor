using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceRegistrationService
{
    Task<DeviceRegistration> CreatePendingAsync(DeviceRegistrationCreateRequest request, CancellationToken cancellationToken = default);
}

internal sealed record DeviceRegistrationCreateRequest(
    string DeviceId,
    string VerificationCode,
    Guid ObservatoryId,
    string FriendlyName,
    TimeSpan? PendingLifetime = null);

internal sealed class DeviceRegistrationService(ApplicationDbContext dbContext, TimeProvider timeProvider) : IDeviceRegistrationService
{
    private static readonly TimeSpan DefaultPendingLifetime = TimeSpan.FromMinutes(15);

    public async Task<DeviceRegistration> CreatePendingAsync(DeviceRegistrationCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = timeProvider.GetUtcNow();
        var expiresAt = now + (request.PendingLifetime ?? DefaultPendingLifetime);
        var verificationHash = ComputeSha256(request.VerificationCode);

        var existing = await dbContext.DeviceRegistrations
            .Where(registration => registration.DeviceId == request.DeviceId && registration.Status == DeviceRegistrationStatus.Pending)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.ObservatoryId = request.ObservatoryId;
            existing.FriendlyName = request.FriendlyName;
            existing.VerificationCodeHash = verificationHash;
            existing.DevicePublicId = null;
            existing.DeviceKeyHash = null;
            existing.RegistrationTokenHash = null;
            existing.ActivatedAtUtc = null;
            existing.IssuedAtUtc = now;
            existing.ExpiresAtUtc = expiresAt;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var registration = new DeviceRegistration
        {
            DeviceId = request.DeviceId,
            ObservatoryId = request.ObservatoryId,
            FriendlyName = request.FriendlyName,
            Status = DeviceRegistrationStatus.Pending,
            VerificationCodeHash = verificationHash,
            IssuedAtUtc = now,
            ExpiresAtUtc = expiresAt
        };

        await dbContext.DeviceRegistrations.AddAsync(registration, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return registration;
    }

    internal static string ComputeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes);
    }
}
