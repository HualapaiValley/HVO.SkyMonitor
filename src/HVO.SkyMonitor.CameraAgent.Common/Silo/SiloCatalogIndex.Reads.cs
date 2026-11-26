using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using static HVO.SkyMonitor.CameraAgent.Common.Silo.SiloSqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloPublicationRef(string GroupId, string RevisionId, SiloJournalPosition Position, int Index);

/// <summary>
/// What the catalog can claim (#1965 4.5, 5.4): how far authority is proven, the complete-publication frontier inside
/// it, and whether an authority-unknown interval follows. These are reported separately and never merged.
/// </summary>
internal sealed record SiloAuthorityReport(
    string StartKind,
    SiloJournalPosition? Start,
    SiloJournalPosition? PrefixEnd,
    string EndReason,
    bool UnknownInterval,
    SiloPublicationRef? FirstIncompletePublication,
    IReadOnlyList<SiloPublicationRef> UnprovenPublications)
{
    /// <summary>True when every publication in the prefix verified its manifest: the frontier is the prefix end.</summary>
    public bool CompleteThroughPrefixEnd => FirstIncompletePublication is null;
}

internal sealed record SiloIndexedPublication(
    string GroupKind,
    string GroupId,
    string RevisionId,
    string? PreviousRevisionId,
    SiloJournalPosition Position,
    int Index,
    string State,
    string ManifestStatus,
    string? Outcome,
    string? OutcomeDetail,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    string? CameraId,
    string? StreamId,
    long? RevisionOrder,
    SiloCaptureSequence? Capture,
    bool ReachedP4);

/// <summary>Each base keeps three independent pointers (#1965 6.3); the default reopen target is the last live, else the first.</summary>
internal sealed record SiloRevisionPointers(string GroupId, string? FirstRevisionId, string? LatestRevisionId, string? LastLiveRevisionId)
{
    public string? DefaultRevisionId => LastLiveRevisionId ?? FirstRevisionId;
}

internal sealed record SiloLiveInterval(
    string CameraId, string StreamId, string GroupId, string RevisionId, long StartedOrder, DateTimeOffset StartedUtc,
    long? EndedOrder, DateTimeOffset? EndedUtc);

internal sealed record SiloLocatorStatus(
    SiloExactLocator Locator, string Source, bool Eligible, string? IneligibleReason, string CopyState, string? ProbeStatus, string? ProbeDetail);

internal sealed record SiloRoleStatus(
    SiloRoleKey Role, string Sha256, long Length, string Disposition, string? Journaled, SiloExactLocator? AvailableAt,
    IReadOnlyList<SiloLocatorStatus> Locators);

internal sealed record SiloViewLayerRow(
    string Kind, string Scope, string? SourceCaptureId, long EvidenceOrder, string? EvidenceArtifactId, string? EvidenceIdentitySha256,
    string? EvidencePayloadSha256, DateTimeOffset? ObservedUtc, string Readiness, string? Reason);

internal sealed partial class SiloCatalogIndex
{
    private const string PublicationColumns = """
        group_kind, group_id, revision_id, previous_revision_id, generation, sequence, record_index, state, manifest_status, outcome,
        outcome_detail, from_utc_ticks, to_utc_ticks, camera_id, stream_id, revision_order, agent_id, capture_sequence, requires_members
        """;

