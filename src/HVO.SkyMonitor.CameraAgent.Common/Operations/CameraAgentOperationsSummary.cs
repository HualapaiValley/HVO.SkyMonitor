using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Operations;



public sealed record OperationsSection<T>(
    string Source,
    DateTimeOffset? ObservedUtc,
    string Freshness,
    T Value);

/// <summary>
/// The freshness vocabulary of an operations section. <c>fresh</c>/<c>stale</c> are derived from
/// the observation time, except capture control, which is live state and always <c>fresh</c>;
/// <c>unknown</c> means an observation was expected and none exists;
/// <c>disabled</c> means the subsystem is switched off by configuration; <c>static</c> means the
/// value is a startup fact that is never re-observed.
/// </summary>
public static class OperationsFreshness
{
    public const string Fresh = "fresh";
    public const string Stale = "stale";
    public const string Unknown = "unknown";
    public const string Disabled = "disabled";
    public const string Static = "static";
}

public sealed record OperationsQueueState(
    string Availability,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    long TerminalCount,
    DateTimeOffset? OldestPendingUtc);

/// <summary>
/// Archived replay in the queue shape of <see cref="OperationsQueueState"/>, plus the replays abandoned because this
/// version no longer builds their frozen plan. Abandonment is final but not a failure, so it is never part of
/// <see cref="TerminalCount"/>.
/// </summary>
public sealed record OperationsArchivedReplayState(
    string Availability,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    long TerminalCount,
    DateTimeOffset? OldestPendingUtc,
    long AbandonedCount);

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
    int FramesStored);

public sealed record OperationsConfigurationState(
    bool IsCurrent,
    string ValidationStatus,
    string? AgentId,
    string? ModuleType,
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
    OperationsSection<IReadOnlyList<OperationsStorageState>> Storage,
    OperationsSection<OperationsCaptureRuntimeState> CaptureRuntime,
    OperationsSection<OperationsTransientWorkerState> TransientWorker,
    OperationsSection<OperationsCaptureTelemetryState> CaptureTelemetry,
    OperationsSection<OperationsConfigurationState> Configuration,
    OperationsSection<OperationsArchivedReplayState> ArchivedReplay);

