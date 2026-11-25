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
    private TaskCompletionSource _registered = Completed();
    private Task _execution = Task.CompletedTask;
    private Task? _shutdown;
    private bool _reserved;
    private string? _scriptRunKey;
    private CancellationTokenSource? _scriptStop;
    private int _disposed;

    internal Reservation? TryReserve()
    {
        lock (_sync)
        {
            if (_shutdown is not null || _reserved)
            {
                return null;
            }
            _reserved = true;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>Cancels only the matching script attempt and waits for its real journal/tree drain.</summary>
    public async ValueTask<bool> StopScriptRunAsync(string runKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        cancellationToken.ThrowIfCancellationRequested();
        Task registered;
        lock (_sync)
        {
            // Reservation precedes the durable claim. Stop may observe Running before Dispatch
            // publishes its exact attempt key; registration or release wakes that handoff waiter.
            registered = _registered.Task;
        }
        await registered.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task cancellation;
        Task drained;
        lock (_sync)
        {
            if (_scriptStop is null || !string.Equals(_scriptRunKey, runKey, StringComparison.Ordinal)) return false;
            // Capture this occurrence's completion under the same lock as its identity. A later
            // reservation can never be cancelled by a retry of a previous run's stop command.
            drained = _idle.Task;
            cancellation = _scriptStop.CancelAsync();
        }
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (AggregateException)
        {
            // Once cancellation has faulted, report it only after this attempt releases its authority.
            await drained.ConfigureAwait(false);
            throw;
        }
        await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return _shutdown ??= DrainAsync(cancellationToken);
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            // The host deadline and cancellation callback faults cannot release authority.
            await WhenIdleAsync().ConfigureAwait(false);
            Task execution;
            lock (_sync)
            {
                execution = _execution;
            }
            await execution.ConfigureAwait(false);
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Joins the shared drain, so the stopping source is released only after an active adapter returns. A drain
    /// fault, such as a throwing cancellation callback, stays on the shared task that every <see cref="StopAsync"/>
    /// caller receives. Disposal does not rethrow it, so a service provider goes on to dispose its other singletons.
    /// </summary>
    public override void Dispose()
    {
        StopAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stopping.Dispose();
            base.Dispose();
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            _reserved = false;
            _scriptRunKey = null;
            _scriptStop?.Dispose();
            _scriptStop = null;
            _registered.TrySetResult();
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

        internal void Dispatch(Func<CancellationToken, Task> execute, LocalAutomationOccurrence occurrence,
            string journalRunKey, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _dispatched, 1) != 0)
            {
                throw new InvalidOperationException("An off-sweep reservation can dispatch only once.");
            }
            // Do not pass a cancellation token to Task.Run: even a pre-cancelled claim must
            // enter the delegate so its authority and reservation are released.
            lock (owner._sync)
            {
                var scriptStop = occurrence.Definition.TaskKind == LocalAutomationTaskKind.CustomScript
                    ? new CancellationTokenSource() : null;
                owner._scriptStop = scriptStop;
                // The adapter retains the original occurrence identity across retries, but stop
                // must name this saved attempt, just like status and terminal result persistence.
                owner._scriptRunKey = scriptStop is null ? null : journalRunKey;
                owner._registered.TrySetResult();
                owner._execution = Task.Run(async () =>
                {
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(owner._stopping.Token,
                            cancellationToken, scriptStop?.Token ?? CancellationToken.None);
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
