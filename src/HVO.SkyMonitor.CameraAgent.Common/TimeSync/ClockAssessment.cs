using System.Globalization;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

public enum ClockSyncStatus
{
    /// <summary>Clock checking is turned off in the settings.</summary>
    Disabled,

    /// <summary>No round has completed since checking was turned on.</summary>
    NotMeasured,

    /// <summary>A server answered and the offset is within the tolerance.</summary>
    InTolerance,

    /// <summary>No server answered, but the host's time service reports the clock synchronized.</summary>
    HostSynchronized,

    /// <summary>A server answered and the offset exceeds the tolerance.</summary>
    Drifting,

    /// <summary>The offset is within the tolerance, but the host's time service reports the clock unsynchronized.</summary>
    Unsynchronized,

    /// <summary>No server answered and nothing else vouches for the clock.</summary>
    Unverified,
}

/// <summary>
/// What the latest measurement says about the clock. The health check and the operator UI both use it, so they agree.
/// A clock problem is never reported as unhealthy: capture keeps running and the problem is recorded instead. The
/// description names no server, because the health endpoint serves it without authentication.
/// </summary>
public sealed record ClockAssessment(ClockSyncStatus Status, string Description)
{
    public bool IsHealthy => Status is ClockSyncStatus.Disabled or ClockSyncStatus.InTolerance or
        ClockSyncStatus.HostSynchronized;

    public static ClockAssessment Evaluate(TimeSyncSettings settings, ClockSyncSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
        {
            return new(ClockSyncStatus.Disabled, "Clock checking is turned off.");
        }
        if (snapshot is null || !snapshot.Enabled)
        {
            return new(ClockSyncStatus.NotMeasured, "The clock has not been measured yet.");
        }
        var kernel = snapshot.Kernel.Status;
        if (snapshot.Selected?.Offset is not { } offset)
        {
            return kernel == KernelClockStatus.Synchronized
                ? new(ClockSyncStatus.HostSynchronized,
                    "No time server answered; the host's time service reports the clock synchronized.")
                : new(ClockSyncStatus.Unverified,
                    "No time server answered and the host's time service does not report the clock synchronized.");
        }
        var tolerance = Milliseconds(settings.Tolerance);
        if (offset.Duration() > settings.Tolerance)
        {
            return new(ClockSyncStatus.Drifting, string.Create(
                CultureInfo.InvariantCulture,
                $"The clock is {Milliseconds(offset.Duration())} ms {(offset < TimeSpan.Zero ? "ahead of" : "behind")} network time, beyond the {tolerance} ms tolerance."));
        }
        return kernel == KernelClockStatus.Unsynchronized
            ? new(ClockSyncStatus.Unsynchronized, string.Create(
                CultureInfo.InvariantCulture,
                $"The clock is within {tolerance} ms of network time, but the host's time service reports it unsynchronized."))
            : new(ClockSyncStatus.InTolerance, string.Create(
                CultureInfo.InvariantCulture,
                $"The clock is within {tolerance} ms of network time."));
    }

    private static string Milliseconds(TimeSpan value)
        => Math.Round(value.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture);
}
