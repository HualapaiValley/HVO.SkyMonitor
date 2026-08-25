using System.Diagnostics;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

public sealed class ProjectedSceneStageLifecycleCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private long _gateHoldTicks;

    internal TimeSpan GateHoldDuration => TimeSpan.FromTicks(Interlocked.Read(ref _gateHoldTicks));

    internal async ValueTask<bool> RegisterPendingAsync(string stageKey, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _pending.Add(stageKey);
        }
        finally
        {
            Interlocked.Add(ref _gateHoldTicks, Stopwatch.GetElapsedTime(started).Ticks);
            _gate.Release();
        }
    }

    internal async ValueTask ResolvePendingAsync(string stageKey)
    {
        var started = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _pending.Remove(stageKey);
        }
        finally
        {
            Interlocked.Add(ref _gateHoldTicks, Stopwatch.GetElapsedTime(started).Ticks);
            _gate.Release();
        }
    }

    internal async ValueTask<ReconciliationLease> AcquireReconciliationLeaseAsync(
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ReconciliationLease(this, new HashSet<string>(_pending, StringComparer.Ordinal), started);
    }

    public void Dispose() => _gate.Dispose();

    internal sealed class ReconciliationLease : IDisposable
    {
        private ProjectedSceneStageLifecycleCoordinator? _owner;
        private readonly long _started;

        internal ReconciliationLease(
            ProjectedSceneStageLifecycleCoordinator owner,
            IReadOnlySet<string> pendingStageKeys,
            long started)
        {
            _owner = owner;
            PendingStageKeys = pendingStageKeys;
            _started = started;
        }

        internal IReadOnlySet<string> PendingStageKeys { get; }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            Interlocked.Add(ref owner._gateHoldTicks, Stopwatch.GetElapsedTime(_started).Ticks);
            owner._gate.Release();
        }
    }
}
