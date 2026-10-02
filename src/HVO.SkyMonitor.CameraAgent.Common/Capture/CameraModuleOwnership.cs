using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

/// <summary>Read-only view of the camera module currently owned by <see cref="CameraCaptureService"/>.</summary>
public sealed record CameraModuleOwnershipSnapshot(
    bool Published,
    long Generation,
    string? ModuleType,
    string? DisplayName,
    CameraFocusPreviewFidelity? FocusPreview,
    CameraSimulatedFocusModel? SimulatedFocus)
{
    public static CameraModuleOwnershipSnapshot None { get; } = new(false, 0, null, null, null, null);
}

/// <summary>
/// Publishes the initialized camera module owned by <see cref="CameraCaptureService"/> so that side paths such as manual
/// focus previews use the same module instance rather than constructing a second owner. A side path holds a
/// <see cref="CameraModuleLease"/> for one call; revocation cancels every lease's token and waits for the leases to be
/// released before the owner disposes the module, so a lease never observes a disposed or re-initialized module.
/// </summary>
public sealed class CameraModuleOwnership : IDisposable
{
    private readonly object _sync = new();
    private Publication? _current;
    private long _generation;
    private bool _disposed;

    public CameraModuleOwnershipSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                if (_current is not { } publication)
                {
                    return CameraModuleOwnershipSnapshot.None with { Generation = _generation };
                }
                var preview = publication.Module as ICameraFocusPreviewCapture;
                return new(true, publication.Generation, publication.Module.ModuleType, publication.Module.DisplayName,
                    preview?.FocusPreviewFidelity, preview?.SimulatedFocus);
            }
        }
    }

    /// <summary>Acquires a lease on the published module, or returns false when no module is published.</summary>
    public bool TryAcquire([NotNullWhen(true)] out CameraModuleLease? lease)
    {
        lock (_sync)
        {
            if (_disposed || _current is not { } publication)
            {
                lease = null;
                return false;
            }
            publication.Leases++;
            lease = new CameraModuleLease(this, publication);
            return true;
        }
    }

    /// <summary>
    /// Publishes an initialized module with the configuration it was initialized from. Only the capture owner calls this,
    /// after initialization succeeds.
    /// </summary>
    internal long Publish(ICameraModule module, CameraModuleConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is not null)
            {
                throw new InvalidOperationException("A camera module is already published; revoke it first.");
            }
            _current = new Publication(module, config, ++_generation);
            return _current.Generation;
        }
    }

    /// <summary>
    /// Withdraws the published module, cancels outstanding leases, and waits up to <paramref name="drainTimeout"/> for
    /// them to be released. Returns false when a lease is still held at the deadline; the caller must then treat the
    /// module as still in use.
    /// </summary>
    internal async Task<bool> RevokeAsync(TimeSpan drainTimeout)
    {
        Publication? publication;
        lock (_sync)
        {
            publication = _current;
            _current = null;
            if (publication is null)
            {
                return true;
            }
            publication.Revoked = true;
            if (publication.Leases == 0)
            {
                publication.Drained.TrySetResult();
            }
        }
        await publication.Revocation.CancelAsync().ConfigureAwait(false);
        try
        {
            await publication.Drained.Task.WaitAsync(drainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        publication.Revocation.Dispose();
        return true;
    }

    internal void Release(Publication publication)
    {
        lock (_sync)
        {
            publication.Leases--;
            if (publication.Revoked && publication.Leases == 0)
            {
                publication.Drained.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_current is { } publication)
            {
                publication.Revoked = true;
                publication.Revocation.Cancel();
                _current = null;
            }
        }
    }

    internal sealed class Publication(ICameraModule module, CameraModuleConfig? config, long generation)
    {
        public ICameraModule Module { get; } = module;
        public CameraModuleConfig? Config { get; } = config;
        public long Generation { get; } = generation;
        public CancellationTokenSource Revocation { get; } = new();
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Leases { get; set; }
        public bool Revoked { get; set; }
    }
}

/// <summary>A short-lived claim on the published module. Dispose it as soon as the call completes.</summary>
public sealed class CameraModuleLease : IDisposable
{
    private readonly CameraModuleOwnership _owner;
    private readonly CameraModuleOwnership.Publication _publication;
    private int _released;

    internal CameraModuleLease(CameraModuleOwnership owner, CameraModuleOwnership.Publication publication)
    {
        _owner = owner;
        _publication = publication;
        Revoked = publication.Revocation.Token;
    }

    public ICameraModule Module => _publication.Module;

    /// <summary>The configuration the module was initialized from, when the owner supplied it.</summary>
    public CameraModuleConfig? Config => _publication.Config;

    public long Generation => _publication.Generation;

    /// <summary>Cancelled when the owner revokes the module; link every call made through the lease to it.</summary>
    public CancellationToken Revoked { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _owner.Release(_publication);
        }
    }
}
