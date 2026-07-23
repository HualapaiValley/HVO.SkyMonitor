using System.Diagnostics;
using System.Reflection;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Fleet.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Common.Fleet;

public sealed class FleetStatusCollector(
    ICameraAgentConfigurationAccessor configurationAccessor,
    RawIngressState rawIngressState,
    CaptureLaneState captureLaneState,
    CaptureProcessingState captureProcessingState,
    ArtifactOutboxState artifactOutboxState,
    StoragePressureState storagePressureState,
    ICaptureTelemetryProvider captureTelemetryProvider,
    FleetRuntimeState runtimeState,
    TimeProvider timeProvider)
{
    private readonly object _processGate = new();
    private TimeSpan _lastProcessCpu;
    private long _lastProcessTimestamp;
    private bool _hasProcessSample;

    public async ValueTask<FleetStatusReportV1> CollectAsync(
        Guid agentInstanceId,
        Guid bootSessionId,
        long sequence,
        DateTimeOffset processStartedAtUtc,
        FleetStatusOutboxSnapshot heartbeatOutbox,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeatOutbox);
        var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var runtime = runtimeState.Snapshot;
        var ingress = rawIngressState.Snapshot;
        var lanes = captureLaneState.Snapshot;
        var processing = captureProcessingState.Snapshot;
        var artifactOutbox = artifactOutboxState.Snapshot;
        var storage = storagePressureState.Snapshots
            .OrderBy(static item => item.StorageRoot, StringComparer.Ordinal)
            .Take(FleetContractJson.MaximumStorageTargets)
            .Select(static (item, index) => new FleetStorageSummary(
                $"storage-{index + 1}",
                item.Capacity.TotalBytes,
                item.Capacity.AvailableBytes,
                item.IsUnderPressure,
                item.EffectiveRetentionDays,
                item.EvaluatedUtc,
                BoundOptional(item.ProbeFailure, 128)))
            .ToArray();
        var fleetLanes = lanes.Lanes
            .OrderBy(static lane => lane.Lane, StringComparer.Ordinal)
            .Take(FleetContractJson.MaximumLanes)
            .Select(static lane => new FleetLaneSummary(
                Bound(lane.Lane, 64), lane.Required, lane.PendingCount, lane.PendingBytes,
                lane.LeasedCount, lane.QuarantineCount, lane.PressureLevel, lane.OldestPendingUtc))
            .ToArray();
        var heartbeatQueue = Queue(
            heartbeatOutbox.QuarantineCount > 0 || heartbeatOutbox.BlockedCount > 0 || heartbeatOutbox.OverflowCount > 0
                ? FleetAvailability.Unavailable
                : heartbeatOutbox.RetryCount > 0 ? FleetAvailability.Degraded : FleetAvailability.Available,
            heartbeatOutbox.QuarantineCount > 0 ? "quarantined" : heartbeatOutbox.BlockedCount > 0 ? "credentials-blocked" :
                heartbeatOutbox.OverflowCount > 0 ? "overflow-evidence" : heartbeatOutbox.RetryCount > 0 ? "retry" : "ready",
            heartbeatOutbox.PendingCount, heartbeatOutbox.PendingBytes, heartbeatOutbox.LeasedCount,
            heartbeatOutbox.RetryCount, heartbeatOutbox.QuarantineCount, heartbeatOutbox.OldestPendingUtc,
            heartbeatOutbox.EvaluatedUtc);
        var checks = new[]
        {
            Check("capture", runtime.Capture.Availability, runtime.Capture.Reason),
            Check("ingress", Availability(ingress.Availability), ingress.Reason),
            Check("lanes", Availability(lanes.Availability), lanes.Reason),
            Check("processing", Availability(processing.Availability), processing.Reason),
            Check("artifact-outbox", Availability(artifactOutbox.Availability), artifactOutbox.FailureReason ?? "ready"),
            Check("heartbeat-outbox", heartbeatQueue.Availability, heartbeatQueue.Reason),
            new FleetHealthCheckSummary(
                "storage",
                storage.Any(static item => item.FailureReason is not null) ? FleetHealth.Unhealthy :
                    storage.Any(static item => item.IsUnderPressure) ? FleetHealth.Degraded : FleetHealth.Healthy,
                storage.Any(static item => item.FailureReason is not null) ? "probe-failure" :
                    storage.Any(static item => item.IsUnderPressure) ? "pressure" : "ready")
        };
        var overall = checks.Any(static check => check.Status == FleetHealth.Unhealthy)
            ? FleetHealth.Unhealthy
            : checks.Any(static check => check.Status == FleetHealth.Degraded)
                ? FleetHealth.Degraded
                : FleetHealth.Healthy;
        var telemetry = captureTelemetryProvider.Latest;
        var processRuntime = SampleProcessRuntime();

        return new FleetStatusReportV1(
            FleetStatusReportV1.CurrentSchemaVersion,
            agentInstanceId,
            bootSessionId,
            sequence,
            now,
            processStartedAtUtc,
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                ?? "unknown",
            new FleetConfigurationIdentity(
                "1",
                CaptureContractJson.ComputeCanonicalJsonSha256(new
                {
                    configuration.AgentId,
                    configuration.Module,
                    configuration.Rig,
                    ProcessingSteps = configuration.ResolveProcessingSteps(),
                    Location = configuration.DeploymentLocation?.ToProvenance()
                }),
                Bound(configuration.ModuleType, 64),
                Bound(configuration.Rig.ProfileVersion, 64),
                CameraRigProfileIdentity.ComputeSha256(configuration.Rig),
                CaptureContractJson.ComputeCanonicalJsonSha256(configuration.ResolveProcessingSteps())),
            runtime.Capture,
            new FleetRuntimeSummary(
                processRuntime.CpuPercent,
                processRuntime.WorkingSetBytes,
                telemetry?.TemperatureC is { } temperature && double.IsFinite(temperature) ? temperature : null,
                telemetry?.TemperatureC is not null ? telemetry.StartedUtc : null),
            Queue(
                Availability(ingress.Availability), ingress.Reason, ingress.PendingCount, ingress.PendingBytes,
                0, 0, ingress.QuarantineCount, ingress.OldestPendingUtc, ingress.EvaluatedUtc),
            Queue(
                Availability(processing.Availability), processing.Reason, processing.PendingCount, 0,
                0, processing.RetryCount, processing.TerminalCount, processing.OldestPendingUtc, now),
            Queue(
                Availability(artifactOutbox.Availability), artifactOutbox.FailureReason ?? "ready",
                artifactOutbox.PendingCount, artifactOutbox.PendingBytes, artifactOutbox.LeasedCount,
                artifactOutbox.RetryCount, artifactOutbox.QuarantineCount, artifactOutbox.OldestPendingUtc, now),
            heartbeatQueue,
            fleetLanes,
            storage,
            runtime.Timings,
            overall,
            checks);
    }

    private (double? CpuPercent, long WorkingSetBytes) SampleProcessRuntime()
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var timestamp = timeProvider.GetTimestamp();
        lock (_processGate)
        {
            double? percent = null;
            if (_hasProcessSample)
            {
                var elapsed = timeProvider.GetElapsedTime(_lastProcessTimestamp, timestamp);
                var cpuElapsed = cpu - _lastProcessCpu;
                if (elapsed > TimeSpan.Zero && cpuElapsed >= TimeSpan.Zero)
                {
                    percent = Math.Clamp(
                        100d * cpuElapsed.TotalSeconds / (elapsed.TotalSeconds * Environment.ProcessorCount),
                        0,
                        100);
                }
            }
            _lastProcessCpu = cpu;
            _lastProcessTimestamp = timestamp;
            _hasProcessSample = true;
            return (percent, process.WorkingSet64);
        }
    }

    public static string ComputeStateFingerprint(FleetStatusReportV1 report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var categorical = new
        {
            report.Configuration.ConfigurationSha256,
            report.Capture.Availability,
            report.Capture.Reason,
            report.OverallHealth,
            Checks = report.HealthChecks.Select(static check => new { check.Name, check.Status, check.Reason }),
            Lanes = report.Lanes.Select(static lane => new { lane.Name, lane.Required, lane.PressureLevel, HasQuarantine = lane.QuarantineCount > 0 }),
            Storage = report.Storage.Select(static storage => new { storage.Name, storage.IsUnderPressure, HasFailure = storage.FailureReason is not null }),
            Ingress = new { report.Ingress.Availability, report.Ingress.Reason, HasQuarantine = report.Ingress.QuarantineCount > 0 },
            Processing = new { report.Processing.Availability, report.Processing.Reason, HasTerminal = report.Processing.QuarantineCount > 0 },
            Artifact = new { report.ArtifactOutbox.Availability, report.ArtifactOutbox.Reason, HasQuarantine = report.ArtifactOutbox.QuarantineCount > 0 }
        };
        return CaptureContractJson.ComputeCanonicalJsonSha256(categorical);
    }

    private static FleetQueueSummary Queue(
        FleetAvailability availability,
        string reason,
        long pendingCount,
        long pendingBytes,
        long leasedCount,
        long retryCount,
        long quarantineCount,
        DateTimeOffset? oldestPendingUtc,
        DateTimeOffset evaluatedUtc)
        => new(availability, Bound(reason, 128), pendingCount, pendingBytes, leasedCount, retryCount,
            quarantineCount, oldestPendingUtc, evaluatedUtc);

    private static FleetHealthCheckSummary Check(string name, FleetAvailability availability, string reason)
        => new(name, availability switch
        {
            FleetAvailability.Available => FleetHealth.Healthy,
            FleetAvailability.Initializing or FleetAvailability.Degraded => FleetHealth.Degraded,
            _ => FleetHealth.Unhealthy
        }, Bound(reason, 128));

    private static FleetAvailability Availability(RawIngressAvailability value) => value switch
    {
        RawIngressAvailability.Accepting => FleetAvailability.Available,
        RawIngressAvailability.Initializing => FleetAvailability.Initializing,
        RawIngressAvailability.Degraded => FleetAvailability.Degraded,
        _ => FleetAvailability.Unavailable
    };

    private static FleetAvailability Availability(CaptureLaneAvailability value) => value switch
    {
        CaptureLaneAvailability.Healthy => FleetAvailability.Available,
        CaptureLaneAvailability.Initializing => FleetAvailability.Initializing,
        CaptureLaneAvailability.Degraded => FleetAvailability.Degraded,
        _ => FleetAvailability.Unavailable
    };

    private static FleetAvailability Availability(CaptureProcessingAvailability value) => value switch
    {
        CaptureProcessingAvailability.Healthy => FleetAvailability.Available,
        CaptureProcessingAvailability.Degraded => FleetAvailability.Degraded,
        _ => FleetAvailability.Unavailable
    };

    private static FleetAvailability Availability(ArtifactOutboxAvailability value) => value switch
    {
        ArtifactOutboxAvailability.Healthy => FleetAvailability.Available,
        ArtifactOutboxAvailability.Initializing => FleetAvailability.Initializing,
        ArtifactOutboxAvailability.Degraded => FleetAvailability.Degraded,
        _ => FleetAvailability.Unavailable
    };

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static string? BoundOptional(string? value, int maximum) => value is null ? null : Bound(value, maximum);
}
