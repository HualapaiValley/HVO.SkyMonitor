using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal enum DurableProcessingNodeStatus
{
    Completed,
    Skipped,
    RetryableFailure,
    TerminalFailure
}

internal sealed record DurableProcessingOutput(
    string OutputIdentitySha256,
    Guid ArtifactId,
    string PayloadRelativePath,
    string SidecarRelativePath,
    byte[] EvidenceJson,
    ReconstructionDescriptor? Descriptor,
    IDurableProcessingProductManifest? ProductManifest,
    string RecipeIdentitySha256,
    IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    ProcessingCompatibilityIdentity Compatibility,
    TimeSpan TotalIntegration,
    long CaptureSequence,
    string? LegacyRecipeVersion)
{
    internal CaptureIdentityDescriptor Capture => Descriptor?.Capture ?? ProductManifest?.Capture
        ?? throw new InvalidDataException("Durable processing output has no capture descriptor.");

    internal ArtifactDescriptor Artifact => Descriptor?.Artifact ?? ProductManifest?.Artifact
        ?? throw new InvalidDataException("Durable processing output has no artifact descriptor.");
}

internal sealed record DurableProcessingNodeInput(
    int Ordinal,
    string Kind,
    string? Name,
    Guid? ArtifactId,
    FrameArtifactRole? Role,
    string? Variant,
    string? RecipeIdentitySha256,
    string? SchemaVersion,
    string? IdentitySha256,
    bool Selected);

internal sealed record DurableProcessingNode(
    Guid CaptureId,
    string NodeId,
    bool Required,
    DurableProcessingNodeStatus Status,
    string? Reason,
    int Attempt,
    string PlanSha256,
    string? ProcessingProfileIdentitySha256,
    DateTimeOffset? StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan? Duration,
    ProcessingOutcomeStatus? Outcome,
    IReadOnlyList<DurableProcessingNodeInput>? Inputs,
    IReadOnlyList<DurableProcessingOutput> Outputs);

internal sealed record DurableGalleryProcessingNode(
    Guid CaptureId,
    string NodeId,
    bool Required,
    DurableProcessingNodeStatus Status,
    string? RecipeName,
    string? OutputRole,
    string? OutputVariant,
    IReadOnlyList<DurableProcessingOutput> Outputs);

internal sealed record DurableGalleryProcessingProjection(
    IReadOnlyList<DurableGalleryProcessingNode> Nodes,
    IReadOnlySet<Guid> TruncatedNodeCaptures,
    IReadOnlySet<Guid> TruncatedOutputCaptures);

internal sealed record DurableGalleryProcessingNodeDetail(
    string NodeId,
    IReadOnlyList<string> Dependencies,
    int Attempt,
    DateTimeOffset CompletedUtc,
    string? Reason,
    string? ProcessingProfileIdentitySha256,
    DateTimeOffset? StartedUtc,
    TimeSpan? Duration,
    ProcessingOutcomeStatus? Outcome,
    IReadOnlyList<DurableProcessingNodeInput>? Inputs,
    bool InputsTruncated);

internal sealed record DurableRawProcessingInput(
    ReconstructionDescriptor Descriptor,
    string PayloadRelativePath);

internal sealed record CaptureProcessingOperationalState(
    long PendingCount,
    long RetryCount,
    long TerminalCount,
    DateTimeOffset? OldestPendingUtc);

