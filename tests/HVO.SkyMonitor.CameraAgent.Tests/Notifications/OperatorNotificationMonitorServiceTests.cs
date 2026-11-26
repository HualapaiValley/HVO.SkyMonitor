using System.Collections.Concurrent;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Modules.Zwo;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Notifications;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", CameraAgentTestAreas.Operations)]
public sealed class OperatorNotificationMonitorServiceTests
{
    [TestMethod]
    public async Task DisabledNotifications_PerformsZeroEvaluationAndPublishesNothingAsync()
    {
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 16, 0, 0, TimeSpan.Zero));
        var publisher = new RecordingPublisher();
        var fleet = new FleetRuntimeState(clock);
        fleet.CaptureFailed("usb disconnect", isCameraDisconnected: true);
        var storage = new StoragePressureState();
        storage.Set(new StoragePressureSnapshot("/var/secret/camera-root", new StorageCapacity(1000, 20), true, 1, clock.GetUtcNow()));

        var config = new StaticConfigProvider(new OperatorNotificationsOptions { Enabled = false });
        using var monitor = new OperatorNotificationMonitorService(
            config,
            publisher,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = "/var/secret/camera-root" }),
            clock,
            fleetRuntimeState: fleet,
            storagePressureState: storage,
            configurationLoader: new FakeConfigLoader(recoveryRequired: true));

        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(publisher.Events);
    }

    [TestMethod]
    public async Task CaptureStalledAndCameraDisconnected_TriggerAfterThresholdsAndClearOnRecoveryAsync()
    {
        var start = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
        var clock = new MonitorClock(start);
        var publisher = new RecordingPublisher();
        var fleet = new FleetRuntimeState(clock);
        fleet.ModuleAvailable();

        var options = EnabledOptions(captureStallMinutes: 10, disconnectedSeconds: 60, heartbeatTime: "23:59");
        using var monitor = new OperatorNotificationMonitorService(
            new StaticConfigProvider(options),
            publisher,
            Options.Create(new CameraAgentHostOptions { DisplayName = "North Dome" }),
            clock,
            fleetRuntimeState: fleet);

        // Initial evaluation at t=0: not stalled yet.
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(publisher.Events);

        // Advance 11 minutes without any capture -> CaptureStalled triggers.
        clock.Advance(TimeSpan.FromMinutes(11));
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CaptureStalled));

        // Camera disconnects at t=11m -> below 60s threshold, should NOT fire CameraDisconnected yet.
        publisher.Events.Clear();
        fleet.CaptureFailed("USB read timeout", isCameraDisconnected: true);
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CameraDisconnected));
        Assert.IsFalse(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CaptureStalled),
            "Disconnected camera should report CameraDisconnected rather than duplicate CaptureStalled.");

        // Advance 65s -> CameraDisconnected triggers.
        clock.Advance(TimeSpan.FromSeconds(65));
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var disconnected = publisher.Events.Single(static e => e.Kind == OperatorNotificationEventKind.CameraDisconnected);
        Assert.AreEqual(OperatorNotificationSeverity.Critical, disconnected.Severity);
        StringAssert.Contains(disconnected.Summary, "North Dome", StringComparison.Ordinal);
        StringAssert.Contains(disconnected.Summary, "USB read timeout", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task CaptureStalled_PauseAndResumeAfterExceedingStallThreshold_ProducesZeroFalseAlarmAsync()
    {
        var start = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
        var clock = new MonitorClock(start);
        var publisher = new RecordingPublisher();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-notif-pause-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var hostOptions = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, DisplayName = "Pause Resume Cam" });
            var fleet = new FleetRuntimeState(clock);
            fleet.ModuleAvailable();
            var dummyResult = new CaptureResult(
                Frame: null,
                NextSetpoint: new CaptureSetpoint(TimeSpan.FromMilliseconds(10), 0, null, null),
                ProcessingLatency: TimeSpan.FromMilliseconds(10),
                Mode: default,
                RequiresImmediateUpload: false);
            fleet.CaptureSucceeded(dummyResult, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

            using var controlStore = new CameraAgentControlStore(hostOptions, clock);
            var options = EnabledOptions(captureStallMinutes: 15, disconnectedSeconds: 60, heartbeatTime: "23:59");
            using var monitor = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher,
                hostOptions,
                clock,
                fleetRuntimeState: fleet,
                controlStore: controlStore);

            // Evaluate while running at t=0 -> healthy.
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty(publisher.Events);

            // Hold capture admission (simulating pause/drain) for 30 minutes (> 15m stall threshold).
            var workerId = Guid.NewGuid();
            var opId = Guid.NewGuid();
            _ = await controlStore.ExchangeAsync(new CameraAgentControlExchange(workerId), CancellationToken.None).ConfigureAwait(false);
            var submitted = await controlStore.SubmitAsync(
                new CameraAgentControlRequest(opId, CameraAgentControlKind.Drain, "owner", "Maintenance", 1, clock.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
            var draining = submitted with
            {
                Phase = CameraAgentControlPhase.Draining,
                Events = [.. submitted.Events, new(CameraAgentControlPhase.Draining, clock.GetUtcNow(), "lifecycle.draining")]
            };
            _ = await controlStore.ExchangeAsync(
                new CameraAgentControlExchange(workerId, draining),
                CancellationToken.None).ConfigureAwait(false);

            clock.Advance(TimeSpan.FromMinutes(10));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty(publisher.Events);

            // Resume after 30 minutes total elapsed since last capture.
            clock.Advance(TimeSpan.FromMinutes(20));
            _ = await controlStore.ExchangeAsync(
                new CameraAgentControlExchange(workerId, draining with
                {
                    Phase = CameraAgentControlPhase.Succeeded,
                    PausedVersion = 1,
                    DrainedCaptureSequence = 1,
                    Events = [.. draining.Events, new(CameraAgentControlPhase.Succeeded, clock.GetUtcNow(), "lifecycle.succeeded")]
                }),
                CancellationToken.None).ConfigureAwait(false);

            // Immediately upon resume (t=30m) and 5m later (t=35m) -> ZERO false CaptureStalled!
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMinutes(5));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CaptureStalled),
                "Resuming after a pause longer than CaptureStallThresholdMinutes must NOT trigger a false CaptureStalled alarm.");

            // If no new capture completes for 16 minutes AFTER resume -> CaptureStalled triggers accurately.
            clock.Advance(TimeSpan.FromMinutes(11));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CaptureStalled));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GenericAcquisitionFailure_DoesNotEmitCameraDisconnectedAsync()
    {
        var start = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
        var clock = new MonitorClock(start);
        var publisher = new RecordingPublisher();
        var fleet = new FleetRuntimeState(clock);
        fleet.ModuleAvailable();

        var options = EnabledOptions(captureStallMinutes: 30, disconnectedSeconds: 10, heartbeatTime: "23:59");
        using var monitor = new OperatorNotificationMonitorService(
            new StaticConfigProvider(options),
            publisher,
            Options.Create(new CameraAgentHostOptions { DisplayName = "South Dome" }),
            clock,
            fleetRuntimeState: fleet);

        foreach (var genericReason in new[] { "null-result", "InvalidOperationException", "activation-publication-failed", "TimeoutException", "SqliteException" })
        {
            fleet.CaptureFailed(genericReason);
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(30));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Assert.IsFalse(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CameraDisconnected),
            "Generic capture/acquisition/publication failures must NOT emit CameraDisconnected.");
    }

    [TestMethod]
    public async Task FleetSnapshotTimeout_RetainsLastKnownDisconnectTimerInsteadOfResettingAsync()
    {
        var start = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
        var clock = new MonitorClock(start);
        var publisher = new RecordingPublisher();
        var disconnectedSnapshot = new FleetRuntimeSnapshot(
            new FleetCaptureSummary(FleetAvailability.Unavailable, "camera-removed", null, start, null),
            []);

        var simulateTimeout = false;
        var options = EnabledOptions(captureStallMinutes: 60, disconnectedSeconds: 60, heartbeatTime: "23:59");
        using var monitor = new OperatorNotificationMonitorService(
            new StaticConfigProvider(options),
            publisher,
            Options.Create(new CameraAgentHostOptions { DisplayName = "Snapshot Timeout Cam" }),
            clock)
        {
            FleetSnapshotReader = _ => simulateTimeout ? (false, null) : (true, disconnectedSnapshot)
        };

        // t=0: camera disconnect first observed.
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(publisher.Events);

        // t=30s: transient 50ms fleet snapshot lock timeout -> must NOT reset disconnect start timestamp!
        clock.Advance(TimeSpan.FromSeconds(30));
        simulateTimeout = true;
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(publisher.Events);

        // t=65s: snapshot succeeds again -> elapsed is 65s >= 60s threshold -> emits CameraDisconnected!
        clock.Advance(TimeSpan.FromSeconds(35));
        simulateTimeout = false;
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CameraDisconnected));
    }

    [TestMethod]
    public async Task DiskPressure_EmitsSanitizedStorageAliasAndNeverLeaksFilesystemPathAsync()
    {
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
        var publisher = new RecordingPublisher();
        var storage = new StoragePressureState();
        var rawRoot = Path.Combine(Path.GetTempPath(), "hvo-secret-raw-path-" + Guid.NewGuid().ToString("N"));
        var extraRoot = Path.Combine(Path.GetTempPath(), "hvo-secret-extra-path-" + Guid.NewGuid().ToString("N"));
        storage.Set(new StoragePressureSnapshot(rawRoot, new StorageCapacity(1000, 40), IsUnderPressure: true, EffectiveRetentionDays: 1, clock.GetUtcNow()));
        storage.Set(new StoragePressureSnapshot(extraRoot, new StorageCapacity(0, 0), IsUnderPressure: false, EffectiveRetentionDays: 7, clock.GetUtcNow(), ProbeFailure: "IOException"));

        var options = EnabledOptions(captureStallMinutes: 60, heartbeatTime: "23:59");
        using var monitor = new OperatorNotificationMonitorService(
            new StaticConfigProvider(options),
            publisher,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = rawRoot, DisplayName = "East Cam" }),
            clock,
            storagePressureState: storage);

        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);

        var diskEvents = publisher.Events.Where(static e => e.Kind == OperatorNotificationEventKind.DiskPressure).ToArray();
        Assert.HasCount(2, diskEvents);
        Assert.IsTrue(diskEvents.Any(static e => e.DedupeKey == "disk_pressure:raw-ingress"));
        Assert.IsTrue(diskEvents.Any(static e => e.DedupeKey == "disk_pressure:storage-volume"));
        foreach (var ev in diskEvents)
        {
            Assert.DoesNotContain(rawRoot, ev.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(extraRoot, ev.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(rawRoot, ev.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(extraRoot, ev.DedupeKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    [TestMethod]
    public async Task UpgradeOrRecoveryFailed_DetectsFailuresAndPrunesExpiredControlReceiptDedupEntriesAsync()
    {
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
        var publisher = new RecordingPublisher();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-notif-ctrl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var hostOptions = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, DisplayName = "West Cam" });
            using var controlStore = new CameraAgentControlStore(hostOptions, clock);
            var workerId = Guid.NewGuid();
            var opId = Guid.NewGuid();
            _ = await controlStore.ExchangeAsync(new CameraAgentControlExchange(workerId), CancellationToken.None).ConfigureAwait(false);
            var queued = await controlStore.SubmitAsync(
                new CameraAgentControlRequest(opId, CameraAgentControlKind.Restart, "owner", "Upgrade test", 1, clock.GetUtcNow().AddMinutes(-5)),
                CancellationToken.None).ConfigureAwait(false);
            var failedReceipt = queued with
            {
                Phase = CameraAgentControlPhase.Failed,
                Events =
                [
                    .. queued.Events,
                    new(CameraAgentControlPhase.Failed, clock.GetUtcNow().AddMinutes(-2), "lifecycle.verification-failed")
                ]
            };
            _ = await controlStore.ExchangeAsync(
                new CameraAgentControlExchange(workerId, failedReceipt),
                CancellationToken.None).ConfigureAwait(false);

            var options = EnabledOptions(captureStallMinutes: 60, heartbeatTime: "23:59");
            using var monitor = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher,
                hostOptions,
                clock,
                controlStore: controlStore,
                deploymentLocationStore: new FakeDeploymentLocationStore(recentReady: false, timeZoneId: "UTC"),
                configurationLoader: new FakeConfigLoader(recoveryRequired: true));

            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);

            var upgradeEvents = publisher.Events.Where(static e => e.Kind == OperatorNotificationEventKind.UpgradeOrRecoveryFailed).ToArray();
            Assert.HasCount(3, upgradeEvents);
            Assert.IsTrue(upgradeEvents.Any(e => e.DedupeKey == $"upgrade_recovery_failed:control:{opId:N}"));
            Assert.IsTrue(upgradeEvents.Any(static e => e.DedupeKey == "upgrade_recovery_failed:retained-integrity"));
            Assert.IsTrue(upgradeEvents.Any(static e => e.DedupeKey == "upgrade_recovery_failed:retained-evidence"));
            Assert.AreEqual(1, monitor.TrackedFailedControlOperationCount);

            // Re-evaluating within 24h does not duplicate the already-notified failed control operation ID.
            publisher.Events.Clear();
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(publisher.Events.Any(e => e.DedupeKey == $"upgrade_recovery_failed:control:{opId:N}"));

            // Advancing > 24h evicts the expired failed receipt ID so memory stays bounded.
            clock.Advance(TimeSpan.FromHours(25));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, monitor.TrackedFailedControlOperationCount, "Expired failed control operation IDs must be pruned.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyHeartbeat_PersistsCheckpointAcrossRestartAndDoesNotDuplicateSameSiteDayAsync()
    {
        // America/Phoenix is UTC-7 year-round. 14:30 UTC == 07:30 Phoenix (before 08:00).
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 14, 30, 0, TimeSpan.Zero));
        var stateDir = Path.Combine(Path.GetTempPath(), $"hvo-notif-hb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        try
        {
            var heartbeatStore = new FileOperatorNotificationHeartbeatStore(stateDir);
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                DisplayName = "HVO AllSky",
                Observatory = new ObservatoryLocation(35.5, -113.9, 520, "America/Phoenix")
            });
            var options = EnabledOptions(captureStallMinutes: 120, heartbeatTime: "08:00");

            var publisher1 = new RecordingPublisher();
            using (var monitor1 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options), publisher1, hostOptions, clock, heartbeatStore: heartbeatStore))
            {
                // 07:30 local -> not due yet, and NO checkpoint file written yet.
                await monitor1.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(publisher1.Events.Any(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));
                Assert.IsFalse(File.Exists(heartbeatStore.FilePath));

                // 08:05 local (15:05 UTC) -> fires once for 2026-11-26 and persists 0600 checkpoint.
                clock.Advance(TimeSpan.FromMinutes(35));
                await monitor1.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                var hb1 = publisher1.Events.Single(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
                Assert.AreEqual("daily_heartbeat:2026-11-26", hb1.DedupeKey);
                Assert.AreEqual(OperatorNotificationSeverity.Info, hb1.Severity);
                StringAssert.Contains(hb1.Summary, "America/Phoenix", StringComparison.Ordinal);
                Assert.IsTrue(File.Exists(heartbeatStore.FilePath));
                if (!OperatingSystem.IsWindows())
                {
                    Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(heartbeatStore.FilePath));
                }
            }

            // Simulate full CameraAgent restart at 08:25 local on the SAME calendar day -> MUST NOT emit duplicate heartbeat!
            clock.Advance(TimeSpan.FromMinutes(20));
            var publisher2 = new RecordingPublisher();
            using (var monitor2 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options), publisher2, hostOptions, clock, heartbeatStore: new FileOperatorNotificationHeartbeatStore(stateDir)))
            {
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(publisher2.Events.Any(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat),
                    "Restarting after the scheduled heartbeat time on the same site-local date must NOT emit a duplicate daily heartbeat.");

                // Next day 08:25 local -> fires for 2026-11-27.
                clock.Advance(TimeSpan.FromHours(24));
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                var hb2 = publisher2.Events.Single(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
                Assert.AreEqual("daily_heartbeat:2026-11-27", hb2.DedupeKey);
            }
        }
        finally
        {
            if (Directory.Exists(stateDir)) Directory.Delete(stateDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyHeartbeat_TransientCheckpointWriteFailure_RetriesPersistenceAndPreventsSameDayDuplicateAfterRestartAsync()
    {
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 15, 5, 0, TimeSpan.Zero)); // 08:05 Phoenix
        var stateDir = Path.Combine(Path.GetTempPath(), $"hvo-notif-hb-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        try
        {
            var backingStore = new FileOperatorNotificationHeartbeatStore(stateDir);
            var flakyStore = new FailOnceHeartbeatStore(backingStore);
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                DisplayName = "HVO AllSky",
                Observatory = new ObservatoryLocation(35.5, -113.9, 520, "America/Phoenix")
            });
            var options = EnabledOptions(captureStallMinutes: 120, heartbeatTime: "08:00");
            var totalHeartbeatAdmissions = 0;

            var publisher1 = new RecordingPublisher();
            using (var monitor1 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options), publisher1, hostOptions, clock, heartbeatStore: flakyStore))
            {
                // 1. Evaluation fires heartbeat, but initial checkpoint write fails transiently with IOException.
                await monitor1.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1, publisher1.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));
                totalHeartbeatAdmissions += publisher1.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
                Assert.IsFalse(File.Exists(backingStore.FilePath), "First checkpoint write failed transiently.");

                // 2. Storage recovers: next evaluation on same site day MUST persist checkpoint WITHOUT re-dispatching!
                publisher1.Events.Clear();
                clock.Advance(TimeSpan.FromMinutes(5));
                await monitor1.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(publisher1.Events.Any(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat),
                    "In-process retry of checkpoint persistence must not emit a duplicate heartbeat.");
                Assert.IsTrue(File.Exists(backingStore.FilePath),
                    "Subsequent evaluation after storage recovery must persist the pending heartbeat checkpoint.");
            }

            // 3. Restart on the same site day -> reads persisted checkpoint and emits ZERO duplicate heartbeats!
            clock.Advance(TimeSpan.FromMinutes(10));
            var publisher2 = new RecordingPublisher();
            using (var monitor2 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options), publisher2, hostOptions, clock, heartbeatStore: new FileOperatorNotificationHeartbeatStore(stateDir)))
            {
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                totalHeartbeatAdmissions += publisher2.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
            }

            Assert.AreEqual(1, totalHeartbeatAdmissions, "Total heartbeat admissions across transient write failure + recovery + restart must be 1.");
        }
        finally
        {
            if (Directory.Exists(stateDir)) Directory.Delete(stateDir, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("io")]
    [DataRow("sqlite")]
    public async Task RealPath_StartupStorageFailure_NeverEmitsCameraDisconnectedAndRoutesToUpgradeOrRecoveryFailedAsync(string failureKind)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-notif-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1,
                DisplayName = "Storage Failure Dome"
            });

            Exception failure = failureKind == "sqlite"
                ? new SqliteException("database disk image is malformed", 11)
                : new IOException("Read-only filesystem during raw-ingress initialization.");

            var ingress = new FailingInitRawIngress(failure);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(ingress, hostOptions, clock, telemetry);
            var fleet = new FleetRuntimeState(clock);
            var namedRigs = new SqliteNamedRigProfileStore(ingress, hostOptions, clock);
            using var lifetime = new TestHostApplicationLifetime();
            var module = new FaultingCameraModule(initException: null, captureException: null);

            using var captureService = new CameraCaptureService(
                new StaticConfigAccessor(CreateTestCameraConfig()),
                new SingleModuleFactory(module),
                ingress,
                new NoOpCaptureDistributor(),
                clock,
                new AstronomyEnginePlanetEphemeris(),
                telemetry,
                admission,
                fleet,
                lifetime,
                NullLogger<CameraCaptureService>.Instance,
                namedRigProfiles: namedRigs);

            await captureService.StartAsync(CancellationToken.None).ConfigureAwait(false);
            lifetime.NotifyStarted();
            await ingress.InitializeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitUntilAsync(() => fleet.Snapshot.Capture.Availability == FleetAvailability.Unavailable, TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await captureService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            // Confirm real-path preconditions: admission stays Initializing, namedRigProfiles recorded ActiveRuntimeFailure,
            // and FleetRuntimeState classified the failure as storage (not camera disconnect).
            Assert.AreEqual(CaptureAdmissionState.Initializing, admission.Snapshot.State);
            Assert.IsFalse(string.IsNullOrWhiteSpace(namedRigs.ActiveRuntimeFailure));
            Assert.IsFalse(fleet.IsCameraDisconnected);
            Assert.IsTrue(fleet.IsStorageFailure);

            var publisher = new RecordingPublisher();
            var options = EnabledOptions(captureStallMinutes: 15, disconnectedSeconds: 10, heartbeatTime: "23:59");
            using var monitor = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher,
                hostOptions,
                clock,
                fleetRuntimeState: fleet,
                admissionCoordinator: admission,
                rigProfileStore: namedRigs);

            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(publisher.Events.Any(static e =>
                e.Kind == OperatorNotificationEventKind.UpgradeOrRecoveryFailed &&
                e.DedupeKey == "upgrade_recovery_failed:capture-storage"),
                $"Pre-initialization startup {failureKind} storage failure must alert immediately on the first evaluation.");
            clock.Advance(TimeSpan.FromSeconds(30));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CameraDisconnected),
                $"Startup {failureKind} storage failure must NEVER emit CameraDisconnected.");
            var storageAlert = publisher.Events.FirstOrDefault(static e =>
                e.Kind == OperatorNotificationEventKind.UpgradeOrRecoveryFailed &&
                e.DedupeKey == "upgrade_recovery_failed:capture-storage");
            Assert.IsNotNull(storageAlert,
                $"Startup {failureKind} storage failure must alert the operator via UpgradeOrRecoveryFailed instead of going silent.");
            Assert.AreEqual(OperatorNotificationSeverity.Critical, storageAlert.Severity);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow((int)AsiErrorCode.Timeout, true, false)]
    [DataRow((int)AsiErrorCode.Timeout, false, false)]
    [DataRow((int)AsiErrorCode.GeneralError, true, false)]
    [DataRow((int)AsiErrorCode.GeneralError, false, false)]
    [DataRow((int)AsiErrorCode.CameraRemoved, true, true)]
    [DataRow((int)AsiErrorCode.CameraRemoved, false, true)]
    [DataRow((int)AsiErrorCode.CameraClosed, true, true)]
    [DataRow((int)AsiErrorCode.CameraClosed, false, true)]
    public async Task RealPath_AsiExceptions_ClassifyOnlyDisconnectCodesAsCameraDisconnectedAsync(
        int rawErrorCode,
        bool failDuringInit,
        bool expectCameraDisconnected)
    {
        var errorCode = (AsiErrorCode)rawErrorCode;
        var root = Path.Combine(Path.GetTempPath(), $"hvo-notif-asi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1,
                DisplayName = "ZWO Dome"
            });

            var asiEx = new AsiException(failDuringInit ? "ASIOpenCamera" : "ASIGetExpStatus", errorCode);
            var ingress = new WorkingRawIngress(root);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(ingress, hostOptions, clock, telemetry);
            var fleet = new FleetRuntimeState(clock);
            var namedRigs = new SqliteNamedRigProfileStore(ingress, hostOptions, clock);
            using var lifetime = new TestHostApplicationLifetime();
            var module = new FaultingCameraModule(
                initException: failDuringInit ? asiEx : null,
                captureException: failDuringInit ? null : asiEx);

            using var captureService = new CameraCaptureService(
                new StaticConfigAccessor(CreateTestCameraConfig()),
                new SingleModuleFactory(module),
                ingress,
                new NoOpCaptureDistributor(),
                clock,
                new AstronomyEnginePlanetEphemeris(),
                telemetry,
                admission,
                fleet,
                lifetime,
                NullLogger<CameraCaptureService>.Instance,
                namedRigProfiles: namedRigs);

            await captureService.StartAsync(CancellationToken.None).ConfigureAwait(false);
            lifetime.NotifyStarted();
            await module.FaultThrown.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitUntilAsync(() => fleet.Snapshot.Capture.Availability == FleetAvailability.Unavailable, TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await captureService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual("AsiException", fleet.Snapshot.Capture.Reason);
            Assert.AreEqual(expectCameraDisconnected, fleet.IsCameraDisconnected);

            var publisher = new RecordingPublisher();
            var options = EnabledOptions(captureStallMinutes: 30, disconnectedSeconds: 10, heartbeatTime: "23:59");
            using var monitor = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher,
                hostOptions,
                clock,
                fleetRuntimeState: fleet,
                admissionCoordinator: admission,
                rigProfileStore: namedRigs);

            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(30));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);

            var emittedDisconnect = publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CameraDisconnected);
            Assert.AreEqual(expectCameraDisconnected, emittedDisconnect,
                $"AsiErrorCode.{errorCode} (failDuringInit={failDuringInit}) expected CameraDisconnected={expectCameraDisconnected}.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RealPath_CameraNotConnectedInvalidOperation_EmitsCameraDisconnectedAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-notif-notconn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1,
                DisplayName = "ZWO Unplugged Dome"
            });

            var ingress = new WorkingRawIngress(root);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(ingress, hostOptions, clock, telemetry);
            var fleet = new FleetRuntimeState(clock);
            var namedRigs = new SqliteNamedRigProfileStore(ingress, hostOptions, clock);
            using var lifetime = new TestHostApplicationLifetime();
            var module = new FaultingCameraModule(
                initException: new InvalidOperationException("The configured ASI camera is not connected."),
                captureException: null);

            using var captureService = new CameraCaptureService(
                new StaticConfigAccessor(CreateTestCameraConfig()),
                new SingleModuleFactory(module),
                ingress,
                new NoOpCaptureDistributor(),
                clock,
                new AstronomyEnginePlanetEphemeris(),
                telemetry,
                admission,
                fleet,
                lifetime,
                NullLogger<CameraCaptureService>.Instance,
                namedRigProfiles: namedRigs);

            await captureService.StartAsync(CancellationToken.None).ConfigureAwait(false);
            lifetime.NotifyStarted();
            await module.FaultThrown.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitUntilAsync(() => fleet.Snapshot.Capture.Availability == FleetAvailability.Unavailable, TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await captureService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.IsTrue(fleet.IsCameraDisconnected);

            var publisher = new RecordingPublisher();
            var options = EnabledOptions(captureStallMinutes: 30, disconnectedSeconds: 10, heartbeatTime: "23:59");
            using var monitor = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher,
                hostOptions,
                clock,
                fleetRuntimeState: fleet,
                admissionCoordinator: admission,
                rigProfileStore: namedRigs);

            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(20));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CameraDisconnected));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [DataRow((int)AsiErrorCode.Timeout, "init", false)]
    [DataRow((int)AsiErrorCode.GeneralError, "init", false)]
    [DataRow((int)AsiErrorCode.CameraRemoved, "init", true)]
    [DataRow((int)AsiErrorCode.CameraClosed, "init", true)]
    [DataRow((int)AsiErrorCode.Timeout, "open2", false)]
    [DataRow((int)AsiErrorCode.GeneralError, "open2", false)]
    [DataRow((int)AsiErrorCode.CameraRemoved, "open2", true)]
    [DataRow((int)AsiErrorCode.CameraClosed, "open2", true)]
    [DataRow((int)AsiErrorCode.Timeout, "property", false)]
    [DataRow((int)AsiErrorCode.GeneralError, "property", false)]
    [DataRow((int)AsiErrorCode.CameraRemoved, "property", true)]
    [DataRow((int)AsiErrorCode.CameraClosed, "property", true)]
    public async Task RealPath_ZwoAsiModuleNativeInitializationErrors_ClassifyOnlyDisconnectCodesAsCameraDisconnectedAsync(
        int rawErrorCode,
        string stage,
        bool expectCameraDisconnected)
    {
        var errorCode = (AsiErrorCode)rawErrorCode;
        var root = Path.Combine(Path.GetTempPath(), $"hvo-notif-zwo-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1,
                DisplayName = "ZWO Native Boundary Dome"
            });

            var native = new ZwoAsiCameraModuleTests.FakeAsiNativeApi();
            if (stage == "init")
            {
                native.InitError = errorCode;
            }
            else if (stage == "property")
            {
                native.FailPropertyByIndex[0] = errorCode;
            }
            else
            {
                native.OpenErrorCode = errorCode;
                native.FailOpenOnCallByCameraId[7] = 2;
            }

            var zwoModule = ZwoAsiCameraModuleTests.Module(native, clock);
            var ingress = new WorkingRawIngress(root);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(ingress, hostOptions, clock, telemetry);
            var fleet = new FleetRuntimeState(clock);
            var namedRigs = new SqliteNamedRigProfileStore(ingress, hostOptions, clock);
            using var lifetime = new TestHostApplicationLifetime();

            using var captureService = new CameraCaptureService(
                new StaticConfigAccessor(ZwoAsiCameraModuleTests.CreateCenteredTransportTestConfig()),
                new SingleModuleFactory(zwoModule),
                ingress,
                new NoOpCaptureDistributor(),
                clock,
                new AstronomyEnginePlanetEphemeris(),
                telemetry,
                admission,
                fleet,
                lifetime,
                NullLogger<CameraCaptureService>.Instance,
                namedRigProfiles: namedRigs);

            await captureService.StartAsync(CancellationToken.None).ConfigureAwait(false);
            lifetime.NotifyStarted();
            await WaitUntilAsync(() => fleet.Snapshot.Capture.Availability == FleetAvailability.Unavailable, TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await captureService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual(expectCameraDisconnected, fleet.IsCameraDisconnected,
                $"Real ZwoAsiCameraModule native error {errorCode} at {stage} expected FleetRuntimeState.IsCameraDisconnected={expectCameraDisconnected}.");

            var publisher = new RecordingPublisher();
            var options = EnabledOptions(captureStallMinutes: 30, disconnectedSeconds: 10, heartbeatTime: "23:59");
            using var monitor = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher,
                hostOptions,
                clock,
                fleetRuntimeState: fleet,
                admissionCoordinator: admission,
                rigProfileStore: namedRigs);

            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(30));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);

            var emittedDisconnect = publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.CameraDisconnected);
            Assert.AreEqual(expectCameraDisconnected, emittedDisconnect,
                $"Real ZwoAsiCameraModule native error {errorCode} at {stage} expected CameraDisconnected={expectCameraDisconnected}.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task DailyHeartbeat_UnreadableCheckpointAfterRestart_DefersWithoutDuplicateAndResumesWhenRestoredAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        RequireModesEnforced();
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 15, 5, 0, TimeSpan.Zero)); // 08:05 Phoenix
        var stateDir = Path.Combine(Path.GetTempPath(), $"hvo-notif-hb-unreadable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        var heartbeatFile = Path.Combine(stateDir, FileOperatorNotificationHeartbeatStore.HeartbeatFileName);
        try
        {
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                DisplayName = "HVO AllSky",
                Observatory = new ObservatoryLocation(35.5, -113.9, 520, "America/Phoenix")
            });
            var options = EnabledOptions(captureStallMinutes: 120, heartbeatTime: "08:00");
            var cumulativeHeartbeats = 0;

            // 1. Emit and persist today's daily heartbeat.
            var publisher1 = new RecordingPublisher();
            using (var monitor1 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher1,
                hostOptions,
                clock,
                heartbeatStore: new FileOperatorNotificationHeartbeatStore(stateDir)))
            {
                await monitor1.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                cumulativeHeartbeats += publisher1.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
                Assert.AreEqual(1, cumulativeHeartbeats);
                Assert.IsTrue(File.Exists(heartbeatFile));
            }

            // 2. Make the checkpoint unreadable (0000) and restart the monitor on the same site day.
            File.SetUnixFileMode(heartbeatFile, UnixFileMode.None);
            clock.Advance(TimeSpan.FromMinutes(10));
            var publisher2 = new RecordingPublisher();
            var logger2 = new RecordingMonitorLogger();
            using (var monitor2 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher2,
                hostOptions,
                clock,
                logger: logger2,
                heartbeatStore: new FileOperatorNotificationHeartbeatStore(stateDir)))
            {
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromMinutes(5));
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);

                cumulativeHeartbeats += publisher2.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
                Assert.AreEqual(1, cumulativeHeartbeats,
                    "Unreadable heartbeat checkpoint after restart MUST defer dispatch and NEVER publish a duplicate daily heartbeat.");
                Assert.AreEqual(1, logger2.Warning16209Count,
                    "Repeated checkpoint read failures in the same episode must deduplicate Warning 16209.");

                // 3. Restore readability: same site day loads persisted date (still 1); next site day resumes normally (2).
                File.SetUnixFileMode(heartbeatFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                clock.Advance(TimeSpan.FromMinutes(5));
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                cumulativeHeartbeats += publisher2.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
                Assert.AreEqual(1, cumulativeHeartbeats,
                    "After restoring readability on the same site day, persisted checkpoint must still prevent duplicates.");

                clock.Advance(TimeSpan.FromHours(24));
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                var nextDayHeartbeats = publisher2.Events.Where(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat).ToArray();
                Assert.HasCount(1, nextDayHeartbeats);
                Assert.AreEqual("daily_heartbeat:2026-11-27", nextDayHeartbeats[0].DedupeKey);
                cumulativeHeartbeats += nextDayHeartbeats.Length;
                Assert.AreEqual(2, cumulativeHeartbeats);
            }
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && File.Exists(heartbeatFile))
            {
                File.SetUnixFileMode(heartbeatFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            if (Directory.Exists(stateDir))
            {
                Directory.Delete(stateDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DailyHeartbeat_RepeatedCheckpointWriteFailures_DeduplicatesWarning16209UntilRecoveryAsync()
    {
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 15, 5, 0, TimeSpan.Zero)); // 08:05 Phoenix
        var stateDir = Path.Combine(Path.GetTempPath(), $"hvo-notif-hb-logdedup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        try
        {
            var backingStore = new FileOperatorNotificationHeartbeatStore(stateDir);
            var toggleStore = new ControllableFailHeartbeatStore(backingStore) { FailWrites = true };
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                DisplayName = "HVO AllSky",
                Observatory = new ObservatoryLocation(35.5, -113.9, 520, "America/Phoenix")
            });
            var options = EnabledOptions(captureStallMinutes: 120, heartbeatTime: "08:00");
            var publisher = new RecordingPublisher();
            var logger = new RecordingMonitorLogger();

            using var monitor = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher,
                hostOptions,
                clock,
                logger: logger,
                heartbeatStore: toggleStore);

            // Fail persistence across 3 consecutive evaluations -> Warning 16209 logged ONCE.
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMinutes(1));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMinutes(1));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, publisher.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));
            Assert.AreEqual(3, toggleStore.WriteAttempts);
            Assert.AreEqual(1, logger.Warning16209Count,
                "Repeated heartbeat checkpoint write failures must log Warning 16209 once per failure episode.");

            // Recover persistence -> next evaluation succeeds and resets failure log dedup.
            toggleStore.FailWrites = false;
            clock.Advance(TimeSpan.FromMinutes(1));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(backingStore.FilePath));
            Assert.AreEqual(1, logger.Warning16209Count);

            // Next site day: new write failure episode logs Warning 16209 once again.
            toggleStore.FailWrites = true;
            clock.Advance(TimeSpan.FromHours(24));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMinutes(1));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, logger.Warning16209Count,
                "A new failure episode after a successful write must log Warning 16209 once again.");
        }
        finally
        {
            if (Directory.Exists(stateDir))
            {
                Directory.Delete(stateDir, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow("malformed-date", "{\"lastSiteDate\":\"not-a-date\"}")]
    [DataRow("empty-file", "")]
    [DataRow("truncated-json", "{\"lastSiteDate\":\"2026-11")]
    [DataRow("json-null", "null")]
    [DataRow("empty-object", "{}")]
    public async Task DailyHeartbeat_ReadableMalformedCheckpoint_RecoversAtomicallyAndResumesAcrossDaysAsync(
        string scenario,
        string corruptContent)
    {
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 15, 5, 0, TimeSpan.Zero)); // 08:05 Phoenix
        var stateDir = Path.Combine(Path.GetTempPath(), $"hvo-notif-hb-corrupt-{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        var heartbeatFile = Path.Combine(stateDir, FileOperatorNotificationHeartbeatStore.HeartbeatFileName);
        try
        {
            await File.WriteAllTextAsync(heartbeatFile, corruptContent).ConfigureAwait(false);
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                DisplayName = "HVO AllSky",
                Observatory = new ObservatoryLocation(35.5, -113.9, 520, "America/Phoenix")
            });
            var options = EnabledOptions(captureStallMinutes: 120, heartbeatTime: "08:00");

            // 1. Day 1: logs Warning 16209 once, recovers immediately, dispatches Day 1, and atomically overwrites checkpoint.
            var publisher1 = new RecordingPublisher();
            var logger1 = new RecordingMonitorLogger();
            using (var monitor1 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher1,
                hostOptions,
                clock,
                logger: logger1,
                heartbeatStore: new FileOperatorNotificationHeartbeatStore(stateDir)))
            {
                await monitor1.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromMinutes(5));
                await monitor1.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);

                var day1Events = publisher1.Events.Where(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat).ToArray();
                Assert.HasCount(1, day1Events, $"Scenario '{scenario}' must recover immediately on Day 1.");
                Assert.AreEqual("daily_heartbeat:2026-11-26", day1Events[0].DedupeKey);
                Assert.AreEqual(1, logger1.Warning16209Count,
                    $"Scenario '{scenario}' must log Warning 16209 once when recovering a malformed checkpoint.");

                var repairedDate = await new FileOperatorNotificationHeartbeatStore(stateDir)
                    .ReadLastHeartbeatSiteDateAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(new DateOnly(2026, 11, 26), repairedDate,
                    $"Scenario '{scenario}' must atomically repair the checkpoint file with today's valid site date.");
                if (!OperatingSystem.IsWindows())
                {
                    Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(heartbeatFile));
                }
            }

            // 2. Same-day restart reads repaired checkpoint and sends ZERO duplicate and ZERO warnings.
            clock.Advance(TimeSpan.FromMinutes(15));
            var publisher2 = new RecordingPublisher();
            var logger2 = new RecordingMonitorLogger();
            using (var monitor2 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher2,
                hostOptions,
                clock,
                logger: logger2,
                heartbeatStore: new FileOperatorNotificationHeartbeatStore(stateDir)))
            {
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(publisher2.Events.Any(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat),
                    $"Scenario '{scenario}': same-day restart after atomic repair must NOT send a duplicate heartbeat.");
                Assert.AreEqual(0, logger2.Warning16209Count);

                // 3. Subsequent days (Day 2, Day 3, Day 4) each dispatch exactly 1 heartbeat normally.
                foreach (var expectedDate in new[] { "2026-11-27", "2026-11-28", "2026-11-29" })
                {
                    publisher2.Events.Clear();
                    clock.Advance(TimeSpan.FromHours(24));
                    await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                    var nextDay = publisher2.Events.Where(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat).ToArray();
                    Assert.HasCount(1, nextDay, $"Scenario '{scenario}' failed to dispatch daily heartbeat for {expectedDate}.");
                    Assert.AreEqual($"daily_heartbeat:{expectedDate}", nextDay[0].DedupeKey);
                }
            }
        }
        finally
        {
            if (Directory.Exists(stateDir))
            {
                Directory.Delete(stateDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DailyHeartbeat_FutureDatedCheckpoint_RecoversAsMalformedWhilePreservingOneDaySkewToleranceAsync()
    {
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 15, 5, 0, TimeSpan.Zero)); // 08:05 Phoenix (2026-11-26)
        var stateDir = Path.Combine(Path.GetTempPath(), $"hvo-notif-hb-future-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        try
        {
            var store = new FileOperatorNotificationHeartbeatStore(stateDir);
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                DisplayName = "HVO AllSky",
                Observatory = new ObservatoryLocation(35.5, -113.9, 520, "America/Phoenix")
            });
            var options = EnabledOptions(captureStallMinutes: 120, heartbeatTime: "08:00");

            // Case 1: Checkpoint dated 30 days in the future (> today.AddDays(1)) is treated as malformed and repaired.
            await store.WriteLastHeartbeatSiteDateAsync(new DateOnly(2026, 11, 26).AddDays(30), CancellationToken.None).ConfigureAwait(false);
            var publisher1 = new RecordingPublisher();
            var logger1 = new RecordingMonitorLogger();
            using (var monitor1 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher1,
                hostOptions,
                clock,
                logger: logger1,
                heartbeatStore: new FileOperatorNotificationHeartbeatStore(stateDir)))
            {
                await monitor1.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                var day1 = publisher1.Events.Where(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat).ToArray();
                Assert.HasCount(1, day1, "Future-dated checkpoint (> today + 1d) must recover and dispatch today's heartbeat.");
                Assert.AreEqual("daily_heartbeat:2026-11-26", day1[0].DedupeKey);
                Assert.AreEqual(1, logger1.Warning16209Count);
                Assert.AreEqual(new DateOnly(2026, 11, 26),
                    await store.ReadLastHeartbeatSiteDateAsync(CancellationToken.None).ConfigureAwait(false));
            }

            // Same-day restart reads repaired date (no duplicate); next day resumes cleanly.
            clock.Advance(TimeSpan.FromMinutes(15));
            var publisher2 = new RecordingPublisher();
            using (var monitor2 = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher2,
                hostOptions,
                clock,
                heartbeatStore: new FileOperatorNotificationHeartbeatStore(stateDir)))
            {
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(publisher2.Events.Any(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));

                clock.Advance(TimeSpan.FromHours(24));
                await monitor2.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                var day2 = publisher2.Events.Single(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
                Assert.AreEqual("daily_heartbeat:2026-11-27", day2.DedupeKey);
            }

            // Case 2: Checkpoint dated today.AddDays(1) (within 1-day timezone skew tolerance) is accepted without warning.
            var skewClock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 15, 5, 0, TimeSpan.Zero));
            await store.WriteLastHeartbeatSiteDateAsync(new DateOnly(2026, 11, 27), CancellationToken.None).ConfigureAwait(false);
            var skewPublisher = new RecordingPublisher();
            var skewLogger = new RecordingMonitorLogger();
            using var skewMonitor = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                skewPublisher,
                hostOptions,
                skewClock,
                logger: skewLogger,
                heartbeatStore: new FileOperatorNotificationHeartbeatStore(stateDir));
            await skewMonitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(skewPublisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));
            Assert.AreEqual(0, skewLogger.Warning16209Count);
        }
        finally
        {
            if (Directory.Exists(stateDir))
            {
                Directory.Delete(stateDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DailyHeartbeat_AlternatingReadAndWriteExceptionTypes_DeduplicatesWarning16209UntilRecoveryAsync()
    {
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 15, 5, 0, TimeSpan.Zero)); // 08:05 Phoenix
        var stateDir = Path.Combine(Path.GetTempPath(), $"hvo-notif-hb-alt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        try
        {
            var backingStore = new FileOperatorNotificationHeartbeatStore(stateDir);
            var alternatingStore = new QueuedExceptionHeartbeatStore(backingStore);
            alternatingStore.ReadFailures.Enqueue(new IOException("Transient I/O error on read 1."));
            alternatingStore.ReadFailures.Enqueue(new UnauthorizedAccessException("Transient permission error on read 2."));
            alternatingStore.ReadFailures.Enqueue(new IOException("Transient I/O error on read 3."));

            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                DisplayName = "HVO AllSky",
                Observatory = new ObservatoryLocation(35.5, -113.9, 520, "America/Phoenix")
            });
            var options = EnabledOptions(captureStallMinutes: 120, heartbeatTime: "08:00");
            var publisher = new RecordingPublisher();
            var logger = new RecordingMonitorLogger();

            using var monitor = new OperatorNotificationMonitorService(
                new StaticConfigProvider(options),
                publisher,
                hostOptions,
                clock,
                logger: logger,
                heartbeatStore: alternatingStore);

            // 1. Alternating read exceptions during a single outage must emit ONLY 1 Warning 16209.
            for (var i = 0; i < 3; i++)
            {
                await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromMinutes(1));
            }
            Assert.AreEqual(0, publisher.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));
            Assert.AreEqual(1, logger.Warning16209Count,
                "Alternating read exception types during one outage must emit only 1 Warning 16209.");

            // 2. Read recovers, heartbeat dispatches, then alternating write exceptions emit ONLY 1 Warning 16209.
            alternatingStore.WriteFailures.Enqueue(new IOException("Transient I/O error on write 1."));
            alternatingStore.WriteFailures.Enqueue(new UnauthorizedAccessException("Transient permission error on write 2."));
            alternatingStore.WriteFailures.Enqueue(new IOException("Transient I/O error on write 3."));

            for (var i = 0; i < 3; i++)
            {
                await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromMinutes(1));
            }
            Assert.AreEqual(1, publisher.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));
            Assert.AreEqual(2, logger.Warning16209Count,
                "Alternating write exception types during one outage must emit only 1 Warning 16209.");

            // 3. Write succeeds -> clears active write failure episode without logging another warning.
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(backingStore.FilePath));
            Assert.AreEqual(2, logger.Warning16209Count);

            // 4. Next day new outage with alternating write exception types emits 1 new Warning 16209.
            alternatingStore.WriteFailures.Enqueue(new UnauthorizedAccessException("Next-day permission error."));
            alternatingStore.WriteFailures.Enqueue(new IOException("Next-day I/O error."));
            clock.Advance(TimeSpan.FromHours(24));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMinutes(1));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, publisher.Events.Count(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));
            Assert.AreEqual(3, logger.Warning16209Count,
                "A new write outage after recovery must log Warning 16209 once even across alternating exception types.");
        }
        finally
        {
            if (Directory.Exists(stateDir))
            {
                Directory.Delete(stateDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RuntimeCaptureStorageFailure_RequiresSustainedEvaluationsWhileStartupFailureAlertsImmediatelyAsync()
    {
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
        var hostOptions = Options.Create(new CameraAgentHostOptions { DisplayName = "Storage Threshold Cam" });
        var options = EnabledOptions(captureStallMinutes: 120, disconnectedSeconds: 60, heartbeatTime: "23:59");
        var dummyResult = new CaptureResult(
            Frame: null,
            NextSetpoint: new CaptureSetpoint(TimeSpan.FromMilliseconds(10), 0, null, null),
            ProcessingLatency: TimeSpan.FromMilliseconds(10),
            Mode: default,
            RequiresImmediateUpload: false);

        // 1. Pre-initialization startup storage failure alerts IMMEDIATELY on evaluation 1.
        var startupFleet = new FleetRuntimeState(clock);
        startupFleet.CaptureFailed("IOException", isCameraDisconnected: false, isStorageFailure: true, isStartupStorageFailure: true);
        var startupPublisher = new RecordingPublisher();
        using (var startupMonitor = new OperatorNotificationMonitorService(
            new StaticConfigProvider(options), startupPublisher, hostOptions, clock, fleetRuntimeState: startupFleet))
        {
            await startupMonitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(startupPublisher.Events.Any(static e =>
                e.Kind == OperatorNotificationEventKind.UpgradeOrRecoveryFailed &&
                e.DedupeKey == "upgrade_recovery_failed:capture-storage"),
                "Pre-initialization startup storage failure must alert immediately on evaluation 1.");
        }

        // 2. Runtime single transient capture-storage failure (clears before 2nd evaluation) produces ZERO alert.
        var runtimeFleet = new FleetRuntimeState(clock);
        runtimeFleet.ModuleAvailable();
        var runtimePublisher = new RecordingPublisher();
        using var runtimeMonitor = new OperatorNotificationMonitorService(
            new StaticConfigProvider(options), runtimePublisher, hostOptions, clock, fleetRuntimeState: runtimeFleet);

        runtimeFleet.CaptureFailed("IOException", isCameraDisconnected: false, isStorageFailure: true, isStartupStorageFailure: false);
        await runtimeMonitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(runtimePublisher.Events.Any(static e => e.DedupeKey == "upgrade_recovery_failed:capture-storage"),
            "A single transient runtime capture-storage error must NOT immediately raise a Critical alert.");

        // Capture recovers before the next evaluation -> counter resets, still zero alerts.
        runtimeFleet.CaptureSucceeded(dummyResult, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(5));
        clock.Advance(TimeSpan.FromSeconds(30));
        await runtimeMonitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(runtimePublisher.Events);

        // 3. Sustained runtime capture-storage failure across 2 consecutive evaluations raises Critical alert on eval 2.
        runtimeFleet.CaptureFailed("SqliteException", isCameraDisconnected: false, isStorageFailure: true, isStartupStorageFailure: false);
        clock.Advance(TimeSpan.FromSeconds(30));
        await runtimeMonitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(runtimePublisher.Events, "First evaluation of new runtime storage failure must still wait for confirmation.");

        clock.Advance(TimeSpan.FromSeconds(30));
        await runtimeMonitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var sustainedAlert = runtimePublisher.Events.Single(static e => e.DedupeKey == "upgrade_recovery_failed:capture-storage");
        Assert.AreEqual(OperatorNotificationEventKind.UpgradeOrRecoveryFailed, sustainedAlert.Kind);
        Assert.AreEqual(OperatorNotificationSeverity.Critical, sustainedAlert.Severity);
    }

    private static void RequireModesEnforced()
    {
        if (Environment.UserName == "root")
        {
            Assert.Inconclusive("Needs a non-root user, for whom modes 0000 and 0200 are enforced.");
        }
    }

    private static OperatorNotificationsOptions EnabledOptions(
        int captureStallMinutes = 15,
        int disconnectedSeconds = 60,
        string heartbeatTime = "08:00") => new()
        {
            Enabled = true,
            CaptureStallThresholdMinutes = captureStallMinutes,
            CameraDisconnectedThresholdSeconds = disconnectedSeconds,
            Smtp = new SmtpNotificationOptions { Enabled = true, Host = "smtp.example.test", FromAddress = "a@example.test", Recipients = ["b@example.test"] },
            Events = new OperatorNotificationEventsOptions { DailyHeartbeatTime = heartbeatTime }
        };

    private sealed class MonitorClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private sealed class RecordingPublisher : IOperatorNotificationPublisher
    {
        public List<OperatorNotificationEvent> Events { get; } = [];
        public bool TryPublish(OperatorNotificationEvent notification)
        {
            Events.Add(notification);
            return true;
        }
    }

    private sealed class StaticConfigProvider(OperatorNotificationsOptions options) : IOperatorNotificationConfigProvider
    {
        public OperatorNotificationsOptions CurrentOptions { get; } = options;
        public ValueTask<OperatorNotificationsConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OperatorNotificationsConfigurationSnapshot(CurrentOptions, OperatorNotificationSecretPresence.Empty, "v1", false, false));
        public ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult((CurrentOptions, OperatorNotificationSecrets.Empty));
        public ValueTask<OperatorNotificationsSaveResult> SaveAsync(OperatorNotificationsUpdateRequest request, string actor, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OperatorNotificationsSaveResult(OperatorNotificationsSaveOutcome.Unavailable, null));
    }

    private sealed class FakeConfigLoader(bool recoveryRequired) : ICameraAgentConfigurationLoader
    {
        public bool RetainedIntegrityRecoveryRequired => recoveryRequired;
        public Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeDeploymentLocationStore(bool recentReady, string timeZoneId) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active { get; } = DeploymentLocationSnapshot.Create(
            "loc-1", 1, "test", null, DateTimeOffset.UtcNow, null, 35, -113, 500, timeZoneId);
        public bool RecentRetainedEvidenceReady => recentReady;
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Active!);
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null) => Active!;
    }

    private static CameraModuleConfig CreateTestCameraConfig() =>
        new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1), 0, 0),
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }),
            CapturePipelineConfig.Empty);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Condition not met within {timeout.TotalSeconds:0.0}s.");
            }
            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    private sealed class QueuedExceptionHeartbeatStore(IOperatorNotificationHeartbeatStore inner) : IOperatorNotificationHeartbeatStore
    {
        public Queue<Exception> ReadFailures { get; } = new();
        public Queue<Exception> WriteFailures { get; } = new();

        public ValueTask<DateOnly?> ReadLastHeartbeatSiteDateAsync(CancellationToken cancellationToken)
        {
            if (ReadFailures.Count > 0)
            {
                throw ReadFailures.Dequeue();
            }
            return inner.ReadLastHeartbeatSiteDateAsync(cancellationToken);
        }

        public ValueTask WriteLastHeartbeatSiteDateAsync(DateOnly siteDate, CancellationToken cancellationToken)
        {
            if (WriteFailures.Count > 0)
            {
                throw WriteFailures.Dequeue();
            }
            return inner.WriteLastHeartbeatSiteDateAsync(siteDate, cancellationToken);
        }
    }

    private sealed class ControllableFailHeartbeatStore(IOperatorNotificationHeartbeatStore inner) : IOperatorNotificationHeartbeatStore
    {
        private int _writeAttempts;
        public bool FailWrites { get; set; }
        public int WriteAttempts => Volatile.Read(ref _writeAttempts);

        public ValueTask<DateOnly?> ReadLastHeartbeatSiteDateAsync(CancellationToken cancellationToken)
            => inner.ReadLastHeartbeatSiteDateAsync(cancellationToken);

        public ValueTask WriteLastHeartbeatSiteDateAsync(DateOnly siteDate, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _writeAttempts);
            if (FailWrites)
            {
                throw new IOException("Simulated persistent storage outage while writing heartbeat checkpoint.");
            }
            return inner.WriteLastHeartbeatSiteDateAsync(siteDate, cancellationToken);
        }
    }

    private sealed class RecordingMonitorLogger : ILogger<OperatorNotificationMonitorService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, EventId EventId, string Message)> _entries = new();
        public int Warning16209Count => _entries.Count(static e => e.Level == LogLevel.Warning && e.EventId.Id == 16209);
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, eventId, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private sealed class FailOnceHeartbeatStore(IOperatorNotificationHeartbeatStore inner) : IOperatorNotificationHeartbeatStore
    {
        private int _writeAttempts;

        public ValueTask<DateOnly?> ReadLastHeartbeatSiteDateAsync(CancellationToken cancellationToken)
            => inner.ReadLastHeartbeatSiteDateAsync(cancellationToken);

        public ValueTask WriteLastHeartbeatSiteDateAsync(DateOnly siteDate, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writeAttempts) == 1)
            {
                throw new IOException("Simulated transient storage outage while writing heartbeat checkpoint.");
            }
            return inner.WriteLastHeartbeatSiteDateAsync(siteDate, cancellationToken);
        }
    }

    private sealed class StaticConfigAccessor(CameraModuleConfig config) : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => true;
        public void SetConfiguration(CameraModuleConfig value) => throw new NotSupportedException();
        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(config);
    }

    private sealed class SingleModuleFactory(ICameraModule module) : ICameraModuleFactory
    {
        public ICameraModule Create(string moduleType) => module;
    }

    private sealed class FailingInitRawIngress(Exception failure) : IRawCaptureIngress
    {
        public TaskCompletionSource InitializeAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeAttempted.TrySetResult();
            return ValueTask.FromException(failure);
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class WorkingRawIngress(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class FaultingCameraModule(Exception? initException, Exception? captureException) : ICameraModule
    {
        public TaskCompletionSource FaultThrown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "faulting-test";
        public string DisplayName => "Faulting Test Camera";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
        {
            if (initException is not null)
            {
                FaultThrown.TrySetResult();
                return Task.FromException(initException);
            }
            return Task.CompletedTask;
        }

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            if (captureException is not null)
            {
                FaultThrown.TrySetResult();
                return Task.FromException<CaptureResult>(captureException);
            }
            return Task.FromCanceled<CaptureResult>(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpCaptureDistributor : ICaptureDistributor
    {
        public void NotifyCommittedCapture() { }
        public ValueTask ProcessEphemeralAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
        public void NotifyStarted() => _started.Cancel();
        public void Dispose() => _started.Dispose();
    }
}
