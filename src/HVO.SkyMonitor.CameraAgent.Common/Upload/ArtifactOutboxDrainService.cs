using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Drains durable artifact manifests without participating in capture-path execution.</summary>
public sealed class ArtifactOutboxDrainService(
    ICameraAgentConfigurationAccessor configurationAccessor,
    IArtifactOutbox outbox,
    ArtifactUploadClient uploadClient) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = await configurationAccessor.WaitForConfigurationAsync(stoppingToken).ConfigureAwait(false);
        var storageRoot = ResolveStorageRoot(config);
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var manifest in outbox.List(storageRoot, 10))
            {
                if (await uploadClient.UploadAsync(storageRoot, manifest, stoppingToken).ConfigureAwait(false))
                {
                    await outbox.AcknowledgeAsync(storageRoot, manifest.IdempotencyKey, stoppingToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private static string ResolveStorageRoot(HVO.SkyMonitor.AgentCore.CameraModuleConfig config)
    {
        foreach (var step in config.ResolveProcessingSteps())
        {
            if (step.Type.Contains(nameof(NoOpFileStorageProcessingStep), StringComparison.OrdinalIgnoreCase) && step.Options is { } options)
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<NoOpFileStorageProcessingStepOptions>(options.GetRawText());
                if (!string.IsNullOrWhiteSpace(parsed?.StorageRoot))
                {
                    return parsed.StorageRoot;
                }
            }
        }

        throw new InvalidOperationException("Artifact outbox requires a configured file storage root.");
    }
}
