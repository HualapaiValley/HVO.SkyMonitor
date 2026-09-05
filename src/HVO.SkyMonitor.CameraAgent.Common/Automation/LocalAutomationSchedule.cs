namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>
/// The occurrence arithmetic shared by the runner and the operator projection, so the calendar an
/// operator reads is computed by exactly the code that decides when a run fires.
/// </summary>
public static class LocalAutomationSchedule
{
    /// <summary>
    /// The instant the next periodic occurrence is due. Occurrences sit on fixed boundaries measured
    /// from the definition's epoch, so a late run never shifts the cadence. The first occurrence is
    /// one whole interval after the epoch, never at the instant the definition was recorded.
    /// </summary>
    public static DateTimeOffset NextPeriodicDueUtc(
        DateTimeOffset epochUtc,
        int intervalSeconds,
        DateTimeOffset? lastOccurrenceUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalSeconds, 1);
        var intervalTicks = TimeSpan.FromSeconds(intervalSeconds).Ticks;
        if (lastOccurrenceUtc is not { } last || last < epochUtc)
        {
            return epochUtc.AddTicks(intervalTicks);
        }
        var completed = (last - epochUtc).Ticks / intervalTicks;
        return epochUtc.AddTicks(intervalTicks * (completed + 1));
    }

    /// <summary>
    /// The occurrence index of an instant that sits on a boundary. The index identifies the
    /// occurrence in a run key, so a retry of the same occurrence is recognized as a replay.
    /// </summary>
    public static long OccurrenceIndex(DateTimeOffset epochUtc, int intervalSeconds, DateTimeOffset occurrenceUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalSeconds, 1);
        var intervalTicks = TimeSpan.FromSeconds(intervalSeconds).Ticks;
        return (occurrenceUtc - epochUtc).Ticks / intervalTicks;
    }

    /// <summary>
    /// Resolves the periodic occurrence to run now, and how many whole occurrences elapsed before it
    /// without the CameraAgent running. Returns null when nothing is due yet.
    /// </summary>
    public static (DateTimeOffset OccurrenceUtc, long MissedOccurrences)? ResolvePeriodic(
        DateTimeOffset epochUtc,
        int intervalSeconds,
        DateTimeOffset? lastOccurrenceUtc,
        DateTimeOffset nowUtc)
    {
        var due = NextPeriodicDueUtc(epochUtc, intervalSeconds, lastOccurrenceUtc);
        if (nowUtc < due)
        {
            return null;
        }
        var intervalTicks = TimeSpan.FromSeconds(intervalSeconds).Ticks;
        var skipped = (nowUtc - due).Ticks / intervalTicks;
        return (due.AddTicks(intervalTicks * skipped), skipped);
    }

    /// <summary>
    /// The most recent occurrence boundary at or before <paramref name="nowUtc"/>. Used to re-anchor a
    /// definition whose progress is being reset, so that resuming it schedules the next occurrence one
    /// whole interval away instead of reporting every boundary since the epoch as missed.
    /// </summary>
    public static DateTimeOffset LatestPeriodicBoundary(
        DateTimeOffset epochUtc,
        int intervalSeconds,
        DateTimeOffset nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalSeconds, 1);
        if (nowUtc <= epochUtc)
        {
            return epochUtc;
        }
        var intervalTicks = TimeSpan.FromSeconds(intervalSeconds).Ticks;
        var elapsed = (nowUtc - epochUtc).Ticks / intervalTicks;
        return epochUtc.AddTicks(intervalTicks * elapsed);
    }

    /// <summary>The capture sequence at which a capture-relative definition next becomes due.</summary>
    public static long? NextCaptureSequence(int intervalCaptures, long? lastCaptureSequence)
        => lastCaptureSequence is { } last ? last + intervalCaptures : null;
}
