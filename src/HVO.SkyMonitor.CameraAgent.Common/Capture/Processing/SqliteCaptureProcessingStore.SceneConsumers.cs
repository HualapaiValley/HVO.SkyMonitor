using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    internal sealed record SatelliteConsumer(Guid ArtifactId, string PayloadRelativePath, string SidecarRelativePath,
        bool Satellite, bool Unavailable, bool ProjectedScene);

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
                availability_state <> 'Available', product_schema_version, json_extract(descriptor_json, '$.artifact.recipe.name')
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
            var schema = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5);
            var independentPrediction = !await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) &&
                reader.GetString(6) == BuiltInProcessingRecipes.AstrometryPrediction;
            rows.Add(new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4),
                !independentPrediction && SceneProvenance.IsRetainedProjectedSceneSchemaVersion(schema)));
        }
        return rows;
    }
    internal async ValueTask<IReadOnlyList<Guid>?> ReadSceneConsumersAsync(
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
        var artifactIds = new List<Guid> { raw.Descriptor.Artifact.ArtifactId };
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT artifact_id FROM processing_outputs
                WHERE capture_id = $capture AND artifact_id <> $scene
                LIMIT 4097;
                """;
            command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
            command.Parameters.AddWithValue("$scene", sceneArtifactId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) artifactIds.Add(Guid.ParseExact(reader.GetString(0), "N"));
            if (artifactIds.Count > 4097) throw new InvalidDataException("Scene consumer inventory exceeds its safety bound.");
        }
        return artifactIds;
    }
}
