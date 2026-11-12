using System.Globalization;
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

    // What the serializer writes for a null and for an identifier in quotes.
    private const int NullLength = 4;
    private const int GuidLength = 38;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // The fixed bytes of each manifest object, for EncodedLength. The names are the members of Encode's projection
    // as the serializer writes them.
    private static readonly int ExecutionFrame = Frame("schemaVersion", "executionId", "executionClass", "status", "captureId",
        "primaryArtifactId", "graphRevisionId", "graphDefinitionIdentitySha256", "sharedPlanIdentitySha256", "localPlanIdentitySha256",
        "acceptedUtc", "startedUtc", "completedUtc", "cancellationRequested", "attemptCount", "nodes");
    private static readonly int NodeFrame = Frame("nodeId", "required", "planSha256", "status", "startedUtc", "completedUtc",
        "attemptCount", "normalizationPlanIdentitySha256", "inputs", "attempts", "outputs");
    private static readonly int InputFrame = Frame("ordinal", "windowPosition", "kind", "captureId", "artifactId", "descriptorSha256",
        "payloadSha256", "outputIdentitySha256", "calibrationKind");
    private static readonly int AttemptFrame = Frame("attemptNumber", "startedUtc", "completedUtc", "status", "outcome", "duration",
        "executionRoute");
    private static readonly int OutputFrame = Frame("ordinal", "outputIdentitySha256", "artifactId", "role", "payloadSha256",
        "sourceArtifactIds", "published");

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
    /// The reason <see cref="Create"/> gives for the same detail, without building the payload and without
    /// serializing anything. The checks are <see cref="Create"/>'s own, and the byte limit is decided from the
    /// payload's length, which is computed.
    /// </summary>
    internal static string? UnavailableReason(ProcessingGraphExecutionDetail detail)
        => Validate(detail) ?? (EncodedLength(detail) <= ProcessingRunManifestBounds.MaximumBytes
            ? null : ProcessingRunManifestBounds.UnavailableReason);

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
    {
        var execution = detail.Execution;
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
        return JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
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

    /// <summary>
    /// The length of <see cref="Encode"/>'s result, computed from the detail. Nothing is serialized: every member's
    /// encoded length follows from its value and the serializer's rules for it. This mirrors <see cref="Encode"/>
    /// and <see cref="CreateInputs"/> member for member, in their order. A member added there needs its length here;
    /// the tests that hold the two equal to the byte fail until it has one.
    /// </summary>
    internal static long EncodedLength(ProcessingGraphExecutionDetail detail)
    {
        var execution = detail.Execution;
        long length = ExecutionFrame
            + Text(SchemaVersion)
            + GuidLength // executionId
            + Name(execution.ExecutionClass)
            + Name(execution.Status)
            + GuidLength // captureId
            + GuidLength // primaryArtifactId
            + Text(execution.GraphRevisionId)
            + Text(execution.GraphDefinitionIdentitySha256)
            + Text(execution.SharedPlanIdentitySha256)
            + Text(execution.LocalPlanIdentitySha256)
            + Instant(execution.AcceptedUtc)
            + Instant(execution.StartedUtc)
            + Instant(execution.CompletedUtc)
            + Flag(execution.CancellationRequested)
            + Number(execution.AttemptCount)
            + ArrayFrame(detail.Nodes.Count);
        foreach (var node in detail.Nodes)
        {
            length += NodeFrame
                + Text(node.NodeId)
                + Flag(node.Required)
                + Text(node.PlanSha256)
                + Text(node.Status)
                + Instant(node.StartedUtc)
                + Instant(node.CompletedUtc)
                + Number(node.AttemptCount)
                + Text(node.NormalizationPlanIdentitySha256)
                + ArrayFrame(node.Inputs.Count + node.CalibrationInputs.Count)
                + ArrayFrame(node.Attempts.Count)
                + ArrayFrame(node.Outputs.Count);
            foreach (var input in node.Inputs)
                length += InputFrame
                    + Number(input.Ordinal)
                    + (input.Kind is ProcessingGraphExecutionInputKind.AuxiliaryRawCapture or ProcessingGraphExecutionInputKind.AuxiliaryProcessingOutput
                        ? NullLength : Number(input.WindowPosition))
                    + Text(Enum.GetName(input.Kind) ?? input.Kind.ToString()) // the kind's ToString, unboxed when it has a name
                    + GuidLength // captureId
                    + GuidLength // artifactId
                    + Text(input.DescriptorSha256)
                    + Text(input.PayloadSha256)
                    + Text(input.OutputIdentitySha256)
                    + NullLength; // calibrationKind
            var ordinal = node.Inputs.Count == 0 ? 0 : node.Inputs.Max(static input => input.Ordinal) + 1;
            foreach (var master in node.CalibrationInputs)
                length += InputFrame
                    + Number(ordinal++)
                    + NullLength // windowPosition
                    + Text(CalibrationMasterKind)
                    + NullLength // captureId
                    + GuidLength // artifactId
                    + NullLength // descriptorSha256
                    + Text(master.PayloadSha256)
                    + NullLength // outputIdentitySha256
                    + Text(master.Kind);
            foreach (var attempt in node.Attempts)
                length += AttemptFrame
                    + Number(attempt.AttemptNumber)
                    + Instant(attempt.StartedUtc)
                    + Instant(attempt.CompletedUtc)
                    + Text(attempt.Status)
                    + (attempt.Outcome is { } outcome ? Name(outcome) : NullLength)
                    + Duration(attempt.Duration)
                    + Name(attempt.ExecutionRoute);
            foreach (var output in node.Outputs)
                length += OutputFrame
                    + Number(output.Ordinal)
                    + Text(output.OutputIdentitySha256)
                    + GuidLength // artifactId
                    + Name(output.Role)
                    + Text(output.PayloadSha256)
                    // The identifiers are all one length, so their number is all that is needed of them.
                    + ArrayFrame(output.SourceArtifactIds.Count) + (long)GuidLength * output.SourceArtifactIds.Count
                    + Flag(output.Published);
        }
        return length;
    }

    // An object's braces, its member names in quotes with their colons, and the commas between its members.
    private static int Frame(params string[] names) => 2 + names.Sum(static name => name.Length + 3) + names.Length - 1;

    // An array's brackets and the commas between its elements.
    private static long ArrayFrame(int count) => count == 0 ? 2 : count + 1L;

    private static int Flag(bool value) => value ? 4 : 5;

    private static int Number(int value)
    {
        var length = value < 0 ? 2 : 1;
        for (var rest = Math.Abs((long)value) / 10; rest != 0; rest /= 10) length++;
        return length;
    }

    // In quotes: "yyyy-MM-ddTHH:mm:ss", the fraction of a second without its trailing zeros and omitted when it is
    // zero, and the offset as "+hh:mm".
    private static int Instant(DateTimeOffset? value)
    {
        if (value is not { } instant) return NullLength;
        var length = 2 + 19 + 6;
        var fraction = instant.Ticks % TimeSpan.TicksPerSecond;
        if (fraction == 0) return length;
        length += 8;
        for (; fraction % 10 == 0; fraction /= 10) length--;
        return length;
    }

    // In quotes, the constant format the serializer writes a duration in.
    private static int Duration(TimeSpan? value)
    {
        if (value is not { } duration) return NullLength;
        Span<char> text = stackalloc char[32];
        return duration.TryFormat(text, out var written, "c", CultureInfo.InvariantCulture)
            ? written + 2 : throw new InvalidOperationException("A duration did not format.");
    }

    // The string enum converter writes a named value as its name in quotes and any other value as its number.
    private static long Name<TEnum>(TEnum value) where TEnum : struct, Enum
        => Enum.GetName(value) is { } name ? Text(name) : value.ToString().Length;

    private static long Text(string? value) => value is null ? NullLength : 2 + Escaped(value);

    // The serializer's default encoder: an ASCII character is written as it is unless JSON or HTML could misread it;
    // the backslash and five controls take a two-byte escape; every other UTF-16 unit, a surrogate included, takes
    // a six-byte \uXXXX escape.
    private static long Escaped(string value)
    {
        long bytes = 0;
        foreach (var character in value)
            bytes += character switch
            {
                '\b' or '\t' or '\n' or '\f' or '\r' or '\\' => 2,
                < ' ' or '"' or '&' or '\'' or '+' or '<' or '>' or '`' or >= '\u007f' => 6,
                _ => 1
            };
        return bytes;
    }

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
