using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private static async ValueTask<(IReadOnlyList<ProcessingGraphExecutionInputState> Inputs,
        IReadOnlyList<ProcessingGraphExecutionCalibrationInputState> Masters, bool Complete)> CompleteRunManifestInputsAsync(
        SqliteConnection connection,
        ProcessingGraphExecutionState execution,
        IReadOnlyList<ProcessingGraphExecutionInputState> retainedInputs,
        IReadOnlyList<ProcessingGraphExecutionCalibrationInputState> masters,
        IReadOnlyList<ProcessingGraphExecutionOutputState> outputs,
        CancellationToken cancellationToken)
    {
        var expected = outputs.SelectMany(static output => output.SourceArtifactIds).ToHashSet();
        // The current dependency artifact is consumed in process, whereas derived pins describe
        // history. Recover its immutable identity from this execution's own output lineage, never
        // from the mutable per-capture node cache that a later replay can replace.
        if (expected.Count > ProcessingRunManifestBounds.MaximumRows)
            throw new InvalidDataException(ProcessingRunManifestBounds.UnavailableReason);
        var inputs = retainedInputs.ToList();
        var references = masters.ToList();
        var retained = inputs.Select(static input => input.ArtifactId).Concat(masters.Select(static master => master.ArtifactId)).ToHashSet();
        var missing = expected.Except(retained).Select(static id => id.ToString("N")).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return (inputs, references, true);
        using (var command = connection.CreateCommand())
        {
            // Ordinary reference calibration has no mixed-gain normalization plan. Its produced
            // output binds immutable master IDs; never consult today's active library selection.
            // ID uniqueness, fixed kinds and 64-character hashes bound this scalar-only projection.
            command.CommandText = """
                SELECT artifact_id, payload_sha256, reference_kind
                FROM calibration_library_artifacts
                WHERE role = 'master' AND artifact_id IN (SELECT value FROM json_each($ids))
                ORDER BY artifact_id LIMIT 4097;
                """;
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(missing));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (inputs.Count + references.Count >= ProcessingRunManifestBounds.MaximumRows)
                    throw new InvalidDataException(ProcessingRunManifestBounds.UnavailableReason);
                var artifactId = Guid.ParseExact(reader.GetString(0), "N");
                references.Add(new(artifactId, reader.GetString(1), reader.GetString(2)));
                retained.Add(artifactId);
            }
        }
        missing = expected.Except(retained).Select(static id => id.ToString("N")).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return (inputs, references, true);
        using (var command = connection.CreateCommand())
        {
            // Only current-capture sources can be reconstructed without inventing a historical
            // window position. Historical pins must survive; a deleted source leaves Complete false.
            command.CommandText = """
                SELECT 'ProcessingOutput', capture_id, artifact_id, NULL,
                       COALESCE(json_extract(CAST(descriptor_json AS TEXT), '$.descriptor.artifact.checksumSha256'),
                                json_extract(CAST(descriptor_json AS TEXT), '$.artifact.checksumSha256')),
                       output_identity_sha256
                FROM processing_outputs
                WHERE capture_id = $capture AND artifact_id IN (SELECT value FROM json_each($ids))
                UNION ALL
                SELECT 'RawCapture', capture_id, raw_artifact_id, descriptor_sha256, payload_sha256, NULL
                FROM raw_captures
                WHERE capture_id = $capture AND raw_artifact_id IN (SELECT value FROM json_each($ids))
                ORDER BY 3 LIMIT 4097;
                """;
            command.Parameters.AddWithValue("$capture", execution.CaptureId.ToString("N"));
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(missing));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var ordinal = inputs.Count == 0 ? 0 : inputs.Max(static input => input.Ordinal) + 1;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var artifactId = Guid.ParseExact(reader.GetString(2), "N");
                if (!retained.Add(artifactId)) continue;
                if (inputs.Count + references.Count >= ProcessingRunManifestBounds.MaximumRows)
                    throw new InvalidDataException(ProcessingRunManifestBounds.UnavailableReason);
                inputs.Add(new(ordinal++, 0,
                    Enum.Parse<ProcessingGraphExecutionInputKind>(reader.GetString(0)),
                    Guid.ParseExact(reader.GetString(1), "N"), artifactId,
                    await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                    await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
                    await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5)));
            }
        }
        return (inputs, references, expected.IsSubsetOf(retained));
    }
}
