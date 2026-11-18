using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Tests.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalCaptureFailureTests
{
    [TestMethod]
    public async Task ProducedCaptureSurvivesShutdownDuringDiagnosticAttemptWrite()
    {
        var fixture = await AttributionFixture.CreateAsync(0, interruptAttempt: true).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var scope = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(scope);
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        fixture.Device.Release(2, fail: false);
        await fixture.AttemptWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, scope.Id, Epoch).ConfigureAwait(false);
        await fixture.StopAsync().ConfigureAwait(false);
        fixture.Reopen();
        await fixture.Store.RecoverCaptureAcquisitionsAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, (await fixture.AssociateAsync(
            raw.Manifest.Descriptor.Capture.CaptureId, raw.Manifest.Descriptor.Capture.CaptureSequence, Epoch, scope.Id)
            .ConfigureAwait(false)).Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReceiptRetentionNeverPartiallyRemovesACapturesFailureFence(bool batchBoundary)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var scope = Guid.NewGuid();
        foreach (var source in new[] { "sqm", "temperature" })
        {
            var key = EnvironmentalCaptureAcquisition.Prefix(scope) + source;
            var claim = await fixture.Store.ClaimOnDemandAsync(fixture.Root, key, new string('A', 64), source,
                "capture-before", null, Epoch, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            fixture.Clock.Now = source == "sqm" ? Epoch : Epoch.AddSeconds(2);
            await fixture.Store.CompleteOnDemandAsync(fixture.Root, key, claim.LeaseToken,
                new(source, EnvironmentalAcquisitionTrigger.BeforeCapture,
                    source == "sqm" ? EnvironmentalAcquisitionDisposition.Failed : EnvironmentalAcquisitionDisposition.Produced,
                    "scripted", null, Epoch, fixture.Clock.Now), CancellationToken.None).ConfigureAwait(false);
        }
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, scope, Epoch).ConfigureAwait(false);
        fixture.Clock.Now = Epoch.AddSeconds(10);
        var poll = fixture.Coordinator.AcquireSourceAsync("sqm", EnvironmentalAcquisitionTrigger.Periodic, fixture.Clock.Now).AsTask();
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        fixture.Device.Release(2, fail: false);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, (await poll.ConfigureAwait(false)).Disposition);
        await fixture.StopAsync().ConfigureAwait(false);
        await fixture.Store.RetainLocalAsync(fixture.Root, Epoch.AddSeconds(batchBoundary ? 3 : 1), batchBoundary ? 1 : 100,
            CancellationToken.None).ConfigureAwait(false);
        fixture.Reopen();
        var association = await fixture.AssociateAsync(raw.Manifest.Descriptor.Capture.CaptureId,
            raw.Manifest.Descriptor.Capture.CaptureSequence, Epoch, scope).ConfigureAwait(false);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, association.Status,
            "A cutoff or batch boundary cannot erase only the failed source of an acquisition.");
        Assert.IsNull(association.SelectedRecordId);
        var count = (await CaptureMetadataCountsAsync(fixture.Root).ConfigureAwait(false)).Receipts;
        Assert.IsTrue(count is 0 or 2, "Receipt sets must expire atomically.");
    }

    [TestMethod]
    [DataRow(EnvironmentalAcquisitionDisposition.Failed)]
    [DataRow(EnvironmentalAcquisitionDisposition.TimedOut)]
    [DataRow(EnvironmentalAcquisitionDisposition.Missing)]
    public async Task LegacyCaptureFailureFenceSurvivesRetentionAndRestart(EnvironmentalAcquisitionDisposition failure)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        var service = new EnvironmentalAssociationService(fixture.Store, fixture.Store, fixture.HostOptions, fixture.Clock,
            acquisitionStates: fixture.Store);
        var pin = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, null, Epoch.AddSeconds(-50)).ConfigureAwait(false);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, (await service.AssociateAsync(
            pin.Manifest.Descriptor.Capture.CaptureId, pin.Manifest.Descriptor.Capture.CaptureSequence,
            Epoch.AddSeconds(-50), Epoch.AddSeconds(-30), null, [EnvironmentalObservationKind.SkyQuality],
            CancellationToken.None).ConfigureAwait(false)).Single().Status);
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, null, Epoch).ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        await fixture.Store.RecordAttemptAsync(fixture.Root, fixture.HostOptions.Value.EnvironmentalAcquisition.Sources.Single().ToDescriptor(),
            new("sqm", EnvironmentalAcquisitionTrigger.BeforeCapture, failure, "scripted", null, Epoch, Epoch),
            raw.Manifest.Descriptor.Capture.CaptureSequence, raw.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Now = Epoch.AddSeconds(10);
        var poll = fixture.Coordinator.AcquireSourceAsync("sqm", EnvironmentalAcquisitionTrigger.Periodic, fixture.Clock.Now).AsTask();
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        fixture.Device.Release(2, fail: false);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, (await poll.ConfigureAwait(false)).Disposition);
        await fixture.StopAsync().ConfigureAwait(false);
        fixture.Clock.Now = Epoch.AddDays(32);
        await fixture.Store.RetainLocalAsync(fixture.Root, Epoch.AddDays(1), 100, CancellationToken.None).ConfigureAwait(false);
        fixture.Reopen();
        service = new(fixture.Store, fixture.Store, fixture.HostOptions, fixture.Clock, acquisitionStates: fixture.Store);
        var association = (await service.AssociateAsync(raw.Manifest.Descriptor.Capture.CaptureId,
            raw.Manifest.Descriptor.Capture.CaptureSequence, Epoch, Epoch.AddSeconds(20), null,
            [EnvironmentalObservationKind.SkyQuality], CancellationToken.None).ConfigureAwait(false)).Single();
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, association.Status,
            "Age retention must preserve the pre-upgrade capture's durable failure fence.");
        Assert.IsNull(association.SelectedRecordId);
        await fixture.Store.RetainLocalAsync(fixture.Root, Epoch.AddDays(1), 100, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, (await CaptureMetadataCountsAsync(fixture.Root).ConfigureAwait(false)).Attempts,
            "A completed legacy association still keeps its fence while its raw capture is retained.");
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "journal", "raw-ingress.db")}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var remove = connection.CreateCommand();
            remove.CommandText = "DELETE FROM raw_captures WHERE capture_id = $capture;";
            remove.Parameters.AddWithValue("$capture", raw.Manifest.Descriptor.Capture.CaptureId.ToString("N"));
            await remove.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await fixture.Store.RetainLocalAsync(fixture.Root, Epoch.AddDays(1), 100, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, (await CaptureMetadataCountsAsync(fixture.Root).ConfigureAwait(false)).Attempts,
            "The legacy exception must end once association is complete and its raw capture is gone.");
    }

    [TestMethod]
    public async Task HourlyRetentionKeepsUpWithTwoThousandFailedCaptureReceiptsPerHour()
    {
        var root = Path.Combine(Path.GetTempPath(), "receipt-rate-" + Guid.NewGuid().ToString("N"));
        using var clock = new RetentionClock(Epoch.AddDays(32));
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            await store.InitializeAsync(root, CancellationToken.None).ConfigureAwait(false);
            using var services = new ServiceCollection().BuildServiceProvider();
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var coordinator = new EnvironmentalAcquisitionCoordinator(new(services, []),
                Mock.Of<IEnvironmentalObservationPublisher>(), Mock.Of<IDeploymentLocationStore>(), options, clock);
            using var telemetry = new EnvironmentalAcquisitionTelemetry();
            using var service = new EnvironmentalAcquisitionService(coordinator, store, store, telemetry, clock, options,
                NullLogger<EnvironmentalAcquisitionService>.Instance);
            try
            {
                for (var hour = 0; hour < 3; hour++)
                {
                    await SeedExpiredReceiptsAsync(root, clock.GetUtcNow().AddDays(-32), hour).ConfigureAwait(false);
                    if (hour == 0) await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    else clock.AdvanceHour();
                    await clock.HourlyDelay.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    Assert.AreEqual(0L, (await CaptureMetadataCountsAsync(root).ConfigureAwait(false)).Receipts,
                        $"Hour {hour + 1}: retention must drain full metadata batches even when no observations are produced.");
                }
            }
            finally { await service.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task SeedExpiredReceiptsAsync(string root, DateTimeOffset completed, int hour)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, ".environment", "environmental-observation-outbox.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var seed = connection.CreateCommand();
        seed.CommandText = """
            WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<2000)
            INSERT INTO environmental_on_demand_commands
                (idempotency_key,payload_sha256,source_id,actor_id,observed_unix_ms,status,receipt_json,updated_unix_ms)
            SELECT '__capture-before:' || printf('%032x', n + $hour * 2000) || ':sqm', $digest, 'sqm',
                'capture-before', $completed, 'completed', $receipt, $completed FROM numbers;
            """;
        seed.Parameters.AddWithValue("$hour", hour);
        seed.Parameters.AddWithValue("$digest", new string('A', 64));
        seed.Parameters.AddWithValue("$completed", completed.ToUnixTimeMilliseconds());
        seed.Parameters.AddWithValue("$receipt", Encoding.UTF8.GetBytes("{}"));
        await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed class RetentionClock(DateTimeOffset now) : TimeProvider, IDisposable
    {
        private TaskCompletionSource hourly = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RetentionTimer? timer;
        public void Dispose() => timer?.Dispose();
        public Task HourlyDelay => hourly.Task;
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != TimeSpan.FromHours(1)) return base.CreateTimer(callback, state, dueTime, period);
            timer = new(callback, state);
            hourly.TrySetResult();
            return timer;
        }
        internal void AdvanceHour()
        {
            now = now.AddHours(1);
            hourly = new(TaskCreationOptions.RunContinuationsAsynchronously);
            timer!.Fire();
        }
        private sealed class RetentionTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
