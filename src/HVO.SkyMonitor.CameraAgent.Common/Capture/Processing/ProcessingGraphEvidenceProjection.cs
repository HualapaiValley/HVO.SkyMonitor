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
internal static class ProcessingGraphEvidenceProjection
{
    /// <summary>
    /// Projects one persisted graph revision. <paramref name="assignment"/> is supplied by the caller from the
    /// delivery store; when it is null the revision is exported as locally compiled.
    /// </summary>
    internal static GraphRevisionEvidenceV1 CreateRevisionEvidence(
        ProcessingGraphRevisionSnapshot revision,
        ExecutionEvidenceAssignmentProvenanceV1? assignment)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var state = revision.State;
        return new(
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
            ParseDocument(revision.DefinitionJson),
            ParseDocument(revision.FrozenPlanJson),
            state.CreatedUtc,
            state.ValidatedUtc,
            state.ActivatedUtc,
            state.RetiredUtc,
            assignment);
    }

    /// <summary>Projects one execution's immutable production facts. Availability is reported separately.</summary>
    internal static GraphExecutionEvidenceV1 CreateExecutionEvidence(ProcessingGraphExecutionDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var execution = detail.Execution;
        return new(
            GraphExecutionEvidenceV1.CurrentSchemaVersion,
            execution.ExecutionId,
            execution.ExecutionClass == ProcessingGraphExecutionClass.Replay
                ? ExecutionEvidenceExecutionClass.Replay
                : ExecutionEvidenceExecutionClass.Live,
            Enum.Parse<ExecutionEvidenceExecutionStatus>(execution.Status.ToString()),
            execution.CaptureId,
            execution.PrimaryArtifactId,
            execution.GraphRevisionId,
            execution.GraphDefinitionIdentitySha256,
            execution.SharedPlanIdentitySha256,
            execution.LocalPlanIdentitySha256,
            execution.TriggerKind,
            [.. detail.Nodes.Select(node => CreateNode(node, execution.CaptureId))],
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
            execution.FailureReason);
    }

    /// <summary>
    /// Projects the current availability of every artifact this execution produced. Returns null when the
    /// execution produced no output, because an empty observation batch carries no information.
    /// </summary>
    internal static ArtifactAvailabilityReportV1? CreateAvailabilityReport(
        ProcessingGraphExecutionDetail detail,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var observations = detail.Nodes
            .SelectMany(node => node.Outputs)
            .DistinctBy(static output => output.ArtifactId)
            .Select(output => new ArtifactAvailabilityObservationV1(
                ArtifactAvailabilityObservationV1.CurrentSchemaVersion,
                CreateOutputArtifact(output, detail.Execution.CaptureId),
                Enum.Parse<ExecutionEvidenceAvailabilityState>(output.AvailabilityState),
                observedAtUtc,
                output.AvailabilityReason))
            .ToImmutableArray();
        return observations.IsEmpty
            ? null
            : new(
                ArtifactAvailabilityReportV1.CurrentSchemaVersion,
                detail.Execution.ExecutionId,
                observations);
    }

    /// <summary>Wraps one projected body in a sealed, sequenced envelope.</summary>
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

    /// <summary>Wraps one availability report in a sealed, sequenced envelope. Availability is never corrected.</summary>
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

    private static ExecutionEvidenceNodeV1 CreateNode(
        ProcessingGraphExecutionNodeState node,
        Guid executionCaptureId)
        => new(
            ExecutionEvidenceNodeV1.CurrentSchemaVersion,
            node.NodeId,
            node.Required,
            node.PlanSha256,
            Enum.Parse<ExecutionEvidenceNodeStatus>(node.Status),
            [.. node.Inputs.Select(CreateInput)],
            [.. node.Attempts.Select(CreateAttempt)],
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

    private static ExecutionEvidenceAttemptV1 CreateAttempt(ProcessingGraphNodeAttemptState attempt)
        => new(
            ExecutionEvidenceAttemptV1.CurrentSchemaVersion,
            attempt.AttemptNumber,
            Enum.Parse<ExecutionEvidenceAttemptStatus>(attempt.Status),
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

    private static JsonElement ParseDocument(byte[] utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        return document.RootElement.Clone();
    }
}
