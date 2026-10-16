using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed partial class SqliteRawCaptureJournal
{
    // These reserved journal stages are operator dispositions, retained with the immutable capture identity.
    // Audit stages are append-only; the operation-key digest binds duplicate requests to their original result.
    private const string EvidenceAuditPrefix = "raw-evidence-release.";

    internal async Task<bool> IsEvidenceWrittenOffAsync(Guid captureId, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM raw_capture_stage_events event
                JOIN raw_captures raw ON raw.raw_capture_row_id = event.raw_capture_row_id
                WHERE raw.capture_id = $capture AND event.stage_key = 'raw-evidence-written-off');
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 0;
    }

    internal async Task<RawEvidencePage> ReadEvidencePageAsync(int pageSize, long? beforeRow, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw.raw_capture_row_id, raw.capture_id, raw.manifest_sha256, raw.state, raw.failure_reason,
                   raw.agent_id, raw.capture_sequence, raw.raw_artifact_id, raw.payload_length, raw.durable_ingress_unix_ms,
                   EXISTS (SELECT 1 FROM raw_capture_stage_events terminal
                       WHERE terminal.raw_capture_row_id = raw.raw_capture_row_id AND terminal.stage_key = 'raw-evidence-written-off'),
                   audit.state, audit.source, audit.event_unix_ms
            FROM raw_captures raw
            LEFT JOIN raw_capture_stage_events audit ON audit.rowid = (
                SELECT event.rowid FROM raw_capture_stage_events event
                WHERE event.raw_capture_row_id = raw.raw_capture_row_id AND event.stage_key GLOB 'raw-evidence-release.*'
                ORDER BY event.event_unix_ms DESC, event.rowid DESC LIMIT 1)
            WHERE (raw.state != 'committed' OR audit.rowid IS NOT NULL)
              AND ($before IS NULL OR raw.raw_capture_row_id < $before)
            ORDER BY raw.raw_capture_row_id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$before", beforeRow.HasValue ? beforeRow.Value : DBNull.Value);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var items = new List<RawEvidenceItem>();
        long? next = null;
        var hasMore = false;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (items.Count == pageSize)
            {
                hasMore = true;
                break;
            }
            next = reader.GetInt64(0);
            items.Add(new RawEvidenceItem(
                new(Guid.ParseExact(reader.GetString(1), "N"), reader.GetString(2), reader.GetString(3),
                    await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(4)),
                reader.GetString(5), reader.GetInt64(6), Guid.ParseExact(reader.GetString(7), "N"), reader.GetInt64(8),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)), reader.GetInt64(10) != 0,
                await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null : ReadEvidenceAudit(reader.GetString(11), reader.GetString(12), reader.GetInt64(13))));
        }
        return new(items, hasMore ? next : null);
    }

    private static RawEvidenceAudit ReadEvidenceAudit(string state, string actor, long occurred)
    {
        var separator = state.IndexOf(':', StringComparison.Ordinal);
        return new(Enum.Parse<RawEvidenceReleaseAction>(state[..separator]), actor, state[(separator + 1)..],
            DateTimeOffset.FromUnixTimeMilliseconds(occurred));
    }

    internal Task<RawEvidenceReleaseReceipt> ReleaseEvidenceAsync(
        RawEvidenceTarget target, RawEvidenceReleaseAction action, string operationKey,
        string actor, string reason, Func<RawIngressJournalEntry, Task> revalidate,
        CancellationToken cancellationToken)
        => TrackTransactionAsync("evidence-release", () => ReleaseEvidenceCoreAsync(
            target, action, operationKey, actor, reason, revalidate, cancellationToken));

    private async Task<RawEvidenceReleaseReceipt> ReleaseEvidenceCoreAsync(
        RawEvidenceTarget target, RawEvidenceReleaseAction action, string operationKey,
        string actor, string reason, Func<RawIngressJournalEntry, Task> revalidate,
        CancellationToken cancellationToken)
    {
        var stageKey = EvidenceAuditPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey)));
        var auditState = $"{action}:{reason}";
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT raw.capture_id, event.state, event.source, event.event_unix_ms, raw.manifest_sha256
                FROM raw_capture_stage_events event JOIN raw_captures raw ON raw.raw_capture_row_id = event.raw_capture_row_id
                WHERE event.stage_key = $stage;
                """;
            duplicate.Parameters.AddWithValue("$stage", stageKey);
            using var reader = await duplicate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) != target.CaptureId.ToString("N") || reader.GetString(1) != auditState || reader.GetString(2) != actor || reader.GetString(4) != target.ManifestSha256)
                    throw new InvalidOperationException("Raw evidence operation key conflicts with an earlier request.");
                return new(true, ReadEvidenceAudit(reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            }
        }
        RawIngressJournalEntry entry;
        using (var readEntry = connection.CreateCommand())
        {
            readEntry.Transaction = transaction;
            readEntry.CommandText = """
                SELECT agent_id, capture_sequence, capture_id, raw_artifact_id,
                       descriptor_sha256, manifest_sha256, payload_sha256, payload_length,
                       payload_relative_path, sidecar_relative_path, manifest_json,
                       exposure_started_unix_ms, durable_ingress_unix_ms, state, retention_hold, evidence_origin
                FROM raw_captures WHERE capture_id = $capture;
                """;
            readEntry.Parameters.AddWithValue("$capture", target.CaptureId.ToString("N"));
            using var reader = await readEntry.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Raw evidence capture is unavailable.");
            entry = ReadEntry(reader);
        }
        if (entry.State is not ("quarantined" or "missing_evidence") || entry.State != target.State ||
            entry.ManifestSha256 != target.ManifestSha256 || await IsEvidenceWrittenOffAsync(target.CaptureId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Raw evidence quarantine changed.");
        long rowId;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT raw_capture_row_id FROM raw_captures raw
                WHERE capture_id = $capture AND failure_reason = $failure
                  AND NOT EXISTS (SELECT 1 FROM capture_lane_work work
                      WHERE work.raw_capture_row_id = raw.raw_capture_row_id AND work.state = 'leased');
                """;
            read.Parameters.AddWithValue("$capture", target.CaptureId.ToString("N"));
            read.Parameters.AddWithValue("$failure", target.FailureReason);
            var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Raw evidence changed or still has a leased lane.");
            rowId = (long)value;
        }
        if (action == RawEvidenceReleaseAction.Revalidate)
            await revalidate(entry).ConfigureAwait(false);
        var occurred = DateTimeOffset.FromUnixTimeMilliseconds(_utcNow().ToUnixTimeMilliseconds());
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raw_capture_stage_events(raw_capture_row_id, stage_key, state, source, event_unix_ms)
            VALUES ($raw, $stage, $state, $actor, $now);
            """;
        command.Parameters.AddWithValue("$raw", rowId);
        command.Parameters.AddWithValue("$stage", stageKey);
        command.Parameters.AddWithValue("$state", auditState);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$now", occurred.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (action == RawEvidenceReleaseAction.WriteOff)
        {
            command.CommandText = """
                INSERT INTO raw_capture_stage_events(raw_capture_row_id, stage_key, state, source, event_unix_ms)
                VALUES ($raw, 'raw-evidence-written-off', $state, $actor, $now);
                UPDATE capture_lane_work SET state = 'abandoned', failure_reason = 'raw-evidence-written-off',
                    completed_unix_ms = $now, updated_unix_ms = $now
                WHERE raw_capture_row_id = $raw AND state NOT IN ('completed', 'abandoned');
                UPDATE raw_captures SET retention_hold = 1 WHERE raw_capture_row_id = $raw;
                """;
        }
        else
        {
            // Standard execution failures remain terminal. Other lanes may retry the recovered immutable input;
            // their original age/deadline still applies. Unrelated quarantines never match this transition.
            command.CommandText = """
                UPDATE raw_captures SET state = 'committed', failure_reason = NULL, retention_hold = 1
                WHERE raw_capture_row_id = $raw;
                UPDATE capture_lane_work
                SET state = CASE WHEN lane_name = 'standard' THEN 'abandoned' ELSE 'pending' END,
                    failure_reason = CASE WHEN lane_name = 'standard' THEN 'raw-evidence-revalidated' ELSE NULL END,
                    attempt_count = CASE WHEN lane_name = 'standard' THEN attempt_count ELSE 0 END,
                    available_unix_ms = $now,
                    lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                    completed_unix_ms = CASE WHEN lane_name = 'standard' THEN $now ELSE NULL END,
                    updated_unix_ms = $now
                WHERE raw_capture_row_id = $raw AND state = 'quarantined'
                  AND failure_reason IN ('evidence-unavailable', 'evidence-invalid');
                """;
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _faultInjector.Inject(RawIngressFaultPoint.BeforeJournalTransactionCommit);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(false, new(action, actor, reason, occurred));
    }
}
