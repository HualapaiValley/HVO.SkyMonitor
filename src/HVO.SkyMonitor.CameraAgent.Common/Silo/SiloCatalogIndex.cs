using Microsoft.Data.Sqlite;
using static HVO.SkyMonitor.CameraAgent.Common.Silo.SiloSqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// Whether a later generation's opening is proven by its witnesses (#1972 supplies the proof). The proof binds the
/// exact opening: the SHA-256 of the segment that carries it, so a witness of any other opening of the same generation
/// and prefix, differing in any field, proves nothing.
/// </summary>
internal interface ISiloGenerationWitness
{
    bool IsWitnessed(SiloGenerationOpenRecord open, SiloJournalPosition position, string openingSegmentSha256);
}

/// <summary>
/// Whether an anchored checkpoint is inside a sealed backup and, when enabled, a verified archive receipt (#1972), and
/// which exact version of it a verified receipt names in another store. A replica's checkpoint is read only through
/// that exact receipted version, never through whatever version its key holds now.
/// </summary>
internal interface ISiloAnchorTrust
{
    bool IsTrusted(SiloAnchorRecord anchor, SiloJournalPosition position);

    SiloExactLocator? ReceiptedCopy(SiloAnchorRecord anchor, SiloStoreDescriptor store);
}

/// <summary>The default: nothing is proven, so later generations end the prefix and replay starts at generation 1.</summary>
internal sealed class SiloNoAuthorityEvidence : ISiloGenerationWitness, ISiloAnchorTrust
{
    public static SiloNoAuthorityEvidence Instance { get; } = new();

    public bool IsWitnessed(SiloGenerationOpenRecord open, SiloJournalPosition position, string openingSegmentSha256) => false;

    public bool IsTrusted(SiloAnchorRecord anchor, SiloJournalPosition position) => false;

    public SiloExactLocator? ReceiptedCopy(SiloAnchorRecord anchor, SiloStoreDescriptor store) => null;
}

/// <summary>Role dispositions (#1965 5.3, 5.4, 6.2). Copy states never imply a role state.</summary>
internal static class SiloDisposition
{
    public const string Unverified = "unverified";
    public const string Available = "available";
    public const string TemporarilyUnavailable = "temporarily-unavailable";
    public const string Missing = "missing";
    public const string Conflict = "conflict";
    public const string PolicyExpired = "policy-expired";
    public const string AuthorityUnproven = "authority-unproven";
    public const string Pending = "pending";
    public const string Verified = "verified";
}

/// <summary>
/// The application-owned, rebuildable SQLite catalog (<c>hvo.cameraagent.silo.catalog-index/1</c>). Everything here is
/// derived from the authenticated journal prefix, the manifests it names and exact-version reads, so deleting the file
/// loses no authority. One applier serves live publication (P4), startup catch-up and rebuild.
/// </summary>
internal sealed partial class SiloCatalogIndex(string path, SiloStorageBinding binding, TimeProvider clock) : IDisposable
{
    internal const int ApplicationId = 0x48534349; // "HSCI"

    internal static readonly SiloSqliteFormat Format = new(SiloFormats.CatalogIndex, ApplicationId, 1, SchemaSql, "Silo catalog index");

