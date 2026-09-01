using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class ProcessingRawWindowSelector
{
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The selected query is one of two fixed internal statements and all values remain parameterized.")]
    internal static async ValueTask<IReadOnlyList<ProcessingFrozenRawInput>> SelectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ReconstructionDescriptor current,
        ProcessingGraphWindowRequirement requirement,
        int maximumAllowedInputs,
        CancellationToken cancellationToken)
    {
        var maximumCount = Math.Min(requirement.MaximumInputCount, maximumAllowedInputs);
        if (maximumCount < 1 || requirement.MinimumInputCount < 0 || requirement.MinimumInputCount > maximumCount)
            throw new InvalidDataException("The processing graph window bounds are invalid.");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = requirement.Kind == ProcessingGraphWindowKind.Trailing
            ? """
              SELECT raw_capture_row_id, capture_sequence, manifest_json, manifest_sha256,
                     descriptor_sha256, payload_sha256, payload_relative_path
              FROM raw_captures
              WHERE state = 'committed' AND agent_id = $agent AND capture_sequence <= $sequence
              ORDER BY capture_sequence DESC
              LIMIT $candidate_count;
              """
            : """
              SELECT raw_capture_row_id, capture_sequence, manifest_json, manifest_sha256,
                     descriptor_sha256, payload_sha256, payload_relative_path
              FROM raw_captures
              WHERE state = 'committed' AND agent_id = $agent
              ORDER BY ABS(capture_sequence - $sequence), capture_sequence
              LIMIT $candidate_count;
              """;
        command.Parameters.AddWithValue("$agent", current.Capture.AgentId);
        command.Parameters.AddWithValue("$sequence", current.Capture.CaptureSequence);
        command.Parameters.AddWithValue("$candidate_count", Math.Min(512, maximumAllowedInputs * 4));
        var candidates = new List<(long RawRowId, long Sequence, byte[] ManifestJson, string ManifestSha256,
            string DescriptorSha256, string PayloadSha256, string PayloadPath)>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    await reader.GetFieldValueAsync<byte[]>(2, cancellationToken).ConfigureAwait(false),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
        }

        var compatible = new List<(long RawRowId, long Sequence, ReconstructionDescriptor Descriptor,
            string DescriptorSha256, string PayloadSha256, string PayloadPath)>();
        foreach (var candidate in candidates)
        {
            var parsed = CaptureContractJson.ParseManifest(candidate.ManifestJson);
            if (!parsed.IsValid || parsed.Document?.Manifest.Descriptor is not { } descriptor ||
                !string.Equals(CaptureContractJson.ComputeManifestSha256(candidate.ManifestJson), candidate.ManifestSha256, StringComparison.Ordinal) ||
                !string.Equals(CaptureContractJson.ComputeDescriptorSha256(descriptor), candidate.DescriptorSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A raw processing window input has invalid durable evidence.");
            }
            if (!IsCompatible(current, descriptor))
            {
                if (requirement.Kind == ProcessingGraphWindowKind.Trailing) break;
                continue;
            }
            compatible.Add((candidate.RawRowId, candidate.Sequence, descriptor, candidate.DescriptorSha256,
                candidate.PayloadSha256, candidate.PayloadPath));
            if (requirement.Kind == ProcessingGraphWindowKind.Trailing && compatible.Count == maximumCount) break;
        }

        if (requirement.Kind == ProcessingGraphWindowKind.Centered)
        {
            compatible = compatible.Take(maximumCount).OrderBy(static value => value.Sequence).ToList();
        }
        else
        {
            compatible.Reverse();
        }
        if (compatible.Count < requirement.MinimumInputCount)
            throw new ProcessingGraphStoreConflictException("The archived processing window is incomplete.");

        var currentIndex = compatible.FindIndex(value => value.Descriptor.Capture.CaptureId == current.Capture.CaptureId);
        if (currentIndex < 0) throw new ProcessingGraphStoreConflictException("The processing window does not contain its primary capture.");
        var selected = compatible.Select((value, index) => new ProcessingFrozenRawInput(
            value.RawRowId,
            requirement.Kind == ProcessingGraphWindowKind.Trailing ? index - compatible.Count + 1 : index - currentIndex,
            value.Descriptor,
            value.DescriptorSha256,
            value.PayloadSha256,
            value.PayloadPath)).ToArray();
        if (!requirement.RequiredPositions.IsDefaultOrEmpty &&
            requirement.RequiredPositions.Any(position => selected.All(input => input.WindowPosition != position)))
        {
            throw new ProcessingGraphStoreConflictException("The archived processing window is missing a required position.");
        }
        return selected;
    }

    private static bool IsCompatible(ReconstructionDescriptor current, ReconstructionDescriptor candidate)
        => current.Layout == candidate.Layout &&
           current.Profiles.Rig == candidate.Profiles.Rig &&
           current.Profiles.Calibration == candidate.Profiles.Calibration &&
           current.Profiles.Mask == candidate.Profiles.Mask &&
           current.Profiles.Sensor == candidate.Profiles.Sensor &&
           current.Profiles.Processing == candidate.Profiles.Processing &&
           current.Location == candidate.Location &&
           current.Controls.EffectiveExposure == candidate.Controls.EffectiveExposure &&
           current.Controls.EffectiveGain == candidate.Controls.EffectiveGain &&
           current.Controls.EffectiveOffset == candidate.Controls.EffectiveOffset &&
           current.Controls.TemperatureSetpointC == candidate.Controls.TemperatureSetpointC;
}
