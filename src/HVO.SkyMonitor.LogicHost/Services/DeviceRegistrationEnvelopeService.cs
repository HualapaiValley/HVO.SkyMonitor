using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceRegistrationEnvelopeService
{
    Task<DeviceRegistrationEnvelopeResponse> CreateEnvelopeAsync(DeviceRegistrationEnvelopeRequest request, CancellationToken cancellationToken = default);
}

internal sealed record DeviceRegistrationEnvelopeRequest(
    Guid RegistrationId,
    string DeviceId,
    Guid ObservatoryId,
    string OwnerUserId,
    TimeSpan? EnvelopeLifetime = null);

internal sealed record DeviceRegistrationEnvelopeResponse(
    Guid RegistrationId,
    Guid DevicePublicId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Envelope,
    string EnvelopeVersion);

internal sealed class DeviceRegistrationEnvelopeService : IDeviceRegistrationEnvelopeService
{
    private static readonly TimeSpan DefaultEnvelopeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxEnvelopeLifetime = TimeSpan.FromMinutes(30);

    private readonly ApplicationDbContext dbContext;
    private readonly TimeProvider timeProvider;
    private readonly IDataProtector protector;
    private readonly ILogger<DeviceRegistrationEnvelopeService> logger;

    public DeviceRegistrationEnvelopeService(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<DeviceRegistrationEnvelopeService> logger)
    {
        this.dbContext = dbContext;
        this.timeProvider = timeProvider;
        protector = dataProtectionProvider.CreateProtector("LogicHost", "DeviceRegistration", "Envelope", "v1");
        this.logger = logger;
    }

    public async Task<DeviceRegistrationEnvelopeResponse> CreateEnvelopeAsync(DeviceRegistrationEnvelopeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerUserId);

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
        var observatory = await observatoryQuery
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (observatory is null
            || !await ObservatoryMembershipAccess.ForOwner(dbContext, request.OwnerUserId)
                .AnyAsync(item => item.ObservatoryId == request.ObservatoryId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new DeviceRegistrationException(
                "Device registration not found or access denied.",
                DeviceRegistrationException.NotFoundReasonCode);
        }

        if (!observatory.IsActive)
        {
            throw new DeviceRegistrationException("Observatory must be active to issue device envelopes.");
        }
        IQueryable<DeviceRegistration> registrationQuery = isRelational
            ? dbContext.DeviceRegistrations.FromSqlInterpolated($"""
                SELECT * FROM [DeviceRegistrations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {request.RegistrationId}
                  AND [DeviceId] = {request.DeviceId}
                  AND [ObservatoryId] = {request.ObservatoryId}
                """)
            : dbContext.DeviceRegistrations.Where(registration => registration.Id == request.RegistrationId
                && registration.DeviceId == request.DeviceId
                && registration.ObservatoryId == request.ObservatoryId);
        var registration = await registrationQuery
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (registration is null)
        {
            throw new DeviceRegistrationException(
                "Device registration not found or access denied.",
                DeviceRegistrationException.NotFoundReasonCode);
        }

        if (registration.Status != DeviceRegistrationStatus.Pending)
        {
            throw new DeviceRegistrationException("Envelope can only be issued for pending registrations.");
        }

        var now = timeProvider.GetUtcNow();
        if (registration.ExpiresAtUtc is { } pendingExpires && pendingExpires <= now)
        {
            throw new DeviceRegistrationException("Verification window has expired. Restart registration.");
        }

        var lifetime = ClampLifetime(request.EnvelopeLifetime ?? DefaultEnvelopeLifetime);
        var expiresAt = now + lifetime;

        var devicePublicId = registration.DevicePublicId ?? Guid.NewGuid();
        var deviceKey = GenerateSecret();
        var registrationToken = GenerateSecret();
        var observatoryLocation = await ObservatoryLocationAuthority.EnsureCurrentVersionAsync(
            dbContext,
            observatory,
            now,
            request.OwnerUserId,
            cancellationToken).ConfigureAwait(false);
        registration.ObservatoryLocationVersion = observatoryLocation.Version;
        registration.ObservatoryLocationCanonicalSha256 = observatoryLocation.CanonicalSha256;
        registration.LocationEvidenceState = RegistrationLocationEvidenceState.ObservatoryPinned;
        registration.EnvelopeVersion = "v2";

        var payload = new DeviceRegistrationEnvelopePayload(
            registration.Id,
            registration.DeviceId,
            devicePublicId,
            registration.ObservatoryId,
            registration.FriendlyName,
            registration.EnvelopeVersion,
            deviceKey,
            registrationToken,
            now,
            expiresAt,
            observatoryLocation.Version,
            observatoryLocation.CanonicalSha256);

        var envelope = protector.Protect(JsonSerializer.Serialize(payload, DeviceRegistrationJson.Options));

        registration.DevicePublicId = devicePublicId;
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256(deviceKey);
        registration.RegistrationTokenHash = DeviceRegistrationService.ComputeSha256(registrationToken);
        registration.IssuedAtUtc = now;
        registration.ExpiresAtUtc = expiresAt;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Issued device envelope for {DeviceId} / {ObservatoryId} (registration {RegistrationId})",
                registration.DeviceId,
                registration.ObservatoryId,
                registration.Id);
        }

        return new DeviceRegistrationEnvelopeResponse(
            registration.Id,
            devicePublicId,
            now,
            expiresAt,
            envelope,
            registration.EnvelopeVersion);
    }

    private static TimeSpan ClampLifetime(TimeSpan requested)
    {
        if (requested <= TimeSpan.Zero)
        {
            return DefaultEnvelopeLifetime;
        }

        return requested <= MaxEnvelopeLifetime ? requested : MaxEnvelopeLifetime;
    }

    private static string GenerateSecret()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer);
    }
}
