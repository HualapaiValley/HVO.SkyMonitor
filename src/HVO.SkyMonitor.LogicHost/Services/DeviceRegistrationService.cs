using System.Data;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceRegistrationService
{
    Task<DeviceRegistration> CreatePendingAsync(DeviceRegistrationCreateRequest request, CancellationToken cancellationToken = default);

    Task<DeviceRegistration> RevokeAsync(DeviceRegistrationRevokeRequest request, CancellationToken cancellationToken = default);
}

internal sealed record DeviceRegistrationCreateRequest(
    string DeviceId,
    string VerificationCode,
    Guid ObservatoryId,
    string FriendlyName,
    string OwnerUserId,
    string OwnerDisplayName,
    string? OwnerEmail,
    string OwnerConfirmationMethod,
    string? OwnerConfirmationNotes,
    TimeSpan? PendingLifetime = null);

internal sealed record DeviceRegistrationRevokeRequest(
    Guid RegistrationId,
    string DeviceId,
    string OwnerUserId,
    string OwnerDisplayName,
    string OwnerConfirmationMethod,
    string? RevocationNotes = null);

internal sealed class DeviceRegistrationService(ApplicationDbContext dbContext, TimeProvider timeProvider) : IDeviceRegistrationService
{
    private static readonly TimeSpan DefaultPendingLifetime = TimeSpan.FromMinutes(15);

    public async Task<DeviceRegistration> CreatePendingAsync(DeviceRegistrationCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerConfirmationMethod);

        var now = timeProvider.GetUtcNow();
        var expiresAt = now + (request.PendingLifetime ?? DefaultPendingLifetime);
        var verificationHash = ComputeSha256(request.VerificationCode);
        var trimmedFriendlyName = request.FriendlyName.Trim();
        if (string.IsNullOrEmpty(trimmedFriendlyName))
        {
            throw new InvalidOperationException("Friendly name is required.");
        }

        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        IQueryable<Observatory> observatoryQuery = isRelational
            ? dbContext.Observatories.FromSqlInterpolated($"""
                SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {request.ObservatoryId}
                """)
            : dbContext.Observatories.Where(observatory => observatory.Id == request.ObservatoryId);
        var observatoryEntity = await observatoryQuery
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DeviceRegistrationException(
                "Device registration not found or access denied.",
                DeviceRegistrationException.NotFoundReasonCode);

        if (!string.Equals(observatoryEntity.OwnerUserId, request.OwnerUserId, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException(
                "Device registration not found or access denied.",
                DeviceRegistrationException.NotFoundReasonCode);
        }

        if (!observatoryEntity.IsActive)
        {
            throw new InvalidOperationException("Observatory must be active to register devices.");
        }
        var observatoryLocation = await ObservatoryLocationAuthority.EnsureCurrentVersionAsync(
            dbContext,
            observatoryEntity,
            now,
            request.OwnerUserId,
            cancellationToken).ConfigureAwait(false);
        var observatory = new ObservatorySnapshot(
            observatoryEntity.Id,
            observatoryEntity.OwnerUserId,
            observatoryEntity.Name,
            observatoryEntity.LatitudeDegrees,
            observatoryEntity.LongitudeDegrees,
            observatoryEntity.ElevationMeters,
            observatoryEntity.TimeZoneId,
            observatoryEntity.IsActive,
            observatoryLocation.Version,
            observatoryLocation.CanonicalSha256);

        IQueryable<DeviceRegistration> registrationQuery = isRelational
            ? dbContext.DeviceRegistrations.FromSqlInterpolated($"""
                SELECT * FROM [DeviceRegistrations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [DeviceId] = {request.DeviceId} AND [Status] = {nameof(DeviceRegistrationStatus.Pending)}
                """)
            : dbContext.DeviceRegistrations.Where(registration =>
                registration.DeviceId == request.DeviceId && registration.Status == DeviceRegistrationStatus.Pending);
        var existingRegistrations = await registrationQuery
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existingRegistrations.Any(registration =>
                !string.Equals(registration.OwnerUserId, request.OwnerUserId, StringComparison.Ordinal)))
        {
            throw new DeviceRegistrationException(
                "Device registration not found or access denied.",
                DeviceRegistrationException.NotFoundReasonCode);
        }

        var existing = existingRegistrations.FirstOrDefault();

