using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record SceneConsumerInventory(
    ArtifactManifestV2 Raw,
    IReadOnlyList<string> CanonicalPayloadPaths,
    IReadOnlySet<string> ArchiveRoots,
    bool HasRecordedHistory);

internal sealed partial class SqliteCaptureProcessingStore
{
    internal sealed record SatelliteConsumer(Guid ArtifactId, string PayloadRelativePath, string SidecarRelativePath, bool Satellite, bool Unavailable);

    internal async ValueTask<bool> VerifySatelliteRawAsync(Guid captureId, Guid artifactId, string payload, string sidecar,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM raw_captures WHERE capture_id = $capture AND raw_artifact_id = $artifact
                AND payload_relative_path = $payload AND sidecar_relative_path = $sidecar;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$sidecar", sidecar);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    internal async ValueTask<IReadOnlyList<SatelliteConsumer>> ReadSatelliteConsumersAsync(
        Guid captureId, string satelliteNodeId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, payload_relative_path, sidecar_relative_path,
                COALESCE(node_id = $node OR product_schema_version = $source OR
                    json_extract(descriptor_json, '$.artifact.recipe.implementationVersion') = $producer, 0),
                availability_state <> 'Available'
            FROM processing_outputs WHERE capture_id = $capture LIMIT 4097;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node", satelliteNodeId);
        command.Parameters.AddWithValue("$source", HVO.SkyMonitor.Processing.CaptureSatelliteProducts.SourceSchema);
        command.Parameters.AddWithValue("$producer", HVO.SkyMonitor.Processing.OrbitalTrackPresentation.ProducerVersion);
        var rows = new List<SatelliteConsumer>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == 4096) throw new InvalidDataException("Satellite capture consumer discovery exceeds its bound.");
            rows.Add(new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        }
        return rows;
    }
    // Step options retain their original JSON; match the factory's accepted string/numeric enums.
    private static readonly JsonSerializerOptions SceneStorageOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    internal async ValueTask<SceneConsumerInventory?> ReadSceneConsumersAsync(
        Guid captureId, Guid sceneArtifactId, Action<long> accountBytes, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ArtifactManifestV2 raw;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT manifest_json, length(manifest_json) FROM raw_captures WHERE capture_id = $capture;";
            command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            accountBytes(reader.GetInt64(1));
            var bytes = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(bytes);
            raw = parsed.IsValid && parsed.Document is not null ? parsed.Document.Manifest
                : throw new InvalidDataException("Scene retention cannot authenticate the source raw manifest.");
        }
        var paths = new List<string> { raw.RelativeArtifactPath };
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT payload_relative_path FROM processing_outputs
                WHERE capture_id = $capture AND artifact_id <> $scene
                LIMIT 4097;
                """;
            command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
            command.Parameters.AddWithValue("$scene", sceneArtifactId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) paths.Add(reader.GetString(0));
            if (paths.Count > 4097) throw new InvalidDataException("Scene consumer inventory exceeds its safety bound.");
        }
        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var historyCount = 0;
        var revisions = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT revision.pipeline_json, length(revision.pipeline_json), revision.revision_id
                FROM processing_executions execution
                JOIN processing_graph_revisions revision ON revision.revision_id = execution.graph_revision_id
                WHERE execution.execution_class IN ('Live', 'Replay') AND execution.capture_id = $capture
                LIMIT 4097;
                """;
            command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (++historyCount > 4096) throw new InvalidDataException("Scene graph history exceeds its safety bound.");
                if (!revisions.Add(reader.GetString(2))) continue;
                accountBytes(reader.GetInt64(1));
                var bytes = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
                var pipeline = JsonSerializer.Deserialize<CapturePipelineConfig>(bytes, ExecutionSerializerOptions)
                    ?? throw new InvalidDataException("Scene retention cannot read a recorded processing pipeline.");
                foreach (var step in pipeline.Steps.Where(static step => step.Enabled != false &&
                             RetentionBackgroundService.IsFileStorageStep(step.Type)))
                {
                    var options = step.Options?.Deserialize<FileStorageCaptureProcessingStepOptions>(SceneStorageOptions)
                        ?? new FileStorageCaptureProcessingStepOptions();
                    roots.Add(Path.GetFullPath(options.StorageRoot));
                    if (roots.Count > 4096) throw new InvalidDataException("Scene archive roots exceed their safety bound.");
                }
            }
        }
        return new(raw, paths, roots, historyCount != 0);
    }
}
