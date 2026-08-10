using System;
using System.Net.Http.Json;
using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.CameraAgent.Services.Models;
using HVO.SkyMonitor.AgentCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface IDeviceBootstrapWorkflow
{
    Task<DeviceSecrets> BootstrapAsync(string envelope, CancellationToken cancellationToken = default);
}

internal sealed class DeviceBootstrapWorkflow(
    IHttpClientFactory httpClientFactory,
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IDeviceRigProfileSeeder rigProfileSeeder,
    IDeploymentLocationStore deploymentLocationStore,
    IOptions<CameraAgentHostOptions> options,
    ILogger<DeviceBootstrapWorkflow> logger) : IDeviceBootstrapWorkflow
{
    public async Task<DeviceSecrets> BootstrapAsync(string envelope, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(envelope))
        {
            throw new ArgumentException("Envelope is required.", nameof(envelope));
        }
        if (options.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            throw new InvalidOperationException("LogicHost integration is disabled for this CameraAgent.");
        }

        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var nonce = GenerateNonce();

        var client = httpClientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);
        var activeDeploymentLocation = deploymentLocationStore.Active
            ?? throw new InvalidOperationException("Deployment location is not initialized.");
        var deploymentLocation = deploymentLocationStore.Candidate ?? activeDeploymentLocation;
        var deploymentLocationSourceKind = deploymentLocationStore.ResolveSourceKind(deploymentLocation);
        var request = new DeviceBootstrapRequestDto(
            identity.DeviceId,
            envelope.Trim(),
            nonce,
            deploymentLocation,
            deploymentLocationSourceKind);

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
        var acknowledgment = secretsPayload.DeploymentLocationAcknowledgment;
        if (string.Equals(payload.EnvelopeVersion, "v2", StringComparison.Ordinal) && acknowledgment is null)
        {
            throw new InvalidOperationException("The v2 bootstrap response omitted its deployment-location acknowledgment.");
        }
        if (acknowledgment is not null
            && (!acknowledgment.Validate().IsValid
                || acknowledgment.Observatory.ObservatoryId != secretsPayload.ObservatoryId
                || !string.Equals(
                    acknowledgment.Deployment.CanonicalSha256,
                    deploymentLocation.CanonicalSha256,
                    StringComparison.OrdinalIgnoreCase)
                || acknowledgment.SourceKind != request.DeploymentLocationSourceKind))
        {
            throw new InvalidOperationException("The bootstrap deployment-location acknowledgment is invalid.");
        }

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
            secretsPayload.RigProfileEndpoint,
            acknowledgment);

        if (acknowledgment is
            {
                Status: DeploymentLocationResolutionStatus.Acknowledged
            } acknowledged
            && !string.Equals(
                acknowledged.Deployment.CanonicalSha256,
                activeDeploymentLocation.CanonicalSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            await deploymentLocationStore.StageAsync(acknowledged.Deployment, cancellationToken)
                .ConfigureAwait(false);
        }

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
