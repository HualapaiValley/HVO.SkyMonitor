using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>
/// Read-only projection from the delivered durable graph-execution store into the transport-neutral
/// <c>hvo-cameraagent-execution-evidence-v1</c> contract. The projection never writes, never changes the durable
/// schema, and never reads artifact payload bytes: it maps immutable production facts that the store already
/// holds. The durable outbox, sender, retry, and retention behavior belong to the exporter issue, not here.
/// </summary>
internal enum ProcessingGraphEvidenceProjectionOutcome
{
    /// <summary>The body was projected and <c>Value</c> is populated.</summary>
    Projected,

    /// <summary>The source carried nothing to report; this is not a failure and no unit is enlisted.</summary>
    Empty,

    /// <summary>
    /// The durable row carries a value this contract version cannot express. The exporter quarantines the unit with
    /// the reason and field path instead of failing the export loop, so one unmappable row never stalls the lane.
    /// </summary>
    Rejected
}

internal sealed record ProcessingGraphEvidenceProjectionResult<T>(
    ProcessingGraphEvidenceProjectionOutcome Outcome,
    T? Value,
    string? ReasonCode,
    string? FieldPath)
    where T : class
{
    internal static ProcessingGraphEvidenceProjectionResult<T> Projected(T value)
        => new(ProcessingGraphEvidenceProjectionOutcome.Projected, value, null, null);

    internal static ProcessingGraphEvidenceProjectionResult<T> Empty { get; } =
        new(ProcessingGraphEvidenceProjectionOutcome.Empty, null, null, null);

    internal static ProcessingGraphEvidenceProjectionResult<T> Rejected(string reasonCode, string fieldPath)
        => new(ProcessingGraphEvidenceProjectionOutcome.Rejected, null, reasonCode, fieldPath);
}

