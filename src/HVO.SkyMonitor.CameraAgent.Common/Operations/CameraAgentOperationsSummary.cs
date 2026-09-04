using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;

namespace HVO.SkyMonitor.CameraAgent.Common.Operations;



public sealed record OperationsSection<T>(
    string Source,
    DateTimeOffset? ObservedUtc,
    string Freshness,
    T Value);

public sealed record OperationsQueueState(
    string Availability,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    long TerminalCount,
    DateTimeOffset? OldestPendingUtc);

public sealed record OperationsLaneState(
    string Name,
    bool Required,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    int PressureLevel,
    DateTimeOffset? OldestPendingUtc,
    IReadOnlyList<OperationsPendingCapture> PendingCaptures);

public sealed record OperationsPendingCapture(string AgentId, long CaptureSequence);

public sealed record OperationsCaptureLanesState(
    string Availability,
    IReadOnlyList<OperationsLaneState> Lanes,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    DateTimeOffset? OldestPendingUtc);

public sealed record OperationsStorageState(
    string Alias,
    long TotalBytes,
    long AvailableBytes,
    bool IsUnderPressure,
    int EffectiveRetentionDays,
    bool ProbeSucceeded);

public sealed record OperationsCaptureRuntimeState(
    string Availability,
    DateTimeOffset? LastSucceededUtc,
    DateTimeOffset? LastFailedUtc,
    DateTimeOffset? LastRecoveredUtc,
    IReadOnlyList<OperationsTimingState> Timings);

public sealed record OperationsTimingState(
    string Segment,
    long SampleCount,
    double MedianMilliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds);

public sealed record OperationsHeartbeatState(
    string Availability,
    DateTimeOffset? LastAcknowledgedUtc,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    long OverflowCount,
    long BlockedCount,
    DateTimeOffset? OldestPendingUtc);

public sealed record OperationsEnvironmentalDeliveryState(
    string Availability,
    DateTimeOffset? LastAcknowledgedUtc,
    long StoredCount,
    long StoredBytes,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    long TerminalCount,
    long OverflowCount,
    DateTimeOffset? OldestPendingUtc);

/// <summary>
/// Bounded sanitized status for the durable graph-execution evidence export lane. Every member is a counter, a
/// bounded reason code, or a state name; no identity, payload, path, or credential is exposed.
/// </summary>
public sealed record OperationsExecutionEvidenceExportState(
    string Availability,
    string ReasonCode,
    long PendingCount,
    long PendingBytes,
    long RetryCount,
    long QuarantineCount,
    long AbandonedCount,
    long AcknowledgedCount,
    long TotalAttempts,
    long ConflictCount,
    long RejectCount,
    long DrainedCount,
    long ResyncRequestCount,
    long SourcePrunedCount,
    long StorageBytes,
    long HighestSequence,
    long AcknowledgedThroughSequence,
    int InFlightRequests,
    bool StoragePressure,
    string? NegotiatedSchemaVersion,
    DateTimeOffset? LastAcknowledgementUtc,
    DateTimeOffset? OldestPendingUtc);

public sealed record OperationsTransientWorkerState(
    string Availability,
    long PendingFrames,
    long PendingCandidates,
    int MaximumCandidates);

public sealed record OperationsCaptureTelemetryState(
    int SampleCount,
    DateTimeOffset? LatestCaptureStartedUtc,
    string? LatestMode,
    double? LatestExposureMilliseconds,
    double? LatestGain,
    double AverageIntervalMilliseconds,
    double AverageExposureMilliseconds,
    double AverageProcessingMilliseconds,
    double AverageLoopMilliseconds,
    double CapturesPerMinute,
    double DutyCycle,
    int FramesStored,
    int ImmediateUploadCount);

public sealed record OperationsConfigurationState(
    bool IsCurrent,
    string ValidationStatus,
    string? AgentId,
    string? ModuleType,
    string CentralIntegration,
    string TransientDetection);

public sealed record OperationsCaptureControlState(
    string State,
    long Version,
    bool IsInitialized);

