using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Hosting;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Drains durable artifact manifests without participating in capture-path execution.</summary>
public sealed class ArtifactOutboxDrainService(
    ICameraAgentConfigurationAccessor configurationAccessor,
    IArtifactOutbox outbox,
    IFrameStorageService frameStorageService,
    ArtifactUploadClient uploadClient,
    IOptions<CameraAgentHostOptions> hostOptions,
    TimeProvider timeProvider,
    ILogger<ArtifactOutboxDrainService> logger,
    ArtifactOutboxState state,
    ArtifactOutboxTelemetry telemetry,
    FleetRuntimeState fleetRuntimeState,
    CaptureScheduleRuntimeCoordinator? scheduleCoordinator = null) : BackgroundService
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static readonly Action<ILogger, Exception?> PayloadQuarantined = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2081, nameof(PayloadQuarantined)),
        "Artifact outbox quarantined unreadable payload evidence");
    private static readonly Action<ILogger, Exception?> LeaseLost = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2082, nameof(LeaseLost)),
        "Artifact outbox lease was lost before local settlement");
    private static readonly Action<ILogger, Exception?> RootDrainFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(2083, nameof(RootDrainFailed)),
        "Artifact outbox root could not be drained");
    private static readonly Action<ILogger, Exception?> LeaseRenewalFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2084, nameof(LeaseRenewalFailed)),
        "Artifact outbox lease renewal failed");
    private static readonly Action<ILogger, int, Exception?> WorkClaimed = LoggerMessage.Define<int>(
        LogLevel.Debug, new EventId(2076, nameof(WorkClaimed)),
        "Artifact outbox claimed durable work at attempt {Attempt}");
    private static readonly Action<ILogger, int, string, long, Exception?> RetryScheduled = LoggerMessage.Define<int, string, long>(
        LogLevel.Warning, new EventId(2077, nameof(RetryScheduled)),
        "Artifact outbox scheduled retry attempt {Attempt} because {Reason} after {DelayMilliseconds} ms");
    private static readonly Action<ILogger, string, Exception?> WorkAcknowledged = LoggerMessage.Define<string>(
        LogLevel.Debug, new EventId(2078, nameof(WorkAcknowledged)),
        "Artifact outbox accepted structured acknowledgement for manifest schema {ManifestSchemaVersion}");
    private static readonly Action<ILogger, int, string, Exception?> WorkQuarantined = LoggerMessage.Define<int, string>(
        LogLevel.Warning, new EventId(2079, nameof(WorkQuarantined)),
        "Artifact outbox quarantined durable work at attempt {Attempt} because {Reason}");
    private readonly string _leaseOwner = string.Concat(Environment.MachineName, ":", Environment.ProcessId, ":", Guid.NewGuid().ToString("N"));

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The linked upload cancellation source is disposed unconditionally in the immediately enclosing finally block.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (hostOptions.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            state.MarkInitialized();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = await configurationAccessor.WaitForConfigurationAsync(stoppingToken).ConfigureAwait(false);
            config = scheduleCoordinator?.Snapshot?.Configuration ?? config;
            var storageRoots = ResolveStorageRoots(config, hostOptions.Value);
            if (storageRoots.Count == 0)
            {
                state.MarkInitialized();
                await Task.Delay(
                    TimeSpan.FromSeconds(hostOptions.Value.UploadPollIntervalSeconds),
                    timeProvider,
                    stoppingToken).ConfigureAwait(false);
                continue;
            }
            foreach (var storageRoot in storageRoots)
            {
                try
                {
                    await outbox.InitializeAsync(storageRoot, stoppingToken).ConfigureAwait(false);
                    for (var index = 0; index < hostOptions.Value.UploadBatchSize; index++)
                    {
                        var leaseDuration = TimeSpan.FromSeconds(hostOptions.Value.CaptureDistribution.LeaseSeconds);
                        ArtifactOutboxLease? lease;
                        var claimStarted = timeProvider.GetTimestamp();
                        using (ArtifactOutboxTelemetry.ActivitySource.StartActivity("outbox.claim"))
                        {
                            lease = await outbox.ClaimAsync(
                                storageRoot, _leaseOwner, leaseDuration, stoppingToken).ConfigureAwait(false);
                        }
                        telemetry.RecordClaim(timeProvider.GetElapsedTime(claimStarted));
                        if (lease is null)
                        {
                            var snapshot = await outbox.GetSnapshotAsync(storageRoot, stoppingToken).ConfigureAwait(false);
                            if (snapshot.PendingCount > 0)
                            {
                                continue;
                            }
                            break;
                        }
                        WorkClaimed(logger, lease.Record.AttemptCount, null);

                        try
                        {
                            CancellationTokenSource? uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            try
                            {
                                var renewal = RenewLeaseAsync(
                                    storageRoot, lease, leaseDuration, uploadCancellation, stoppingToken);
                                ArtifactUploadResult result;
                                try
                                {
                                    var started = timeProvider.GetTimestamp();
                                    using var uploadActivity = ArtifactOutboxTelemetry.ActivitySource.StartActivity("artifact.upload");
                                    result = await uploadClient.UploadAsync(
                                        storageRoot, lease.Record, uploadCancellation.Token).ConfigureAwait(false);
                                    fleetRuntimeState.UploadCompleted(timeProvider.GetElapsedTime(started));
                                    telemetry.RecordUpload(
                                        result,
                                        lease.Record.PayloadLength ?? 0,
                                        timeProvider.GetElapsedTime(started));
                                }
                                finally
                                {
                                    await uploadCancellation.CancelAsync().ConfigureAwait(false);
                                    await renewal.ConfigureAwait(false);
                                }

                                if (result.Disposition == ArtifactUploadDisposition.Acknowledged)
                                {
                                    var acceptedManifestSchemaVersion = result.Acknowledgement?.AcceptedManifestSchemaVersion
                                        ?? throw new InvalidDataException("Successful upload result omitted acknowledgement evidence.");
                                    var settlementStarted = timeProvider.GetTimestamp();
                                    using var acknowledgementActivity = ArtifactOutboxTelemetry.ActivitySource.StartActivity("outbox.ack");
                                    await AcknowledgeAsync(storageRoot, lease, result, stoppingToken).ConfigureAwait(false);
                                    telemetry.RecordSettlement(result.Disposition, timeProvider.GetElapsedTime(settlementStarted));
                                    WorkAcknowledged(logger, acceptedManifestSchemaVersion, null);
                                }
                                else if (result.Disposition == ArtifactUploadDisposition.Retry)
                                {
                                    var configuredMaximum = TimeSpan.FromSeconds(hostOptions.Value.UploadRetryMaximumDelaySeconds);
                                    var delay = result.RetryAfter is { } retryAfter
                                        ? TimeSpan.FromTicks(Math.Min(retryAfter.Ticks, configuredMaximum.Ticks))
                                        : CalculateRetryDelay(
                                            lease.Record.AttemptCount,
                                            TimeSpan.FromSeconds(hostOptions.Value.UploadRetryInitialDelaySeconds),
                                            configuredMaximum);
                                    var settlementStarted = timeProvider.GetTimestamp();
                                    using (ArtifactOutboxTelemetry.ActivitySource.StartActivity("outbox.retry"))
                                    {
                                        await outbox.RetryAsync(
                                            storageRoot,
                                            lease,
                                            timeProvider.GetUtcNow() + delay,
                                            result.Reason,
                                            stoppingToken).ConfigureAwait(false);
                                    }
                                    telemetry.RecordSettlement(result.Disposition, timeProvider.GetElapsedTime(settlementStarted));
                                    RetryScheduled(logger, lease.Record.AttemptCount, result.Reason, (long)delay.TotalMilliseconds, null);
                                }
                                else
                                {
                                    var settlementStarted = timeProvider.GetTimestamp();
                                    using (ArtifactOutboxTelemetry.ActivitySource.StartActivity("outbox.quarantine"))
                                    {
                                        await outbox.QuarantineAsync(
                                            storageRoot, lease, result.Reason, stoppingToken).ConfigureAwait(false);
                                    }
                                    telemetry.RecordSettlement(result.Disposition, timeProvider.GetElapsedTime(settlementStarted));
                                    WorkQuarantined(logger, lease.Record.AttemptCount, result.Reason, null);
                                }
                            }
                            finally
                            {
                                uploadCancellation.Dispose();
                                uploadCancellation = null;
                            }
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            await outbox.QuarantineAsync(
                                storageRoot, lease, "payload-unreadable", stoppingToken).ConfigureAwait(false);
                            PayloadQuarantined(logger, exception);
                        }
                    }
                    state.Update(
                        storageRoot,
                        await outbox.GetSnapshotAsync(storageRoot, stoppingToken).ConfigureAwait(false));
                }
                catch (ArtifactOutboxLeaseLostException exception)
                {
                    LeaseLost(logger, exception);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                    or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                {
                    state.ReportUnavailable(storageRoot, exception.GetType().Name);
                    RootDrainFailed(logger, exception);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(hostOptions.Value.UploadPollIntervalSeconds), timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RenewLeaseAsync(
        string storageRoot,
        ArtifactOutboxLease lease,
        TimeSpan leaseDuration,
        CancellationTokenSource uploadCancellation,
        CancellationToken stoppingToken)
    {
        try
        {
            var interval = TimeSpan.FromSeconds(hostOptions.Value.CaptureDistribution.LeaseRenewalSeconds);
            while (!uploadCancellation.IsCancellationRequested)
            {
                await Task.Delay(interval, timeProvider, uploadCancellation.Token).ConfigureAwait(false);
                await outbox.RenewAsync(storageRoot, lease, leaseDuration, uploadCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (uploadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is ArtifactOutboxLeaseLostException or IOException)
        {
            LeaseRenewalFailed(logger, exception);
            await uploadCancellation.CancelAsync().ConfigureAwait(false);
            stoppingToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private async Task AcknowledgeAsync(
        string storageRoot,
        ArtifactOutboxLease lease,
        ArtifactUploadResult result,
        CancellationToken cancellationToken)
    {
        var acknowledgement = result.Acknowledgement
            ?? throw new InvalidDataException("Successful upload result omitted acknowledgement evidence.");
        var delivery = ArtifactUploadClient.ResolveDelivery(lease.Record);
        var lifecycleGate = StorageLifecycleLock.ForRoot(storageRoot);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await outbox.AcknowledgeAsync(storageRoot, lease, acknowledgement, cancellationToken).ConfigureAwait(false);
            if (ShouldRemoveUploadedArtifact(storageRoot, hostOptions.Value.RawIngressRoot))
            {
                var path = Path.Combine(Path.GetFullPath(storageRoot), delivery.RelativeArtifactPath);
                await frameStorageService.RemoveAsync(
                    storageRoot,
                    new StoredFrameReference(
                        delivery.RelativeArtifactPath, path, delivery.CapturedAtUtc, delivery.Role),
                    delivery.ArtifactId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
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

    internal static bool ShouldRemoveUploadedArtifact(
        string storageRoot,
        string rawIngressRoot)
        => string.IsNullOrWhiteSpace(rawIngressRoot)
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawIngressRoot)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string? ResolveStorageRoot(HVO.SkyMonitor.AgentCore.CameraModuleConfig config)
    {
        foreach (var step in config.Pipeline.Steps)
        {
            if (IsStorageStep(step.Type) && step.Enabled != false)
            {
                var parsed = ParseStorageOptions(step.Options);
                if (parsed is not null && IsUploadEnabled(parsed) && !string.IsNullOrWhiteSpace(parsed.StorageRoot))
                {
                    return parsed.StorageRoot;
                }
            }
        }

        return null;
    }

    public static IReadOnlyList<string> ResolveStorageRoots(
        HVO.SkyMonitor.AgentCore.CameraModuleConfig config,
        CameraAgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        if (options.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            return [];
        }
        var roots = new List<string>();
        if (!options.ProvisioningStartupGate.Enabled && options.CaptureDistribution.UploadEnabled)
        {
            roots.Add(Path.GetFullPath(options.RawIngressRoot));
        }
        foreach (var step in config.Pipeline.Steps)
        {
            if (!IsStorageStep(step.Type) || step.Enabled == false)
            {
                continue;
            }
            var parsed = ParseStorageOptions(step.Options);
            if (parsed is not null && IsUploadEnabled(parsed) && !string.IsNullOrWhiteSpace(parsed.StorageRoot))
            {
                var root = Path.GetFullPath(parsed.StorageRoot);
                if (!roots.Any(existing => PathsEqual(existing, root)))
                {
                    roots.Add(root);
                }
            }
        }
        return roots;
    }

    public static IReadOnlyList<string> ResolveLocalStorageRoots(
        HVO.SkyMonitor.AgentCore.CameraModuleConfig config,
        CameraAgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        var roots = new List<string> { Path.GetFullPath(options.RawIngressRoot) };
        foreach (var step in config.Pipeline.Steps)
        {
            if (!IsStorageStep(step.Type) || step.Enabled == false)
            {
                continue;
            }

            var parsed = ParseStorageOptions(step.Options);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.StorageRoot))
            {
                continue;
            }

            var root = Path.GetFullPath(parsed.StorageRoot);
            if (!roots.Any(existing => PathsEqual(existing, root)))
            {
                roots.Add(root);
            }
        }
        return roots;
    }

    private static bool IsUploadEnabled(NoOpFileStorageProcessingStepOptions options)
        => options.QueueForUpload || (options.Policies ?? []).Any(static policy => policy.QueueForUpload == true);

    private static bool IsStorageStep(string typeName)
        => string.Equals(typeName, NoOpFileStorageProcessingStep.StableAlias, StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains(nameof(NoOpFileStorageProcessingStep), StringComparison.OrdinalIgnoreCase);

    private static NoOpFileStorageProcessingStepOptions? ParseStorageOptions(System.Text.Json.JsonElement? options)
    {
        if (options is null)
        {
            return new NoOpFileStorageProcessingStepOptions();
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<NoOpFileStorageProcessingStepOptions>(
                options.Value.GetRawText(), SerializerOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

}
