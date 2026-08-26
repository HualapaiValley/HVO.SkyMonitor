using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProjectedSceneStageReconciliationServiceTests
{
    [TestMethod]
    public async Task ReconcileOnceAsync_DrainsOneBoundedBatchAndRecomputesOwnersEachPass()
    {
        var ingress = new StubIngress();
        var owners = new RecordingOwnerProvider();
        var reconciler = new RecordingReconciler([
            new ProjectedSceneStageReconciliationResult(2, 2, 3),
            new ProjectedSceneStageReconciliationResult(2, 2, 1),
            new ProjectedSceneStageReconciliationResult(1, 1, 0)
        ]);
        var state = new RawIngressState(TimeProvider.System);
        state.Set(RawIngressAvailability.Accepting, "accepting");
        using var telemetry = new RawIngressTelemetry(state);
        var service = new ProjectedSceneStageReconciliationService(
            ingress, owners, reconciler, state, telemetry, TimeProvider.System,
            new ProjectedSceneStageLifecycleCoordinator(),
            NullLogger<ProjectedSceneStageReconciliationService>.Instance);

        var first = await service.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var second = await service.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var third = await service.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(3, owners.CallCount);
        Assert.AreEqual(2, first.Inspected);
        Assert.AreEqual(2, second.Inspected);
        Assert.AreEqual(1, third.Inspected);
        Assert.AreEqual(0, third.BacklogCount);
        Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
    }

    [TestMethod]
    public async Task ReconcileLease_BlocksConcurrentStageRegistrationUntilSnapshotAndScanComplete()
    {
        using var lifecycle = new ProjectedSceneStageLifecycleCoordinator();
        var owners = new MutableOwnerProvider();
        var reconciler = new BarrierReconciler();
        var state = new RawIngressState(TimeProvider.System);
        using var telemetry = new RawIngressTelemetry(state);
        var service = new ProjectedSceneStageReconciliationService(
            new StubIngress(), owners, reconciler, state, telemetry, TimeProvider.System, lifecycle,
            NullLogger<ProjectedSceneStageReconciliationService>.Instance);
        var reconcile = service.ReconcileOnceAsync(CancellationToken.None).AsTask();
        await reconciler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var stageKey = new string('A', 64);

        var register = lifecycle.RegisterPendingAsync(stageKey, CancellationToken.None).AsTask();
        Assert.IsFalse(register.IsCompleted);
        reconciler.Release.SetResult();
        await reconcile.ConfigureAwait(false);
        Assert.IsTrue(await register.ConfigureAwait(false));
        owners.Keys.Add(stageKey);
        await lifecycle.ResolvePendingAsync(stageKey).ConfigureAwait(false);

        reconciler.Reset();
        await service.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.Contains(reconciler.LastProtectedKeys.ToArray(), stageKey);
        Assert.IsTrue(lifecycle.GateHoldDuration > TimeSpan.Zero);
    }

    [TestMethod]
    public async Task StartupBacklogReason_FirstBackgroundDrainReturnsAccepting()
    {
        using var lifecycle = new ProjectedSceneStageLifecycleCoordinator();
        var state = new RawIngressState(TimeProvider.System);
        state.Set(RawIngressAvailability.Accepting, "accepting");
        state.SetProjectedSceneBacklog(2);
        using var telemetry = new RawIngressTelemetry(state);
        var service = new ProjectedSceneStageReconciliationService(
            new StubIngress(), new RecordingOwnerProvider(),
            new RecordingReconciler([new ProjectedSceneStageReconciliationResult(1, 1, 0)]),
            state, telemetry, TimeProvider.System, lifecycle,
            NullLogger<ProjectedSceneStageReconciliationService>.Instance);

        await service.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
        Assert.AreEqual("accepting", state.Snapshot.Reason);
    }

    private sealed class StubIngress : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class RecordingOwnerProvider : IProjectedSceneStageOwnerProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<IReadOnlySet<string>> GetOwnedStageKeysAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
        }
    }

    private sealed class RecordingReconciler(IEnumerable<ProjectedSceneStageReconciliationResult> results) :
        IProjectedSceneStagingReconciler
    {
        private readonly Queue<ProjectedSceneStageReconciliationResult> _results = new(results);

        public ValueTask<ProjectedSceneStageReconciliationResult> ReconcileAsync(
            IReadOnlySet<string> ownedStageKeys,
            CancellationToken cancellationToken) => ValueTask.FromResult(_results.Dequeue());
    }

    private sealed class MutableOwnerProvider : IProjectedSceneStageOwnerProvider
    {
        internal HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public ValueTask<IReadOnlySet<string>> GetOwnedStageKeysAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlySet<string>>(new HashSet<string>(Keys, StringComparer.Ordinal));
    }

    private sealed class BarrierReconciler : IProjectedSceneStagingReconciler
    {
        internal TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlySet<string> LastProtectedKeys { get; private set; } = new HashSet<string>();

        public async ValueTask<ProjectedSceneStageReconciliationResult> ReconcileAsync(
            IReadOnlySet<string> ownedStageKeys,
            CancellationToken cancellationToken)
        {
            LastProtectedKeys = new HashSet<string>(ownedStageKeys, StringComparer.Ordinal);
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(1, 0, 0);
        }

        internal void Reset()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release.SetResult();
        }
    }
}