    internal const string SchemaSql = """
        CREATE TABLE silo_meta(
            key TEXT PRIMARY KEY CHECK(length(key) BETWEEN 1 AND 64),
            value TEXT NOT NULL CHECK(length(value) BETWEEN 1 AND 512)
        ) STRICT;
        CREATE TRIGGER silo_meta_no_update BEFORE UPDATE ON silo_meta BEGIN SELECT RAISE(ABORT, 'silo meta is immutable'); END;

        CREATE TABLE catalog_cursor(
            cursor_key INTEGER PRIMARY KEY CHECK(cursor_key = 1),
            generation INTEGER NOT NULL CHECK(generation >= 0),
            sequence INTEGER NOT NULL CHECK(sequence >= 0),
            sha256 TEXT,
            CHECK((sequence = 0) = (sha256 IS NULL))
        ) STRICT;
        CREATE TRIGGER catalog_cursor_forward BEFORE UPDATE ON catalog_cursor
        WHEN NEW.generation < OLD.generation OR (NEW.generation = OLD.generation AND NEW.sequence <= OLD.sequence)
        BEGIN SELECT RAISE(ABORT, 'the catalog cursor only advances'); END;

        CREATE TABLE catalog_authority(
            authority_key INTEGER PRIMARY KEY CHECK(authority_key = 1),
            start_kind TEXT NOT NULL CHECK(start_kind IN ('genesis', 'anchor')),
            start_generation INTEGER,
            start_sequence INTEGER,
            end_reason TEXT NOT NULL,
            unknown_interval INTEGER NOT NULL CHECK(unknown_interval IN (0, 1)),
            evaluated_unix_ms INTEGER NOT NULL
        ) STRICT;

        CREATE TABLE journal_records(
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            record_type TEXT NOT NULL,
            record_json TEXT NOT NULL,
            rejection TEXT,
            PRIMARY KEY(generation, sequence, record_index)
        ) STRICT;

        CREATE TABLE generations(
            generation INTEGER PRIMARY KEY,
            reason TEXT NOT NULL,
            witnessed INTEGER NOT NULL CHECK(witnessed IN (0, 1))
        ) STRICT;

        CREATE TABLE identity_floors(
            counter TEXT PRIMARY KEY,
            value INTEGER NOT NULL CHECK(value >= 0)
        ) STRICT;

        CREATE TABLE roles(
            kind TEXT NOT NULL CHECK(kind IN ('member', 'manifest')),
            id TEXT NOT NULL,
            revision TEXT NOT NULL,
            sha256 TEXT NOT NULL CHECK(length(sha256) = 64),
            length INTEGER NOT NULL CHECK(length >= 0),
            facts_sha256 TEXT NOT NULL CHECK(length(facts_sha256) = 64),
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            journaled TEXT CHECK(journaled IS NULL OR journaled IN ('missing', 'conflict', 'policy-expired')),
            disposition TEXT NOT NULL CHECK(disposition IN ('unverified', 'available', 'temporarily-unavailable', 'missing', 'conflict', 'policy-expired')),
            available_store TEXT,
            available_bucket TEXT,
            available_key TEXT,
            available_version TEXT,
            verified_unix_ms INTEGER,
            PRIMARY KEY(kind, id, revision)
        ) STRICT;
        CREATE INDEX roles_disposition ON roles(disposition, generation, sequence, record_index);
        CREATE TRIGGER roles_identity_fixed BEFORE UPDATE ON roles
        WHEN NEW.sha256 IS NOT OLD.sha256 OR NEW.length IS NOT OLD.length OR NEW.facts_sha256 IS NOT OLD.facts_sha256
        BEGIN SELECT RAISE(ABORT, 'a role is bound to exactly one length, SHA-256 and set of facts'); END;

        CREATE TABLE locators(
            kind TEXT NOT NULL,
            id TEXT NOT NULL,
            revision TEXT NOT NULL,
            store_id TEXT NOT NULL,
            bucket TEXT NOT NULL,
            key TEXT NOT NULL,
            version_id TEXT NOT NULL,
            source TEXT NOT NULL CHECK(source IN ('named', 'copy', 'remap')),
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            store_class TEXT,
            retrieval TEXT,
            custody TEXT,
            eligible INTEGER NOT NULL CHECK(eligible IN (0, 1)),
            ineligible_reason TEXT,
            copy_state TEXT NOT NULL CHECK(copy_state IN ('active', 'evicted', 'missing', 'quarantined')),
            probe_status TEXT CHECK(probe_status IS NULL OR probe_status IN ('verified', 'absent', 'damaged', 'unavailable')),
            probe_detail TEXT,
            probe_unix_ms INTEGER,
            PRIMARY KEY(kind, id, revision, store_id, bucket, key, version_id),
            CHECK((eligible = 1) = (ineligible_reason IS NULL))
        ) STRICT;
        CREATE INDEX locators_version ON locators(store_id, bucket, key, version_id);

        CREATE TABLE publications(
            group_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            group_kind TEXT NOT NULL,
            previous_revision_id TEXT,
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            manifest_sha256 TEXT NOT NULL CHECK(length(manifest_sha256) = 64),
            manifest_length INTEGER NOT NULL CHECK(manifest_length >= 0),
            agent_id TEXT,
            capture_sequence INTEGER,
            manifest_status TEXT NOT NULL CHECK(manifest_status IN ('pending', 'verified', 'temporarily-unavailable', 'missing', 'unverified', 'conflict', 'policy-expired')),
            state TEXT NOT NULL CHECK(state IN ('pending', 'visible', 'closed')),
            requires_members INTEGER NOT NULL DEFAULT 1 CHECK(requires_members IN (0, 1)),
            outcome TEXT CHECK(outcome IS NULL OR outcome IN ('temporarily-unavailable', 'missing', 'unverified', 'conflict', 'policy-expired')),
            outcome_detail TEXT,
            published_utc_ticks INTEGER,
            from_utc_ticks INTEGER,
            to_utc_ticks INTEGER,
            camera_id TEXT,
            stream_id TEXT,
            reference_capture_id TEXT,
            revision_order INTEGER,
            manifest_json TEXT,
            PRIMARY KEY(group_id, revision_id),
            UNIQUE(generation, sequence, record_index),
            CHECK((state = 'closed') = (outcome IS NOT NULL)),
            CHECK(state <> 'visible' OR manifest_status = 'verified')
        ) STRICT;
        CREATE INDEX publications_time ON publications(from_utc_ticks, group_id, revision_id) WHERE state = 'visible';
        CREATE INDEX publications_camera ON publications(camera_id, from_utc_ticks) WHERE state = 'visible';
        CREATE INDEX publications_group ON publications(group_id, generation, sequence, record_index);
        CREATE INDEX publications_open ON publications(generation, sequence, record_index) WHERE state <> 'visible';

        CREATE TABLE publication_members(
            group_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            artifact_id TEXT NOT NULL,
            artifact_revision TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            length INTEGER NOT NULL,
            facts_sha256 TEXT NOT NULL,
            required INTEGER NOT NULL CHECK(required IN (0, 1)),
            store_id TEXT NOT NULL,
            bucket TEXT NOT NULL,
            key TEXT NOT NULL,
            version_id TEXT NOT NULL,
            role TEXT,
            retention_class TEXT,
            media_type TEXT,
            content_identity_sha256 TEXT,
            output_identity_sha256 TEXT,
            producer TEXT,
            recipe_identity_sha256 TEXT,
            PRIMARY KEY(group_id, revision_id, ordinal)
        ) STRICT;
        CREATE INDEX publication_members_artifact ON publication_members(artifact_id, artifact_revision);

        CREATE TABLE dependencies(
            group_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            artifact_id TEXT NOT NULL,
            artifact_revision TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            kind TEXT NOT NULL CHECK(kind IN ('reopen', 'compute')),
            required INTEGER NOT NULL CHECK(required IN (0, 1)),
            PRIMARY KEY(group_id, revision_id, ordinal)
        ) STRICT;
        CREATE INDEX dependencies_artifact ON dependencies(artifact_id, artifact_revision);

        CREATE TABLE view_layers(
            group_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            layer_kind TEXT NOT NULL,
            scope TEXT NOT NULL,
            source_capture_id TEXT,
            evidence_order INTEGER NOT NULL,
            evidence_artifact_id TEXT,
            evidence_identity_sha256 TEXT,
            evidence_payload_sha256 TEXT,
            observed_utc_ticks INTEGER,
            readiness TEXT NOT NULL,
            reason TEXT,
            PRIMARY KEY(group_id, revision_id, ordinal)
        ) STRICT;

        CREATE TABLE expiry_intents(
            intent_id TEXT PRIMARY KEY,
            scope TEXT NOT NULL CHECK(scope IN ('copy', 'role')),
            kind TEXT NOT NULL,
            id TEXT NOT NULL,
            revision TEXT NOT NULL,
            authorization TEXT NOT NULL,
            last_copy INTEGER NOT NULL CHECK(last_copy IN (0, 1)),
            policy_reason TEXT NOT NULL,
            covers_authoritative INTEGER NOT NULL CHECK(covers_authoritative IN (0, 1)),
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL
        ) STRICT;

        CREATE TABLE expiry_targets(
            intent_id TEXT NOT NULL REFERENCES expiry_intents(intent_id),
            store_id TEXT NOT NULL,
            bucket TEXT NOT NULL,
            key TEXT NOT NULL,
            version_id TEXT NOT NULL,
            custody TEXT NOT NULL,
            allocation_id TEXT,
            allocated_bytes INTEGER,
            outcome TEXT CHECK(outcome IS NULL OR outcome IN ('deleted', 'already-absent', 'not-deleted', 'refused')),
            outcome_generation INTEGER,
            outcome_sequence INTEGER,
            outcome_index INTEGER,
            PRIMARY KEY(intent_id, store_id, bucket, key, version_id)
        ) STRICT;

        CREATE TABLE survivor_reservations(
            reservation_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            id TEXT NOT NULL,
            revision TEXT NOT NULL,
            store_id TEXT NOT NULL,
            bucket TEXT NOT NULL,
            key TEXT NOT NULL,
            version_id TEXT NOT NULL,
            intent_id TEXT,
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            released_generation INTEGER,
            released_sequence INTEGER,
            released_index INTEGER,
            release_reason TEXT
        ) STRICT;

        CREATE TABLE live_intervals(
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            camera_id TEXT NOT NULL,
            stream_id TEXT NOT NULL,
            group_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            change_order INTEGER NOT NULL,
            started_utc_ticks INTEGER NOT NULL,
            ended_change_order INTEGER,
            ended_utc_ticks INTEGER,
            PRIMARY KEY(generation, sequence, record_index)
        ) STRICT;
        CREATE INDEX live_intervals_open ON live_intervals(camera_id) WHERE ended_change_order IS NULL;
        CREATE INDEX live_intervals_group ON live_intervals(group_id, generation, sequence, record_index);

        CREATE TABLE pins(
            pin_id TEXT PRIMARY KEY,
            target_kind TEXT NOT NULL,
            target_id TEXT NOT NULL,
            target_revision TEXT NOT NULL,
            active INTEGER NOT NULL CHECK(active IN (0, 1)),
            reason TEXT NOT NULL,
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL
        ) STRICT;

        CREATE TABLE policies(
            policy_id TEXT PRIMARY KEY,
            policy_json TEXT NOT NULL,
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL
        ) STRICT;

        CREATE TABLE anchors(
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            store_id TEXT NOT NULL,
            bucket TEXT NOT NULL,
            key TEXT NOT NULL,
            version_id TEXT NOT NULL,
            checkpoint_sha256 TEXT NOT NULL,
            checkpoint_length INTEGER NOT NULL,
            covers_generation INTEGER NOT NULL,
            covers_sequence INTEGER NOT NULL,
            covers_sha256 TEXT NOT NULL,
            PRIMARY KEY(generation, sequence, record_index)
        ) STRICT;

        CREATE TABLE verification_cursors(
            name TEXT PRIMARY KEY CHECK(name IN ('lazy', 'audit')),
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            kind TEXT NOT NULL,
            id TEXT NOT NULL,
            revision TEXT NOT NULL
        ) STRICT;

        CREATE TABLE unproven_publications(
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            record_index INTEGER NOT NULL,
            group_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            PRIMARY KEY(generation, sequence, record_index)
        ) STRICT;
        """;

