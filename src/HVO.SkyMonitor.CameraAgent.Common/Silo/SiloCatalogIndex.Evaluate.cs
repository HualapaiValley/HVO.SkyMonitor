using System.Text;
using Microsoft.Data.Sqlite;
using static HVO.SkyMonitor.CameraAgent.Common.Silo.SiloSqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// <see cref="Startup"/> is P3 recovery (#1965 5.3): the manifest and every required member must verify before P4.
/// <see cref="Rebuild"/> verifies each manifest and leaves member roles unverified for the lazy phase (#1965 7.5).
/// </summary>
internal enum SiloEvaluationMode { Startup, Rebuild }

/// <summary>Exact-version verifications this process has just performed, so live P4 need not repeat its own GETs.</summary>
internal sealed class SiloFreshEvidence
{
    private readonly Dictionary<SiloExactLocator, (DateTimeOffset When, byte[]? Bytes)> _verified = new();

    public void Verified(SiloExactLocator locator, DateTimeOffset when, byte[]? bytes = null) => _verified[locator] = (when, bytes);

    public bool TryGet(SiloExactLocator locator, out DateTimeOffset when, out byte[]? bytes)
    {
        if (_verified.TryGetValue(locator, out var value))
        {
            (when, bytes) = value;
            return true;
        }
        (when, bytes) = (default, null);
        return false;
    }
}

internal sealed record SiloReopenItem(
    SiloRoleKey Role, string Sha256, bool Required, string Source, string Disposition, SiloExactLocator? ServedFrom, string? Detail);

/// <summary>Whether a revision reopens exactly, which required member blocks it, and what else is reported (#1965 6.1).</summary>
internal sealed record SiloReopenResult(
    string GroupId, string RevisionId, bool Found, bool Reopenable, string? ClosedOutcome, IReadOnlyList<SiloReopenItem> Items,
    string? ManifestJson)
{
    public IEnumerable<SiloReopenItem> Blocking => Items.Where(static item => item.Required && item.Disposition != SiloDisposition.Available);
}

internal sealed partial class SiloCatalogIndex
{
    private static readonly string[] OutcomeSeverity =
    [
        SiloDisposition.TemporarilyUnavailable, SiloDisposition.Unverified, SiloDisposition.Missing,
        SiloDisposition.PolicyExpired, SiloDisposition.Conflict
    ];

