using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using System.Text.Json;
using static HVO.SkyMonitor.CameraAgent.Common.Silo.SiloSqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed partial class SiloCatalogIndex
{
    internal async ValueTask<SiloRetentionPolicy> ReadRetentionPolicyAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var json = await ScalarStringAsync(connection, null, "SELECT policy_json FROM policies WHERE policy_id = $id;",
            cancellationToken, ("$id", SiloRetentionPolicy.PolicyId)).ConfigureAwait(false);
        return json is null ? SiloRetentionPolicy.Held : SiloRetentionPolicy.Read(json);
    }

    internal async ValueTask<IReadOnlyList<SiloRetentionHold>> ReadRetentionPinsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT target_id, target_revision FROM pins WHERE active = 1
            UNION
            SELECT m.artifact_id, m.artifact_revision FROM pins pin JOIN publication_members m ON m.group_id = pin.target_id
                AND (m.revision_id = pin.target_revision OR pin.target_revision = '*')
            WHERE pin.active = 1 AND pin.target_kind = 'group';
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloRetentionHold>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(SiloHoldKind.ManualPin, reader.GetString(0), reader.GetString(1) is "*" ? null : reader.GetString(1)));
        return result;
    }

    internal async ValueTask<IReadOnlyDictionary<SiloExactLocator, IReadOnlyList<SiloRetentionReference>>> ReadRetentionReferencesAsync(
        CancellationToken cancellationToken, SiloExactLocator? only = null)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            WITH physical AS (
                SELECT DISTINCT key, version_id FROM locators
                WHERE store_id = $store AND bucket = $bucket AND eligible = 1 AND copy_state = 'active'
                    AND (key GLOB 'p/*' OR key GLOB 'm/*') AND ($key IS NULL OR (key = $key AND version_id = $version))
            ), member_facts AS (
                SELECT m.artifact_id, m.artifact_revision,
                    CASE WHEN COUNT(m.role) = COUNT(*) THEN MIN(m.role) END AS role,
                    CASE WHEN COUNT(p.published_utc_ticks) = COUNT(*) THEN MAX(p.published_utc_ticks) END AS published_utc_ticks
                FROM publication_members m JOIN publications p USING(group_id, revision_id)
                GROUP BY m.artifact_id, m.artifact_revision
            )
            SELECT source.key, source.version_id, r.kind, r.id, r.revision, r.sha256, r.length,
                CASE WHEN r.kind = 'manifest' THEN 'manifest' ELSE m.role END,
                COALESCE(m.published_utc_ticks, p.published_utc_ticks),
                target.store_id, target.bucket, target.key, target.version_id
            FROM physical picked JOIN locators source USING(key, version_id)
                JOIN roles r USING(kind, id, revision)
                JOIN locators target USING(kind, id, revision)
                LEFT JOIN member_facts m ON r.kind = 'member' AND m.artifact_id = r.id AND m.artifact_revision = r.revision
                LEFT JOIN publications p ON r.kind = 'manifest' AND p.group_id = r.id AND p.revision_id = r.revision
            WHERE source.store_id = $store AND source.bucket = $bucket AND source.eligible = 1 AND source.copy_state <> 'evicted'
                AND target.eligible = 1 AND target.copy_state <> 'evicted'
            ORDER BY source.key, source.version_id, r.kind, r.id, r.revision,
                target.store_id, target.bucket, target.key, target.version_id;
            """, ("$store", Binding.StoreId), ("$bucket", Binding.Bucket), ("$key", only?.Key), ("$version", only?.VersionId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var physical = new Dictionary<SiloExactLocator, Dictionary<SiloRoleKey, (SiloRetentionReference Reference, List<SiloExactLocator> Copies)>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var locator = new SiloExactLocator(Binding.StoreId, Binding.Bucket, reader.GetString(0), reader.GetString(1));
            var role = new SiloRoleKey(ParseKind(reader.GetString(2)), reader.GetString(3), reader.GetString(4));
            if (!physical.TryGetValue(locator, out var roles))
            {
                roles = [];
                physical.Add(locator, roles);
            }
            if (!roles.TryGetValue(role, out var found))
            {
                var identity = new SiloObjectIdentity(role.Kind, role.Id, role.Revision, reader.GetString(5), reader.GetInt64(6));
                var copies = new List<SiloExactLocator>();
                found = (new(identity, NullableString(reader, 7), NullableLong(reader, 8) is { } ticks ? new(ticks, TimeSpan.Zero) : null, copies), copies);
                roles.Add(role, found);
            }
            found.Copies.Add(new(reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetString(12)));
        }
        return physical.ToDictionary(static item => item.Key,
            static item => (IReadOnlyList<SiloRetentionReference>)item.Value.Values.Select(static role => role.Reference).ToArray());
    }

    internal async ValueTask<IReadOnlyList<SiloRetentionHold>> ReadReopenRetentionHoldsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT DISTINCT d.artifact_id, d.artifact_revision
            FROM dependencies d JOIN roles r ON r.kind = 'manifest' AND r.id = d.group_id AND r.revision = d.revision_id
            WHERE d.kind = 'reopen' AND d.required = 1 AND COALESCE(r.journaled, '') <> 'policy-expired';
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloRetentionHold>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(SiloHoldKind.Recovery, reader.GetString(0), reader.GetString(1)));
        return result;
    }

    internal async ValueTask<bool> CanPinViewAsync(string groupId, string revisionId, SiloExactLocator? fenced,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarLongAsync(connection, null, """
            WITH required_roles(kind, id, revision) AS (
                SELECT 'manifest', $group, $revision
                UNION SELECT 'member', artifact_id, artifact_revision FROM publication_members
                    WHERE group_id = $group AND revision_id = $revision AND required = 1
                UNION SELECT 'member', artifact_id, artifact_revision FROM dependencies
                    WHERE group_id = $group AND revision_id = $revision AND kind = 'reopen' AND required = 1
            )
            SELECT NOT EXISTS (
                SELECT 1 FROM required_roles needed WHERE NOT EXISTS (
                    SELECT 1 FROM roles r JOIN locators l USING(kind, id, revision)
                    WHERE (r.kind, r.id, r.revision) = (needed.kind, needed.id, needed.revision)
                      AND COALESCE(r.journaled, '') <> 'policy-expired' AND l.eligible = 1 AND l.copy_state = 'active'
                ) OR EXISTS (
                    SELECT 1 FROM locators l WHERE (l.kind, l.id, l.revision) = (needed.kind, needed.id, needed.revision)
                      AND (l.store_id, l.bucket, l.key, l.version_id) = ($store, $bucket, $key, $version)
                )
            );
            """, cancellationToken, ("$group", groupId), ("$revision", revisionId), ("$store", fenced?.StoreId),
            ("$bucket", fenced?.Bucket), ("$key", fenced?.Key), ("$version", fenced?.VersionId)).ConfigureAwait(false) != 0;
    }

    internal async ValueTask<IReadOnlyList<SiloRetentionHold>> ReadViewRetentionHoldsAsync(
        IEnumerable<SiloRetentionHold> savedViews, CancellationToken cancellationToken)
    {
        var selections = JsonSerializer.Serialize(savedViews.Select(static hold => new[] { hold.ArtifactId, hold.Revision }));
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            WITH retained_views(group_id, revision_id) AS (
                SELECT group_id, revision_id FROM live_intervals WHERE ended_change_order IS NULL
                UNION SELECT json_extract(value, '$[0]'), json_extract(value, '$[1]') FROM json_each($saved)
            )
            SELECT group_id, revision_id, 'manifest' FROM retained_views
            UNION
            SELECT m.artifact_id, m.artifact_revision, 'member'
            FROM retained_views JOIN publication_members m USING(group_id, revision_id) WHERE m.required = 1
            UNION
            SELECT d.artifact_id, d.artifact_revision, 'member'
            FROM retained_views JOIN dependencies d USING(group_id, revision_id) WHERE d.kind = 'reopen';
            """, ("$saved", selections));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloRetentionHold>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(SiloHoldKind.ViewManifest, reader.GetString(0), reader.GetString(1), IdentityKind: ParseKind(reader.GetString(2))));
        return result;
    }

    internal async ValueTask<IReadOnlyList<SiloRetentionHold>> ReadSceneRetentionHoldsAsync(
        IReadOnlyList<SiloSceneLineage> scenes, bool archiveHistoryUnknown, CancellationToken cancellationToken)
    {
        if (scenes.Count == 0) return [];
        var lineage = JsonSerializer.Serialize(scenes.Select(static scene => new
        {
            id = scene.ArtifactId,
            raw = scene.RawArtifactId,
            known = scene.HasRecordedHistory,
            consumers = scene.ConsumerIds
        }));
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            WITH scenes(id, raw, history_known, consumers) AS (
                SELECT json_extract(value, '$.id'), json_extract(value, '$.raw'), json_extract(value, '$.known'),
                    json_extract(value, '$.consumers') FROM json_each($scenes)
            )
            SELECT DISTINCT scene.id FROM scenes scene
            WHERE $unknown OR NOT scene.history_known OR scene.raw IS NULL OR EXISTS (
                SELECT 1 FROM roles r JOIN locators l USING(kind, id, revision)
                WHERE r.kind = 'member' AND l.eligible = 1 AND l.copy_state <> 'evicted'
                    AND COALESCE(r.journaled, '') <> 'policy-expired' AND
                    (r.id = scene.raw OR r.id IN (SELECT value FROM json_each(scene.consumers)))
            ) OR EXISTS (
                SELECT 1 FROM dependencies d JOIN publication_members m USING(group_id, revision_id)
                JOIN roles r ON r.kind = 'member' AND r.id = m.artifact_id AND r.revision = m.artifact_revision
                JOIN locators l ON l.kind = r.kind AND l.id = r.id AND l.revision = r.revision
                WHERE d.kind = 'compute' AND d.artifact_id = scene.raw AND m.artifact_id <> scene.id
                    AND NOT EXISTS (SELECT 1 FROM scenes sibling WHERE sibling.id = m.artifact_id)
                    AND l.eligible = 1 AND l.copy_state <> 'evicted' AND COALESCE(r.journaled, '') <> 'policy-expired'
            );
            """, ("$scenes", lineage), ("$unknown", archiveHistoryUnknown));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloRetentionHold>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(SiloHoldKind.ProjectedScene, reader.GetString(0), IdentityKind: SiloIdentityKind.Member));
        return result;
    }

    internal async ValueTask<IReadOnlySet<string>> ReadArchivedCaptureReferencesAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            WITH archived_groups AS (
                SELECT DISTINCT m.group_id, m.revision_id
                FROM publication_members m JOIN locators l ON l.kind = 'member' AND l.id = m.artifact_id
                    AND l.revision = m.artifact_revision
                JOIN roles r USING(kind, id, revision)
                WHERE l.store_class = 'archive' AND l.eligible = 1 AND l.copy_state <> 'evicted'
                    AND COALESCE(r.journaled, '') <> 'policy-expired'
            )
            SELECT m.artifact_id FROM archived_groups JOIN publication_members m USING(group_id, revision_id)
            WHERE m.role = 'raw'
            UNION
            SELECT d.artifact_id FROM archived_groups JOIN dependencies d USING(group_id, revision_id)
            WHERE d.kind = 'compute';
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetString(0));
        return ids;
    }

    internal async ValueTask<bool> HasRetentionKeyAsync(SiloExactLocator locator, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarLongAsync(connection, null, """
            SELECT EXISTS (SELECT 1 FROM locators WHERE store_id = $store AND bucket = $bucket AND key = $key);
            """, cancellationToken, ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key))
            .ConfigureAwait(false) != 0;
    }

    internal ValueTask<bool> HasRetentionLocatorAsync(SiloExactLocator locator, CancellationToken cancellationToken) =>
        HasRetentionLocatorAsync(locator, activeOnly: false, cancellationToken);

    internal async ValueTask<IReadOnlyList<(string IntentId, SiloObjectIdentity Identity, SiloExactLocator Target)>> ReadArchiveExpiryTargetsAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT i.intent_id, r.kind, r.id, r.revision, r.sha256, r.length, t.store_id, t.bucket, t.key, t.version_id
            FROM expiry_intents i JOIN roles r USING(kind, id, revision)
                JOIN expiry_targets t ON t.intent_id = i.intent_id
            WHERE i.authorization = $policy AND i.scope = 'role' AND i.covers_authoritative = 1 AND i.last_copy = 1
                AND t.store_id <> $local AND t.outcome IS NULL
                AND COALESCE(r.journaled, '') <> 'policy-expired'
                AND NOT EXISTS (SELECT 1 FROM expiry_targets local WHERE local.intent_id = i.intent_id AND local.store_id = $local
                    AND COALESCE(local.outcome, '') NOT IN ('deleted','already-absent'))
            ORDER BY i.generation, i.sequence, i.record_index, t.store_id, t.key, t.version_id
            LIMIT 32;
            """, ("$policy", SiloRetentionPolicy.PolicyId), ("$local", Binding.StoreId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<(string, SiloObjectIdentity, SiloExactLocator)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add((reader.GetString(0), new(ParseKind(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5)), new(reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9))));
        return result;
    }

    internal async ValueTask<bool> IsFinalExpiryTargetAsync(string intentId, SiloExactLocator target, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarLongAsync(connection, null, """
            SELECT NOT EXISTS (SELECT 1 FROM expiry_targets WHERE intent_id = $intent
                AND COALESCE(outcome, '') NOT IN ('deleted','already-absent')
                AND (store_id, bucket, key, version_id) <> ($store, $bucket, $key, $version));
            """, cancellationToken, ("$intent", intentId), ("$store", target.StoreId), ("$bucket", target.Bucket), ("$key", target.Key),
            ("$version", target.VersionId)).ConfigureAwait(false) != 0;
    }

    internal ValueTask<bool> HasActiveRetentionLocatorAsync(SiloExactLocator locator, CancellationToken cancellationToken) =>
        HasRetentionLocatorAsync(locator, activeOnly: true, cancellationToken);

    internal async ValueTask<(SiloExactLocator Locator, long Length)?> FindRetainedLocalPayloadAsync(
        string artifactId, string sha256, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT l.key, l.version_id, r.length FROM roles r JOIN locators l USING(kind, id, revision)
            WHERE r.kind = 'member' AND r.id = $id AND r.sha256 = $sha AND COALESCE(r.journaled, '') <> 'policy-expired'
                AND l.store_id = $store AND l.bucket = $bucket AND l.eligible = 1 AND l.copy_state = 'active'
            ORDER BY l.generation, l.sequence, l.record_index LIMIT 1;
            """, ("$id", artifactId), ("$sha", sha256), ("$store", Binding.StoreId), ("$bucket", Binding.Bucket));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (new(Binding.StoreId, Binding.Bucket, reader.GetString(0), reader.GetString(1)), reader.GetInt64(2)) : null;
    }

    private async ValueTask<bool> HasRetentionLocatorAsync(SiloExactLocator locator, bool activeOnly, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarLongAsync(connection, null, """
            SELECT EXISTS(SELECT 1 FROM locators l JOIN roles r USING(kind, id, revision)
                WHERE l.store_id = $store AND l.bucket = $bucket AND l.key = $key AND l.version_id = $version
                AND ($active = 0 OR (l.eligible = 1 AND l.copy_state = 'active' AND COALESCE(r.journaled, '') <> 'policy-expired')));
            """, cancellationToken, ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key),
            ("$version", locator.VersionId), ("$active", activeOnly ? 1 : 0)).ConfigureAwait(false) != 0;
    }
}