    private readonly SemaphoreSlim _writer = new(1, 1);

    public void Dispose() => _writer.Dispose();

    public string Path { get; } = path;

    public SiloStorageBinding Binding { get; } = binding;

    /// <summary>
    /// Prove any interrupted replacement belongs to this instance before finishing or discarding it, refuse a foreign or
    /// newer catalog (judging committed WAL pages on a copy, so nothing is written), then create or prove the schema.
    /// </summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var metadata = SiloPublicationState.BindingMetadata(Binding);
        await SiloCatalogFiles.RecoverAsync(Path, Format, metadata, cancellationToken).ConfigureAwait(false);
        if (new FileInfo(Path + "-wal") is { Exists: true, Length: > 0 })
        {
            await InspectAsync(Path, Format, metadata, System.IO.Path.GetDirectoryName(Path)!, cancellationToken).ConfigureAwait(false);
        }
        RefuseUnsupportedHeader(Path, Format);
        using var connection = await OpenAsync(Path, cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, Format, metadata, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO catalog_cursor(cursor_key, generation, sequence) VALUES(1, 0, 0) ON CONFLICT(cursor_key) DO NOTHING;
            INSERT INTO catalog_authority(authority_key, start_kind, end_reason, unknown_interval, evaluated_unix_ms)
            VALUES(1, 'genesis', 'tail', 0, $now) ON CONFLICT(authority_key) DO NOTHING;
            """, cancellationToken, ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds())).ConfigureAwait(false);
    }

    internal ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken) => OpenAsync(Path, cancellationToken);

    private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The last applied segment: the end of the applied authenticated prefix.</summary>
    public async ValueTask<(SiloJournalPosition? Position, string? Sha256)> ReadCursorAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCursorAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<(SiloJournalPosition? Position, string? Sha256)> ReadCursorAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, "SELECT generation, sequence, sha256 FROM catalog_cursor WHERE cursor_key = 1;");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt64(1) == 0)
        {
            return (null, null);
        }
        return (new(reader.GetInt64(0), reader.GetInt64(1)), reader.GetString(2));
    }

    private static async ValueTask<bool> UnknownIntervalAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        await ScalarLongAsync(connection, transaction, "SELECT unknown_interval FROM catalog_authority WHERE authority_key = 1;",
            cancellationToken).ConfigureAwait(false) == 1;

    /// <summary>
    /// Record where the authenticated prefix ends, in one transaction with the publications named past it and every role
    /// disposition the authority-unknown interval implies, so a crash leaves all of them or none (#1965 4.5, 5.4). A null
    /// start kind keeps the recorded start. A complete scan replaces the unproven names; an incomplete one only adds to
    /// them, because a read that failed proves nothing about the gap.
    /// </summary>
    internal async ValueTask RecordAuthorityEndAsync(
        string? startKind, SiloJournalPosition? start, string endReason, bool unknownInterval, SiloUnprovenScan unproven,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unproven);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var wasUnknown = await UnknownIntervalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                UPDATE catalog_authority SET start_kind = COALESCE($kind, start_kind),
                    start_generation = CASE WHEN $kind IS NULL THEN start_generation ELSE $g END,
                    start_sequence = CASE WHEN $kind IS NULL THEN start_sequence ELSE $s END,
                    end_reason = $reason, unknown_interval = $unknown, evaluated_unix_ms = $now WHERE authority_key = 1;
                """, cancellationToken, ("$kind", startKind), ("$g", start?.Generation), ("$s", start?.Sequence),
                ("$reason", endReason), ("$unknown", unknownInterval ? 1 : 0), ("$now", Now())).ConfigureAwait(false);
            if (unproven.Complete)
            {
                await ExecuteAsync(connection, transaction, "DELETE FROM unproven_publications;", cancellationToken).ConfigureAwait(false);
            }
            foreach (var (position, index, group, revision) in unproven.Found)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO unproven_publications(generation, sequence, record_index, group_id, revision_id)
                    VALUES($g, $s, $i, $group, $revision) ON CONFLICT DO NOTHING;
                    """, cancellationToken, ("$g", position.Generation), ("$s", position.Sequence), ("$i", index),
                    ("$group", group), ("$revision", revision)).ConfigureAwait(false);
            }
            if (wasUnknown != unknownInterval)
            {
                var roles = new List<SiloRoleKey>();
                using (var command = CreateCommand(connection, transaction, "SELECT kind, id, revision FROM roles ORDER BY kind, id, revision;"))
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        roles.Add(new(ParseKind(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
                    }
                }
                foreach (var role in roles)
                {
                    await RecomputeRoleAsync(connection, transaction, role, unknownInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Fold the WAL into the main file and leave rollback journaling, so the file can be published by rename.</summary>
    internal async ValueTask SealForReplacementAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await Sqlite.SqliteConnectionConfigurationGate.RunAsync(async () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