internal static class ProcessingGraphEvidenceProjection
{
    /// <summary>
    /// Projects one persisted graph revision. <paramref name="assignment"/> is supplied by the caller from the
    /// delivery store; when it is null the revision is exported as locally compiled.
    /// </summary>
    internal static ProcessingGraphEvidenceProjectionResult<GraphRevisionEvidenceV1> CreateRevisionEvidence(
        ProcessingGraphRevisionSnapshot revision,
        ExecutionEvidenceAssignmentProvenanceV1? assignment)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var state = revision.State;
        if (!TryParseDocument(revision.DefinitionJson, out var definition))
        {
            return ProcessingGraphEvidenceProjectionResult<GraphRevisionEvidenceV1>.Rejected(
                GraphExecutionEvidenceReasonCodes.InvalidJson, "graphRevision.canonicalDefinition");
        }
        if (!TryParseDocument(revision.FrozenPlanJson, out var frozenPlan))
        {
            return ProcessingGraphEvidenceProjectionResult<GraphRevisionEvidenceV1>.Rejected(
                GraphExecutionEvidenceReasonCodes.InvalidJson, "graphRevision.frozenPlan");
        }
        return ProcessingGraphEvidenceProjectionResult<GraphRevisionEvidenceV1>.Projected(new(
            GraphRevisionEvidenceV1.CurrentSchemaVersion,
            state.RevisionId,
            state.Name,
            state.Revision,
            assignment is null
                ? ExecutionEvidenceRevisionOrigin.LocalOnly
                : ExecutionEvidenceRevisionOrigin.CentrallyAssigned,
            state.DefinitionIdentitySha256,
            state.SharedPlanIdentitySha256,
            state.LocalPlanIdentitySha256,
            definition,
            frozenPlan,
            state.CreatedUtc,
            state.ValidatedUtc,
            state.ActivatedUtc,
            state.RetiredUtc,
            assignment));
    }

    /// <summary>Projects one execution's immutable production facts. Availability is reported separately.</summary>
    internal static ProcessingGraphEvidenceProjectionResult<GraphExecutionEvidenceV1> CreateExecutionEvidence(
        ProcessingGraphExecutionDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var execution = detail.Execution;
        if (MapExecutionClass(execution.ExecutionClass) is not { } executionClass)
        {
            return Reject<GraphExecutionEvidenceV1>(
                GraphExecutionEvidenceReasonCodes.InvalidBody, "execution.executionClass");
        }
        if (MapStatus(execution.Status) is not { } status)
        {
            return Reject<GraphExecutionEvidenceV1>(
                GraphExecutionEvidenceReasonCodes.InvalidBody, "execution.status");
        }
        var nodes = new ExecutionEvidenceNodeV1[detail.Nodes.Count];
        for (var index = 0; index < detail.Nodes.Count; index++)
        {
            var node = detail.Nodes[index];
            if (MapNodeStatus(node.Status) is not { } nodeStatus)
            {
                return Reject<GraphExecutionEvidenceV1>(
                    GraphExecutionEvidenceReasonCodes.InvalidNode, $"execution.nodes[{index}].status");
            }
            var attempts = new ExecutionEvidenceAttemptV1[node.Attempts.Count];
            for (var attempt = 0; attempt < node.Attempts.Count; attempt++)
            {
                if (MapAttemptStatus(node.Attempts[attempt].Status) is not { } attemptStatus)
                {
                    return Reject<GraphExecutionEvidenceV1>(
                        GraphExecutionEvidenceReasonCodes.InvalidAttempt,
                        $"execution.nodes[{index}].attempts[{attempt}].status");
                }
                attempts[attempt] = CreateAttempt(node.Attempts[attempt], attemptStatus);
            }
            nodes[index] = CreateNode(node, nodeStatus, attempts, execution.CaptureId);
        }
        return ProcessingGraphEvidenceProjectionResult<GraphExecutionEvidenceV1>.Projected(new(
            GraphExecutionEvidenceV1.CurrentSchemaVersion,
            execution.ExecutionId,
            executionClass,
            status,
            execution.CaptureId,
            execution.PrimaryArtifactId,
            execution.GraphRevisionId,
            execution.GraphDefinitionIdentitySha256,
            execution.SharedPlanIdentitySha256,
            execution.LocalPlanIdentitySha256,
            execution.TriggerKind,
            [.. nodes],
            execution.AcceptedUtc,
            execution.AvailableUtc,
            execution.DeadlineUtc,
            execution.MaximumAgeUtc,
            execution.CancellationRequested,
            execution.AttemptCount,
            execution.TriggerReference,
            execution.Priority,
            execution.StartedUtc,
            execution.CompletedUtc,
            execution.FailureReason));
    }

    /// <summary>
    /// Projects the current availability of every artifact this execution produced. Returns null when the
    /// execution produced no output, because an empty observation batch carries no information.
    /// </summary>
    internal static ProcessingGraphEvidenceProjectionResult<ArtifactAvailabilityReportV1> CreateAvailabilityReport(
        ProcessingGraphExecutionDetail detail,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var outputs = detail.Nodes
            .SelectMany(static node => node.Outputs)
            .DistinctBy(static output => output.ArtifactId)
            .ToArray();
        var observations = new ArtifactAvailabilityObservationV1[outputs.Length];
        for (var index = 0; index < outputs.Length; index++)
        {
            var output = outputs[index];
            if (MapAvailability(output.AvailabilityState) is not { } state)
            {
                return Reject<ArtifactAvailabilityReportV1>(
                    GraphExecutionEvidenceReasonCodes.InvalidAvailability,
                    $"availability.observations[{index}].state");
            }
            observations[index] = new(
                ArtifactAvailabilityObservationV1.CurrentSchemaVersion,
                CreateOutputArtifact(output, detail.Execution.CaptureId),
                state,
                observedAtUtc,
                // The durable schema does not couple state and reason, but the contract reserves a reason for a
                // non-available observation, so an 'Available' row's reason is dropped rather than exported.
                state == ExecutionEvidenceAvailabilityState.Available ? null : output.AvailabilityReason);
        }
        return observations.Length == 0
            ? ProcessingGraphEvidenceProjectionResult<ArtifactAvailabilityReportV1>.Empty
            : ProcessingGraphEvidenceProjectionResult<ArtifactAvailabilityReportV1>.Projected(new(
                ArtifactAvailabilityReportV1.CurrentSchemaVersion,
                detail.Execution.ExecutionId,
                [.. observations]));
    }

    private static ProcessingGraphEvidenceProjectionResult<T> Reject<T>(string reasonCode, string fieldPath)
        where T : class
        => ProcessingGraphEvidenceProjectionResult<T>.Rejected(reasonCode, fieldPath);

    /// <summary>
    /// Wraps one projected revision in a sealed, sequenced envelope. The declared policy is stamped without a
    /// rewrite because a revision body has no operator-identifying member today: redaction applies only to an
    /// execution's trigger reference and lease owners.
    /// </summary>
    internal static ExecutionEvidenceEnvelopeV1 CreateEnvelope(
        ExecutionEvidenceOriginV1 origin,
        long originSequence,
        Guid evidenceId,
        DateTimeOffset producedAtUtc,
        GraphRevisionEvidenceV1 revision,
        ExecutionEvidenceRedactionPolicyV1 redaction,
        ExecutionEvidenceCorrectionV1? correction = null)
        => GraphExecutionEvidenceJson.Seal(new(
            ExecutionEvidenceEnvelopeV1.CurrentSchemaVersion,
            evidenceId,
            origin,
            originSequence,
            producedAtUtc,
            ExecutionEvidenceBodyKind.GraphRevision,
            GraphExecutionEvidenceJson.UnhashedPayloadSha256,
            redaction,
            GraphRevision: revision,
            Correction: correction));

    /// <summary>Wraps one projected execution in a sealed, sequenced envelope, applying the redaction policy.</summary>
    internal static ExecutionEvidenceEnvelopeV1 CreateEnvelope(
        ExecutionEvidenceOriginV1 origin,
        long originSequence,
        Guid evidenceId,
        DateTimeOffset producedAtUtc,
        GraphExecutionEvidenceV1 execution,
        ExecutionEvidenceRedactionPolicyV1 redaction,
        ExecutionEvidenceCorrectionV1? correction = null)
        => GraphExecutionEvidenceJson.Seal(GraphExecutionEvidenceJson.Redact(
            new(
                ExecutionEvidenceEnvelopeV1.CurrentSchemaVersion,
                evidenceId,
                origin,
                originSequence,
                producedAtUtc,
                ExecutionEvidenceBodyKind.GraphExecution,
                GraphExecutionEvidenceJson.UnhashedPayloadSha256,
                redaction,
                Execution: execution,
                Correction: correction),
            redaction));

    /// <summary>
    /// Wraps one availability report in a sealed, sequenced envelope. Availability is never corrected, and like a
    /// revision it has no operator-identifying member to rewrite.
    /// </summary>
    internal static ExecutionEvidenceEnvelopeV1 CreateEnvelope(
        ExecutionEvidenceOriginV1 origin,
        long originSequence,
        Guid evidenceId,
        DateTimeOffset producedAtUtc,
        ArtifactAvailabilityReportV1 availability,
        ExecutionEvidenceRedactionPolicyV1 redaction)
        => GraphExecutionEvidenceJson.Seal(new(
            ExecutionEvidenceEnvelopeV1.CurrentSchemaVersion,
            evidenceId,
            origin,
            originSequence,
            producedAtUtc,
            ExecutionEvidenceBodyKind.ArtifactAvailability,
            GraphExecutionEvidenceJson.UnhashedPayloadSha256,
            redaction,
            Availability: availability));

    /// <remarks>
    /// <c>outputs[].ordinal</c> is the dense export ordinal the delivered store already surfaces through
    /// <see cref="ProcessingGraphExecutionOutputState.Ordinal"/>, not the durable <c>output_ordinal</c> column.
    /// Order is preserved because the store reads outputs ordered by the durable ordinal; the values differ only
    /// when the stored ordinals have gaps. Surfacing the durable ordinal would change delivered read behaviour
    /// and is therefore out of scope here.
    /// </remarks>
    private static ExecutionEvidenceNodeV1 CreateNode(
        ProcessingGraphExecutionNodeState node,
        ExecutionEvidenceNodeStatus status,
        IReadOnlyList<ExecutionEvidenceAttemptV1> attempts,
        Guid executionCaptureId)
        => new(
            ExecutionEvidenceNodeV1.CurrentSchemaVersion,
            node.NodeId,
            node.Required,
            node.PlanSha256,
            status,
            [.. node.Inputs.Select(CreateInput)],
            [.. attempts],
            [.. node.Outputs.Select(output => new ExecutionEvidenceOutputV1(
                ExecutionEvidenceOutputV1.CurrentSchemaVersion,
                output.Ordinal,
                CreateOutputArtifact(output, executionCaptureId)))],
            node.Reason,
            node.StartedUtc,
            node.CompletedUtc);

    private static ExecutionEvidenceInputV1 CreateInput(ProcessingGraphExecutionInputState input)
        => new(
            ExecutionEvidenceInputV1.CurrentSchemaVersion,
            input.Ordinal,
            input.WindowPosition,
            input.Kind == ProcessingGraphExecutionInputKind.ProcessingOutput
                ? ExecutionEvidenceInputKind.ProcessingOutput
                : ExecutionEvidenceInputKind.RawCapture,
            new(
                ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion,
                input.ArtifactId,
                input.CaptureId,
                input.OutputIdentitySha256,
                input.Kind == ProcessingGraphExecutionInputKind.RawCapture
                    ? AgentCore.FrameArtifactRole.Raw
                    : null,
                null,
                input.PayloadSha256,
                null,
                null,
                input.DescriptorSha256));

    // The execution route recorded on the attempt is deliberately not exported here. ExecutionEvidenceAttemptV1
    // is a versioned durable export contract, so adding a member is a schema-version change with its own
    // compatibility obligations for existing readers. Issue #799 asks for the route to be observable on the
    // operations read path, which ReplayExecutionAttemptView and CameraAgentProcessingNodeAttemptView satisfy by
    // projecting it; widening the durable evidence export is separate work and is not smuggled in here.
    private static ExecutionEvidenceAttemptV1 CreateAttempt(
        ProcessingGraphNodeAttemptState attempt,
        ExecutionEvidenceAttemptStatus status)
        => new(
            ExecutionEvidenceAttemptV1.CurrentSchemaVersion,
            attempt.AttemptNumber,
            status,
            attempt.StartedUtc,
            attempt.CompletedUtc,
            attempt.Outcome,
            attempt.Reason,
            attempt.Duration?.Ticks,
            attempt.LeaseOwner);

    private static ExecutionEvidenceArtifactReferenceV1 CreateOutputArtifact(
        ProcessingGraphExecutionOutputState output,
        Guid captureId)
        => new(
            ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion,
            output.ArtifactId,
            captureId,
            output.OutputIdentitySha256,
            output.Role,
            output.Variant);

    // The durable enums and strings are mapped explicitly rather than by name, so a future durable member is an
    // explicit bounded rejection here instead of a silent name coincidence at export time. Returning null rather
    // than throwing keeps one unmappable durable row from stalling the whole export lane: the exporter quarantines
    // that unit and continues.
    private static ExecutionEvidenceExecutionClass? MapExecutionClass(ProcessingGraphExecutionClass value)
        => value switch
        {
            ProcessingGraphExecutionClass.Live => ExecutionEvidenceExecutionClass.Live,
            ProcessingGraphExecutionClass.Replay => ExecutionEvidenceExecutionClass.Replay,
            _ => null
        };

    private static ExecutionEvidenceExecutionStatus? MapStatus(ProcessingGraphExecutionStatus value)
        => value switch
        {
            ProcessingGraphExecutionStatus.Pending => ExecutionEvidenceExecutionStatus.Pending,
            ProcessingGraphExecutionStatus.Running => ExecutionEvidenceExecutionStatus.Running,
            ProcessingGraphExecutionStatus.Completed => ExecutionEvidenceExecutionStatus.Completed,
            ProcessingGraphExecutionStatus.Failed => ExecutionEvidenceExecutionStatus.Failed,
            ProcessingGraphExecutionStatus.Cancelled => ExecutionEvidenceExecutionStatus.Cancelled,
            ProcessingGraphExecutionStatus.Expired => ExecutionEvidenceExecutionStatus.Expired,
            _ => null
        };

    private static ExecutionEvidenceNodeStatus? MapNodeStatus(string value)
        => value switch
        {
            "Pending" => ExecutionEvidenceNodeStatus.Pending,
            "Running" => ExecutionEvidenceNodeStatus.Running,
            "Completed" => ExecutionEvidenceNodeStatus.Completed,
            "Skipped" => ExecutionEvidenceNodeStatus.Skipped,
            "RetryableFailure" => ExecutionEvidenceNodeStatus.RetryableFailure,
            "TerminalFailure" => ExecutionEvidenceNodeStatus.TerminalFailure,
            _ => null
        };

    private static ExecutionEvidenceAttemptStatus? MapAttemptStatus(string value)
        => value switch
        {
            "Running" => ExecutionEvidenceAttemptStatus.Running,
            "Completed" => ExecutionEvidenceAttemptStatus.Completed,
            "Skipped" => ExecutionEvidenceAttemptStatus.Skipped,
            "RetryableFailure" => ExecutionEvidenceAttemptStatus.RetryableFailure,
            "TerminalFailure" => ExecutionEvidenceAttemptStatus.TerminalFailure,
            "Interrupted" => ExecutionEvidenceAttemptStatus.Interrupted,
            _ => null
        };

    private static ExecutionEvidenceAvailabilityState? MapAvailability(string value)
        => value switch
        {
            "Available" => ExecutionEvidenceAvailabilityState.Available,
            "Missing" => ExecutionEvidenceAvailabilityState.Missing,
            "Quarantined" => ExecutionEvidenceAvailabilityState.Quarantined,
            _ => null
        };

    private static bool TryParseDocument(byte[] utf8Json, out JsonElement value)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