    private static SiloIndexedPublication ReadPublication(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3),
        new(reader.GetInt64(4), reader.GetInt64(5)), reader.GetInt32(6), reader.GetString(7), reader.GetString(8),
        NullableString(reader, 9), NullableString(reader, 10),
        NullableLong(reader, 11) is { } from ? FromTicks(from) : null, NullableLong(reader, 12) is { } to ? FromTicks(to) : null,
        NullableString(reader, 13), NullableString(reader, 14), NullableLong(reader, 15),
        NullableString(reader, 16) is { } agent ? new SiloCaptureSequence(agent, reader.GetInt64(17)) : null, !reader.GetBoolean(18));

    public async ValueTask<SiloAuthorityReport> ReadAuthorityAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var (cursor, _) = await ReadCursorAsync(connection, null, cancellationToken).ConfigureAwait(false);
        string startKind, endReason;
        SiloJournalPosition? start;
        bool unknown;
        using (var command = CreateCommand(connection, null, """
            SELECT start_kind, start_generation, start_sequence, end_reason, unknown_interval FROM catalog_authority WHERE authority_key = 1;
            """))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            startKind = reader.GetString(0);
            start = NullableLong(reader, 1) is { } generation ? new SiloJournalPosition(generation, reader.GetInt64(2)) : null;
            endReason = reader.GetString(3);
            unknown = reader.GetInt64(4) == 1;
        }
        SiloPublicationRef? first = null;
        using (var command = CreateCommand(connection, null, """
            SELECT group_id, revision_id, generation, sequence, record_index FROM publications
            WHERE manifest_status NOT IN ('verified', 'policy-expired')
            ORDER BY generation, sequence, record_index LIMIT 1;
            """))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                first = new(reader.GetString(0), reader.GetString(1), new(reader.GetInt64(2), reader.GetInt64(3)), reader.GetInt32(4));
            }
        }
        var unproven = new List<SiloPublicationRef>();
        using (var command = CreateCommand(connection, null, """
            SELECT group_id, revision_id, generation, sequence, record_index FROM unproven_publications ORDER BY generation, sequence, record_index;
            """))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                unproven.Add(new(reader.GetString(0), reader.GetString(1), new(reader.GetInt64(2), reader.GetInt64(3)), reader.GetInt32(4)));
            }
        }
        return new(startKind, start, cursor, endReason, unknown, first, unproven);
    }

    public async ValueTask<SiloIndexedPublication?> ReadPublicationAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, $"""
            SELECT {PublicationColumns} FROM publications WHERE group_id = $group AND revision_id = $revision;
            """, ("$group", groupId), ("$revision", revisionId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPublication(reader) : null;
    }

    /// <summary>Every committed revision of a group in publish order, original revisions included.</summary>
    public async ValueTask<IReadOnlyList<SiloIndexedPublication>> ReadRevisionsAsync(string groupId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, $"""
            SELECT {PublicationColumns} FROM publications WHERE group_id = $group ORDER BY generation, sequence, record_index;
            """, ("$group", groupId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloIndexedPublication>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadPublication(reader));
        }
        return result;
    }

    /// <summary>Visible revisions whose UTC anchor lies in [from, to), from the indexed catalog.</summary>
    public async ValueTask<IReadOnlyList<SiloIndexedPublication>> QueryVisibleAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, string? cameraId, int limit, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, $"""
            SELECT {PublicationColumns} FROM publications
            WHERE state = 'visible' AND from_utc_ticks >= $from AND from_utc_ticks < $to AND ($camera IS NULL OR camera_id = $camera)
            ORDER BY from_utc_ticks, group_id, revision_id LIMIT $limit;
            """, ("$from", Ticks(fromUtc)), ("$to", Ticks(toUtc)), ("$camera", cameraId), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloIndexedPublication>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadPublication(reader));
        }
        return result;
    }

    /// <summary>The first committed, latest committed and last live-issued revisions, from publish order and live issue.</summary>
    public async ValueTask<SiloRevisionPointers> ReadPointersAsync(string groupId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var first = await ScalarStringAsync(connection, null, """
            SELECT revision_id FROM publications WHERE group_id = $group ORDER BY generation, sequence, record_index LIMIT 1;
            """, cancellationToken, ("$group", groupId)).ConfigureAwait(false);
        var latest = await ScalarStringAsync(connection, null, """
            SELECT revision_id FROM publications WHERE group_id = $group ORDER BY generation DESC, sequence DESC, record_index DESC LIMIT 1;
            """, cancellationToken, ("$group", groupId)).ConfigureAwait(false);
        var live = await ScalarStringAsync(connection, null, """
            SELECT revision_id FROM live_intervals WHERE group_id = $group ORDER BY generation DESC, sequence DESC, record_index DESC LIMIT 1;
            """, cancellationToken, ("$group", groupId)).ConfigureAwait(false);
        return new(groupId, first, latest, live);
    }

    public async ValueTask<SiloLiveInterval?> ReadCurrentLiveAsync(string cameraId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT camera_id, stream_id, group_id, revision_id, change_order, started_utc_ticks
            FROM live_intervals WHERE camera_id = $camera AND ended_change_order IS NULL
            ORDER BY generation DESC, sequence DESC, record_index DESC LIMIT 1;
            """, ("$camera", cameraId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                FromTicks(reader.GetInt64(5)), null, null)
            : null;
    }

    public async ValueTask<IReadOnlyList<SiloLiveInterval>> ReadLiveHistoryAsync(string cameraId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT camera_id, stream_id, group_id, revision_id, change_order, started_utc_ticks, ended_change_order, ended_utc_ticks
            FROM live_intervals WHERE camera_id = $camera ORDER BY generation, sequence, record_index;
            """, ("$camera", cameraId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloLiveInterval>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                FromTicks(reader.GetInt64(5)), NullableLong(reader, 6), NullableLong(reader, 7) is { } ended ? FromTicks(ended) : null));
        }
        return result;
    }

    internal async ValueTask<IReadOnlyList<SiloLiveInterval>> ReadVisibleLiveIntervalsAsync(string baseId, long afterOrder,
        int limit, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT i.camera_id, i.stream_id, i.group_id, i.revision_id, i.change_order, i.started_utc_ticks,
                   i.ended_change_order, i.ended_utc_ticks
            FROM live_intervals i JOIN publications p ON p.group_id = i.group_id AND p.revision_id = i.revision_id
            WHERE i.group_id = $base AND i.change_order > $after AND p.state = 'visible'
            ORDER BY i.change_order LIMIT $limit;
            """, ("$base", baseId), ("$after", afterOrder), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloLiveInterval>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                FromTicks(reader.GetInt64(5)), NullableLong(reader, 6), NullableLong(reader, 7) is { } ended ? FromTicks(ended) : null));
        }
        return result;
    }

    internal async ValueTask<bool> HasLiveIssueAsync(string groupId, long order, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT EXISTS (SELECT 1 FROM live_intervals WHERE group_id = $group AND change_order = $order);
            """, ("$group", groupId), ("$order", order));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    internal async ValueTask<bool> HasVisibleViewAsync(string groupId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT EXISTS (SELECT 1 FROM publications WHERE group_id = $group AND state = 'visible');
            """, ("$group", groupId));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    internal async ValueTask<string?> ReadLastVisibleLiveRevisionAsync(string groupId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarStringAsync(connection, null, """
            SELECT i.revision_id FROM live_intervals i
            JOIN publications p ON p.group_id = i.group_id AND p.revision_id = i.revision_id
            WHERE i.group_id = $group AND p.state = 'visible' ORDER BY i.change_order DESC LIMIT 1;
            """, cancellationToken, ("$group", groupId)).ConfigureAwait(false);
    }

    /// <summary>Layer bindings of a view revision, exactly as published, in every layer state.</summary>
    public async ValueTask<IReadOnlyList<SiloViewLayerRow>> ReadLayersAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT layer_kind, scope, source_capture_id, evidence_order, evidence_artifact_id, evidence_identity_sha256,
                evidence_payload_sha256, observed_utc_ticks, readiness, reason
            FROM view_layers WHERE group_id = $group AND revision_id = $revision ORDER BY ordinal;
            """, ("$group", groupId), ("$revision", revisionId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloViewLayerRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), NullableString(reader, 2), reader.GetInt64(3), NullableString(reader, 4),
                NullableString(reader, 5), NullableString(reader, 6), NullableLong(reader, 7) is { } observed ? FromTicks(observed) : null,
                reader.GetString(8), NullableString(reader, 9)));
        }
        return result;
    }

    public async ValueTask<SiloRoleStatus?> ReadRoleAsync(SiloRoleKey role, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string sha, disposition;
        string? journaled;
        long length;
        SiloExactLocator? available;
        using (var command = CreateCommand(connection, null, """
            SELECT sha256, length, disposition, journaled, available_store, available_bucket, available_key, available_version
            FROM roles WHERE kind = $kind AND id = $id AND revision = $revision;
            """, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            (sha, length, disposition, journaled) = (reader.GetString(0), reader.GetInt64(1), reader.GetString(2), NullableString(reader, 3));
            available = NullableString(reader, 4) is { } store
                ? new SiloExactLocator(store, reader.GetString(5), reader.GetString(6), reader.GetString(7))
                : null;
        }
        var locators = new List<SiloLocatorStatus>();
        using (var command = CreateCommand(connection, null, """
            SELECT store_id, bucket, key, version_id, source, eligible, ineligible_reason, copy_state, probe_status, probe_detail
            FROM locators WHERE kind = $kind AND id = $id AND revision = $revision
            ORDER BY generation, sequence, record_index, store_id, key, version_id;
            """, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                locators.Add(new(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                    reader.GetString(4), reader.GetInt64(5) == 1, NullableString(reader, 6), reader.GetString(7),
                    NullableString(reader, 8), NullableString(reader, 9)));
            }
        }
        return new(role, sha, length, disposition, journaled, available, locators);
    }

    internal async ValueTask<bool> IsArtifactRetainedAsync(string artifactId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            WITH payloads AS (
                SELECT disposition FROM roles WHERE kind = 'member' AND id = $id AND revision NOT IN ($raw, $product)
            )
            SELECT NOT EXISTS (SELECT 1 FROM payloads)
                OR EXISTS (SELECT 1 FROM payloads WHERE disposition <> 'policy-expired');
            """, ("$id", artifactId), ("$raw", SiloArtifactStorage.RawEvidenceRevision),
            ("$product", SiloArtifactStorage.ProductEvidenceRevision));
        // No indexed role can mean the catalog has not caught up yet. Require explicit expiry to release a hold.
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    internal async ValueTask<bool> HasVisibleMemberAsync(string artifactId, string revision, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT EXISTS (
                SELECT 1 FROM publication_members m JOIN publications p ON p.group_id = m.group_id AND p.revision_id = m.revision_id
                WHERE m.artifact_id = $artifact AND m.artifact_revision = $revision AND p.state = 'visible');
            """, ("$artifact", artifactId), ("$revision", revision));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    internal async ValueTask<bool> HasVisibleMemberPairAsync(string artifactId, string revision, string evidenceRevision,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT COUNT(DISTINCT m.artifact_revision) = 2
            FROM publication_members m JOIN publications p ON p.group_id = m.group_id AND p.revision_id = m.revision_id
            WHERE m.artifact_id = $artifact AND m.artifact_revision IN ($revision, $evidence) AND p.state = 'visible';
            """, ("$artifact", artifactId), ("$revision", revision), ("$evidence", evidenceRevision));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    /// <summary>Whether an artifact revision is already published with another length, digest or set of facts.</summary>
    public async ValueTask<bool> ConflictsWithPublishedAsync(SiloObjectIdentity identity, string factsSha256, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null,
            "SELECT sha256, length, facts_sha256 FROM roles WHERE kind = $kind AND id = $id AND revision = $revision;",
            ("$kind", KindName(identity.Kind)), ("$id", identity.Id), ("$revision", identity.Revision));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            (!string.Equals(reader.GetString(0), identity.Sha256, StringComparison.Ordinal) || reader.GetInt64(1) != identity.Length ||
             !string.Equals(reader.GetString(2), factsSha256, StringComparison.Ordinal));
    }

    public async ValueTask<IReadOnlyDictionary<string, long>> ReadIdentityFloorsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, "SELECT counter, value FROM identity_floors ORDER BY counter;");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }
        return result;
    }

    /// <summary>Every applied record at or before the cursor, exactly as committed, for a checkpoint.</summary>
    internal async ValueTask<(SiloJournalPosition Covers, string CoversSha256, IReadOnlyList<SiloCheckpointEntry> Entries,
        IReadOnlyList<SiloCheckpointPublication> ReachedP4)> ExportRecordsAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The writer gate is held, so no segment applies between the cursor read and the record export.
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var (cursor, sha) = await ReadCursorAsync(connection, null, cancellationToken).ConfigureAwait(false);
            if (cursor is not { } covers)
            {
                throw new InvalidOperationException("An empty catalog has nothing to checkpoint.");
            }
            var entries = new List<SiloCheckpointEntry>();
            using (var command = CreateCommand(connection, null, """
                SELECT generation, sequence, record_index, record_json FROM journal_records ORDER BY generation, sequence, record_index;
                """))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    using var document = System.Text.Json.JsonDocument.Parse(reader.GetString(3));
                    entries.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), document.RootElement.Clone()));
                }
            }
            // Settled history is every publication proven to have reached P4, whatever an outage has closed since.
            var reachedP4 = new List<SiloCheckpointPublication>();
            using (var command = CreateCommand(connection, null, """
                SELECT group_id, revision_id FROM publications WHERE requires_members = 0 ORDER BY generation, sequence, record_index;
                """))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    reachedP4.Add(new(reader.GetString(0), reader.GetString(1)));
                }
            }
            return (covers, sha!, entries, reachedP4);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// A canonical description of everything the catalog claims, without probe times. Two catalogs that describe the
    /// same text answer every query the same way; it is the parity check between incremental and rebuilt indexes.
    /// </summary>
    public async ValueTask<string> DescribeAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var builder = new StringBuilder();
        foreach (var (title, sql) in DescribeQueries)
        {
            builder.Append("# ").AppendLine(title);
            using var command = CreateCommand(connection, null, sql);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    if (ordinal > 0)
                    {
                        builder.Append(" | ");
                    }
                    builder.Append(await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) ? "∅" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture));
                }
                builder.AppendLine();
            }
        }
        return builder.ToString();
    }

    private static readonly (string Title, string Sql)[] DescribeQueries =
    [
        ("cursor", "SELECT generation, sequence, sha256 FROM catalog_cursor;"),
        ("generations", "SELECT generation, reason, witnessed FROM generations ORDER BY generation;"),
        ("floors", "SELECT counter, value FROM identity_floors ORDER BY counter;"),
        ("publications", """
            SELECT generation, sequence, record_index, group_kind, group_id, revision_id, previous_revision_id, manifest_sha256,
                manifest_length, agent_id, capture_sequence, manifest_status, state, outcome, published_utc_ticks, from_utc_ticks,
                to_utc_ticks, camera_id, stream_id, reference_capture_id, revision_order, manifest_json
            FROM publications ORDER BY generation, sequence, record_index;
            """),
        ("members", """
            SELECT group_id, revision_id, ordinal, artifact_id, artifact_revision, sha256, length, facts_sha256, required, store_id, bucket, key,
                version_id, role, retention_class, media_type, content_identity_sha256, output_identity_sha256, producer,
                recipe_identity_sha256
            FROM publication_members ORDER BY group_id, revision_id, ordinal;
            """),
        ("dependencies", "SELECT * FROM dependencies ORDER BY group_id, revision_id, ordinal;"),
        ("layers", "SELECT * FROM view_layers ORDER BY group_id, revision_id, ordinal;"),
        ("roles", """
            SELECT kind, id, revision, sha256, length, facts_sha256, generation, sequence, record_index, journaled, disposition,
                available_store, available_bucket, available_key, available_version
            FROM roles ORDER BY kind, id, revision;
            """),
        ("locators", """
            SELECT kind, id, revision, store_id, bucket, key, version_id, source, generation, sequence, record_index, store_class,
                retrieval, custody, eligible, ineligible_reason, copy_state
            FROM locators ORDER BY kind, id, revision, store_id, bucket, key, version_id;
            """),
        ("intents", "SELECT * FROM expiry_intents ORDER BY intent_id;"),
        ("targets", "SELECT * FROM expiry_targets ORDER BY intent_id, store_id, bucket, key, version_id;"),
        ("reservations", "SELECT * FROM survivor_reservations ORDER BY reservation_id;"),
        ("live", "SELECT * FROM live_intervals ORDER BY generation, sequence, record_index;"),
        ("pins", "SELECT * FROM pins ORDER BY pin_id;"),
        ("policies", "SELECT * FROM policies ORDER BY policy_id;"),
        ("anchors", "SELECT * FROM anchors ORDER BY generation, sequence, record_index;"),
        ("records", "SELECT generation, sequence, record_index, record_type, rejection FROM journal_records ORDER BY generation, sequence, record_index;")
    ];
}
