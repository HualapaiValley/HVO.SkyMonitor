using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal readonly record struct CenteredDispatchSchedule(
    DateTimeOffset RequestedStartUtc, TimeSpan Interval, CaptureCadenceMode? Mode = null,
    bool IntervalChanged = false, DateTimeOffset? OverrunFromUtc = null, TimeSpan PreviousAcquisitionDuration = default);

internal static class CenteredWindowRuntimeSchedule
{
    internal const int MaximumPendingWindows = 16;
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProcessingGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegistrationGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ContinuousHandoffAllowance = TimeSpan.FromSeconds(10);
    internal const string NominalSlotMissed = "nominal-slot-missed";
    private static readonly JsonSerializerOptions SlotJson = new(JsonSerializerDefaults.Web);

    internal static async Task TrackDispatchedAsync(
        SqliteTransaction transaction, ExposureAttemptKey attempt, CenteredDispatchSchedule dispatch,
        TimeSpan integration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        const string savepoint = "centered_calendar_dispatch";
        await transaction.SaveAsync(savepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            await TrackDispatchedCoreAsync(transaction, attempt, dispatch, integration, now, false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentOutOfRangeException or OverflowException)
        {
            await transaction.RollbackAsync(savepoint, cancellationToken).ConfigureAwait(false);
            await TrackDispatchedCoreAsync(transaction, attempt, dispatch, integration, now, true, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.ReleaseAsync(savepoint, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TrackDispatchedCoreAsync(
        SqliteTransaction transaction, ExposureAttemptKey attempt, CenteredDispatchSchedule dispatch,
        TimeSpan integration, DateTimeOffset now, bool isolate, CancellationToken cancellationToken)
    {
        var continuous = dispatch.Mode == CaptureCadenceMode.Continuous;
        var schedulerOwned = dispatch.Mode.HasValue;
        var interval = continuous || isolate ? MinimumInterval : dispatch.Interval > TimeSpan.Zero ? dispatch.Interval : integration;
        if (interval <= TimeSpan.Zero) interval = MinimumInterval;
        // A minimum interval is a floor. Each sequential successor can spend
        // this much time integrating and completing acquisition before the next.
        var forecastTicks = schedulerOwned
            ? Math.Max(interval.Ticks, Math.Max(integration.Ticks, dispatch.PreviousAcquisitionDuration.Ticks))
            : interval.Ticks;
        var forecastInterval = TimeSpan.FromTicks(forecastTicks);
        var requested = dispatch.RequestedStartUtc.ToUniversalTime();
        var tolerance = SlotTolerance(interval);
        var previous = await ReadPriorAsync(transaction, attempt.RunId, requested, interval, cancellationToken).ConfigureAwait(false);
        var prior = previous.Count == 0 ? null : previous[0];
        var cadenceChanged = prior is not null && (prior.Start is null || interval != prior.Interval ||
            prior.Continuous != continuous || dispatch.IntervalChanged);
        var start = requested;
        // Keep the new attempt beyond the old window's reach if its last nominal time cannot be read.
        var steps = prior is not null && (isolate || prior.Start is null) ? CenteredWindowScheduler.CenterIndex + 1L : 1L;
        var missedStartUtc = dispatch.OverrunFromUtc?.ToUniversalTime();
        if (!isolate && prior is { Start: { } priorStart } && !continuous)
        {
            var elapsed = (requested - priorStart).Ticks;
            if (dispatch.Mode == CaptureCadenceMode.MinimumStartInterval)
            {
                if (missedStartUtc is { } missedStart)
                    steps += Math.Max(0L, (requested - missedStart).Ticks / interval.Ticks);
            }
            else if (!cadenceChanged)
            {
                steps = Math.Max(1L, (elapsed + interval.Ticks / 2) / interval.Ticks);
                // Minimum-start scheduling resumes from the previous actual module-call start.
                // Count holes with the cadence, then use this dispatch's request as its shutter target.
                if (elapsed <= interval.Ticks / 2)
                    start = priorStart + interval;
            }
            else
            {
                // The new interval starts at the dispatch that declares it. Earlier nominal slots retain old cadence.
                // A request close enough to the last old target occupies that slot. Recording both
                // would give their allowable shutter intervals overlapping targets.
                var separation = SlotTolerance(prior.Interval).Ticks + tolerance.Ticks;
                steps += Math.Max(0L, (elapsed - separation - 1) / prior.Interval.Ticks);
            }
        }
        var slot = (prior?.Slot ?? 0) + steps;
        var broken = steps > MaximumPendingWindows + 1L;
        var cadence = prior is not null && !isolate && !cadenceChanged && !broken ? prior.Cadence : attempt.AttemptId.ToString("N");

        var center = new CenteredWindowSlotTime(start, cadence, ExpectedIntegration: integration,
            Tolerance: tolerance, Continuous: continuous, PlannedSlot: schedulerOwned ? slot : null);
        var skipped = ImmutableArray.CreateBuilder<CenteredWindowSlotTime>();
        if (schedulerOwned && !broken && !isolate && missedStartUtc is { } firstMissed && prior is not null)
        {
            for (var ordinal = prior.Slot + 1; ordinal < slot; ordinal++)
            {
                skipped.Add(center with
                {
                    PlannedSlot = ordinal,
                    ExpectedStartUtc = firstMissed.AddTicks(checked((ordinal - prior.Slot - 1) * interval.Ticks))
                });
            }
        }
        var priorDispatch = schedulerOwned && !isolate && prior is { Start: not null }
            ? ReadDispatchCalendar(prior.DispatchCalendarJson, prior.Slot) : null;
        var priorTwo = new CenteredWindowSlotTime[CenteredWindowScheduler.CenterIndex];
        var oldest = previous.Count == 0 ? null : previous[^1];
        for (var offset = 1; offset <= priorTwo.Length; offset++)
        {
            var pastSlot = slot - offset;
            if (broken || isolate || prior is { Start: null })
            {
                priorTwo[offset - 1] = center with
                {
                    ExpectedStartUtc = start.AddTicks(-offset * interval.Ticks),
                    PlannedSlot = null,
                    BoundaryReason = broken ? "cadence-discontinuity" : "calendar-predecessor-unavailable"
                };
                continue;
            }
            var pinnedMiss = skipped.FirstOrDefault(time => time.PlannedSlot == pastSlot) ??
                priorDispatch?.Skipped.FirstOrDefault(time => time.PlannedSlot == pastSlot);
            if (pinnedMiss is not null)
            {
                priorTwo[offset - 1] = pinnedMiss;
                continue;
            }
            var saved = previous.Find(item => item.Slot <= pastSlot);
            if (saved?.Slot != pastSlot && prior is { } predecessor && predecessor.Slot == pastSlot + 1 &&
                ReadProjectedPredecessor(predecessor.ProjectedPredecessorJson) is { } pinned)
            {
                priorTwo[offset - 1] = pinned;
                continue;
            }
            if (saved is { } && ProjectPastSlot(saved, pastSlot) is { } point)
            {
                if (saved.Slot != pastSlot && prior is { Start: { } currentSegmentStart } &&
                    prior.Slot - saved.Slot > MaximumPendingWindows + 1L)
                {
                    priorTwo[offset - 1] = new(currentSegmentStart.AddTicks(-prior.Interval.Ticks), prior.Cadence,
                        "cadence-discontinuity", prior.Integration, SlotTolerance(prior.Interval), prior.Continuous);
                    continue;
                }
                priorTwo[offset - 1] = PriorTime(saved, point, schedulerOwned && saved.Slot == pastSlot ? pastSlot : null);
                continue;
            }
            var pointBeforeRun = (saved is null && oldest is not null ? ProjectPastSlot(oldest, pastSlot) : null)
                ?? start.AddTicks(-offset * interval.Ticks);
            // The calendar can start partway through a run admitted by an earlier reader.
            // Only the durable acquisition ordinal proves the predecessor predates the run.
            var beforeRun = pastSlot <= 0 && attempt.Ordinal <= offset;
            priorTwo[offset - 1] = new(pointBeforeRun, oldest?.Cadence ?? cadence,
                beforeRun ? "before-run-start" : "calendar-predecessor-unavailable", oldest?.Integration ?? integration,
                oldest is null ? tolerance : SlotTolerance(oldest.Interval), oldest?.Continuous ?? continuous);
        }

        var projected = ImmutableArray.Create(priorTwo[1], priorTwo[0], center,
            center with { ExpectedStartUtc = start + forecastInterval, PlannedSlot = null },
            center with { ExpectedStartUtc = start + forecastInterval + forecastInterval, PlannedSlot = null });
        var schedule = CreateSchedule(attempt, start, interval, integration, forecastInterval, continuous);
        if (isolate)
            schedule = schedule with { GenerationId = cadence };
        if (!broken && !isolate && steps > 1 && prior is { Start: { } priorScheduled })
        {
            for (var offset = 1L; offset < steps; offset++)
            {
                var missedInterval = missedStartUtc.HasValue ? interval : prior.Interval;
                var missedOrdinal = prior.Slot + offset;
                var missedTime = schedulerOwned ? skipped.First(time => time.PlannedSlot == missedOrdinal) : null;
                var missedStart = missedTime?.ExpectedStartUtc ?? (missedStartUtc is { } firstMissedStart
                    ? firstMissedStart.AddTicks(checked(interval.Ticks * (offset - 1)))
                    : priorScheduled.AddTicks(checked(prior.Interval.Ticks * offset)));
                var missed = CreateSchedule(attempt, missedStart, missedInterval, prior.Integration,
                    schedulerOwned ? forecastInterval : missedInterval);
                var missedTimes = Enumerable.Range(-CenteredWindowScheduler.CenterIndex, CenteredWindowScheduler.SlotCount)
                    .Select(relative => skipped.FirstOrDefault(time => time.PlannedSlot == missedOrdinal + relative) ??
                        (schedulerOwned && missedOrdinal + relative == slot ? center : null) ??
                        (schedulerOwned ? priorDispatch?.Skipped.FirstOrDefault(time => time.PlannedSlot == missedOrdinal + relative) : null) ??
                        (schedulerOwned ? ExactPriorTime(previous, missedOrdinal + relative) : null) ??
                        new CenteredWindowSlotTime(missedStart.AddTicks(checked(missedInterval.Ticks * relative)),
                        missedStartUtc.HasValue ? cadence : prior.Cadence, ExpectedIntegration: prior.Integration,
                        Tolerance: SlotTolerance(missedInterval))).ToImmutableArray();
                await SqliteCaptureProcessingStore.RecordCenteredMissedReferenceAsync(transaction, missed, missedTimes, now,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        var dispatchCalendar = schedulerOwned
            ? new CenteredWindowDispatchCalendar(slot - skipped.Count, forecastInterval, tolerance, skipped.ToImmutable()) : null;
        var checkpoint = new CenteredWindowCheckpoint(schedule, projected, attempt, DispatchCalendar: dispatchCalendar);
        var tracked = await SqliteCaptureProcessingStore.TrackCenteredWindowAsync(
            transaction, checkpoint, MaximumPendingWindows, cancellationToken).ConfigureAwait(false);
        if (!tracked)
            await SqliteCaptureProcessingStore.RecordCenteredCapacityRefusalAsync(transaction, checkpoint, cancellationToken)
                .ConfigureAwait(false);

        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_centered_attempt_calendar(
                attempt_id, run_id, slot, start_ticks, interval_ticks, integration_ticks, cadence, dispatched_ticks, window_sha256)
            VALUES($attempt, $run, $slot, $start, $interval, $integration, $cadence, $dispatch, $window);
            """;
        command.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
        command.Parameters.AddWithValue("$run", attempt.RunId.ToString("N"));
        command.Parameters.AddWithValue("$slot", slot);
        command.Parameters.AddWithValue("$start", start.UtcTicks);
        command.Parameters.AddWithValue("$interval", interval.Ticks);
        command.Parameters.AddWithValue("$integration", integration.Ticks);
        command.Parameters.AddWithValue("$cadence", cadence);
        command.Parameters.AddWithValue("$dispatch", CenteredWindowRuntimeJournal.UnobservedCalendarTicks);
        command.Parameters.AddWithValue("$window", schedule.Identity);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (tracked)
            await CenteredWindowRuntimeJournal.RefreshHoldsAsync(transaction, attempt, now, cancellationToken).ConfigureAwait(false);
    }

    private static CenteredWindowSchedule CreateSchedule(ExposureAttemptKey attempt, DateTimeOffset start,
        TimeSpan interval, TimeSpan integration, TimeSpan forecastInterval, bool continuous = false)
    {
        // Two forecast cycles, the final integration and the existing processing allowance.
        var lookahead = checked(2 * forecastInterval.Ticks + (continuous ? ContinuousHandoffAllowance.Ticks : 0));
        var deadline = start.AddTicks(checked(lookahead + Math.Max(integration.Ticks, 0) + ProcessingGrace.Ticks));
        return new(attempt.Camera, attempt.RunId, attempt.RunId.ToString("N"), start, SlotTolerance(interval), deadline,
            CenteredRegisteredStackRecipe.FixedIdentity.IdentitySha256, deadline + RegistrationGrace);
    }

    internal static TimeSpan SlotTolerance(TimeSpan interval) =>
        TimeSpan.FromTicks(Math.Min(interval.Ticks / 5, TimeSpan.FromMilliseconds(50).Ticks));

    internal static CenteredWindowSlotTime? ReadProjectedPredecessor(string? json) =>
        json is not null && JsonSerializer.Deserialize<CenteredWindowSlotTime>(json, SlotJson) is
        { Tolerance: not null, BoundaryReason: null } time ? time : null;

    internal static CenteredWindowDispatchCalendar? ReadDispatchCalendar(string? json, long dispatchedSlot)
    {
        if (json is null)
            return null;
        var calendar = JsonSerializer.Deserialize<CenteredWindowDispatchCalendar>(json, SlotJson);
        if (calendar is not { Version: 1, FirstAffectedSlot: > 0 } || calendar.ForecastInterval <= TimeSpan.Zero ||
            calendar.Tolerance < TimeSpan.Zero ||
            calendar.Skipped.IsDefault || calendar.Skipped.Length > MaximumPendingWindows ||
            dispatchedSlot - calendar.FirstAffectedSlot != calendar.Skipped.Length ||
            calendar.Skipped.Where((time, index) => time is not
            { Tolerance: not null, BoundaryReason: null, Continuous: false } ||
                time.PlannedSlot != calendar.FirstAffectedSlot + index).Any())
            throw new InvalidDataException("The centered dispatch has an invalid planned-slot record.");
        return calendar;
    }

    private static CenteredWindowSlotTime? ExactPriorTime(List<PriorSlot> previous, long ordinal) =>
        previous.Find(slot => slot.Slot == ordinal) is { Start: { } start } slot
            ? PriorTime(slot, start, ordinal) : null;

    private static CenteredWindowSlotTime PriorTime(PriorSlot slot, DateTimeOffset start, long? ordinal)
    {
        var tolerance = ordinal.HasValue ? ReadDispatchCalendar(slot.DispatchCalendarJson, slot.Slot)?.Tolerance : null;
        return new(start, slot.Cadence, ExpectedIntegration: slot.Integration,
            Tolerance: tolerance ?? SlotTolerance(slot.Interval), Continuous: slot.Continuous, PlannedSlot: ordinal);
    }

    private static DateTimeOffset? ProjectPastSlot(PriorSlot saved, long slot)
    {
        if (saved.Start is not { } start) return null;
        var ticks = start.UtcTicks + (Int128)saved.Interval.Ticks * (slot - saved.Slot);
        return ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
            ? new DateTimeOffset((long)ticks, TimeSpan.Zero) : null;
    }

    private static async Task<List<PriorSlot>> ReadPriorAsync(
        SqliteTransaction transaction, Guid run, DateTimeOffset requested, TimeSpan interval, CancellationToken cancellationToken)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT calendar.slot, calendar.start_ticks, calendar.interval_ticks, calendar.integration_ticks, calendar.cadence,
                COALESCE(json_extract(CASE WHEN json_valid(window.checkpoint_json) THEN window.checkpoint_json END,
                    '$.projectedTimes[2].continuous'), 0),
                json_extract(CASE WHEN json_valid(window.checkpoint_json) THEN window.checkpoint_json END, '$.projectedTimes[1]'),
                json_extract(CASE WHEN json_valid(window.checkpoint_json) THEN window.checkpoint_json END, '$.dispatchCalendar')
            FROM processing_centered_attempt_calendar calendar
            LEFT JOIN processing_centered_windows window ON window.window_sha256 = calendar.window_sha256
            WHERE calendar.run_id = $run ORDER BY calendar.slot DESC LIMIT 2;
            """;
        command.Parameters.AddWithValue("$run", run.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PriorSlot>(2);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ticks = reader.GetInt64(1);
            var start = ticks >= DateTimeOffset.MinValue.UtcTicks &&
                ticks <= DateTimeOffset.MaxValue.UtcTicks - interval.Ticks && ticks - requested.UtcTicks <= interval.Ticks
                ? new DateTimeOffset(ticks, TimeSpan.Zero) : (DateTimeOffset?)null;
            result.Add(new(reader.GetInt64(0), start, TimeSpan.FromTicks(reader.GetInt64(2)),
                TimeSpan.FromTicks(reader.GetInt64(3)), reader.GetString(4), reader.GetBoolean(5),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7)));
        }
        return result;
    }

    private sealed record PriorSlot(long Slot, DateTimeOffset? Start, TimeSpan Interval, TimeSpan Integration, string Cadence,
        bool Continuous, string? ProjectedPredecessorJson, string? DispatchCalendarJson);
}
