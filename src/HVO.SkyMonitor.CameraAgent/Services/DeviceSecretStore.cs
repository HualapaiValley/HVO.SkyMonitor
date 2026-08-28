using System;
using System.IO;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.AgentCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface IDeviceSecretStore
{
    Task<DeviceSecrets?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DeviceSecrets secrets, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

internal sealed record DeviceSecrets(
    Guid DevicePublicId,
    Guid ObservatoryId,
    string FriendlyName,
    string RegistrationToken,
    string HeartbeatEndpoint,
    int HeartbeatIntervalSeconds,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string DeviceKey,
    CentralIdentityOptions CentralIdentity,
    string RigProfileEndpoint = "/api/device/profile/rig",
    DeploymentLocationAcknowledgment? DeploymentLocationAcknowledgment = null);

internal sealed class DeviceSecretStore(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<DeviceProvisioningOptions> optionsAccessor,
    ILogger<DeviceSecretStore> logger) : IDeviceSecretStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly DeviceProvisioningOptions options = optionsAccessor.Value;
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("CameraAgent", "DeviceSecrets", "v1");

    public async Task<DeviceSecrets?> GetAsync(CancellationToken cancellationToken = default)
    {
        var path = options.GetSecretsPath();
        if (!File.Exists(path))
        {
            return null;
        }

        DeviceStateFilePermissions.RestrictDirectory(Path.GetDirectoryName(path)!);
        DeviceStateFilePermissions.RestrictFile(path);
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        var protectedPayload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var json = protector.Unprotect(protectedPayload);
        return JsonSerializer.Deserialize<DeviceSecrets>(json, SerializerOptions);
    }

    public async Task SaveAsync(DeviceSecrets secrets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var path = options.GetSecretsPath();
        DeviceStateFilePermissions.RestrictDirectory(Path.GetDirectoryName(path)!);

        var json = JsonSerializer.Serialize(secrets, SerializerOptions);
        var protectedPayload = protector.Protect(json);

        await File.WriteAllTextAsync(path, protectedPayload, cancellationToken).ConfigureAwait(false);
        DeviceStateFilePermissions.RestrictFile(path);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Persisted device secrets for {DevicePublicId}", secrets.DevicePublicId);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var path = options.GetSecretsPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
