using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

public interface IDeviceBootstrapService
{
    Task<DeviceBootstrapResult> BootstrapAsync(DeviceBootstrapRequest request, CancellationToken cancellationToken = default);
}

public sealed record DeviceBootstrapRequest(
    string DeviceId,
    string Envelope,
    string? Nonce = null);

public sealed record DeviceBootstrapResult(
    Guid RegistrationId,
    Guid DevicePublicId,
    string EnvelopeVersion,
    string DeviceKey,
    DeviceBootstrapEncryptedPayload Payload);

public sealed record DeviceBootstrapEncryptedPayload(
    string Ciphertext,
    string Nonce,
    string Tag,
    string Algorithm);

internal sealed class DeviceBootstrapService : IDeviceBootstrapService
{
    private static readonly TimeSpan DefaultSecretLifetime = TimeSpan.FromDays(30);
    private const int DefaultHeartbeatIntervalSeconds = 60;

    private readonly ApplicationDbContext dbContext;
    private readonly TimeProvider timeProvider;
    private readonly IDataProtector protector;
    private readonly ILogger<DeviceBootstrapService> logger;
    private readonly CentralIdentityOptions centralIdentityOptions;
    private readonly DeviceBootstrapSecretsOptions bootstrapOptions;

    public DeviceBootstrapService(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<DeviceBootstrapService> logger,
        IOptions<CentralIdentityOptions> centralIdentityOptions,
        IOptions<DeviceBootstrapSecretsOptions> bootstrapOptions)
    {
        this.dbContext = dbContext;
        this.timeProvider = timeProvider;
        protector = dataProtectionProvider.CreateProtector("LogicHost", "DeviceRegistration", "Envelope", "v1");
        this.logger = logger;
        this.centralIdentityOptions = centralIdentityOptions.Value;
        this.bootstrapOptions = bootstrapOptions.Value;
    }

    public async Task<DeviceBootstrapResult> BootstrapAsync(DeviceBootstrapRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Envelope);

        var envelope = DeserializeEnvelope(request.Envelope);

        if (!string.Equals(envelope.DeviceId, request.DeviceId, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("Device identifier mismatch.");
        }

        var registration = await dbContext.DeviceRegistrations
            .Where(reg => reg.Id == envelope.RegistrationId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DeviceRegistrationException("Device registration not found.");

        ValidateRegistration(registration, envelope);

        var now = timeProvider.GetUtcNow();
        if (envelope.ExpiresAtUtc <= now)
        {
            throw new DeviceRegistrationException("Envelope has expired.");
        }

        var computedDeviceKeyHash = DeviceRegistrationService.ComputeSha256(envelope.DeviceKey);
        if (!string.Equals(computedDeviceKeyHash, registration.DeviceKeyHash, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("Envelope secrets mismatch.");
        }

        var computedRegistrationTokenHash = DeviceRegistrationService.ComputeSha256(envelope.RegistrationToken);
        if (!string.Equals(computedRegistrationTokenHash, registration.RegistrationTokenHash, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("Registration token is invalid or has already been used.");
        }

        var secrets = new DeviceBootstrapSecretPayload(
            registration.DevicePublicId ?? envelope.DevicePublicId,
            registration.ObservatoryId,
            registration.FriendlyName,
            envelope.RegistrationToken,
            "/api/device/heartbeat",
            "/api/device/upload",
            "/api/device/profile/rig",
            DefaultHeartbeatIntervalSeconds,
            now,
            now + DefaultSecretLifetime,
            CloneCentralIdentityOptions());

        var encryptedPayload = EncryptSecrets(envelope.DeviceKey, secrets);

        registration.Status = DeviceRegistrationStatus.Active;
        registration.DevicePublicId ??= envelope.DevicePublicId;
        registration.ActivatedAtUtc = now;
        registration.LastSeenUtc = now;
        registration.ExpiresAtUtc = secrets.ExpiresAtUtc;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Activated device registration {RegistrationId} (device {DeviceId})",
                registration.Id,
                registration.DeviceId);
        }

        return new DeviceBootstrapResult(
            registration.Id,
            registration.DevicePublicId!.Value,
            registration.EnvelopeVersion,
            envelope.DeviceKey,
            encryptedPayload);
    }

    private DeviceRegistrationEnvelopePayload DeserializeEnvelope(string protectedEnvelope)
    {
        try
        {
            var json = protector.Unprotect(protectedEnvelope);
            return JsonSerializer.Deserialize<DeviceRegistrationEnvelopePayload>(json, DeviceRegistrationJson.Options)
                ?? throw new DeviceRegistrationException("Envelope payload is invalid.");
        }
        catch (CryptographicException ex)
        {
            throw new DeviceRegistrationException("Envelope could not be verified.", ex);
        }
        catch (JsonException ex)
        {
            throw new DeviceRegistrationException("Envelope payload is malformed.", ex);
        }
    }

    private static void ValidateRegistration(DeviceRegistration registration, DeviceRegistrationEnvelopePayload envelope)
    {
        if (!string.Equals(registration.DeviceId, envelope.DeviceId, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("Device mismatch.");
        }

        if (registration.ObservatoryId != envelope.ObservatoryId)
        {
            throw new DeviceRegistrationException("Observatory mismatch.");
        }

        if (!string.Equals(registration.EnvelopeVersion, envelope.EnvelopeVersion, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("Envelope version mismatch.");
        }

        if (registration.Status != DeviceRegistrationStatus.Pending)
        {
            throw new DeviceRegistrationException("Registration is not pending.");
        }
    }

    private static DeviceBootstrapEncryptedPayload EncryptSecrets(string deviceKey, DeviceBootstrapSecretPayload payload)
    {
        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(deviceKey);
        }
        catch (FormatException ex)
        {
            throw new DeviceRegistrationException("Device key format is invalid.", ex);
        }

        if (keyBytes.Length != 32)
        {
            throw new DeviceRegistrationException("Device key must be 256 bits.");
        }

        Span<byte> nonce = stackalloc byte[12];
        RandomNumberGenerator.Fill(nonce);
        Span<byte> tag = stackalloc byte[16];

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, DeviceRegistrationJson.Options);
        var ciphertext = new byte[plaintext.Length];

        using (var aesGcm = new AesGcm(keyBytes, tag.Length))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        return new DeviceBootstrapEncryptedPayload(
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            "AES-256-GCM");
    }

    private CentralIdentityOptions CloneCentralIdentityOptions()
    {
        var template = bootstrapOptions.CentralIdentity ?? centralIdentityOptions;
        var serialized = JsonSerializer.Serialize(template, DeviceRegistrationJson.Options);
        return JsonSerializer.Deserialize<CentralIdentityOptions>(serialized, DeviceRegistrationJson.Options)
            ?? throw new InvalidOperationException("Central identity configuration could not be cloned.");
    }

    private sealed record DeviceBootstrapSecretPayload(
        Guid DevicePublicId,
        Guid ObservatoryId,
        string FriendlyName,
        string RegistrationToken,
        string HeartbeatEndpoint,
        string UploadEndpoint,
        string RigProfileEndpoint,
        int HeartbeatIntervalSeconds,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        CentralIdentityOptions CentralIdentity);
}
