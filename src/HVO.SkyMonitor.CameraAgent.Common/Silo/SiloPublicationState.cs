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
    void RequireWithinCapacity();

    ValueTask StageAsync(SiloDraftMember member, Func<SiloDraftMember, CancellationToken, ValueTask<Stream>> open,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenAsync(string sourceToken, CancellationToken cancellationToken);

    ValueTask ReleaseAsync(string sourceToken, CancellationToken cancellationToken);

    ValueTask ReconcileAsync(IReadOnlySet<string> claimedTokens, CancellationToken cancellationToken);
}

/// <summary>Publication states (#1965 5.3). Only <see cref="Visible"/> is browsable.</summary>
internal enum SiloStagedState { Staged, PayloadsQualified, ManifestQualified, Recorded, Visible, Abandoned }

internal sealed record SiloJournalHead(long WriterGeneration, long Generation, long Sequence, string? Sha256, string? VersionId)
{
    public SiloJournalPosition? Position => Sequence == 0 ? null : new(Generation, Sequence);
}

/// <summary>
/// A segment whose exact bytes are fixed and spooled, awaiting its create-only write and readback, with what its write
/// attempts proved (#1965 4.5, 5.3). <see cref="AttemptInFlight"/> is a write whose outcome was never recorded, as a
/// crash leaves it; <see cref="MayHaveLanded"/> is sticky once any attempt could have been applied. Only a segment
/// with neither has provably never reached the store. An absent key is never taken as that proof.
/// </summary>
internal sealed record SiloPendingSegment(
    SiloJournalPosition Position, string Sha256, long Length, string OperationId, string OperationSha256, string? GroupId, string? RevisionId,
    bool AttemptInFlight = false, bool MayHaveLanded = false)
{
    public bool ProvablyUnapplied => !AttemptInFlight && !MayHaveLanded;
}

/// <summary>A committed journal operation, found again by its key and the fingerprint of its exact records.</summary>
internal sealed record SiloCommittedOperation(
    string OperationId, SiloJournalPosition Position, string Sha256, string VersionId, string OperationSha256);

internal sealed record SiloFixtureManifestVersion(string Sha256, long Length, string VersionId);

internal sealed record SiloFixtureReceipt(string DraftSha256, SiloStagedState State, string? BlockedCode,
    SiloFixtureManifestVersion? Manifest)
{
    internal HashSet<string> UnreleasedPayloads { get; } = new(StringComparer.Ordinal);
    internal Dictionary<int, string> QualifiedPayloadVersions { get; } = [];
}

internal sealed record SiloFixtureTarget(SiloJournalHead? Head, SiloPendingSegment? Pending,
    IReadOnlyDictionary<string, SiloFixtureReceipt> Receipts);

/// <summary>
/// What the writer's surviving State proves (#1965 4.5, 5.3): the committed head that authenticates the journal tail,
/// and the committed publications whose first P4 has not happened yet, which recovery treats as P3 recovery rather than
/// as settled history.
/// </summary>
internal sealed record SiloWriterEvidence(
    SiloJournalPosition? Head, string? HeadSha256, IReadOnlySet<(string GroupId, string RevisionId)> AwaitingFirstP4,
    ISiloWriterRecords? Records = null)
{
    public static SiloWriterEvidence Empty { get; } = new(null, null, new HashSet<(string, string)>());
}

/// <summary>
/// What the writer's surviving State durably recorded, read on demand so evidence never grows with history (#1965 4.5,
/// 5.3): the exact SHA-256 of every segment it committed, which certifies that segment independently of any other, and
/// whether a publication reached its first P4. Absence of a record proves nothing either way.
/// </summary>
internal interface ISiloWriterRecords
{
    ValueTask<string?> CommittedSha256Async(SiloJournalPosition position, CancellationToken cancellationToken);

    ValueTask<bool> ReachedFirstP4Async(string groupId, string revisionId, CancellationToken cancellationToken);
}

