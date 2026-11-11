using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Tests.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalCaptureFailureTests
{
    [TestMethod]
    [DataRow(250, false, false)]
    [DataRow(250, false, true)]
    [DataRow(250, true, false)]
    [DataRow(250, true, true)]
    [DataRow(0, false, false)]
    [DataRow(0, false, true)]
    public async Task EachCaptureKeepsItsOwnBusyOrQueuedOutcome(int budget, bool busyReadFails, bool restart)
    {
        // Use independent histories: an early immutable association must not hide a late-selection defect.
        foreach (var associateAfterPoll in new[] { false, true })
        {
            var fixture = await AttributionFixture.CreateAsync(budget).ConfigureAwait(false);
            await using var cleanup = fixture.ConfigureAwait(false);
            fixture.Clock.Now = Epoch;
            var a = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(a);
            if (budget == 0)
            {
                await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                fixture.Clock.Now = Epoch.AddMilliseconds(500);
                fixture.Device.Release(2, fail: true);
            }
            await a.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var rawA = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, a.Id, Epoch).ConfigureAwait(false);
            var descriptorA = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(rawA.Manifest)).Document!.Manifest!.Descriptor;

            fixture.Clock.Now = Epoch.AddSeconds(30);
            Task<EnvironmentalAcquisitionReceipt>? periodic = null;
            if (budget != 0)
            {
                periodic = fixture.Coordinator.AcquireSourceAsync("sqm", EnvironmentalAcquisitionTrigger.Periodic, fixture.Clock.Now).AsTask();
                await fixture.Device.Started(3).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            var b = await fixture.Bridge.BeforeCaptureAsync(fixture.Clock.Now, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(b);
            await fixture.Device.Started(3).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            fixture.Clock.Now = Epoch.AddMilliseconds(budget == 0 ? 30_500 : 31_000);
            fixture.Device.Release(3, busyReadFails);
            if (periodic is not null) await periodic.ConfigureAwait(false);
            await b.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var rawB = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, b.Id, Epoch.AddSeconds(30)).ConfigureAwait(false);
            var descriptorB = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(rawB.Manifest)).Document!.Manifest!.Descriptor;
            Assert.AreEqual(b.Id, descriptorB.CycleEvidence!.AcquisitionAttemptId);
            var expected = busyReadFails ? LocalEnvironmentalAssociationStatus.Missing : LocalEnvironmentalAssociationStatus.Fresh;
            LocalEnvironmentalCaptureAssociation? first = null;
            if (!associateAfterPoll)
            {
                first = await fixture.AssociateAsync(descriptorB.Capture.CaptureId, descriptorB.Capture.CaptureSequence,
                    Epoch.AddSeconds(30), b.Id).ConfigureAwait(false);
                Assert.AreEqual(expected, first.Status);
            }

            fixture.Clock.Now = Epoch.AddSeconds(40);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, (await fixture.Coordinator.AcquireSourceAsync("sqm",
                EnvironmentalAcquisitionTrigger.Periodic, fixture.Clock.Now).ConfigureAwait(false)).Disposition);
            // Recent receipts survive retention in both the bound and raw-publication / binding restart window.
            await fixture.Store.RetainLocalAsync(fixture.Root, fixture.Clock.Now.AddDays(-31), 100, CancellationToken.None).ConfigureAwait(false);
            await fixture.StopAsync().ConfigureAwait(false);
            if (restart) fixture.Reopen();
            var later = await fixture.AssociateAsync(descriptorB.Capture.CaptureId, descriptorB.Capture.CaptureSequence,
                Epoch.AddSeconds(30), descriptorB.CycleEvidence.AcquisitionAttemptId!.Value).ConfigureAwait(false);
            Assert.AreEqual(expected, later.Status);
            if (first is not null) Assert.AreEqual(first.AssociationIdentitySha256, later.AssociationIdentitySha256);
            // A has not been associated yet. B's successful poll cannot change A's failure.
            var lateA = await fixture.AssociateAsync(descriptorA.Capture.CaptureId, descriptorA.Capture.CaptureSequence,
                Epoch, descriptorA.CycleEvidence!.AcquisitionAttemptId!.Value).ConfigureAwait(false);
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, lateA.Status);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingCaptureRetriesWithoutStoringMissingAndRestartRetainsItsOwnScope(bool interrupted)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var acquisition = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(acquisition);
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var captureId = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<EnvironmentalCaptureAcquisitionPendingException>(async () =>
            await fixture.AssociateAsync(captureId, 1, Epoch, acquisition.Id).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.HasCount(0, await fixture.Store.ReadAssociationsAsync(fixture.Root, captureId, CancellationToken.None).ConfigureAwait(false));
        if (interrupted)
        {
            var queued = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(queued);
            await fixture.StopAsync().ConfigureAwait(false);
            Assert.IsTrue(queued.Completion.Task.IsCompleted, "Shutdown must release queued capture waiters.");
            fixture.Reopen();
            await fixture.Store.RecoverCaptureAcquisitionsAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing,
                (await fixture.AssociateAsync(Guid.NewGuid(), 2, Epoch, queued.Id).ConfigureAwait(false)).Status);
        }
        else
        {
            fixture.Clock.Now = Epoch.AddMilliseconds(500);
            fixture.Device.Release(2, fail: false);
            await acquisition.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        var ready = await fixture.AssociateAsync(captureId, 1, Epoch, acquisition.Id).ConfigureAwait(false);
        Assert.AreEqual(interrupted ? LocalEnvironmentalAssociationStatus.Missing : LocalEnvironmentalAssociationStatus.Fresh, ready.Status);
        await Assert.ThrowsExactlyAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await fixture.Store.ReadCaptureAvailabilityAsync(fixture.Root, Guid.NewGuid(), acquisition.Id, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task BusyDependencyIsChosenAtAdmissionEvenWhenTheCaptureQueueDrainsLater()
    {
        var fixture = await AttributionFixture.CreateAsync(250).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var a = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        await a!.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        fixture.Clock.Now = Epoch.AddSeconds(30);
        var periodic = fixture.Coordinator.AcquireSourceAsync("sqm", EnvironmentalAcquisitionTrigger.Periodic, fixture.Clock.Now).AsTask();
        await fixture.Device.Started(3).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        // The first queued capture occupies the request worker until the periodic read settles.
        var first = await fixture.Bridge.BeforeCaptureAsync(fixture.Clock.Now, CancellationToken.None).ConfigureAwait(false);
        var second = await fixture.Bridge.BeforeCaptureAsync(fixture.Clock.Now, CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Now = Epoch.AddSeconds(31);
        fixture.Device.Release(3, fail: true);
        await periodic.ConfigureAwait(false);
        await first!.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await second!.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(3, fixture.Device.Calls, "A queued capture must retain its admitted dependency rather than start another read.");
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing,
            (await fixture.AssociateAsync(Guid.NewGuid(), 1, Epoch.AddSeconds(30), second.Id).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task CaptureQueueRefusalIsDurableWithoutMakingTheHealthySourceUnavailable()
    {
        var fixture = await AttributionFixture.CreateAsync(0, 1).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        _ = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsTrue(fixture.Enqueue());
        var refused = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(refused);
        Assert.IsTrue(refused.Completion.Task.IsCompleted);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing,
            (await fixture.AssociateAsync(Guid.NewGuid(), 1, Epoch, refused.Id).ConfigureAwait(false)).Status);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced,
            (await fixture.Store.ReadSourceStatesAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false)).Single().LastDisposition);
    }

    private sealed class AttributionDevice(Clock clock, bool queueFirst) : IEnvironmentalDevice
    {
        private sealed class Gate
        {
            internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly ConcurrentDictionary<int, Gate> gates = new();
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal Task Started(int call) => gates.GetOrAdd(call, _ => new()).Started.Task;
        internal void Release(int call, bool fail) => gates.GetOrAdd(call, _ => new()).Released.TrySetResult(fail);
        public async ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 2 && !queueFirst) throw new InvalidOperationException("capture A failed");
            if (call == 3 || call == 2 && queueFirst)
            {
                var gate = gates.GetOrAdd(call, _ => new());
                gate.Started.TrySetResult();
                if (await gate.Released.Task.WaitAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("scripted read failure");
            }
            return new(Encoding.ASCII.GetBytes(EnvironmentalHardwareAdapterTests.Sqm), clock.Now, false, ReceivedAtUtc: clock.Now);
        }
    }

    private sealed class AttributionFixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "capture-scope-" + Guid.NewGuid().ToString("N"));
        internal Clock Clock { get; } = new() { Now = Epoch.AddSeconds(-50) };
        internal SqliteEnvironmentalObservationOutbox Store { get; private set; } = null!;
        internal AttributionDevice Device { get; private set; } = null!;
        internal EnvironmentalAcquisitionCoordinator Coordinator { get; private set; } = null!;
        internal EnvironmentalCaptureTriggerBridge Bridge { get; private set; } = null!;
        private EnvironmentalAcquisitionService service = null!;
        private ServiceProvider provider = null!;
        private EnvironmentalAcquisitionTelemetry telemetry = null!;
        private IOptions<CameraAgentHostOptions> options = null!;
        internal IOptions<CameraAgentHostOptions> HostOptions => options;
        private bool stopped;
        private int completionAttempts;
        internal int CompletionAttempts => Volatile.Read(ref completionAttempts);

        internal static async Task<AttributionFixture> CreateAsync(int budget, int queueCapacity = 64, int completionFailures = 0)
        {
            var fixture = new AttributionFixture();
            fixture.Store = new(fixture.Clock);
            fixture.Device = new(fixture.Clock, budget == 0);
            var source = new EnvironmentalSourceConfiguration
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
            fixture.options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = fixture.Root,
                EnvironmentalAcquisition = new()
                {
                    Enabled = true,
                    BeforeCaptureWaitMilliseconds = budget,
                    QueueCapacity = queueCapacity,
                    SourceTimeoutMilliseconds = 10000,
                    Sources = [source]
                }
            });
            fixture.provider = new ServiceCollection().AddSingleton<IEnvironmentalDevice>(fixture.Device).BuildServiceProvider();
            IEnvironmentalAcquisitionStateStore acquisitionState = fixture.Store;
            if (completionFailures > 0)
            {
                var state = new Mock<IEnvironmentalAcquisitionStateStore>();
                state.Setup(value => value.RecordAttemptAsync(It.IsAny<string>(), It.IsAny<EnvironmentalSourceDescriptor>(),
                    It.IsAny<EnvironmentalAcquisitionReceipt>(), It.IsAny<long?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                    .Returns((string root, EnvironmentalSourceDescriptor descriptor, EnvironmentalAcquisitionReceipt receipt,
                        long? sequence, Guid? capture, CancellationToken token) =>
                        fixture.Store.RecordAttemptAsync(root, descriptor, receipt, sequence, capture, token));
                state.As<IEnvironmentalOnDemandCommandStore>().Setup(value => value.CompleteOnDemandAsync(It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<EnvironmentalAcquisitionReceipt>(), It.IsAny<CancellationToken>()))
                    .Returns((string root, string key, string lease, EnvironmentalAcquisitionReceipt receipt, CancellationToken token) =>
                        Interlocked.Increment(ref fixture.completionAttempts) <= completionFailures
                            ? ValueTask.FromException(new IOException("scripted transient completion failure"))
                            : fixture.Store.CompleteOnDemandAsync(root, key, lease, receipt, token));
                acquisitionState = state.Object;
            }
            fixture.Coordinator = new(new(fixture.provider, EnvironmentalAdapterPipelineTests.Registrations()),
                new Publisher(fixture.Store, fixture.Root), new LocationStore(), fixture.options, fixture.Clock, acquisitionState);
            await fixture.Coordinator.AcquireSourceAsync("sqm", EnvironmentalAcquisitionTrigger.Periodic, fixture.Clock.Now).ConfigureAwait(false);
            await fixture.Store.UpdateSourceScheduleAsync(fixture.Root, source.ToDescriptor(), Epoch.AddDays(1), CancellationToken.None).ConfigureAwait(false);
            fixture.telemetry = new();
            fixture.service = new(fixture.Coordinator, fixture.Store, fixture.Store, fixture.telemetry, fixture.Clock,
                fixture.options, NullLogger<EnvironmentalAcquisitionService>.Instance);
            fixture.Bridge = new(fixture.Coordinator, fixture.service, fixture.Store, fixture.options);
            await fixture.service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            return fixture;
        }

        internal async Task<LocalEnvironmentalCaptureAssociation> AssociateAsync(Guid captureId, long sequence, DateTimeOffset start, Guid scope)
            => (await new EnvironmentalAssociationService(Store, Store, options, Clock, acquisitionStates: Store)
                .AssociateAsync(captureId, sequence, start, start.AddSeconds(20), null, [EnvironmentalObservationKind.SkyQuality],
                    CancellationToken.None, scope).ConfigureAwait(false)).Single();

        internal async Task StopAsync()
        {
            if (!stopped) { stopped = true; await service.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        internal bool Enqueue() => service.TryEnqueue(new(EnvironmentalAcquisitionTrigger.Periodic, Clock.Now));
        internal void Reopen() { Store.Dispose(); Store = new(Clock); }
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            service.Dispose(); Coordinator.Dispose(); provider.Dispose(); telemetry.Dispose(); Store.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
