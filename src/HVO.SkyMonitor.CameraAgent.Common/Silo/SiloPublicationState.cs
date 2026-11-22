using Microsoft.Data.Sqlite;
using static HVO.SkyMonitor.CameraAgent.Common.Silo.SiloSqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>What a producer stages: a group revision without locators. Its bytes come from the payload spool.</summary>
internal sealed record SiloGroupDraft(
    string GroupKind,
    string GroupId,
    string RevisionId,
    DateTimeOffset PublishedUtc,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<SiloDraftMember> Members,
    IReadOnlyList<SiloManifestDependency> Dependencies,
    SiloManifestProvenance Provenance,
    string? PreviousRevisionId = null,
    string? CameraId = null,
    SiloManifestView? View = null,
    SiloCaptureSequence? Capture = null);

internal sealed record SiloDraftMember(
    string ArtifactId,
    string ArtifactRevision,
    string Role,
    SiloRetentionClass RetentionClass,
    string MediaType,
    long Length,
    string Sha256,
    bool Required,
    string SourceToken,
    string? ContentIdentitySha256 = null,
    string? OutputIdentitySha256 = null,
    string? Producer = null,
    string? RecipeIdentitySha256 = null)
{
    public SiloMemberFacts Facts() => new(Role, RetentionClass, MediaType, ContentIdentitySha256, OutputIdentitySha256, Producer, RecipeIdentitySha256);
}

/// <summary>
/// The payload spool seam (#1968 owns the spool). It reopens a member's exact staged bytes after a restart and is told
/// when they may be released: only after the group is visible or its staged work was abandoned (#1965 5.2). Release is
/// retried after an interruption or a lost acknowledgment, so it must be idempotent.
/// </summary>
internal interface ISiloPayloadSpool
{
    ValueTask<Stream> OpenAsync(string sourceToken, CancellationToken cancellationToken);

    ValueTask ReleaseAsync(string sourceToken, CancellationToken cancellationToken);
}

/// <summary>Publication states (#1965 5.3). Only <see cref="Visible"/> is browsable.</summary>
internal enum SiloStagedState { Staged, PayloadsQualified, ManifestQualified, Recorded, Visible, Abandoned }

internal sealed record SiloJournalHead(long WriterGeneration, long Generation, long Sequence, string? Sha256, string? VersionId)
{
    public SiloJournalPosition? Position => Sequence == 0 ? null : new(Generation, Sequence);
}

/// <summary>A segment whose exact bytes are fixed and spooled, awaiting its create-only write and readback.</summary>
internal sealed record SiloPendingSegment(
    SiloJournalPosition Position, string Sha256, long Length, string OperationId, string? GroupId, string? RevisionId);

/// <summary>A committed journal operation, found again by its key when a caller retries it.</summary>
internal sealed record SiloCommittedOperation(string OperationId, SiloJournalPosition Position, string Sha256, string VersionId);

/// <summary>
/// What the writer's surviving State proves (#1965 4.5, 5.3): the committed head that authenticates the journal tail,
/// and the committed publications whose first P4 has not happened yet, which recovery treats as P3 recovery rather than
/// as settled history.
/// </summary>
internal sealed record SiloWriterEvidence(
    SiloJournalPosition? Head, string? HeadSha256, IReadOnlySet<(string GroupId, string RevisionId)> AwaitingFirstP4)
{
    public static SiloWriterEvidence Empty { get; } = new(null, null, new HashSet<(string, string)>());
}

internal sealed record SiloStagedMember(int Ordinal, SiloDraftMember Draft, string? VersionId, DateTimeOffset? VerifiedUtc, bool Released);

internal sealed record SiloStagedGroup(
    SiloGroupDraft Draft,
    string DraftSha256,
    SiloStagedState State,
    IReadOnlyList<SiloStagedMember> Members,
    string? ManifestSha256,
    long? ManifestLength,
    string? ManifestVersionId,
    DateTimeOffset? ManifestVerifiedUtc,
    SiloJournalPosition? JournalPosition,
    string? BlockedCode,
    string? BlockedDetail,
    string? SupersededManifestVersionId);

/// <summary>A version that staged work created and no committed record names. An empty version is an unknown one.</summary>
internal sealed record SiloOrphanCandidate(SiloExactLocator Locator, string Sha256, long Length, string Reason, string? GroupId, string? RevisionId);

/// <summary>
/// Non-rebuildable publication state (<c>hvo.cameraagent.silo.publication-state/1</c>). Staged work and orphan
/// cleanup live here, apart from published revisions, and the instance backup covers it.
/// </summary>
internal sealed class SiloPublicationState(string path, SiloStorageBinding binding, TimeProvider clock)
{
    internal const int ApplicationId = 0x48535053; // "HSPS"

    /// <summary>The empty version of an orphan candidate: a write may have landed a version that was never recorded.</summary>
    public const string UnknownVersion = "";

    internal static readonly SiloSqliteFormat Format = new(SiloFormats.PublicationState, ApplicationId, 1, SchemaSql,
        "Silo publication state");

