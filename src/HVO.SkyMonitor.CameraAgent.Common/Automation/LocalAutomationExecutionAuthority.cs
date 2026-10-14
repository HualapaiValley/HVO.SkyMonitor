using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>Keeps the single local journal authority until an admitted task has actually drained.</summary>
public interface ILocalAutomationExecutionAuthority
{
    /// <summary>Retains the lifetime authority through execution and its durable completion.</summary>
    IDisposable RetainExecutionAuthority();
}

internal sealed class LocalAutomationExecutionAuthority(Action onReleased) : IDisposable
{
    private readonly object _sync = new();
    private FileStream? _stream;
    private int _retainers;
    private bool _disposeRequested;
    private bool _released;

    internal void Acquire(string root, string path)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            if (_stream is not null)
            {
                return;
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.WriteThrough);
            try
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
                _stream = stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    internal IDisposable Retain(bool allowDrainingCompletion = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_released || _disposeRequested &&
                !(allowDrainingCompletion && _retainers > 0), this);
            if (_stream is null)
            {
                throw new InvalidOperationException("The automation journal authority has not initialized.");
            }
            _retainers++;
            return new Retainer(() =>
            {
                lock (_sync)
                {
                    _retainers--;
                    ReleaseIfDrained();
                }
            });
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposeRequested = true;
            ReleaseIfDrained();
        }
    }

    private void ReleaseIfDrained()
    {
        if (_disposeRequested && _retainers == 0 && !_released)
        {
            _released = true;
            _stream?.Dispose();
            onReleased();
        }
    }

    private sealed class Retainer(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }
}