    /// <summary>
    /// Evaluate publications awaiting P4 in journal order: authority is given (only authenticated records were applied),
    /// then identity, then exact bytes through an eligible locator. With <paramref name="retryClosed"/> a retryable
    /// closed revision is evaluated again; a later success brings the same P3 revision to P4 with no loss recorded.
    /// </summary>
    public async ValueTask<int> EvaluatePublicationsAsync(
        SiloStoreSet stores, SiloEvaluationMode mode, SiloFreshEvidence? fresh, bool retryClosed, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var candidates = new List<(string Group, string Revision)>();
            using (var command = CreateCommand(connection, null, """
                SELECT group_id, revision_id FROM publications
                WHERE state = 'pending' OR ($retry = 1 AND state = 'closed'
                    AND outcome IN ('temporarily-unavailable', 'unverified', 'missing') AND manifest_status <> 'conflict')
                ORDER BY generation, sequence, record_index;
                """, ("$retry", retryClosed ? 1 : 0)))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
            foreach (var (group, revision) in candidates)
            {
                await EvaluateLockedAsync(connection, stores, group, revision, mode, fresh, cancellationToken).ConfigureAwait(false);
            }
            return candidates.Count;
        }
        finally
        {
            _writer.Release();
        }
    }

    private async ValueTask EvaluateLockedAsync(
        SqliteConnection connection, SiloStoreSet stores, string groupId, string revisionId, SiloEvaluationMode mode,
        SiloFreshEvidence? fresh, CancellationToken cancellationToken)
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
        var (manifestDisposition, bytes) = await ProbeRoleAsync(connection, stores, manifestRole, manifestSha, manifestLength,
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
        if (IdentityMismatch(manifest, kind, groupId, revisionId, previous, recorded) is { } mismatch)
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
        // Check 3 reads every required member's exact bytes now. An earlier verification of the same content, for
        // example through another group, is not evidence that the version survives today; only this process's own
        // fresh readback stands in for a GET.
        var outcomes = new List<string>();
        foreach (var member in recorded.Where(static member => member.Required))
        {
            var role = new SiloRoleKey(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision);
            var (current, _) = await ProbeRoleAsync(connection, stores, role, member.Sha256, member.Length, captureBytes: false,
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
            "a required member did not verify through any eligible locator", cancellationToken).ConfigureAwait(false);
    }

    private sealed record RecordedMember(
        string ArtifactId, string ArtifactRevision, string Sha256, long Length, bool Required, SiloExactLocator Locator);

    private static async ValueTask<IReadOnlyList<RecordedMember>> ReadRecordedMembersAsync(
        SqliteConnection connection, string groupId, string revisionId, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, null, """
            SELECT artifact_id, artifact_revision, sha256, length, required, store_id, bucket, key, version_id
            FROM publication_members WHERE group_id = $group AND revision_id = $revision ORDER BY ordinal;
            """, ("$group", groupId), ("$revision", revisionId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<RecordedMember>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4) == 1,
                new(reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8))));
        }
        return result;
    }

    /// <summary>Check 2 (#1965 5.3): the manifest's instance, binding, group and revision, and its members, match the record.</summary>
    private string? IdentityMismatch(
        SiloGroupManifest manifest, string kind, string groupId, string revisionId, string? previous, IReadOnlyList<RecordedMember> recorded)
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
        var expected = recorded.Select(static member => (member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length,
            member.Required, member.Locator)).ToHashSet();
        var actual = manifest.Members.Select(static member => (member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length,
            member.Required, member.Locator)).ToHashSet();
        return expected.SetEquals(actual) && expected.Count == manifest.Members.Count
            ? null
            : "the manifest's members differ from those its publish record qualified";
    }

    private static async ValueTask ProjectManifestAsync(SqliteConnection connection, SiloGroupManifest manifest, string json, CancellationToken cancellationToken)
    {
        using var transaction = BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, """
            UPDATE publications SET manifest_status = 'verified', published_utc_ticks = $published, from_utc_ticks = $from,
                to_utc_ticks = $to, camera_id = $camera, stream_id = $stream, revision_order = $order, manifest_json = $json
            WHERE group_id = $group AND revision_id = $revision;
            DELETE FROM dependencies WHERE group_id = $group AND revision_id = $revision;
            DELETE FROM view_layers WHERE group_id = $group AND revision_id = $revision;
            """, cancellationToken, ("$published", Ticks(manifest.PublishedUtc)), ("$from", Ticks(manifest.FromUtc)),
            ("$to", Ticks(manifest.ToUtc)), ("$camera", manifest.CameraId), ("$stream", manifest.View?.StreamId),
            ("$order", manifest.View?.RevisionOrder), ("$json", json), ("$group", manifest.GroupId),
            ("$revision", manifest.RevisionId)).ConfigureAwait(false);
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

    private static async ValueTask SetVisibleAsync(SqliteConnection connection, string groupId, string revisionId, CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, null, """
            UPDATE publications SET state = 'visible', outcome = NULL, outcome_detail = NULL
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
    /// independently computed length and SHA-256 match. Every probe outcome is kept as that copy's own finding.
    /// </summary>
    private async ValueTask<(string Disposition, byte[]? Bytes)> ProbeRoleAsync(
        SqliteConnection connection, SiloStoreSet stores, SiloRoleKey role, string sha256, long length, bool captureBytes,
        SiloFreshEvidence? fresh, CancellationToken cancellationToken)
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
                locators.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        byte[]? bytes = null;
        foreach (var locator in locators)
        {
            SiloProbe probe;
            if (fresh is not null && fresh.TryGet(locator, out var when, out var freshBytes) && (!captureBytes || freshBytes is not null))
            {
                probe = new(SiloProbeStatus.Verified, locator, when, SiloFormats.ExactGetSha256) { Bytes = freshBytes };
            }
            else
            {
                probe = await SiloVerifier.ProbeAsync(stores, locator, length, sha256, clock, captureBytes, cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAsync(connection, null, """
                UPDATE locators SET probe_status = $status, probe_detail = $detail, probe_unix_ms = $when
                WHERE kind = $kind AND id = $id AND revision = $revision
                    AND store_id = $store AND bucket = $bucket AND key = $key AND version_id = $version;
                """, cancellationToken, ("$status", SiloJson.EnumName(probe.Status)), ("$detail", probe.Detail),
                ("$when", probe.ObservedUtc.ToUnixTimeMilliseconds()), ("$kind", KindName(role.Kind)), ("$id", role.Id),
                ("$revision", role.Revision), ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key),
                ("$version", locator.VersionId)).ConfigureAwait(false);
            if (probe.Status == SiloProbeStatus.Verified)
            {
                bytes = probe.Bytes;
                break;
            }
        }
        var unknown = await UnknownIntervalAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var disposition = await RecomputeRoleAsync(connection, null, role, unknown, cancellationToken).ConfigureAwait(false);
        return (disposition, bytes);
    }

    /// <summary>Re-derive every role's disposition, for example after the authority-unknown interval opens or closes.</summary>
    public async ValueTask RecomputeAllRolesAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var roles = new List<SiloRoleKey>();
            using (var command = CreateCommand(connection, null, "SELECT kind, id, revision FROM roles ORDER BY kind, id, revision;"))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    roles.Add(new(ParseKind(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
                }
            }
            var unknown = await UnknownIntervalAsync(connection, null, cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            foreach (var role in roles)
            {
                await RecomputeRoleAsync(connection, transaction, role, unknown, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// The lazy phase (#1965 7.5): verify unverified or temporarily unavailable roles by exact GET, oldest first. With
    /// <paramref name="includeAvailable"/> it is an audit that re-reads available roles too, so a deleted or damaged
    /// version behind a structurally valid catalog is found (#1059 negative control).
    /// </summary>
    public async ValueTask<int> VerifyRolesAsync(SiloStoreSet stores, int limit, CancellationToken cancellationToken, bool includeAvailable = false)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var roles = new List<(SiloRoleKey Role, string Sha, long Length)>();
            using (var command = CreateCommand(connection, null, """
                SELECT kind, id, revision, sha256, length FROM roles
                WHERE disposition IN ('unverified', 'temporarily-unavailable') OR ($all = 1 AND disposition IN ('available', 'missing'))
                ORDER BY generation, sequence, record_index, kind, id, revision LIMIT $limit;
                """, ("$limit", limit), ("$all", includeAvailable ? 1 : 0)))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    roles.Add((new(ParseKind(reader.GetString(0)), reader.GetString(1), reader.GetString(2)), reader.GetString(3), reader.GetInt64(4)));
                }
            }
            foreach (var (role, sha, length) in roles)
            {
                await ProbeRoleAsync(connection, stores, role, sha, length, captureBytes: false, null, cancellationToken).ConfigureAwait(false);
            }
            return roles.Count;
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// Exact reopen (#1965 6.1): a published ID alone proves nothing. The manifest, every required member and every
    /// required reopen dependency must each verify through an eligible locator; anything else is reported, not required.
    /// </summary>
    public async ValueTask<SiloReopenResult> ReopenAsync(
        SiloStoreSet stores, string groupId, string revisionId, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var state = await ScalarStringAsync(connection, null,
                "SELECT state || '|' || COALESCE(outcome, '') FROM publications WHERE group_id = $group AND revision_id = $revision;",
                cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
            if (state is null)
            {
                return new(groupId, revisionId, false, false, null, [], null);
            }
            if (!state.StartsWith("visible|", StringComparison.Ordinal) && !state.EndsWith("|conflict", StringComparison.Ordinal))
            {
                await EvaluateLockedAsync(connection, stores, groupId, revisionId, SiloEvaluationMode.Rebuild, null, cancellationToken)
                    .ConfigureAwait(false);
                state = await ScalarStringAsync(connection, null,
                    "SELECT state || '|' || COALESCE(outcome, '') FROM publications WHERE group_id = $group AND revision_id = $revision;",
                    cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
            }
            if (!state!.StartsWith("visible|", StringComparison.Ordinal))
            {
                return new(groupId, revisionId, true, false, state[(state.IndexOf('|', StringComparison.Ordinal) + 1)..], [], null);
            }
            var wanted = new List<(SiloRoleKey Role, string Sha, bool Required, string Source)>();
            var manifestSha = await ScalarStringAsync(connection, null,
                "SELECT manifest_sha256 FROM publications WHERE group_id = $group AND revision_id = $revision;",
                cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
            wanted.Add((new(SiloIdentityKind.Manifest, groupId, revisionId), manifestSha!, true, "manifest"));
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
                var disposition = await ScalarStringAsync(connection, null,
                    "SELECT disposition FROM roles WHERE kind = $kind AND id = $id AND revision = $revision;", cancellationToken,
                    ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision)).ConfigureAwait(false);
                if (disposition != SiloDisposition.Available)
                {
                    (disposition, _) = await ProbeRoleAsync(connection, stores, role, sha, bound.Value.Length, captureBytes: false, null,
                        cancellationToken).ConfigureAwait(false);
                }
                var servedFrom = await ReadAvailableAtAsync(connection, role, cancellationToken).ConfigureAwait(false);
                items.Add(new(role, sha, required, source, disposition!, servedFrom, null));
            }
            var json = await ScalarStringAsync(connection, null,
                "SELECT manifest_json FROM publications WHERE group_id = $group AND revision_id = $revision;",
                cancellationToken, ("$group", groupId), ("$revision", revisionId)).ConfigureAwait(false);
            var reopenable = items.All(static item => !item.Required || item.Disposition == SiloDisposition.Available);
            return new(groupId, revisionId, true, reopenable, null, items, json);
        }
        finally
        {
            _writer.Release();
        }
    }

    private static async ValueTask<SiloExactLocator?> ReadAvailableAtAsync(
        SqliteConnection connection, SiloRoleKey role, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, null, """
            SELECT available_store, available_bucket, available_key, available_version FROM roles
            WHERE kind = $kind AND id = $id AND revision = $revision AND available_store IS NOT NULL;
            """, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }
}
