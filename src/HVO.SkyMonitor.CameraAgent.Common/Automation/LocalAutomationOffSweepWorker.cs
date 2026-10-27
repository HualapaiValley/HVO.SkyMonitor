using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>
/// One bounded execution slot, reserved before an occurrence is claimed. Dispatch always uses the
/// thread pool, including for synchronous adapters. The reservation tracks work through its real
/// journal completion; shutdown cancels and drains it before execution authority can be released.
/// </summary>
public sealed class LocalAutomationOffSweepWorker : BackgroundService
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource _idle = Completed();
    private Task _execution = Task.CompletedTask;
    private bool _reserved;
    private bool _stopped;
    private int _disposed;

    internal Reservation? TryReserve()
    {
        lock (_sync)
        {
            if (_stopped || _reserved)
            {
                return null;
            }
            _reserved = true;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(this);
        }
    }

    /// <summary>Completes only when the reserved occurrence and its authority have drained.</summary>
    public Task WhenIdleAsync()
    {
        lock (_sync)
        {
            return _idle.Task;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _stopped = true;
        }
        await _stopping.CancelAsync().ConfigureAwait(false);
        // The host deadline cannot release authority while an adapter is still draining.
        await WhenIdleAsync().ConfigureAwait(false);
        Task execution;
        lock (_sync)
        {
            execution = _execution;
        }
        await execution.ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _stopping.Dispose();
        base.Dispose();
    }

    private void Release()
    {
        lock (_sync)
        {
            _reserved = false;
            _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    internal sealed class Reservation(LocalAutomationOffSweepWorker owner) : IDisposable
    {
        private int _dispatched;
        private int _released;

        internal void Dispatch(Func<CancellationToken, Task> execute, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _dispatched, 1) != 0)
            {
                throw new InvalidOperationException("An off-sweep reservation can dispatch only once.");
            }
            // Do not pass a cancellation token to Task.Run: even a pre-cancelled claim must
            // enter the delegate so its authority and reservation are released.
            lock (owner._sync)
            {
                owner._execution = Task.Run(async () =>
                {
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(owner._stopping.Token, cancellationToken);
                        await execute(linked.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Release();
                    }
                }, CancellationToken.None);
            }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _dispatched) == 0)
            {
                Release();
            }
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
