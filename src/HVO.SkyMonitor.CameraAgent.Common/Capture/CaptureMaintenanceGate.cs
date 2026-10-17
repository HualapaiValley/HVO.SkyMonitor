using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

/// <summary>
/// The capture maintenance barrier, reachable from code that the admission coordinator itself depends on (raw ingress
/// initialization, processing steps, frame storage). The coordinator attaches itself when it is constructed.
/// </summary>
/// <remarks>
/// A deletion or move that a snapshot restore could depend on runs only while it holds a maintenance lease, and the
/// coordinator acknowledges a pause only after every lease is released. <see cref="TryEnterIfInitialized"/> never
/// initializes admission, so it is safe on paths admission initialization waits for; while admission is not yet
/// initialized, or not running, it returns nothing, and the caller defers its deletion. A process with no admission
/// coordinator at all (a tool or a test host) can never acknowledge a pause, so it has nothing to hold: the CameraAgent
/// host constructs its coordinator with its hosted services, before any lane worker or reconciliation starts.
/// </remarks>
public sealed class CaptureMaintenanceGate
{
    private readonly ConcurrentQueue<Func<CancellationToken, ValueTask>> _deferred = new();
    private CaptureAdmissionCoordinator? _coordinator;
    private int _draining;

    /// <summary>Raised whenever capture admission opens for capture, so deferred maintenance can run again.</summary>
    internal event Action? CaptureRunning;

    internal void Attach(CaptureAdmissionCoordinator coordinator)
        => Volatile.Write(ref _coordinator, coordinator ?? throw new ArgumentNullException(nameof(coordinator)));

    /// <summary>
    /// A lease while admission is initialized, running and not acquiring a pause, and null while it is held; with no
    /// admission coordinator in the process, a lease that holds nothing.
    /// </summary>
    internal IDisposable? TryEnterIfInitialized()
        => Volatile.Read(ref _coordinator) is { } coordinator
            ? coordinator.TryEnterMaintenanceIfInitialized()
            : NoAdmission.Instance;

    private sealed class NoAdmission : IDisposable
    {
        public static readonly NoAdmission Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>The admission state that holds maintenance, for the held log; Unavailable before admission exists.</summary>
    internal CaptureAdmissionState HeldBy
        => Volatile.Read(ref _coordinator)?.Snapshot is { IsInitialized: true } snapshot
            ? snapshot.State
            : CaptureAdmissionState.Unavailable;

    /// <summary>
    /// Keeps a best-effort cleanup that a held barrier deferred, to run under the barrier once capture admission opens.
    /// It lives in memory: a restart while capture is paused forgets it.
    /// </summary>
    internal void DeferUntilRunning(Func<CancellationToken, ValueTask> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _deferred.Enqueue(cleanup);
    }

    internal void NotifyRunning()
    {
        CaptureRunning?.Invoke();
        if (!_deferred.IsEmpty && Interlocked.Exchange(ref _draining, 1) == 0) _ = Task.Run(DrainDeferredAsync);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A deferred cleanup is best effort, like the post-commit cleanup it defers; one failure must not strand the rest.")]
    private async Task DrainDeferredAsync()
    {
        try
        {
            while (_deferred.TryPeek(out _))
            {
                using var lease = TryEnterIfInitialized();
                if (lease is null) return;
                if (!_deferred.TryDequeue(out var cleanup)) return;
                try
                {
                    await cleanup(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }
        finally
        {
            Volatile.Write(ref _draining, 0);
        }
    }
}

/// <summary>
/// A deletion or in-place rewrite of durable evidence deferred because capture admission is not running: a snapshot
/// restore could still depend on the bytes. Callers treat it as retryable.
/// </summary>
internal sealed class CaptureMaintenanceHeldException : IOException
{
    internal CaptureMaintenanceHeldException()
        : base("Maintenance is held while capture admission is not running; it is retried once capture runs.")
    {
    }

    internal CaptureMaintenanceHeldException(string maintenance)
        : base($"{maintenance} is held while capture admission is not running; it is retried once capture runs.")
    {
    }

    internal CaptureMaintenanceHeldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
