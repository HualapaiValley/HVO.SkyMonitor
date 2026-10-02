using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

/// <summary>
/// Converts the host's kernel clock view into the clock facts an astrometric uncertainty consumes. Only the kernel's own
/// synchronization status and maximum error are used: an SNTP offset is one sample against one server, not a bound on
/// the clock, and is never reported as one.
/// </summary>
public static class AstrometricClockFactsMapper
{
    public const string KernelSource = "kernel-adjtimex";

    /// <summary>
    /// Maps the kernel state recorded at or before the exposure. A snapshot older than <paramref name="maximumAge"/> at
    /// the exposure midpoint, or taken after it, says nothing about the clock during the exposure and maps to unknown.
    /// </summary>
    public static AstrometricClockFacts FromSnapshot(ClockSyncSnapshot? snapshot, DateTimeOffset exposureMidpointUtc, TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge), "Maximum age must be positive.");
        if (snapshot is null) return AstrometricClockFacts.NotSupplied;
        var age = exposureMidpointUtc - snapshot.MeasuredUtc;
        if (age < TimeSpan.Zero || age > maximumAge) return new(KernelSource, AstrometricClockSynchronization.Unknown, null, null);
        return FromKernel(snapshot.Kernel);
    }

    public static AstrometricClockFacts FromKernel(KernelClockState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Status switch
        {
            KernelClockStatus.Synchronized => new(KernelSource, AstrometricClockSynchronization.Synchronized,
                state.MaximumError?.TotalSeconds, state.EstimatedError?.TotalSeconds),
            KernelClockStatus.Unsynchronized => new(KernelSource, AstrometricClockSynchronization.Unsynchronized, null, null),
            _ => new(KernelSource, AstrometricClockSynchronization.Unknown, null, null),
        };
    }
}
