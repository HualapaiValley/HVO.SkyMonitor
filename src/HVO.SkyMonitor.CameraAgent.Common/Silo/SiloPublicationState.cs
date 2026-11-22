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
    string? RecipeIdentitySha256 = null);

/// <summary>
/// The payload spool seam (#1968 owns the spool). It reopens a member's exact staged bytes after a restart and is told
/// when they may be released: only after the group is visible or its staged work was abandoned (#1965 5.2).
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

internal sealed record SiloPendingSegment(SiloJournalPosition Position, string Sha256, long Length, string? GroupId, string? RevisionId);

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
    string? BlockedDetail);

internal sealed record SiloOrphanCandidate(SiloExactLocator Locator, string Sha256, long Length, string Reason, string? GroupId, string? RevisionId);

/// <summary>
/// Non-rebuildable publication state (<c>hvo.cameraagent.silo.publication-state/1</c>). Staged work and orphan
/// cleanup live here, apart from published revisions, and the instance backup covers it.
/// </summary>
internal sealed class SiloPublicationState(string path, SiloStorageBinding binding, TimeProvider clock)
{
    internal const int ApplicationId = 0x48535053; // "HSPS"

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
            purpose_group_id TEXT,
            purpose_revision_id TEXT,
            created_unix_ms INTEGER NOT NULL,
            CHECK((purpose_group_id IS NULL) = (purpose_revision_id IS NULL))
        ) STRICT;
        CREATE TRIGGER journal_pending_fixed BEFORE UPDATE ON journal_pending
        BEGIN SELECT RAISE(ABORT, 'a pending segment is replayed with its exact bytes, never changed'); END;

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
            OR (OLD.manifest_sha256 IS NOT NULL AND NEW.manifest_sha256 IS NOT OLD.manifest_sha256)
            OR (OLD.manifest_version_id IS NOT NULL AND NEW.manifest_version_id IS NOT OLD.manifest_version_id)
            OR (OLD.journal_sequence IS NOT NULL AND (NEW.journal_sequence IS NOT OLD.journal_sequence
                OR NEW.journal_generation IS NOT OLD.journal_generation))
        BEGIN SELECT RAISE(ABORT, 'staged identity, manifest bytes and commit position are fixed'); END;
        CREATE TRIGGER staged_groups_forward BEFORE UPDATE OF state ON staged_groups
        WHEN NEW.state IS NOT OLD.state AND (
            OLD.state IN ('visible', 'abandoned')
            OR (NEW.state = 'abandoned' AND OLD.state = 'recorded')
            OR (NEW.state <> 'abandoned' AND
                (CASE NEW.state WHEN 'staged' THEN 0 WHEN 'payloads-qualified' THEN 1 WHEN 'manifest-qualified' THEN 2
                    WHEN 'recorded' THEN 3 ELSE 4 END) <=
                (CASE OLD.state WHEN 'staged' THEN 0 WHEN 'payloads-qualified' THEN 1 WHEN 'manifest-qualified' THEN 2
                    WHEN 'recorded' THEN 3 ELSE 4 END)))
        BEGIN SELECT RAISE(ABORT, 'publication only moves forward; committed work is never abandoned'); END;
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
            source_token TEXT NOT NULL CHECK(length(source_token) BETWEEN 1 AND 1024),
            version_id TEXT,
            verified_unix_ms INTEGER,
            released INTEGER NOT NULL DEFAULT 0 CHECK(released IN (0, 1)),
            PRIMARY KEY(group_id, revision_id, ordinal),
            UNIQUE(group_id, revision_id, artifact_id, artifact_revision),
            FOREIGN KEY(group_id, revision_id) REFERENCES staged_groups(group_id, revision_id),
            CHECK((version_id IS NULL) = (verified_unix_ms IS NULL))
        ) STRICT;
        CREATE INDEX staged_members_identity ON staged_members(artifact_id, artifact_revision);
        CREATE TRIGGER staged_members_fixed BEFORE UPDATE ON staged_members
        WHEN NEW.artifact_id IS NOT OLD.artifact_id OR NEW.artifact_revision IS NOT OLD.artifact_revision
            OR NEW.sha256 IS NOT OLD.sha256 OR NEW.length IS NOT OLD.length OR NEW.source_token IS NOT OLD.source_token
            OR (OLD.version_id IS NOT NULL AND NEW.version_id IS NOT OLD.version_id) OR NEW.released < OLD.released
        BEGIN SELECT RAISE(ABORT, 'a staged member and its named locator are fixed'); END;

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

    // ----- Journal writer head and pending segment -----------------------------------------------------------

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

    public async ValueTask<SiloPendingSegment?> ReadPendingAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT generation, sequence, sha256, length, purpose_group_id, purpose_revision_id
            FROM journal_pending WHERE pending_key = 1;
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(new(reader.GetInt64(0), reader.GetInt64(1)), reader.GetString(2), reader.GetInt64(3),
                NullableString(reader, 4), NullableString(reader, 5))
            : null;
    }

    /// <summary>Record the next segment's fixed bytes. Only one segment is pending, and only at the head's successor.</summary>
    public async ValueTask InsertPendingAsync(SiloPendingSegment pending, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var head = await ReadHeadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var expected = head.Generation == head.WriterGeneration
            ? new SiloJournalPosition(head.WriterGeneration, head.Sequence + 1)
            : new SiloJournalPosition(head.WriterGeneration, 1);
        if (pending.Position != expected)
        {
            throw new InvalidOperationException("A pending segment must be the head's immediate successor.");
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO journal_pending(pending_key, generation, sequence, sha256, length, purpose_group_id, purpose_revision_id, created_unix_ms)
            VALUES(1, $g, $s, $sha, $length, $group, $revision, $now);
            """, cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence),
            ("$sha", pending.Sha256), ("$length", pending.Length), ("$group", pending.GroupId),
            ("$revision", pending.RevisionId), ("$now", Now())).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pending segment is committed: in one transaction the head advances to it and, for a publication, the group
    /// moves to <c>recorded</c> at that position. A crash on either side leaves both or neither.
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
            """, cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence),
            ("$sha", pending.Sha256), ("$version", versionId)).ConfigureAwait(false);
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

    /// <summary>Advance the writer to a new generation (#1972 opens it after takeover). Its first segment must open it.</summary>
    public async ValueTask AdvanceWriterGenerationAsync(long generation, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var head = await ReadHeadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (generation <= head.WriterGeneration || head.Generation != head.WriterGeneration)
        {
            throw new InvalidOperationException("A new writer generation is above every issued generation and follows a committed open.");
        }
        await ExecuteAsync(connection, transaction, "UPDATE journal_head SET writer_generation = $g WHERE head_key = 1;",
            cancellationToken, ("$g", generation)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----- Staged groups --------------------------------------------------------------------------------------

    /// <summary>Stage a draft once. The same draft again is idempotent; a different draft for the same revision is a conflict.</summary>
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
                    AND (sha256 <> $sha OR length <> $length) LIMIT 1;
                """, cancellationToken, ("$artifact", member.ArtifactId), ("$revision", member.ArtifactRevision),
                ("$sha", member.Sha256), ("$length", member.Length)).ConfigureAwait(false);
            if (other is not null)
            {
                throw new SiloConflictException("silo.artifact-revision-conflict",
                    $"Artifact revision {member.ArtifactId}/{member.ArtifactRevision} is already staged with another digest.");
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
                INSERT INTO staged_members(group_id, revision_id, ordinal, artifact_id, artifact_revision, sha256, length, source_token)
                VALUES($group, $revision, $ordinal, $artifact, $artifactRevision, $sha, $length, $token);
                """, cancellationToken, ("$group", draft.GroupId), ("$revision", draft.RevisionId), ("$ordinal", ordinal),
                ("$artifact", member.ArtifactId), ("$artifactRevision", member.ArtifactRevision), ("$sha", member.Sha256),
                ("$length", member.Length), ("$token", member.SourceToken)).ConfigureAwait(false);
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
                journal_generation, journal_sequence, blocked_code, blocked_detail
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
                NullableString(reader, 9), NullableString(reader, 10));
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
    public async ValueTask<IReadOnlyList<(string GroupId, string RevisionId)>> ListOpenAsync(int limit, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT group_id, revision_id FROM staged_groups WHERE state NOT IN ('visible', 'abandoned')
            ORDER BY staged_unix_ms, group_id, revision_id LIMIT $limit;
            """, ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<(string, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }
        return result;
    }

    /// <summary>Artifact revisions referenced by staged work that is not yet visible or abandoned, for retention holds.</summary>
    public async ValueTask<IReadOnlyList<SiloExactLocator>> ListStagedLocatorsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT m.sha256, m.version_id, g.draft_json, m.ordinal FROM staged_members m
            JOIN staged_groups g ON g.group_id = m.group_id AND g.revision_id = m.revision_id
            WHERE g.state NOT IN ('visible', 'abandoned') AND m.version_id IS NOT NULL;
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloExactLocator>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var draft = System.Text.Json.JsonSerializer.Deserialize<SiloGroupDraft>(reader.GetString(2), SiloJson.Options)!;
            var member = draft.Members[reader.GetInt32(3)];
            result.Add(new(binding.StoreId, binding.Bucket, SiloObjectNamespace.PayloadKey(member.RetentionClass, reader.GetString(0)),
                reader.GetString(1)));
        }
        return result;
    }

    public async ValueTask RecordMemberLocatorAsync(
        string groupId, string revisionId, int ordinal, string versionId, DateTimeOffset verifiedUtc, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE staged_members SET version_id = $version, verified_unix_ms = $verified
            WHERE group_id = $group AND revision_id = $revision AND ordinal = $ordinal AND version_id IS NULL;
            """, cancellationToken, ("$version", versionId), ("$verified", verifiedUtc.ToUnixTimeMilliseconds()),
            ("$group", groupId), ("$revision", revisionId), ("$ordinal", ordinal)).ConfigureAwait(false);
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

    public async ValueTask RecordManifestQualifiedAsync(
        string groupId, string revisionId, string versionId, DateTimeOffset verifiedUtc, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(connection, null, """
            UPDATE staged_groups SET manifest_version_id = $version, manifest_verified_unix_ms = $verified,
                state = 'manifest-qualified', updated_unix_ms = $now, blocked_code = NULL, blocked_detail = NULL
            WHERE group_id = $group AND revision_id = $revision AND state = 'payloads-qualified' AND manifest_sha256 IS NOT NULL;
            """, cancellationToken, ("$version", versionId), ("$verified", verifiedUtc.ToUnixTimeMilliseconds()),
            ("$now", Now()), ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException($"Group revision {groupId}/{revisionId} had no spooled manifest to qualify.");
        }
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
    /// Abandon staged work before P3 and hand every version it created to retention as an orphan candidate. A shared,
    /// content-addressed version stays a candidate only: retention must still find no retained reference (#1965 7.3).
    /// </summary>
    public async ValueTask AbandonAsync(string groupId, string revisionId, string reason, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var draftJson = await ScalarStringAsync(connection, transaction, """
            SELECT draft_json FROM staged_groups WHERE group_id = $group AND revision_id = $revision
                AND state IN ('staged', 'payloads-qualified', 'manifest-qualified');
            """, cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Group revision {groupId}/{revisionId} is not abandonable staged work.");
        var draft = System.Text.Json.JsonSerializer.Deserialize<SiloGroupDraft>(draftJson, SiloJson.Options)!;
        var now = Now();
        using (var command = CreateCommand(connection, transaction, """
            SELECT ordinal, version_id FROM staged_members WHERE group_id = $group AND revision_id = $revision AND version_id IS NOT NULL;
            """, ("$group", groupId), ("$revision", revisionId)))
        {
            var created = new List<(int, string)>();
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    created.Add((reader.GetInt32(0), reader.GetString(1)));
                }
            }
            foreach (var (ordinal, version) in created)
            {
                var member = draft.Members[ordinal];
                await InsertOrphanAsync(connection, transaction, new(new(binding.StoreId, binding.Bucket,
                        SiloObjectNamespace.PayloadKey(member.RetentionClass, member.Sha256), version), member.Sha256, member.Length,
                    reason, groupId, revisionId), now, cancellationToken).ConfigureAwait(false);
            }
        }
        using (var command = CreateCommand(connection, transaction, """
            SELECT manifest_sha256, manifest_length, manifest_version_id FROM staged_groups
            WHERE group_id = $group AND revision_id = $revision AND manifest_version_id IS NOT NULL;
            """, ("$group", groupId), ("$revision", revisionId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var orphan = new SiloOrphanCandidate(new(binding.StoreId, binding.Bucket,
                    SiloObjectNamespace.ManifestKey(draft.FromUtc, groupId, revisionId), reader.GetString(2)),
                    reader.GetString(0), reader.GetInt64(1), reason, groupId, revisionId);
                await reader.DisposeAsync().ConfigureAwait(false);
                await InsertOrphanAsync(connection, transaction, orphan, now, cancellationToken).ConfigureAwait(false);
            }
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
            ("$length", orphan.Length), ("$reason", orphan.Reason), ("$group", orphan.GroupId),
            ("$revision", orphan.RevisionId), ("$now", now));

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
