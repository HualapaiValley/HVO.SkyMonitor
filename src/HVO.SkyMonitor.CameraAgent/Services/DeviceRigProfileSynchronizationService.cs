using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class DeviceRigProfileSynchronizationService(
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IDeviceRigProfileSeeder seeder,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<DeviceRigProfileSynchronizationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var provisioned = false;
            try
            {
                var secrets = await secretStore.GetAsync(stoppingToken).ConfigureAwait(false);
                provisioned = secrets is not null;
                if (secrets is not null)
                {
                    var identity = await identityStore.GetOrCreateAsync(stoppingToken).ConfigureAwait(false);
                    await seeder.SeedAsync(identity, secrets, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or System.Security.Cryptography.CryptographicException
                or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Rig profile synchronization could not read provisioning state");
            }
            await Task.Delay(
                provisioned ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30),
                timeProvider,
                stoppingToken).ConfigureAwait(false);
        }
    }
}
