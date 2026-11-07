namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// One-shot exposure clock. Rendering/algorithm fixtures opt into automatic advancement; acquisition tests advance
/// manually to prove that no frame can complete early. This affects only tests, never a runtime camera option.
/// </summary>
internal sealed class ExposureTestTimeProvider(DateTimeOffset startUtc, bool autoAdvance = false) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ExposureTimer> _timers = [];
    private long _ticks = startUtc.UtcTicks;
    internal TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() { lock (_sync) return new(_ticks, TimeSpan.Zero); }
    public override long GetTimestamp() { lock (_sync) return _ticks; }
    internal int ActiveTimers { get { lock (_sync) return _timers.Count; } }

    // Pixel/provenance fixtures intentionally measure modeled scenes rather than wall-clock acquisition.
    internal static ExposureTestTimeProvider ForRendering() => new(new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), true);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot exposure timers are supported.");
        ExposureTimer timer;
        lock (_sync)
        {
            timer = new(this, callback, state, _ticks + dueTime.Ticks);
            _timers.Add(timer);
        }
        TimerCreated.TrySetResult();
        if (autoAdvance) Advance(dueTime);
        return timer;
    }

    internal void Advance(TimeSpan elapsed)
    {
        List<ExposureTimer> ready;
        lock (_sync)
        {
            _ticks += elapsed.Ticks;
            ready = _timers.Where(timer => timer.Deadline <= _ticks).ToList();
            foreach (var timer in ready) _timers.Remove(timer);
        }
        foreach (var timer in ready) timer.Fire();
    }

    private sealed class ExposureTimer(ExposureTestTimeProvider owner, TimerCallback callback, object? state, long deadline) : ITimer
    {
        private int _disposed;
        internal long Deadline { get; } = deadline;
        internal void Fire() { if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            lock (owner._sync) owner._timers.Remove(this);
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
