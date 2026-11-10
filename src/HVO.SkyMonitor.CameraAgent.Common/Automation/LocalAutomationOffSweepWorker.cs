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
    private string? _scriptRunKey;
    private CancellationTokenSource? _scriptStop;
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

    /// <summary>Cancels only the matching script attempt and waits for its real journal/tree drain.</summary>
    public async ValueTask<bool> StopScriptRunAsync(string runKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        cancellationToken.ThrowIfCancellationRequested();
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
        await cancellation.ConfigureAwait(false);
        await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
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
            _scriptRunKey = null;
            _scriptStop?.Dispose();
            _scriptStop = null;
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
