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
    ReconstructionDescriptor Descriptor,
    string RecipeIdentitySha256,
    IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    ProcessingCompatibilityIdentity Compatibility,
    TimeSpan TotalIntegration,
    long CaptureSequence,
    string? LegacyRecipeVersion);

internal sealed record DurableProcessingNode(
    Guid CaptureId,
    string NodeId,
    bool Required,
    DurableProcessingNodeStatus Status,
    string? Reason,
    int Attempt,
    string PlanSha256,
    IReadOnlyList<DurableProcessingOutput> Outputs);

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
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly string _databasePath;
    private readonly int _busyTimeoutSeconds;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public SqliteCaptureProcessingStore(IOptions<CameraAgentHostOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var values = options.Value;
        _root = Path.GetFullPath(values.RawIngressRoot);
        _databasePath = Path.Combine(_root, "journal", "raw-ingress.db");
        _busyTimeoutSeconds = values.RawIngressSqliteBusyTimeoutSeconds;
    }

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
                if (version > 1)
                {
                    throw new InvalidOperationException($"Capture processing schema {version} is newer than supported schema 1.");
                }
            }
            using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                    'ix_processing_outputs_capture_node', 'ix_processing_outputs_window');
                """;
            if (Convert.ToInt32(
                await schemaObjects.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 5)
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
            SELECT required, status, reason, attempt, plan_sha256
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
        await reader.DisposeAsync().ConfigureAwait(false);
        var outputs = await ReadOutputsAsync(connection, captureId, nodeId, cancellationToken).ConfigureAwait(false);
        return new DurableProcessingNode(captureId, nodeId, required, status, reason, attempt, planSha256, outputs);
    }

    internal async ValueTask WriteNodeAsync(
        Guid captureId,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        int attempt,
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
                plan_sha256, status, reason, attempt, completed_unix_ms)
            VALUES (
                $capture_id, $node_id, $required, $dependencies_json, $recipe_name, $output_role, $output_variant,
                $plan_sha256, $status, $reason, $attempt, $completed_unix_ms)
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
                completed_unix_ms = excluded.completed_unix_ms;
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
        command.Parameters.AddWithValue("$completed_unix_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureLeaseAsync(
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
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
                   output.descriptor_json, output.recipe_identity_sha256, output.algorithms_json, output.compatibility_json,
                   output.total_integration_ticks, output.capture_sequence, output.legacy_recipe_version
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
        var values = await ReadOutputsAsync(command, cancellationToken).ConfigureAwait(false);
        values.Reverse();
        return values;
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
            SELECT manifest_json, payload_relative_path
            FROM raw_captures
            WHERE state = 'committed' AND agent_id = $agent_id
              AND capture_sequence <= $current_capture_sequence
            ORDER BY capture_sequence DESC
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$current_capture_sequence", currentCaptureSequence);
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var entries = new List<DurableRawProcessingInput>(maximumCount);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var manifestJson = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(manifestJson);
            if (!parsed.IsValid || parsed.Document?.Manifest?.Descriptor is not { } descriptor)
            {
                throw new InvalidDataException("Durable raw rolling input manifest is invalid.");
            }
            entries.Add(new DurableRawProcessingInput(descriptor, reader.GetString(1)));
        }
        return entries;
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

    private static async ValueTask InsertOutputAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        string nodeId,
        DurableProcessingOutput output,
        CancellationToken cancellationToken)
    {
        var descriptorJson = CaptureContractJson.Serialize(new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion,
            output.Descriptor,
            output.PayloadRelativePath));
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
        AddOutputParameters(command, captureId, nodeId, output, descriptorJson, algorithmsJson, compatibilityJson);
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
        byte[] compatibilityJson)
    {
        command.Parameters.AddWithValue("$output_identity_sha256", output.OutputIdentitySha256);
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$agent_id", output.Descriptor.Capture.AgentId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$artifact_id", output.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$role", output.Descriptor.Artifact.Role.ToString());
        command.Parameters.AddWithValue("$variant", output.Descriptor.Artifact.Variant);
        command.Parameters.AddWithValue("$payload_relative_path", output.PayloadRelativePath);
        command.Parameters.AddWithValue("$sidecar_relative_path", output.SidecarRelativePath);
        command.Parameters.AddWithValue("$descriptor_json", descriptorJson);
        command.Parameters.AddWithValue("$recipe_identity_sha256", output.RecipeIdentitySha256);
        command.Parameters.AddWithValue("$algorithms_json", algorithmsJson);
        command.Parameters.AddWithValue("$compatibility_json", compatibilityJson);
        command.Parameters.AddWithValue("$total_integration_ticks", output.TotalIntegration.Ticks);
        command.Parameters.AddWithValue("$capture_sequence", output.CaptureSequence);
        command.Parameters.AddWithValue("$legacy_recipe_version", (object?)output.LegacyRecipeVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$committed_unix_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
                   descriptor_json, recipe_identity_sha256, algorithms_json, compatibility_json,
                   total_integration_ticks, capture_sequence, legacy_recipe_version
            FROM processing_outputs
            WHERE capture_id = $capture_id AND node_id = $node_id
            ORDER BY output_identity_sha256;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        return await ReadOutputsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<List<DurableProcessingOutput>> ReadOutputsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var outputs = new List<DurableProcessingOutput>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var descriptorJson = await reader.GetFieldValueAsync<byte[]>(4, cancellationToken).ConfigureAwait(false);
            var algorithmsJson = await reader.GetFieldValueAsync<byte[]>(6, cancellationToken).ConfigureAwait(false);
            var compatibilityJson = await reader.GetFieldValueAsync<byte[]>(7, cancellationToken).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(descriptorJson);
            var descriptor = parsed.Document?.Manifest?.Descriptor;
            if (!parsed.IsValid || descriptor is null)
            {
                throw new InvalidDataException("Committed processing output descriptor is invalid.");
            }
            var algorithms = JsonSerializer.Deserialize<ProcessingAlgorithmIdentity[]>(
                algorithmsJson, SerializerOptions) ?? [];
            var compatibility = JsonSerializer.Deserialize<ProcessingCompatibilityIdentity>(
                compatibilityJson, SerializerOptions)
                ?? throw new InvalidDataException("Committed processing compatibility is invalid.");
            outputs.Add(new DurableProcessingOutput(
                reader.GetString(0),
                Guid.ParseExact(reader.GetString(1), "N"),
                reader.GetString(2),
                reader.GetString(3),
                descriptor,
                reader.GetString(5),
                algorithms,
                compatibility,
                TimeSpan.FromTicks(reader.GetInt64(8)),
                reader.GetInt64(9),
                await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(10)));
        }
        return outputs;
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
            version INTEGER NOT NULL CHECK(version = 1)
        ) STRICT;
        INSERT INTO capture_processing_schema(schema_key, version) VALUES (1, 1)
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
            completed_unix_ms INTEGER NOT NULL,
            PRIMARY KEY(capture_id, node_id)
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
        """;
}