public sealed record CameraAgentOperationsSummary(
    DateTimeOffset CapturedUtc,
    OperationsSection<OperationsCaptureControlState> CaptureControl,
    OperationsSection<OperationsQueueState> RawIngress,
    OperationsSection<OperationsCaptureLanesState> CaptureLanes,
    OperationsSection<OperationsQueueState> CaptureProcessing,
    OperationsSection<OperationsQueueState> ArtifactOutbox,
    OperationsSection<IReadOnlyList<OperationsStorageState>> Storage,
    OperationsSection<OperationsCaptureRuntimeState> CaptureRuntime,
    OperationsSection<OperationsHeartbeatState> Heartbeat,
    OperationsSection<OperationsEnvironmentalDeliveryState> EnvironmentalDelivery,
    OperationsSection<OperationsExecutionEvidenceExportState> ExecutionEvidenceExport,
    OperationsSection<OperationsTransientWorkerState> TransientWorker,
    OperationsSection<OperationsCaptureTelemetryState> CaptureTelemetry,
    OperationsSection<OperationsConfigurationState> Configuration);

public sealed class CameraAgentOperationsSummaryProvider(
    TimeProvider timeProvider,
    CaptureAdmissionCoordinator captureControl,
    RawIngressState rawIngress,
    IOperationsQueueSnapshotRefresher operationsQueueSnapshotRefresher,
    CaptureLaneState captureLanes,
    CaptureProcessingState captureProcessing,
    ArtifactOutboxState artifactOutbox,
    StoragePressureState storagePressure,
    FleetRuntimeState fleetRuntime,
    FleetHeartbeatState heartbeat,
    EnvironmentalObservationDeliveryState environmentalDelivery,
    ExecutionEvidenceExportState executionEvidenceExport,
    TransientWorkerState transientWorker,
    ICaptureTelemetryProvider captureTelemetry,
    ICameraAgentConfigurationAccessor configurationAccessor,
    CameraAgentStorageResolver storageResolver,
    IOptions<CameraAgentHostOptions> hostOptions)
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public async ValueTask<CameraAgentOperationsSummary> GetAsync(CancellationToken cancellationToken)
    {
        await operationsQueueSnapshotRefresher.RefreshOperationsQueueSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var control = captureControl.Snapshot;
        var ingress = rawIngress.Snapshot;
        var lanes = captureLanes.Snapshot;
        var processing = captureProcessing.Snapshot;
        var outbox = artifactOutbox.Snapshot;
        var storage = storagePressure.Snapshots;
        var runtime = fleetRuntime.Snapshot;
        var heartbeatSnapshot = heartbeat.Snapshot;
        var environmental = environmentalDelivery.Snapshot;
        var evidenceExport = executionEvidenceExport.Snapshot;
        var transient = transientWorker.Snapshot;
        var telemetry = captureTelemetry.GetSnapshot();
        var latest = telemetry.Samples.Count == 0 ? null : telemetry.Samples[^1];
        var centralDisabled = hostOptions.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled;
        var config = configurationAccessor.IsConfigured
            ? await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var locations = config is null
            ? []
            : await storageResolver.GetStorageLocationsAsync(cancellationToken).ConfigureAwait(false);

        return new CameraAgentOperationsSummary(
            now,
            Section("durable-capture-control", control.UpdatedUtc, now, new OperationsCaptureControlState(
                control.State.ToString(), control.Version, control.IsInitialized)),
            Section("raw-ingress-state", ingress.EvaluatedUtc, now, new OperationsQueueState(
                ingress.Availability.ToString(), ingress.PendingCount, ingress.PendingBytes, 0, 0,
                ingress.QuarantineCount, 0, ingress.OldestPendingUtc)),
            Section("capture-lane-state", lanes.EvaluatedUtc, now, new OperationsCaptureLanesState(
                lanes.Availability.ToString(),
                lanes.Lanes.Select(static lane => new OperationsLaneState(
                    lane.Lane, lane.Required, lane.PendingCount, lane.PendingBytes, lane.LeasedCount,
                    lane.RetryCount, lane.QuarantineCount, lane.PressureLevel, lane.OldestPendingUtc,
                    lane.PendingCaptures.Select(static capture => new OperationsPendingCapture(
                        capture.AgentId,
                        capture.CaptureSequence)).ToArray())).ToArray(),
                lanes.PendingCount, lanes.PendingBytes, lanes.LeasedCount, lanes.RetryCount, lanes.QuarantineCount,
                lanes.OldestPendingUtc)),
            Section("durable-processing-refresh", processing.EvaluatedUtc, now, new OperationsQueueState(
                processing.Availability.ToString(), processing.PendingCount, 0, 0, processing.RetryCount,
                0, processing.TerminalCount, processing.OldestPendingUtc)),
            Section("artifact-outbox-state", outbox.EvaluatedUtc, now, new OperationsQueueState(
                centralDisabled ? "Disabled" : outbox.Availability.ToString(),
                centralDisabled ? 0 : outbox.PendingCount,
                centralDisabled ? 0 : outbox.PendingBytes,
                centralDisabled ? 0 : outbox.LeasedCount,
                centralDisabled ? 0 : outbox.RetryCount,
                centralDisabled ? 0 : outbox.QuarantineCount,
                0,
                centralDisabled ? null : outbox.OldestPendingUtc)),
            Section("storage-pressure-state", Latest(storage.Select(static item => (DateTimeOffset?)item.EvaluatedUtc)), now,
                (IReadOnlyList<OperationsStorageState>)storage
                    .Select(item => new { Snapshot = item, Location = locations.SingleOrDefault(location => PathsEqual(location.Root, item.StorageRoot)) })
                    .Where(static item => item.Location is not null)
                    .OrderBy(static item => item.Location!.Alias, StringComparer.Ordinal)
                    .Select(static item => new OperationsStorageState(
                        item.Location!.Alias, item.Snapshot.Capacity.TotalBytes, item.Snapshot.Capacity.AvailableBytes,
                        item.Snapshot.IsUnderPressure, item.Snapshot.EffectiveRetentionDays, item.Snapshot.ProbeFailure is null))
                    .ToArray()),
            Section("fleet-runtime-state", Latest([
                runtime.Capture.LastSucceededUtc,
                runtime.Capture.LastFailedUtc,
                runtime.Capture.LastRecoveredUtc]), now, new OperationsCaptureRuntimeState(
                runtime.Capture.Availability.ToString(), runtime.Capture.LastSucceededUtc,
                runtime.Capture.LastFailedUtc, runtime.Capture.LastRecoveredUtc,
                runtime.Timings.Select(static timing => new OperationsTimingState(
                    timing.Segment.ToString(), timing.SampleCount, timing.MedianMilliseconds,
                    timing.P95Milliseconds, timing.MaximumMilliseconds)).ToArray())),
            Section("fleet-heartbeat-state", heartbeatSnapshot.Outbox?.EvaluatedUtc, now, new OperationsHeartbeatState(
                centralDisabled ? "Disabled" : heartbeatSnapshot.Availability.ToString(),
                centralDisabled ? null : heartbeatSnapshot.LastAcknowledgedUtc,
                centralDisabled ? 0 : heartbeatSnapshot.Outbox?.PendingCount ?? 0,
                centralDisabled ? 0 : heartbeatSnapshot.Outbox?.PendingBytes ?? 0,
                centralDisabled ? 0 : heartbeatSnapshot.Outbox?.LeasedCount ?? 0,
                centralDisabled ? 0 : heartbeatSnapshot.Outbox?.RetryCount ?? 0,
                centralDisabled ? 0 : heartbeatSnapshot.Outbox?.QuarantineCount ?? 0,
                centralDisabled ? 0 : heartbeatSnapshot.Outbox?.OverflowCount ?? 0,
                centralDisabled ? 0 : heartbeatSnapshot.Outbox?.BlockedCount ?? 0,
                centralDisabled ? null : heartbeatSnapshot.Outbox?.OldestPendingUtc)),
            Section("environmental-delivery-state", environmental.Outbox?.EvaluatedUtc, now,
                new OperationsEnvironmentalDeliveryState(
                    centralDisabled ? "Disabled" : environmental.Availability.ToString(),
                    centralDisabled ? null : environmental.LastAcknowledgedUtc,
                    centralDisabled ? 0 : environmental.Outbox?.StoredCount ?? 0,
                    centralDisabled ? 0 : environmental.Outbox?.StoredBytes ?? 0,
                    centralDisabled ? 0 : environmental.Outbox?.PendingCount ?? 0,
                    centralDisabled ? 0 : environmental.Outbox?.PendingBytes ?? 0,
                    centralDisabled ? 0 : environmental.Outbox?.LeasedCount ?? 0,
                    centralDisabled ? 0 : environmental.Outbox?.RetryCount ?? 0,
                    centralDisabled ? 0 : environmental.Outbox?.QuarantineCount ?? 0,
                    centralDisabled ? 0 : environmental.Outbox?.TerminalCount ?? 0,
                    centralDisabled ? 0 : environmental.Outbox?.OverflowCount ?? 0,
                    centralDisabled ? null : environmental.Outbox?.OldestPendingUtc)),
            Section("execution-evidence-export-state", evidenceExport.EvaluatedUtc, now,
                new OperationsExecutionEvidenceExportState(
                    centralDisabled ? "Disabled" : evidenceExport.Availability.ToString(),
                    centralDisabled ? ExecutionEvidenceExportReasonCodes.Disabled : evidenceExport.ReasonCode,
                    centralDisabled ? 0 : evidenceExport.Backlog.PendingCount,
                    centralDisabled ? 0 : evidenceExport.Backlog.PendingBytes,
                    centralDisabled ? 0 : evidenceExport.Backlog.RetryCount,
                    centralDisabled ? 0 : evidenceExport.Backlog.QuarantinedCount,
                    centralDisabled ? 0 : evidenceExport.Backlog.AbandonedCount,
                    centralDisabled ? 0 : evidenceExport.Backlog.AcknowledgedCount,
                    centralDisabled ? 0 : evidenceExport.Backlog.TotalAttempts,
                    centralDisabled ? 0 : evidenceExport.ConflictUnits,
                    centralDisabled ? 0 : evidenceExport.RejectedUnits,
                    centralDisabled ? 0 : evidenceExport.DrainedUnits,
                    centralDisabled ? 0 : evidenceExport.ResyncRequests,
                    centralDisabled ? 0 : evidenceExport.Backlog.SourcePrunedEvents,
                    centralDisabled ? 0 : evidenceExport.Backlog.DatabaseBytes,
                    centralDisabled ? 0 : evidenceExport.Backlog.HighestSequence,
                    centralDisabled ? 0 : evidenceExport.Backlog.AcknowledgedThroughSequence,
                    centralDisabled ? 0 : evidenceExport.InFlightRequests,
                    !centralDisabled && evidenceExport.StoragePressure,
                    centralDisabled ? null : evidenceExport.NegotiatedSchemaVersion,
                    centralDisabled ? null : evidenceExport.LastAcknowledgementUtc,
                    centralDisabled ? null : evidenceExport.Backlog.OldestPendingUtc)),
            Section("transient-worker-state", transient.UpdatedUtc, now, new OperationsTransientWorkerState(
                transient.Availability.ToString(), transient.PendingFrames, transient.PendingCandidates,
                TransientCandidateExtractionProfiles.EdgeV1.MaximumCandidates)),
            Section("capture-telemetry-window", latest?.StartedUtc, now, new OperationsCaptureTelemetryState(
                telemetry.Samples.Count, latest?.StartedUtc, latest?.Mode.ToString(), latest?.Exposure.TotalMilliseconds,
                latest?.Gain, telemetry.Aggregate.AverageIntervalMilliseconds,
                telemetry.Aggregate.AverageExposureMilliseconds, telemetry.Aggregate.AverageProcessingMilliseconds,
                telemetry.Aggregate.AverageLoopMilliseconds, telemetry.Aggregate.CapturesPerMinute,
                telemetry.Aggregate.DutyCycle, telemetry.Aggregate.FramesStored,
                telemetry.Aggregate.ImmediateUploadCount)),
            Section("validated-configuration", null, now, new OperationsConfigurationState(
                config is not null, config is null ? "unavailable" : "validated", config?.AgentId,
                config?.ModuleType, hostOptions.Value.CentralIntegration.Mode.ToString(),
                hostOptions.Value.TransientDetection.Mode.ToString())));
    }

    private static OperationsSection<T> Section<T>(
        string source,
        DateTimeOffset? observedUtc,
        DateTimeOffset now,
        T value)
        => new(source, observedUtc, Freshness(observedUtc, now), value);

    private static string Freshness(DateTimeOffset? observedUtc, DateTimeOffset now)
        => observedUtc is null
            ? "unknown"
            : now - observedUtc.Value > StaleAfter
                ? "stale"
                : "fresh";

    internal static DateTimeOffset? Latest(IEnumerable<DateTimeOffset?> timestamps)
        => timestamps.Max();

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
