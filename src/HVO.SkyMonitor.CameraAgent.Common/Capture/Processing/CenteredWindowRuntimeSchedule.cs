using System.Collections.Immutable;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class CenteredWindowRuntimeSchedule
{
    internal const int MaximumPendingWindows = 16;
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProcessingGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegistrationGrace = TimeSpan.FromSeconds(30);

    internal static async Task TrackDispatchedAsync(
        SqliteTransaction transaction, ExposureAttemptKey attempt, DateTimeOffset requestedUtc,
        TimeSpan requestedInterval, TimeSpan integration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var interval = requestedInterval > TimeSpan.Zero ? requestedInterval : integration;
        if (interval <= TimeSpan.Zero) interval = MinimumInterval;
        var requested = requestedUtc.ToUniversalTime();
        var tolerance = SlotTolerance(interval);
        var previous = await ReadPriorAsync(transaction, attempt.RunId, requested, interval, cancellationToken).ConfigureAwait(false);
        var prior = previous.Count == 0 ? null : previous[0];
        var cadenceChanged = prior is not null && (prior.Start is null || interval != prior.Interval);
        var start = requested;
        // Keep the new attempt beyond the old window's reach if its last nominal time cannot be read.
        var steps = prior is { Start: null } ? CenteredWindowScheduler.CenterIndex + 1L : 1L;
        if (prior is { Start: { } priorStart })
        {
            var elapsed = (requested - priorStart).Ticks;
            if (!cadenceChanged)
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
                var separation = 2 * Math.Max(SlotTolerance(prior.Interval).Ticks, tolerance.Ticks);
                steps += Math.Max(0L, (elapsed - separation - 1) / prior.Interval.Ticks);
            }
        }
        var slot = (prior?.Slot ?? 0) + steps;
        var broken = steps > MaximumPendingWindows + 1L;
        var cadence = prior is not null && !cadenceChanged && !broken ? prior.Cadence : attempt.AttemptId.ToString("N");

        var center = new CenteredWindowSlotTime(start, cadence, ExpectedIntegration: integration);
        var priorTwo = new CenteredWindowSlotTime[CenteredWindowScheduler.CenterIndex];
        var oldest = previous.Count == 0 ? null : previous[^1];
        for (var offset = 1; offset <= priorTwo.Length; offset++)
        {
            var pastSlot = slot - offset;
            if (broken || prior is { Start: null })
            {
                priorTwo[offset - 1] = center with
                {
                    ExpectedStartUtc = start.AddTicks(-offset * interval.Ticks),
                    BoundaryReason = broken ? "cadence-discontinuity" : "calendar-predecessor-unavailable"
                };
                continue;
            }
            var saved = previous.Find(item => item.Slot <= pastSlot);
            if (saved is { } && ProjectPastSlot(saved, pastSlot) is { } point)
            {
                if (saved.Slot != pastSlot && prior is { Start: { } currentSegmentStart } &&
                    prior.Slot - saved.Slot > MaximumPendingWindows + 1L)
                {
                    priorTwo[offset - 1] = new(currentSegmentStart.AddTicks(-prior.Interval.Ticks), prior.Cadence,
                        "cadence-discontinuity", prior.Integration);
                    continue;
                }
                priorTwo[offset - 1] = new(point, saved.Cadence, ExpectedIntegration: saved.Integration);
                continue;
            }
            var pointBeforeRun = (saved is null && oldest is not null ? ProjectPastSlot(oldest, pastSlot) : null)
                ?? start.AddTicks(-offset * interval.Ticks);
            priorTwo[offset - 1] = new(pointBeforeRun, oldest?.Cadence ?? cadence,
                pastSlot <= 0 ? "before-run-start" : "calendar-predecessor-unavailable", oldest?.Integration ?? integration);
        }

        var projected = ImmutableArray.Create(priorTwo[1], priorTwo[0], center,
            center with { ExpectedStartUtc = start + interval },
            center with { ExpectedStartUtc = start + interval + interval });
        var schedule = CreateSchedule(attempt, start, interval, integration);
        if (!broken && steps > 1 && prior is { Start: { } priorScheduled })
        {
            for (var skipped = 1L; skipped < steps; skipped++)
            {
                var missedStart = priorScheduled.AddTicks(checked(prior.Interval.Ticks * skipped));
                var missed = CreateSchedule(attempt, missedStart, prior.Interval, prior.Integration);
                var missedTimes = Enumerable.Range(-CenteredWindowScheduler.CenterIndex, CenteredWindowScheduler.SlotCount)
                    .Select(offset => new CenteredWindowSlotTime(missedStart.AddTicks(checked(prior.Interval.Ticks * offset)),
                        prior.Cadence, ExpectedIntegration: prior.Integration)).ToImmutableArray();
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
        TimeSpan interval, TimeSpan integration)
    {
        var deadline = start.AddTicks(checked(2 * interval.Ticks + Math.Max(integration.Ticks, 0) + ProcessingGrace.Ticks));
        return new(attempt.Camera, attempt.RunId, attempt.RunId.ToString("N"), start, SlotTolerance(interval), deadline,
            CenteredRegisteredStackRecipe.FixedIdentity.IdentitySha256, deadline + RegistrationGrace);
    }

    private static TimeSpan SlotTolerance(TimeSpan interval) =>
        TimeSpan.FromTicks(Math.Min(interval.Ticks / 5, TimeSpan.FromMilliseconds(50).Ticks));

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
            SELECT slot, start_ticks, interval_ticks, integration_ticks, cadence
            FROM processing_centered_attempt_calendar WHERE run_id = $run ORDER BY slot DESC LIMIT 2;
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
                TimeSpan.FromTicks(reader.GetInt64(3)), reader.GetString(4)));
        }
        return result;
    }

    private sealed record PriorSlot(long Slot, DateTimeOffset? Start, TimeSpan Interval, TimeSpan Integration, string Cadence);
}
