using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record CenteredWindowCheckpoint(
    CenteredWindowSchedule Schedule,
    ImmutableArray<CenteredWindowSlotTime> ProjectedTimes,
    ExposureAttemptKey? ReferenceAttempt = null,
    ExposureKey? ReferenceCapture = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CenteredWindowDispatchCalendar? DispatchCalendar = null);

// The owning attempt calendar row supplies the run, dispatched slot and commit observation.
internal sealed record CenteredWindowDispatchCalendar(
    long FirstAffectedSlot, TimeSpan ForecastInterval, TimeSpan Tolerance, ImmutableArray<CenteredWindowSlotTime> Skipped, int Version = 1);

// Projection of one committed transition or break in the admitted run. A cadence
// change replaces only the slots following the pinned effective predecessor.
internal sealed record CenteredWindowCalendarChange(
    int AfterOffset,
    DateTimeOffset AnchorStartUtc,
    DateTimeOffset CommittedUtc,
    string PriorCadenceRevision,
    string? NewCadenceRevision = null,
    TimeSpan? Interval = null,
    TimeSpan? ExpectedIntegration = null,
    string? BoundaryReason = null,
    DateTimeOffset? FirstAffectedStartUtc = null,
    bool IsCommittedDispatchAdjustment = false,
    TimeSpan? Tolerance = null,
    bool? Continuous = null);

internal sealed record CenteredWindowJournalSnapshot(
    string GenerationId,
    ExposureKey? AdmittedReference,
    ImmutableArray<CenteredWindowCalendarChange> Changes,
    ImmutableArray<CenteredWindowSlotObservation> Observations,
    ImmutableArray<CenteredWindowSlotTime> ResolvedTimes = default);

internal sealed record CenteredWindowRefusal(string Identity, CameraKey Camera, Guid RunId, DateTimeOffset DeadlineUtc, string Reason);

internal sealed record CenteredWindowReadState(CenteredWindowCheckpoint? Checkpoint,
    CenteredWindowDisposition? Closed, CenteredWindowRefusal? Refusal);

internal sealed record CenteredWindowQueueEntry(long DeadlineUtcTicks, string WindowIdentity);

internal sealed record CenteredWindowPublication(
    string ClosureIdentity,
    Guid ReferenceCaptureId,
    ImmutableArray<Guid> ContributingCaptureIds,
    ImmutableArray<Guid> RegistrationArtifactIds,
    string PlanSha256,
    Guid EvidenceArtifactId,
    CenteredStackDisposition StackDisposition);

internal sealed record CenteredWindowDisposition(
    CenteredWindowClosure Closure,
    CenteredWindowPublication? Publication,
    string? FailureReason = null,
    CenteredRegistrationInputFailure? InputFailure = null)
{
    internal bool IsTerminal => !Closure.CanRegister || Publication is not null || FailureReason is not null;
}

// The concrete implementation reads #1919's journal and uses its retention
// authority in the same transaction as the corresponding window mutation.
internal interface ICenteredWindowJournal
{
    ValueTask<CenteredWindowJournalSnapshot> ReadWindowEvidenceAsync(
        SqliteTransaction transaction,
        CenteredWindowCheckpoint checkpoint,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken);

    ValueTask PinClosedInputsAsync(
        SqliteTransaction transaction,
        CenteredWindowClosure closure,
        CancellationToken cancellationToken);

    ValueTask ReleaseWindowInputsAsync(
        SqliteTransaction transaction,
        string windowIdentity,
        CancellationToken cancellationToken);
}

internal sealed partial class SqliteCaptureProcessingStore
{
    private const string EnqueueTerminalWindowWorkSql = """
        INSERT OR IGNORE INTO processing_centered_view_window_work(window_sha256, retry_utc)
        SELECT window_sha256, 0 FROM processing_centered_windows
        WHERE window_sha256 = $window AND terminal = 1 AND closure_json IS NOT NULL AND reference_capture_id IS NOT NULL;
        """;

