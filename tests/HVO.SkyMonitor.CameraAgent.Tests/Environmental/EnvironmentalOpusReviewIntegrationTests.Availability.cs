using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalOpusReviewIntegrationTests
{
    [TestMethod]
    public async Task CoalescedTriggerCannotReviveFailedEvidenceInTheUiOrCaptureAssociation()
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var source = new EnvironmentalSourceConfiguration
            {
                Id = "station",
                Type = "LocalWeatherStation",
                Kind = EnvironmentalObservationKind.AirTemperature,
                Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
                StaleAfterSeconds = 300,
                ValidForSeconds = 900,
                Options = System.Text.Json.JsonSerializer.SerializeToElement(new EnvironmentalDeviceOptions("station"), JsonOptions)
            };
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new() { Enabled = true, Sources = [source] }
            });
            var device = new RecoveringBarrierDevice();
            using var provider = new ServiceCollection().AddSingleton<IEnvironmentalDevice>(device).BuildServiceProvider();
            using var coordinator = new EnvironmentalAcquisitionCoordinator(new(provider, EnvironmentalAdapterPipelineTests.Registrations()),
                new Publisher(store, root), new Locations(), options, clock, store);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced,
                (await coordinator.AcquireSourceAsync(source.Id, EnvironmentalAcquisitionTrigger.Periodic, Epoch).ConfigureAwait(false)).Disposition);
            clock.Now = Epoch.AddSeconds(1);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Missing,
                (await coordinator.AcquireSourceAsync(source.Id, EnvironmentalAcquisitionTrigger.Periodic, clock.Now).ConfigureAwait(false)).Disposition);
            clock.Now = Epoch.AddSeconds(2);
            var recovering = coordinator.AcquireSourceAsync(source.Id, EnvironmentalAcquisitionTrigger.Periodic, clock.Now).AsTask();
            await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try
            {
                clock.Now = Epoch.AddSeconds(3);
                Assert.AreEqual(EnvironmentalAcquisitionDisposition.Coalesced,
                    (await coordinator.AcquireSourceAsync(source.Id, EnvironmentalAcquisitionTrigger.Periodic, clock.Now).ConfigureAwait(false)).Disposition);
                var state = (await store.ReadSourceStatesAsync(root, CancellationToken.None).ConfigureAwait(false)).Single();
                Assert.AreEqual(EnvironmentalAcquisitionDisposition.Missing, state.LastDisposition);
                Assert.AreEqual(1, state.ConsecutiveFailures);
                Assert.AreEqual(Epoch.AddSeconds(1), state.LastCompletedUtc);
                var (principal, authorization) = CameraAgentEnvironmentalUiServiceTests.ReadAuthorized();
                var ui = CameraAgentEnvironmentalUiServiceTests.CreateService(principal, authorization, source,
                    stateStore: store, observationStore: store, centralMode: CentralIntegrationMode.Disabled, root: root, timeProvider: clock);
                Assert.IsEmpty((await ui.GetLatestReadingsAsync(CancellationToken.None).ConfigureAwait(false)).Value!);
                Assert.AreEqual("Unavailable", (await ui.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).Value!.Sources.Single().Freshness);
                var associations = new EnvironmentalAssociationService(store, store, options, clock, acquisitionStates: store);
                Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing,
                    (await associations.AssociateAsync(Guid.NewGuid(), 1, Epoch.AddSeconds(3), Epoch.AddSeconds(4), null,
                        [source.Kind], CancellationToken.None).ConfigureAwait(false)).Single().Status);
                Assert.HasCount(3, await store.ReadAttemptsAsync(root, 10, CancellationToken.None).ConfigureAwait(false));
            }
            finally { device.Release.TrySetResult(); await recovering.ConfigureAwait(false); }
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced,
                (await store.ReadSourceStatesAsync(root, CancellationToken.None).ConfigureAwait(false)).Single().LastDisposition);
        }
        finally { Delete(root); }
    }

    private sealed class RecoveringBarrierDevice : IEnvironmentalDevice
    {
        private int calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 3) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            return new(Encoding.UTF8.GetBytes(EnvironmentalHardwareAdapterTests.Station), receivedAtUtc, false,
                call == 2 ? EnvironmentalDeviceStatus.Faulted : EnvironmentalDeviceStatus.Ready, ReceivedAtUtc: receivedAtUtc);
        }
    }
}
