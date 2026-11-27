using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using static HVO.SkyMonitor.CameraAgent.Common.Silo.SiloSqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloArchiveWork(SiloPublicationRef Publication, int Attempts);
internal sealed record SiloArchiveBacklog(long Count, long Bytes, DateTimeOffset? Oldest,
    CameraAgentSiloArchiveReceipt? Cutoff, CameraAgentSiloArchiveReceipt? Latest, long Blocked, long Failed);

/// <summary>Restartable work and reporting projection. The copy records in the journal are the authority.</summary>
internal sealed class SiloArchiveIndex(string path, SiloArchiveMarker marker)
{
    private static readonly SiloSqliteFormat Format = new("hvo.cameraagent.silo.archive-index/1", 0x48534149, 1, """
        CREATE TABLE silo_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
        CREATE TABLE archive_work(
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            group_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            created_ticks INTEGER NOT NULL,
            bytes INTEGER NOT NULL,
            state TEXT NOT NULL DEFAULT 'pending',
            reason TEXT,
            attempts INTEGER NOT NULL DEFAULT 0,
            next_ticks INTEGER NOT NULL DEFAULT 0,
            receipt_generation INTEGER,
            receipt_sequence INTEGER,
            receipt_version TEXT,
            receipt_sha256 TEXT,
            verified_ticks INTEGER,
            PRIMARY KEY(generation, sequence, record_index),
            UNIQUE(group_id, revision_id)
        ) STRICT;
        CREATE INDEX archive_due ON archive_work(next_ticks, generation, sequence, record_index) WHERE state <> 'policy-expired';
        CREATE INDEX archive_backlog ON archive_work(created_ticks, bytes, state) WHERE state NOT IN ('reachable', 'policy-expired');
        CREATE TABLE archive_prefix(
            row_id INTEGER PRIMARY KEY CHECK(row_id = 1),
            sequence INTEGER NOT NULL,
            sha256 TEXT
        ) STRICT;
        INSERT INTO archive_prefix VALUES(1, 0, NULL);
        """, "Silo archive work index");

    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        RefuseUnsupportedHeader(path, Format);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sourceBinding"] = marker.SourceBindingId,
            ["store"] = marker.StoreId,
            ["bucket"] = marker.Bucket
        };
        if (new FileInfo(path + "-wal") is { Exists: true, Length: > 0 })
            await InspectAsync(path, Format, metadata, Path.GetDirectoryName(path)!, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, Format, metadata, cancellationToken).ConfigureAwait(false);
    }

    internal bool Matches(SiloArchiveMarker archive) => marker == archive;

    internal async ValueTask DiscoverAsync(SiloCatalogIndex index, int limit, CancellationToken cancellationToken)
    {
        using var destination = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        (long Generation, long Sequence, int Index) after = (0, 0, 0);
        using (var cursor = CreateCommand(destination, null, """
            SELECT generation, sequence, record_index FROM archive_work ORDER BY generation DESC, sequence DESC, record_index DESC LIMIT 1;
            """))
        using (var reader = await cursor.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                after = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2));
        }

        using var source = await index.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(source, null, """
            SELECT p.generation, p.sequence, p.record_index, p.group_id, p.revision_id, j.record_json,
              COALESCE((SELECT SUM(r.length) FROM dependencies d JOIN roles r
                  ON r.kind = 'member' AND r.id = d.artifact_id AND r.revision = d.artifact_revision
                  WHERE d.group_id = p.group_id AND d.revision_id = p.revision_id), 0)
            FROM publications p JOIN journal_records j USING(generation, sequence, record_index)
            WHERE (p.generation, p.sequence, p.record_index) > ($g, $s, $i) AND j.rejection IS NULL
            ORDER BY p.generation, p.sequence, p.record_index LIMIT $limit;
            """, ("$g", after.Generation), ("$s", after.Sequence), ("$i", after.Index), ("$limit", limit));
        using var rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(destination);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var json = JsonDocument.Parse(rows.GetString(5));
            var publish = (SiloPublishRecord)SiloJson.ReadRecord(json.RootElement, "indexed publication");
            var bytes = checked(publish.Manifest.Length + publish.Members.Sum(static member => member.Length) + rows.GetInt64(6));
            await ExecuteAsync(destination, transaction, """
                INSERT INTO archive_work(generation, sequence, record_index, group_id, revision_id, created_ticks, bytes)
                VALUES($g, $s, $i, $group, $revision, $created, $bytes);
                """, cancellationToken, ("$g", rows.GetInt64(0)), ("$s", rows.GetInt64(1)), ("$i", rows.GetInt32(2)),
                ("$group", rows.GetString(3)), ("$revision", rows.GetString(4)),
                ("$created", Ticks(publish.Manifest.Qualification.VerifiedUtc)), ("$bytes", bytes)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyList<SiloArchiveWork>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT group_id, revision_id, generation, sequence, record_index, attempts FROM archive_work
            WHERE state <> 'policy-expired' AND next_ticks <= $now
            ORDER BY next_ticks, generation, sequence, record_index LIMIT $limit;
            """, ("$now", Ticks(now)), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloArchiveWork>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(new(reader.GetString(0), reader.GetString(1), new(reader.GetInt64(2), reader.GetInt64(3)), reader.GetInt32(4)),
                reader.GetInt32(5)));
        return result;
    }

    internal async ValueTask CompleteAttemptAsync(SiloArchiveWork work, string state, string? reason, DateTimeOffset next,
        SiloCommittedSegment? receipt, DateTimeOffset verifiedUtc, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE archive_work SET state = $state, reason = $reason, next_ticks = $next, attempts = $attempts,
                receipt_generation = COALESCE($rg, receipt_generation), receipt_sequence = COALESCE($rs, receipt_sequence),
                receipt_version = COALESCE($version, receipt_version), receipt_sha256 = COALESCE($sha, receipt_sha256),
                verified_ticks = COALESCE($verified, verified_ticks)
            WHERE generation = $g AND sequence = $s AND record_index = $i;
            """, cancellationToken, ("$state", state), ("$reason", reason), ("$next", Ticks(next)),
            ("$attempts", state == "reachable" ? 0 : Math.Min(work.Attempts + 1, 16)), ("$rg", receipt?.Position.Generation),
            ("$rs", receipt?.Position.Sequence), ("$version", receipt?.VersionId), ("$sha", receipt?.Sha256),
            ("$verified", receipt is null ? null : Ticks(verifiedUtc)),
            ("$g", work.Publication.Position.Generation), ("$s", work.Publication.Position.Sequence), ("$i", work.Publication.Index))
            .ConfigureAwait(false);
    }

    internal async ValueTask<(long Sequence, string? Sha256)> ReadPrefixAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, "SELECT sequence, sha256 FROM archive_prefix WHERE row_id = 1;");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), NullableString(reader, 1));
    }

    internal async ValueTask SavePrefixAsync(long sequence, string sha256, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE archive_prefix SET sequence = $s, sha256 = $sha WHERE row_id = 1 AND sequence < $s;
            """, cancellationToken, ("$s", sequence), ("$sha", sha256)).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1849", Justification = "SQLite exposes deferred read transactions through this overload.")]
    internal async ValueTask<SiloArchiveBacklog> ReadBacklogAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var aggregate = CreateCommand(connection, transaction, """
            SELECT COUNT(*), COALESCE(SUM(bytes), 0), MIN(created_ticks),
                COALESCE(SUM(state = 'blocked'), 0), COALESCE(SUM(state = 'failed'), 0)
            FROM archive_work WHERE state NOT IN ('reachable', 'policy-expired');
            """);
        long count, bytes, blocked, failed;
        DateTimeOffset? oldest;
        using (var reader = await aggregate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            (count, bytes, blocked, failed) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(3), reader.GetInt64(4));
            oldest = NullableLong(reader, 2) is { } ticks ? FromTicks(ticks) : null;
        }
        using var cutoff = CreateCommand(connection, transaction, """
            WITH first_gap AS (
                SELECT generation, sequence, record_index FROM archive_work
                WHERE state <> 'policy-expired' AND (
                    state <> 'reachable' OR receipt_generation IS NOT 1 OR receipt_sequence IS NULL OR
                    receipt_sequence > (SELECT sequence FROM archive_prefix WHERE row_id = 1))
                ORDER BY generation, sequence, record_index LIMIT 1
            )
            SELECT group_id, revision_id, generation, sequence, record_index, receipt_generation, receipt_sequence, receipt_version, receipt_sha256, verified_ticks
            FROM archive_work w WHERE state = 'reachable' AND receipt_generation = 1
             AND receipt_sequence <= (SELECT sequence FROM archive_prefix WHERE row_id = 1)
             AND NOT EXISTS (SELECT 1 FROM first_gap
                 WHERE (first_gap.generation, first_gap.sequence, first_gap.record_index) <= (w.generation, w.sequence, w.record_index))
            ORDER BY generation DESC, sequence DESC, record_index DESC LIMIT 1;
            """);
        using var latest = CreateCommand(connection, transaction, """
            SELECT group_id, revision_id, generation, sequence, record_index, receipt_generation, receipt_sequence, receipt_version, receipt_sha256, verified_ticks
            FROM archive_work WHERE state = 'reachable' ORDER BY generation DESC, sequence DESC, record_index DESC LIMIT 1;
            """);
        return new(count, bytes, oldest, await ReadReceiptAsync(cutoff, cancellationToken).ConfigureAwait(false),
            await ReadReceiptAsync(latest, cancellationToken).ConfigureAwait(false), blocked, failed);
    }

    internal async ValueTask<CameraAgentSiloArchivePublication?> FindAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT group_id, revision_id, generation, sequence, record_index, receipt_generation, receipt_sequence, receipt_version, receipt_sha256,
                verified_ticks, state, reason FROM archive_work WHERE group_id = $group AND revision_id = $revision;
            """, ("$group", groupId), ("$revision", revisionId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new(reader.GetString(10), NullableString(reader, 11), NullableLong(reader, 5) is null ? null : GetReceipt(reader));
    }

    private async ValueTask<CameraAgentSiloArchiveReceipt?> ReadReceiptAsync(Microsoft.Data.Sqlite.SqliteCommand command,
        CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? GetReceipt(reader) : null;
    }

    private CameraAgentSiloArchiveReceipt GetReceipt(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(marker.StoreId, marker.Bucket, reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt32(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8),
            FromTicks(reader.GetInt64(9)));
}
