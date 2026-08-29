using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.AgentCore;
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
    string? Nonce,
    DeploymentLocationSnapshot DeploymentLocation,
    DeploymentLocationSourceKind DeploymentLocationSourceKind);

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
    private readonly IDeploymentLocationAuthorityService deploymentLocationAuthority;

    public DeviceBootstrapService(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<DeviceBootstrapService> logger,
        IOptions<CentralIdentityOptions> centralIdentityOptions,
        IOptions<DeviceBootstrapSecretsOptions> bootstrapOptions,
        IDeploymentLocationAuthorityService deploymentLocationAuthority)
    {
        this.dbContext = dbContext;
        this.timeProvider = timeProvider;
        protector = dataProtectionProvider.CreateProtector("LogicHost", "DeviceRegistration", "Envelope", "v1");
        this.logger = logger;
        this.centralIdentityOptions = centralIdentityOptions.Value;
        this.bootstrapOptions = bootstrapOptions.Value;
        this.deploymentLocationAuthority = deploymentLocationAuthority;
    }

    public async Task<DeviceBootstrapResult> BootstrapAsync(DeviceBootstrapRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Envelope);

        var envelope = DeserializeEnvelope(request.Envelope);
        if (!string.Equals(envelope.EnvelopeVersion, "v2", StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("The registration envelope version is no longer supported.");
        }

        if (!string.Equals(envelope.DeviceId, request.DeviceId, StringComparison.Ordinal))
        {
            throw new DeviceRegistrationException("Device identifier mismatch.");
        }
        if (envelope.ObservatoryLocationVersion <= 0
            || !IsCanonicalSha256(envelope.ObservatoryLocationCanonicalSha256))
        {
            throw new DeviceRegistrationException("The registration envelope location evidence is invalid.");
        }
        if (request.DeploymentLocation is null || !request.DeploymentLocation.Validate().IsValid)
        {
            throw new DeviceRegistrationException("A v2 registration requires a valid protected deployment location.");
        }
        if (!Enum.IsDefined(request.DeploymentLocationSourceKind)
            || request.DeploymentLocationSourceKind == DeploymentLocationSourceKind.Unspecified)
        {
            throw new DeviceRegistrationException("Deployment location source kind is invalid.");
        }

        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        IQueryable<Observatory> observatoryQuery = isRelational
            ? dbContext.Observatories.FromSqlInterpolated($"""
                SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {envelope.ObservatoryId}
                """)
            : dbContext.Observatories.Where(observatory => observatory.Id == envelope.ObservatoryId);
        var observatory = await observatoryQuery
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (observatory is not { IsActive: true })
        {
            throw new DeviceRegistrationException("Observatory is not active.");
        }
        IQueryable<DeviceRegistration> registrationQuery = isRelational
            ? dbContext.DeviceRegistrations.FromSqlInterpolated($"""
                SELECT * FROM [DeviceRegistrations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {envelope.RegistrationId}
                """)
            : dbContext.DeviceRegistrations.Where(registration => registration.Id == envelope.RegistrationId);
        var registration = await registrationQuery
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

        var observatoryLocation = await ObservatoryLocationAuthority.EnsureCurrentVersionAsync(
            dbContext,
            observatory,
            now,
            registration.OwnerUserId,
            cancellationToken).ConfigureAwait(false);
        if (envelope.ObservatoryLocationVersion != observatoryLocation.Version ||
            !string.Equals(
                envelope.ObservatoryLocationCanonicalSha256,
                observatoryLocation.CanonicalSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceRegistrationException(
                "The Observatory location changed after envelope issuance. Restart registration.");
        }
        registration.DevicePublicId ??= envelope.DevicePublicId;
        var locationAcknowledgment = await deploymentLocationAuthority.ProposeAsync(
            registration,
            request.DeploymentLocation,
            request.DeploymentLocationSourceKind,
            $"bootstrap:{registration.DeviceId}",
            cancellationToken).ConfigureAwait(false);

        var secrets = new DeviceBootstrapSecretPayload(
            registration.DevicePublicId ?? envelope.DevicePublicId,
            registration.ObservatoryId,
            registration.FriendlyName,
            envelope.RegistrationToken,
            "/api/device/heartbeat",
            "/api/device/profile/rig",
            DefaultHeartbeatIntervalSeconds,
            now,
            now + DefaultSecretLifetime,
            CloneCentralIdentityOptions(),
            locationAcknowledgment);

        var encryptedPayload = EncryptSecrets(envelope.DeviceKey, secrets);

        registration.Status = DeviceRegistrationStatus.Active;
        registration.ActivatedAtUtc = now;
        registration.LastSeenUtc = now;
        registration.ExpiresAtUtc = secrets.ExpiresAtUtc;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

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

    private static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 }
            && value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

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
        string RigProfileEndpoint,
        int HeartbeatIntervalSeconds,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        CentralIdentityOptions CentralIdentity,
        DeploymentLocationAcknowledgment DeploymentLocationAcknowledgment);
}
