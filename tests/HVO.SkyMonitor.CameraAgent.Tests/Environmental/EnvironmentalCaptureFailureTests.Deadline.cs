using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Tests.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalCaptureFailureTests
{
    [TestMethod]
    public async Task EnvironmentalWriterCannotHoldCaptureAdmissionPastTwoSeconds()
    {
        var fixture = await AttributionFixture.CreateAsync(250).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        using var writer = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, ".environment", "environmental-observation-outbox.db")};Pooling=False");
        await writer.OpenAsync().ConfigureAwait(false);
        using var transaction = await writer.BeginTransactionAsync().ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var request = Task.Run(async () => await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false));
        try
        {
            var acquisition = await request.WaitAsync(TimeSpan.FromMilliseconds(2400)).ConfigureAwait(false);
            Assert.IsNotNull(acquisition);
            Assert.IsTrue(acquisition.Unavailable);
            Assert.IsLessThan(2400d, stopwatch.Elapsed.TotalMilliseconds);
            Assert.IsGreaterThanOrEqualTo(1800d, stopwatch.Elapsed.TotalMilliseconds);
            var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, acquisition.Id, Epoch,
                unavailable: acquisition.Unavailable).ConfigureAwait(false);
            Assert.IsTrue(CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(raw.Manifest))
                .Document!.Manifest!.Descriptor.CycleEvidence!.EnvironmentalUnavailable);
            var available = await fixture.Store.ReadCaptureAvailabilityAsync(fixture.Root,
                raw.Manifest.Descriptor.Capture.CaptureId, acquisition.Id, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(available.Pending, "The raw admission marker is terminal even while the environmental writer is held.");
            Assert.IsTrue(available.EvidenceMissing);
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            await request.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task BusySettlementCannotReviveTheCaptureAfterAtomicUnavailableAdmissionAndRestart()
    {
        var fixture = await AttributionFixture.CreateAsync(250, blockCompletion: true).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var stopwatch = Stopwatch.StartNew();
        var request = fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).AsTask();
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        fixture.Device.Release(2, fail: false);
        await fixture.CompletionWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        using var writer = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, ".environment", "environmental-observation-outbox.db")};Pooling=False");
        await writer.OpenAsync().ConfigureAwait(false);
        using var transaction = await writer.BeginTransactionAsync().ConfigureAwait(false);
        fixture.AllowCompletionWrite.TrySetResult();
        EnvironmentalCaptureAcquisition? acquisition = null;
        HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawCaptureReceipt? raw = null;
        try
        {
            acquisition = await request.WaitAsync(TimeSpan.FromMilliseconds(2400)).ConfigureAwait(false);
            Assert.IsNotNull(acquisition);
            Assert.IsTrue(acquisition.Unavailable);
            Assert.IsLessThan(2400d, stopwatch.Elapsed.TotalMilliseconds);
            raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, acquisition.Id, Epoch,
                unavailable: true).ConfigureAwait(false);
            Assert.IsTrue((await fixture.Store.ReadCaptureAvailabilityAsync(fixture.Root,
                raw.Manifest.Descriptor.Capture.CaptureId, acquisition.Id, CancellationToken.None).ConfigureAwait(false)).EvidenceMissing);
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            await request.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        await acquisition!.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await fixture.StopAsync().ConfigureAwait(false);
        fixture.Reopen();
        await fixture.Store.RecoverCaptureAcquisitionsAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
        var capture = raw!.Manifest.Descriptor.Capture;
        var unavailable = await fixture.Store.ReadCaptureAvailabilityAsync(fixture.Root, capture.CaptureId, acquisition.Id,
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(unavailable.Pending);
        Assert.IsTrue(unavailable.EvidenceMissing);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, (await fixture.AssociateAsync(capture.CaptureId,
            capture.CaptureSequence, Epoch, acquisition.Id).ConfigureAwait(false)).Status);
        var independent = new EnvironmentalAssociationService(fixture.Store, fixture.Store, fixture.HostOptions, fixture.Clock,
            acquisitionStates: fixture.Store);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, (await independent.AssociateAsync(Guid.NewGuid(), 2,
            Epoch, Epoch.AddSeconds(20), null, [HVO.SkyMonitor.Processing.EnvironmentalObservationKind.SkyQuality],
            CancellationToken.None).ConfigureAwait(false)).Single().Status,
            "A capture-specific marker must not become a source-wide stuck fence.");
    }

    [TestMethod]
    public async Task BusyCapturePreparationStillPropagatesHostCancellation()
    {
        var fixture = await AttributionFixture.CreateAsync(5000).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        using var writer = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, ".environment", "environmental-observation-outbox.db")};Pooling=False");
        await writer.OpenAsync().ConfigureAwait(false);
        using var transaction = await writer.BeginTransactionAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var request = fixture.Bridge.BeforeCaptureAsync(Epoch, cancellation.Token).AsTask();
        await Task.Delay(100).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await request.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsLessThan(500d, stopwatch.Elapsed.TotalMilliseconds);
        }
        finally { await transaction.RollbackAsync().ConfigureAwait(false); }
        await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Device.Calls, "Host cancellation cannot queue a late sensor acquisition.");
    }

    [TestMethod]
    public async Task CapturePreparationQueueIsBoundedAndShutdownDrainsItsOwnedRequests()
    {
        var fixture = await AttributionFixture.CreateAsync(0, queueCapacity: 1).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new EnvironmentalCaptureAcquisition(Guid.NewGuid(), []);
        var second = new EnvironmentalCaptureAcquisition(Guid.NewGuid(), []);
        Assert.IsTrue(fixture.Prepare(first, async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsTrue(fixture.Prepare(second, _ => Task.CompletedTask));
        var refused = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(refused);
        Assert.IsTrue(refused.Unavailable);
        Assert.IsTrue(refused.Completion.Task.IsCompleted);
        await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsInstanceOfType<OperationCanceledException>(await first.Admission.Task.ConfigureAwait(false));
        Assert.IsInstanceOfType<OperationCanceledException>(await second.Admission.Task.ConfigureAwait(false));
        Assert.IsTrue(first.Completion.Task.IsCompleted);
        Assert.IsTrue(second.Completion.Task.IsCompleted);
        Assert.AreEqual(1, fixture.Device.Calls);
        var rejected = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(rejected!.Unavailable);
    }

    [TestMethod]
    public async Task CameraRunnerCarriesTheDeadlineMarkerIntoCaptureAdmissionEvidence()
    {
        var fixture = await AttributionFixture.CreateAsync(250).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        using var writer = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, ".environment", "environmental-observation-outbox.db")};Pooling=False");
        await writer.OpenAsync().ConfigureAwait(false);
        using var transaction = await writer.BeginTransactionAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var context = new DeadlineHostContext(cancellation);
        var runner = new CameraModuleRunner(new DeadlineModule(), context, TimeProvider.System,
            NullLogger.Instance, environmentalTriggers: fixture.Bridge);
        try
        {
            await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromMilliseconds(2800)).ConfigureAwait(false);
            Assert.IsNotNull(context.Submission?.CycleEvidence);
            Assert.IsTrue(context.Submission.CycleEvidence.EnvironmentalUnavailable);
            Assert.IsNotNull(context.Submission.CycleEvidence.AcquisitionAttemptId);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
    }

    private sealed class DeadlineHostContext(CancellationTokenSource cancellation) : ICaptureHostContext
    {
        public CameraModuleConfig Configuration
        {
            get
            {
                var configuration = RawCaptureIngressTests.EnvironmentalConfiguration();
                return configuration with { Rig = configuration.Rig with { ControlPolicy = new()
                    { ExposureControl = AutomaticControlOwnership.Disabled, GainControl = AutomaticControlOwnership.Disabled } } };
            }
        }
        internal CaptureLoopSubmission? Submission { get; private set; }
        public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            Submission = submission;
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private sealed class DeadlineModule : ICameraModule
    {
        public string Id => "deadline-fixture";
        public string DisplayName => "Deadline fixture";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new CaptureResult(null, new(TimeSpan.FromMilliseconds(1), 0, null, null),
                TimeSpan.Zero, CaptureMode.Still, false));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