/// <summary>
/// One staged member. <see cref="PutUnresolved"/> is a create-only write of its key that may have landed a version not
/// yet named here: the first write, or a recreation after the named version was lost.
/// </summary>
internal sealed record SiloStagedMember(
    int Ordinal, SiloDraftMember Draft, string? VersionId, DateTimeOffset? VerifiedUtc, bool Released, bool PutUnresolved);

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
    bool ManifestPutUnresolved);

/// <summary>A version that staged work created and no committed record names. An empty version is an unknown one.</summary>
internal sealed record SiloOrphanCandidate(SiloExactLocator Locator, string Sha256, long Length, string Reason, string? GroupId, string? RevisionId);

/// <summary>
/// Non-rebuildable publication state (<c>hvo.cameraagent.silo.publication-state/1</c>). Staged work and orphan
/// cleanup live here, apart from published revisions, and the instance backup covers it.
/// </summary>
internal sealed class SiloPublicationState(string path, SiloStorageBinding binding, TimeProvider clock) : ISiloWriterRecords
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
            operation_sha256 TEXT NOT NULL CHECK(length(operation_sha256) = 64),
            purpose_group_id TEXT,
            purpose_revision_id TEXT,
            attempt_in_flight INTEGER NOT NULL DEFAULT 0 CHECK(attempt_in_flight IN (0, 1)),
            may_have_landed INTEGER NOT NULL DEFAULT 0 CHECK(may_have_landed IN (0, 1)),
            created_unix_ms INTEGER NOT NULL,
            CHECK((purpose_group_id IS NULL) = (purpose_revision_id IS NULL))
        ) STRICT;
        CREATE TRIGGER journal_pending_fixed BEFORE UPDATE ON journal_pending
        WHEN NEW.pending_key IS NOT OLD.pending_key OR NEW.generation IS NOT OLD.generation OR NEW.sequence IS NOT OLD.sequence
            OR NEW.sha256 IS NOT OLD.sha256 OR NEW.length IS NOT OLD.length OR NEW.operation_id IS NOT OLD.operation_id
            OR NEW.operation_sha256 IS NOT OLD.operation_sha256 OR NEW.purpose_group_id IS NOT OLD.purpose_group_id
            OR NEW.purpose_revision_id IS NOT OLD.purpose_revision_id OR NEW.created_unix_ms IS NOT OLD.created_unix_ms
            OR NEW.may_have_landed < OLD.may_have_landed
        BEGIN SELECT RAISE(ABORT, 'a pending segment is replayed with its exact bytes, and a possible landing is never forgotten'); END;

        CREATE TABLE journal_operations(
            operation_id TEXT PRIMARY KEY CHECK(length(operation_id) BETWEEN 1 AND 256),
            generation INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            sha256 TEXT NOT NULL CHECK(length(sha256) = 64),
            version_id TEXT NOT NULL,
            operation_sha256 TEXT NOT NULL CHECK(length(operation_sha256) = 64),
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
            manifest_put_unresolved_unix_ms INTEGER,
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
            put_unresolved_unix_ms INTEGER,
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
        // Only now that the database is proven this instance's own is the inspection scratch a crash left removed. It only
        // ever inspects this one file, so no replacement- or retired-shaped name is its own.
        SiloCatalogFiles.DeleteInspectionScratch([Path]);
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

    internal async ValueTask<SiloFixtureTarget> InspectFixtureTargetAsync(
        string scratchDirectory, IReadOnlySet<string> selectedGroups, CancellationToken cancellationToken)
    {
        SiloJournalHead? head = null;
        SiloPendingSegment? pending = null;
        var receipts = new Dictionary<string, SiloFixtureReceipt>(StringComparer.Ordinal);
        await InspectAsync(Path, Format, BindingMetadata(binding), scratchDirectory, cancellationToken,
            inspect: async (connection, transaction, token) =>
            {
                await RequireFixtureTargetAsync(connection, transaction, selectedGroups, receipts, token).ConfigureAwait(false);
                head = await ReadHeadAsync(connection, transaction, token).ConfigureAwait(false);
                pending = await ReadPendingAsync(connection, transaction, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return new(head, pending, receipts);
    }

    private static async ValueTask RequireFixtureTargetAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlySet<string> selectedGroups, Dictionary<string, SiloFixtureReceipt> receipts, CancellationToken cancellationToken)
    {
        const string otherHistory = "fixture-target-has-other-history";
        using (var command = CreateCommand(connection, transaction, """
                   SELECT group_id, revision_id, group_kind, draft_sha256, state, blocked_code,
                       manifest_sha256, manifest_length, manifest_version_id
                   FROM staged_groups LIMIT $limit;
                   """,
                   ("$limit", SiloFixtureInventory.MaximumRecords + 1)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(2) != SiloFixtureInventory.GroupKind ||
                    reader.GetString(1) != SiloFixtureInventory.GroupRevision || !selectedGroups.Contains(reader.GetString(0)))
                    throw new SiloConflictException(otherHistory, "The target contains work outside the selected fixtures.");
                var state = ParseState(reader.GetString(4));
                var manifest = state is SiloStagedState.ManifestQualified or SiloStagedState.Recorded
                    ? new SiloFixtureManifestVersion(reader.GetString(6), reader.GetInt64(7), reader.GetString(8))
                    : null;
                receipts.Add(reader.GetString(0), new(reader.GetString(3), state, NullableString(reader, 5), manifest));
            }
        }

        using (var command = CreateCommand(connection, transaction, """
                   SELECT group_id, ordinal, source_token, version_id FROM staged_members WHERE released = 0;
                   """))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!receipts.TryGetValue(reader.GetString(0), out var receipt))
                    throw new SiloConflictException(otherHistory, "The target contains payload claims outside the selected fixtures.");
                receipt.UnreleasedPayloads.Add(reader.GetString(2));
                if (receipt.State is not (SiloStagedState.Visible or SiloStagedState.Abandoned) &&
                    NullableString(reader, 3) is { } version)
                    receipt.QualifiedPayloadVersions.Add(reader.GetInt32(1), version);
            }
        }

        var allowed = selectedGroups.Select(static id => SiloPublisher.PublishOperation(id, SiloFixtureInventory.GroupRevision))
            .ToHashSet(StringComparer.Ordinal);
        allowed.Add(SiloJournalWriter.FirstGenerationOpenOperation);
        using var operations = CreateCommand(connection, transaction, """
            SELECT operation_id FROM journal_operations
            UNION ALL SELECT operation_id FROM journal_pending
            LIMIT $limit;
            """, ("$limit", allowed.Count + 1));
        using var operation = await operations.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await operation.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (!allowed.Remove(operation.GetString(0)))
                throw new SiloConflictException(otherHistory, "The target journal contains an operation outside the selected fixtures.");
    }

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
        return new(head.Position, head.Sha256, awaiting, this);
    }

    /// <summary>The exact SHA-256 the writer committed at this position, from its own operation record.</summary>
    public async ValueTask<string?> CommittedSha256Async(SiloJournalPosition position, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarStringAsync(connection, null,
            "SELECT sha256 FROM journal_operations WHERE generation = $g AND sequence = $s;",
            cancellationToken, ("$g", position.Generation), ("$s", position.Sequence)).ConfigureAwait(false);
    }

    internal async ValueTask<SiloCommittedSegment?> ReadCommittedSegmentAsync(
        SiloJournalPosition position, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null,
            "SELECT sha256, version_id FROM journal_operations WHERE generation = $g AND sequence = $s;",
            ("$g", position.Generation), ("$s", position.Sequence));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(position, reader.GetString(0), reader.GetString(1)) : null;
    }

    /// <summary>Whether this writer made the publication visible: its own proof that the first P4 happened.</summary>
    public async ValueTask<bool> ReachedFirstP4Async(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarLongAsync(connection, null,
            "SELECT COUNT(*) FROM staged_groups WHERE group_id = $group AND revision_id = $revision AND state = 'visible';",
            cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false) == 1;
    }

    public async ValueTask<SiloPendingSegment?> ReadPendingAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPendingAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<SiloPendingSegment?> ReadPendingAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT generation, sequence, sha256, length, operation_id, operation_sha256, purpose_group_id, purpose_revision_id,
                attempt_in_flight, may_have_landed
            FROM journal_pending WHERE pending_key = 1;
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(new(reader.GetInt64(0), reader.GetInt64(1)), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
                reader.GetString(5), NullableString(reader, 6), NullableString(reader, 7), reader.GetInt64(8) == 1, reader.GetInt64(9) == 1)
            : null;
    }

    public async ValueTask<SiloCommittedOperation?> ReadOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null,
            "SELECT generation, sequence, sha256, version_id, operation_sha256 FROM journal_operations WHERE operation_id = $operation;",
            ("$operation", operationId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(operationId, new(reader.GetInt64(0), reader.GetInt64(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    /// <summary>
    /// Record, before a write of the pending segment is sent, that one is in flight. An earlier attempt still marked in
    /// flight never recorded its outcome, as a crash leaves it, so it may have landed.
    /// </summary>
    public async ValueTask BeginPendingAttemptAsync(SiloPendingSegment pending, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(connection, null, """
            UPDATE journal_pending SET may_have_landed = MAX(may_have_landed, attempt_in_flight), attempt_in_flight = 1
            WHERE pending_key = 1 AND generation = $g AND sequence = $s AND sha256 = $sha;
            """, cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence), ("$sha", pending.Sha256))
            .ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException("The pending segment changed before its write was sent.");
        }
    }

    /// <summary>Record a write attempt the store answered: definitely not applied, or one that may have landed.</summary>
    public async ValueTask EndPendingAttemptAsync(SiloPendingSegment pending, bool mayHaveLanded, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE journal_pending SET attempt_in_flight = 0, may_have_landed = MAX(may_have_landed, $landed)
            WHERE pending_key = 1 AND generation = $g AND sequence = $s AND sha256 = $sha;
            """, cancellationToken, ("$landed", mayHaveLanded ? 1 : 0), ("$g", pending.Position.Generation),
            ("$s", pending.Position.Sequence), ("$sha", pending.Sha256)).ConfigureAwait(false);
    }

    /// <summary>
    /// Withdraw a publication's pending segment that provably never reached the store, so its group is requalified from
    /// its staged inputs before its first actual commit (#1965 5.3). The sequence was never committed, so the next
    /// segment takes it with whatever bytes it then has. Nothing that may have landed is ever withdrawn.
    /// </summary>
    public async ValueTask<bool> WithdrawPendingAsync(SiloPendingSegment pending, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(connection, null, """
            DELETE FROM journal_pending WHERE pending_key = 1 AND generation = $g AND sequence = $s AND sha256 = $sha
                AND attempt_in_flight = 0 AND may_have_landed = 0 AND purpose_group_id IS NOT NULL;
            """, cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence), ("$sha", pending.Sha256))
            .ConfigureAwait(false) == 1;
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
            INSERT INTO journal_pending(pending_key, generation, sequence, sha256, length, operation_id, operation_sha256, purpose_group_id,
                purpose_revision_id, created_unix_ms)
            VALUES(1, $g, $s, $sha, $length, $operation, $fingerprint, $group, $revision, $now);
            """, cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence),
            ("$sha", pending.Sha256), ("$length", pending.Length), ("$operation", pending.OperationId),
            ("$fingerprint", pending.OperationSha256), ("$group", pending.GroupId), ("$revision", pending.RevisionId), ("$now", Now()))
            .ConfigureAwait(false);
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
            INSERT INTO journal_operations(operation_id, generation, sequence, sha256, version_id, operation_sha256)
            VALUES($operation, $g, $s, $sha, $version, $fingerprint);
            """, cancellationToken, ("$g", pending.Position.Generation), ("$s", pending.Position.Sequence),
            ("$sha", pending.Sha256), ("$version", versionId), ("$operation", pending.OperationId),
            ("$fingerprint", pending.OperationSha256)).ConfigureAwait(false);
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
                journal_generation, journal_sequence, blocked_code, blocked_detail, manifest_put_unresolved_unix_ms IS NOT NULL
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
                NullableString(reader, 9), NullableString(reader, 10), reader.GetInt64(11) == 1);
        }
        var members = new List<SiloStagedMember>();
        using (var command = CreateCommand(connection, null, """
            SELECT ordinal, version_id, verified_unix_ms, released, put_unresolved_unix_ms IS NOT NULL FROM staged_members
            WHERE group_id = $group AND revision_id = $revision ORDER BY ordinal;
            """, ("$group", groupId), ("$revision", revisionId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = reader.GetInt32(0);
                members.Add(new(ordinal, group.Draft.Members[ordinal], NullableString(reader, 1),
                    NullableLong(reader, 2) is { } verified ? DateTimeOffset.FromUnixTimeMilliseconds(verified) : null,
                    reader.GetInt64(3) == 1, reader.GetInt64(4) == 1));
            }
        }
        return group with { Members = members };
    }

    /// <summary>Open staged work, oldest first: everything not yet visible or abandoned.</summary>
    public ValueTask<IReadOnlyList<(string GroupId, string RevisionId)>> ListOpenAsync(int limit, CancellationToken cancellationToken) =>
        ListOpenAsync(limit, skipRawAdmissions: false, cancellationToken);

    internal ValueTask<IReadOnlyList<(string GroupId, string RevisionId)>> ListOpenAsync(
        int limit, bool skipRawAdmissions, CancellationToken cancellationToken) =>
        ListGroupsAsync("""
            SELECT group_id, revision_id FROM staged_groups WHERE state NOT IN ('visible', 'abandoned')
                AND ($skip_raw = 0 OR group_kind <> 'raw-capture')
            ORDER BY staged_unix_ms, group_id, revision_id LIMIT $limit;
            """, limit, cancellationToken, ("$skip_raw", skipRawAdmissions));

    /// <summary>Visible or abandoned groups that still have members whose spooled bytes were not released.</summary>
    public ValueTask<IReadOnlyList<(string GroupId, string RevisionId)>> ListUnreleasedAsync(int limit, CancellationToken cancellationToken) =>
        ListGroupsAsync("""
            SELECT g.group_id, g.revision_id
            FROM (SELECT DISTINCT group_id, revision_id FROM staged_members WHERE released = 0) pending
            JOIN staged_groups g USING (group_id, revision_id)
            WHERE g.state IN ('visible', 'abandoned')
            ORDER BY g.updated_unix_ms, g.group_id, g.revision_id LIMIT $limit;
            """, limit, cancellationToken);

    private async ValueTask<IReadOnlyList<(string GroupId, string RevisionId)>> ListGroupsAsync(string sql, int limit,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, sql, [("$limit", limit), .. parameters]);
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
                    SiloObjectNamespace.ManifestKey(group.Draft.FromUtc, groupId, revisionId, group.ManifestSha256!), manifest));
            }
        }
        return result;
    }

    /// <summary>
    /// Mark, before a PUT of this member is sent, that it may land a version not yet named here, so an abandonment or a
    /// supersession can hand that version over. Storing the version it produced resolves the mark.
    /// </summary>
    public async ValueTask MarkMemberPutUnresolvedAsync(string groupId, string revisionId, int ordinal, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE staged_members SET put_unresolved_unix_ms = $now
            WHERE group_id = $group AND revision_id = $revision AND ordinal = $ordinal;
            """, cancellationToken, ("$now", Now()), ("$group", groupId), ("$revision", revisionId), ("$ordinal", ordinal)).ConfigureAwait(false);
    }

    /// <summary>The same mark for a manifest PUT, including a recreation under the same digest key.</summary>
    public async ValueTask MarkManifestPutUnresolvedAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE staged_groups SET manifest_put_unresolved_unix_ms = $now WHERE group_id = $group AND revision_id = $revision;
            """, cancellationToken, ("$now", Now()), ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
    }

    /// <summary>
    /// Name a member's first qualified exact version, or record a fresh verification of the version it already names.
    /// A different version replaces the locator only through <see cref="ReplaceMemberLocatorAsync"/>.
    /// </summary>
    public async ValueTask SetMemberLocatorAsync(
        string groupId, string revisionId, int ordinal, string versionId, DateTimeOffset verifiedUtc, bool resolvesPut,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(connection, null, $"""
            UPDATE staged_members SET version_id = $version, verified_unix_ms = $verified,
                put_unresolved_unix_ms = CASE WHEN $resolves = 1 THEN NULL ELSE put_unresolved_unix_ms END
            WHERE group_id = $group AND revision_id = $revision AND ordinal = $ordinal AND (version_id IS NULL OR version_id = $version)
                AND (SELECT state FROM staged_groups g WHERE g.group_id = $group AND g.revision_id = $revision) IN {Unfinished};
            """, cancellationToken, ("$version", versionId), ("$verified", verifiedUtc.ToUnixTimeMilliseconds()),
            ("$resolves", resolvesPut ? 1 : 0), ("$group", groupId), ("$revision", revisionId), ("$ordinal", ordinal)).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException(
                $"Member {ordinal} of {groupId}/{revisionId} is no longer before its commit, or already names another version.");
        }
    }

    /// <summary>
    /// Replace a member's lost qualified version with the new one recreated from its spooled bytes (#1965 5.3, P1 and P2
    /// recovery). One transaction re-names the member and, when a manifest was already built from the old locator,
    /// supersedes it: every version of that never-committed manifest is handed to retention as an orphan candidate under
    /// its own digest key, the recorded one exactly and an unresolved write as its resolved exact version or, when none
    /// was resolved, the key itself. The group returns to <c>payloads-qualified</c>, so P2 writes a new manifest under its
    /// own digest key. The superseded version is never overwritten or deleted here. An uncertain commit of this revision
    /// is reconciled first, so this never runs beside a pending P3.
    /// </summary>
    /// <returns>The orphan candidates recorded for the superseded manifest; empty when none was built.</returns>
    public async ValueTask<IReadOnlyList<SiloOrphanCandidate>> ReplaceMemberLocatorAsync(
        string groupId, string revisionId, int ordinal, string expectedVersionId, string versionId, DateTimeOffset verifiedUtc,
        string? resolvedManifestVersionId, CancellationToken cancellationToken)
    {
        if (string.Equals(expectedVersionId, versionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A replacement names a different version.", nameof(versionId));
        }
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        if (await ScalarLongAsync(connection, transaction, """
                SELECT COUNT(*) FROM journal_pending WHERE purpose_group_id = $group AND purpose_revision_id = $revision;
                """, cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false) != 0)
        {
            throw new InvalidOperationException(
                $"An uncertain commit of {groupId}/{revisionId} is reconciled with its exact bytes before any locator changes.");
        }
        var manifest = await ReadUnfinishedManifestAsync(connection, transaction, groupId, revisionId, cancellationToken).ConfigureAwait(false);
        var replaced = await ExecuteAsync(connection, transaction, """
            UPDATE staged_members SET version_id = $version, verified_unix_ms = $verified, put_unresolved_unix_ms = NULL
            WHERE group_id = $group AND revision_id = $revision AND ordinal = $ordinal AND version_id = $expected;
            """, cancellationToken, ("$version", versionId), ("$verified", verifiedUtc.ToUnixTimeMilliseconds()), ("$group", groupId),
            ("$revision", revisionId), ("$ordinal", ordinal), ("$expected", expectedVersionId)).ConfigureAwait(false);
        if (replaced != 1)
        {
            throw new InvalidOperationException($"Member {ordinal} of {groupId}/{revisionId} no longer names {expectedVersionId}.");
        }
        var orphans = new List<SiloOrphanCandidate>();
        if (manifest.Sha256 is { } sha)
        {
            var key = SiloObjectNamespace.ManifestKey(manifest.Draft.FromUtc, groupId, revisionId, sha);
            orphans.AddRange(Handoff(key, sha, manifest.Length, manifest.VersionId, manifest.PutUnresolved, resolvedManifestVersionId,
                "superseded before the commit by a replaced payload version", groupId, revisionId));
            foreach (var orphan in orphans)
            {
                await InsertOrphanAsync(connection, transaction, orphan, Now(), cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAsync(connection, transaction, """
                UPDATE staged_groups SET state = CASE WHEN state = 'manifest-qualified' THEN 'payloads-qualified' ELSE state END,
                    manifest_sha256 = NULL, manifest_length = NULL, manifest_version_id = NULL, manifest_verified_unix_ms = NULL,
                    manifest_put_unresolved_unix_ms = NULL, updated_unix_ms = $now
                WHERE group_id = $group AND revision_id = $revision;
                """, cancellationToken, ("$now", Now()), ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return orphans;
    }

    private sealed record UnfinishedManifest(SiloGroupDraft Draft, string? Sha256, long Length, string? VersionId, bool PutUnresolved);

    private static async ValueTask<UnfinishedManifest> ReadUnfinishedManifestAsync(
        SqliteConnection connection, SqliteTransaction transaction, string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, $"""
            SELECT draft_json, manifest_sha256, manifest_length, manifest_version_id, manifest_put_unresolved_unix_ms FROM staged_groups
            WHERE group_id = $group AND revision_id = $revision AND state IN {Unfinished};
            """, ("$group", groupId), ("$revision", revisionId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Group revision {groupId}/{revisionId} is no longer before its commit.");
        }
        return new(System.Text.Json.JsonSerializer.Deserialize<SiloGroupDraft>(reader.GetString(0), SiloJson.Options)!,
            NullableString(reader, 1), NullableLong(reader, 2) ?? 0, NullableString(reader, 3),
            !await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The versions of one key that staged work hands to retention: the version it recorded, exactly, and for a write
    /// that may have landed a version it never recorded, that version once resolved, or otherwise the key itself.
    /// </summary>
    private List<SiloOrphanCandidate> Handoff(
        string key, string sha256, long length, string? recorded, bool unresolved, string? resolved, string reason, string groupId,
        string revisionId)
    {
        var result = new List<SiloOrphanCandidate>();
        if (recorded is not null)
        {
            result.Add(new(new(binding.StoreId, binding.Bucket, key, recorded), sha256, length, reason, groupId, revisionId));
        }
        if (unresolved && (resolved is null || !string.Equals(resolved, recorded, StringComparison.Ordinal)))
        {
            result.Add(new(new(binding.StoreId, binding.Bucket, key, resolved ?? UnknownVersion), sha256, length,
                resolved is null ? "uncertain-write: " + reason : reason, groupId, revisionId));
        }
        return result;
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
        string groupId, string revisionId, string versionId, DateTimeOffset verifiedUtc, bool resolvesPut, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(connection, null, """
            UPDATE staged_groups SET manifest_version_id = $version, manifest_verified_unix_ms = $verified,
                manifest_put_unresolved_unix_ms = CASE WHEN $resolves = 1 THEN NULL ELSE manifest_put_unresolved_unix_ms END,
                state = 'manifest-qualified', updated_unix_ms = $now, blocked_code = NULL, blocked_detail = NULL
            WHERE group_id = $group AND revision_id = $revision AND state IN ('payloads-qualified', 'manifest-qualified')
                AND manifest_sha256 IS NOT NULL;
            """, cancellationToken, ("$version", versionId), ("$verified", verifiedUtc.ToUnixTimeMilliseconds()),
            ("$resolves", resolvesPut ? 1 : 0), ("$now", Now()), ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
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

    /// <summary>Whether another unsettled release still owns these staged bytes. Only unreleased rows are searched.</summary>
    public async ValueTask<bool> HasOtherPayloadOwnerAsync(
        string groupId, string revisionId, SiloStagedMember member, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT 1 FROM staged_members
            WHERE released = 0 AND source_token = $token AND (group_id, revision_id, ordinal) <> ($group, $revision, $ordinal)
            LIMIT 1;
            """, ("$token", member.Draft.SourceToken), ("$group", groupId), ("$revision", revisionId), ("$ordinal", member.Ordinal));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async ValueTask<IReadOnlySet<string>> ListClaimedPayloadTokensAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, "SELECT DISTINCT source_token FROM staged_members WHERE released = 0;");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tokens.Add(reader.GetString(0));
        }
        return tokens;
    }

    /// <summary>
    /// Abandon staged work before P3 and hand every version it created, or may have created through an uncertain write,
    /// to retention as orphan candidates: each recorded version exactly, and each unresolved write as the exact version
    /// the caller resolved at its key or, when none was resolved, the key itself with an empty version ID. A shared,
    /// content-addressed version stays a candidate only: retention must still find no retained reference (#1965 7.3).
    /// </summary>
    public async ValueTask AbandonAsync(
        string groupId, string revisionId, string reason, IReadOnlyDictionary<string, string> resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        UnfinishedManifest manifest;
        try
        {
            manifest = await ReadUnfinishedManifestAsync(connection, transaction, groupId, revisionId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"Group revision {groupId}/{revisionId} is not abandonable staged work.", exception);
        }
        var draft = manifest.Draft;
        var now = Now();
        var members = new List<(int Ordinal, string? Version, bool Unresolved)>();
        using (var command = CreateCommand(connection, transaction, """
            SELECT ordinal, version_id, put_unresolved_unix_ms FROM staged_members WHERE group_id = $group AND revision_id = $revision;
            """, ("$group", groupId), ("$revision", revisionId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add((reader.GetInt32(0), NullableString(reader, 1), !await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)));
            }
        }
        var orphans = new List<SiloOrphanCandidate>();
        foreach (var (ordinal, version, unresolved) in members)
        {
            var member = draft.Members[ordinal];
            var key = SiloObjectNamespace.PayloadKey(member.RetentionClass, member.Sha256);
            orphans.AddRange(Handoff(key, member.Sha256, member.Length, version, unresolved, resolved.GetValueOrDefault(key), reason,
                groupId, revisionId));
        }
        if (manifest.Sha256 is { } sha)
        {
            var key = SiloObjectNamespace.ManifestKey(draft.FromUtc, groupId, revisionId, sha);
            orphans.AddRange(Handoff(key, sha, manifest.Length, manifest.VersionId, manifest.PutUnresolved, resolved.GetValueOrDefault(key),
                reason, groupId, revisionId));
        }
        foreach (var orphan in orphans)
        {
            await InsertOrphanAsync(connection, transaction, orphan, now, cancellationToken).ConfigureAwait(false);
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

    internal async ValueTask ResolveRetentionOrphanAsync(SiloOrphanCandidate orphan, string version, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        await InsertOrphanAsync(connection, transaction, orphan with { Locator = orphan.Locator with { VersionId = version } },
            clock.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM orphan_candidates WHERE store_id = $store AND bucket = $bucket AND key = $key AND version_id = '';
            """, cancellationToken, ("$store", orphan.Locator.StoreId), ("$bucket", orphan.Locator.Bucket), ("$key", orphan.Locator.Key))
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask RemoveRetentionOrphanAsync(SiloExactLocator locator, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            DELETE FROM orphan_candidates WHERE store_id = $store AND bucket = $bucket AND key = $key AND version_id = $version;
            """, cancellationToken, ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key),
            ("$version", locator.VersionId)).ConfigureAwait(false);
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
