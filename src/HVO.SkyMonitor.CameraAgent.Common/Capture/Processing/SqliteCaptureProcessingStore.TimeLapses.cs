using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    internal async ValueTask<IReadOnlyList<CameraAgentTimeLapseRawSource>> ReadTimeLapseRawSourcesAsync(
        string agentId, DateTimeOffset startUtc, DateTimeOffset endUtc, int maximumCount, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (maximumCount is < 1 or > 8192 || endUtc <= startUtc || endUtc - startUtc > TimeSpan.FromHours(3))
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT manifest_json, manifest_sha256, payload_relative_path, sidecar_relative_path
            FROM raw_captures INDEXED BY ix_raw_captures_gallery_time
            WHERE state = 'committed' AND agent_id = $agent_id
              AND exposure_started_unix_ms >= $start AND exposure_started_unix_ms < $end
            ORDER BY exposure_started_unix_ms, capture_sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$start", CeilingUnixMilliseconds(startUtc));
        command.Parameters.AddWithValue("$end", CeilingUnixMilliseconds(endUtc));
        command.Parameters.AddWithValue("$limit", maximumCount + 1);
        var result = new List<CameraAgentTimeLapseRawSource>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var manifest = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
            if (CaptureContractJson.ComputeManifestSha256(manifest) != reader.GetString(1))
                throw new InvalidDataException("A time-lapse source manifest differs from its committed checksum.");
            var parsed = CaptureContractJson.ParseManifest(manifest);
            if (!parsed.IsValid || parsed.Document?.Manifest?.Descriptor is not { } descriptor ||
                descriptor.Capture.AgentId != agentId || parsed.Document.Manifest.RelativeArtifactPath != reader.GetString(2))
                throw new InvalidDataException("A time-lapse source has invalid durable capture facts.");
            result.Add(new(descriptor, reader.GetString(2), reader.GetString(3)));
        }
        return result;
    }
}
