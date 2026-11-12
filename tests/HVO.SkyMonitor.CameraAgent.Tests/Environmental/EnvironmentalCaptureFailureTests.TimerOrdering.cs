using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalCaptureFailureTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MaximumCallerWaitUsesTheSharedDeadlineEvenWhenTimerDeliveryIsEarly(bool settlementBlocked)
    {
        var fixture = await AttributionFixture.CreateAsync(2000, blockCompletion: settlementBlocked).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var clock = new EarlyDeliveryClock();
        if (!settlementBlocked)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(fixture.Prepare(new(Guid.NewGuid(), []), async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        var bridge = fixture.CreateBridge(fixture.Store, clock);
        var request = bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).AsTask();
        try
        {
            if (settlementBlocked)
            {
                await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                fixture.Device.Release(2, fail: false);
                await fixture.CompletionWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                // Preparation's timer is disposed before we choose among the remaining capture-wait timers.
                await clock.FirstTimerDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            Assert.IsFalse(request.IsCompleted);
            // Model timer rounding/load ordering: deliver the latest wait timer first while the monotonic
            // clock still reports 1999 ms. Two independent 2000 ms timers would lose the terminal marker.
            clock.DeliverLatestTimerAt(TimeSpan.FromMilliseconds(1999));
            var acquisition = await request.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsNotNull(acquisition);
            Assert.IsTrue(acquisition.Unavailable, "The maximum configured wait is the shared storage deadline.");
            var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, acquisition.Id, Epoch,
                unavailable: acquisition.Unavailable, beforeCaptureSources: acquisition.BeforeCaptureSources).ConfigureAwait(false);
            Assert.IsTrue(raw.Manifest.Descriptor.CycleEvidence!.EnvironmentalUnavailable);
            var availability = await fixture.Store.ReadCaptureAvailabilityAsync(fixture.Root,
                raw.Manifest.Descriptor.Capture.CaptureId, acquisition.Id, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(availability.Pending);
            CollectionAssert.Contains(availability.UnavailableSources.ToArray(), "sqm");
        }
        finally
        {
            fixture.AllowCompletionWrite.TrySetResult();
            await fixture.StopAsync().ConfigureAwait(false);
        }
    }

    private sealed class EarlyDeliveryClock : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ManualTimer> timers = [];
        private long ticks;
        internal TaskCompletionSource FirstTimerDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
            var timer = new ManualTimer(this, callback, state);
            lock (sync) timers.Add(timer);
            return timer;
        }
        internal void DeliverLatestTimerAt(TimeSpan elapsed)
        {
            Interlocked.Exchange(ref ticks, elapsed.Ticks);
            ManualTimer timer;
            lock (sync) timer = timers.Last();
            timer.Fire();
        }
        private sealed class ManualTimer(EarlyDeliveryClock owner, TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            internal void Fire() => callback(state);
            public void Dispose()
            {
                lock (owner.sync) owner.timers.Remove(this);
                owner.FirstTimerDisposed.TrySetResult();
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
