using Microsoft.Data.Sqlite;
using static HVO.SkyMonitor.CameraAgent.Common.Silo.SiloSqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>A record exactly as committed, with its position. The raw JSON is what checkpoints carry forward.</summary>
internal sealed record SiloPositionedRecord(SiloJournalPosition Position, int Index, SiloJournalRecord Record, string RawJson);

internal sealed partial class SiloCatalogIndex
{
    private readonly record struct RecordAt(long Generation, long Sequence, int Index)
    {
        public static RecordAt Of(SiloPositionedRecord record) => new(record.Position.Generation, record.Position.Sequence, record.Index);
    }

    internal static string KindName(SiloIdentityKind kind) => kind == SiloIdentityKind.Member ? "member" : "manifest";

    internal static SiloIdentityKind ParseKind(string kind) => kind == "member" ? SiloIdentityKind.Member : SiloIdentityKind.Manifest;

    /// <summary>
    /// Apply one authenticated segment and advance the cursor atomically. Overlapping catch-up can replay the exact
    /// current segment. A different hash at the cursor, or a segment that does not extend it, is refused.
    /// </summary>
    public async ValueTask ApplySegmentAsync(
        SiloJournalPosition position, string sha256, string? previousSha256, IReadOnlyList<SiloPositionedRecord> records,
        bool generationWitnessed, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var (cursor, cursorSha) = await ReadCursorAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (cursor == position && string.Equals(cursorSha, sha256, StringComparison.Ordinal))
            {
                return;
            }
            var continues = cursor is null
                ? position == new SiloJournalPosition(1, 1) && previousSha256 is null
                : string.Equals(previousSha256, cursorSha, StringComparison.Ordinal) &&
                  (position == new SiloJournalPosition(cursor.Value.Generation, cursor.Value.Sequence + 1) ||
                   position.Generation > cursor.Value.Generation && position.Sequence == 1);
            if (!continues)
            {
                throw new SiloCatalogDivergedException($"Segment {position} does not continue the catalog cursor {cursor?.ToString() ?? "(empty)"}.");
            }
            foreach (var record in records)
            {
                await ApplyRecordAsync(connection, transaction, record, generationWitnessed || position.Generation == 1, cancellationToken)
                    .ConfigureAwait(false);
            }
            await SetCursorAsync(connection, transaction, position, sha256, cancellationToken).ConfigureAwait(false);
            // What the prefix now holds is no longer unproven: the two sets stay disjoint in every committed state.
            await ExecuteAsync(connection, transaction,
                "DELETE FROM unproven_publications WHERE (generation, sequence) <= ($g, $s);",
                cancellationToken, ("$g", position.Generation), ("$s", position.Sequence)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Load a trusted anchor's checkpoint into an empty index: its records, in order, then the cursor.</summary>
    public async ValueTask ApplyCheckpointAsync(SiloJournalCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var (cursor, _) = await ReadCursorAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (cursor is not null)
            {
                throw new InvalidOperationException("A checkpoint loads only into an empty index.");
            }
            foreach (var entry in checkpoint.Entries)
            {
                var subject = $"checkpoint {checkpoint.Covers} entry g{entry.Generation}/{entry.Sequence}#{entry.Index}";
                var record = SiloJson.ReadRecord(entry.Record, subject);
                // Records inside a trusted anchor were authenticated when it was made, generation boundaries included.
                await ApplyRecordAsync(connection, transaction,
                    new(new(entry.Generation, entry.Sequence), entry.Index, record, entry.Record.GetRawText()),
                    generationWitnessed: true, cancellationToken).ConfigureAwait(false);
            }
            await SetCursorAsync(connection, transaction, checkpoint.Covers, checkpoint.CoversSha256, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    private static async ValueTask SetCursorAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloJournalPosition position, string sha256, CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction,
            "UPDATE catalog_cursor SET generation = $g, sequence = $s, sha256 = $sha WHERE cursor_key = 1;",
            cancellationToken, ("$g", position.Generation), ("$s", position.Sequence), ("$sha", sha256)).ConfigureAwait(false);

    private async ValueTask ApplyRecordAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloPositionedRecord record, bool generationWitnessed,
        CancellationToken cancellationToken)
    {
        var at = RecordAt.Of(record);
        string? rejection = record.Record switch
        {
            SiloGenerationOpenRecord open => await ApplyGenerationOpenAsync(connection, transaction, open, generationWitnessed, cancellationToken).ConfigureAwait(false),
            SiloPublishRecord publish => await ApplyPublishAsync(connection, transaction, publish, at, cancellationToken).ConfigureAwait(false),
            SiloLiveIssueRecord live => await ApplyLiveIssueAsync(connection, transaction, live, at, cancellationToken).ConfigureAwait(false),
            SiloCopyRecord copy => await ApplyLocatorRecordAsync(connection, transaction, copy.Identity, copy.Locator, "copy", at,
                copy.StoreClass, copy.Retrieval, copy.Custody, qualified: true, cancellationToken).ConfigureAwait(false),
            SiloRemapRecord remap => await ApplyLocatorRecordAsync(connection, transaction, remap.Identity, remap.To, "remap", at,
                null, null, "remap", remap.PersistenceQualified, cancellationToken).ConfigureAwait(false),
            SiloReserveSurvivorRecord reserve => await ApplyReserveAsync(connection, transaction, reserve, at, cancellationToken).ConfigureAwait(false),
            SiloReleaseSurvivorRecord release => await ApplyReleaseAsync(connection, transaction, release, at, cancellationToken).ConfigureAwait(false),
            SiloPinRecord pin => await ApplyPinAsync(connection, transaction, pin, at, cancellationToken).ConfigureAwait(false),
            SiloPolicyRecord policy => await ApplyPolicyAsync(connection, transaction, policy, at, cancellationToken).ConfigureAwait(false),
            SiloExpiryIntentRecord intent => await ApplyIntentAsync(connection, transaction, intent, at, cancellationToken).ConfigureAwait(false),
            SiloExpiryDispositionRecord disposition => await ApplyDispositionAsync(connection, transaction, disposition, at, cancellationToken).ConfigureAwait(false),
            SiloAvailabilityRecord availability => await ApplyAvailabilityAsync(connection, transaction, availability, at, cancellationToken).ConfigureAwait(false),
            SiloAnchorRecord anchor => await ApplyAnchorAsync(connection, transaction, anchor, at, cancellationToken).ConfigureAwait(false),
            _ => "unknown-record"
        };
        await ExecuteAsync(connection, transaction, """
            INSERT INTO journal_records(generation, sequence, record_index, record_type, record_json, rejection)
            VALUES($g, $s, $i, $type, $json, $rejection);
            """, cancellationToken, ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index),
            ("$type", SiloJson.RecordType(record.Record)), ("$json", record.RawJson), ("$rejection", rejection)).ConfigureAwait(false);
    }

    private static async ValueTask<string?> ApplyGenerationOpenAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloGenerationOpenRecord open, bool witnessed, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO generations(generation, reason, witnessed) VALUES($g, $reason, $witnessed);
            """, cancellationToken, ("$g", open.Generation), ("$reason", open.Reason), ("$witnessed", witnessed ? 1 : 0)).ConfigureAwait(false);
        foreach (var (counter, value) in open.IdentityFloors)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO identity_floors(counter, value) VALUES($counter, $value)
                ON CONFLICT(counter) DO UPDATE SET value = MAX(value, excluded.value);
                """, cancellationToken, ("$counter", counter), ("$value", value)).ConfigureAwait(false);
        }
        return null;
    }

    private static async ValueTask<(string Sha256, long Length)?> ReadRoleIdentityAsync(
        SqliteConnection connection, SqliteTransaction? transaction, SiloRoleKey role, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction,
            "SELECT sha256, length FROM roles WHERE kind = $kind AND id = $id AND revision = $revision;",
            ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    /// <summary>
    /// True when the role is new or already bound to the same bytes and facts; false for a conflicting digest or for
    /// changed producer or sidecar facts, which must be a new revision (#1965 4.2).
    /// </summary>
    private static async ValueTask<bool> EnsureRoleAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloObjectIdentity identity, string factsSha256, RecordAt at,
        CancellationToken cancellationToken)
    {
        using (var command = CreateCommand(connection, transaction,
            "SELECT sha256, length, facts_sha256 FROM roles WHERE kind = $kind AND id = $id AND revision = $revision;",
            ("$kind", KindName(identity.Kind)), ("$id", identity.Id), ("$revision", identity.Revision)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return string.Equals(reader.GetString(0), identity.Sha256, StringComparison.Ordinal) && reader.GetInt64(1) == identity.Length &&
                    string.Equals(reader.GetString(2), factsSha256, StringComparison.Ordinal);
            }
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO roles(kind, id, revision, sha256, length, facts_sha256, generation, sequence, record_index, disposition)
            VALUES($kind, $id, $revision, $sha, $length, $facts, $g, $s, $i, 'unverified');
            """, cancellationToken, ("$kind", KindName(identity.Kind)), ("$id", identity.Id), ("$revision", identity.Revision),
            ("$sha", identity.Sha256), ("$length", identity.Length), ("$facts", factsSha256), ("$g", at.Generation), ("$s", at.Sequence),
            ("$i", at.Index)).ConfigureAwait(false);
        return true;
    }

    private static ValueTask<int> AddLocatorAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloRoleKey role, SiloExactLocator locator, string source,
        RecordAt at, SiloStoreClass? storeClass, SiloRetrieval? retrieval, string? custody, string? ineligibleReason,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO locators(kind, id, revision, store_id, bucket, key, version_id, source, generation, sequence, record_index,
                store_class, retrieval, custody, eligible, ineligible_reason, copy_state, entered_generation, entered_sequence, entered_index)
            VALUES($kind, $id, $revision, $store, $bucket, $key, $version, $source, $g, $s, $i,
                $class, $retrieval, $custody, $eligible, $reason, 'active', $g, $s, $i)
            ON CONFLICT(kind, id, revision, store_id, bucket, key, version_id) DO UPDATE SET
                source = excluded.source, generation = excluded.generation, sequence = excluded.sequence,
                record_index = excluded.record_index, store_class = excluded.store_class, retrieval = excluded.retrieval,
                custody = excluded.custody, eligible = 1, ineligible_reason = NULL,
                entered_generation = excluded.generation, entered_sequence = excluded.sequence, entered_index = excluded.record_index
            WHERE locators.eligible = 0 AND excluded.eligible = 1;
            """, cancellationToken, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision),
            ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key), ("$version", locator.VersionId),
            ("$source", source), ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index),
            ("$class", storeClass is { } value ? SiloJson.EnumName(value) : null),
            ("$retrieval", retrieval is { } mode ? SiloJson.EnumName(mode) : null), ("$custody", custody),
            ("$eligible", ineligibleReason is null ? 1 : 0), ("$reason", ineligibleReason));

    private async ValueTask<string?> ApplyPublishAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloPublishRecord publish, RecordAt at, CancellationToken cancellationToken)
    {
        var existing = await ScalarStringAsync(connection, transaction,
            "SELECT manifest_sha256 FROM publications WHERE group_id = $group AND revision_id = $revision;",
            cancellationToken, ("$group", publish.GroupId), ("$revision", publish.RevisionId)).ConfigureAwait(false);
        if (existing is not null)
        {
            if (string.Equals(existing, publish.Manifest.Sha256, StringComparison.Ordinal))
            {
                return "duplicate-publication";
            }
            // Authenticated evidence contradicts itself: the revision is closed as a conflict and kept for diagnosis.
            await ExecuteAsync(connection, transaction, """
                UPDATE publications SET state = 'closed', outcome = 'conflict', manifest_status = 'conflict',
                    outcome_detail = 'a later publish record names different manifest bytes for this revision'
                WHERE group_id = $group AND revision_id = $revision;
                """, cancellationToken, ("$group", publish.GroupId), ("$revision", publish.RevisionId)).ConfigureAwait(false);
            return "conflicting-publication";
        }
        var manifestIdentity = new SiloObjectIdentity(SiloIdentityKind.Manifest, publish.GroupId, publish.RevisionId,
            publish.Manifest.Sha256, publish.Manifest.Length);
        var conflicts = new List<string>();
        var consistent = new List<(SiloRoleKey Role, SiloExactLocator Locator)>();
        if (await EnsureRoleAsync(connection, transaction, manifestIdentity, manifestIdentity.Sha256, at, cancellationToken).ConfigureAwait(false))
        {
            consistent.Add((manifestIdentity.Role, publish.Manifest.Locator));
        }
        else
        {
            conflicts.Add("manifest");
        }
        foreach (var member in publish.Members)
        {
            var identity = new SiloObjectIdentity(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length);
            if (await EnsureRoleAsync(connection, transaction, identity, member.FactsSha256, at, cancellationToken).ConfigureAwait(false))
            {
                consistent.Add((identity.Role, member.Locator));
            }
            else
            {
                conflicts.Add(member.ArtifactId + "/" + member.ArtifactRevision);
            }
        }
        var conflict = conflicts.Count > 0;
        await ExecuteAsync(connection, transaction, """
            INSERT INTO publications(group_id, revision_id, group_kind, previous_revision_id, generation, sequence, record_index,
                manifest_sha256, manifest_length, agent_id, capture_sequence, manifest_status, state, outcome, outcome_detail)
            VALUES($group, $revision, $kind, $previous, $g, $s, $i, $sha, $length, $agent, $sequenceNumber,
                $status, $state, $outcome, $detail);
            """, cancellationToken, ("$group", publish.GroupId), ("$revision", publish.RevisionId), ("$kind", publish.GroupKind),
            ("$previous", publish.PreviousRevisionId), ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index),
            ("$sha", publish.Manifest.Sha256), ("$length", publish.Manifest.Length), ("$agent", publish.Capture?.AgentId),
            ("$sequenceNumber", publish.Capture?.CaptureSequence), ("$status", conflict ? "conflict" : "pending"),
            ("$state", conflict ? "closed" : "pending"), ("$outcome", conflict ? SiloDisposition.Conflict : null),
            ("$detail", conflict ? "identity conflict: " + string.Join(", ", conflicts) : null)).ConfigureAwait(false);
        foreach (var (role, locator) in consistent)
        {
            await AddLocatorAsync(connection, transaction, role, locator, "named", at, SiloStoreClass.Local, SiloRetrieval.Online,
                "publication", null, cancellationToken).ConfigureAwait(false);
        }
        for (var ordinal = 0; ordinal < publish.Members.Count; ordinal++)
        {
            var member = publish.Members[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO publication_members(group_id, revision_id, ordinal, artifact_id, artifact_revision, sha256, length, facts_sha256,
                    required, store_id, bucket, key, version_id)
                VALUES($group, $revision, $ordinal, $artifact, $artifactRevision, $sha, $length, $facts, $required, $store, $bucket, $key, $version);
                """, cancellationToken, ("$group", publish.GroupId), ("$revision", publish.RevisionId), ("$ordinal", ordinal),
                ("$artifact", member.ArtifactId), ("$artifactRevision", member.ArtifactRevision), ("$sha", member.Sha256),
                ("$facts", member.FactsSha256),
                ("$length", member.Length), ("$required", member.Required ? 1 : 0), ("$store", member.Locator.StoreId),
                ("$bucket", member.Locator.Bucket), ("$key", member.Locator.Key), ("$version", member.Locator.VersionId))
                .ConfigureAwait(false);
        }
        var unknown = await UnknownIntervalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var (role, _) in consistent)
        {
            await RecomputeRoleAsync(connection, transaction, role, unknown, cancellationToken).ConfigureAwait(false);
            // A revision waiting on this exact artifact revision as a required reopen dependency can now be proven.
            await RequeueAsync(connection, transaction, role, cancellationToken).ConfigureAwait(false);
        }
        return conflict ? "identity-conflict" : null;
    }

    private static async ValueTask<string?> ApplyLiveIssueAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloLiveIssueRecord live, RecordAt at, CancellationToken cancellationToken)
    {
        if (await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM publications WHERE group_id = $group AND revision_id = $revision;",
                cancellationToken, ("$group", live.GroupId), ("$revision", live.RevisionId)).ConfigureAwait(false) == 0)
        {
            return "unpublished-revision";
        }
        if (await ScalarLongAsync(connection, transaction, """
                SELECT COUNT(*) FROM live_intervals WHERE camera_id = $camera AND ended_change_order IS NULL
                    AND group_id = $group AND revision_id = $revision AND change_order = $order;
                """, cancellationToken, ("$camera", live.CameraId), ("$group", live.GroupId), ("$revision", live.RevisionId),
                ("$order", live.ChangeOrder)).ConfigureAwait(false) != 0)
        {
            // The same issue again changes nothing; it never closes its own interval or opens a second one.
            return "duplicate-live-issue";
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE live_intervals SET ended_change_order = $order, ended_utc_ticks = $utc
            WHERE camera_id = $camera AND ended_change_order IS NULL;
            INSERT INTO live_intervals(generation, sequence, record_index, camera_id, stream_id, group_id, revision_id, change_order, started_utc_ticks)
            VALUES($g, $s, $i, $camera, $stream, $group, $revision, $order, $utc);
            """, cancellationToken, ("$order", live.ChangeOrder), ("$utc", Ticks(live.StartedUtc)), ("$camera", live.CameraId),
            ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index), ("$stream", live.StreamId), ("$group", live.GroupId),
            ("$revision", live.RevisionId)).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// A copy or remap becomes an eligible locator only when it is bound to a role already published in the prefix with
    /// the same identity, revision, length and SHA-256 (#1965 4.2). Matching keys or bytes alone are never enough.
    /// </summary>
    private async ValueTask<string?> ApplyLocatorRecordAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloObjectIdentity identity, SiloExactLocator locator, string source,
        RecordAt at, SiloStoreClass? storeClass, SiloRetrieval? retrieval, string custody, bool qualified, CancellationToken cancellationToken)
    {
        var bound = await ReadRoleIdentityAsync(connection, transaction, identity.Role, cancellationToken).ConfigureAwait(false);
        var reason = bound switch
        {
            null => "no-prior-publication",
            { } value when !string.Equals(value.Sha256, identity.Sha256, StringComparison.Ordinal) || value.Length != identity.Length => "identity-mismatch",
            _ when !qualified => "not-persistence-qualified",
            _ => null
        };
        if (storeClass is null)
        {
            storeClass = string.Equals(locator.StoreId, Binding.StoreId, StringComparison.Ordinal) ? SiloStoreClass.Local : SiloStoreClass.Archive;
            retrieval = SiloRetrieval.Online;
        }
        var changed = await AddLocatorAsync(connection, transaction, identity.Role, locator, source, at, storeClass, retrieval, custody, reason,
            cancellationToken).ConfigureAwait(false);
        if (reason is null)
        {
            var unknown = await UnknownIntervalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RecomputeRoleAsync(connection, transaction, identity.Role, unknown, cancellationToken).ConfigureAwait(false);
            if (changed == 1)
            {
                await RequeueAsync(connection, transaction, identity.Role, cancellationToken).ConfigureAwait(false);
            }
        }
        return reason;
    }

    /// <summary>
    /// A newly published, newly eligible or restored role can change a closed revision's outcome, so every revision that
    /// P3 recovery or a rebuild closed for want of that role, as its manifest, a required member or a required reopen
    /// dependency, is evaluated again, exactly as a rebuild would.
    /// </summary>
    private static async ValueTask RequeueAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloRoleKey role, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE publications SET state = 'pending', outcome = NULL, outcome_detail = NULL,
                manifest_status = CASE WHEN manifest_status IN ('missing', 'unverified', 'temporarily-unavailable') THEN 'pending'
                    ELSE manifest_status END
            WHERE state = 'closed' AND outcome IN ('missing', 'unverified', 'temporarily-unavailable')
                AND (($kind = 'manifest' AND group_id = $id AND revision_id = $revision)
                    OR ($kind = 'member' AND EXISTS (SELECT 1 FROM publication_members m
                        WHERE m.group_id = publications.group_id AND m.revision_id = publications.revision_id
                            AND m.artifact_id = $id AND m.artifact_revision = $revision AND m.required = 1))
                    OR ($kind = 'member' AND EXISTS (SELECT 1 FROM dependencies d
                        WHERE d.group_id = publications.group_id AND d.revision_id = publications.revision_id AND d.kind = 'reopen'
                            AND d.artifact_id = $id AND d.artifact_revision = $revision AND d.required = 1)));
            """, cancellationToken, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision)).ConfigureAwait(false);
    }

    private static async ValueTask<string?> ApplyReserveAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloReserveSurvivorRecord reserve, RecordAt at, CancellationToken cancellationToken)
    {
        var inserted = await ExecuteAsync(connection, transaction, """
            INSERT INTO survivor_reservations(reservation_id, kind, id, revision, store_id, bucket, key, version_id, intent_id,
                generation, sequence, record_index)
            VALUES($reservation, $kind, $id, $revision, $store, $bucket, $key, $version, $intent, $g, $s, $i)
            ON CONFLICT DO NOTHING;
            """, cancellationToken, ("$reservation", reserve.ReservationId), ("$kind", KindName(reserve.Identity.Kind)),
            ("$id", reserve.Identity.Id), ("$revision", reserve.Identity.Revision), ("$store", reserve.Survivor.StoreId),
            ("$bucket", reserve.Survivor.Bucket), ("$key", reserve.Survivor.Key), ("$version", reserve.Survivor.VersionId),
            ("$intent", reserve.IntentId), ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index)).ConfigureAwait(false);
        return inserted == 1 ? null : "duplicate-reservation";
    }

    private static async ValueTask<string?> ApplyReleaseAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloReleaseSurvivorRecord release, RecordAt at, CancellationToken cancellationToken)
    {
        var updated = await ExecuteAsync(connection, transaction, """
            UPDATE survivor_reservations SET released_generation = $g, released_sequence = $s, released_index = $i, release_reason = $reason
            WHERE reservation_id = $reservation AND released_generation IS NULL;
            """, cancellationToken, ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index), ("$reason", release.Reason),
            ("$reservation", release.ReservationId)).ConfigureAwait(false);
        return updated == 1 ? null : "unknown-or-released-reservation";
    }

    private static async ValueTask<string?> ApplyPinAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloPinRecord pin, RecordAt at, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO pins(pin_id, target_kind, target_id, target_revision, active, reason, generation, sequence, record_index)
            VALUES($pin, $kind, $id, $revision, $active, $reason, $g, $s, $i)
            ON CONFLICT(pin_id) DO UPDATE SET target_kind = excluded.target_kind, target_id = excluded.target_id,
                target_revision = excluded.target_revision, active = excluded.active, reason = excluded.reason,
                generation = excluded.generation, sequence = excluded.sequence, record_index = excluded.record_index;
            """, cancellationToken, ("$pin", pin.PinId), ("$kind", pin.TargetKind), ("$id", pin.TargetId),
            ("$revision", pin.TargetRevision), ("$active", pin.Active ? 1 : 0), ("$reason", pin.Reason),
            ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index)).ConfigureAwait(false);
        return null;
    }

    private static async ValueTask<string?> ApplyPolicyAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloPolicyRecord policy, RecordAt at, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO policies(policy_id, policy_json, generation, sequence, record_index) VALUES($policy, $json, $g, $s, $i)
            ON CONFLICT(policy_id) DO UPDATE SET policy_json = excluded.policy_json, generation = excluded.generation,
                sequence = excluded.sequence, record_index = excluded.record_index;
            """, cancellationToken, ("$policy", policy.PolicyId), ("$json", policy.Policy.GetRawText()),
            ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index)).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The one proof that removes an exact copy from the authority set (#1965 7.3), shared by copy eviction and role
    /// expiry: an absent outcome (<c>deleted</c> or <c>already-absent</c>) under the same intent, recorded at or after the
    /// copy's latest entry into the set, compared by generation, segment and record position. Proof from before a
    /// restoration, under another intent or under none is stale and removes nothing. Binds <c>$intent</c> to locator
    /// row <c>l</c>.
    /// </summary>
    private const string FreshAbsenceUnderIntent = """
        EXISTS (SELECT 1 FROM expiry_targets t WHERE t.intent_id = $intent AND t.store_id = l.store_id
            AND t.bucket = l.bucket AND t.key = l.key AND t.version_id = l.version_id
            AND t.outcome IN ('deleted', 'already-absent')
            AND (t.outcome_generation, t.outcome_sequence, t.outcome_index)
                >= (l.entered_generation, l.entered_sequence, l.entered_index))
        """;

    /// <summary>
    /// The role's current authoritative copies: every eligible exact version whose absence was not proven under an
    /// authorized intent. A copy found unexpectedly missing stays a member until an intent covers it and proves it absent.
    /// </summary>
    private static async ValueTask<HashSet<SiloExactLocator>> AuthoritativeCopiesAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloRoleKey role, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT store_id, bucket, key, version_id FROM locators
            WHERE kind = $kind AND id = $id AND revision = $revision AND eligible = 1 AND copy_state IN ('active', 'quarantined', 'missing');
            """, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new HashSet<SiloExactLocator>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return result;
    }

    private static async ValueTask<string?> ApplyIntentAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloExpiryIntentRecord intent, RecordAt at, CancellationToken cancellationToken)
    {
        var bound = await ReadRoleIdentityAsync(connection, transaction, intent.Role.Role, cancellationToken).ConfigureAwait(false);
        if (bound is not { } value || !string.Equals(value.Sha256, intent.Role.Sha256, StringComparison.Ordinal) || value.Length != intent.Role.Length)
        {
            return "unknown-role";
        }
        if (await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM expiry_intents WHERE intent_id = $intent;",
                cancellationToken, ("$intent", intent.IntentId)).ConfigureAwait(false) != 0)
        {
            return "duplicate-intent";
        }
        var targets = intent.Targets.Select(static target => target.Locator).ToHashSet();
        var authoritative = await AuthoritativeCopiesAsync(connection, transaction, intent.Role.Role, cancellationToken).ConfigureAwait(false);
        var covers = authoritative.IsSubsetOf(targets);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO expiry_intents(intent_id, scope, kind, id, revision, authorization, last_copy, policy_reason, covers_authoritative,
                generation, sequence, record_index)
            VALUES($intent, $scope, $kind, $id, $revision, $authorization, $last, $reason, $covers, $g, $s, $i);
            """, cancellationToken, ("$intent", intent.IntentId), ("$scope", SiloJson.EnumName(intent.Scope)),
            ("$kind", KindName(intent.Role.Kind)), ("$id", intent.Role.Id), ("$revision", intent.Role.Revision),
            ("$authorization", intent.Authorization), ("$last", intent.LastCopy ? 1 : 0), ("$reason", intent.PolicyReason),
            ("$covers", covers ? 1 : 0), ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index)).ConfigureAwait(false);
        foreach (var target in intent.Targets)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO expiry_targets(intent_id, store_id, bucket, key, version_id, custody, allocation_id, allocated_bytes)
                VALUES($intent, $store, $bucket, $key, $version, $custody, $allocation, $bytes) ON CONFLICT DO NOTHING;
                """, cancellationToken, ("$intent", intent.IntentId), ("$store", target.Locator.StoreId), ("$bucket", target.Locator.Bucket),
                ("$key", target.Locator.Key), ("$version", target.Locator.VersionId), ("$custody", target.Custody),
                ("$allocation", target.AllocationId), ("$bytes", target.AllocatedBytes)).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// One delete attempt's terminal outcome on one exact copy, linked to its intent (#1965 7.3 step 8). Proven absence
    /// evicts that exact version; presence ends the attempt with no removal; an unlinked outcome changes nothing.
    /// </summary>
    private async ValueTask<string?> ApplyDispositionAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloExpiryDispositionRecord disposition, RecordAt at,
        CancellationToken cancellationToken)
    {
        var current = await ScalarStringAsync(connection, transaction, """
            SELECT COALESCE(outcome, '') FROM expiry_targets
            WHERE intent_id = $intent AND store_id = $store AND bucket = $bucket AND key = $key AND version_id = $version;
            """, cancellationToken, ("$intent", disposition.IntentId), ("$store", disposition.Locator.StoreId),
            ("$bucket", disposition.Locator.Bucket), ("$key", disposition.Locator.Key), ("$version", disposition.Locator.VersionId))
            .ConfigureAwait(false);
        if (current is null)
        {
            return "unlinked";
        }
        var absent = disposition.Outcome is SiloExpiryOutcome.Deleted or SiloExpiryOutcome.AlreadyAbsent;
        // Presence contradicts only absence that is still fresh: absence proven before the copy re-entered the authority
        // set is stale, so a later presence outcome under the same intent stands and removes nothing.
        if (current is "deleted" or "already-absent" && !absent && await ScalarLongAsync(connection, transaction, $"""
                SELECT COUNT(*) FROM locators l WHERE l.store_id = $store AND l.bucket = $bucket AND l.key = $key AND l.version_id = $version
                    AND {FreshAbsenceUnderIntent};
                """, cancellationToken, ("$intent", disposition.IntentId), ("$store", disposition.Locator.StoreId),
                ("$bucket", disposition.Locator.Bucket), ("$key", disposition.Locator.Key), ("$version", disposition.Locator.VersionId))
                .ConfigureAwait(false) != 0)
        {
            return "contradicts-proven-absence";
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE expiry_targets SET outcome = $outcome, outcome_generation = $g, outcome_sequence = $s, outcome_index = $i
            WHERE intent_id = $intent AND store_id = $store AND bucket = $bucket AND key = $key AND version_id = $version;
            """, cancellationToken, ("$outcome", SiloJson.EnumName(disposition.Outcome)), ("$g", at.Generation), ("$s", at.Sequence),
            ("$i", at.Index), ("$intent", disposition.IntentId), ("$store", disposition.Locator.StoreId),
            ("$bucket", disposition.Locator.Bucket), ("$key", disposition.Locator.Key), ("$version", disposition.Locator.VersionId))
            .ConfigureAwait(false);
        if (absent)
        {
            await SetCopyStateAsync(connection, transaction, disposition.Locator, null, "evicted", at, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Set one exact copy's state for every role it serves, or one. A copy that returns from <c>evicted</c> to the
    /// authoritative set records the position where it re-entered, so an intent opened before then must still settle it.
    /// </summary>
    private async ValueTask SetCopyStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloExactLocator locator, SiloRoleKey? role, string state, RecordAt at,
        CancellationToken cancellationToken)
    {
        var roles = new List<SiloRoleKey>();
        using (var command = CreateCommand(connection, transaction, """
            SELECT kind, id, revision FROM locators
            WHERE store_id = $store AND bucket = $bucket AND key = $key AND version_id = $version
                AND ($kind IS NULL OR (kind = $kind AND id = $id AND revision = $revision));
            """, ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key), ("$version", locator.VersionId),
            ("$kind", role is { } key ? KindName(key.Kind) : null), ("$id", role?.Id), ("$revision", role?.Revision)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                roles.Add(new(ParseKind(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
            }
        }
        var unknown = await UnknownIntervalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var affected in roles)
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE locators SET
                    entered_generation = CASE WHEN copy_state = 'evicted' AND $state <> 'evicted' THEN $g ELSE entered_generation END,
                    entered_sequence = CASE WHEN copy_state = 'evicted' AND $state <> 'evicted' THEN $s ELSE entered_sequence END,
                    entered_index = CASE WHEN copy_state = 'evicted' AND $state <> 'evicted' THEN $i ELSE entered_index END,
                    copy_state = $state
                WHERE kind = $kind AND id = $id AND revision = $revision
                    AND store_id = $store AND bucket = $bucket AND key = $key AND version_id = $version;
                """, cancellationToken, ("$state", state), ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index),
                ("$kind", KindName(affected.Kind)), ("$id", affected.Id),
                ("$revision", affected.Revision), ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key),
                ("$version", locator.VersionId)).ConfigureAwait(false);
            await RecomputeRoleAsync(connection, transaction, affected, unknown, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<string?> ApplyAvailabilityAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloAvailabilityRecord availability, RecordAt at,
        CancellationToken cancellationToken)
    {
        var role = availability.Identity.Role;
        var bound = await ReadRoleIdentityAsync(connection, transaction, role, cancellationToken).ConfigureAwait(false);
        if (bound is not { } value || !string.Equals(value.Sha256, availability.Identity.Sha256, StringComparison.Ordinal) ||
            value.Length != availability.Identity.Length)
        {
            return "unknown-role";
        }
        if (availability.Scope == SiloAvailabilityScope.Copy)
        {
            var locator = availability.Locator!;
            var known = await ScalarLongAsync(connection, transaction, """
                SELECT COUNT(*) FROM locators WHERE kind = $kind AND id = $id AND revision = $revision
                    AND store_id = $store AND bucket = $bucket AND key = $key AND version_id = $version;
                """, cancellationToken, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision),
                ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key), ("$version", locator.VersionId))
                .ConfigureAwait(false);
            if (known == 0)
            {
                return "unknown-copy";
            }
            if (availability.State == SiloAvailabilityState.Evicted)
            {
                if (availability.IntentId is null)
                {
                    return "eviction-without-intent";
                }
                var proven = await ScalarLongAsync(connection, transaction, $"""
                    SELECT COUNT(*) FROM locators l WHERE l.kind = $kind AND l.id = $id AND l.revision = $revision
                        AND l.store_id = $store AND l.bucket = $bucket AND l.key = $key AND l.version_id = $version
                        AND {FreshAbsenceUnderIntent};
                    """, cancellationToken, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision),
                    ("$store", locator.StoreId), ("$bucket", locator.Bucket), ("$key", locator.Key),
                    ("$version", locator.VersionId), ("$intent", availability.IntentId)).ConfigureAwait(false);
                if (proven == 0)
                {
                    return "eviction-without-fresh-proven-absence";
                }
            }
            var state = availability.State switch
            {
                SiloAvailabilityState.Evicted => "evicted",
                SiloAvailabilityState.Missing => "missing",
                SiloAvailabilityState.Quarantined => "quarantined",
                _ => "active"
            };
            await SetCopyStateAsync(connection, transaction, locator, role, state, at, cancellationToken).ConfigureAwait(false);
            if (availability.State == SiloAvailabilityState.Restored)
            {
                await RequeueAsync(connection, transaction, role, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }
        if (availability.State == SiloAvailabilityState.PolicyExpired &&
            await RoleExpirySettledAsync(connection, transaction, role, availability.IntentId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE roles SET journaled = $state WHERE kind = $kind AND id = $id AND revision = $revision;
            """, cancellationToken, ("$state", SiloJson.EnumName(availability.State)), ("$kind", KindName(role.Kind)),
            ("$id", role.Id), ("$revision", role.Revision)).ConfigureAwait(false);
        var unknown = await UnknownIntervalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await RecomputeRoleAsync(connection, transaction, role, unknown, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// #1965 7.3 step 9: a role is policy-expired only under an authorized role-scoped intent that covered the complete
    /// authoritative copy set, with proven absence for every target under that same intent, and no copy outside it. A
    /// copy that entered the authoritative set after the intent, by arrival, promotion or restoration after eviction, must
    /// also be proven absent under that intent before it settles, even when a different operation has since removed it.
    /// Proof counts only at or after a copy's latest entry, by ordered generation, segment and record position, so an
    /// intent's earlier disposition of a copy that has since returned, original target or not, is stale.
    /// </summary>
    private static async ValueTask<string?> RoleExpirySettledAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloRoleKey role, string? intentId, CancellationToken cancellationToken)
    {
        if (intentId is null)
        {
            return "role-expiry-without-intent";
        }
        long intentGeneration, intentSequence, intentIndex;
        using (var command = CreateCommand(connection, transaction, """
            SELECT scope, kind, id, revision, covers_authoritative, last_copy, generation, sequence, record_index
            FROM expiry_intents WHERE intent_id = $intent;
            """, ("$intent", intentId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(0) != "role" ||
                reader.GetString(1) != KindName(role.Kind) || reader.GetString(2) != role.Id || reader.GetString(3) != role.Revision)
            {
                return "role-expiry-intent-mismatch";
            }
            if (reader.GetInt64(5) != 1)
            {
                return "role-expiry-without-last-copy-authority";
            }
            if (reader.GetInt64(4) != 1)
            {
                return "role-expiry-incomplete-copy-set";
            }
            (intentGeneration, intentSequence, intentIndex) = (reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8));
        }
        if (await ScalarLongAsync(connection, transaction, $"""
                SELECT COUNT(*) FROM locators l
                WHERE l.kind = $kind AND l.id = $id AND l.revision = $revision AND l.eligible = 1
                    AND (l.entered_generation, l.entered_sequence, l.entered_index) > ($g, $s, $i)
                    AND NOT {FreshAbsenceUnderIntent};
                """, cancellationToken, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision),
                ("$g", intentGeneration), ("$s", intentSequence), ("$i", intentIndex), ("$intent", intentId)).ConfigureAwait(false) != 0)
        {
            return "role-expiry-copy-arrived-after-intent";
        }
        if (await ScalarLongAsync(connection, transaction, """
                SELECT COUNT(*) FROM expiry_targets WHERE intent_id = $intent AND COALESCE(outcome, '') NOT IN ('deleted', 'already-absent');
                """, cancellationToken, ("$intent", intentId)).ConfigureAwait(false) != 0)
        {
            return "role-expiry-unsettled";
        }
        var remaining = await AuthoritativeCopiesAsync(connection, transaction, role, cancellationToken).ConfigureAwait(false);
        return remaining.Count == 0 ? null : "role-expiry-copy-outside-intent";
    }

    private static async ValueTask<string?> ApplyAnchorAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloAnchorRecord anchor, RecordAt at, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO anchors(generation, sequence, record_index, store_id, bucket, key, version_id, checkpoint_sha256, checkpoint_length,
                covers_generation, covers_sequence, covers_sha256)
            VALUES($g, $s, $i, $store, $bucket, $key, $version, $sha, $length, $coversG, $coversS, $coversSha);
            """, cancellationToken, ("$g", at.Generation), ("$s", at.Sequence), ("$i", at.Index), ("$store", anchor.Checkpoint.StoreId),
            ("$bucket", anchor.Checkpoint.Bucket), ("$key", anchor.Checkpoint.Key), ("$version", anchor.Checkpoint.VersionId),
            ("$sha", anchor.CheckpointSha256), ("$length", anchor.CheckpointLength), ("$coversG", anchor.Covers.Generation),
            ("$coversS", anchor.Covers.Sequence), ("$coversSha", anchor.CoversSha256)).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Derive a role's disposition from its journaled state and its eligible copies' physical probes. A copy state never
    /// implies a role state, and absence is concluded only when no authority-unknown interval could hold a later copy.
    /// </summary>
    private async ValueTask<string> RecomputeRoleAsync(
        SqliteConnection connection, SqliteTransaction? transaction, SiloRoleKey role, bool unknownInterval, CancellationToken cancellationToken)
    {
        var journaled = await ScalarStringAsync(connection, transaction,
            "SELECT journaled FROM roles WHERE kind = $kind AND id = $id AND revision = $revision;",
            cancellationToken, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision)).ConfigureAwait(false);
        var probes = new List<(SiloExactLocator Locator, string? Status, long? ProbedUnixMs)>();
        using (var command = CreateCommand(connection, transaction, """
            SELECT store_id, bucket, key, version_id, probe_status, probe_unix_ms FROM locators
            WHERE kind = $kind AND id = $id AND revision = $revision AND eligible = 1 AND copy_state = 'active'
            ORDER BY CASE WHEN store_id = $local THEN 0 ELSE 1 END, generation, sequence, record_index, key, version_id;
            """, ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision), ("$local", Binding.StoreId)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                probes.Add((new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                    NullableString(reader, 4), NullableLong(reader, 5)));
            }
        }
        // Settled authority first, then present physical evidence. A journaled or earlier `missing` observation is only
        // evidence of absence: it never outranks a source that could not be checked, an unprobed source, or an
        // authority-unknown interval in which a later copy may exist.
        var verified = probes.FirstOrDefault(static probe => probe.Status == "verified");
        var disposition = journaled switch
        {
            "policy-expired" => SiloDisposition.PolicyExpired,
            "conflict" => SiloDisposition.Conflict,
            _ when verified.Locator is not null => SiloDisposition.Available,
            _ when probes.Any(static probe => probe.Status == "unavailable") => SiloDisposition.TemporarilyUnavailable,
            _ when probes.Any(static probe => probe.Status is null) => SiloDisposition.Unverified,
            _ => unknownInterval ? SiloDisposition.Unverified : SiloDisposition.Missing
        };
        var available = disposition == SiloDisposition.Available ? verified.Locator : null;
        await ExecuteAsync(connection, transaction, """
            UPDATE roles SET disposition = $disposition, available_store = $store, available_bucket = $bucket, available_key = $key,
                available_version = $version, verified_unix_ms = $verified
            WHERE kind = $kind AND id = $id AND revision = $revision;
            """, cancellationToken, ("$disposition", disposition), ("$store", available?.StoreId), ("$bucket", available?.Bucket),
            ("$key", available?.Key), ("$version", available?.VersionId), ("$verified", available is null ? null : verified.ProbedUnixMs),
            ("$kind", KindName(role.Kind)), ("$id", role.Id), ("$revision", role.Revision)).ConfigureAwait(false);
        return disposition;
    }
}
