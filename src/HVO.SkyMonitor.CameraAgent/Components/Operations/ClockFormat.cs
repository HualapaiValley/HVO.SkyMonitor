using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>Shared wording for the clock measurement, so Health and System control describe it the same way.</summary>
internal static class ClockFormat
{
    /// <summary>
    /// A signed clock difference: "+12 ms", "−1.25 s", "+3m 20s". The sign always shows, since the direction is the
    /// point of the value, except for a difference that rounds to "0 ms".
    /// </summary>
    internal static string Signed(TimeSpan value)
    {
        var magnitude = value.Duration();
        if (magnitude.TotalMilliseconds < 0.5)
        {
            return Unsigned(TimeSpan.Zero);
        }
        var sign = value < TimeSpan.Zero ? "−" : "+";
        return string.Concat(sign, magnitude.TotalMilliseconds < 1000
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(magnitude.TotalMilliseconds):0} ms")
            : Unsigned(magnitude));
    }

    /// <summary>A duration without a sign: "12 ms", "1.25 s", "3m 20s", "2h 5m", "4d 1h".</summary>
    internal static string Unsigned(TimeSpan value)
    {
        var magnitude = value.Duration();
        return magnitude switch
        {
            { TotalMilliseconds: < 1000 } => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(magnitude.TotalMilliseconds):0} ms"),
            { TotalSeconds: < 60 } => string.Create(CultureInfo.InvariantCulture, $"{magnitude.TotalSeconds:0.##} s"),
            { TotalHours: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{magnitude.Minutes}m {magnitude.Seconds}s"),
            { TotalDays: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{magnitude.Hours}h {magnitude.Minutes}m"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{(int)magnitude.TotalDays}d {magnitude.Hours}h"),
        };
    }

    /// <summary>A clock difference in words: "12 ms ahead", "1.25 s behind", or "0 ms" when it rounds to nothing.</summary>
    internal static string Relative(TimeSpan drift)
        => drift.Duration().TotalMilliseconds < 0.5
            ? Unsigned(TimeSpan.Zero)
            : string.Concat(Unsigned(drift), drift < TimeSpan.Zero ? " behind" : " ahead");

    internal static string StatusText(ClockSyncStatus status) => status switch
    {
        ClockSyncStatus.Disabled => "Checking off",
        ClockSyncStatus.NotMeasured => "Not measured",
        ClockSyncStatus.InTolerance => "In tolerance",
        ClockSyncStatus.HostSynchronized => "Host synced",
        ClockSyncStatus.Drifting => "Drifting",
        ClockSyncStatus.Unsynchronized => "Unsynchronized",
        _ => "Unverified",
    };

    internal static string StatusChip(ClockSyncStatus status) => status switch
    {
        ClockSyncStatus.InTolerance or ClockSyncStatus.HostSynchronized => "success",
        ClockSyncStatus.Drifting or ClockSyncStatus.Unsynchronized or ClockSyncStatus.Unverified => "warning",
        ClockSyncStatus.NotMeasured => "pending",
        _ => "neutral",
    };

    /// <summary>The Health page's "Clock drift" fact: the measured drift, or why there is none.</summary>
    internal static string HealthFact(SystemClockFact? clock) => clock switch
    {
        null => "Unknown",
        { Drift: { } drift } => Signed(drift),
        _ => StatusText(clock.Status),
    };
}
