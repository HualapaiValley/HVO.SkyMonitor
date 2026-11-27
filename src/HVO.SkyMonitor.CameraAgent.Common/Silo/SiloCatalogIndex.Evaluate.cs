using System.Text;
using Microsoft.Data.Sqlite;
using static HVO.SkyMonitor.CameraAgent.Common.Silo.SiloSqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// <see cref="Startup"/> is P3 recovery (#1965 5.3): the manifest and every required member must verify by exact read
/// before P4. <see cref="Rebuild"/> applies to settled history after catalog loss: each manifest is verified and member
/// roles are left unverified for the lazy phase (#1965 7.5). A P3 the writer knows has not reached its first P4 is
/// always recovered in <see cref="Startup"/> mode, even during a rebuild.
/// </summary>
internal enum SiloEvaluationMode { Startup, Rebuild }

/// <summary>
/// Exact-version verifications made during one operation. Each proves one exact version held the length and SHA-256 an
/// object was required to have, and is reused only for that same exact expectation: a verified manifest, or another
/// member read through the same version, never stands in for a different role. They let the same operation avoid
/// repeating its own GETs, and they never outlive it: the next operation reads the bytes again.
/// </summary>
internal sealed class SiloFreshEvidence
{
    private readonly Dictionary<(SiloExactLocator Locator, long Length, string Sha256), (DateTimeOffset When, byte[]? Bytes)> _verified = new();

    public void Verified(SiloExactLocator locator, long length, string sha256, DateTimeOffset when, byte[]? bytes = null)
    {
        var key = (locator, length, sha256);
        _verified[key] = (when, bytes ?? (_verified.TryGetValue(key, out var existing) ? existing.Bytes : null));
    }

    public bool TryGet(SiloExactLocator locator, long length, string sha256, out DateTimeOffset when, out byte[]? bytes)
    {
        if (_verified.TryGetValue((locator, length, sha256), out var value))
        {
            (when, bytes) = value;
            return true;
        }
        (when, bytes) = (default, null);
        return false;
    }
}

internal sealed record SiloReopenItem(
    SiloRoleKey Role, string Sha256, bool Required, string Source, string Disposition, SiloExactLocator? ServedFrom, string? Detail)
{
    internal bool Damaged { get; init; }
}

/// <summary>
/// Whether a revision reopens exactly, which required role blocks it, and what else is reported (#1965 6.1). Every
/// disposition and served locator comes from exact reads made by this reopen.
/// </summary>
internal sealed record SiloReopenResult(
    string GroupId, string RevisionId, bool Found, bool Reopenable, string? ClosedOutcome, IReadOnlyList<SiloReopenItem> Items,
    string? ManifestJson)
{
    public IEnumerable<SiloReopenItem> Blocking => Items.Where(static item => item.Required && item.Disposition != SiloDisposition.Available);
}

internal sealed partial class SiloCatalogIndex
{
    internal const string LinkMismatch = "link-mismatch";

    private static readonly string[] OutcomeSeverity =
    [
        SiloDisposition.TemporarilyUnavailable, SiloDisposition.Unverified, SiloDisposition.Missing,
        SiloDisposition.PolicyExpired, SiloDisposition.Conflict
    ];