internal sealed class SqliteCaptureProcessingStore : IDisposable
{
    internal const int MaximumGalleryInputsPerNode = 8;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly string _databasePath;
    private readonly int _busyTimeoutSeconds;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public SqliteCaptureProcessingStore(
        IOptions<CameraAgentHostOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var values = options.Value;
        _root = Path.GetFullPath(values.RawIngressRoot);
        _databasePath = Path.Combine(_root, "journal", "raw-ingress.db");
        _busyTimeoutSeconds = values.RawIngressSqliteBusyTimeoutSeconds;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The migration statement is selected only from internal constants.")]
    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            EnsureDatabaseFilesArePhysical();
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            EnsureDatabaseFilesArePhysical();
            using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'capture_processing_schema';";
                var schemaExists = Convert.ToInt32(
                    await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) == 1;
                var version = 0;
                if (schemaExists)
                {
                    versionCommand.CommandText = "SELECT COALESCE((SELECT version FROM capture_processing_schema WHERE schema_key = 1), 0);";
                    version = Convert.ToInt32(
                        await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                if (version > 3)
                {
                    throw new InvalidOperationException($"Capture processing schema {version} is newer than supported schema 3.");
                }
                if (version < 3)
                {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
                    using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = version switch
                    {
                        0 => SchemaSql,
                        1 => string.Concat(ProcessingV2MigrationSql, ProcessingV3MigrationSql),
                        2 => ProcessingV3MigrationSql,
                        _ => throw new InvalidOperationException($"Unsupported capture processing schema {version}.")
                    };
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(
                await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Capture processing SQLite integrity check failed.");
            }
            using var schemaObjects = connection.CreateCommand();
            schemaObjects.CommandText = """
                SELECT COUNT(*) FROM sqlite_master WHERE name IN (
                    'capture_processing_schema', 'processing_nodes', 'processing_outputs',
                    'ix_processing_outputs_capture_node', 'ix_processing_outputs_window',
                    'ix_processing_nodes_status', 'ix_processing_nodes_recipe',
                    'ix_processing_outputs_role', 'ix_processing_outputs_recipe',
                    'processing_node_inputs', 'ix_processing_node_inputs_artifact');
                """;
            if (Convert.ToInt32(
                await schemaObjects.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 11)
            {
                throw new InvalidDataException("Capture processing SQLite schema is incomplete or drifted.");
            }
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    internal async ValueTask<DurableProcessingNode?> ReadNodeAsync(
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT required, status, reason, attempt, plan_sha256,
                   processing_profile_identity_sha256, started_unix_ms, completed_unix_ms,
                   duration_ticks, outcome, input_evidence_version
            FROM processing_nodes
            WHERE capture_id = $capture_id AND node_id = $node_id;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var required = reader.GetInt64(0) != 0;
        var status = Enum.Parse<DurableProcessingNodeStatus>(reader.GetString(1));
        var reason = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2);
        var attempt = reader.GetInt32(3);
        var planSha256 = reader.GetString(4);
        var profileIdentity = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5);
        DateTimeOffset? startedUtc = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6));
        var completedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7));
        TimeSpan? duration = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
            ? null
            : TimeSpan.FromTicks(reader.GetInt64(8));
        ProcessingOutcomeStatus? outcome = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false)
            ? null
            : Enum.Parse<ProcessingOutcomeStatus>(reader.GetString(9));
        var hasInputEvidence = !await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false);
        await reader.DisposeAsync().ConfigureAwait(false);
        var outputs = await ReadOutputsAsync(connection, captureId, nodeId, cancellationToken).ConfigureAwait(false);
        var inputs = hasInputEvidence
            ? await ReadInputsAsync(connection, captureId, nodeId, cancellationToken).ConfigureAwait(false)
            : null;
        return new DurableProcessingNode(
            captureId, nodeId, required, status, reason, attempt, planSha256, profileIdentity,
            startedUtc, completedUtc, duration, outcome, inputs, outputs);
    }

    internal async ValueTask WriteNodeAsync(
        Guid captureId,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        int attempt,
        string? processingProfileIdentitySha256,
        DateTimeOffset? startedUtc,
        DateTimeOffset completedUtc,
        TimeSpan? duration,
        ProcessingOutcomeStatus? outcome,
        IReadOnlyList<DurableProcessingNodeInput> inputs,
        long workId,
        string? leaseToken,
        IReadOnlyList<DurableProcessingOutput> outputs,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        await EnsureLeaseAsync(connection, transaction, workId, leaseToken, cancellationToken).ConfigureAwait(false);
        foreach (var output in outputs)
        {
            await InsertOutputAsync(connection, transaction, captureId, node.Id, output, cancellationToken).ConfigureAwait(false);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_nodes(
                capture_id, node_id, required, dependencies_json, recipe_name, output_role, output_variant,
                plan_sha256, status, reason, attempt, input_evidence_version,
                processing_profile_identity_sha256, started_unix_ms, completed_unix_ms, duration_ticks, outcome)
            VALUES (
                $capture_id, $node_id, $required, $dependencies_json, $recipe_name, $output_role, $output_variant,
                $plan_sha256, $status, $reason, $attempt, 1,
                $processing_profile, $started_unix_ms, $completed_unix_ms, $duration_ticks, $outcome)
            ON CONFLICT(capture_id, node_id) DO UPDATE SET
                required = excluded.required,
                dependencies_json = excluded.dependencies_json,
                recipe_name = excluded.recipe_name,
                output_role = excluded.output_role,
                output_variant = excluded.output_variant,
                plan_sha256 = excluded.plan_sha256,
                status = excluded.status,
                reason = excluded.reason,
                attempt = excluded.attempt,
                input_evidence_version = excluded.input_evidence_version,
                processing_profile_identity_sha256 = excluded.processing_profile_identity_sha256,
                started_unix_ms = excluded.started_unix_ms,
                completed_unix_ms = excluded.completed_unix_ms,
                duration_ticks = excluded.duration_ticks,
                outcome = excluded.outcome;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", node.Id);
        command.Parameters.AddWithValue("$required", node.Required ? 1 : 0);
        command.Parameters.AddWithValue("$dependencies_json", JsonSerializer.Serialize(node.Dependencies, SerializerOptions));
        command.Parameters.AddWithValue("$recipe_name", (object?)node.RecipeName ?? DBNull.Value);
        command.Parameters.AddWithValue("$output_role", node.OutputRole?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$output_variant", (object?)node.OutputVariant ?? DBNull.Value);
        command.Parameters.AddWithValue("$plan_sha256", node.PlanSha256);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempt", attempt);
        command.Parameters.AddWithValue("$processing_profile", (object?)processingProfileIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$started_unix_ms", startedUtc is null ? DBNull.Value : startedUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$completed_unix_ms", completedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$duration_ticks", duration is null ? DBNull.Value : duration.Value.Ticks);
        command.Parameters.AddWithValue("$outcome", outcome?.ToString() ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        using (var deleteInputs = connection.CreateCommand())
        {
            deleteInputs.Transaction = transaction;
            deleteInputs.CommandText = "DELETE FROM processing_node_inputs WHERE capture_id = $capture_id AND node_id = $node_id;";
            deleteInputs.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
            deleteInputs.Parameters.AddWithValue("$node_id", node.Id);
            await deleteInputs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var input in inputs)
        {
            await InsertInputAsync(connection, transaction, captureId, node.Id, input, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask DeleteOutputlessNodeAsync(
        Guid captureId,
        string nodeId,
        long workId,
        string? leaseToken,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        await EnsureLeaseAsync(connection, transaction, workId, leaseToken, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM processing_nodes
            WHERE capture_id = $capture_id AND node_id = $node_id
              AND NOT EXISTS (
                  SELECT 1 FROM processing_outputs
                  WHERE capture_id = $capture_id AND node_id = $node_id);
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM processing_nodes
                WHERE capture_id = $capture_id AND node_id = $node_id);
            """;
        verify.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        verify.Parameters.AddWithValue("$node_id", nodeId);
        if (Convert.ToInt32(await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidDataException("A memory-only processing node has durable outputs and cannot be cleared.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertInputAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        string nodeId,
        DurableProcessingNodeInput input,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_node_inputs(
                capture_id, node_id, input_ordinal, kind, name, artifact_id, role, variant,
                recipe_identity_sha256, schema_version, identity_sha256, selected_flag)
            VALUES(
                $capture_id, $node_id, $ordinal, $kind, $name, $artifact_id, $role, $variant,
                $recipe_identity, $schema_version, $identity, $selected);
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$ordinal", input.Ordinal);
        command.Parameters.AddWithValue("$kind", input.Kind);
        command.Parameters.AddWithValue("$name", (object?)input.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifact_id", input.ArtifactId?.ToString("N") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$role", input.Role?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$variant", (object?)input.Variant ?? DBNull.Value);
        command.Parameters.AddWithValue("$recipe_identity", (object?)input.RecipeIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$schema_version", (object?)input.SchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$identity", (object?)input.IdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$selected", input.Selected ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<DurableProcessingNodeInput>> ReadInputsAsync(
        SqliteConnection connection,
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_ordinal, kind, name, artifact_id, role, variant,
                   recipe_identity_sha256, schema_version, identity_sha256, selected_flag
            FROM processing_node_inputs
            WHERE capture_id = $capture_id AND node_id = $node_id
            ORDER BY input_ordinal
            LIMIT 129;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        var values = new List<DurableProcessingNodeInput>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new DurableProcessingNodeInput(
                reader.GetInt32(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : Guid.ParseExact(reader.GetString(3), "N"),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : Enum.Parse<FrameArtifactRole>(reader.GetString(4)),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7),
                await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(8),
                reader.GetBoolean(9)));
        }
        if (values.Count > 128)
        {
            throw new InvalidDataException("Processing node input evidence exceeds its durable bound.");
        }
        return values;
    }

    private async ValueTask EnsureLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long workId,
        string? leaseToken,
        CancellationToken cancellationToken)
    {
        if (workId == 0)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(leaseToken))
        {
            throw new InvalidOperationException("Durable processing requires a lane lease token.");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM capture_lane_work
            WHERE work_id = $work_id AND state = 'leased' AND lease_token = $lease_token
              AND lease_expires_unix_ms > $now;
            """;
        command.Parameters.AddWithValue("$work_id", workId);
        command.Parameters.AddWithValue("$lease_token", leaseToken);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new InvalidOperationException("Capture processing lease is stale and cannot commit durable state.");
        }
    }

    internal async ValueTask<IReadOnlyList<DurableProcessingOutput>> ReadRecentOutputsAsync(
        string agentId,
        long currentCaptureSequence,
        string nodeId,
        FrameArtifactRole role,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output.output_identity_sha256, output.artifact_id, output.payload_relative_path, output.sidecar_relative_path,
                   output.descriptor_json, output.capture_id, output.agent_id, output.node_id, output.role, output.variant,
                   output.recipe_identity_sha256, output.algorithms_json, output.compatibility_json, output.total_integration_ticks,
                   output.capture_sequence, output.legacy_recipe_version
            FROM processing_outputs AS output
            WHERE output.agent_id = $agent_id
              AND output.capture_sequence <= $current_capture_sequence
              AND output.node_id = $node_id AND output.role = $role
            ORDER BY output.capture_sequence DESC, output.output_identity_sha256 DESC
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$current_capture_sequence", currentCaptureSequence);
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$role", role.ToString());
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var values = (await ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false))
            .Select(static row => row.Output)
            .ToList();
        values.Reverse();
        return values;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The generated placeholders contain only bounded integer ordinals; capture identities remain parameterized.")]
    internal async ValueTask<DurableGalleryProcessingProjection> ReadGalleryNodesAsync(
        IReadOnlyList<Guid> captureIds,
        int maximumNodesPerCapture,
        int maximumOutputsPerCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureIds);
        if (captureIds.Count == 0)
        {
            return new([], new HashSet<Guid>(), new HashSet<Guid>());
        }
        if (captureIds.Count > 101)
        {
            throw new ArgumentOutOfRangeException(nameof(captureIds));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var placeholders = string.Join(", ", Enumerable.Range(0, captureIds.Count).Select(static index => $"$capture{index}"));
        var nodes = new List<(Guid CaptureId, string NodeId, bool Required, DurableProcessingNodeStatus Status, string? RecipeName, string? OutputRole, string? OutputVariant)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                WITH ranked_nodes AS (
                    SELECT capture_id, node_id, required, status, recipe_name, output_role, output_variant,
                           ROW_NUMBER() OVER (PARTITION BY capture_id ORDER BY node_id) AS gallery_rank
                    FROM processing_nodes
                    WHERE capture_id IN ({placeholders})
                )
                SELECT capture_id, node_id, required, status, recipe_name, output_role, output_variant
                FROM ranked_nodes
                WHERE gallery_rank <= $maximum_nodes
                ORDER BY capture_id, node_id;
                """;
            AddCaptureParameters(command, captureIds);
            command.Parameters.AddWithValue("$maximum_nodes", maximumNodesPerCapture + 1);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nodes.Add((
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    Enum.Parse<DurableProcessingNodeStatus>(reader.GetString(3)),
                    await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
                    await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                    await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6)));
            }
        }

        List<(Guid CaptureId, string NodeId, DurableProcessingOutput Output)> outputRows;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                WITH ranked_outputs AS (
                    SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
                           descriptor_json, capture_id, agent_id, node_id, role, variant, recipe_identity_sha256,
                           algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                           legacy_recipe_version,
                           ROW_NUMBER() OVER (
                               PARTITION BY capture_id ORDER BY node_id, output_identity_sha256) AS gallery_rank
                    FROM processing_outputs
                    WHERE capture_id IN ({placeholders})
                )
                SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
                       descriptor_json, capture_id, agent_id, node_id, role, variant, recipe_identity_sha256,
                       algorithms_json, compatibility_json, total_integration_ticks, capture_sequence, legacy_recipe_version
                FROM ranked_outputs
                WHERE gallery_rank <= $maximum_outputs
                ORDER BY capture_id, node_id, output_identity_sha256;
                """;
            AddCaptureParameters(command, captureIds);
            command.Parameters.AddWithValue("$maximum_outputs", maximumOutputsPerCapture + 1);
            outputRows = await ReadOutputRowsAsync(command, cancellationToken, skipInvalid: true).ConfigureAwait(false);
        }

        var truncatedNodes = nodes.GroupBy(static node => node.CaptureId)
            .Where(group => group.Count() > maximumNodesPerCapture)
            .Select(static group => group.Key)
            .ToHashSet();
        var retainedNodes = nodes.GroupBy(static node => node.CaptureId)
            .SelectMany(group => group.Take(maximumNodesPerCapture))
            .ToArray();
        var retainedNodeKeys = retainedNodes
            .Select(static node => (node.CaptureId, node.NodeId))
            .ToHashSet();
        var truncatedOutputs = outputRows.GroupBy(static row => row.CaptureId)
            .Where(group => group.Count() > maximumOutputsPerCapture)
            .Select(static group => group.Key)
            .ToHashSet();
        truncatedOutputs.UnionWith(outputRows
            .Where(row => !retainedNodeKeys.Contains((row.CaptureId, row.NodeId)))
            .Select(static row => row.CaptureId));
        outputRows = outputRows.GroupBy(static row => row.CaptureId)
            .SelectMany(group => group.Take(maximumOutputsPerCapture))
            .ToList();
        var outputs = outputRows.ToLookup(static row => (row.CaptureId, row.NodeId), static row => row.Output);
        var projected = retainedNodes.Select(node => new DurableGalleryProcessingNode(
            node.CaptureId,
            node.NodeId,
            node.Required,
            node.Status,
            node.RecipeName,
            node.OutputRole,
            node.OutputVariant,
            outputs[(node.CaptureId, node.NodeId)].ToArray())).ToArray();
        return new(projected, truncatedNodes, truncatedOutputs);
    }

    internal async ValueTask<IReadOnlyList<DurableGalleryProcessingNodeDetail>> ReadGalleryNodeDetailsAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, dependencies_json, attempt, completed_unix_ms, reason,
                   processing_profile_identity_sha256, started_unix_ms, duration_ticks, outcome,
                   input_evidence_version
            FROM processing_nodes
            WHERE capture_id = $capture_id
            ORDER BY node_id
            LIMIT 65;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        var rows = new List<(string NodeId, string[] Dependencies, int Attempt, DateTimeOffset CompletedUtc,
            string? Reason, string? Profile, DateTimeOffset? StartedUtc, TimeSpan? Duration,
            ProcessingOutcomeStatus? Outcome, bool HasInputs)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dependencies = JsonSerializer.Deserialize<string[]>(reader.GetString(1), SerializerOptions)
                ?? throw new InvalidDataException("Processing dependencies are invalid.");
            if (dependencies.Length > 64 || dependencies.Any(static dependency =>
                    string.IsNullOrWhiteSpace(dependency) || dependency.Length > 128))
            {
                throw new InvalidDataException("Processing dependencies exceed gallery bounds.");
            }
            rows.Add((
                reader.GetString(0),
                dependencies,
                reader.GetInt32(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                    ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                    ? null : TimeSpan.FromTicks(reader.GetInt64(7)),
                await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
                    ? null : Enum.Parse<ProcessingOutcomeStatus>(reader.GetString(8)),
                !await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false)));
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        var details = new List<DurableGalleryProcessingNodeDetail>(Math.Min(64, rows.Count));
        foreach (var row in rows.Take(64))
        {
            var inputs = row.HasInputs
                ? await ReadInputsAsync(connection, captureId, row.NodeId, cancellationToken).ConfigureAwait(false)
                : null;
            details.Add(new DurableGalleryProcessingNodeDetail(
                row.NodeId, row.Dependencies, row.Attempt, row.CompletedUtc, row.Reason, row.Profile,
                row.StartedUtc, row.Duration, row.Outcome,
                inputs?.Take(MaximumGalleryInputsPerNode).ToArray(),
                inputs is not null && inputs.Count > MaximumGalleryInputsPerNode));
        }
        return details;
    }

    internal async ValueTask<IReadOnlyDictionary<Guid, bool>> ReadGalleryRetentionStatesAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output.artifact_id,
                   CASE WHEN (
                       SELECT COUNT(*)
                       FROM processing_outputs newer
                       WHERE newer.agent_id = output.agent_id AND newer.node_id = output.node_id
                         AND (newer.capture_sequence > output.capture_sequence OR
                              (newer.capture_sequence = output.capture_sequence AND
                               newer.output_identity_sha256 > output.output_identity_sha256))) < 100
                     OR EXISTS (
                       SELECT 1
                       FROM raw_captures raw
                       JOIN capture_lane_work work ON work.raw_capture_row_id = raw.raw_capture_row_id
                       WHERE raw.capture_id = output.capture_id AND work.lane_name = 'standard'
                         AND work.state NOT IN ('completed', 'abandoned'))
                   THEN 1 ELSE 0 END
            FROM processing_outputs output
            WHERE output.capture_id = $capture_id
            LIMIT 129;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        var values = new Dictionary<Guid, bool>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (values.Count == 128)
            {
                break;
            }
            if (Guid.TryParseExact(reader.GetString(0), "N", out var artifactId))
            {
                values[artifactId] = reader.GetBoolean(1);
            }
        }
        return values;
    }

    private static void AddCaptureParameters(SqliteCommand command, IReadOnlyList<Guid> captureIds)
    {
        for (var index = 0; index < captureIds.Count; index++)
        {
            command.Parameters.AddWithValue($"$capture{index}", captureIds[index].ToString("N"));
        }
    }

    internal async ValueTask<IReadOnlyList<ProcessingRetentionHold>> ReadRetentionHoldsAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH ranked AS (
                SELECT artifact_id, payload_relative_path, sidecar_relative_path, capture_id,
                       ROW_NUMBER() OVER (PARTITION BY agent_id, node_id ORDER BY capture_sequence DESC, output_identity_sha256 DESC) AS rank
                FROM processing_outputs
            )
            SELECT DISTINCT artifact_id, payload_relative_path, sidecar_relative_path
            FROM ranked
            WHERE rank <= 100 OR EXISTS (
                SELECT 1
                FROM raw_captures raw
                JOIN capture_lane_work work ON work.raw_capture_row_id = raw.raw_capture_row_id
                WHERE raw.capture_id = ranked.capture_id
                  AND work.lane_name = 'standard'
                  AND work.state NOT IN ('completed', 'abandoned'))
            UNION
            SELECT raw_artifact_id, payload_relative_path, sidecar_relative_path
            FROM (
                SELECT raw_artifact_id, payload_relative_path, sidecar_relative_path,
                       ROW_NUMBER() OVER (PARTITION BY agent_id ORDER BY capture_sequence DESC) AS rank
                FROM raw_captures
                WHERE state = 'committed'
            )
            WHERE rank <= 100;
            """;
        var holds = new List<ProcessingRetentionHold>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            holds.Add(new ProcessingRetentionHold(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1),
                reader.GetString(2)));
        }
        return holds;
    }

    internal async ValueTask<IReadOnlyList<DurableRawProcessingInput>> ReadRecentRawInputsAsync(
        string agentId,
        long currentCaptureSequence,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_capture_row_id, manifest_json, manifest_sha256, payload_relative_path
            FROM raw_captures
            WHERE state = 'committed' AND agent_id = $agent_id
              AND capture_sequence <= $current_capture_sequence
            ORDER BY capture_sequence DESC
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$current_capture_sequence", currentCaptureSequence);
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var candidates = new List<(long RawRowId, byte[] ManifestJson, string ManifestSha256, string PayloadPath)>(maximumCount);
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((
                    reader.GetInt64(0),
                    await reader.GetFieldValueAsync<byte[]>(1, cancellationToken).ConfigureAwait(false),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        var entries = new List<DurableRawProcessingInput>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!string.Equals(
                    CaptureContractJson.ComputeManifestSha256(candidate.ManifestJson),
                    candidate.ManifestSha256,
                    StringComparison.Ordinal))
            {
                await QuarantineRawInputAsync(connection, candidate.RawRowId, cancellationToken).ConfigureAwait(false);
                throw new InvalidDataException("Durable raw rolling input manifest differs from its committed bytes.");
            }
            var parsed = CaptureContractJson.ParseManifest(candidate.ManifestJson);
            if (!parsed.IsValid || parsed.Document?.Manifest?.Descriptor is not { } descriptor)
            {
                await QuarantineRawInputAsync(connection, candidate.RawRowId, cancellationToken).ConfigureAwait(false);
                throw new InvalidDataException("Durable raw rolling input manifest is invalid.");
            }
            entries.Add(new DurableRawProcessingInput(descriptor, candidate.PayloadPath));
        }
        return entries;
    }

    private async Task QuarantineRawInputAsync(
        SqliteConnection connection,
        long rawRowId,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE capture_lane_work
                SET state = 'quarantined', failure_reason = 'evidence-invalid',
                    lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                    updated_unix_ms = $now
                WHERE raw_capture_row_id = $raw AND lane_name = 'standard';
                """;
            command.Parameters.AddWithValue("$raw", rawRowId);
            command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE raw_captures SET retention_hold = 1 WHERE raw_capture_row_id = $raw;";
            command.Parameters.AddWithValue("$raw", rawRowId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<CaptureProcessingOperationalState> ReadOperationalStateAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN work.state IN ('pending', 'leased', 'retry_wait') THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state = 'retry_wait' THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state = 'quarantined' THEN 1 ELSE 0 END),
                MIN(CASE WHEN work.state IN ('pending', 'leased', 'retry_wait') THEN raw.exposure_started_unix_ms END)
            FROM capture_lane_work AS work
            JOIN raw_captures AS raw ON raw.raw_capture_row_id = work.raw_capture_row_id
            WHERE work.lane_name = 'standard';
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var pending = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetInt64(0);
        var retry = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetInt64(1);
        var terminal = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetInt64(2);
        var oldest = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
        return new CaptureProcessingOperationalState(pending, retry, terminal, oldest);
    }

    private async ValueTask InsertOutputAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        string nodeId,
        DurableProcessingOutput output,
        CancellationToken cancellationToken)
    {
        var descriptorJson = output.EvidenceJson;
        var algorithmsJson = JsonSerializer.SerializeToUtf8Bytes(output.Algorithms, SerializerOptions);
        var compatibilityJson = JsonSerializer.SerializeToUtf8Bytes(output.Compatibility, SerializerOptions);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_outputs(
                output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                legacy_recipe_version, committed_unix_ms)
            VALUES (
                $output_identity_sha256, $capture_id, $agent_id, $node_id, $artifact_id, $role, $variant,
                $payload_relative_path, $sidecar_relative_path, $descriptor_json, $recipe_identity_sha256,
                $algorithms_json, $compatibility_json, $total_integration_ticks, $capture_sequence,
                $legacy_recipe_version, $committed_unix_ms)
            ON CONFLICT(output_identity_sha256) DO NOTHING;
            """;
        AddOutputParameters(
            command,
            captureId,
            nodeId,
            output,
            descriptorJson,
            algorithmsJson,
            compatibilityJson,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = """
            SELECT capture_id, node_id, artifact_id, payload_relative_path, sidecar_relative_path,
                   descriptor_json, recipe_identity_sha256, algorithms_json, compatibility_json,
                   total_integration_ticks, capture_sequence, legacy_recipe_version
            FROM processing_outputs WHERE output_identity_sha256 = $output_identity_sha256;
            """;
        verify.Parameters.AddWithValue("$output_identity_sha256", output.OutputIdentitySha256);
        using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("A processing output identity was not committed.");
        }
        var existingDescriptorJson = await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false);
        var existingAlgorithmsJson = await reader.GetFieldValueAsync<byte[]>(7, cancellationToken).ConfigureAwait(false);
        var existingCompatibilityJson = await reader.GetFieldValueAsync<byte[]>(8, cancellationToken).ConfigureAwait(false);
        var existingLegacyRecipeVersion = await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(11);
        if (!string.Equals(reader.GetString(0), captureId.ToString("N"), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), nodeId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), output.ArtifactId.ToString("N"), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(3), output.PayloadRelativePath, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(4), output.SidecarRelativePath, StringComparison.Ordinal) ||
            !existingDescriptorJson.AsSpan().SequenceEqual(descriptorJson) ||
            !string.Equals(reader.GetString(6), output.RecipeIdentitySha256, StringComparison.Ordinal) ||
            !existingAlgorithmsJson.AsSpan().SequenceEqual(algorithmsJson) ||
            !existingCompatibilityJson.AsSpan().SequenceEqual(compatibilityJson) ||
            reader.GetInt64(9) != output.TotalIntegration.Ticks ||
            reader.GetInt64(10) != output.CaptureSequence ||
            !string.Equals(existingLegacyRecipeVersion, output.LegacyRecipeVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A processing output identity conflicts with committed immutable facts.");
        }
    }

    private static void AddOutputParameters(
        SqliteCommand command,
        Guid captureId,
        string nodeId,
        DurableProcessingOutput output,
        byte[] descriptorJson,
        byte[] algorithmsJson,
        byte[] compatibilityJson,
        long committedUnixMilliseconds)
    {
        command.Parameters.AddWithValue("$output_identity_sha256", output.OutputIdentitySha256);
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$agent_id", output.Capture.AgentId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$artifact_id", output.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$role", output.Artifact.Role.ToString());
        command.Parameters.AddWithValue("$variant", output.Artifact.Variant);
        command.Parameters.AddWithValue("$payload_relative_path", output.PayloadRelativePath);
        command.Parameters.AddWithValue("$sidecar_relative_path", output.SidecarRelativePath);
        command.Parameters.AddWithValue("$descriptor_json", descriptorJson);
        command.Parameters.AddWithValue("$recipe_identity_sha256", output.RecipeIdentitySha256);
        command.Parameters.AddWithValue("$algorithms_json", algorithmsJson);
        command.Parameters.AddWithValue("$compatibility_json", compatibilityJson);
        command.Parameters.AddWithValue("$total_integration_ticks", output.TotalIntegration.Ticks);
        command.Parameters.AddWithValue("$capture_sequence", output.CaptureSequence);
        command.Parameters.AddWithValue("$legacy_recipe_version", (object?)output.LegacyRecipeVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$committed_unix_ms", committedUnixMilliseconds);
    }

    private static async ValueTask<IReadOnlyList<DurableProcessingOutput>> ReadOutputsAsync(
        SqliteConnection connection,
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
                   descriptor_json, capture_id, agent_id, node_id, role, variant, recipe_identity_sha256,
                   algorithms_json, compatibility_json, total_integration_ticks, capture_sequence, legacy_recipe_version
            FROM processing_outputs
            WHERE capture_id = $capture_id AND node_id = $node_id
            ORDER BY output_identity_sha256;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        return (await ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false))
            .Select(static row => row.Output)
            .ToArray();
    }

    private static async ValueTask<List<(Guid CaptureId, string NodeId, DurableProcessingOutput Output)>> ReadOutputRowsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken,
        bool skipInvalid = false)
    {
        var outputs = new List<(Guid CaptureId, string NodeId, DurableProcessingOutput Output)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                outputs.Add(await ReadOutputRowAsync(reader, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (skipInvalid && exception is InvalidDataException or FormatException or ArgumentException)
            {
            }
        }
        return outputs;
    }

    private static async ValueTask<(Guid CaptureId, string NodeId, DurableProcessingOutput Output)> ReadOutputRowAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var descriptorJson = await reader.GetFieldValueAsync<byte[]>(4, cancellationToken).ConfigureAwait(false);
        var algorithmsJson = await reader.GetFieldValueAsync<byte[]>(11, cancellationToken).ConfigureAwait(false);
        var compatibilityJson = await reader.GetFieldValueAsync<byte[]>(12, cancellationToken).ConfigureAwait(false);
        ReconstructionDescriptor? descriptor = null;
        IDurableProcessingProductManifest? productManifest = null;
        try
        {
            using var evidence = JsonDocument.Parse(descriptorJson);
            if (evidence.RootElement.ValueKind != JsonValueKind.Object ||
                !evidence.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Committed processing output descriptor is invalid.");
            }
            if (schema.GetString() is DurableProcessingProductManifestV1.CurrentSchemaVersion or
                DurableEncodedProductManifestV2.CurrentSchemaVersion or
                DurableTypedMetadataProductManifestV3.CurrentSchemaVersion)
            {
                productManifest = DurableProcessingProductManifestJson.Parse(descriptorJson);
            }
            else
            {
                var parsed = CaptureContractJson.ParseManifest(descriptorJson);
                descriptor = parsed.Document?.Manifest?.Descriptor;
                if (!parsed.IsValid || descriptor is null)
                {
                    throw new InvalidDataException("Committed processing output descriptor is invalid.");
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Committed processing output descriptor is invalid.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("Committed processing output descriptor is invalid.", exception);
        }
        var algorithms = JsonSerializer.Deserialize<ProcessingAlgorithmIdentity[]>(algorithmsJson, SerializerOptions) ?? [];
        var compatibility = JsonSerializer.Deserialize<ProcessingCompatibilityIdentity>(compatibilityJson, SerializerOptions)
            ?? throw new InvalidDataException("Committed processing compatibility is invalid.");
        var artifact = descriptor?.Artifact ?? productManifest!.Artifact;
        var capture = descriptor?.Capture ?? productManifest!.Capture;
        var outputIdentity = reader.GetString(0);
        var artifactId = Guid.ParseExact(reader.GetString(1), "N");
        var payloadRelativePath = reader.GetString(2);
        var recipeIdentity = reader.GetString(10);
        var totalIntegration = TimeSpan.FromTicks(reader.GetInt64(13));
        var captureSequence = reader.GetInt64(14);
        var sourceMatchesManifest = productManifest is null ||
            (productManifest.ProducerStepId is { } producerStepId
                ? string.Equals(reader.GetString(7), producerStepId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(reader.GetString(7), productManifest.Artifact.SourceId, StringComparison.Ordinal));
        if (!string.Equals(reader.GetString(5), capture.CaptureId.ToString("N"), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(6), capture.AgentId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reader.GetString(7)) ||
            !sourceMatchesManifest ||
            !string.Equals(reader.GetString(8), artifact.Role.ToString(), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(9), artifact.Variant, StringComparison.Ordinal) ||
            artifactId != artifact.ArtifactId ||
            !string.Equals(payloadRelativePath, productManifest?.RelativeArtifactPath ?? payloadRelativePath, StringComparison.Ordinal) ||
            !string.Equals(outputIdentity, productManifest?.OutputIdentitySha256 ?? outputIdentity, StringComparison.Ordinal) ||
            !string.Equals(recipeIdentity, ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe).IdentitySha256, StringComparison.Ordinal) ||
            !algorithms.SequenceEqual(productManifest?.Algorithms ?? algorithms) ||
            compatibility != (productManifest?.Compatibility ?? compatibility) ||
            totalIntegration.Ticks != (productManifest?.TotalIntegrationTicks ?? totalIntegration.Ticks) ||
            captureSequence != capture.CaptureSequence)
        {
            throw new InvalidDataException("Committed processing output columns conflict with its descriptor.");
        }
        return (
            capture.CaptureId,
            reader.GetString(7),
            new DurableProcessingOutput(
                outputIdentity,
                artifactId,
                payloadRelativePath,
                reader.GetString(3),
                descriptorJson,
                descriptor,
                productManifest,
                recipeIdentity,
                algorithms,
                compatibility,
                totalIntegration,
                captureSequence,
                await reader.IsDBNullAsync(15, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(15)));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The interpolated value is a validated integer host option used only for SQLite PRAGMA configuration.")]
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout={checked(_busyTimeoutSeconds * 1000)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void EnsureDatabaseFilesArePhysical()
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, _databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, string.Concat(_databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, string.Concat(_databasePath, "-shm"));
    }

    public void Dispose()
    {
        _initializeGate.Dispose();
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS capture_processing_schema(
            schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
            version INTEGER NOT NULL CHECK(version = 3)
        ) STRICT;
        INSERT INTO capture_processing_schema(schema_key, version) VALUES (1, 3)
            ON CONFLICT(schema_key) DO NOTHING;
        CREATE TABLE IF NOT EXISTS processing_nodes(
            capture_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            required INTEGER NOT NULL CHECK(required IN (0, 1)),
            dependencies_json TEXT NOT NULL,
            recipe_name TEXT NULL,
            output_role TEXT NULL,
            output_variant TEXT NULL,
            plan_sha256 TEXT NOT NULL CHECK(length(plan_sha256) = 64),
            status TEXT NOT NULL CHECK(status IN ('Completed', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
            reason TEXT NULL,
            attempt INTEGER NOT NULL,
            input_evidence_version INTEGER NULL CHECK(input_evidence_version IS NULL OR input_evidence_version = 1),
            processing_profile_identity_sha256 TEXT NULL CHECK(processing_profile_identity_sha256 IS NULL OR length(processing_profile_identity_sha256) = 64),
            started_unix_ms INTEGER NULL,
            completed_unix_ms INTEGER NOT NULL,
            duration_ticks INTEGER NULL CHECK(duration_ticks IS NULL OR duration_ticks >= 0),
            outcome TEXT NULL CHECK(outcome IS NULL OR outcome IN ('Produced', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
            PRIMARY KEY(capture_id, node_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS processing_node_inputs(
            capture_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            input_ordinal INTEGER NOT NULL CHECK(input_ordinal >= 0 AND input_ordinal < 128),
            kind TEXT NOT NULL CHECK(kind IN ('Artifact', 'AuxiliaryArtifact', 'CanonicalContext')),
            name TEXT NULL CHECK(name IS NULL OR length(name) BETWEEN 1 AND 64),
            artifact_id TEXT NULL CHECK(artifact_id IS NULL OR length(artifact_id) = 32),
            role TEXT NULL,
            variant TEXT NULL CHECK(variant IS NULL OR length(variant) BETWEEN 1 AND 128),
            recipe_identity_sha256 TEXT NULL CHECK(recipe_identity_sha256 IS NULL OR length(recipe_identity_sha256) = 64),
            schema_version TEXT NULL CHECK(schema_version IS NULL OR length(schema_version) BETWEEN 1 AND 128),
            identity_sha256 TEXT NULL CHECK(identity_sha256 IS NULL OR length(identity_sha256) = 64),
            selected_flag INTEGER NOT NULL CHECK(selected_flag IN (0, 1)),
            PRIMARY KEY(capture_id, node_id, input_ordinal),
            FOREIGN KEY(capture_id, node_id) REFERENCES processing_nodes(capture_id, node_id)
                ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
        ) STRICT;
        CREATE TABLE IF NOT EXISTS processing_outputs(
            output_identity_sha256 TEXT PRIMARY KEY CHECK(length(output_identity_sha256) = 64),
            capture_id TEXT NOT NULL,
            agent_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            artifact_id TEXT NOT NULL UNIQUE CHECK(length(artifact_id) = 32),
            role TEXT NOT NULL,
            variant TEXT NOT NULL,
            payload_relative_path TEXT NOT NULL,
            sidecar_relative_path TEXT NOT NULL,
            descriptor_json BLOB NOT NULL,
            recipe_identity_sha256 TEXT NOT NULL CHECK(length(recipe_identity_sha256) = 64),
            algorithms_json BLOB NOT NULL,
            compatibility_json BLOB NOT NULL,
            total_integration_ticks INTEGER NOT NULL,
            capture_sequence INTEGER NOT NULL CHECK(capture_sequence > 0),
            legacy_recipe_version TEXT NULL,
            committed_unix_ms INTEGER NOT NULL,
            FOREIGN KEY(capture_id, node_id) REFERENCES processing_nodes(capture_id, node_id)
                DEFERRABLE INITIALLY DEFERRED
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_processing_outputs_capture_node
            ON processing_outputs(capture_id, node_id);
        CREATE INDEX IF NOT EXISTS ix_processing_outputs_window
            ON processing_outputs(agent_id, node_id, role, capture_sequence);
        CREATE INDEX IF NOT EXISTS ix_processing_nodes_status
            ON processing_nodes(status, capture_id);
        CREATE INDEX IF NOT EXISTS ix_processing_nodes_recipe
            ON processing_nodes(upper(recipe_name), capture_id);
        CREATE INDEX IF NOT EXISTS ix_processing_outputs_role
            ON processing_outputs(role, capture_id);
        CREATE INDEX IF NOT EXISTS ix_processing_outputs_recipe
            ON processing_outputs(recipe_identity_sha256, capture_id);
        CREATE INDEX IF NOT EXISTS ix_processing_node_inputs_artifact
            ON processing_node_inputs(artifact_id, capture_id, node_id);
        """;

    private const string ProcessingV2MigrationSql = """
        CREATE INDEX IF NOT EXISTS ix_processing_nodes_status
            ON processing_nodes(status, capture_id);
        CREATE INDEX IF NOT EXISTS ix_processing_nodes_recipe
            ON processing_nodes(upper(recipe_name), capture_id);
        CREATE INDEX IF NOT EXISTS ix_processing_outputs_role
            ON processing_outputs(role, capture_id);
        CREATE INDEX IF NOT EXISTS ix_processing_outputs_recipe
            ON processing_outputs(recipe_identity_sha256, capture_id);
        DROP TABLE capture_processing_schema;
        CREATE TABLE capture_processing_schema(
            schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
            version INTEGER NOT NULL CHECK(version = 2)
        ) STRICT;
        INSERT INTO capture_processing_schema(schema_key, version) VALUES (1, 2);
        """;

    private const string ProcessingV3MigrationSql = """
        ALTER TABLE processing_nodes ADD COLUMN input_evidence_version INTEGER NULL
            CHECK(input_evidence_version IS NULL OR input_evidence_version = 1);
        ALTER TABLE processing_nodes ADD COLUMN processing_profile_identity_sha256 TEXT NULL
            CHECK(processing_profile_identity_sha256 IS NULL OR length(processing_profile_identity_sha256) = 64);
        ALTER TABLE processing_nodes ADD COLUMN started_unix_ms INTEGER NULL;
        ALTER TABLE processing_nodes ADD COLUMN duration_ticks INTEGER NULL
            CHECK(duration_ticks IS NULL OR duration_ticks >= 0);
        ALTER TABLE processing_nodes ADD COLUMN outcome TEXT NULL
            CHECK(outcome IS NULL OR outcome IN ('Produced', 'Skipped', 'RetryableFailure', 'TerminalFailure'));
        CREATE TABLE processing_node_inputs(
            capture_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            input_ordinal INTEGER NOT NULL CHECK(input_ordinal >= 0 AND input_ordinal < 128),
            kind TEXT NOT NULL CHECK(kind IN ('Artifact', 'AuxiliaryArtifact', 'CanonicalContext')),
            name TEXT NULL CHECK(name IS NULL OR length(name) BETWEEN 1 AND 64),
            artifact_id TEXT NULL CHECK(artifact_id IS NULL OR length(artifact_id) = 32),
            role TEXT NULL,
            variant TEXT NULL CHECK(variant IS NULL OR length(variant) BETWEEN 1 AND 128),
            recipe_identity_sha256 TEXT NULL CHECK(recipe_identity_sha256 IS NULL OR length(recipe_identity_sha256) = 64),
            schema_version TEXT NULL CHECK(schema_version IS NULL OR length(schema_version) BETWEEN 1 AND 128),
            identity_sha256 TEXT NULL CHECK(identity_sha256 IS NULL OR length(identity_sha256) = 64),
            selected_flag INTEGER NOT NULL CHECK(selected_flag IN (0, 1)),
            PRIMARY KEY(capture_id, node_id, input_ordinal),
            FOREIGN KEY(capture_id, node_id) REFERENCES processing_nodes(capture_id, node_id)
                ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
        ) STRICT;
        CREATE INDEX ix_processing_node_inputs_artifact
            ON processing_node_inputs(artifact_id, capture_id, node_id);
        DROP TABLE capture_processing_schema;
        CREATE TABLE capture_processing_schema(
            schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
            version INTEGER NOT NULL CHECK(version = 3)
        ) STRICT;
        INSERT INTO capture_processing_schema(schema_key, version) VALUES (1, 3);
        """;
}