    private const string InvalidCheckpoint = "invalid-window-checkpoint";
    private const string CapacityExhausted = "window-capacity-exhausted";
    private const string RegistrationDeadlineExpired = "registration-deadline-expired";
    private const string RetainedInputUnreadable = "registration.retained-input-unreadable";
    private const string CenteredRegistrationStateSchemaSql = """
        CREATE INDEX ix_processing_centered_windows_publication_retention
            ON processing_centered_windows(registration_deadline_utc_ticks)
            WHERE terminal = 1 AND publication_json IS NOT NULL;
        CREATE TABLE processing_centered_registration_input_failures(
            window_sha256 TEXT PRIMARY KEY REFERENCES processing_centered_windows(window_sha256),
            input_failure_json BLOB NOT NULL
        ) STRICT;
        """;

    private const string CenteredWindowSchemaSql = """
        CREATE TABLE processing_centered_windows(
            window_sha256 TEXT PRIMARY KEY CHECK(length(window_sha256) = 64),
            deadline_utc_ticks INTEGER NOT NULL,
            registration_deadline_utc_ticks INTEGER NOT NULL CHECK(registration_deadline_utc_ticks > deadline_utc_ticks),
            checkpoint_json BLOB NOT NULL,
            reference_capture_id TEXT CHECK(reference_capture_id IS NULL OR length(reference_capture_id) = 32),
            closure_json BLOB,
            publication_json BLOB,
            failure_reason TEXT CHECK(failure_reason IS NULL OR length(failure_reason) BETWEEN 1 AND 128),
            terminal INTEGER NOT NULL DEFAULT 0 CHECK(terminal IN (0, 1)),
            CHECK(terminal = 0 OR closure_json IS NOT NULL OR failure_reason IN ('invalid-window-checkpoint','window-capacity-exhausted')),
            CHECK(failure_reason IS NULL OR (terminal = 1 AND publication_json IS NULL)),
            CHECK(publication_json IS NULL OR (closure_json IS NOT NULL AND terminal = 1))
        ) STRICT;
        CREATE INDEX ix_processing_centered_windows_pending
            ON processing_centered_windows(deadline_utc_ticks, window_sha256)
            WHERE terminal = 0;
        CREATE UNIQUE INDEX ix_processing_centered_windows_reference
            ON processing_centered_windows(reference_capture_id) WHERE reference_capture_id IS NOT NULL;
        CREATE TABLE processing_centered_attempt_calendar(
            attempt_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL,
            slot INTEGER NOT NULL CHECK(slot > 0),
            start_ticks INTEGER NOT NULL,
            interval_ticks INTEGER NOT NULL CHECK(interval_ticks > 0),
            integration_ticks INTEGER NOT NULL CHECK(integration_ticks >= 0),
            cadence TEXT NOT NULL,
            dispatched_ticks INTEGER NOT NULL,
            admitted_ticks INTEGER,
            expired_ticks INTEGER,
            expired_reason TEXT,
            window_sha256 TEXT REFERENCES processing_centered_windows(window_sha256),
            UNIQUE(run_id, slot)
        ) STRICT;
        CREATE TABLE processing_centered_product_clock(
            revision INTEGER PRIMARY KEY,
            settled_ticks INTEGER
        ) STRICT;
        CREATE INDEX ix_processing_centered_product_clock_pending
            ON processing_centered_product_clock(revision) WHERE settled_ticks IS NULL;
        CREATE TABLE processing_centered_window_holds(
            window_sha256 TEXT NOT NULL REFERENCES processing_centered_windows(window_sha256),
            owner_id TEXT NOT NULL,
            PRIMARY KEY(window_sha256, owner_id)
        ) STRICT;
        """ + "\n" + CenteredAvailabilitySchemaSql;

