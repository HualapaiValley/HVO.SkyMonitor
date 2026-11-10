using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Versioned, allowlisted operator evidence. Never serialize the durable detail directly.</summary>
internal static class CameraAgentRunManifest
{
    internal const string SchemaVersion = "hvo-cameraagent-run-manifest-v1";
    internal const string MissingReason = "The execution's node evidence is no longer retained.";
    internal const string ArtifactEvidenceUnavailableReason = "The execution's artifact input or output evidence is no longer retained.";
    internal const string IdentifierUnavailableReason = "A retained node identifier cannot be included safely in the manifest.";
    internal const string IncompleteReason = "The retained artifact checksum or lineage evidence is incomplete.";
    internal const string UnrecordedOutputReason = "The execution's original produced-output evidence was not recorded completely.";
    internal const string CorruptReason = "The retained run evidence is corrupt and cannot be downloaded.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static (byte[]? Payload, string? Reason) Create(ProcessingGraphExecutionDetail detail)
    {
        if (detail.FrozenNodeCount is { } expected && expected != detail.Nodes.Count) return (null, MissingReason);
        if (detail.Nodes.Count > ProcessingRunManifestBounds.MaximumNodes || detail.Nodes.Any(static node =>
            node.Inputs.Count + node.CalibrationInputs.Count > ProcessingRunManifestBounds.MaximumRows ||
            node.Outputs.Count > ProcessingRunManifestBounds.MaximumRows ||
            node.Attempts.Count > ProcessingRunManifestBounds.MaximumRows) ||
            detail.Nodes.Sum(static node => (long)node.Inputs.Count + node.CalibrationInputs.Count + node.Outputs.Count + node.Attempts.Count) > ProcessingRunManifestBounds.MaximumRows * 4L)
            return (null, ProcessingRunManifestBounds.UnavailableReason);
        if (detail.Nodes.Any(static node => !HasRetainedArtifactBindings(node)))
            return (null, ArtifactEvidenceUnavailableReason);
        if (detail.Nodes.Any(static node => node.CommittedOutputCount is null &&
            (node.Status is "Completed" or "Skipped" or "RetryableFailure" or "TerminalFailure" || node.Attempts.Any(static attempt =>
                attempt.Status is "Completed" or "Skipped" or "RetryableFailure" or "TerminalFailure" ||
                attempt.Outcome == HVO.SkyMonitor.Processing.ProcessingOutcomeStatus.Produced))))
            return (null, UnrecordedOutputReason);
        if (detail.Nodes.Any(static node => node.NodeId.Length is 0 or > 128 ||
            node.NodeId != node.NodeId.Trim() || node.NodeId.Any(static value => char.IsControl(value) || value is '/' or '\\')))
            return (null, IdentifierUnavailableReason);
        if (detail.Nodes.Any(static node => node.Outputs.Any(static output =>
            !IsSha256(output.PayloadSha256) || output.SourceArtifactIds.Count > 512) ||
            node.Inputs.Any(static input => !IsSha256(input.PayloadSha256)) ||
            node.CalibrationInputs.Any(static input => !IsSha256(input.PayloadSha256))))
            return (null, IncompleteReason);
        var execution = detail.Execution;
        if (!IsSha256(execution.GraphRevisionId) || !IsSha256(execution.GraphDefinitionIdentitySha256) ||
            !IsSha256(execution.SharedPlanIdentitySha256) || !IsSha256(execution.LocalPlanIdentitySha256) ||
            detail.Nodes.Any(static node => !IsSha256(node.PlanSha256)))
            return (null, IncompleteReason);
        // Free-text reasons, trigger references, lease owners, configurations and diagnostic objects
        // are deliberately excluded. Status strings originate in checked durable enum columns.
        var manifest = new
        {
            schemaVersion = SchemaVersion,
            execution.ExecutionId,
            execution.ExecutionClass,
            execution.Status,
            execution.CaptureId,
            execution.PrimaryArtifactId,
            execution.GraphRevisionId,
            execution.GraphDefinitionIdentitySha256,
            execution.SharedPlanIdentitySha256,
            execution.LocalPlanIdentitySha256,
            execution.AcceptedUtc,
            execution.StartedUtc,
            execution.CompletedUtc,
            execution.CancellationRequested,
            execution.AttemptCount,
            nodes = detail.Nodes.Select(static node => new
            {
                node.NodeId,
                node.Required,
                node.PlanSha256,
                node.Status,
                node.StartedUtc,
                node.CompletedUtc,
                node.AttemptCount,
                node.NormalizationPlanIdentitySha256,
                inputs = CreateInputs(node),
                attempts = node.Attempts.Select(static attempt => new
                {
                    attempt.AttemptNumber,
                    attempt.StartedUtc,
                    attempt.CompletedUtc,
                    attempt.Status,
                    attempt.Outcome,
                    attempt.Duration,
                    attempt.ExecutionRoute
                }),
                outputs = node.Outputs.Select(static output => new
                {
                    output.Ordinal,
                    output.OutputIdentitySha256,
                    output.ArtifactId,
                    output.Role,
                    output.PayloadSha256,
                    output.SourceArtifactIds,
                    output.Published
                })
            })
        };
        // The bounded store snapshot caps the input. Also cap the encoded result, including JSON escaping.
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        return payload.Length > ProcessingRunManifestBounds.MaximumBytes
            ? (null, ProcessingRunManifestBounds.UnavailableReason) : (payload, null);
    }

    private static IEnumerable<ManifestInput> CreateInputs(ProcessingGraphExecutionNodeState node)
    {
        foreach (var input in node.Inputs)
            yield return new(input.Ordinal,
                input.Kind is ProcessingGraphExecutionInputKind.AuxiliaryRawCapture or ProcessingGraphExecutionInputKind.AuxiliaryProcessingOutput
                    ? null : input.WindowPosition,
                input.Kind.ToString(), input.CaptureId,
                input.ArtifactId, input.DescriptorSha256, input.PayloadSha256, input.OutputIdentitySha256, null);
        var ordinal = node.Inputs.Count == 0 ? 0 : node.Inputs.Max(static input => input.Ordinal) + 1;
        foreach (var master in node.CalibrationInputs)
            yield return new(ordinal++, null, "CalibrationMaster", null, master.ArtifactId, null, master.PayloadSha256, null, master.Kind);
    }

    private sealed record ManifestInput(int Ordinal, int? WindowPosition, string Kind, Guid? CaptureId,
        Guid ArtifactId, string? DescriptorSha256, string? PayloadSha256, string? OutputIdentitySha256, string? CalibrationKind);

    private static bool HasRetainedArtifactBindings(ProcessingGraphExecutionNodeState node)
    {
        if (!node.ArtifactBindingsRetained) return false;
        if (node.CommittedOutputCount is { } committed && node.Outputs.Count != committed) return false;
        var inputs = node.Inputs.Select(static input => input.ArtifactId)
            .Concat(node.CalibrationInputs.Select(static input => input.ArtifactId)).ToHashSet();
        return node.Outputs.All(output => output.SourceArtifactIds.All(inputs.Contains));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
