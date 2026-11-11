using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed partial class EnvironmentalCaptureFailureTests
{
    private static readonly DateTimeOffset Epoch = EnvironmentalHardwareAdapterTests.Epoch;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(EnvironmentalAcquisitionDisposition.TimedOut, false, false)]
    [DataRow(EnvironmentalAcquisitionDisposition.TimedOut, true, false)]
    [DataRow(EnvironmentalAcquisitionDisposition.Failed, false, false)]
    [DataRow(EnvironmentalAcquisitionDisposition.Failed, true, false)]
    [DataRow(EnvironmentalAcquisitionDisposition.Missing, false, false)]
    [DataRow(EnvironmentalAcquisitionDisposition.Missing, true, false)]
    [DataRow(EnvironmentalAcquisitionDisposition.TimedOut, false, true)]
    [DataRow(EnvironmentalAcquisitionDisposition.TimedOut, true, true)]
    [DataRow(EnvironmentalAcquisitionDisposition.Failed, false, true)]
    [DataRow(EnvironmentalAcquisitionDisposition.Failed, true, true)]
    [DataRow(EnvironmentalAcquisitionDisposition.Missing, false, true)]
    [DataRow(EnvironmentalAcquisitionDisposition.Missing, true, true)]
    public async Task LaterPollCannotReviveCaptureBeforeFailure(
        EnvironmentalAcquisitionDisposition failure, bool restart, bool identifiedCapture)
    {
        var captureId = Guid.NewGuid();
        var results = new List<LocalEnvironmentalCaptureAssociation>();
        // Separate histories prevent a retained early association from masking a defective late selection.
        foreach (var associateAfterPoll in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-fence-" + Guid.NewGuid().ToString("N"));
            try
            {
                var clock = new Clock { Now = Epoch.AddSeconds(-50) };
                using var initialStore = new SqliteEnvironmentalObservationOutbox(clock);
                var configuration = new EnvironmentalSourceConfiguration
                {
                    Id = "sqm",
                    Type = "SkyQualityMeter",
                    Kind = EnvironmentalObservationKind.SkyQuality,
                    Required = false,
                    ScheduleEpochUtc = Epoch.AddDays(1),
                    Triggers = [EnvironmentalAcquisitionTrigger.BeforeCapture, EnvironmentalAcquisitionTrigger.Periodic],
                    ValidForSeconds = 900,
                    StaleAfterSeconds = 300,
                    Options = JsonSerializer.SerializeToElement(new EnvironmentalDeviceOptions("sqm"), SerializerOptions)
                };
                var options = Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    EnvironmentalAcquisition = new()
                    { Enabled = true, BeforeCaptureWaitMilliseconds = 30, SourceTimeoutMilliseconds = 1000, Sources = [configuration] }
                });
                using var provider = new ServiceCollection().AddSingleton<IEnvironmentalDevice>(new Device(failure)).BuildServiceProvider();
                var factory = new EnvironmentalSourceFactory(provider, EnvironmentalAdapterPipelineTests.Registrations());
                Guid? acquisitionId = null;
                using (var coordinator = new EnvironmentalAcquisitionCoordinator(factory, new Publisher(initialStore, root), new LocationStore(), options, clock, initialStore))
                {
                    Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, (await coordinator.AcquireSourceAsync("sqm",
                        EnvironmentalAcquisitionTrigger.Periodic, clock.Now).ConfigureAwait(false)).Disposition);
                    clock.Now = Epoch;
                    if (identifiedCapture)
                    {
                        var receipts = await coordinator.AcquireTriggerWithBudgetAsync(EnvironmentalAcquisitionTrigger.BeforeCapture,
                            clock.Now, 30, 1, captureId).ConfigureAwait(false);
                        Assert.AreEqual(failure, receipts.Single().Disposition);
                    }
                    else
                    {
                        using var telemetry = new EnvironmentalAcquisitionTelemetry();
                        using var acquisition = new EnvironmentalAcquisitionService(coordinator, initialStore, initialStore, telemetry,
                            clock, options, NullLogger<EnvironmentalAcquisitionService>.Instance);
                        var bridge = new EnvironmentalCaptureTriggerBridge(coordinator, acquisition, initialStore, options);
                        await initialStore.UpdateSourceScheduleAsync(root, configuration.ToDescriptor(), Epoch.AddDays(1), CancellationToken.None).ConfigureAwait(false);
                        await acquisition.StartAsync(CancellationToken.None).ConfigureAwait(false);
                        var scope = await bridge.BeforeCaptureAsync(clock.Now, CancellationToken.None).ConfigureAwait(false);
                        Assert.IsNotNull(scope);
                        await scope.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        acquisitionId = scope.Id;
                        await acquisition.StopAsync(CancellationToken.None).ConfigureAwait(false);
                        Assert.AreEqual(failure, (await initialStore.ReadAttemptsAsync(root, 10, CancellationToken.None).ConfigureAwait(false))[0].Disposition);
                    }
                }
                if (restart) initialStore.Dispose();
                using var reopened = restart ? new SqliteEnvironmentalObservationOutbox(clock) : null;
                var store = reopened ?? initialStore;
                var association = new EnvironmentalAssociationService(store, store, options, clock, acquisitionStates: store);
                LocalEnvironmentalCaptureAssociation? early = null;
                if (!associateAfterPoll) early = (await association.AssociateAsync(captureId, 1, Epoch, Epoch.AddSeconds(20),
                    null, [configuration.Kind], CancellationToken.None, acquisitionId).ConfigureAwait(false)).Single();

                clock.Now = Epoch.AddSeconds(10);
                // A new source instance models a recovered device after restart.
                using var healthyProvider = new ServiceCollection().AddSingleton<IEnvironmentalDevice>(new Device(null)).BuildServiceProvider();
                using var healthy = new EnvironmentalAcquisitionCoordinator(new EnvironmentalSourceFactory(healthyProvider,
                    EnvironmentalAdapterPipelineTests.Registrations()), new Publisher(store, root), new LocationStore(), options, clock, store);
                Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, (await healthy.AcquireSourceAsync("sqm",
                    EnvironmentalAcquisitionTrigger.Periodic, clock.Now).ConfigureAwait(false)).Disposition);
                if (restart && associateAfterPoll)
                {
                    await store.RetainLocalAsync(root, Epoch.AddDays(1), 100, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(2L, (await store.GetLocalSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
                }
                var late = (await association.AssociateAsync(captureId, 1, Epoch, Epoch.AddSeconds(20), null,
                    [configuration.Kind], CancellationToken.None, acquisitionId).ConfigureAwait(false)).Single();
                Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, late.Status);
                Assert.IsNull(late.SelectedRecordId);
                if (early is not null) Assert.AreEqual(early.AssociationIdentitySha256, late.AssociationIdentitySha256);
                results.Add(late);
                if (identifiedCapture)
                {
                    var independent = (await association.AssociateAsync(Guid.NewGuid(), 3, Epoch, Epoch.AddSeconds(20), null,
                        [configuration.Kind], CancellationToken.None).ConfigureAwait(false)).Single();
                    Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, independent.Status);
                }

                // The fence belongs to the failed capture, rather than permanently disabling the source.
                clock.Now = Epoch.AddSeconds(30);
                var nextCapture = Guid.NewGuid();
                Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, (await healthy.AcquireSourceAsync("sqm",
                    EnvironmentalAcquisitionTrigger.BeforeCapture, clock.Now, 2, nextCapture).ConfigureAwait(false)).Disposition);
                var recovered = (await association.AssociateAsync(nextCapture, 2, clock.Now, clock.Now.AddSeconds(20), null,
                    [configuration.Kind], CancellationToken.None).ConfigureAwait(false)).Single();
                Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, recovered.Status);
                Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, (await association.ReadCompletedAsync(captureId, 1,
                    Epoch, Epoch.AddSeconds(20), null, [configuration.Kind], CancellationToken.None).ConfigureAwait(false))!.Single().Status);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Assert.AreEqual(results[0].AssociationIdentitySha256, results[1].AssociationIdentitySha256);
    }

    [TestMethod]
    public async Task UnattributedAttemptRetentionDoesNotPinObservationHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-fence-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock { Now = Epoch.AddSeconds(-50) };
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var source = EnvironmentalHardwareAdapterTests.Source("SkyQualityMeter");
            var first = (await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with
            { ObservedAtUtc = clock.Now }, CancellationToken.None).ConfigureAwait(false)).Fact!;
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            await store.CommitLocalAsync(root, first, target, CancellationToken.None).ConfigureAwait(false);
            foreach (var seconds in new[] { 0, 500 })
            {
                var at = Epoch.AddSeconds(seconds);
                await store.RecordAttemptAsync(root, source.Descriptor, new(source.Descriptor.Id,
                    EnvironmentalAcquisitionTrigger.BeforeCapture, EnvironmentalAcquisitionDisposition.Failed,
                    "source-failure", null, at, at), null, null, CancellationToken.None).ConfigureAwait(false);
            }
            clock.Now = Epoch.AddSeconds(1000);
            var next = (await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with
            { ObservedAtUtc = clock.Now }, CancellationToken.None).ConfigureAwait(false)).Fact!;
            await store.CommitLocalAsync(root, next, target, CancellationToken.None).ConfigureAwait(false);
            await store.RecordAttemptAsync(root, source.Descriptor, new(source.Descriptor.Id,
                EnvironmentalAcquisitionTrigger.BeforeCapture, EnvironmentalAcquisitionDisposition.Produced, "produced",
                next.ObservationId, clock.Now, clock.Now, next.ObservedAtUtc, next.StaleAfterUtc), null, null, CancellationToken.None).ConfigureAwait(false);
            await store.RetainLocalAsync(root, Epoch.AddDays(1), 100, CancellationToken.None).ConfigureAwait(false);
            var retained = await store.ReadAttemptsAsync(root, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(0, retained);
            Assert.AreEqual(2L, (await store.GetLocalSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
            for (var i = 0; i < 2; i++)
            {
                var lease = await store.ClaimAsync(root, "retention", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                await store.AcknowledgeAsync(root, lease, new(EnvironmentalObservationAcknowledgement.CurrentSchemaVersion, lease.Record.Observation.ObservationId,
                    lease.Record.SourceIdentitySha256, lease.Record.ContentSha256, clock.Now, EnvironmentalObservationDeliveryDisposition.Accepted),
                    CancellationToken.None).ConfigureAwait(false);
            }
            clock.Now = Epoch.AddSeconds(2000);
            await store.RetainLocalAsync(root, Epoch.AddDays(1), 100, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0L, (await store.GetLocalSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
            await store.RetainLocalAsync(root, Epoch.AddDays(1), 100, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(0, await store.ReadAttemptsAsync(root, 10, CancellationToken.None).ConfigureAwait(false));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Device(EnvironmentalAcquisitionDisposition? failure) : IEnvironmentalDevice
    {
        private int calls;
        public async ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
        {
            if (++calls == 2 && failure.HasValue)
            {
                if (failure == EnvironmentalAcquisitionDisposition.TimedOut)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                if (failure == EnvironmentalAcquisitionDisposition.Failed) throw new InvalidOperationException("scripted failure");
                return new(ReadOnlyMemory<byte>.Empty, receivedAtUtc, false, EnvironmentalDeviceStatus.Missing, ReceivedAtUtc: receivedAtUtc);
            }
            return new(Encoding.ASCII.GetBytes(EnvironmentalHardwareAdapterTests.Sqm), receivedAtUtc, false, ReceivedAtUtc: receivedAtUtc);
        }
    }

    private sealed class Publisher(SqliteEnvironmentalObservationOutbox store, string root) : IEnvironmentalObservationPublisher
    {
        public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(EnvironmentalObservationFactV1 fact, CancellationToken cancellationToken = default)
        {
            await store.CommitLocalAsync(root, fact, cancellationToken).ConfigureAwait(false);
            return new(EnvironmentalObservationPublishDisposition.Enqueued, null);
        }
    }

    private sealed class LocationStore : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => EnvironmentalHardwareAdapterTests.Context().DeploymentLocation;
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken) => ValueTask.FromResult(Active!);
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null) => Active!;
    }
}