    private static async ValueTask AddCenteredWindowSchemaAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CenteredWindowSchemaSql + "\n" + CenteredRegistrationStateSchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddCenteredRegistrationStateSchemaAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CenteredRegistrationStateSchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Can also be called as part of a journal transaction by the admission
    // adapter. An existing window is an immutable checkpoint, even after close.
    internal static async ValueTask<bool> TrackCenteredWindowAsync(
        SqliteTransaction transaction, CenteredWindowCheckpoint checkpoint, int maximumPendingCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPendingCount);
        var schedule = checkpoint.Schedule;
        _ = CenteredWindowScheduler.TryClose(schedule, checkpoint.ProjectedTimes, [], schedule.ReferenceStartUtc);
        if ((checkpoint.ReferenceAttempt is { } attempt &&
                (attempt.Camera != schedule.Camera || attempt.RunId != schedule.RunId || attempt.AttemptId == Guid.Empty)) ||
            (checkpoint.ReferenceCapture is { } reference &&
                (reference.Camera != schedule.Camera || reference.RunId != schedule.RunId || reference.CaptureId == Guid.Empty)))
            throw new InvalidDataException("The reserved centered reference does not belong to its run.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, SerializerOptions);
        using var command = CreateCenteredCommand(transaction, schedule.Identity);
        command.CommandText = """
            INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks, checkpoint_json)
            SELECT $window, $deadline, $registrationDeadline, $checkpoint
            WHERE (SELECT COUNT(*) FROM (SELECT 1 FROM processing_centered_windows WHERE terminal = 0 LIMIT $maximum)) < $maximum
            ON CONFLICT(window_sha256) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$maximum", maximumPendingCount);
        command.Parameters.AddWithValue("$deadline", schedule.DeadlineUtc.UtcTicks);
        command.Parameters.AddWithValue("$registrationDeadline", schedule.RegistrationDeadlineUtc.UtcTicks);
        command.Parameters.AddWithValue("$checkpoint", bytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = "SELECT checkpoint_json FROM processing_centered_windows WHERE window_sha256 = $window;";
        var retained = (byte[]?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (retained is null)
            return false;
        if (!bytes.AsSpan().SequenceEqual(retained))
            throw new InvalidDataException("A centered window was already tracked with different scheduling facts.");
        return true;
    }

    internal static async Task RecordCenteredCapacityRefusalAsync(SqliteTransaction transaction,
        CenteredWindowCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var schedule = checkpoint.Schedule;
        using var command = CreateCenteredCommand(transaction, schedule.Identity);
        command.CommandText = """
            INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks,
                registration_deadline_utc_ticks, checkpoint_json, failure_reason, terminal)
            VALUES($window, $deadline, $registrationDeadline, $checkpoint, $reason, 1)
            ON CONFLICT(window_sha256) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$deadline", schedule.DeadlineUtc.UtcTicks);
        command.Parameters.AddWithValue("$registrationDeadline", schedule.RegistrationDeadlineUtc.UtcTicks);
        command.Parameters.AddWithValue("$checkpoint", JsonSerializer.SerializeToUtf8Bytes(checkpoint, SerializerOptions));
        command.Parameters.AddWithValue("$reason", CapacityExhausted);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RecordCenteredMissedReferenceAsync(SqliteTransaction transaction,
        CenteredWindowSchedule schedule, ImmutableArray<CenteredWindowSlotTime> slots, DateTimeOffset observedUtc,
        CancellationToken cancellationToken)
    {
        var cutoff = observedUtc < schedule.DeadlineUtc ? observedUtc : schedule.DeadlineUtc;
        var decisions = slots.Select((time, index) => index == CenteredWindowScheduler.CenterIndex
            ? new CenteredWindowClosedSlot(0, time, CenteredWindowSlotState.MissingExposure, "nominal-slot-missed", null, null)
            : new CenteredWindowClosedSlot(index - CenteredWindowScheduler.CenterIndex, time, CenteredWindowSlotState.Rejected,
                "registration.reference-input-unavailable", null, null)).ToImmutableArray();
        var closure = new CenteredWindowClosure(schedule, cutoff, decisions);
        using var command = CreateCenteredCommand(transaction, schedule.Identity);
        command.CommandText = """
            INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks,
                registration_deadline_utc_ticks, checkpoint_json, closure_json, terminal)
            VALUES($window, $deadline, $registrationDeadline, $checkpoint, $closure, 1)
            ON CONFLICT(window_sha256) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$deadline", schedule.DeadlineUtc.UtcTicks);
        command.Parameters.AddWithValue("$registrationDeadline", schedule.RegistrationDeadlineUtc.UtcTicks);
        command.Parameters.AddWithValue("$checkpoint", JsonSerializer.SerializeToUtf8Bytes(new CenteredWindowCheckpoint(schedule, slots), SerializerOptions));
        command.Parameters.AddWithValue("$closure", JsonSerializer.SerializeToUtf8Bytes(closure, SerializerOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<CenteredWindowDisposition?> TryCloseCenteredWindowAsync(
        string identity, ICenteredWindowJournal journal, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        (CenteredWindowCheckpoint Checkpoint, CenteredWindowDisposition? Disposition) row;
        try
        {
            row = await ReadCenteredWindowAsync(transaction, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or FormatException)
        {
            await SettleInvalidCenteredCheckpointAsync(transaction, identity, journal, cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException("The centered window checkpoint was terminalized as invalid.", exception);
        }
        var now = _timeProvider.GetUtcNow();
        if (row.Disposition is { } previous)
        {
            return !previous.IsTerminal && now >= previous.Closure.Schedule.RegistrationDeadlineUtc
                ? await SettleCenteredRegistrationFailureAsync(transaction, previous, RegistrationDeadlineExpired,
                    journal, cancellationToken).ConfigureAwait(false)
                : previous;
        }

        var deadline = row.Checkpoint.Schedule.DeadlineUtc;
        CenteredWindowClosure? closure;
        string? failure = null;
        try
        {
            var evidence = await journal.ReadWindowEvidenceAsync(transaction, row.Checkpoint, now < deadline ? now : deadline,
                cancellationToken).ConfigureAwait(false);
            closure = CenteredWindowScheduler.TryClose(row.Checkpoint, evidence, now);
        }
        catch (Exception exception) when (now >= deadline && exception is (InvalidDataException or JsonException or ArgumentException or FormatException))
        {
            failure = "invalid-window-evidence";
            closure = new(row.Checkpoint.Schedule, deadline, row.Checkpoint.ProjectedTimes.Select((time, offset) =>
                new CenteredWindowClosedSlot(offset - CenteredWindowScheduler.CenterIndex, time,
                    CenteredWindowSlotState.InvalidEvidence, failure, null, null)).ToImmutableArray());
        }
        if (closure is null)
            return null;

        if (closure.CanRegister && now >= closure.Schedule.RegistrationDeadlineUtc)
            failure = RegistrationDeadlineExpired;

        if (closure.CanRegister && failure is null)
            await journal.PinClosedInputsAsync(transaction, closure, cancellationToken).ConfigureAwait(false);
        else
            await journal.ReleaseWindowInputsAsync(transaction, identity, cancellationToken).ConfigureAwait(false);

        using var command = CreateCenteredCommand(transaction, identity);
        command.CommandText = """
            UPDATE processing_centered_windows
            SET closure_json = $closure, reference_capture_id = COALESCE(reference_capture_id, $reference),
                terminal = $terminal, failure_reason = $failure
            WHERE window_sha256 = $window AND closure_json IS NULL;
            """ + "\n" + EnqueueTerminalWindowWorkSql;
        command.Parameters.AddWithValue("$closure", JsonSerializer.SerializeToUtf8Bytes(closure, SerializerOptions));
        command.Parameters.AddWithValue("$reference", (object?)closure.Center.Input?.Key.CaptureId.ToString("N") ?? DBNull.Value);
        command.Parameters.AddWithValue("$terminal", closure.CanRegister && failure is null ? 0 : 1);
        command.Parameters.AddWithValue("$failure", (object?)failure ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _faultInjector.Inject(CaptureProcessingFaultPoint.BeforeCenteredWindowCloseCommit, identity);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(closure, null, failure);
    }

    private static async Task SettleInvalidCenteredCheckpointAsync(SqliteTransaction transaction, string identity,
        ICenteredWindowJournal journal, CancellationToken cancellationToken)
    {
        using var command = CreateCenteredCommand(transaction, identity);
        command.CommandText = """
            DELETE FROM processing_centered_registration_input_failures WHERE window_sha256 = $window
                AND EXISTS (SELECT 1 FROM processing_centered_windows WHERE window_sha256 = $window AND terminal = 0);
            UPDATE processing_centered_windows SET closure_json = NULL, failure_reason = $failure, terminal = 1
            WHERE window_sha256 = $window AND terminal = 0;
            """;
        command.Parameters.AddWithValue("$failure", InvalidCheckpoint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await journal.ReleaseWindowInputsAsync(transaction, identity, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<CenteredWindowDisposition> FailCenteredRegistrationAsync(
        CenteredWindowClosure closure, string reason, ICenteredWindowJournal journal, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var row = await ReadCenteredWindowAsync(transaction, closure.Schedule.Identity, cancellationToken).ConfigureAwait(false);
        if (row.Disposition is not { } current || current.Closure.Identity != closure.Identity)
            throw new InvalidDataException("Registration failure does not match the frozen centered window.");

        if (current.IsTerminal)
            return current;

        return await SettleCenteredRegistrationFailureAsync(transaction, current, reason, journal, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> UpdateCenteredRegistrationInputFailureAsync(CenteredWindowClosure closure,
        CenteredRegistrationInputFailure? failure, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$window", closure.Schedule.Identity);
        if (failure is null)
        {
            command.CommandText = """
                DELETE FROM processing_centered_registration_input_failures WHERE window_sha256 = $window
                    AND EXISTS (SELECT 1 FROM processing_centered_windows WHERE window_sha256 = $window AND terminal = 0);
                """;
        }
        else
        {
            command.CommandText = """
                INSERT INTO processing_centered_registration_input_failures(window_sha256, input_failure_json)
                SELECT window_sha256, $failure FROM processing_centered_windows
                WHERE window_sha256 = $window AND terminal = 0 AND closure_json IS NOT NULL
                ON CONFLICT(window_sha256) DO UPDATE SET input_failure_json = excluded.input_failure_json
                WHERE input_failure_json <> excluded.input_failure_json;
                """;
            command.Parameters.AddWithValue("$failure", JsonSerializer.SerializeToUtf8Bytes(failure, SerializerOptions));
        }
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static async ValueTask<CenteredWindowDisposition> SettleCenteredRegistrationFailureAsync(
        SqliteTransaction transaction, CenteredWindowDisposition current, string reason, ICenteredWindowJournal journal,
        CancellationToken cancellationToken)
    {
        var closure = current.Closure;
        var inputFailure = reason == RegistrationDeadlineExpired ? current.InputFailure : null;
        if (inputFailure is not null)
            reason = RetainedInputUnreadable;
        using var command = CreateCenteredCommand(transaction, closure.Schedule.Identity);
        command.CommandText = """
            UPDATE processing_centered_windows SET failure_reason = $reason, terminal = 1
            WHERE window_sha256 = $window AND terminal = 0;
            DELETE FROM processing_centered_registration_input_failures
            WHERE window_sha256 = $window AND $retain_input_failure = 0;
            """ + "\n" + EnqueueTerminalWindowWorkSql;
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$retain_input_failure", inputFailure is not null);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await journal.ReleaseWindowInputsAsync(transaction, closure.Schedule.Identity, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(closure, null, reason, inputFailure);
    }

    internal async ValueTask<CenteredWindowDisposition> PublishCenteredWindowAsync(
        CenteredWindowRegistrationResult result,
        IReadOnlyList<DurableProcessingOutput> outputs,
        ICenteredWindowJournal journal,
        CancellationToken cancellationToken)
    {
        if (result.RecomputationId is not null)
            throw new InvalidOperationException("Archive recomputation cannot publish a live centered window.");
        var identity = result.Closure.Schedule.Identity;
        var publication = result.Publication;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var row = await ReadCenteredWindowAsync(transaction, identity, cancellationToken).ConfigureAwait(false);
        if (row.Disposition?.Closure is not { CanRegister: true } closure)
            throw new InvalidOperationException("A centered result requires a frozen window with a usable reference.");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(publication, SerializerOptions);
        if (row.Disposition.Publication is not null)
        {
            if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(row.Disposition.Publication, SerializerOptions)))
                throw new InvalidDataException("The centered window already has a different live publication.");
            return new(closure, row.Disposition.Publication);
        }

        if (row.Disposition.IsTerminal)
            return row.Disposition;
        if (_timeProvider.GetUtcNow() >= closure.Schedule.RegistrationDeadlineUtc)
            return await SettleCenteredRegistrationFailureAsync(transaction, row.Disposition, RegistrationDeadlineExpired,
                journal, cancellationToken).ConfigureAwait(false);
        if (closure.Identity != result.Closure.Identity ||
            !outputs.Select(output => output.ArtifactId).SequenceEqual(publication.RegistrationArtifactIds))
            throw new InvalidDataException("Registration products do not belong to the frozen live window.");

        for (var index = 0; index < outputs.Count; index++)
        {
            var output = outputs[index];
            var product = result.Products[index];
            if (output.Capture.CaptureId != publication.ReferenceCaptureId ||
                output.OutputIdentitySha256 != product.OutputIdentitySha256 ||
                output.Artifact.ChecksumSha256 != product.ChecksumSha256 ||
                !output.Artifact.SourceArtifactIds.SequenceEqual(product.SourceArtifactIds))
                throw new InvalidDataException("Materialized registration artifacts differ from the verified live output.");

            await InsertOutputAsync(connection, transaction, publication.ReferenceCaptureId,
                CenteredWindowCoordinator.RegistrationProducer, output, allowProducerAlias: false, cancellationToken)
                .ConfigureAwait(false);
        }
        using var command = CreateCenteredCommand(transaction, identity);
        command.CommandText = """
            UPDATE processing_centered_windows SET publication_json = $publication, terminal = 1
            WHERE window_sha256 = $window AND terminal = 0;
            DELETE FROM processing_centered_registration_input_failures WHERE window_sha256 = $window;
            """ + "\n" + EnqueueTerminalWindowWorkSql;
        command.Parameters.AddWithValue("$publication", bytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await journal.ReleaseWindowInputsAsync(transaction, identity, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(closure, publication);
    }

    internal async ValueTask<IReadOnlyList<string>> ReadOpenCenteredWindowIdsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT window_sha256 FROM processing_centered_windows
            WHERE terminal = 0 AND closure_json IS NULL
            ORDER BY deadline_utc_ticks, window_sha256 LIMIT $count;
            """;
        command.Parameters.AddWithValue("$count", CenteredWindowRuntimeSchedule.MaximumPendingWindows);
        var entries = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            entries.Add(reader.GetString(0));
        return entries;
    }

