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
    internal const string UnrecordedInputReason = "The execution's original frozen-input evidence was not recorded completely.";
    internal const string CorruptReason = "The retained run evidence is corrupt and cannot be downloaded.";
    internal const string TemporarilyUnavailableReason = "The run manifest is temporarily unavailable. Refresh the run to try again.";

    // Allowances for the encoded members that are not variable strings: names, punctuation, numbers, identifiers,
    // timestamps and durations at their widest. Each is above the widest actual encoding, which the tests measure.
    internal const int EncodedHeaderBytes = 1024;
    internal const int EncodedNodeBytes = 512;
    internal const int EncodedRowBytes = 384;
    internal const int EncodedSourceArtifactIdBytes = 39;
    // An ASCII letter or digit is never escaped and encodes as one byte. Any other UTF-16 unit encodes as at most a
    // six-byte \uXXXX escape.
    private const int EncodedBytesPerEscapedCharacter = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static (byte[]? Payload, string? Reason) Create(ProcessingGraphExecutionDetail detail)
    {
        if (Validate(detail) is { } reason) return (null, reason);
        // The bounded store snapshot caps the input. Also cap the encoded result, including JSON escaping.
        var payload = Encode(detail);
        return payload.Length > ProcessingRunManifestBounds.MaximumBytes
            ? (null, ProcessingRunManifestBounds.UnavailableReason) : (payload, null);
    }

    /// <summary>
    /// Why the run page cannot offer this execution's manifest, from the evidence read beside its detail, or null
    /// when the download will produce it.
    /// </summary>
    internal static string? UnavailableReason(ProcessingGraphExecutionDetailWithRunManifest read) => read.RunManifestFailure switch
    {
        ProcessingRunManifestEvidenceFailure.ExceedsBounds => ProcessingRunManifestBounds.UnavailableReason,
        ProcessingRunManifestEvidenceFailure.Corrupt => CorruptReason,
        ProcessingRunManifestEvidenceFailure.Unavailable => TemporarilyUnavailableReason,
        _ => read.RunManifest is { } manifest ? UnavailableReason(manifest) : MissingReason
    };

    /// <summary>
    /// The reason <see cref="Create"/> gives for the same detail, without building the payload at any size. The
    /// checks are <see cref="Create"/>'s own. A manifest whose upper bound is within the byte limit is known to fit
    /// from its counts and strings. Any other is decided by its exact encoded length, which is counted and never
    /// held.
    /// </summary>
    internal static string? UnavailableReason(ProcessingGraphExecutionDetail detail)
        => UnavailableReason(detail, EncodedLength);

    /// <inheritdoc cref="UnavailableReason(ProcessingGraphExecutionDetail)"/>
    internal static string? UnavailableReason(ProcessingGraphExecutionDetail detail, Func<ProcessingGraphExecutionDetail, long> encodedLength)
        => Validate(detail) ?? (MaximumEncodedBytes(detail) <= ProcessingRunManifestBounds.MaximumBytes ||
            encodedLength(detail) <= ProcessingRunManifestBounds.MaximumBytes ? null : ProcessingRunManifestBounds.UnavailableReason);

    /// <summary>
    /// An upper bound on the encoded manifest's length, from element counts and the characters of its strings. It
    /// never understates: every member that is not a variable string is covered by a fixed allowance, and every
    /// character of a variable string that could be escaped is counted as escaped. A member added to the manifest
    /// must keep it so.
    /// </summary>
    internal static long MaximumEncodedBytes(ProcessingGraphExecutionDetail detail)
    {
        static long Text(string? value)
        {
            var bytes = 0L;
            foreach (var character in value ?? string.Empty)
                bytes += char.IsAsciiLetterOrDigit(character) ? 1 : EncodedBytesPerEscapedCharacter;
            return bytes;
        }
        var execution = detail.Execution;
        var bytes = EncodedHeaderBytes + Text(execution.ExecutionClass.ToString()) + Text(execution.Status.ToString()) +
            Text(execution.GraphRevisionId) + Text(execution.GraphDefinitionIdentitySha256) +
            Text(execution.SharedPlanIdentitySha256) + Text(execution.LocalPlanIdentitySha256);
        foreach (var node in detail.Nodes)
        {
            bytes += EncodedNodeBytes + Text(node.NodeId) + Text(node.PlanSha256) + Text(node.Status) +
                Text(node.NormalizationPlanIdentitySha256);
            foreach (var input in node.Inputs)
                bytes += EncodedRowBytes + Text(input.Kind.ToString()) + Text(input.DescriptorSha256) +
                    Text(input.PayloadSha256) + Text(input.OutputIdentitySha256);
            foreach (var master in node.CalibrationInputs)
                bytes += EncodedRowBytes + Text(CalibrationMasterKind) + Text(master.PayloadSha256) + Text(master.Kind);
            foreach (var attempt in node.Attempts)
                bytes += EncodedRowBytes + Text(attempt.Status) + Text(attempt.Outcome?.ToString()) +
                    Text(attempt.ExecutionRoute.ToString());
            foreach (var output in node.Outputs)
                bytes += EncodedRowBytes + Text(output.OutputIdentitySha256) + Text(output.Role.ToString()) +
                    Text(output.PayloadSha256) + (long)EncodedSourceArtifactIdBytes * output.SourceArtifactIds.Count;
        }
        return bytes;
    }

    private static string? Validate(ProcessingGraphExecutionDetail detail)
    {
        if (detail.FrozenNodeCount is { } expected && expected != detail.Nodes.Count) return MissingReason;
        if (detail.Nodes.Count > ProcessingRunManifestBounds.MaximumNodes || detail.Nodes.Any(static node =>
            node.Inputs.Count + node.CalibrationInputs.Count > ProcessingRunManifestBounds.MaximumRows ||
            node.Outputs.Count > ProcessingRunManifestBounds.MaximumRows ||
            node.Attempts.Count > ProcessingRunManifestBounds.MaximumRows) ||
            detail.Nodes.Sum(static node => (long)node.Inputs.Count + node.CalibrationInputs.Count + node.Outputs.Count + node.Attempts.Count) > ProcessingRunManifestBounds.MaximumRows * 4L)
            return ProcessingRunManifestBounds.UnavailableReason;
        if (detail.Nodes.Any(static node => !HasRetainedArtifactBindings(node)))
            return ArtifactEvidenceUnavailableReason;
        if (detail.Nodes.Any(static node => node.InputEvidenceIncomplete))
            return UnrecordedInputReason;
        if (detail.Nodes.Any(static node => node.CommittedOutputCount is null))
            return UnrecordedOutputReason;
        if (detail.Nodes.Any(static node => node.NodeId.Length is 0 or > 128 ||
            node.NodeId != node.NodeId.Trim() || node.NodeId.Any(static value => char.IsControl(value) || value is '/' or '\\')))
            return IdentifierUnavailableReason;
        if (detail.Nodes.Any(static node => node.Outputs.Any(static output =>
            !IsSha256(output.PayloadSha256) || output.SourceArtifactIds.Count > 512) ||
            node.Inputs.Any(static input => !IsSha256(input.PayloadSha256)) ||
            node.CalibrationInputs.Any(static input => !IsSha256(input.PayloadSha256))))
            return IncompleteReason;
        var execution = detail.Execution;
        if (!IsSha256(execution.GraphRevisionId) || !IsSha256(execution.GraphDefinitionIdentitySha256) ||
            !IsSha256(execution.SharedPlanIdentitySha256) || !IsSha256(execution.LocalPlanIdentitySha256) ||
            detail.Nodes.Any(static node => !IsSha256(node.PlanSha256)))
            return IncompleteReason;
        return null;
    }

    /// <summary>The manifest's bytes for a detail that passed <see cref="Validate"/>, whatever their length.</summary>
    internal static byte[] Encode(ProcessingGraphExecutionDetail detail)
        => JsonSerializer.SerializeToUtf8Bytes(Manifest(detail), Json);

    /// <summary>
    /// The length of <see cref="Encode"/>'s result, without building it. The same manifest goes through the same
    /// serializer, which writes it in pieces to a sink that keeps only their total.
    /// </summary>
    internal static long EncodedLength(ProcessingGraphExecutionDetail detail)
    {
        using var counted = new LengthStream();
        JsonSerializer.Serialize(counted, Manifest(detail), Json);
        return counted.Length;
    }

    // Serialized by its runtime type, so the payload and its counted length come from one definition.
    private static object Manifest(ProcessingGraphExecutionDetail detail)
    {
        var execution = detail.Execution;
        // Free-text reasons, trigger references, lease owners, configurations and diagnostic objects
        // are deliberately excluded. Status strings originate in checked durable enum columns.
        return new
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
            yield return new(ordinal++, null, CalibrationMasterKind, null, master.ArtifactId, null, master.PayloadSha256, null, master.Kind);
    }

    private const string CalibrationMasterKind = "CalibrationMaster";

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

    /// <summary>A write-only sink that keeps the number of bytes written and none of the bytes.</summary>
    private sealed class LengthStream : Stream
    {
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => length += buffer.Length;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
