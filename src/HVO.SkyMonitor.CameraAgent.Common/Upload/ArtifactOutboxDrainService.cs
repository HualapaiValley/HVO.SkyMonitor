using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Hosting;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Drains durable artifact manifests without participating in capture-path execution.</summary>
public sealed class ArtifactOutboxDrainService(
    ICameraAgentConfigurationAccessor configurationAccessor,
    IArtifactOutbox outbox,
    IFrameStorageService frameStorageService,
    ArtifactUploadClient uploadClient,
    IOptions<CameraAgentHostOptions> hostOptions,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly Dictionary<string, RetryState> _retries = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = await configurationAccessor.WaitForConfigurationAsync(stoppingToken).ConfigureAwait(false);
        var storageRoot = ResolveStorageRoot(config);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var deferred = _retries
                .Where(retry => retry.Value.NextAttemptUtc > now)
                .Select(retry => retry.Key)
                .ToHashSet(StringComparer.Ordinal);
            var ready = outbox.List(storageRoot, hostOptions.Value.UploadBatchSize, deferred);
            var removals = new List<StoredFrameRemoval>(ready.Count);
            foreach (var manifest in ready)
            {
                if (await uploadClient.UploadAsync(storageRoot, manifest, stoppingToken).ConfigureAwait(false))
                {
                    _retries.Remove(manifest.IdempotencyKey);
                    await outbox.AcknowledgeAsync(storageRoot, manifest.IdempotencyKey, stoppingToken).ConfigureAwait(false);
                    var path = Path.Combine(Path.GetFullPath(storageRoot), manifest.RelativeArtifactPath);
                    removals.Add(new StoredFrameRemoval(new StoredFrameReference(
                        manifest.RelativeArtifactPath, path, manifest.CapturedAtUtc, manifest.Role), manifest.ArtifactId));
                }
                else
                {
                    var attempt = _retries.TryGetValue(manifest.IdempotencyKey, out var retry) ? retry.Attempt + 1 : 1;
                    var delay = CalculateRetryDelay(
                        attempt,
                        TimeSpan.FromSeconds(hostOptions.Value.UploadRetryInitialDelaySeconds),
                        TimeSpan.FromSeconds(hostOptions.Value.UploadRetryMaximumDelaySeconds));
                    _retries[manifest.IdempotencyKey] = new RetryState(attempt, timeProvider.GetUtcNow() + delay);
                }
            }
            await frameStorageService.RemoveBatchAsync(storageRoot, removals, stoppingToken).ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromSeconds(hostOptions.Value.UploadPollIntervalSeconds), timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    internal static TimeSpan CalculateRetryDelay(int attempt, TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var multiplier = 1L << Math.Min(attempt - 1, 30);
        var ticks = initialDelay.Ticks > maximumDelay.Ticks / multiplier
            ? maximumDelay.Ticks
            : initialDelay.Ticks * multiplier;
        return TimeSpan.FromTicks(Math.Min(ticks, maximumDelay.Ticks));
    }

    private static string ResolveStorageRoot(HVO.SkyMonitor.AgentCore.CameraModuleConfig config)
    {
        foreach (var step in config.ResolveProcessingSteps())
        {
            if (step.Type.Contains(nameof(NoOpFileStorageProcessingStep), StringComparison.OrdinalIgnoreCase) && step.Options is { } options)
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<NoOpFileStorageProcessingStepOptions>(options.GetRawText(), SerializerOptions);
                if (parsed is { QueueForUpload: true } && !string.IsNullOrWhiteSpace(parsed.StorageRoot))
                {
                    return parsed.StorageRoot;
                }
            }
        }

        throw new InvalidOperationException("Artifact outbox requires a configured file storage root.");
    }

    private sealed record RetryState(int Attempt, DateTimeOffset NextAttemptUtc);
}
