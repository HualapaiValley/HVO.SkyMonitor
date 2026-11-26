using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Deployment.Contracts;
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
        fleet.CaptureFailed("usb disconnect");
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

        // Camera fails at t=11m -> below 60s threshold, should NOT fire CameraDisconnected yet.
        publisher.Events.Clear();
        fleet.CaptureFailed("USB read timeout");
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
    public async Task UpgradeOrRecoveryFailed_DetectsControlFailureRetainedIntegrityAndDeploymentLocationAsync()
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

            // Re-evaluating does not duplicate the already-notified failed control operation ID.
            publisher.Events.Clear();
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(publisher.Events.Any(e => e.DedupeKey == $"upgrade_recovery_failed:control:{opId:N}"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyHeartbeat_FiresOncePerSiteCalendarDayAtConfiguredLocalTimeAsync()
    {
        // America/Phoenix is UTC-7 year-round. 14:30 UTC == 07:30 Phoenix (before 08:00).
        var clock = new MonitorClock(new DateTimeOffset(2026, 11, 26, 14, 30, 0, TimeSpan.Zero));
        var publisher = new RecordingPublisher();
        var options = EnabledOptions(captureStallMinutes: 120, heartbeatTime: "08:00");
        using var monitor = new OperatorNotificationMonitorService(
            new StaticConfigProvider(options),
            publisher,
            Options.Create(new CameraAgentHostOptions
            {
                DisplayName = "HVO AllSky",
                Observatory = new ObservatoryLocation(35.5, -113.9, 520, "America/Phoenix")
            }),
            clock);

        // 07:30 local -> not due yet.
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));

        // 08:05 local (15:05 UTC) -> fires once for 2026-11-26.
        clock.Advance(TimeSpan.FromMinutes(35));
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var hb1 = publisher.Events.Single(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
        Assert.AreEqual("daily_heartbeat:2026-11-26", hb1.DedupeKey);
        Assert.AreEqual(OperatorNotificationSeverity.Info, hb1.Severity);
        StringAssert.Contains(hb1.Summary, "America/Phoenix", StringComparison.Ordinal);

        // 09:00 local same day -> does NOT fire again.
        publisher.Events.Clear();
        clock.Advance(TimeSpan.FromMinutes(55));
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(publisher.Events.Any(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat));

        // Next day 08:00 local -> fires for 2026-11-27.
        clock.Advance(TimeSpan.FromHours(23));
        await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var hb2 = publisher.Events.Single(static e => e.Kind == OperatorNotificationEventKind.DailyHeartbeat);
        Assert.AreEqual("daily_heartbeat:2026-11-27", hb2.DedupeKey);
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
}
