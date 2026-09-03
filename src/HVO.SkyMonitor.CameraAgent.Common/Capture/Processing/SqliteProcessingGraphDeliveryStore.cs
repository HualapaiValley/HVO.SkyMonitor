using System.Text;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal enum ProcessingGraphLocalProposalDisposition
{
    Pending,
    Accepted,
    Rejected,
    Expired,
    Superseded
}

internal sealed partial class SqliteCaptureProcessingStore
{
    private const int RevisionFactBatchSize = 128;

    internal async ValueTask<ProcessingGraphLocalProposalDisposition> UpsertDeliveryProposalAsync(
        ProcessingGraphDeliveryProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var definitionJson = ProcessingGraphJson.SerializeCanonical(proposal.Definition);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO processing_graph_delivery_proposals(
                    proposal_id, catalog_revision_id, assignment_id, registration_id,
                    installation_id, installation_public_id, expected_active_revision_id,
                    capability_snapshot_sha256, definition_identity_sha256,
                    shared_plan_identity_sha256, definition_json, issued_unix_ms,
                    expires_unix_ms, disposition, received_unix_ms)
                VALUES ($proposal, $catalog, $assignment, $registration, $installation,
                    $public, $expected, $capability, $definition, $shared, $json,
                    $issued, $expires, 'Pending', $received)
                ON CONFLICT(proposal_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$proposal", proposal.ProposalId.ToString("N"));
            command.Parameters.AddWithValue("$catalog", proposal.CatalogRevisionId.ToString("N"));
            command.Parameters.AddWithValue("$assignment", proposal.AssignmentId.ToString("N"));
            command.Parameters.AddWithValue("$registration", proposal.RegistrationId.ToString("N"));
            command.Parameters.AddWithValue("$installation", proposal.LogicalCameraInstallationId.ToString("N"));
            command.Parameters.AddWithValue("$public", proposal.InstallationPublicId.ToString("N"));
            command.Parameters.AddWithValue("$expected", (object?)proposal.ExpectedActiveLocalRevisionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$capability", proposal.CapabilitySnapshotSha256);
            command.Parameters.AddWithValue("$definition", proposal.DefinitionIdentitySha256);
            command.Parameters.AddWithValue("$shared", proposal.SharedPlanIdentitySha256);
            command.Parameters.AddWithValue("$json", definitionJson);
            command.Parameters.AddWithValue("$issued", proposal.IssuedAtUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$expires", proposal.ExpiresAtUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$received", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT catalog_revision_id, assignment_id, registration_id, installation_id,
                   installation_public_id, expected_active_revision_id, capability_snapshot_sha256,
                   definition_identity_sha256, shared_plan_identity_sha256, definition_json,
                   issued_unix_ms, expires_unix_ms, disposition
            FROM processing_graph_delivery_proposals WHERE proposal_id = $proposal;
            """;
        read.Parameters.AddWithValue("$proposal", proposal.ProposalId.ToString("N"));
        using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The processing graph proposal was not durably stored.");
        }
        var storedDefinition = await reader.GetFieldValueAsync<byte[]>(9, cancellationToken).ConfigureAwait(false);
        var expected = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5);
        if (reader.GetString(0) != proposal.CatalogRevisionId.ToString("N") ||
            reader.GetString(1) != proposal.AssignmentId.ToString("N") ||
            reader.GetString(2) != proposal.RegistrationId.ToString("N") ||
            reader.GetString(3) != proposal.LogicalCameraInstallationId.ToString("N") ||
            reader.GetString(4) != proposal.InstallationPublicId.ToString("N") || expected != proposal.ExpectedActiveLocalRevisionId ||
            reader.GetString(6) != proposal.CapabilitySnapshotSha256 ||
            reader.GetString(7) != proposal.DefinitionIdentitySha256 ||
            reader.GetString(8) != proposal.SharedPlanIdentitySha256 ||
            !storedDefinition.AsSpan().SequenceEqual(definitionJson) ||
            reader.GetInt64(10) != proposal.IssuedAtUtc.ToUnixTimeMilliseconds() ||
            reader.GetInt64(11) != proposal.ExpiresAtUtc.ToUnixTimeMilliseconds())
        {
            throw new ProcessingGraphStoreConflictException(
                "The processing graph proposal identity has different immutable content.");
        }
        return Enum.Parse<ProcessingGraphLocalProposalDisposition>(reader.GetString(12));
    }

    internal ValueTask AcceptDeliveryProposalAsync(
        Guid proposalId,
        ProcessingGraphRevisionState revision,
        CancellationToken cancellationToken)
        => SettleDeliveryProposalAsync(
            proposalId,
            ProcessingGraphLocalProposalDisposition.Accepted,
            ProcessingGraphDeliveryFactKind.Accepted,
            null,
            revision,
            cancellationToken);

    internal ValueTask RejectDeliveryProposalAsync(
        Guid proposalId,
        string reasonCode,
        CancellationToken cancellationToken)
        => SettleDeliveryProposalAsync(
            proposalId,
            ProcessingGraphLocalProposalDisposition.Rejected,
            ProcessingGraphDeliveryFactKind.Rejected,
            reasonCode,
            null,
            cancellationToken);

    internal ValueTask ExpireDeliveryProposalAsync(
        Guid proposalId,
        string reasonCode,
        CancellationToken cancellationToken)
        => SettleDeliveryProposalAsync(
            proposalId,
            ProcessingGraphLocalProposalDisposition.Expired,
            ProcessingGraphDeliveryFactKind.Expired,
            reasonCode,
            null,
            cancellationToken);

    internal async ValueTask RecordDeliveredActivationAsync(
        string localRevisionId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var queued = await QueueRevisionFactsAsync(
            connection,
            transaction,
            localRevisionId,
            ProcessingGraphDeliveryFactKind.Activated,
            null,
            excludeRevision: false,
            RevisionFactBatchSize,
            cancellationToken).ConfigureAwait(false);
        if (queued < RevisionFactBatchSize)
        {
            _ = await QueueRevisionFactsAsync(
                connection,
                transaction,
                localRevisionId,
                ProcessingGraphDeliveryFactKind.RolledBack,
                "local-revision-changed",
                excludeRevision: true,
                RevisionFactBatchSize - queued,
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProcessingGraphDeliveryFactV1?> ReadPendingDeliveryFactAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT fact_id, proposal_id, fact_kind, occurred_unix_ms, local_revision_id,
                   definition_identity_sha256, shared_plan_identity_sha256,
                   local_plan_identity_sha256, reason_code
            FROM processing_graph_delivery_facts
            WHERE delivery_state = 'Pending' AND next_attempt_unix_ms <= $now
            ORDER BY occurred_unix_ms,
                     CASE fact_kind WHEN 'Accepted' THEN 0 WHEN 'Rejected' THEN 0 WHEN 'Expired' THEN 0 ELSE 1 END,
                     fact_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new(
            ProcessingGraphDeliverySchemaVersions.Current,
            Guid.ParseExact(reader.GetString(0), "N"),
            Guid.ParseExact(reader.GetString(1), "N"),
            Enum.Parse<ProcessingGraphDeliveryFactKind>(reader.GetString(2)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8));
    }

    internal async ValueTask AcknowledgeDeliveryFactAsync(Guid factId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE processing_graph_delivery_facts
            SET delivery_state = 'Acknowledged', acknowledged_unix_ms = $now,
                last_reason_code = NULL
            WHERE fact_id = $fact AND delivery_state = 'Pending';
            """;
        command.Parameters.AddWithValue("$fact", factId.ToString("N"));
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ProcessingGraphStoreConflictException("The processing graph delivery fact is not pending.");
        }
    }

    internal async ValueTask SupersedeDeliveryProposalAsync(
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        if (proposalId == Guid.Empty)
        {
            throw new ArgumentException("The processing graph proposal identity is required.", nameof(proposalId));
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var acknowledgedUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        using (var proposal = connection.CreateCommand())
        {
            proposal.Transaction = transaction;
            proposal.CommandText = """
                UPDATE processing_graph_delivery_proposals
                SET disposition = 'Superseded', disposition_reason = 'central-proposal-superseded',
                    settled_unix_ms = CASE WHEN disposition = 'Superseded' THEN settled_unix_ms ELSE $acknowledged END
                WHERE proposal_id = $proposal;
                """;
            proposal.Parameters.AddWithValue("$proposal", proposalId.ToString("N"));
            proposal.Parameters.AddWithValue("$acknowledged", acknowledgedUnixMs);
            if (await proposal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProcessingGraphStoreConflictException("The processing graph proposal does not exist.");
            }
        }
        using (var facts = connection.CreateCommand())
        {
            facts.Transaction = transaction;
            facts.CommandText = """
                UPDATE processing_graph_delivery_facts
                SET delivery_state = 'Acknowledged', acknowledged_unix_ms = $acknowledged,
                    last_reason_code = NULL
                WHERE proposal_id = $proposal AND delivery_state = 'Pending';
                """;
            facts.Parameters.AddWithValue("$proposal", proposalId.ToString("N"));
            facts.Parameters.AddWithValue("$acknowledged", acknowledgedUnixMs);
            _ = await facts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask RetryDeliveryFactAsync(
        Guid factId,
        DateTimeOffset nextAttemptUtc,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        reasonCode = BoundReason(reasonCode);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE processing_graph_delivery_facts
            SET attempt_count = attempt_count + 1, next_attempt_unix_ms = $next,
                last_reason_code = $reason
            WHERE fact_id = $fact AND delivery_state = 'Pending';
            """;
        command.Parameters.AddWithValue("$fact", factId.ToString("N"));
        command.Parameters.AddWithValue("$next", nextAttemptUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$reason", reasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ProcessingGraphStoreConflictException("The processing graph delivery fact is not pending.");
        }
    }

    internal async ValueTask<ProcessingGraphDeliveryBacklog> ReadDeliveryBacklogAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM processing_graph_delivery_proposals WHERE disposition = 'Pending'),
                (SELECT MIN(received_unix_ms) FROM processing_graph_delivery_proposals WHERE disposition = 'Pending'),
                (SELECT COUNT(*) FROM processing_graph_delivery_facts WHERE delivery_state = 'Pending'),
                (SELECT MIN(occurred_unix_ms) FROM processing_graph_delivery_facts WHERE delivery_state = 'Pending');
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The processing graph delivery backlog is unavailable.");
        }
        return new(
            reader.GetInt64(0),
            await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            reader.GetInt64(2),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
    }

    private async ValueTask SettleDeliveryProposalAsync(
        Guid proposalId,
        ProcessingGraphLocalProposalDisposition disposition,
        ProcessingGraphDeliveryFactKind factKind,
        string? reasonCode,
        ProcessingGraphRevisionState? revision,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (reasonCode is not null)
        {
            reasonCode = BoundReason(reasonCode);
        }
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var observedAt = _timeProvider.GetUtcNow();
        DateTimeOffset settledAt;
        using (var proposal = connection.CreateCommand())
        {
            proposal.Transaction = transaction;
            proposal.CommandText = "SELECT issued_unix_ms, expires_unix_ms FROM processing_graph_delivery_proposals WHERE proposal_id = $proposal;";
            proposal.Parameters.AddWithValue("$proposal", proposalId.ToString("N"));
            using var reader = await proposal.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new ProcessingGraphStoreConflictException("The processing graph proposal does not exist.");
            }
            var issuedUnixMs = reader.GetInt64(0);
            var expiresUnixMs = reader.GetInt64(1);
            if (factKind is ProcessingGraphDeliveryFactKind.Accepted or ProcessingGraphDeliveryFactKind.Rejected &&
                observedAt.ToUnixTimeMilliseconds() > expiresUnixMs)
            {
                disposition = ProcessingGraphLocalProposalDisposition.Expired;
                factKind = ProcessingGraphDeliveryFactKind.Expired;
                reasonCode = "proposal-expired";
                revision = null;
            }
            var floorUnixMs = factKind == ProcessingGraphDeliveryFactKind.Expired
                ? expiresUnixMs
                : issuedUnixMs;
            settledAt = DateTimeOffset.FromUnixTimeMilliseconds(
                Math.Max(observedAt.ToUnixTimeMilliseconds(), floorUnixMs));
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_graph_delivery_proposals
                SET disposition = $disposition, disposition_reason = $reason,
                    local_revision_id = $local, local_plan_identity_sha256 = $local_plan,
                    settled_unix_ms = $settled
                WHERE proposal_id = $proposal AND disposition = 'Pending';
                """;
            update.Parameters.AddWithValue("$proposal", proposalId.ToString("N"));
            update.Parameters.AddWithValue("$disposition", disposition.ToString());
            update.Parameters.AddWithValue("$reason", (object?)reasonCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$local", (object?)revision?.RevisionId ?? DBNull.Value);
            update.Parameters.AddWithValue("$local_plan", (object?)revision?.LocalPlanIdentitySha256 ?? DBNull.Value);
            update.Parameters.AddWithValue("$settled", settledAt.ToUnixTimeMilliseconds());
            var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed == 0)
            {
                using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = "SELECT disposition FROM processing_graph_delivery_proposals WHERE proposal_id = $proposal;";
                read.Parameters.AddWithValue("$proposal", proposalId.ToString("N"));
                var existing = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                if (!string.Equals(existing, disposition.ToString(), StringComparison.Ordinal))
                {
                    throw new ProcessingGraphStoreConflictException("The processing graph proposal has already settled.");
                }
            }
        }
        await InsertDeliveryFactAsync(
            connection,
            transaction,
            proposalId,
            factKind,
            revision?.RevisionId,
            revision?.DefinitionIdentitySha256,
            revision?.SharedPlanIdentitySha256,
            revision?.LocalPlanIdentitySha256,
            reasonCode,
            settledAt,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> QueueRevisionFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string localRevisionId,
        ProcessingGraphDeliveryFactKind kind,
        string? reasonCode,
        bool excludeRevision,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT proposal_id, definition_identity_sha256, shared_plan_identity_sha256,
                   local_plan_identity_sha256, local_revision_id, issued_unix_ms,
                   (SELECT occurred_unix_ms FROM processing_graph_delivery_facts
                     WHERE proposal_id = processing_graph_delivery_proposals.proposal_id
                       AND fact_kind = 'Accepted'),
                   COALESCE((SELECT MAX(occurred_unix_ms) FROM processing_graph_delivery_facts
                     WHERE proposal_id = processing_graph_delivery_proposals.proposal_id
                       AND fact_kind = CASE $kind WHEN 'Activated' THEN 'RolledBack' ELSE 'Activated' END),
                     issued_unix_ms)
            FROM processing_graph_delivery_proposals
            WHERE disposition = 'Accepted'
              AND (($exclude = 0 AND local_revision_id = $revision)
                   OR ($exclude = 1 AND local_revision_id <> $revision))
              AND (($kind = 'Activated' AND
                       (SELECT COUNT(*) FROM processing_graph_delivery_facts
                        WHERE proposal_id = processing_graph_delivery_proposals.proposal_id
                          AND fact_kind = 'Activated') =
                       (SELECT COUNT(*) FROM processing_graph_delivery_facts
                        WHERE proposal_id = processing_graph_delivery_proposals.proposal_id
                          AND fact_kind = 'RolledBack'))
                OR ($kind = 'RolledBack' AND
                       (SELECT COUNT(*) FROM processing_graph_delivery_facts
                        WHERE proposal_id = processing_graph_delivery_proposals.proposal_id
                          AND fact_kind = 'Activated') =
                       (SELECT COUNT(*) FROM processing_graph_delivery_facts
                        WHERE proposal_id = processing_graph_delivery_proposals.proposal_id
                          AND fact_kind = 'RolledBack') + 1))
            ORDER BY issued_unix_ms, proposal_id
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$revision", localRevisionId);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$exclude", excludeRevision ? 1 : 0);
        command.Parameters.AddWithValue("$maximum", maximumCount);
        var rows = new List<(Guid ProposalId, string Definition, string Shared, string LocalPlan,
            string LocalRevision, long EarliestOccurrence)>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    Math.Max(reader.GetInt64(5), Math.Max(reader.GetInt64(6), reader.GetInt64(7)))));
            }
        }
        foreach (var row in rows)
        {
            await InsertDeliveryFactAsync(
                connection,
                transaction,
                row.ProposalId,
                kind,
                row.LocalRevision,
                row.Definition,
                row.Shared,
                row.LocalPlan,
                reasonCode,
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), row.EarliestOccurrence)),
                cancellationToken).ConfigureAwait(false);
        }
        return rows.Count;
    }

    private static async ValueTask InsertDeliveryFactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid proposalId,
        ProcessingGraphDeliveryFactKind kind,
        string? localRevisionId,
        string? definitionIdentity,
        string? sharedPlanIdentity,
        string? localPlanIdentity,
        string? reasonCode,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var now = occurredAt.ToUnixTimeMilliseconds();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_graph_delivery_facts(
                fact_id, proposal_id, fact_kind, occurred_unix_ms, local_revision_id,
                definition_identity_sha256, shared_plan_identity_sha256,
                local_plan_identity_sha256, reason_code, delivery_state,
                attempt_count, next_attempt_unix_ms)
            VALUES ($fact, $proposal, $kind, $occurred, $local, $definition, $shared,
                    $local_plan, $reason, 'Pending', 0, $occurred)
            ON CONFLICT(proposal_id)
                WHERE fact_kind IN ('Accepted', 'Rejected', 'Expired') DO NOTHING;
            """;
        command.Parameters.AddWithValue("$fact", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$proposal", proposalId.ToString("N"));
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$occurred", now);
        command.Parameters.AddWithValue("$local", (object?)localRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$definition", (object?)definitionIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$shared", (object?)sharedPlanIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$local_plan", (object?)localPlanIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)reasonCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string BoundReason(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return reasonCode.Length <= 128 ? reasonCode : reasonCode[..128];
    }
}