    /// <summary>
    /// Evaluate publications awaiting P4 in journal order: authority is given (only authenticated records were applied),
    /// then identity, then exact bytes through an eligible locator, each row in its own recovery mode. With
    /// <paramref name="retryClosed"/> a retryable closed revision is evaluated again; a later success brings the same P3
    /// revision to P4 with no loss recorded.
    /// </summary>
    public async ValueTask<int> EvaluatePublicationsAsync(
        SiloStoreSet stores, SiloFreshEvidence? fresh, bool retryClosed, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var candidates = new List<(string Group, string Revision, bool RequiresMembers)>();
            using (var command = CreateCommand(connection, null, """
                SELECT group_id, revision_id, requires_members FROM publications
                WHERE state = 'pending' OR ($retry = 1 AND state = 'closed'
                    AND outcome IN ('temporarily-unavailable', 'unverified', 'missing') AND manifest_status <> 'conflict')
                ORDER BY generation, sequence, record_index;
                """, ("$retry", retryClosed ? 1 : 0)))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1));
                }
            }
            fresh ??= new SiloFreshEvidence();
            foreach (var (group, revision, requiresMembers) in candidates)
            {
                await EvaluateLockedAsync(connection, stores, group, revision,
                    requiresMembers ? SiloEvaluationMode.Startup : SiloEvaluationMode.Rebuild, fresh, cancellationToken).ConfigureAwait(false);
            }
            return candidates.Count;
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// After a rebuild applies the journal, decide each pending publication's footing (#1965 5.3, 7.5). Settled history
    /// is rebuilt from its verified manifest, its member bytes read lazily. A publication is settled only on positive
    /// proof that it reached P4: the writer's own records, or the trusted anchor the rebuild started from, which records
    /// each covered publication proven to have reached P4 when it was written. The writer's list of revisions awaiting their first
    /// P4 overrides both. Any other publication, including one with no proof either way, is verified exactly as a first
    /// P4 would be: its manifest, required members and required reopen dependencies by exact read, and it becomes visible
    /// only on that proof. Absence of proof is not evidence of settlement. A read that cannot complete becomes a reported
    /// state, never a stalled rebuild.
    /// </summary>
    internal async ValueTask MarkHistoryAsync(
        SiloWriterEvidence? writer, IReadOnlyList<SiloCheckpointPublication>? anchoredP4, CancellationToken cancellationToken)
    {
        var awaitingFirstP4 = writer?.AwaitingFirstP4 ?? SiloWriterEvidence.Empty.AwaitingFirstP4;
        var anchored = (anchoredP4 ?? []).Select(static publication => (publication.GroupId, publication.RevisionId)).ToHashSet();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var pending = new List<(string, string)>();
            using (var command = CreateCommand(connection, null, "SELECT group_id, revision_id FROM publications WHERE state = 'pending';"))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    pending.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
            var settled = new List<(string, string)>();
            foreach (var pair in pending.Where(pair => !awaitingFirstP4.Contains(pair)))
            {
                if (anchored.Contains(pair) ||
                    writer?.Records is { } records && await records.ReachedFirstP4Async(pair.Item1, pair.Item2, cancellationToken).ConfigureAwait(false))
                {
                    settled.Add(pair);
                }
            }
            using var transaction = BeginImmediate(connection);
            foreach (var (group, revision) in settled)
            {
                await ExecuteAsync(connection, transaction,
                    "UPDATE publications SET requires_members = 0 WHERE group_id = $group AND revision_id = $revision;",
                    cancellationToken, ("$group", group), ("$revision", revision)).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// The writer's own first P4: P3 recovery of this revision now, whatever state a read or a rebuild left it in. Only
    /// <c>visible</c> after this call lets the writer release its spooled bytes.
    /// </summary>
    public async ValueTask<string?> ConfirmP4Async(
        SiloStoreSet stores, string groupId, string revisionId, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var state = await ScalarStringAsync(connection, null,
                "SELECT state || '|' || manifest_status FROM publications WHERE group_id = $group AND revision_id = $revision;",
                cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
            if (state is null)
            {
                return null;
            }
            if (!state.EndsWith("|conflict", StringComparison.Ordinal) && !state.EndsWith("|policy-expired", StringComparison.Ordinal))
            {
                await ExecuteAsync(connection, null,
                    "UPDATE publications SET requires_members = 1 WHERE group_id = $group AND revision_id = $revision;",
                    cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
                await EvaluateLockedAsync(connection, stores, groupId, revisionId, SiloEvaluationMode.Startup, fresh, cancellationToken)
                    .ConfigureAwait(false);
            }
            return await ScalarStringAsync(connection, null,
                "SELECT state FROM publications WHERE group_id = $group AND revision_id = $revision;",
                cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    private async ValueTask EvaluateLockedAsync(
        SqliteConnection connection, SiloStoreSet stores, string groupId, string revisionId, SiloEvaluationMode mode,
        SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        string kind, manifestSha;
        string? previous;
        long manifestLength;
        using (var command = CreateCommand(connection, null, """
            SELECT group_kind, previous_revision_id, manifest_sha256, manifest_length FROM publications
            WHERE group_id = $group AND revision_id = $revision;
            """, ("$group", groupId), ("$revision", revisionId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            (kind, previous, manifestSha, manifestLength) = (reader.GetString(0), NullableString(reader, 1), reader.GetString(2), reader.GetInt64(3));
        }
        var recorded = await ReadRecordedMembersAsync(connection, groupId, revisionId, cancellationToken).ConfigureAwait(false);
        var manifestRole = new SiloRoleKey(SiloIdentityKind.Manifest, groupId, revisionId);
        var (manifestDisposition, bytes, _, _) = await ProbeRoleAsync(connection, stores, manifestRole, manifestSha, manifestLength,
            captureBytes: true, fresh, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            await CloseAsync(connection, groupId, revisionId, manifestDisposition, manifestDisposition,
                "the manifest did not verify through any eligible locator", cancellationToken).ConfigureAwait(false);
            return;
        }
        SiloGroupManifest manifest;
        try
        {
            manifest = SiloJson.ReadManifest(bytes, $"manifest {groupId}/{revisionId}");
        }
        catch (SiloInvalidObjectException exception)
        {
            await CloseAsync(connection, groupId, revisionId, SiloDisposition.Conflict, SiloDisposition.Conflict, exception.Message,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        var namedKey = await ScalarStringAsync(connection, null, """
            SELECT key FROM locators WHERE kind = $kind AND id = $group AND revision = $revision AND source = 'named'
            ORDER BY generation, sequence, record_index LIMIT 1;
            """, cancellationToken, ("$kind", KindName(SiloIdentityKind.Manifest)), ("$group", groupId), ("$revision", revisionId))
            .ConfigureAwait(false);
        if (IdentityMismatch(manifest, kind, groupId, revisionId, previous, manifestSha, namedKey, recorded) is { } mismatch)
        {
            await CloseAsync(connection, groupId, revisionId, SiloDisposition.Conflict, SiloDisposition.Conflict, mismatch,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        await ProjectManifestAsync(connection, manifest, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false);
        if (mode == SiloEvaluationMode.Rebuild)
        {
            await SetVisibleAsync(connection, groupId, revisionId, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Check 3 reads every required member's exact bytes in this operation. An earlier verification of the same
        // content, for example through another group, is not evidence that the version survives now.
        var outcomes = new List<string>();
        foreach (var member in recorded.Where(static member => member.Required))
        {
            var role = new SiloRoleKey(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision);
            var (current, _, _, _) = await ProbeRoleAsync(connection, stores, role, member.Sha256, member.Length, captureBytes: false,
                fresh, cancellationToken).ConfigureAwait(false);
            if (current != SiloDisposition.Available)
            {
                outcomes.Add(current);
            }
        }
        // First visibility also proves every required reopen dependency (#1965 6.1): an authenticated publication of that
        // exact artifact revision, and its bytes read now through an eligible locator. Without a proven publication the
        // revision is not concluded; it is evaluated again when that publication or a copy of it arrives. An optional
        // dependency never blocks.
        foreach (var dependency in await ReadRequiredReopenDependenciesAsync(connection, groupId, revisionId, cancellationToken)
            .ConfigureAwait(false))
        {
            var role = new SiloRoleKey(SiloIdentityKind.Member, dependency.ArtifactId, dependency.ArtifactRevision);
            if (await ReadRoleIdentityAsync(connection, null, role, cancellationToken).ConfigureAwait(false) is not { } published)
            {
                outcomes.Add(SiloDisposition.Unverified);
                continue;
            }
            if (!string.Equals(published.Sha256, dependency.Sha256, StringComparison.Ordinal))
            {
                outcomes.Add(SiloDisposition.Conflict);
                continue;
            }
            var (current, _, _, _) = await ProbeRoleAsync(connection, stores, role, dependency.Sha256, published.Length, captureBytes: false,
                fresh, cancellationToken).ConfigureAwait(false);
            if (current != SiloDisposition.Available)
            {
                outcomes.Add(current);
            }
        }
        if (outcomes.Count == 0)
        {
            await SetVisibleAsync(connection, groupId, revisionId, cancellationToken).ConfigureAwait(false);
            return;
        }
        var worst = outcomes.MaxBy(static outcome => Array.IndexOf(OutcomeSeverity, outcome))!;
        await CloseAsync(connection, groupId, revisionId, SiloDisposition.Verified, worst,
            "a required member or reopen dependency did not verify through an authenticated publication and eligible locator",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<(string ArtifactId, string ArtifactRevision, string Sha256)>> ReadRequiredReopenDependenciesAsync(
        SqliteConnection connection, string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, null, """
            SELECT artifact_id, artifact_revision, sha256 FROM dependencies
            WHERE group_id = $group AND revision_id = $revision AND kind = 'reopen' AND required = 1 ORDER BY ordinal;
            """, ("$group", groupId), ("$revision", revisionId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<(string, string, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    private sealed record RecordedMember(
        string ArtifactId, string ArtifactRevision, string Sha256, long Length, string FactsSha256, bool Required, SiloExactLocator Locator);

    private static async ValueTask<IReadOnlyList<RecordedMember>> ReadRecordedMembersAsync(
        SqliteConnection connection, string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, null, """
            SELECT artifact_id, artifact_revision, sha256, length, facts_sha256, required, store_id, bucket, key, version_id
            FROM publication_members WHERE group_id = $group AND revision_id = $revision ORDER BY ordinal;
            """, ("$group", groupId), ("$revision", revisionId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<RecordedMember>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
                reader.GetInt64(5) == 1, new(reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9))));
        }
        return result;
    }

    /// <summary>
    /// Check 2 (#1965 4.3, 5.3): the manifest's instance, binding, group and revision, and its members with their bytes,
    /// facts and locators, match the publish record that committed it, and the record names it at the immutable key its
    /// own UTC anchor, logical revision and exact digest fix.
    /// </summary>
    private string? IdentityMismatch(
        SiloGroupManifest manifest, string kind, string groupId, string revisionId, string? previous, string manifestSha, string? namedKey,
        IReadOnlyList<RecordedMember> recorded)
    {
        if (!string.Equals(manifest.InstanceId, Binding.InstanceId, StringComparison.Ordinal) ||
            !string.Equals(manifest.StorageBindingId, Binding.BindingId, StringComparison.Ordinal))
        {
            return "the manifest names another instance or storage binding";
        }
        if (!string.Equals(manifest.GroupId, groupId, StringComparison.Ordinal) ||
            !string.Equals(manifest.RevisionId, revisionId, StringComparison.Ordinal) ||
            !string.Equals(manifest.GroupKind, kind, StringComparison.Ordinal) ||
            !string.Equals(manifest.PreviousRevisionId, previous, StringComparison.Ordinal))
        {
            return "the manifest names another group revision than its publish record";
        }
        if (namedKey is not null &&
            !string.Equals(namedKey, SiloObjectNamespace.ManifestKey(manifest.FromUtc, groupId, revisionId, manifestSha), StringComparison.Ordinal))
        {
            return "the publish record names the manifest outside the key its anchor, revision and digest fix";
        }
        var expected = recorded.Select(static member => (member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length,
            member.FactsSha256, member.Required, member.Locator)).ToHashSet();
        var actual = manifest.Members.Select(static member => (member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length,
            SiloMemberFacts.Of(member).Sha256(), member.Required, member.Locator)).ToHashSet();
        return expected.SetEquals(actual) && expected.Count == manifest.Members.Count
            ? null
            : "the manifest's members differ from those its publish record qualified";
    }

    private static async ValueTask ProjectManifestAsync(SqliteConnection connection, SiloGroupManifest manifest, string json, CancellationToken cancellationToken)
    {
        using var transaction = BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, """
            UPDATE publications SET manifest_status = 'verified', published_utc_ticks = $published, from_utc_ticks = $from,
                to_utc_ticks = $to, camera_id = $camera, stream_id = $stream, reference_capture_id = $reference, revision_order = $order,
                manifest_json = $json
            WHERE group_id = $group AND revision_id = $revision;
            DELETE FROM dependencies WHERE group_id = $group AND revision_id = $revision;
            DELETE FROM view_layers WHERE group_id = $group AND revision_id = $revision;
            """, cancellationToken, ("$published", Ticks(manifest.PublishedUtc)), ("$from", Ticks(manifest.FromUtc)),
            ("$to", Ticks(manifest.ToUtc)), ("$camera", manifest.CameraId), ("$stream", manifest.View?.StreamId),
            ("$reference", manifest.View?.ReferenceCaptureId), ("$order", manifest.View?.RevisionOrder), ("$json", json),
            ("$group", manifest.GroupId), ("$revision", manifest.RevisionId)).ConfigureAwait(false);
        foreach (var member in manifest.Members)
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE publication_members SET role = $role, retention_class = $class, media_type = $media,
                    content_identity_sha256 = $content, output_identity_sha256 = $output, producer = $producer, recipe_identity_sha256 = $recipe
                WHERE group_id = $group AND revision_id = $revision AND artifact_id = $artifact AND artifact_revision = $artifactRevision;
                """, cancellationToken, ("$role", member.Role), ("$class", SiloJson.EnumName(member.RetentionClass)),
                ("$media", member.MediaType), ("$content", member.ContentIdentitySha256), ("$output", member.OutputIdentitySha256),
                ("$producer", member.Producer), ("$recipe", member.RecipeIdentitySha256), ("$group", manifest.GroupId),
                ("$revision", manifest.RevisionId), ("$artifact", member.ArtifactId), ("$artifactRevision", member.ArtifactRevision))
                .ConfigureAwait(false);
        }
        for (var ordinal = 0; ordinal < manifest.Dependencies.Count; ordinal++)
        {
            var dependency = manifest.Dependencies[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dependencies(group_id, revision_id, ordinal, artifact_id, artifact_revision, sha256, kind, required)
                VALUES($group, $revision, $ordinal, $artifact, $artifactRevision, $sha, $kind, $required);
                """, cancellationToken, ("$group", manifest.GroupId), ("$revision", manifest.RevisionId), ("$ordinal", ordinal),
                ("$artifact", dependency.ArtifactId), ("$artifactRevision", dependency.ArtifactRevision), ("$sha", dependency.Sha256),
                ("$kind", SiloJson.EnumName(dependency.Kind)), ("$required", dependency.Required ? 1 : 0)).ConfigureAwait(false);
        }
        var layers = manifest.View?.Layers ?? [];
        for (var ordinal = 0; ordinal < layers.Count; ordinal++)
        {
            var layer = layers[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO view_layers(group_id, revision_id, ordinal, layer_kind, scope, source_capture_id, evidence_order,
                    evidence_artifact_id, evidence_identity_sha256, evidence_payload_sha256, observed_utc_ticks, readiness, reason)
                VALUES($group, $revision, $ordinal, $kind, $scope, $source, $order, $artifact, $identity, $payload, $observed, $readiness, $reason);
                """, cancellationToken, ("$group", manifest.GroupId), ("$revision", manifest.RevisionId), ("$ordinal", ordinal),
                ("$kind", layer.Kind), ("$scope", layer.Scope), ("$source", layer.SourceCaptureId), ("$order", layer.EvidenceOrder),
                ("$artifact", layer.Evidence?.ArtifactId), ("$identity", layer.Evidence?.IdentitySha256),
                ("$payload", layer.Evidence?.PayloadSha256), ("$observed", layer.ObservedUtc is { } observed ? Ticks(observed) : null),
                ("$readiness", layer.Readiness), ("$reason", layer.Reason)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// P4: the revision becomes visible, and from now on it is settled history (<c>requires_members = 0</c>). A later
    /// outage may close it again, but a retry verifies only its manifest and the proof that it reached P4 is kept, for
    /// checkpoints too. Only the writer's own first P4 (<see cref="ConfirmP4Async"/>) puts it back under first-P4 rules.
    /// </summary>
    private static async ValueTask SetVisibleAsync(SqliteConnection connection, string groupId, string revisionId, CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, null, """
            UPDATE publications SET state = 'visible', outcome = NULL, outcome_detail = NULL, requires_members = 0
            WHERE group_id = $group AND revision_id = $revision AND manifest_status = 'verified';
            """, cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);

    private static async ValueTask CloseAsync(
        SqliteConnection connection, string groupId, string revisionId, string manifestStatus, string outcome, string detail,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, null, """
            UPDATE publications SET manifest_status = $status, state = 'closed', outcome = $outcome, outcome_detail = $detail
            WHERE group_id = $group AND revision_id = $revision;
            """, cancellationToken, ("$status", manifestStatus == SiloDisposition.Available ? SiloDisposition.Verified : manifestStatus),
            ("$outcome", outcome == SiloDisposition.Available ? SiloDisposition.Unverified : outcome), ("$detail", detail),
            ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);

    /// <summary>
    /// Establish a role through its eligible locators, local first, stopping at the first exact-version GET whose
    /// independently computed length and SHA-256 match. Every probe outcome is kept as that copy's own finding, and a
    /// success is remembered only for the rest of this operation.
    /// </summary>
    private async ValueTask<(string Disposition, byte[]? Bytes, SiloExactLocator? ServedFrom, bool Damaged)> ProbeRoleAsync(
        SqliteConnection connection, SiloStoreSet stores, SiloRoleKey role, string sha256, long length, bool captureBytes,
        SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        var locators = new List<SiloExactLocator>();
        using (var command = CreateCommand(connection, null, """
            SELECT store_id, bucket, key, version_id FROM locators
            WHERE kind = $kind AND id = $id AND revision = $revision AND eligible = 1 AND copy_state = 'active'
            ORDER BY CASE WHEN store_id = $local THEN 0 ELSE 1 END, generation, sequence, record_index, key, version_id;
            """, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision), ("$local", Binding.StoreId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var locator = new SiloExactLocator(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
                if (stores.TryResolve(locator.StoreId, locator.Bucket, out _))
                    locators.Add(locator);
            }
        }
        byte[]? bytes = null;
        SiloExactLocator? servedFrom = null;
        var damaged = false;
        foreach (var locator in locators)
        {
            SiloProbe probe;
            if (fresh.TryGet(locator, length, sha256, out var when, out var freshBytes) && (!captureBytes || freshBytes is not null))
            {
                probe = new(SiloProbeStatus.Verified, locator, when, SiloFormats.ExactGetSha256) { Bytes = freshBytes };
            }
            else
            {
                probe = await SiloVerifier.ProbeAsync(stores, locator, length, sha256, clock, captureBytes, cancellationToken).ConfigureAwait(false);
                if (probe.Status == SiloProbeStatus.Verified)
                {
                    fresh.Verified(locator, length, sha256, probe.ObservedUtc, probe.Bytes);
                }
            }
            await ExecuteAsync(connection, null, """
                UPDATE locators SET probe_status = $status, probe_detail = $detail, probe_unix_ms = $when
                WHERE kind = $kind AND id = $id AND revision = $revision
                    AND store_id = $store AND bucket = $bucket AND key = $key AND version_id = $version;
                """, cancellationToken, ("$status", SiloJson.EnumName(probe.Status)), ("$detail", probe.Detail),
                ("$when", probe.ObservedUtc.ToUnixTimeMilliseconds()), ("$kind", KindName(role.Kind)), ("$id", role.Id),
                ("$revision", role.Revision), ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key),
                ("$version", locator.VersionId)).ConfigureAwait(false);
            damaged |= probe.Status == SiloProbeStatus.Damaged;
            if (probe.Status == SiloProbeStatus.Verified)
            {
                bytes = probe.Bytes;
                servedFrom = locator;
                break;
            }
        }
        var unknown = await UnknownIntervalAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var disposition = await RecomputeRoleAsync(connection, null, role, unknown, cancellationToken, stores).ConfigureAwait(false);
        return (disposition, bytes, servedFrom, damaged && disposition == SiloDisposition.Missing);
    }

    private const string VerificationColumns = "kind, id, revision, sha256, length, generation, sequence, record_index";

    private const string VerificationEligible =
        "(disposition IN ('unverified', 'temporarily-unavailable') OR ($all = 1 AND disposition IN ('available', 'missing')))";

    private const string VerificationOrder = "ORDER BY generation, sequence, record_index, kind, id, revision LIMIT $limit";

    private sealed record VerificationRow(SiloRoleKey Role, string Sha256, long Length, long Generation, long Sequence, long Index);

    /// <summary>
    /// The lazy phase (#1965 7.5): verify unverified or temporarily unavailable roles by exact GET. With
    /// <paramref name="includeAvailable"/> it is an audit that re-reads available and missing roles too, so a deleted or
    /// damaged version behind a structurally valid catalog is found (#1059 negative control). Each pass resumes after a
    /// durable cursor and wraps around, so a role that keeps timing out, or a healthy early role, never starves the rest.
    /// </summary>
    public async ValueTask<int> VerifyRolesAsync(SiloStoreSet stores, int limit, CancellationToken cancellationToken, bool includeAvailable = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var cursorName = includeAvailable ? "audit" : "lazy";
            var all = includeAvailable ? 1 : 0;
            var rows = await ReadVerificationRowsAsync(connection, $"""
                SELECT {VerificationColumns} FROM roles r WHERE {VerificationEligible}
                    AND EXISTS (SELECT 1 FROM verification_cursors c WHERE c.name = $name
                        AND (r.generation, r.sequence, r.record_index, r.kind, r.id, r.revision)
                            > (c.generation, c.sequence, c.record_index, c.kind, c.id, c.revision))
                {VerificationOrder};
                """, limit, cancellationToken, ("$name", cursorName), ("$all", all)).ConfigureAwait(false);
            if (rows.Count < limit)
            {
                // Wrap to the start; a role is never taken twice in one pass.
                var taken = rows.Select(static row => row.Role).ToHashSet();
                var wrapped = await ReadVerificationRowsAsync(connection, $"SELECT {VerificationColumns} FROM roles WHERE {VerificationEligible} {VerificationOrder};",
                    limit, cancellationToken, ("$all", all)).ConfigureAwait(false);
                rows.AddRange(wrapped.Where(row => !taken.Contains(row.Role)).Take(limit - rows.Count));
            }
            var fresh = new SiloFreshEvidence();
            foreach (var row in rows)
            {
                await ProbeRoleAsync(connection, stores, row.Role, row.Sha256, row.Length, captureBytes: false, fresh, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (rows.Count > 0)
            {
                var last = rows[^1];
                await ExecuteAsync(connection, null, """
                    INSERT INTO verification_cursors(name, generation, sequence, record_index, kind, id, revision)
                    VALUES($name, $g, $s, $i, $kind, $id, $revision)
                    ON CONFLICT(name) DO UPDATE SET generation = excluded.generation, sequence = excluded.sequence,
                        record_index = excluded.record_index, kind = excluded.kind, id = excluded.id, revision = excluded.revision;
                    """, cancellationToken, ("$name", cursorName), ("$g", last.Generation), ("$s", last.Sequence), ("$i", last.Index),
                    ("$kind", KindName(last.Role.Kind)), ("$id", last.Role.Id), ("$revision", last.Role.Revision)).ConfigureAwait(false);
            }
            return rows.Count;
        }
        finally
        {
            _writer.Release();
        }
    }

    private static async ValueTask<List<VerificationRow>> ReadVerificationRowsAsync(
        SqliteConnection connection, string sql, int limit, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, null, sql, [.. parameters, ("$limit", limit)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<VerificationRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new(new(ParseKind(reader.GetString(0)), reader.GetString(1), reader.GetString(2)), reader.GetString(3), reader.GetInt64(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));
        }
        return rows;
    }

    internal async ValueTask<CameraAgentSiloVerificationResult> VerifyHistoryAsync(
        SiloStoreSet stores, SiloJournalPosition cutoff, CancellationToken cancellationToken)
    {
        const int batchSize = 16;
        VerificationRow? after = null;
        long verified = 0, unavailable = 0, expired = 0, failed = 0, damaged = 0, missing = 0;
        while (true)
        {
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                var rows = await ReadVerificationRowsAsync(connection, $"""
                    SELECT {VerificationColumns} FROM roles
                    WHERE (generation, sequence) <= ($cutoff_generation, $cutoff_sequence)
                      AND (generation, sequence, record_index, kind, id, revision) > ($g, $s, $i, $kind, $id, $revision)
                    {VerificationOrder};
                    """, batchSize, cancellationToken, ("$cutoff_generation", cutoff.Generation), ("$cutoff_sequence", cutoff.Sequence),
                    ("$g", after?.Generation ?? 0), ("$s", after?.Sequence ?? 0), ("$i", after?.Index ?? 0),
                    ("$kind", after is null ? string.Empty : KindName(after.Role.Kind)), ("$id", after?.Role.Id ?? string.Empty),
                    ("$revision", after?.Role.Revision ?? string.Empty)).ConfigureAwait(false);
                var fresh = new SiloFreshEvidence();
                foreach (var row in rows)
                {
                    var result = await ProbeRoleAsync(connection, stores, row.Role, row.Sha256, row.Length, captureBytes: false,
                        fresh, cancellationToken).ConfigureAwait(false);
                    switch (result.Disposition)
                    {
                        case SiloDisposition.Available: verified++; break;
                        case SiloDisposition.TemporarilyUnavailable: unavailable++; break;
                        case SiloDisposition.PolicyExpired: expired++; break;
                        case SiloDisposition.Missing:
                            if (result.Damaged) damaged++; else missing++;
                            failed++;
                            break;
                        case SiloDisposition.Conflict: damaged++; failed++; break;
                        default: failed++; break;
                    }
                }
                if (rows.Count < batchSize)
                    return new(cutoff.Generation, cutoff.Sequence, verified, unavailable, expired, failed)
                    { DamagedRoles = damaged, MissingRoles = missing };
                after = rows[^1];
            }
            finally
            {
                _writer.Release();
            }
        }
    }

    /// <summary>
    /// Exact reopen (#1965 6.1). A published ID alone proves nothing: the manifest, every required member and every
    /// required reopen dependency must each verify now, by exact read, through an eligible locator, falling back from the
    /// local copy to eligible replicas. A reopen only reads: it never changes a revision's publication state.
    /// </summary>
    public ValueTask<SiloReopenResult> ReopenAsync(SiloStoreSet stores, string groupId, string revisionId, CancellationToken cancellationToken) =>
        ReopenCoreAsync(stores, groupId, revisionId, null, cancellationToken);

    /// <summary>An exact link (#1965 6.3) names the stream and reference capture too; a link that does not match is refused.</summary>
    public ValueTask<SiloReopenResult> ReopenViewAsync(
        SiloStoreSet stores, string streamId, string referenceCaptureId, string baseId, string revisionId, CancellationToken cancellationToken) =>
        ReopenCoreAsync(stores, baseId, revisionId, (streamId, referenceCaptureId), cancellationToken);

    private async ValueTask<SiloReopenResult> ReopenCoreAsync(
        SiloStoreSet stores, string groupId, string revisionId, (string StreamId, string ReferenceCaptureId)? link,
        CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            string state, manifestSha;
            bool requiresMembers;
            string? outcome, stream, reference, json;
            using (var command = CreateCommand(connection, null, """
                SELECT state, outcome, manifest_sha256, stream_id, reference_capture_id, manifest_json, requires_members FROM publications
                WHERE group_id = $group AND revision_id = $revision;
                """, ("$group", groupId), ("$revision", revisionId)))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new(groupId, revisionId, false, false, null, [], null);
                }
                (state, outcome, manifestSha, stream, reference, json) = (reader.GetString(0), NullableString(reader, 1), reader.GetString(2),
                    NullableString(reader, 3), NullableString(reader, 4), NullableString(reader, 5));
                requiresMembers = reader.GetBoolean(6);
            }
            if (link is { } expected && (!string.Equals(expected.StreamId, stream, StringComparison.Ordinal) ||
                !string.Equals(expected.ReferenceCaptureId, reference, StringComparison.Ordinal)))
            {
                return new(groupId, revisionId, true, false, LinkMismatch, [], null);
            }
            if (state != "visible" && (requiresMembers || json is null || outcome != SiloDisposition.Missing))
            {
                return new(groupId, revisionId, true, false, outcome ?? SiloDisposition.Pending, [], null);
            }
            var wanted = new List<(SiloRoleKey Role, string Sha, bool Required, string Source)>
            {
                (new(SiloIdentityKind.Manifest, groupId, revisionId), manifestSha, true, "manifest")
            };
            using (var command = CreateCommand(connection, null, """
                SELECT artifact_id, artifact_revision, sha256, required, 'member' FROM publication_members
                WHERE group_id = $group AND revision_id = $revision
                UNION ALL
                SELECT artifact_id, artifact_revision, sha256, required, 'reopen-dependency' FROM dependencies
                WHERE group_id = $group AND revision_id = $revision AND kind = 'reopen';
                """, ("$group", groupId), ("$revision", revisionId)))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    wanted.Add((new(SiloIdentityKind.Member, reader.GetString(0), reader.GetString(1)), reader.GetString(2),
                        reader.GetInt64(3) == 1, reader.GetString(4)));
                }
            }
            var fresh = new SiloFreshEvidence();
            var items = new List<SiloReopenItem>();
            foreach (var (role, sha, required, source) in wanted)
            {
                var bound = await ReadRoleIdentityAsync(connection, null, role, cancellationToken).ConfigureAwait(false);
                if (bound is null)
                {
                    items.Add(new(role, sha, required, source, SiloDisposition.Missing, null, "not published in the authenticated prefix"));
                    continue;
                }
                if (!string.Equals(bound.Value.Sha256, sha, StringComparison.Ordinal))
                {
                    items.Add(new(role, sha, required, source, SiloDisposition.Conflict, null, "published with another digest"));
                    continue;
                }
                var (disposition, _, servedFrom, damaged) = await ProbeRoleAsync(connection, stores, role, sha, bound.Value.Length, captureBytes: false,
                    fresh, cancellationToken).ConfigureAwait(false);
                items.Add(new SiloReopenItem(role, sha, required, source, disposition, servedFrom, null) { Damaged = damaged });
            }
            var reopenable = state == "visible" && items.All(static item => !item.Required || item.Disposition == SiloDisposition.Available);
            return new(groupId, revisionId, true, reopenable, state == "visible" ? null : outcome, items, json);
        }
        finally
        {
            _writer.Release();
        }
    }
}