    internal ValueTask<IReadOnlyList<CenteredWindowQueueEntry>> ReadDueCenteredWindowsAsync(
        CenteredWindowQueueEntry? after, int maximumCount, CancellationToken cancellationToken) =>
        ReadCenteredWindowQueueAsync(after, maximumCount, readyToRegister: false, cancellationToken);

    internal ValueTask<IReadOnlyList<CenteredWindowQueueEntry>> ReadPendingCenteredRegistrationsAsync(
        CenteredWindowQueueEntry? after, int maximumCount, CancellationToken cancellationToken) =>
        ReadCenteredWindowQueueAsync(after, maximumCount, readyToRegister: true, cancellationToken);

    private async ValueTask<IReadOnlyList<CenteredWindowQueueEntry>> ReadCenteredWindowQueueAsync(
        CenteredWindowQueueEntry? after, int maximumCount, bool readyToRegister, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT deadline_utc_ticks, window_sha256 FROM processing_centered_windows
            WHERE terminal = 0 AND (deadline_utc_ticks, window_sha256) > ($afterDeadline, $afterIdentity)
                AND (($ready = 1 AND closure_json IS NOT NULL AND registration_deadline_utc_ticks > $now)
                    OR ($ready = 0 AND ((closure_json IS NULL AND deadline_utc_ticks <= $now)
                        OR (closure_json IS NOT NULL AND registration_deadline_utc_ticks <= $now))))
            ORDER BY deadline_utc_ticks, window_sha256 LIMIT $count;
            """;
        command.Parameters.AddWithValue("$ready", readyToRegister ? 1 : 0);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().UtcTicks);
        command.Parameters.AddWithValue("$afterDeadline", after?.DeadlineUtcTicks ?? 0L);
        command.Parameters.AddWithValue("$afterIdentity", after?.WindowIdentity ?? string.Empty);
        command.Parameters.AddWithValue("$count", maximumCount);
        var entries = new List<CenteredWindowQueueEntry>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            entries.Add(new(reader.GetInt64(0), reader.GetString(1)));

        return entries;
    }

    internal async ValueTask<CenteredWindowDisposition?> ReadCenteredWindowDispositionAsync(
        string identity, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes deferred transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: true);
#pragma warning restore CA1849
        var row = await ReadCenteredWindowAsync(transaction, identity, cancellationToken).ConfigureAwait(false);
        return row.Disposition;
    }

    internal async ValueTask<CenteredWindowDisposition?> ReadCenteredWindowByReferenceAsync(
        Guid referenceCaptureId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT window.closure_json, window.publication_json, window.failure_reason, failure.input_failure_json
            FROM processing_centered_windows window
            LEFT JOIN processing_centered_registration_input_failures failure ON failure.window_sha256 = window.window_sha256
            WHERE window.reference_capture_id = $reference;
            """;
        command.Parameters.AddWithValue("$reference", referenceCaptureId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var closure = ReadCenteredClosure(reader, 0);
        if (closure is null)
            return null;
        if (closure.Center.Input is { } admitted && admitted.Key.CaptureId != referenceCaptureId)
            throw new InvalidDataException("The retained centered window does not match its reference capture.");

        return new(closure, ReadCenteredJson<CenteredWindowPublication>(reader, 1), ReadCenteredFailure(reader, 2),
            ReadCenteredJson<CenteredRegistrationInputFailure>(reader, 3));
    }

    internal async ValueTask<CenteredWindowReadState?> ReadCenteredWindowStateAsync(
        Guid referenceCaptureId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes deferred transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: true);
#pragma warning restore CA1849
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT window.window_sha256, window.deadline_utc_ticks, window.failure_reason,
                run.agent_id, run.camera_id, run.run_id
            FROM processing_centered_windows window
            LEFT JOIN processing_centered_attempt_calendar calendar ON calendar.window_sha256 = window.window_sha256
            LEFT JOIN centered_exposure_runs run ON run.run_id = calendar.run_id
            WHERE window.reference_capture_id = $reference;
            """;
        command.Parameters.AddWithValue("$reference", referenceCaptureId.ToString("N"));
        string? identity = null;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                identity = reader.GetString(0);
                var reason = ReadCenteredFailure(reader, 2);
                if (reason is InvalidCheckpoint or CapacityExhausted)
                {
                    return new(null, null, new(identity, new(reader.GetString(3), reader.GetString(4)),
                        Guid.ParseExact(reader.GetString(5), "N"), new(reader.GetInt64(1), TimeSpan.Zero), reason));
                }
            }
        }
        if (identity is null)
            return await ReadLateCenteredWindowAsync(transaction, command, referenceCaptureId, cancellationToken)
                .ConfigureAwait(false);
        var row = await ReadCenteredWindowAsync(transaction, identity, cancellationToken).ConfigureAwait(false);
        return new(row.Checkpoint, row.Disposition, null);
    }

    private static async ValueTask<CenteredWindowReadState?> ReadLateCenteredWindowAsync(
        SqliteTransaction transaction, SqliteCommand command, Guid referenceCaptureId, CancellationToken cancellationToken)
    {
        // This link is for lookup only. The frozen reference stays absent and
        // downstream window publication continues using its original reference column.
        command.CommandText = """
            SELECT window.window_sha256, attempt.attempt_id, attempt.ordinal, run.run_id, run.agent_id, run.camera_id,
                raw.manifest_json, raw.manifest_sha256, run.snapshot_json, run.snapshot_sha256
            FROM centered_exposure_attempts attempt
            JOIN processing_centered_attempt_calendar calendar ON calendar.attempt_id = attempt.attempt_id
                AND calendar.run_id = attempt.run_id
            JOIN processing_centered_windows window ON window.window_sha256 = calendar.window_sha256
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            JOIN raw_captures raw ON raw.capture_id = attempt.capture_id AND raw.agent_id = run.agent_id
            WHERE attempt.capture_id = $reference AND attempt.status = 'observed' AND raw.state = 'committed'
                AND window.terminal = 1 AND window.reference_capture_id IS NULL AND window.closure_json IS NOT NULL;
            """;
        string identity;
        ExposureAttemptKey attempt;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            identity = reader.GetString(0);
            attempt = new(new(reader.GetString(4), reader.GetString(5)), Guid.ParseExact(reader.GetString(3), "N"),
                reader.GetInt64(2), Guid.ParseExact(reader.GetString(1), "N"));
            var manifestBytes = await reader.GetFieldValueAsync<byte[]>(6, cancellationToken).ConfigureAwait(false);
            var snapshotBytes = await reader.GetFieldValueAsync<byte[]>(8, cancellationToken).ConfigureAwait(false);
            var manifest = CaptureContractJson.ParseManifest(manifestBytes).Document?.Manifest;
            var snapshotHash = reader.GetString(9);
            if (PayloadChecksum.ComputeSha256(manifestBytes) != reader.GetString(7) ||
                PayloadChecksum.ComputeSha256(snapshotBytes) != snapshotHash ||
                manifest?.CenteredExposure is not { } binding || binding.Source != attempt ||
                binding.FrozenRunSha256 != snapshotHash || manifest.Descriptor.Capture.CaptureId != referenceCaptureId)
                throw new InvalidDataException("The late centered capture differs from its reserved attempt.");
            var run = JsonSerializer.Deserialize<CenteredRunSnapshot>(snapshotBytes);
            if (run?.RunId != attempt.RunId || run.Camera != attempt.Camera ||
                run.GraphRevisionId != manifest.CaptureGraphRevisionId)
                throw new InvalidDataException("The late centered capture differs from its frozen run.");
        }

        var row = await ReadCenteredWindowAsync(transaction, identity, cancellationToken).ConfigureAwait(false);
        if (row.Checkpoint.ReferenceAttempt != attempt)
            throw new InvalidDataException("The late centered capture does not match the window's reserved attempt.");
        return row.Disposition is
        {
            IsTerminal: true, Publication: null,
            Closure.Center: { State: CenteredWindowSlotState.MissingExposure, Input: null }
        }
            ? new(row.Checkpoint, row.Disposition, null) : null;
    }

    private static async ValueTask<(CenteredWindowCheckpoint Checkpoint, CenteredWindowDisposition? Disposition)>
        ReadCenteredWindowAsync(SqliteTransaction transaction, string identity, CancellationToken cancellationToken)
    {
        using var command = CreateCenteredCommand(transaction, identity);
        command.CommandText = """
            SELECT window.checkpoint_json, window.closure_json, window.publication_json, window.failure_reason, failure.input_failure_json
            FROM processing_centered_windows window
            LEFT JOIN processing_centered_registration_input_failures failure ON failure.window_sha256 = window.window_sha256
            WHERE window.window_sha256 = $window;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("No centered window is tracked with that identity.");

        var checkpoint = ReadCenteredJson<CenteredWindowCheckpoint>(reader, 0);
        var closure = ReadCenteredClosure(reader, 1);
        if (checkpoint?.Schedule is not { Camera: not null } schedule || schedule.Identity != identity ||
            checkpoint.ProjectedTimes.IsDefaultOrEmpty || checkpoint.ProjectedTimes.Length != CenteredWindowScheduler.SlotCount ||
            checkpoint.ProjectedTimes.Any(time => time is null) ||
            (closure is not null && (closure.Schedule != schedule || closure.Slots.IsDefaultOrEmpty ||
                closure.Slots.Length != CenteredWindowScheduler.SlotCount || closure.Slots.Any(slot => slot is null))))
            throw new InvalidDataException("The retained centered window does not match its scheduling identity.");

        return (checkpoint, closure is null ? null :
            new(closure, ReadCenteredJson<CenteredWindowPublication>(reader, 2), ReadCenteredFailure(reader, 3),
                ReadCenteredJson<CenteredRegistrationInputFailure>(reader, 4)));
    }

