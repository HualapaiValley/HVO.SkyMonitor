using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CenteredWindowRuntimeJournal : ICenteredWindowJournal
{
    // The existing non-null calendar tick can carry a pending commit observation outside the UTC range.
    internal const long UnobservedCalendarTicks = long.MaxValue;

    internal const string HoldRefusalMetricName = "camera_agent.centered_window.input_hold.refused";
    private static readonly Meter CustodyMeter = new(CaptureProcessingTelemetry.MeterName);
    private static readonly Counter<long> HoldRefusals = CustodyMeter.CreateCounter<long>(HoldRefusalMetricName, "{hold}");

    internal const string RefreshHoldsSql = """
        SELECT window.window_sha256, raw.capture_id, raw.raw_artifact_id, raw.payload_length,
            window.registration_deadline_utc_ticks
        FROM processing_centered_attempt_calendar changed
        JOIN processing_centered_attempt_calendar observer ON observer.run_id = changed.run_id
            AND observer.slot BETWEEN changed.slot - 2 AND changed.slot + 2
        JOIN processing_centered_windows window ON window.window_sha256 = observer.window_sha256
        JOIN processing_centered_attempt_calendar input ON input.run_id = observer.run_id
            AND input.slot BETWEEN observer.slot - 2 AND observer.slot + 2
        JOIN centered_exposure_attempts admitted ON admitted.attempt_id = input.attempt_id
        JOIN raw_captures raw ON raw.capture_id = admitted.capture_id
        WHERE changed.attempt_id = $attempt AND changed.slot IN (observer.slot, input.slot)
            AND window.terminal = 0 AND window.closure_json IS NULL
            AND (input.admitted_ticks IS NULL OR input.admitted_ticks <= window.deadline_utc_ticks)
            AND window.deadline_utc_ticks >= $now AND raw.state = 'committed'
        ORDER BY observer.slot, input.slot;
        """;

    public async ValueTask<CenteredWindowJournalSnapshot> ReadWindowEvidenceAsync(
        SqliteTransaction transaction, CenteredWindowCheckpoint checkpoint, DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var reference = checkpoint.ReferenceAttempt ?? throw new InvalidDataException("The live window has no reserved attempt.");
        var committed = await ReadCalendarAsync(transaction, reference, cutoffUtc, cancellationToken).ConfigureAwait(false);
        var calendar = committed.FindAll(fact => fact.Offset <= CenteredWindowScheduler.CenterIndex);
        var skips = ReadCommittedSkips(committed);
        var planned = checkpoint.DispatchCalendar is not null || skips.Count > 0 ||
            calendar.Exists(fact => fact.DispatchCalendar is not null);
        var changes = ImmutableArray<CenteredWindowCalendarChange>.Empty;
        ImmutableArray<CenteredWindowSlotTime> times;
        if (planned)
            times = ResolvePlannedTimes(checkpoint, calendar, skips);
        else
            (times, changes) = ResolveLegacyTimes(checkpoint, calendar, cutoffUtc);

        var observations = ImmutableArray.CreateBuilder<CenteredWindowSlotObservation>();
        ExposureKey? admittedReference = null;
        foreach (var fact in calendar)
        {
            var time = times[fact.Offset + CenteredWindowScheduler.CenterIndex];
            if (time.ExpectedStartUtc != fact.Start || time.CadenceRevision != fact.Cadence)
                throw new InvalidDataException("The live attempt differs from the window's projected cadence.");
            if (fact is { CaptureId: { } capture, Manifest: { } manifest, Admitted: { } admitted })
            {
                var descriptor = manifest.Descriptor;
                var raw = new CenteredRawEvidence(manifest, fact.ManifestHash!, fact.Run, IsCommitted: true);
                var key = new ExposureKey(reference.Camera, reference.RunId, capture);
                if (fact.Offset == 0)
                    admittedReference = key;
                var input = new CenteredWindowInput(key, descriptor.Capture.CaptureSequence, descriptor.Timing,
                    descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor),
                    descriptor.Artifact.ChecksumSha256, admitted);
                var products = await SqliteCaptureProcessingStore.ReadCenteredWindowProductsAsync(
                    transaction, checkpoint.Schedule, key, raw, admitted, cutoffUtc, cancellationToken).ConfigureAwait(false);
                observations.Add(new(time, input, products, Attempt: fact.Attempt));
            }
            else if (fact.Expired is { } failed &&
                (fact.ExpiryReason is "target-progress-unknown" or "schedule.start-window-closed" ||
                    cutoffUtc >= checkpoint.Schedule.DeadlineUtc))
            {
                var uncertain = fact.ExpiryReason == "target-progress-unknown";
                observations.Add(new(time, EmptySlotSettledUtc: failed, EmptySlotReason: fact.ExpiryReason,
                    Attempt: fact.Attempt, EmptySlotKind: uncertain ? CenteredWindowEmptySlotKind.Uncertain : CenteredWindowEmptySlotKind.Failed));
            }
        }

        if (planned)
        {
            foreach (var (offset, miss) in skips)
                observations.Add(new(times[offset + CenteredWindowScheduler.CenterIndex],
                    EmptySlotSettledUtc: miss.ObservedUtc, EmptySlotReason: CenteredWindowRuntimeSchedule.NominalSlotMissed));
        }
        else
        {
            var furthest = calendar.Count == 0 ? 0 : calendar[^1].Offset;
            for (var offset = -2; offset < furthest; offset++)
            {
                var time = times[offset + CenteredWindowScheduler.CenterIndex];
                if (time.BoundaryReason is null && !calendar.Exists(fact => fact.Offset == offset))
                {
                    var later = calendar.Find(fact => fact.Offset > offset)!;
                    observations.Add(new(time, EmptySlotSettledUtc: later.Dispatched,
                        EmptySlotReason: CenteredWindowRuntimeSchedule.NominalSlotMissed));
                }
            }
        }

        return new(checkpoint.Schedule.GenerationId, admittedReference, changes, observations.ToImmutable(),
            ResolvedTimes: planned ? times : default);
    }

    private static Dictionary<int, CommittedSkip> ReadCommittedSkips(List<CalendarFact> calendar)
    {
        var skips = new Dictionary<int, CommittedSkip>();
        foreach (var fact in calendar)
        {
            if (fact.DispatchCalendar is not { } dispatch)
                continue;
            foreach (var time in dispatch.Skipped)
            {
                var offset = time.PlannedSlot!.Value - fact.CenterSlot;
                if (offset is < -CenteredWindowScheduler.CenterIndex or > CenteredWindowScheduler.CenterIndex)
                    continue;
                if (!skips.TryAdd((int)offset, new(time, fact.Dispatched)))
                    throw new InvalidDataException("Multiple centered dispatches settled the same planned slot.");
            }
        }
        return skips;
    }

    private static ImmutableArray<CenteredWindowSlotTime> ResolvePlannedTimes(
        CenteredWindowCheckpoint checkpoint, List<CalendarFact> calendar, Dictionary<int, CommittedSkip> skips)
    {
        var times = checkpoint.ProjectedTimes.ToBuilder();
        var forecastInterval = times[3].ExpectedStartUtc - times[2].ExpectedStartUtc;
        for (var index = 0; index < times.Count; index++)
        {
            var offset = index - CenteredWindowScheduler.CenterIndex;
            var actual = calendar.Find(fact => fact.Offset == offset);
            if (actual is not null)
            {
                if (skips.ContainsKey(offset))
                    throw new InvalidDataException("A centered dispatch occupies a committed missed slot.");
                times[index] = new(actual.Start, actual.Cadence, ExpectedIntegration: actual.Integration,
                    Tolerance: actual.DispatchCalendar?.Tolerance ?? CenteredWindowRuntimeSchedule.SlotTolerance(actual.Interval),
                    Continuous: actual.Continuous,
                    PlannedSlot: actual.Slot);
                if (offset >= 0)
                    forecastInterval = actual.DispatchCalendar?.ForecastInterval ?? actual.Interval;
            }
            else if (skips.TryGetValue(offset, out var miss))
            {
                times[index] = miss.Time;
            }
            else if (offset > 0)
            {
                var previous = calendar.Find(fact => fact.Offset == offset - 1);
                var anchor = times[index - 1];
                var boundary = anchor.BoundaryReason;
                if (previous?.Expired is not null && calendar[^1].Offset < offset)
                    boundary = previous.ExpiryReason;
                times[index] = anchor with
                {
                    ExpectedStartUtc = anchor.ExpectedStartUtc + forecastInterval,
                    PlannedSlot = null,
                    BoundaryReason = boundary
                };
            }
        }
        return times.ToImmutable();
    }

    private static (ImmutableArray<CenteredWindowSlotTime> Times, ImmutableArray<CenteredWindowCalendarChange> Changes)
        ResolveLegacyTimes(CenteredWindowCheckpoint checkpoint, List<CalendarFact> calendar, DateTimeOffset cutoffUtc)
    {
        var changes = ImmutableArray.CreateBuilder<CenteredWindowCalendarChange>();
        var times = checkpoint.ProjectedTimes;
        for (var offset = 1; offset <= 2; offset++)
        {
            var fact = calendar.Find(item => item.Offset == offset);
            var previous = calendar.Find(item => item.Offset == offset - 1);
            var index = offset + CenteredWindowScheduler.CenterIndex;
            var projected = times[index];
            var dispatch = fact ?? (offset == 1 ? calendar.Find(item => item.Offset == 2) : null);
            // A dispatch across a gap carries the skipped predecessor's nominal time.
            // Its dispatch observation makes that hole visible at the same cutoff.
            CenteredWindowSlotTime? revised = fact is null
                ? CenteredWindowRuntimeSchedule.ReadProjectedPredecessor(dispatch?.ProjectedPredecessorJson)
                : new(fact.Start, fact.Cadence, ExpectedIntegration: fact.Integration, Continuous: fact.Continuous,
                    Tolerance: projected.Tolerance.HasValue ? CenteredWindowRuntimeSchedule.SlotTolerance(fact.Interval) : null);
            if (dispatch is not null && revised is not null && revised != projected)
            {
                var anchor = times[index - 1];
                changes.Add(new(offset - 1, anchor.ExpectedStartUtc, dispatch.Dispatched, anchor.CadenceRevision,
                    revised.CadenceRevision, dispatch.Interval, revised.ExpectedIntegration,
                    FirstAffectedStartUtc: revised.ExpectedStartUtc,
                    IsCommittedDispatchAdjustment: anchor.CadenceRevision == revised.CadenceRevision,
                    Tolerance: revised.Tolerance, Continuous: revised.Continuous));
                times = CenteredWindowScheduler.ResolveCalendar(checkpoint, changes.ToImmutable(), cutoffUtc);
            }
            else if (fact is null && previous?.Expired is { } expired && previous.ExpiryReason is { } reason &&
                calendar[^1].Offset < offset)
            {
                changes.Add(new(offset - 1, previous.Start, expired, previous.Cadence, BoundaryReason: reason));
                times = CenteredWindowScheduler.ResolveCalendar(checkpoint, changes.ToImmutable(), cutoffUtc);
                break;
            }
        }

        return (times, changes.ToImmutable());
    }

    private sealed record CommittedSkip(CenteredWindowSlotTime Time, DateTimeOffset ObservedUtc);

    private static async Task<List<CalendarFact>> ReadCalendarAsync(
        SqliteTransaction transaction, ExposureAttemptKey reference, DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH center AS MATERIALIZED (
                SELECT run_id, slot FROM processing_centered_attempt_calendar WHERE attempt_id = $attempt
            ), relevant AS (
                SELECT t.*, center.slot AS center_slot
                FROM center
                JOIN processing_centered_attempt_calendar t ON t.run_id = center.run_id
                    AND t.slot BETWEEN center.slot - 2 AND center.slot + 2
                UNION ALL
                SELECT t.*, center.slot AS center_slot
                FROM center
                JOIN processing_centered_attempt_calendar t ON t.run_id = center.run_id AND t.slot = (
                    SELECT successor.slot FROM processing_centered_attempt_calendar successor
                    WHERE successor.run_id = center.run_id AND successor.slot > center.slot + 2
                    ORDER BY successor.slot LIMIT 1)
            ), slots AS MATERIALIZED (
                SELECT t.*, attempt.ordinal, raw.capture_id, raw.manifest_json, raw.manifest_sha256,
                    run.snapshot_json, run.snapshot_sha256,
                    COALESCE(json_extract(CASE WHEN json_valid(window.checkpoint_json) THEN window.checkpoint_json END,
                        '$.projectedTimes[2].continuous'), 0) AS continuous,
                    json_extract(CASE WHEN json_valid(window.checkpoint_json) THEN window.checkpoint_json END,
                        '$.projectedTimes[1]') AS projected_predecessor,
                    json_extract(CASE WHEN json_valid(window.checkpoint_json) THEN window.checkpoint_json END,
                        '$.dispatchCalendar') AS dispatch_calendar,
                    CASE WHEN raw.capture_id IS NOT NULL THEN (
                        SELECT MIN(clock.settled_ticks)
                        FROM centered_exposure_products product
                        JOIN processing_centered_product_clock clock ON clock.revision = product.revision
                        JOIN processing_executions execution ON execution.execution_id = product.execution_id
                        WHERE product.capture_id = raw.capture_id AND product.agent_id = run.agent_id AND product.camera_id = run.camera_id
                          AND execution.capture_id = raw.capture_id AND execution.primary_artifact_id = raw.raw_artifact_id
                          AND execution.graph_revision_id = CASE WHEN json_valid(CAST(run.snapshot_json AS TEXT))
                              THEN json_extract(CAST(run.snapshot_json AS TEXT), '$.GraphRevisionId') END
                          AND execution.execution_class = 'Live' AND execution.trigger_kind = 'capture'
                          AND execution.allow_automatic_publication = 1
                    ) END AS product_ticks
                FROM relevant t
                JOIN centered_exposure_attempts attempt ON attempt.attempt_id = t.attempt_id
                JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
                LEFT JOIN processing_centered_windows window ON window.window_sha256 = t.window_sha256
                LEFT JOIN raw_captures raw ON raw.capture_id = attempt.capture_id AND raw.state = 'committed'
                    AND raw.agent_id = run.agent_id
                WHERE run.agent_id = $agent AND run.camera_id = $camera
            ), observed AS (
                SELECT *,
                    MIN(COALESCE(admitted_ticks, product_ticks), COALESCE(product_ticks, admitted_ticks)) AS raw_ticks
                FROM slots
            )
            SELECT MIN(slot - center_slot, 3), start_ticks, interval_ticks, integration_ticks, cadence,
                MIN(dispatched_ticks, COALESCE(raw_ticks, dispatched_ticks)), raw_ticks, expired_ticks, expired_reason,
                attempt_id, ordinal,
                CASE WHEN slot <= center_slot + 2 AND raw_ticks <= $cutoff THEN capture_id END, manifest_json, manifest_sha256,
                snapshot_json, snapshot_sha256, continuous, projected_predecessor, slot, center_slot, dispatch_calendar
            FROM observed
            WHERE MIN(dispatched_ticks, COALESCE(raw_ticks, dispatched_ticks)) <= $cutoff
                AND (slot <= center_slot + 2 OR
                    (json_array_length(dispatch_calendar, '$.skipped') > 0 AND
                     json_extract(dispatch_calendar, '$.firstAffectedSlot') <= center_slot + 2))
            ORDER BY slot;
            """;
        command.Parameters.AddWithValue("$attempt", reference.AttemptId.ToString("N"));
        command.Parameters.AddWithValue("$agent", reference.Camera.AgentId);
        command.Parameters.AddWithValue("$camera", reference.Camera.CameraId);
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.UtcTicks);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var facts = new List<CalendarFact>(5);
        CenteredRunSnapshot? run = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (run is null)
            {
                var snapshot = await reader.GetFieldValueAsync<byte[]>(14, cancellationToken).ConfigureAwait(false);
                if (PayloadChecksum.ComputeSha256(snapshot) != reader.GetString(15))
                    throw new InvalidDataException("The live centered run snapshot does not match its hash.");
                run = JsonSerializer.Deserialize<CenteredRunSnapshot>(snapshot);
                if (run?.RunId != reference.RunId || run.Camera != reference.Camera)
                    throw new InvalidDataException("The live centered run snapshot differs from the reserved attempt.");
            }

            ArtifactManifestV2? manifest = null;
            Guid? capture = null;
            string? manifestHash = null;
            var attempt = new ExposureAttemptKey(reference.Camera, reference.RunId, reader.GetInt64(10),
                Guid.ParseExact(reader.GetString(9), "N"));
            if (!await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false))
            {
                capture = Guid.ParseExact(reader.GetString(11), "N");
                var bytes = await reader.GetFieldValueAsync<byte[]>(12, cancellationToken).ConfigureAwait(false);
                manifestHash = reader.GetString(13);
                if (PayloadChecksum.ComputeSha256(bytes) != manifestHash)
                    throw new InvalidDataException("The centered raw manifest differs from its journal hash.");
                manifest = CaptureContractJson.ParseManifest(bytes).Document?.Manifest;
                if (manifest?.CenteredExposure is not { } binding || binding.Source != attempt ||
                    manifest.Descriptor.Capture.CaptureId != capture || binding.FrozenRunSha256 != reader.GetString(15))
                    throw new InvalidDataException("The centered raw manifest differs from its reserved attempt.");
            }

            var expiry = !await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) && reader.GetInt64(7) <= cutoffUtc.UtcTicks
                ? At(reader, 7) : (DateTimeOffset?)null;
            if (expiry is not null && (await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ||
                string.IsNullOrWhiteSpace(reader.GetString(8))))
                throw new InvalidDataException("An expired centered attempt has no durable reason.");
            var ordinal = reader.GetInt64(18);
            var dispatch = CenteredWindowRuntimeSchedule.ReadDispatchCalendar(
                await reader.IsDBNullAsync(20, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(20), ordinal);
            // The successor outside the window supplies skip evidence only.
            if (reader.GetInt32(0) > CenteredWindowScheduler.CenterIndex && dispatch is null)
                continue;
            facts.Add(new(reader.GetInt32(0), ordinal, reader.GetInt64(19), At(reader, 1), TimeSpan.FromTicks(reader.GetInt64(2)),
                TimeSpan.FromTicks(reader.GetInt64(3)), reader.GetString(4), At(reader, 5),
                capture is null ? null : At(reader, 6), expiry, expiry is null ? null : reader.GetString(8),
                attempt, capture, manifest, manifestHash, run, reader.GetBoolean(16),
                await reader.IsDBNullAsync(17, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(17), dispatch));
        }
        return facts;
    }

    private static DateTimeOffset At(SqliteDataReader reader, int column) => new(reader.GetInt64(column), TimeSpan.Zero);

    private sealed record CalendarFact(int Offset, long Slot, long CenterSlot, DateTimeOffset Start, TimeSpan Interval,
        TimeSpan Integration,
        string Cadence, DateTimeOffset Dispatched, DateTimeOffset? Admitted, DateTimeOffset? Expired, string? ExpiryReason,
        ExposureAttemptKey Attempt, Guid? CaptureId, ArtifactManifestV2? Manifest, string? ManifestHash, CenteredRunSnapshot Run,
        bool Continuous, string? ProjectedPredecessorJson, CenteredWindowDispatchCalendar? DispatchCalendar);

    internal static async Task MarkAdmittedAsync(SqliteTransaction transaction, ExposureAttemptKey attempt,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE processing_centered_windows SET reference_capture_id = (
                SELECT capture_id FROM centered_exposure_attempts WHERE attempt_id = $attempt)
            WHERE window_sha256 = (
                SELECT window_sha256 FROM processing_centered_attempt_calendar WHERE attempt_id = $attempt)
                AND closure_json IS NULL AND reference_capture_id IS NULL;
            """;
        command.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await RefreshHoldsAsync(transaction, attempt, now, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ObserveCommittedAdmissionAsync(SqliteConnection connection, ExposureAttemptKey attempt,
        DateTimeOffset observedUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE processing_centered_attempt_calendar SET admitted_ticks = $observed
            WHERE attempt_id = $attempt AND admitted_ticks IS NULL;
            """;
        command.Parameters.AddWithValue("$observed", observedUtc.UtcTicks);
        command.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal static async Task ObserveCommittedDispatchAsync(SqliteConnection connection, ExposureAttemptKey attempt,
        DateTimeOffset observedUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE processing_centered_attempt_calendar SET dispatched_ticks = $observed
            WHERE attempt_id = $attempt AND dispatched_ticks = $unobserved;
            """;
        command.Parameters.AddWithValue("$observed", observedUtc.UtcTicks);
        command.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
        command.Parameters.AddWithValue("$unobserved", UnobservedCalendarTicks);
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal static async Task RefreshHoldsAsync(SqliteTransaction transaction, ExposureAttemptKey attempt,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RefreshHoldsSql;
        command.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        var candidates = await ReadHoldCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
            await HoldAsync(transaction, candidate, now, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> RetryOpenHoldsAsync(SqliteTransaction transaction, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH pending AS MATERIALIZED (
                SELECT window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks, checkpoint_json
                FROM processing_centered_windows
                WHERE terminal = 0 AND closure_json IS NULL AND deadline_utc_ticks >= $now
                ORDER BY deadline_utc_ticks, window_sha256 LIMIT $limit
            )
            SELECT window.window_sha256, raw.capture_id, raw.raw_artifact_id, raw.payload_length,
                window.registration_deadline_utc_ticks
            FROM pending window
            JOIN processing_centered_attempt_calendar observer ON observer.attempt_id = REPLACE(
                CASE WHEN json_valid(window.checkpoint_json)
                     THEN json_extract(window.checkpoint_json, '$.referenceAttempt.attemptId') END, '-', '')
                AND observer.window_sha256 = window.window_sha256
            JOIN processing_centered_attempt_calendar input ON input.run_id = observer.run_id
                AND input.slot BETWEEN observer.slot - 2 AND observer.slot + 2
            JOIN centered_exposure_attempts admitted ON admitted.attempt_id = input.attempt_id
            JOIN raw_captures raw ON raw.capture_id = admitted.capture_id
            WHERE (input.admitted_ticks IS NULL OR input.admitted_ticks <= window.deadline_utc_ticks)
                AND raw.state = 'committed'
                AND NOT EXISTS (
                    SELECT 1 FROM processing_centered_window_holds owner
                    JOIN centered_exposure_holds hold ON hold.owner_id = owner.owner_id
                    WHERE owner.window_sha256 = window.window_sha256 AND hold.capture_id = raw.capture_id
                      AND hold.expires_unix_ms > $now_ms)
            ORDER BY window.deadline_utc_ticks, window.window_sha256, input.slot;
            """;
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        command.Parameters.AddWithValue("$now_ms", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", CenteredWindowRuntimeSchedule.MaximumPendingWindows);
        var candidates = await ReadHoldCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
        var added = false;
        foreach (var candidate in candidates)
            added |= await HoldAsync(transaction, candidate, now, cancellationToken).ConfigureAwait(false);
        return added;
    }

    private static async Task<List<HoldCandidate>> ReadHoldCandidatesAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var candidates = new List<HoldCandidate>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            candidates.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), At(reader, 4)));
        return candidates;
    }

    private static async Task<bool> HoldAsync(SqliteTransaction transaction, HoldCandidate candidate, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var id = ProcessingIdentity.CreateArtifactId(ProcessingIdentity.ComputePayloadSha256(
            System.Text.Encoding.UTF8.GetBytes(candidate.Window + candidate.CaptureId))).ToString("N");
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM centered_exposure_holds WHERE owner_id = $owner);
            """;
        command.Parameters.AddWithValue("$owner", id);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is 1L)
            return false;

        command.CommandText = SqliteCaptureProcessingStore.CenteredInputHoldBudgetSql + ";";
        command.Parameters.AddWithValue("$capture", candidate.CaptureId);
        command.Parameters.AddWithValue("$output", "raw:" + candidate.RawId);
        const long metadata = SqliteCaptureProcessingStore.CenteredWindowMetadataReserveBytes;
        // Reserve raw, packed linear, mask and bounded per-capture science metadata.
        command.Parameters.AddWithValue("$bytes", checked(candidate.RawBytes * 3 + metadata));
        command.Parameters.AddWithValue("$metadata", metadata);
        command.Parameters.AddWithValue("$expires", candidate.Expires.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            HoldRefusals.Add(1);
            return false;
        }

        command.CommandText = """
            INSERT INTO processing_centered_window_holds(window_sha256, owner_id)
            VALUES ($window, $owner)
            ON CONFLICT(window_sha256, owner_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$window", candidate.Window);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed record HoldCandidate(string Window, string CaptureId, string RawId, long RawBytes, DateTimeOffset Expires);

    public ValueTask PinClosedInputsAsync(SqliteTransaction transaction, CenteredWindowClosure closure,
        CancellationToken cancellationToken) => ReleaseAsync(transaction, closure.Schedule.Identity,
            closure.Slots.Where(slot => slot.State == CenteredWindowSlotState.Eligible)
                .Select(slot => slot.Input!.Key.CaptureId.ToString("N")).ToArray(), cancellationToken);

    public ValueTask ReleaseWindowInputsAsync(SqliteTransaction transaction, string windowIdentity,
        CancellationToken cancellationToken) => ReleaseAsync(transaction, windowIdentity, [], cancellationToken);

    private static async ValueTask ReleaseAsync(SqliteTransaction transaction, string windowIdentity,
        string[] keep, CancellationToken cancellationToken)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM centered_exposure_holds
            WHERE owner_id IN (SELECT owner_id FROM processing_centered_window_holds WHERE window_sha256 = $window)
                AND capture_id NOT IN (SELECT value FROM json_each($keep));
            DELETE FROM processing_centered_window_holds WHERE window_sha256 = $window
                AND owner_id NOT IN (SELECT owner_id FROM centered_exposure_holds);
            """;
        command.Parameters.AddWithValue("$window", windowIdentity);
        command.Parameters.AddWithValue("$keep", JsonSerializer.Serialize(keep));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