    internal const string SchemaSql = """
        CREATE TABLE silo_meta(
            key TEXT PRIMARY KEY CHECK(length(key) BETWEEN 1 AND 64),
            value TEXT NOT NULL CHECK(length(value) BETWEEN 1 AND 512)
        ) STRICT;
        CREATE TRIGGER silo_meta_no_update BEFORE UPDATE ON silo_meta BEGIN SELECT RAISE(ABORT, 'silo meta is immutable'); END;
        CREATE TRIGGER silo_meta_no_delete BEFORE DELETE ON silo_meta BEGIN SELECT RAISE(ABORT, 'silo meta is immutable'); END;

        CREATE TABLE journal_head(
            head_key INTEGER PRIMARY KEY CHECK(head_key = 1),
            writer_generation INTEGER NOT NULL CHECK(writer_generation >= 1),
            generation INTEGER NOT NULL CHECK(generation >= 0 AND generation <= writer_generation),
            sequence INTEGER NOT NULL CHECK(sequence >= 0),
            sha256 TEXT CHECK(sha256 IS NULL OR (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*')),
            version_id TEXT,
            CHECK((sequence = 0) = (generation = 0) AND (sequence = 0) = (sha256 IS NULL) AND (sequence = 0) = (version_id IS NULL))
        ) STRICT;
        CREATE TRIGGER journal_head_forward BEFORE UPDATE ON journal_head
        WHEN NEW.writer_generation < OLD.writer_generation OR NEW.generation < OLD.generation
            OR (NEW.generation = OLD.generation AND NEW.sequence < OLD.sequence)
        BEGIN SELECT RAISE(ABORT, 'the journal head never moves backwards'); END;
        CREATE TRIGGER journal_head_no_delete BEFORE DELETE ON journal_head
        BEGIN SELECT RAISE(ABORT, 'the journal head is permanent'); END;

        CREATE TABLE journal_pending(
            pending_key INTEGER PRIMARY KEY CHECK(pending_key = 1),
            generation INTEGER NOT NULL CHECK(generation >= 1),
            sequence INTEGER NOT NULL CHECK(sequence >= 1),
            sha256 TEXT NOT NULL CHECK(length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
            length INTEGER NOT NULL CHECK(length > 0),
            operation_id TEXT NOT NULL CHECK(length(operation_id) BETWEEN 1 AND 256),
            purpose_group_id TEXT,
            purpose_revision_id TEXT,
            created_unix_ms INTEGER NOT NULL,
            CHECK((purpose_group_id IS NULL) = (purpose_revision_id IS NULL))
        ) STRICT;
        CREATE TRIGGER journal_pending_fixed BEFORE UPDATE ON journal_pending
        BEGIN SELECT RAISE(ABORT, 'a pending segment is replayed with its exact bytes, never changed'); END;

        CREATE TABLE journal_operations(
            operation_id TEXT PRIMARY KEY CHECK(length(operation_id) BETWEEN 1 AND 256),
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            sha256 TEXT NOT NULL CHECK(length(sha256) = 64),
            version_id TEXT NOT NULL,
            UNIQUE(generation, sequence)
        ) STRICT;
        CREATE TRIGGER journal_operations_fixed BEFORE UPDATE ON journal_operations
        BEGIN SELECT RAISE(ABORT, 'a committed operation is permanent'); END;
        CREATE TRIGGER journal_operations_no_delete BEFORE DELETE ON journal_operations
        BEGIN SELECT RAISE(ABORT, 'a committed operation is permanent'); END;

        CREATE TABLE staged_groups(
            group_id TEXT NOT NULL CHECK(length(group_id) BETWEEN 1 AND 128),
            revision_id TEXT NOT NULL CHECK(length(revision_id) BETWEEN 1 AND 128),
            group_kind TEXT NOT NULL CHECK(length(group_kind) BETWEEN 1 AND 64),
            draft_sha256 TEXT NOT NULL CHECK(length(draft_sha256) = 64),
            draft_json TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('staged', 'payloads-qualified', 'manifest-qualified', 'recorded', 'visible', 'abandoned')),
            manifest_sha256 TEXT CHECK(manifest_sha256 IS NULL OR length(manifest_sha256) = 64),
            manifest_length INTEGER CHECK(manifest_length IS NULL OR manifest_length > 0),
            manifest_version_id TEXT,
            manifest_verified_unix_ms INTEGER,
            manifest_put_started_unix_ms INTEGER,
            superseded_manifest_version_id TEXT,
            journal_generation INTEGER,
            journal_sequence INTEGER,
            blocked_code TEXT,
            blocked_detail TEXT,
            staged_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            PRIMARY KEY(group_id, revision_id),
            CHECK((manifest_sha256 IS NULL) = (manifest_length IS NULL)),
            CHECK((manifest_version_id IS NULL) = (manifest_verified_unix_ms IS NULL)),
            CHECK(manifest_version_id IS NULL OR manifest_sha256 IS NOT NULL),
            CHECK(state NOT IN ('manifest-qualified', 'recorded', 'visible') OR manifest_version_id IS NOT NULL),
            CHECK((journal_generation IS NULL) = (journal_sequence IS NULL)),
            CHECK((state IN ('recorded', 'visible')) = (journal_sequence IS NOT NULL))
        ) STRICT;
        CREATE INDEX staged_groups_open ON staged_groups(staged_unix_ms) WHERE state NOT IN ('visible', 'abandoned');
        CREATE TRIGGER staged_groups_draft_fixed BEFORE UPDATE ON staged_groups
        WHEN NEW.group_kind IS NOT OLD.group_kind OR NEW.draft_sha256 IS NOT OLD.draft_sha256
            OR NEW.draft_json IS NOT OLD.draft_json OR NEW.staged_unix_ms IS NOT OLD.staged_unix_ms
            OR (OLD.state IN ('recorded', 'visible', 'abandoned') AND (NEW.manifest_sha256 IS NOT OLD.manifest_sha256
                OR NEW.manifest_version_id IS NOT OLD.manifest_version_id OR NEW.manifest_verified_unix_ms IS NOT OLD.manifest_verified_unix_ms))
            OR (OLD.journal_sequence IS NOT NULL AND (NEW.journal_sequence IS NOT OLD.journal_sequence
                OR NEW.journal_generation IS NOT OLD.journal_generation))
        BEGIN SELECT RAISE(ABORT, 'staged identity is fixed, and manifest bytes and commit position are fixed at the commit'); END;
        CREATE TRIGGER staged_groups_forward BEFORE UPDATE OF state ON staged_groups
        WHEN NEW.state IS NOT OLD.state AND (
            OLD.state IN ('visible', 'abandoned')
            OR (NEW.state = 'abandoned' AND OLD.state = 'recorded')
            OR (NEW.state <> 'abandoned'
                AND NOT (OLD.state = 'manifest-qualified' AND NEW.state = 'payloads-qualified' AND NEW.manifest_sha256 IS NULL)
                AND (CASE NEW.state WHEN 'staged' THEN 0 WHEN 'payloads-qualified' THEN 1 WHEN 'manifest-qualified' THEN 2
                        WHEN 'recorded' THEN 3 ELSE 4 END) <=
                    (CASE OLD.state WHEN 'staged' THEN 0 WHEN 'payloads-qualified' THEN 1 WHEN 'manifest-qualified' THEN 2
                        WHEN 'recorded' THEN 3 ELSE 4 END)))
        BEGIN SELECT RAISE(ABORT, 'publication only moves forward, except to re-qualify a manifest before the commit'); END;
        CREATE TRIGGER staged_groups_no_delete BEFORE DELETE ON staged_groups
        BEGIN SELECT RAISE(ABORT, 'staged publication history is retained'); END;

        CREATE TABLE staged_members(
            group_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            artifact_id TEXT NOT NULL,
            artifact_revision TEXT NOT NULL,
            sha256 TEXT NOT NULL CHECK(length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
            length INTEGER NOT NULL CHECK(length >= 0),
            facts_sha256 TEXT NOT NULL CHECK(length(facts_sha256) = 64),
            source_token TEXT NOT NULL CHECK(length(source_token) BETWEEN 1 AND 1024),
            version_id TEXT,
            verified_unix_ms INTEGER,
            put_started_unix_ms INTEGER,
            released INTEGER NOT NULL DEFAULT 0 CHECK(released IN (0, 1)),
            PRIMARY KEY(group_id, revision_id, ordinal),
            UNIQUE(group_id, revision_id, artifact_id, artifact_revision),
            FOREIGN KEY(group_id, revision_id) REFERENCES staged_groups(group_id, revision_id),
            CHECK((version_id IS NULL) = (verified_unix_ms IS NULL))
        ) STRICT;
        CREATE INDEX staged_members_identity ON staged_members(artifact_id, artifact_revision);
        CREATE INDEX staged_members_unreleased ON staged_members(group_id, revision_id) WHERE released = 0;
        CREATE TRIGGER staged_members_fixed BEFORE UPDATE ON staged_members
        WHEN NEW.artifact_id IS NOT OLD.artifact_id OR NEW.artifact_revision IS NOT OLD.artifact_revision
            OR NEW.sha256 IS NOT OLD.sha256 OR NEW.length IS NOT OLD.length OR NEW.facts_sha256 IS NOT OLD.facts_sha256
            OR NEW.source_token IS NOT OLD.source_token OR NEW.released < OLD.released
            OR ((NEW.version_id IS NOT OLD.version_id OR NEW.verified_unix_ms IS NOT OLD.verified_unix_ms)
                AND (SELECT state FROM staged_groups g WHERE g.group_id = OLD.group_id AND g.revision_id = OLD.revision_id)
                    IN ('recorded', 'visible', 'abandoned'))
        BEGIN SELECT RAISE(ABORT, 'a staged member is fixed, and its named locator is fixed at the commit'); END;

        CREATE TABLE orphan_candidates(
            store_id TEXT NOT NULL,
            bucket TEXT NOT NULL,
            key TEXT NOT NULL,
            version_id TEXT NOT NULL,
            sha256 TEXT NOT NULL CHECK(length(sha256) = 64),
            length INTEGER NOT NULL CHECK(length >= 0),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 256),
            group_id TEXT,
            revision_id TEXT,
            recorded_unix_ms INTEGER NOT NULL,
            PRIMARY KEY(store_id, bucket, key, version_id)
        ) STRICT;
        """;