    private static string? ReadCenteredFailure(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static CenteredWindowClosure? ReadCenteredClosure(SqliteDataReader reader, int ordinal)
    {
        var closure = ReadCenteredJson<CenteredWindowClosure>(reader, ordinal);
        if (closure is null)
            return null;
        if (closure.Slots.IsDefaultOrEmpty || closure.Slots.Length != CenteredWindowScheduler.SlotCount ||
            closure.Slots.Any(slot => slot is null))
            throw new InvalidDataException("The retained centered closure does not contain five slots.");
        if (!closure.Slots.Any(slot => slot.Products?.RegistrationPin is
        { Raw.EnvelopeSha256: null } or { LinearLayout: null }))
            return closure;

        return CenteredWindowHistoricalClosure.RestoreIdentity(closure, reader.GetFieldValue<byte[]>(ordinal));
    }

    private static T? ReadCenteredJson<T>(SqliteDataReader reader, int ordinal) where T : class =>
        reader.IsDBNull(ordinal) ? null :
        JsonSerializer.Deserialize<T>(reader.GetFieldValue<byte[]>(ordinal), SerializerOptions)
            ?? throw new InvalidDataException("The retained centered window contains a null document.");

    private static SqliteCommand CreateCenteredCommand(SqliteTransaction transaction, string identity)
    {
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$window", identity);
        return command;
    }

}
