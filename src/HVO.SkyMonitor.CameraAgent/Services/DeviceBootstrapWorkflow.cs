using System;
using System.Net.Http.Json;
using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.CameraAgent.Services.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class DeviceBootstrapWorkflow(
    IHttpClientFactory httpClientFactory,
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IDeviceRigProfileSeeder rigProfileSeeder,
    ILogger<DeviceBootstrapWorkflow> logger)
{
    public async Task<DeviceSecrets> BootstrapAsync(string envelope, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(envelope))
        {
            throw new ArgumentException("Envelope is required.", nameof(envelope));
        }

        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var nonce = GenerateNonce();

        var client = httpClientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);
        var request = new DeviceBootstrapRequestDto(identity.DeviceId, envelope.Trim(), nonce);

        using var message = new HttpRequestMessage(HttpMethod.Post, "api/device/bootstrap")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.TryAddWithoutValidation(CentralIdentityDelegatingHandler.SkipAuthHeader, "1");
        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Device bootstrap failed with status {StatusCode}", response.StatusCode);
            response.EnsureSuccessStatusCode();
        }

        var payload = await response.Content.ReadFromJsonAsync<DeviceBootstrapResponseDto>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Bootstrap response could not be parsed.");

        var secretsPayload = DeviceBootstrapCrypto.Decrypt(payload.Payload, payload.DeviceKey);

        var secrets = new DeviceSecrets(
            secretsPayload.DevicePublicId,
            secretsPayload.ObservatoryId,
            secretsPayload.FriendlyName,
            secretsPayload.RegistrationToken,
            secretsPayload.HeartbeatEndpoint,
            secretsPayload.UploadEndpoint,
            secretsPayload.HeartbeatIntervalSeconds,
            secretsPayload.IssuedAtUtc,
            secretsPayload.ExpiresAtUtc,
            payload.DeviceKey,
            secretsPayload.CentralIdentity,
            secretsPayload.RigProfileEndpoint);

        await secretStore.SaveAsync(secrets, cancellationToken).ConfigureAwait(false);

        await rigProfileSeeder.SeedAsync(identity, secrets, cancellationToken).ConfigureAwait(false);
        return secrets;
    }

    private static string GenerateNonce()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer);
    }
}