        if (existing is not null)
        {
            ApplySnapshot(existing, trimmedFriendlyName, request, observatory, now);
            existing.VerificationCodeHash = verificationHash;
            existing.DevicePublicId = null;
            existing.DeviceKeyHash = null;
            existing.RegistrationTokenHash = null;
            existing.ActivatedAtUtc = null;
            existing.IssuedAtUtc = now;
            existing.ExpiresAtUtc = expiresAt;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return existing;
        }

        var registration = new DeviceRegistration
        {
            DeviceId = request.DeviceId,
            Status = DeviceRegistrationStatus.Pending,
            VerificationCodeHash = verificationHash,
            IssuedAtUtc = now,
            ExpiresAtUtc = expiresAt
        };

        ApplySnapshot(registration, trimmedFriendlyName, request, observatory, now);

        await dbContext.DeviceRegistrations.AddAsync(registration, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return registration;
    }

    internal static string ComputeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes);
    }

    public async Task<DeviceRegistration> RevokeAsync(DeviceRegistrationRevokeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerConfirmationMethod);

        var registration = await dbContext.DeviceRegistrations
            .Where(reg => reg.Id == request.RegistrationId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DeviceRegistrationException("Device registration not found.");

        if (!string.Equals(registration.OwnerUserId, request.OwnerUserId, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("Access denied for the specified registration.");
        }

        if (!string.Equals(registration.DeviceId, request.DeviceId, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("Device identifier mismatch.");
        }

        if (registration.Status == DeviceRegistrationStatus.Revoked)
        {
            return registration;
        }

        var now = timeProvider.GetUtcNow();
        registration.Status = DeviceRegistrationStatus.Revoked;
        registration.ExpiresAtUtc = now;
        registration.RevokedReason = string.IsNullOrWhiteSpace(request.RevocationNotes)
            ? $"Revoked via portal by {request.OwnerDisplayName}"
            : request.RevocationNotes.Trim();
        registration.OwnerConfirmationMethod = request.OwnerConfirmationMethod;
        registration.OwnerConfirmedAtUtc ??= now;
        registration.OwnerConfirmationNotes = string.IsNullOrWhiteSpace(request.RevocationNotes)
            ? registration.OwnerConfirmationNotes
            : request.RevocationNotes.Trim();
        registration.DeviceKeyHash = null;
        registration.RegistrationTokenHash = null;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return registration;
    }

    private static void ApplySnapshot(
        DeviceRegistration registration,
        string friendlyName,
        DeviceRegistrationCreateRequest request,
        ObservatorySnapshot observatory,
        DateTimeOffset now)
    {
        registration.ObservatoryId = observatory.Id;
        registration.FriendlyName = friendlyName;
        registration.ObservatoryName = observatory.Name;
        registration.ObservatoryLatitudeDegrees = observatory.LatitudeDegrees;
        registration.ObservatoryLongitudeDegrees = observatory.LongitudeDegrees;
        registration.ObservatoryElevationMeters = observatory.ElevationMeters;
        registration.ObservatoryTimeZoneId = observatory.TimeZoneId;
        registration.ObservatoryLocationVersion = observatory.LocationVersion;
        registration.ObservatoryLocationCanonicalSha256 = observatory.LocationCanonicalSha256;
        registration.LocationEvidenceState = RegistrationLocationEvidenceState.ObservatoryPinned;
        registration.EnvelopeVersion = "v2";
        registration.OwnerUserId = request.OwnerUserId;
        registration.OwnerDisplayName = request.OwnerDisplayName.Trim();
        registration.OwnerEmail = string.IsNullOrWhiteSpace(request.OwnerEmail)
            ? null
            : request.OwnerEmail.Trim();
        registration.OwnerConfirmationMethod = request.OwnerConfirmationMethod;
        registration.OwnerConfirmationNotes = string.IsNullOrWhiteSpace(request.OwnerConfirmationNotes)
            ? null
            : request.OwnerConfirmationNotes.Trim();
        registration.OwnerConfirmedAtUtc = now;
    }

    private sealed record ObservatorySnapshot(
        Guid Id,
        string OwnerUserId,
        string Name,
        double LatitudeDegrees,
        double LongitudeDegrees,
        double ElevationMeters,
        string TimeZoneId,
        bool IsActive,
        long LocationVersion,
        string LocationCanonicalSha256);
}
