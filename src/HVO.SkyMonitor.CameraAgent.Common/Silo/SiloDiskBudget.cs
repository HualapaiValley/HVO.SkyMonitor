using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal enum SiloSpacePurpose { Capture, Restore, Catalog, Cleanup }

/// <summary>Physical block accounting. Available bytes already include versions, staging, and trash on the mount.</summary>
internal readonly record struct SiloPhysicalHeadroom(long TotalBytes, long AvailableBytes, long? TrashBytes = null,
    StorageCapacity? StateCapacity = null, SiloReclaimRoot? ReclaimRoot = null);

internal readonly record struct SiloReclaimRoot(uint DeviceMajor, uint DeviceMinor, ulong Inode, bool TracksTrash);

internal sealed record SiloPhysicalAllocation(string Id, long Bytes);

internal interface ISiloDiskBudget
{
    Task? AdmissionRelease { get; }

    long ReclaimCaptureHourBytes { get; }

    SiloPhysicalHeadroom? Observe();

    SiloPhysicalHeadroom? ObserveReclamation() => Observe();

    IDisposable? TryReserve(SiloSpacePurpose purpose, long bytes);

    /// <summary>Returns an allocation only when the observer can bind measured blocks to this exact version.</summary>
    long? ReadExactAllocationBytes(SiloExactLocator locator);
}

internal sealed class SiloDiskBudget(
    Func<SiloPhysicalHeadroom> measure,
    long captureReserve,
    long catalogReserve,
    long cleanupReserve,
    long recoveryMargin,
    Func<SiloExactLocator, long?>? measureAllocation = null, long reclaimCaptureHourBytes = long.MaxValue, Func<long?>? measureTrash = null,
    Func<SiloReclaimRoot?>? measureReclaimRoot = null) : ISiloDiskBudget
{
    internal const long GiB = 1024L * 1024 * 1024;
    private readonly object _gate = new();
    private long _reserved;
    private long _nextCapture;
    private TaskCompletionSource? _release;

    internal static long InstallerReserve(long total) => Math.Max(10 * GiB, total / 20 + (total % 20 == 0 ? 0 : 1));

    public Task? AdmissionRelease
    {
        get
        {
            lock (_gate) return _release?.Task;
        }
    }

    public SiloPhysicalHeadroom? Observe()
    {
        lock (_gate) return ObserveLocked();
    }

    public SiloPhysicalHeadroom? ObserveReclamation()
    {
        var root = measureReclaimRoot?.Invoke();
        var trash = measureTrash?.Invoke();
        SiloPhysicalHeadroom? sample;
        lock (_gate)
            sample = ObserveLocked();
        // A remount between the directory walk and capacity measurement would mix two filesystems.
        if (measureReclaimRoot is not null && (root is null || root != measureReclaimRoot())) return null;
        return sample is { } observed
            ? observed with { TrashBytes = measureTrash is null ? observed.TrashBytes : trash, ReclaimRoot = root ?? observed.ReclaimRoot }
            : null;
    }

    private SiloPhysicalHeadroom? ObserveLocked()
    {
        SiloPhysicalHeadroom sample;
        try
        {
            sample = measure();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Hold();
            return null;
        }
        if (sample.TotalBytes <= 0 || sample.AvailableBytes < 0 || sample.AvailableBytes > sample.TotalBytes ||
            sample.TrashBytes < 0)
        {
            Hold();
            return null;
        }

        var available = (decimal)sample.AvailableBytes - _reserved;
        var floor = (decimal)InstallerReserve(sample.TotalBytes) + catalogReserve + cleanupReserve + _nextCapture;
        var stateMargin = sample.StateCapacity is { } state
            ? (decimal)state.AvailableBytes - _reserved -
              ((decimal)InstallerReserve(state.TotalBytes) + catalogReserve + cleanupReserve + _nextCapture)
            : decimal.MaxValue;
        if (available <= floor || stateMargin <= 0)
            Hold();
        else if (_release is { } release && available >= floor + recoveryMargin && stateMargin >= recoveryMargin)
        {
            _release = null;
            _nextCapture = 0;
            release.TrySetResult();
        }
        return sample;
    }

    public IDisposable? TryReserve(SiloSpacePurpose purpose, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            var sample = ObserveLocked();
            if (sample is null) return null;
            var floor = purpose switch
            {
                SiloSpacePurpose.Cleanup => 0m,
                SiloSpacePurpose.Catalog => cleanupReserve,
                SiloSpacePurpose.Capture => (decimal)InstallerReserve(sample.Value.TotalBytes) + catalogReserve + cleanupReserve,
                SiloSpacePurpose.Restore => (decimal)InstallerReserve(sample.Value.TotalBytes) + catalogReserve + cleanupReserve + captureReserve,
                _ => throw new ArgumentOutOfRangeException(nameof(purpose))
            };
            var stateTooFull = sample.Value.StateCapacity is { } state &&
                (decimal)state.AvailableBytes - _reserved - bytes < (purpose is SiloSpacePurpose.Capture or SiloSpacePurpose.Restore
                    ? floor - InstallerReserve(sample.Value.TotalBytes) + InstallerReserve(state.TotalBytes) : floor);
            if ((decimal)sample.Value.AvailableBytes - _reserved - bytes < floor || stateTooFull ||
                (purpose is SiloSpacePurpose.Capture or SiloSpacePurpose.Restore && _release is not null))
            {
                if (purpose == SiloSpacePurpose.Capture)
                {
                    _nextCapture = Math.Max(bytes, _nextCapture);
                    Hold();
                }
                return null;
            }
            _reserved += bytes;
            return new Reservation(this, bytes);
        }
    }

    public long? ReadExactAllocationBytes(SiloExactLocator locator) => measureAllocation?.Invoke(locator) is > 0 and var bytes ? bytes : null;

    public long ReclaimCaptureHourBytes => reclaimCaptureHourBytes;

    private void Hold() => _release ??= new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Reservation(SiloDiskBudget owner, long bytes) : IDisposable
    {
        private SiloDiskBudget? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } budget) return;
            lock (budget._gate) budget._reserved -= bytes;
        }
    }
}
