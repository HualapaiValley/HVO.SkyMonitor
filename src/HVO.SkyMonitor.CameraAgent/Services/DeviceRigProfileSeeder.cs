using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface IDeviceRigProfileSeeder
{
    Task SeedAsync(DeviceIdentity identity, DeviceSecrets secrets, CancellationToken cancellationToken = default);
}

internal sealed class DeviceRigProfileSeeder(
    IHttpClientFactory httpClientFactory,
    ICameraAgentConfigurationLoader configurationLoader,
    ILogger<DeviceRigProfileSeeder> logger) : IDeviceRigProfileSeeder
{
    private static readonly JsonSerializerOptions RigSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public async Task SeedAsync(DeviceIdentity identity, DeviceSecrets secrets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(secrets);

        if (string.IsNullOrWhiteSpace(secrets.RigProfileEndpoint))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Rig profile endpoint is missing; skipping rig profile seed for {DeviceId}", identity.DeviceId);
            }
            return;
        }

        try
        {
            var config = await configurationLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
            var rigConfigJson = JsonSerializer.Serialize(config.Rig, RigSerializerOptions);

            var request = new DeviceRigProfileUpsertRequestDto(
                identity.DeviceId,
                secrets.DeviceKey,
                rigConfigJson,
                SoftwareVersion: null);

            var client = httpClientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);
            using var message = new HttpRequestMessage(HttpMethod.Post, secrets.RigProfileEndpoint)
            {
                Content = JsonContent.Create(request)
            };

            message.Headers.TryAddWithoutValidation(CentralIdentityDelegatingHandler.SkipAuthHeader, "1");

            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                logger.LogWarning(
                    "Rig profile seed failed with status {StatusCode} for {DeviceId}: {Detail}",
                    response.StatusCode,
                    identity.DeviceId,
                    detail);
                return;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Rig profile seeded for {DeviceId}", identity.DeviceId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Rig profile seed HTTP failure for {DeviceId}", identity.DeviceId);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Rig profile seed JSON failure for {DeviceId}", identity.DeviceId);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Rig profile seed failed for {DeviceId}", identity.DeviceId);
        }
    }

    private sealed record DeviceRigProfileUpsertRequestDto(
        string DeviceId,
        string DeviceKey,
        string RigConfigJson,
        string? SoftwareVersion);
}