    private static readonly string[] StateNames =
        ["staged", "payloads-qualified", "manifest-qualified", "recorded", "visible", "abandoned"];

    internal static string StateName(SiloStagedState state) => StateNames[(int)state];

    private static SiloStagedState ParseState(string state) => (SiloStagedState)Array.IndexOf(StateNames, state);

    public string Path { get; } = path;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (new FileInfo(Path + "-wal") is { Exists: true, Length: > 0 })
        {
            // Committed pages still in a WAL are judged on a copy first, so a foreign or newer database is never written.
            await InspectAsync(Path, Format, BindingMetadata(binding), System.IO.Path.GetDirectoryName(Path)!, cancellationToken)
                .ConfigureAwait(false);
        }
        RefuseUnsupportedHeader(Path, Format);
        using var connection = await OpenAsync(Path, cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, Format, BindingMetadata(binding), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO journal_head(head_key, writer_generation, generation, sequence) VALUES(1, 1, 0, 0)
            ON CONFLICT(head_key) DO NOTHING;
            """, cancellationToken).ConfigureAwait(false);
    }

    internal static Dictionary<string, string> BindingMetadata(SiloStorageBinding binding) => new(StringComparer.Ordinal)
    {
        ["instance_id"] = binding.InstanceId,
        ["store_id"] = binding.StoreId,
        ["bucket"] = binding.Bucket,
        ["storage_binding_id"] = binding.BindingId
    };

    public ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken) => OpenAsync(Path, cancellationToken);

    private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();

    private const string Unfinished = "('staged', 'payloads-qualified', 'manifest-qualified')";

    // ----- Journal writer head, pending segment and committed operations ---------------------------------------

    public async ValueTask<SiloJournalHead> ReadHeadAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadHeadAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<SiloJournalHead> ReadHeadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction,
            "SELECT writer_generation, generation, sequence, sha256, version_id FROM journal_head WHERE head_key = 1;");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The Silo journal head is missing.");
        }
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), NullableString(reader, 3), NullableString(reader, 4));
    }

    /// <summary>The committed head and the committed publications still awaiting their first P4.</summary>
    public async ValueTask<SiloWriterEvidence> ReadWriterEvidenceAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var head = await ReadHeadAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var awaiting = new HashSet<(string, string)>();
        using var command = CreateCommand(connection, null, "SELECT group_id, revision_id FROM staged_groups WHERE state = 'recorded';");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            awaiting.Add((reader.GetString(0), reader.GetString(1)));
        }
        return new(head.Position, head.Sha256, awaiting);
    }

    public async ValueTask<SiloPendingSegment?> ReadPendingAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT generation, sequence, sha256, length, operation_id, purpose_group_id, purpose_revision_id
            FROM journal_pending WHERE pending_key = 1;
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(new(reader.GetInt64(0), reader.GetInt64(1)), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
                NullableString(reader, 5), NullableString(reader, 6))
            : null;
    }

    public async ValueTask<SiloCommittedOperation?> ReadOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null,
            "SELECT generation, sequence, sha256, version_id FROM journal_operations WHERE operation_id = $operation;",
            ("$operation", operationId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(operationId, new(reader.GetInt64(0), reader.GetInt64(1)), reader.GetString(2), reader.GetString(3))
            : null;
    }

    /// <summary>
    /// Record the next segment's fixed bytes. Only one segment is pending, and only at the head's successor. A segment
    /// that opens a later generation advances the writer generation in the same transaction, so a crash leaves either
    /// nothing or a pending opening that replays.
    /// </summary>
    public async ValueTask InsertPendingAsync(SiloPendingSegment pending, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var head = await ReadHeadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var opensLater = pending.Position.Sequence == 1 && pending.Position.Generation > head.WriterGeneration;
        var expected = opensLater
            ? pending.Position
            : head.Generation == head.WriterGeneration
                ? new SiloJournalPosition(head.WriterGeneration, head.Sequence + 1)
                : new SiloJournalPosition(head.WriterGeneration, 1);
        if (pending.Position != expected || opensLater && head.Generation != head.WriterGeneration)
        {
            throw new InvalidOperationException("A pending segment must be the head's immediate successor.");
        }
        if (opensLater)
        {
            await ExecuteAsync(connection, transaction, "UPDATE journal_head SET writer_generation = $g WHERE head_key = 1;",
                cancellationToken, ("$g", pending.Position.Generation)).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO journal_pending(pending_key, generation, sequence, sha256, length, operation_id, purpose_group_id,
                purpose_revision_id, created_unix_ms)
            VALUES(1, $g, $s, $sha, $length, $operation, $group, $revision, $now);
            """, cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence),
            ("$sha", pending.Sha256), ("$length", pending.Length), ("$operation", pending.OperationId), ("$group", pending.GroupId),
            ("$revision", pending.RevisionId), ("$now", Now())).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pending segment is committed: in one transaction the head advances to it, its operation is recorded and, for
    /// a publication, the group moves to <c>recorded</c> at that position. A crash leaves all of these or none.
    /// </summary>
    public async ValueTask CommitPendingAsync(SiloPendingSegment pending, string versionId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var deleted = await ExecuteAsync(connection, transaction,
            "DELETE FROM journal_pending WHERE pending_key = 1 AND generation = $g AND sequence = $s AND sha256 = $sha;",
            cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence), ("$sha", pending.Sha256))
            .ConfigureAwait(false);
        if (deleted != 1)
        {
            throw new InvalidOperationException("The pending segment changed before it was committed.");
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE journal_head SET generation = $g, sequence = $s, sha256 = $sha, version_id = $version WHERE head_key = 1;
            INSERT INTO journal_operations(operation_id, generation, sequence, sha256, version_id) VALUES($operation, $g, $s, $sha, $version);
            """, cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence),
            ("$sha", pending.Sha256), ("$version", versionId), ("$operation", pending.OperationId)).ConfigureAwait(false);
        if (pending.GroupId is not null)
        {
            var updated = await ExecuteAsync(connection, transaction, """
                UPDATE staged_groups SET state = 'recorded', journal_generation = $g, journal_sequence = $s, updated_unix_ms = $now
                WHERE group_id = $group AND revision_id = $revision AND state = 'manifest-qualified';
                """, cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence),
                ("$now", Now()), ("$group", pending.GroupId), ("$revision", pending.RevisionId)).ConfigureAwait(false);
            if (updated != 1)
            {
                throw new InvalidOperationException("A publish segment committed for a group that was not manifest-qualified.");
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----- Staged groups --------------------------------------------------------------------------------------

    /// <summary>
    /// Stage a draft once. The same draft again is idempotent. A different draft for the same revision, or an artifact
    /// revision already staged with other bytes or other producer and sidecar facts, is a conflict.
    /// </summary>
    public async ValueTask<bool> StageAsync(SiloGroupDraft draft, string draftJson, string draftSha256, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var existing = await ScalarStringAsync(connection, transaction,
            "SELECT draft_sha256 FROM staged_groups WHERE group_id = $group AND revision_id = $revision;",
            cancellationToken, ("$group", draft.GroupId), ("$revision", draft.RevisionId)).ConfigureAwait(false);
        if (existing is not null)
        {
            return string.Equals(existing, draftSha256, StringComparison.Ordinal)
                ? false
                : throw new SiloConflictException("silo.revision-conflict",
                    $"Group revision {draft.GroupId}/{draft.RevisionId} is already staged with different content.");
        }
        foreach (var member in draft.Members)
        {
            var other = await ScalarStringAsync(connection, transaction, """
                SELECT sha256 FROM staged_members WHERE artifact_id = $artifact AND artifact_revision = $revision
                    AND (sha256 <> $sha OR length <> $length OR facts_sha256 <> $facts) LIMIT 1;
                """, cancellationToken, ("$artifact", member.ArtifactId), ("$revision", member.ArtifactRevision),
                ("$sha", member.Sha256), ("$length", member.Length), ("$facts", member.Facts().Sha256())).ConfigureAwait(false);
            if (other is not null)
            {
                throw new SiloConflictException("silo.artifact-revision-conflict",
                    $"Artifact revision {member.ArtifactId}/{member.ArtifactRevision} is already staged with other bytes or facts.");
            }
        }
        var now = Now();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO staged_groups(group_id, revision_id, group_kind, draft_sha256, draft_json, state, staged_unix_ms, updated_unix_ms)
            VALUES($group, $revision, $kind, $sha, $json, 'staged', $now, $now);
            """, cancellationToken, ("$group", draft.GroupId), ("$revision", draft.RevisionId), ("$kind", draft.GroupKind),
            ("$sha", draftSha256), ("$json", draftJson), ("$now", now)).ConfigureAwait(false);
        for (var ordinal = 0; ordinal < draft.Members.Count; ordinal++)
        {
            var member = draft.Members[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO staged_members(group_id, revision_id, ordinal, artifact_id, artifact_revision, sha256, length, facts_sha256, source_token)
                VALUES($group, $revision, $ordinal, $artifact, $artifactRevision, $sha, $length, $facts, $token);
                """, cancellationToken, ("$group", draft.GroupId), ("$revision", draft.RevisionId), ("$ordinal", ordinal),
                ("$artifact", member.ArtifactId), ("$artifactRevision", member.ArtifactRevision), ("$sha", member.Sha256),
                ("$length", member.Length), ("$facts", member.Facts().Sha256()), ("$token", member.SourceToken)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<SiloStagedGroup?> ReadGroupAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SiloStagedGroup? group;
        using (var command = CreateCommand(connection, null, """
            SELECT draft_json, draft_sha256, state, manifest_sha256, manifest_length, manifest_version_id, manifest_verified_unix_ms,
                journal_generation, journal_sequence, blocked_code, blocked_detail, superseded_manifest_version_id
            FROM staged_groups WHERE group_id = $group AND revision_id = $revision;
            """, ("$group", groupId), ("$revision", revisionId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            var draft = System.Text.Json.JsonSerializer.Deserialize<SiloGroupDraft>(reader.GetString(0), SiloJson.Options)
                ?? throw new InvalidDataException("A staged draft is empty.");
            group = new(draft, reader.GetString(1), ParseState(reader.GetString(2)), [], NullableString(reader, 3),
                NullableLong(reader, 4), NullableString(reader, 5),
                NullableLong(reader, 6) is { } verified ? DateTimeOffset.FromUnixTimeMilliseconds(verified) : null,
                NullableLong(reader, 7) is { } generation ? new SiloJournalPosition(generation, reader.GetInt64(8)) : null,
                NullableString(reader, 9), NullableString(reader, 10), NullableString(reader, 11));
        }
        var members = new List<SiloStagedMember>();
        using (var command = CreateCommand(connection, null, """
            SELECT ordinal, version_id, verified_unix_ms, released FROM staged_members
            WHERE group_id = $group AND revision_id = $revision ORDER BY ordinal;
            """, ("$group", groupId), ("$revision", revisionId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = reader.GetInt32(0);
                members.Add(new(ordinal, group.Draft.Members[ordinal], NullableString(reader, 1),
                    NullableLong(reader, 2) is { } verified ? DateTimeOffset.FromUnixTimeMilliseconds(verified) : null,
                    reader.GetInt64(3) == 1));
            }
        }
        return group with { Members = members };
    }

    /// <summary>Open staged work, oldest first: everything not yet visible or abandoned.</summary>
    public ValueTask<IReadOnlyList<(string GroupId, string RevisionId)>> ListOpenAsync(int limit, CancellationToken cancellationToken) =>
        ListGroupsAsync("""
            SELECT group_id, revision_id FROM staged_groups WHERE state NOT IN ('visible', 'abandoned')
            ORDER BY staged_unix_ms, group_id, revision_id LIMIT $limit;
            """, limit, cancellationToken);

    /// <summary>Visible or abandoned groups that still have members whose spooled bytes were not released.</summary>
    public ValueTask<IReadOnlyList<(string GroupId, string RevisionId)>> ListUnreleasedAsync(int limit, CancellationToken cancellationToken) =>
        ListGroupsAsync("""
            SELECT g.group_id, g.revision_id FROM staged_groups g
            WHERE g.state IN ('visible', 'abandoned')
                AND EXISTS (SELECT 1 FROM staged_members m WHERE m.group_id = g.group_id AND m.revision_id = g.revision_id AND m.released = 0)
            ORDER BY g.updated_unix_ms, g.group_id, g.revision_id LIMIT $limit;
            """, limit, cancellationToken);

    private async ValueTask<IReadOnlyList<(string GroupId, string RevisionId)>> ListGroupsAsync(string sql, int limit, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, sql, ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<(string, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }
        return result;
    }

    /// <summary>Exact versions that staged work still names before its commit, for retention holds (#1970).</summary>
    public async ValueTask<IReadOnlyList<SiloExactLocator>> ListStagedLocatorsAsync(CancellationToken cancellationToken)
    {
        var result = new List<SiloExactLocator>();
        foreach (var (groupId, revisionId) in await ListOpenAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
        {
            var group = await ReadGroupAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false);
            foreach (var member in group!.Members.Where(static member => member.VersionId is not null))
            {
                result.Add(new(binding.StoreId, binding.Bucket, SiloObjectNamespace.PayloadKey(member.Draft.RetentionClass, member.Draft.Sha256),
                    member.VersionId!));
            }
            if (group.ManifestVersionId is { } manifest)
            {
                result.Add(new(binding.StoreId, binding.Bucket,
                    SiloObjectNamespace.ManifestKey(group.Draft.FromUtc, groupId, revisionId), manifest));
            }
        }
        return result;
    }

    /// <summary>Mark that a PUT of this member may land, before it is sent, so an abandon can find what it created.</summary>
    public async ValueTask RecordMemberPutStartedAsync(string groupId, string revisionId, int ordinal, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE staged_members SET put_started_unix_ms = COALESCE(put_started_unix_ms, $now)
            WHERE group_id = $group AND revision_id = $revision AND ordinal = $ordinal;
            """, cancellationToken, ("$now", Now()), ("$group", groupId), ("$revision", revisionId), ("$ordinal", ordinal)).ConfigureAwait(false);
    }

    public async ValueTask RecordManifestPutStartedAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE staged_groups SET manifest_put_started_unix_ms = COALESCE(manifest_put_started_unix_ms, $now)
            WHERE group_id = $group AND revision_id = $revision;
            """, cancellationToken, ("$now", Now()), ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
    }

    /// <summary>Name (or, before the commit, re-name) a member's qualified exact version and its verification time.</summary>
    public async ValueTask SetMemberLocatorAsync(
        string groupId, string revisionId, int ordinal, string versionId, DateTimeOffset verifiedUtc, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(connection, null, $"""
            UPDATE staged_members SET version_id = $version, verified_unix_ms = $verified
            WHERE group_id = $group AND revision_id = $revision AND ordinal = $ordinal
                AND (SELECT state FROM staged_groups g WHERE g.group_id = $group AND g.revision_id = $revision) IN {Unfinished};
            """, cancellationToken, ("$version", versionId), ("$verified", verifiedUtc.ToUnixTimeMilliseconds()),
            ("$group", groupId), ("$revision", revisionId), ("$ordinal", ordinal)).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException($"Group revision {groupId}/{revisionId} is no longer before its commit.");
        }
    }

    public async ValueTask MoveAsync(string groupId, string revisionId, SiloStagedState from, SiloStagedState to, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(connection, null, """
            UPDATE staged_groups SET state = $to, updated_unix_ms = $now, blocked_code = NULL, blocked_detail = NULL
            WHERE group_id = $group AND revision_id = $revision AND state = $from;
            """, cancellationToken, ("$to", StateName(to)), ("$now", Now()), ("$group", groupId), ("$revision", revisionId),
            ("$from", StateName(from))).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException($"Group revision {groupId}/{revisionId} was not {StateName(from)}.");
        }
    }

    public async ValueTask RecordManifestBytesAsync(string groupId, string revisionId, string sha256, long length, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE staged_groups SET manifest_sha256 = $sha, manifest_length = $length, updated_unix_ms = $now
            WHERE group_id = $group AND revision_id = $revision AND state = 'payloads-qualified' AND manifest_sha256 IS NULL;
            """, cancellationToken, ("$sha", sha256), ("$length", length), ("$now", Now()), ("$group", groupId),
            ("$revision", revisionId)).ConfigureAwait(false);
    }

    /// <summary>Name (or, before the commit, re-name) the qualified manifest version; the manifest bytes never change here.</summary>
    public async ValueTask SetManifestVersionAsync(
        string groupId, string revisionId, string versionId, DateTimeOffset verifiedUtc, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(connection, null, """
            UPDATE staged_groups SET manifest_version_id = $version, manifest_verified_unix_ms = $verified,
                state = 'manifest-qualified', updated_unix_ms = $now, blocked_code = NULL, blocked_detail = NULL
            WHERE group_id = $group AND revision_id = $revision AND state IN ('payloads-qualified', 'manifest-qualified')
                AND manifest_sha256 IS NOT NULL;
            """, cancellationToken, ("$version", versionId), ("$verified", verifiedUtc.ToUnixTimeMilliseconds()),
            ("$now", Now()), ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException($"Group revision {groupId}/{revisionId} had no spooled manifest to qualify.");
        }
    }

    /// <summary>
    /// A payload locator changed before the commit, so the manifest that named the old one must be rebuilt. Its version,
    /// if one was qualified, is handed to retention as an orphan candidate and remembered as superseded.
    /// </summary>
    public async ValueTask InvalidateManifestAsync(string groupId, string revisionId, string reason, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        string draftJson;
        string? sha, version;
        long length;
        using (var command = CreateCommand(connection, transaction, $"""
            SELECT draft_json, manifest_sha256, manifest_length, manifest_version_id FROM staged_groups
            WHERE group_id = $group AND revision_id = $revision AND state IN {Unfinished};
            """, ("$group", groupId), ("$revision", revisionId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Group revision {groupId}/{revisionId} is no longer before its commit.");
            }
            (draftJson, sha, length, version) = (reader.GetString(0), NullableString(reader, 1), NullableLong(reader, 2) ?? 0,
                NullableString(reader, 3));
        }
        if (version is not null && sha is not null)
        {
            var draft = System.Text.Json.JsonSerializer.Deserialize<SiloGroupDraft>(draftJson, SiloJson.Options)!;
            await InsertOrphanAsync(connection, transaction, new(new(binding.StoreId, binding.Bucket,
                SiloObjectNamespace.ManifestKey(draft.FromUtc, groupId, revisionId), version), sha, length, reason, groupId, revisionId),
                Now(), cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE staged_groups SET state = CASE WHEN state = 'manifest-qualified' THEN 'payloads-qualified' ELSE state END,
                manifest_sha256 = NULL, manifest_length = NULL, manifest_version_id = NULL, manifest_verified_unix_ms = NULL,
                superseded_manifest_version_id = COALESCE($version, superseded_manifest_version_id), updated_unix_ms = $now
            WHERE group_id = $group AND revision_id = $revision;
            """, cancellationToken, ("$version", version), ("$now", Now()), ("$group", groupId), ("$revision", revisionId))
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Record why the writer stopped for this group. The group stays staged and hidden; nothing is replaced.</summary>
    public async ValueTask BlockAsync(string groupId, string revisionId, string code, string detail, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE staged_groups SET blocked_code = $code, blocked_detail = $detail, updated_unix_ms = $now
            WHERE group_id = $group AND revision_id = $revision;
            """, cancellationToken, ("$code", code), ("$detail", detail.Length > 1024 ? detail[..1024] : detail),
            ("$now", Now()), ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
    }

    public async ValueTask MarkReleasedAsync(string groupId, string revisionId, int ordinal, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE staged_members SET released = 1 WHERE group_id = $group AND revision_id = $revision AND ordinal = $ordinal;
            """, cancellationToken, ("$group", groupId), ("$revision", revisionId), ("$ordinal", ordinal)).ConfigureAwait(false);
    }

    /// <summary>
    /// Abandon staged work before P3 and hand every version it created, or may have created through an uncertain write,
    /// to retention as orphan candidates. A shared, content-addressed version stays a candidate only: retention must still
    /// find no retained reference (#1965 7.3). An unknown version is recorded with an empty version ID.
    /// </summary>
    public async ValueTask AbandonAsync(string groupId, string revisionId, string reason, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        string draftJson;
        string? manifestSha, manifestVersion;
        long manifestLength;
        bool manifestPutStarted;
        using (var command = CreateCommand(connection, transaction, $"""
            SELECT draft_json, manifest_sha256, manifest_length, manifest_version_id, manifest_put_started_unix_ms FROM staged_groups
            WHERE group_id = $group AND revision_id = $revision AND state IN {Unfinished};
            """, ("$group", groupId), ("$revision", revisionId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Group revision {groupId}/{revisionId} is not abandonable staged work.");
            }
            (draftJson, manifestSha, manifestLength, manifestVersion) = (reader.GetString(0), NullableString(reader, 1),
                NullableLong(reader, 2) ?? 0, NullableString(reader, 3));
            manifestPutStarted = !await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false);
        }
        var draft = System.Text.Json.JsonSerializer.Deserialize<SiloGroupDraft>(draftJson, SiloJson.Options)!;
        var now = Now();
        var members = new List<(int Ordinal, string? Version, bool Started)>();
        using (var command = CreateCommand(connection, transaction, """
            SELECT ordinal, version_id, put_started_unix_ms FROM staged_members WHERE group_id = $group AND revision_id = $revision;
            """, ("$group", groupId), ("$revision", revisionId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add((reader.GetInt32(0), NullableString(reader, 1), !await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)));
            }
        }
        foreach (var (ordinal, version, started) in members.Where(static member => member.Version is not null || member.Started))
        {
            var member = draft.Members[ordinal];
            await InsertOrphanAsync(connection, transaction, new(new(binding.StoreId, binding.Bucket,
                    SiloObjectNamespace.PayloadKey(member.RetentionClass, member.Sha256), version ?? UnknownVersion), member.Sha256, member.Length,
                version is null ? "uncertain-write: " + reason : reason, groupId, revisionId), now, cancellationToken).ConfigureAwait(false);
        }
        if (manifestVersion is not null || manifestPutStarted && manifestSha is not null)
        {
            await InsertOrphanAsync(connection, transaction, new(new(binding.StoreId, binding.Bucket,
                    SiloObjectNamespace.ManifestKey(draft.FromUtc, groupId, revisionId), manifestVersion ?? UnknownVersion),
                manifestSha!, manifestLength, manifestVersion is null ? "uncertain-write: " + reason : reason, groupId, revisionId),
                now, cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE staged_groups SET state = 'abandoned', blocked_code = 'abandoned', blocked_detail = $reason, updated_unix_ms = $now
            WHERE group_id = $group AND revision_id = $revision;
            """, cancellationToken, ("$reason", reason), ("$now", now), ("$group", groupId), ("$revision", revisionId))
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RecordOrphanAsync(SiloOrphanCandidate orphan, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await InsertOrphanAsync(connection, null, orphan, Now(), cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask<int> InsertOrphanAsync(
        SqliteConnection connection, SqliteTransaction? transaction, SiloOrphanCandidate orphan, long now, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO orphan_candidates(store_id, bucket, key, version_id, sha256, length, reason, group_id, revision_id, recorded_unix_ms)
            VALUES($store, $bucket, $key, $version, $sha, $length, $reason, $group, $revision, $now)
            ON CONFLICT DO NOTHING;
            """, cancellationToken, ("$store", orphan.Locator.StoreId), ("$bucket", orphan.Locator.Bucket),
            ("$key", orphan.Locator.Key), ("$version", orphan.Locator.VersionId), ("$sha", orphan.Sha256),
            ("$length", orphan.Length), ("$reason", orphan.Reason.Length > 256 ? orphan.Reason[..256] : orphan.Reason),
            ("$group", orphan.GroupId), ("$revision", orphan.RevisionId), ("$now", now));

    public async ValueTask<IReadOnlyList<SiloOrphanCandidate>> ListOrphansAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT store_id, bucket, key, version_id, sha256, length, reason, group_id, revision_id FROM orphan_candidates
            ORDER BY recorded_unix_ms, key, version_id;
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloOrphanCandidate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                reader.GetString(4), reader.GetInt64(5), reader.GetString(6), NullableString(reader, 7), NullableString(reader, 8)));
        }
        return result;
    }

    /// <summary>Spool names still needed: the pending segment and every manifest of work not yet visible or abandoned.</summary>
    public async ValueTask<IReadOnlySet<string>> ReferencedSpoolNamesAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT sha256 FROM journal_pending
            UNION SELECT manifest_sha256 FROM staged_groups
            WHERE manifest_sha256 IS NOT NULL AND state NOT IN ('visible', 'abandoned');
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(SiloObjectSpool.NameOf(reader.GetString(0)));
        }
        return result;
    }
}
