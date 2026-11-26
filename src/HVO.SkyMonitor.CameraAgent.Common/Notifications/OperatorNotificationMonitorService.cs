using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Passive background observer that evaluates CameraAgent's bounded 5-event operational health catalog
/// (CaptureStalled, CameraDisconnected, DiskPressure, UpgradeOrRecoveryFailed, DailyHeartbeat) without ever touching
/// or blocking capture, processing, retention, login, or recovery critical paths. Performs zero work when disabled.
/// </summary>
public sealed class OperatorNotificationMonitorService(
    IOperatorNotificationConfigProvider configProvider,
    IOperatorNotificationPublisher publisher,
    IOptions<CameraAgentHostOptions> hostOptions,
    TimeProvider timeProvider,
    ILogger<OperatorNotificationMonitorService>? logger = null,
    FleetRuntimeState? fleetRuntimeState = null,
    ICaptureTelemetryProvider? captureTelemetryProvider = null,
    CaptureAdmissionCoordinator? admissionCoordinator = null,
    CaptureScheduleRuntimeCoordinator? scheduleCoordinator = null,
    StoragePressureState? storagePressureState = null,
    ICameraAgentStorageResolver? storageResolver = null,
    CameraAgentControlStore? controlStore = null,
    IDeploymentLocationStore? deploymentLocationStore = null,
    ICameraAgentConfigurationLoader? configurationLoader = null,
    ICameraAgentConfigurationAccessor? configurationAccessor = null,
    SqliteNamedRigProfileStore? rigProfileStore = null,
    ISiteProfileStore? siteProfileStore = null,
    IOperatorNotificationHeartbeatStore? heartbeatStore = null) : BackgroundService
{
    private static readonly TimeSpan SnapshotLockTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FailedReceiptWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan DisabledPollDelay = TimeSpan.FromSeconds(15);
    private const int MaximumTrackedFailedControlOperations = 512;
    private const int RuntimeStorageFailureConfirmationEvaluations = 2;

    private readonly IOperatorNotificationConfigProvider _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    private readonly IOperatorNotificationPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly CameraAgentHostOptions _hostOptions = (hostOptions ?? throw new ArgumentNullException(nameof(hostOptions))).Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger _logger = logger ?? NullLogger<OperatorNotificationMonitorService>.Instance;
    private readonly DateTimeOffset _startedUtc = timeProvider.GetUtcNow();

    private readonly Dictionary<Guid, DateTimeOffset> _notifiedFailedControlOperations = [];
    private FleetRuntimeSnapshot? _lastKnownFleetSnapshot;
    private DateTimeOffset? _cameraDisconnectedSinceUtc;
    private DateTimeOffset? _runningSinceUtc;
    private DateOnly? _lastDispatchedHeartbeatSiteDate;
    private DateOnly? _lastPersistedHeartbeatSiteDate;
    private bool _heartbeatCheckpointLoaded;
    private int _consecutiveRuntimeStorageFailureEvaluations;
    private bool _heartbeatReadFailureActive;
    private bool _heartbeatWriteFailureActive;

    /// <summary>Internal test seam to simulate transient fleet-snapshot lock timeouts deterministically.</summary>
    internal Func<TimeSpan, (bool Success, FleetRuntimeSnapshot? Snapshot)>? FleetSnapshotReader { get; init; }

    /// <summary>Internal test visibility for bounded failed-control-operation dedup eviction.</summary>
    internal int TrackedFailedControlOperationCount => _notifiedFailedControlOperations.Count;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Passive operational health monitor must never crash the host or affect capture.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DisabledPollDelay;
            try
            {
                var options = _configProvider.CurrentOptions;
                if (options.Enabled &&
                    (options.Smtp.Enabled || options.Webhook.Enabled) &&
                    _configProvider.ConfigurationErrorReasonCode is null)
                {
                    await EvaluateOnceAsync(stoppingToken).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Clamp(options.EvaluationIntervalSeconds, 5, 600));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OperatorNotificationLog.MonitorEvaluationFailed(_logger, ex.GetType().Name);
            }

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Evaluates all five bounded operational health conditions once. Safe to call in deterministic tests.</summary>
    public async Task EvaluateOnceAsync(CancellationToken cancellationToken)
    {
        var options = _configProvider.CurrentOptions;
        if (!options.Enabled ||
            (!options.Smtp.Enabled && !options.Webhook.Enabled) ||
            _configProvider.ConfigurationErrorReasonCode is not null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var cameraName = await ResolveCameraNameAsync(cancellationToken).ConfigureAwait(false);

        FleetRuntimeSnapshot? fleetSnapshot = _lastKnownFleetSnapshot;
        var fleetSnapshotTimedOut = false;
        if (FleetSnapshotReader is not null)
        {
            var (success, captured) = FleetSnapshotReader(SnapshotLockTimeout);
            if (success)
            {
                fleetSnapshot = captured;
                _lastKnownFleetSnapshot = captured;
            }
            else
            {
                fleetSnapshotTimedOut = true;
            }
        }
        else if (fleetRuntimeState is not null)
        {
            if (fleetRuntimeState.TrySnapshot(SnapshotLockTimeout, out var captured))
            {
                fleetSnapshot = captured;
                _lastKnownFleetSnapshot = captured;
            }
            else
            {
                fleetSnapshotTimedOut = true;
            }
        }

        var admissionSnapshot = admissionCoordinator?.Snapshot;
        var admissionHeld = controlStore?.AdmissionHeld == true;
        var scheduleAdmitted = scheduleCoordinator?.Snapshot?.CurrentDecision?.Admitted;
        var isPausedOrDraining = admissionHeld ||
            admissionSnapshot is { State: CaptureAdmissionState.Paused or CaptureAdmissionState.PauseRequested };
        var isRunningAndAdmitted = !isPausedOrDraining &&
            (admissionSnapshot is null || (admissionSnapshot.IsInitialized && admissionSnapshot.State == CaptureAdmissionState.Running)) &&
            scheduleAdmitted != false;

        if (isRunningAndAdmitted)
        {
            _runningSinceUtc ??= now;
        }
        else
        {
            _runningSinceUtc = null;
        }

        var activeFaults = 0;

        // 1. Camera disconnected
        var cameraDisconnected = EvaluateCameraDisconnected(options, now, cameraName, fleetSnapshot, fleetSnapshotTimedOut, isPausedOrDraining);
        if (cameraDisconnected)
        {
            activeFaults++;
        }

        // 2. Capture stalled
        if (await EvaluateCaptureStalledAsync(options, now, cameraName, fleetSnapshot, isRunningAndAdmitted, cameraDisconnected, cancellationToken).ConfigureAwait(false))
        {
            activeFaults++;
        }

        // 3. Disk pressure
        if (await EvaluateDiskPressureAsync(options, now, cameraName, cancellationToken).ConfigureAwait(false))
        {
            activeFaults++;
        }

        // 4. Upgrade / recovery failed
        if (await EvaluateUpgradeOrRecoveryFailedAsync(options, now, cameraName, fleetSnapshot, admissionSnapshot, cancellationToken).ConfigureAwait(false))
        {
            activeFaults++;
        }

        // 5. Daily heartbeat
        await EvaluateDailyHeartbeatAsync(options, now, cameraName, fleetSnapshot, admissionSnapshot, activeFaults, cancellationToken).ConfigureAwait(false);
    }

    private bool EvaluateCameraDisconnected(
        OperatorNotificationsOptions options,
        DateTimeOffset now,
        string cameraName,
        FleetRuntimeSnapshot? fleetSnapshot,
        bool fleetSnapshotTimedOut,
        bool isPausedOrDraining)
    {
        if (isPausedOrDraining)
        {
            _cameraDisconnectedSinceUtc = null;
            return false;
        }

        var rigFailure = rigProfileStore?.ActiveRuntimeFailure;
        var rigIndicatesCameraFailure = fleetRuntimeState is null && IsCameraDisconnectRigFailure(rigFailure);

        // Preserve last known camera disconnect timer if the 50 ms fleet-snapshot read transiently timed out.
        if (fleetSnapshotTimedOut && !rigIndicatesCameraFailure)
        {
            return _cameraDisconnectedSinceUtc is not null;
        }

        // Rely on the single unified typed disconnect classification on FleetRuntimeState when present so
        // startup storage failures and non-disconnect AsiException errors never override IsCameraDisconnected == false.
        var disconnected = fleetRuntimeState is not null
            ? fleetRuntimeState.IsCameraDisconnected
            : rigIndicatesCameraFailure ||
              (fleetSnapshot is { Capture.Availability: FleetAvailability.Unavailable } &&
               IsCameraDisconnectReason(fleetSnapshot.Capture.Reason));

        if (!disconnected)
        {
            _cameraDisconnectedSinceUtc = null;
            return false;
        }

        _cameraDisconnectedSinceUtc ??= now;
        var disconnectedFor = now - _cameraDisconnectedSinceUtc.Value;
        var threshold = TimeSpan.FromSeconds(Math.Clamp(options.CameraDisconnectedThresholdSeconds, 0, 3600));
        if (disconnectedFor < threshold || !options.Events.CameraDisconnectedEnabled)
        {
            return true;
        }

        var reason = rigIndicatesCameraFailure
            ? OperatorNotificationRedactor.SanitizeOperationalText(rigFailure, 160)
            : OperatorNotificationRedactor.SanitizeOperationalText(fleetSnapshot?.Capture.Reason, 160);
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "Camera device disconnected";
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["availability"] = "Unavailable",
            ["reason"] = reason,
            ["durationSeconds"] = Math.Max(0, (long)disconnectedFor.TotalSeconds).ToString(CultureInfo.InvariantCulture)
        };

        _publisher.TryPublish(new OperatorNotificationEvent(
            OperatorNotificationEventKind.CameraDisconnected,
            OperatorNotificationSeverity.Critical,
            "camera_disconnected",
            "Camera disconnected",
            $"Camera module '{cameraName}' has been disconnected/unavailable for {FormatDuration(disconnectedFor)}. Reason: {reason}.",
            now,
            cameraName,
            details));
        return true;
    }

    internal static bool IsCameraDisconnectRigFailure(string? rigFailure)
    {
        if (string.IsNullOrWhiteSpace(rigFailure))
        {
            return false;
        }
        // Storage, publication, and non-disconnect initialization errors are not camera hardware disconnects.
        if (rigFailure.Contains("activation could not be published", StringComparison.OrdinalIgnoreCase) ||
            rigFailure.Contains("IOException", StringComparison.OrdinalIgnoreCase) ||
            rigFailure.Contains("SqliteException", StringComparison.OrdinalIgnoreCase) ||
            rigFailure.Contains("RawIngressConflictException", StringComparison.OrdinalIgnoreCase) ||
            rigFailure.Contains("InvalidDataException", StringComparison.OrdinalIgnoreCase) ||
            rigFailure.Contains("UnauthorizedAccessException", StringComparison.OrdinalIgnoreCase) ||
            rigFailure.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return IsCameraDisconnectReason(rigFailure);
    }

    internal static bool IsCameraDisconnectReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var trimmed = reason.Trim();
        if (string.Equals(trimmed, "null-result", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "activation-publication-failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "pause-publication-failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "InvalidOperationException", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "TimeoutException", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "IOException", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "SqliteException", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "InvalidDataException", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "UnauthorizedAccessException", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "RawIngressConflictException", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "AsiException", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return trimmed.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("removed", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("not connected", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("not-connected", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("usb", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("camera-closed", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("camera_closed", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("device-lost", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("device-unavailable", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("camera-unavailable", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> EvaluateCaptureStalledAsync(
        OperatorNotificationsOptions options,
        DateTimeOffset now,
        string cameraName,
        FleetRuntimeSnapshot? fleetSnapshot,
        bool isRunningAndAdmitted,
        bool cameraDisconnected,
        CancellationToken cancellationToken)
    {
        if (!isRunningAndAdmitted || cameraDisconnected)
        {
            return false;
        }

        var latestActivityUtc = ResolveLatestCaptureUtc(fleetSnapshot);
        var runningSince = _runningSinceUtc ?? _startedUtc;
        var referenceUtc = latestActivityUtc is { } latest && latest > runningSince
            ? latest
            : runningSince;
        var threshold = await ComputeCaptureStallThresholdAsync(options, cancellationToken).ConfigureAwait(false);
        var elapsed = now - referenceUtc;
        if (elapsed < threshold)
        {
            return false;
        }

        if (!options.Events.CaptureStalledEnabled)
        {
            return true;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["elapsedMinutes"] = Math.Round(elapsed.TotalMinutes, 1).ToString("0.0", CultureInfo.InvariantCulture),
            ["thresholdMinutes"] = Math.Round(threshold.TotalMinutes, 1).ToString("0.0", CultureInfo.InvariantCulture),
            ["lastCaptureUtc"] = latestActivityUtc?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "none"
        };

        var summary = latestActivityUtc is { } last && last > runningSince
            ? $"No successful capture has completed for '{cameraName}' in {FormatDuration(elapsed)} (last capture at {last.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z, threshold {FormatDuration(threshold)})."
            : $"No capture has succeeded for '{cameraName}' since capture admission became active {FormatDuration(elapsed)} ago (threshold {FormatDuration(threshold)}).";

        _publisher.TryPublish(new OperatorNotificationEvent(
            OperatorNotificationEventKind.CaptureStalled,
            OperatorNotificationSeverity.Warning,
            "capture_stalled",
            "Capture stalled",
            summary,
            now,
            cameraName,
            details));
        return true;
    }

    private async Task<bool> EvaluateDiskPressureAsync(
        OperatorNotificationsOptions options,
        DateTimeOffset now,
        string cameraName,
        CancellationToken cancellationToken)
    {
        if (storagePressureState is null)
        {
            return false;
        }

        var snapshots = storagePressureState.Snapshots;
        var affected = snapshots.Where(static s => s.IsUnderPressure || !string.IsNullOrWhiteSpace(s.ProbeFailure)).ToArray();
        if (affected.Length == 0)
        {
            return false;
        }

        if (!options.Events.DiskPressureEnabled)
        {
            return true;
        }

        IReadOnlyList<CameraAgentStorageLocation>? locations = null;
        if (storageResolver is not null && configurationAccessor?.IsConfigured == true)
        {
            try
            {
                locations = await storageResolver.GetStorageLocationsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                locations = null;
            }
        }

        foreach (var snapshot in affected)
        {
            var alias = ResolveSanitizedStorageAlias(snapshot.StorageRoot, locations);
            var isProbeFailure = !string.IsNullOrWhiteSpace(snapshot.ProbeFailure);
            var severity = isProbeFailure || snapshot.Capacity.AvailablePercent < 5.0
                ? OperatorNotificationSeverity.Critical
                : OperatorNotificationSeverity.Warning;

            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["volume"] = alias,
                ["state"] = isProbeFailure ? "probe-failed" : "under-pressure",
                ["effectiveRetentionDays"] = snapshot.EffectiveRetentionDays.ToString(CultureInfo.InvariantCulture)
            };
            if (!isProbeFailure && snapshot.Capacity.TotalBytes > 0)
            {
                details["availablePercent"] = snapshot.Capacity.AvailablePercent.ToString("0.0", CultureInfo.InvariantCulture);
            }

            var summary = isProbeFailure
                ? $"Storage volume '{alias}' on '{cameraName}' could not be probed for free space. Retention safety requires operator attention."
                : $"Storage volume '{alias}' on '{cameraName}' is under disk pressure ({snapshot.Capacity.AvailablePercent:0.0}% free; effective retention reduced to {snapshot.EffectiveRetentionDays} day(s)).";

            _publisher.TryPublish(new OperatorNotificationEvent(
                OperatorNotificationEventKind.DiskPressure,
                severity,
                $"disk_pressure:{alias}",
                $"Disk pressure ({alias})",
                summary,
                now,
                cameraName,
                details));
        }

        return true;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Reading optional control receipts must never throw out of the passive notification observer.")]
    private async Task<bool> EvaluateUpgradeOrRecoveryFailedAsync(
        OperatorNotificationsOptions options,
        DateTimeOffset now,
        string cameraName,
        FleetRuntimeSnapshot? fleetSnapshot,
        CaptureAdmissionSnapshot? admissionSnapshot,
        CancellationToken cancellationToken)
    {
        PruneExpiredFailedControlOperations(now);
        var anyFault = false;

        if (controlStore is not null)
        {
            try
            {
                var controlView = await controlStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (var receipt in controlView.Receipts)
                {
                    if (receipt.Phase != CameraAgentControlPhase.Failed)
                    {
                        continue;
                    }
                    var eventUtc = receipt.Events.Count > 0 ? receipt.Events[^1].Utc : receipt.Request.RequestedUtc;
                    if (now - eventUtc > FailedReceiptWindow)
                    {
                        continue;
                    }
                    anyFault = true;
                    if (_notifiedFailedControlOperations.ContainsKey(receipt.Request.OperationId))
                    {
                        continue;
                    }
                    if (!options.Events.UpgradeOrRecoveryFailedEnabled)
                    {
                        continue;
                    }

                    var code = receipt.Events.Count > 0
                        ? OperatorNotificationRedactor.SanitizeOperationalText(receipt.Events[^1].Code, 64)
                        : "lifecycle.failed";
                    var kind = receipt.Request.Kind.ToString();
                    var details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["operationKind"] = kind,
                        ["failureCode"] = string.IsNullOrEmpty(code) ? "lifecycle.failed" : code
                    };

                    var published = _publisher.TryPublish(new OperatorNotificationEvent(
                        OperatorNotificationEventKind.UpgradeOrRecoveryFailed,
                        OperatorNotificationSeverity.Critical,
                        $"upgrade_recovery_failed:control:{receipt.Request.OperationId:N}",
                        "Upgrade / lifecycle operation failed",
                        $"Installer lifecycle operation ({kind}) failed on '{cameraName}' with code '{details["failureCode"]}'. No automatic resume was issued; check Operations > System control.",
                        now,
                        cameraName,
                        details));
                    if (published)
                    {
                        RecordNotifiedFailedControlOperation(receipt.Request.OperationId, eventUtc);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                OperatorNotificationLog.MonitorEvaluationFailed(_logger, ex.GetType().Name);
            }
        }

        if (configurationLoader?.RetainedIntegrityRecoveryRequired == true)
        {
            anyFault = true;
            if (options.Events.UpgradeOrRecoveryFailedEnabled)
            {
                _publisher.TryPublish(new OperatorNotificationEvent(
                    OperatorNotificationEventKind.UpgradeOrRecoveryFailed,
                    OperatorNotificationSeverity.Critical,
                    "upgrade_recovery_failed:retained-integrity",
                    "Retained integrity recovery required",
                    $"CameraAgent '{cameraName}' reported that retained-integrity recovery is required before verification can complete.",
                    now,
                    cameraName,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "retained-integrity" }));
            }
        }

        if (deploymentLocationStore is { RecentRetainedEvidenceReady: false })
        {
            anyFault = true;
            if (options.Events.UpgradeOrRecoveryFailedEnabled)
            {
                _publisher.TryPublish(new OperatorNotificationEvent(
                    OperatorNotificationEventKind.UpgradeOrRecoveryFailed,
                    OperatorNotificationSeverity.Critical,
                    "upgrade_recovery_failed:retained-evidence",
                    "Retained evidence recovery incomplete",
                    $"CameraAgent '{cameraName}' reported that recent retained evidence is not ready after startup or recovery.",
                    now,
                    cameraName,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "deployment-location" }));
            }
        }

        if (fleetRuntimeState?.IsStorageFailure != true)
        {
            _consecutiveRuntimeStorageFailureEvaluations = 0;
        }

        if (admissionSnapshot is { IsInitialized: true, State: CaptureAdmissionState.Unavailable })
        {
            anyFault = true;
            if (options.Events.UpgradeOrRecoveryFailedEnabled)
            {
                _publisher.TryPublish(new OperatorNotificationEvent(
                    OperatorNotificationEventKind.UpgradeOrRecoveryFailed,
                    OperatorNotificationSeverity.Critical,
                    "upgrade_recovery_failed:capture-admission",
                    "Capture admission recovery unavailable",
                    $"Capture admission durable state is unavailable on '{cameraName}'. Inspect Operations > Health & diagnostics.",
                    now,
                    cameraName,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "capture-admission" }));
            }
        }
        else if (fleetRuntimeState?.IsStorageFailure == true)
        {
            var isStartupStorageFailure = fleetRuntimeState.IsStartupStorageFailure ||
                admissionSnapshot is { IsInitialized: false };
            if (!isStartupStorageFailure &&
                _consecutiveRuntimeStorageFailureEvaluations < RuntimeStorageFailureConfirmationEvaluations)
            {
                _consecutiveRuntimeStorageFailureEvaluations++;
            }

            if (isStartupStorageFailure ||
                _consecutiveRuntimeStorageFailureEvaluations >= RuntimeStorageFailureConfirmationEvaluations)
            {
                anyFault = true;
                if (options.Events.UpgradeOrRecoveryFailedEnabled)
                {
                    var storageReason = OperatorNotificationRedactor.SanitizeOperationalText(fleetSnapshot?.Capture.Reason, 64);
                    if (string.IsNullOrWhiteSpace(storageReason))
                    {
                        storageReason = "storage-unavailable";
                    }
                    _publisher.TryPublish(new OperatorNotificationEvent(
                        OperatorNotificationEventKind.UpgradeOrRecoveryFailed,
                        OperatorNotificationSeverity.Critical,
                        "upgrade_recovery_failed:capture-storage",
                        "Capture storage / startup recovery failed",
                        $"CameraAgent '{cameraName}' could not access durable capture storage ({storageReason}); capture is unavailable until storage recovers.",
                        now,
                        cameraName,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["source"] = "capture-storage",
                            ["reason"] = storageReason
                        }));
                }
            }
        }

        return anyFault;
    }

    private void PruneExpiredFailedControlOperations(DateTimeOffset now)
    {
        if (_notifiedFailedControlOperations.Count == 0)
        {
            return;
        }

        List<Guid>? expired = null;
        foreach (var pair in _notifiedFailedControlOperations)
        {
            if (now - pair.Value > FailedReceiptWindow)
            {
                expired ??= [];
                expired.Add(pair.Key);
            }
        }

        if (expired is not null)
        {
            foreach (var id in expired)
            {
                _notifiedFailedControlOperations.Remove(id);
            }
        }
    }

    private void RecordNotifiedFailedControlOperation(Guid operationId, DateTimeOffset eventUtc)
    {
        _notifiedFailedControlOperations[operationId] = eventUtc;
        while (_notifiedFailedControlOperations.Count > MaximumTrackedFailedControlOperations)
        {
            var oldestId = Guid.Empty;
            var oldestUtc = DateTimeOffset.MaxValue;
            foreach (var pair in _notifiedFailedControlOperations)
            {
                if (pair.Value < oldestUtc)
                {
                    oldestUtc = pair.Value;
                    oldestId = pair.Key;
                }
            }
            _notifiedFailedControlOperations.Remove(oldestId);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Durable heartbeat checkpoint I/O failures must never crash the monitor or affect capture.")]
    private async Task EvaluateDailyHeartbeatAsync(
        OperatorNotificationsOptions options,
        DateTimeOffset now,
        string cameraName,
        FleetRuntimeSnapshot? fleetSnapshot,
        CaptureAdmissionSnapshot? admissionSnapshot,
        int activeFaults,
        CancellationToken cancellationToken)
    {
        // Retry persisting any previously dispatched heartbeat checkpoint whose initial write failed transiently.
        if (heartbeatStore is not null &&
            _lastDispatchedHeartbeatSiteDate is { } pendingPersistDate &&
            (_lastPersistedHeartbeatSiteDate is null || pendingPersistDate > _lastPersistedHeartbeatSiteDate))
        {
            await TryPersistHeartbeatCheckpointAsync(pendingPersistDate, cancellationToken).ConfigureAwait(false);
        }

        if (!options.Events.DailyHeartbeatEnabled ||
            !OperatorNotificationValidation.TryParseTimeOfDay(options.Events.DailyHeartbeatTime, out var scheduledTime))
        {
            return;
        }

        var (timeZone, zoneLabel) = ResolveSiteTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var localTime = TimeOnly.FromDateTime(localNow.DateTime);

        if (!_heartbeatCheckpointLoaded && heartbeatStore is not null)
        {
            try
            {
                var persistedDate = await heartbeatStore.ReadLastHeartbeatSiteDateAsync(cancellationToken).ConfigureAwait(false);
                _heartbeatCheckpointLoaded = true;
                var maxValidDate = localDate < DateOnly.MaxValue ? localDate.AddDays(1) : DateOnly.MaxValue;
                if (persistedDate is { } loadedDate && loadedDate > maxValidDate)
                {
                    // Corrupted future-dated checkpoint (> today + 1d) must not suppress heartbeats indefinitely;
                    // treat as malformed/absent so today's heartbeat dispatches and atomically overwrites it.
                    LogHeartbeatReadFailureOnce(nameof(InvalidDataException));
                }
                else
                {
                    _heartbeatReadFailureActive = false;
                    if (persistedDate is not null)
                    {
                        if (_lastPersistedHeartbeatSiteDate is null || persistedDate > _lastPersistedHeartbeatSiteDate)
                        {
                            _lastPersistedHeartbeatSiteDate = persistedDate;
                        }
                        if (_lastDispatchedHeartbeatSiteDate is null || persistedDate > _lastDispatchedHeartbeatSiteDate)
                        {
                            _lastDispatchedHeartbeatSiteDate = persistedDate;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Verified regular checkpoint file is readable but malformed/empty/missing-date:
                // log Warning 16209 once and treat as absent so daily heartbeats recover and atomically overwrite it.
                LogHeartbeatReadFailureOnce(ex.GetType().Name);
                _heartbeatCheckpointLoaded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogHeartbeatReadFailureOnce(ex.GetType().Name);
                // Defer daily heartbeat dispatch until checkpoint readability or genuine absence is confirmed.
                return;
            }
            catch (Exception ex)
            {
                LogHeartbeatReadFailureOnce(ex.GetType().Name);
                // Defer daily heartbeat dispatch on unexpected non-format store failures.
                return;
            }
        }

        if (localTime < scheduledTime || (_lastDispatchedHeartbeatSiteDate is { } lastDate && localDate <= lastDate))
        {
            return;
        }

        var captureState = admissionSnapshot?.State.ToString() ?? "Unknown";
        var latestCaptureUtc = ResolveLatestCaptureUtc(fleetSnapshot);
        var lastCaptureText = latestCaptureUtc is { } last ? $"{FormatDuration(now - last)} ago" : "none since start";
        var diskPressureCount = storagePressureState?.Snapshots.Count(static s => s.IsUnderPressure || !string.IsNullOrWhiteSpace(s.ProbeFailure)) ?? 0;
        var uptime = now > _startedUtc ? now - _startedUtc : TimeSpan.Zero;

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["siteDate"] = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["timeZone"] = zoneLabel,
            ["captureState"] = captureState,
            ["lastCaptureAge"] = lastCaptureText,
            ["diskStatus"] = diskPressureCount == 0 ? "Normal" : $"{diskPressureCount} volume(s) under pressure",
            ["activeFaults"] = activeFaults.ToString(CultureInfo.InvariantCulture),
            ["uptime"] = FormatDuration(uptime)
        };

        var published = _publisher.TryPublish(new OperatorNotificationEvent(
            OperatorNotificationEventKind.DailyHeartbeat,
            OperatorNotificationSeverity.Info,
            $"daily_heartbeat:{localDate:yyyy-MM-dd}",
            "Daily heartbeat",
            $"Standalone CameraAgent '{cameraName}' daily heartbeat ({localDate:yyyy-MM-dd} {zoneLabel}): capture {captureState}, last capture {lastCaptureText}, disk {details["diskStatus"]}, active faults {activeFaults}, uptime {details["uptime"]}.",
            now,
            cameraName,
            details));

        if (published)
        {
            _lastDispatchedHeartbeatSiteDate = localDate;
            if (heartbeatStore is not null)
            {
                await TryPersistHeartbeatCheckpointAsync(localDate, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Durable heartbeat checkpoint I/O failures must never crash the monitor or affect capture.")]
    private async Task TryPersistHeartbeatCheckpointAsync(DateOnly siteDate, CancellationToken cancellationToken)
    {
        try
        {
            await heartbeatStore!.WriteLastHeartbeatSiteDateAsync(siteDate, cancellationToken).ConfigureAwait(false);
            _lastPersistedHeartbeatSiteDate = siteDate;
            _heartbeatWriteFailureActive = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogHeartbeatWriteFailureOnce(ex.GetType().Name);
        }
    }

    private void LogHeartbeatReadFailureOnce(string exceptionType)
    {
        if (!_heartbeatReadFailureActive)
        {
            _heartbeatReadFailureActive = true;
            OperatorNotificationLog.MonitorEvaluationFailed(_logger, exceptionType);
        }
    }

    private void LogHeartbeatWriteFailureOnce(string exceptionType)
    {
        if (!_heartbeatWriteFailureActive)
        {
            _heartbeatWriteFailureActive = true;
            OperatorNotificationLog.MonitorEvaluationFailed(_logger, exceptionType);
        }
    }

    private DateTimeOffset? ResolveLatestCaptureUtc(FleetRuntimeSnapshot? fleetSnapshot)
    {
        var fleetLast = fleetSnapshot?.Capture.LastSucceededUtc;
        var telemetryLast = captureTelemetryProvider?.Latest?.StartedUtc;
        if (fleetLast is null) return telemetryLast;
        if (telemetryLast is null) return fleetLast;
        return fleetLast >= telemetryLast ? fleetLast : telemetryLast;
    }

    private async ValueTask<TimeSpan> ComputeCaptureStallThresholdAsync(
        OperatorNotificationsOptions options,
        CancellationToken cancellationToken)
    {
        var configured = TimeSpan.FromMinutes(Math.Clamp(options.CaptureStallThresholdMinutes, 1, 1440));
        var captureInterval = scheduleCoordinator?.Snapshot?.Configuration?.Rig.Pipeline.CaptureInterval;
        if (captureInterval is null && configurationAccessor?.IsConfigured == true)
        {
            var config = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
            captureInterval = config.Rig.Pipeline.CaptureInterval;
        }
        if (captureInterval is { } interval && interval > TimeSpan.Zero)
        {
            var threeIntervals = TimeSpan.FromTicks(interval.Ticks * 3);
            if (threeIntervals > configured)
            {
                return threeIntervals;
            }
        }
        return configured;
    }

    private string ResolveSanitizedStorageAlias(string storageRoot, IReadOnlyList<CameraAgentStorageLocation>? locations)
    {
        if (locations is not null)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var normalized = NormalizeRoot(storageRoot);
            var matched = locations.FirstOrDefault(loc => string.Equals(NormalizeRoot(loc.Root), normalized, comparison));
            if (matched is not null)
            {
                return matched.Alias;
            }
        }

        if (!string.IsNullOrWhiteSpace(_hostOptions.RawIngressRoot))
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(NormalizeRoot(storageRoot), NormalizeRoot(_hostOptions.RawIngressRoot), comparison))
            {
                return "raw-ingress";
            }
        }

        return "storage-volume";
    }

    private static string NormalizeRoot(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (NotSupportedException)
        {
            return string.Empty;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Site profile read is best-effort for display name formatting only.")]
    private async Task<string> ResolveCameraNameAsync(CancellationToken cancellationToken)
    {
        if (siteProfileStore is not null)
        {
            try
            {
                var state = await siteProfileStore.GetAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(state.EffectiveCameraName))
                {
                    return OperatorNotificationRedactor.SanitizeOperationalText(state.EffectiveCameraName, 80);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fall back below.
            }
        }

        if (!string.IsNullOrWhiteSpace(_hostOptions.DisplayName))
        {
            return OperatorNotificationRedactor.SanitizeOperationalText(_hostOptions.DisplayName, 80);
        }

        return "CameraAgent";
    }

    private (TimeZoneInfo Zone, string Label) ResolveSiteTimeZone()
    {
        var zoneId = deploymentLocationStore?.Active?.TimeZoneId;
        if (string.IsNullOrWhiteSpace(zoneId))
        {
            zoneId = _hostOptions.Observatory.TimeZoneId;
        }
        if (!string.IsNullOrWhiteSpace(zoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found))
        {
            return (found, zoneId);
        }
        return (TimeZoneInfo.Utc, "UTC");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalDays}d {duration.Hours}h");
        }
        if (duration.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h {duration.Minutes}m");
        }
        if (duration.TotalMinutes >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes}m {duration.Seconds}s");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalSeconds}s");
    }
}