public sealed class CameraAgentOperationsSummaryProvider(
    TimeProvider timeProvider,
    CaptureAdmissionCoordinator captureControl,
    RawIngressState rawIngress,
    IOperationsQueueSnapshotRefresher operationsQueueSnapshotRefresher,
    CaptureLaneState captureLanes,
    CaptureProcessingState captureProcessing,
    StoragePressureState storagePressure,
    CaptureRuntimeState captureRuntime,
    TransientWorkerState transientWorker,
    ICaptureTelemetryProvider captureTelemetry,
    ICameraAgentConfigurationAccessor configurationAccessor,
    CameraAgentStorageResolver storageResolver,
    IOptions<CameraAgentHostOptions> hostOptions,
    CaptureScheduleRuntimeCoordinator? scheduleRuntime = null)
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
        var storage = storagePressure.Snapshots;
        var runtime = captureRuntime.Snapshot;
        var transient = transientWorker.Snapshot;
        var telemetry = captureTelemetry.GetSnapshot();
        var latest = telemetry.Samples.Count == 0 ? null : telemetry.Samples[^1];
        var transientDisabled = hostOptions.Value.TransientDetection.Mode == TransientOperatingMode.Off;
        var startupConfig = configurationAccessor.IsConfigured
            ? await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var config = scheduleRuntime?.Snapshot?.Configuration ?? startupConfig;
        var locations = startupConfig is null
            ? []
            : await storageResolver.GetStorageLocationsAsync(cancellationToken).ConfigureAwait(false);

        return new CameraAgentOperationsSummary(
            now,
            // Capture control is the coordinator's live state, read now, so it is always current; its time is when the
            // state last changed, which a camera left running for hours never does, not when it was observed (#1384).
            Section("durable-capture-control", OperationsFreshness.Fresh, control.UpdatedUtc, new OperationsCaptureControlState(
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
                processing.Availability.ToString(), processing.PendingCount, 0, processing.LeasedCount, processing.RetryCount,
                0, processing.TerminalCount, processing.OldestPendingUtc)),
            Section("storage-pressure-state", Latest(storage.Select(static item => (DateTimeOffset?)item.EvaluatedUtc)), now,
                (IReadOnlyList<OperationsStorageState>)storage
                    .Select(item => new { Snapshot = item, Location = locations.SingleOrDefault(location => PathsEqual(location.Root, item.StorageRoot)) })
                    .Where(static item => item.Location is not null)
                    .OrderBy(static item => item.Location!.Alias, StringComparer.Ordinal)
                    .Select(static item => new OperationsStorageState(
                        item.Location!.Alias, item.Snapshot.Capacity.TotalBytes, item.Snapshot.Capacity.AvailableBytes,
                        item.Snapshot.IsUnderPressure, item.Snapshot.EffectiveRetentionDays, item.Snapshot.ProbeFailure is null))
                    .ToArray()),
            Section("capture-runtime-state", Latest([
                runtime.Capture.LastSucceededUtc,
                runtime.Capture.LastFailedUtc,
                runtime.Capture.LastRecoveredUtc]), now, new OperationsCaptureRuntimeState(
                runtime.Capture.Availability.ToString(), runtime.Capture.LastSucceededUtc,
                runtime.Capture.LastFailedUtc, runtime.Capture.LastRecoveredUtc,
                runtime.Timings.Select(static timing => new OperationsTimingState(
                    timing.Segment.ToString(), timing.SampleCount, timing.MedianMilliseconds,
                    timing.P95Milliseconds, timing.MaximumMilliseconds)).ToArray())),
            Section("transient-worker-state", transientDisabled, transient.UpdatedUtc, now, new OperationsTransientWorkerState(
                transient.Availability.ToString(), transient.PendingFrames, transient.PendingCandidates,
                TransientCandidateExtractionProfiles.EdgeV1.MaximumCandidates)),
            Section("capture-telemetry-window", latest?.StartedUtc, now, new OperationsCaptureTelemetryState(
                telemetry.Samples.Count, latest?.StartedUtc, latest?.Mode.ToString(), latest?.Exposure.TotalMilliseconds,
                latest?.Gain, telemetry.Aggregate.AverageIntervalMilliseconds,
                telemetry.Aggregate.AverageExposureMilliseconds, telemetry.Aggregate.AverageProcessingMilliseconds,
                telemetry.Aggregate.AverageLoopMilliseconds, telemetry.Aggregate.CapturesPerMinute,
                telemetry.Aggregate.DutyCycle, telemetry.Aggregate.FramesStored)),
            Section("validated-configuration", OperationsFreshness.Static, null, new OperationsConfigurationState(
                config is not null, config is null ? "unavailable" : "validated", startupConfig?.AgentId,
                startupConfig?.ModuleType,
                hostOptions.Value.TransientDetection.Mode.ToString())),
            // Archived replay shares the processing refresh but is reported apart from live processing: the
            // lifecycle drain waits on live leases only, and records replay in its continuity boundary.
            Section("durable-processing-refresh", processing.EvaluatedUtc, now, new OperationsArchivedReplayState(
                processing.Availability.ToString(), processing.ReplayPendingCount, processing.ReplayPendingBytes,
                processing.ReplayLeasedCount, processing.ReplayRetryCount, 0, processing.ReplayTerminalCount,
                processing.OldestReplayPendingUtc, processing.ReplayAbandonedCount)));
    }

    private static OperationsSection<T> Section<T>(
        string source,
        DateTimeOffset? observedUtc,
        DateTimeOffset now,
        T value)
        => new(source, observedUtc, Freshness(observedUtc, now), value);

    /// <summary>
    /// A section whose subsystem is switched off by configuration. Its facts will not change while
    /// it is off, so neither "stale" nor "unknown" describes it; the operator reads "disabled" and
    /// the overall state does not degrade on its account. The observation time is kept: the
    /// subsystem did record that it is disabled at that instant, and consumers that bound a section
    /// by its observation time (the deployment drain reads the transient worker this way) keep
    /// that bound.
    /// </summary>
    private static OperationsSection<T> Section<T>(
        string source,
        bool disabled,
        DateTimeOffset? observedUtc,
        DateTimeOffset now,
        T value)
        => disabled
            ? new(source, observedUtc, OperationsFreshness.Disabled, value)
            : new(source, observedUtc, Freshness(observedUtc, now), value);

    /// <summary>A section with an explicit freshness that is not derived from an observation time.</summary>
    private static OperationsSection<T> Section<T>(
        string source,
        string freshness,
        DateTimeOffset? observedUtc,
        T value)
        => new(source, observedUtc, freshness, value);

    private static string Freshness(DateTimeOffset? observedUtc, DateTimeOffset now)
        => observedUtc is null
            ? OperationsFreshness.Unknown
            : now - observedUtc.Value > StaleAfter
                ? OperationsFreshness.Stale
                : OperationsFreshness.Fresh;

    internal static DateTimeOffset? Latest(IEnumerable<DateTimeOffset?> timestamps)
        => timestamps.Max();

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
