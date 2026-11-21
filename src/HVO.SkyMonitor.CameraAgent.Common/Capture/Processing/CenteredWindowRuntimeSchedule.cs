using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal readonly record struct CenteredDispatchSchedule(
    DateTimeOffset RequestedStartUtc, TimeSpan Interval, CaptureCadenceMode? Mode = null,
    bool IntervalChanged = false, DateTimeOffset? OverrunFromUtc = null);

internal static class CenteredWindowRuntimeSchedule
{
    internal const int MaximumPendingWindows = 16;
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProcessingGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegistrationGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ContinuousLookahead = TimeSpan.FromSeconds(10);
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
        var interval = continuous || isolate ? MinimumInterval : dispatch.Interval > TimeSpan.Zero ? dispatch.Interval : integration;
        if (interval <= TimeSpan.Zero) interval = MinimumInterval;
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
            Tolerance: tolerance, Continuous: continuous);
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
                    BoundaryReason = broken ? "cadence-discontinuity" : "calendar-predecessor-unavailable"
                };
                continue;
            }
            if (missedStartUtc is { } missed && prior is not null && pastSlot > prior.Slot)
            {
                priorTwo[offset - 1] = center with
                {
                    ExpectedStartUtc = missed.AddTicks(checked((pastSlot - prior.Slot - 1) * interval.Ticks))
                };
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
                priorTwo[offset - 1] = new(point, saved.Cadence, ExpectedIntegration: saved.Integration,
                    Tolerance: SlotTolerance(saved.Interval), Continuous: saved.Continuous);
                continue;
            }
            var pointBeforeRun = (saved is null && oldest is not null ? ProjectPastSlot(oldest, pastSlot) : null)
                ?? start.AddTicks(-offset * interval.Ticks);
            priorTwo[offset - 1] = new(pointBeforeRun, oldest?.Cadence ?? cadence,
                pastSlot <= 0 ? "before-run-start" : "calendar-predecessor-unavailable", oldest?.Integration ?? integration,
                oldest is null ? tolerance : SlotTolerance(oldest.Interval), oldest?.Continuous ?? continuous);
        }

        var projected = ImmutableArray.Create(priorTwo[1], priorTwo[0], center,
            center with { ExpectedStartUtc = start + interval },
            center with { ExpectedStartUtc = start + interval + interval });
        var schedule = CreateSchedule(attempt, start, interval, integration, continuous);
        if (isolate)
            schedule = schedule with { GenerationId = cadence };
        if (!broken && !isolate && steps > 1 && prior is { Start: { } priorScheduled })
        {
            for (var skipped = 1L; skipped < steps; skipped++)
            {
                var missedInterval = missedStartUtc.HasValue ? interval : prior.Interval;
                var missedStart = missedStartUtc is { } firstMissed
                    ? firstMissed.AddTicks(checked(interval.Ticks * (skipped - 1)))
                    : priorScheduled.AddTicks(checked(prior.Interval.Ticks * skipped));
                var missed = CreateSchedule(attempt, missedStart, missedInterval, prior.Integration);
                var missedTimes = Enumerable.Range(-CenteredWindowScheduler.CenterIndex, CenteredWindowScheduler.SlotCount)
                    .Select(offset => new CenteredWindowSlotTime(missedStart.AddTicks(checked(missedInterval.Ticks * offset)),
                        missedStartUtc.HasValue ? cadence : prior.Cadence, ExpectedIntegration: prior.Integration,
                        Tolerance: SlotTolerance(missedInterval))).ToImmutableArray();
                await SqliteCaptureProcessingStore.RecordCenteredMissedReferenceAsync(transaction, missed, missedTimes, now,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        var checkpoint = new CenteredWindowCheckpoint(schedule, projected, attempt);
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
        TimeSpan interval, TimeSpan integration, bool continuous = false)
    {
        var lookahead = continuous ? ContinuousLookahead.Ticks : checked(2 * interval.Ticks);
        var deadline = start.AddTicks(checked(lookahead + Math.Max(integration.Ticks, 0) + ProcessingGrace.Ticks));
        return new(attempt.Camera, attempt.RunId, attempt.RunId.ToString("N"), start, SlotTolerance(interval), deadline,
            CenteredRegisteredStackRecipe.FixedIdentity.IdentitySha256, deadline + RegistrationGrace);
    }

    internal static TimeSpan SlotTolerance(TimeSpan interval) =>
        TimeSpan.FromTicks(Math.Min(interval.Ticks / 5, TimeSpan.FromMilliseconds(50).Ticks));

    internal static CenteredWindowSlotTime? ReadProjectedPredecessor(string? json) =>
        json is not null && JsonSerializer.Deserialize<CenteredWindowSlotTime>(json, SlotJson) is
        { Tolerance: not null, BoundaryReason: null } time ? time : null;

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
                json_extract(CASE WHEN json_valid(window.checkpoint_json) THEN window.checkpoint_json END, '$.projectedTimes[1]')
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
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6)));
        }
        return result;
    }

    private sealed record PriorSlot(long Slot, DateTimeOffset? Start, TimeSpan Interval, TimeSpan Integration, string Cadence,
        bool Continuous, string? ProjectedPredecessorJson);
}
