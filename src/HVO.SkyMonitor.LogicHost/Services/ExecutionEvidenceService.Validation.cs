using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class ExecutionEvidenceService
{
    private async Task<ExecutionEvidenceValidationResult> ValidateContextAsync(ExecutionEvidenceBinding binding,
        long originId, ExecutionEvidenceEnvelopeV1 envelope, CancellationToken cancellationToken)
    {
        if (await _db.Set<ImportedExecutionEvidenceUnit>().AnyAsync(u => u.OriginId == originId && u.EvidenceId == envelope.EvidenceId,
            cancellationToken).ConfigureAwait(false)) { return Invalid(GraphExecutionEvidenceReasonCodes.InvalidIdentity, "evidenceId"); }
        if (envelope.Correction is { } correction)
        {
            var predecessor = await _db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().SingleOrDefaultAsync(u =>
                u.OriginId == originId && u.OriginSequence == correction.CorrectsOriginSequence, cancellationToken).ConfigureAwait(false);
            if (predecessor is null) { return Invalid(GraphExecutionEvidenceReasonCodes.UnknownPredecessor, "correction"); }
            if (!predecessor.Accepted || predecessor.EvidenceId != correction.CorrectsEvidenceId || predecessor.Kind != envelope.Kind.ToString() ||
                envelope.Execution is { } correctedExecution && predecessor.ExecutionId != correctedExecution.ExecutionId ||
                envelope.GraphRevision is { } correctedRevision && predecessor.RevisionId != correctedRevision.RevisionId)
            { return Invalid(GraphExecutionEvidenceReasonCodes.InvalidCorrection, "correction"); }
        }
        if (envelope.GraphRevision is { } revision)
        {
            var plan = ExecutionEvidencePlanValidator.ValidateRevision(revision);
            if (!plan.IsValid) { return plan; }
            var existing = await RevisionAsync(binding.Id, revision.RevisionId, cancellationToken).ConfigureAwait(false);
            if (existing is not null && (existing.DefinitionIdentitySha256 != revision.DefinitionIdentitySha256 ||
                existing.SharedPlanIdentitySha256 != revision.SharedPlanIdentitySha256 || existing.LocalPlanIdentitySha256 != revision.LocalPlanIdentitySha256 ||
                CaptureContractJson.ComputeCanonicalJsonSha256(existing.FrozenPlan) != CaptureContractJson.ComputeCanonicalJsonSha256(revision.FrozenPlan)))
            { return Invalid(GraphExecutionEvidenceReasonCodes.InvalidHash, "graphRevision.revisionId"); }
            if (revision.Assignment is { } assignment)
            {
                var proposal = await _db.CentralProcessingGraphDeliveryProposals.AsNoTracking().Include(p => p.Revision)
                    .Include(p => p.Assignment).SingleOrDefaultAsync(p => p.Id == assignment.ProposalId, cancellationToken).ConfigureAwait(false);
                if (proposal is null || assignment.RegistrationId != binding.RegistrationId ||
                    assignment.LogicalCameraInstallationId != binding.LogicalCameraInstallationId || assignment.InstallationPublicId != binding.InstallationPublicId ||
                    proposal.RegistrationId != binding.RegistrationId || proposal.LogicalCameraInstallationId != binding.LogicalCameraInstallationId ||
                    proposal.InstallationPublicId != binding.InstallationPublicId || proposal.AssignmentId != assignment.AssignmentId ||
                    proposal.RevisionId != assignment.CatalogRevisionId || proposal.CapabilitySnapshotSha256 != assignment.CapabilitySnapshotSha256 ||
                    proposal.IssuedAtUtc != assignment.IssuedAtUtc || assignment.AcceptedAtUtc < proposal.IssuedAtUtc ||
                    proposal.Revision!.DefinitionIdentitySha256 != revision.DefinitionIdentitySha256 ||
                    proposal.Revision.EdgePlanIdentitySha256 != revision.SharedPlanIdentitySha256 ||
                    proposal.Assignment!.TargetHost != CentralProcessingGraphTargetHost.Edge)
                { return Invalid(GraphExecutionEvidenceReasonCodes.InvalidAssignment, "graphRevision.assignment"); }
                // Provenance names the historical delivery, not today's effective assignment or capability snapshot.
                var accepted = await _db.CentralProcessingGraphDeliveryFacts.AsNoTracking().AnyAsync(f =>
                    f.ProposalId == proposal.Id && f.Kind == "Accepted" && f.OccurredAtUtc == assignment.AcceptedAtUtc &&
                    f.LocalRevisionId == revision.RevisionId && f.DefinitionIdentitySha256 == revision.DefinitionIdentitySha256 &&
                    f.SharedPlanIdentitySha256 == revision.SharedPlanIdentitySha256 && f.LocalPlanIdentitySha256 == revision.LocalPlanIdentitySha256,
                    cancellationToken).ConfigureAwait(false);
                if (!accepted) { return Invalid(GraphExecutionEvidenceReasonCodes.InvalidAssignment, "graphRevision.assignment"); }
            }
        }
        if (envelope.Execution is { } execution)
        {
            var executionRevision = await RevisionAsync(binding.Id, execution.GraphRevisionId, cancellationToken).ConfigureAwait(false);
            if (executionRevision is null) { return Invalid(GraphExecutionEvidenceReasonCodes.UnknownRevision, "execution.graphRevisionId"); }
            var plan = ExecutionEvidencePlanValidator.ValidateExecution(execution, executionRevision);
            if (!plan.IsValid) { return plan; }
            var previous = await _db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().Where(u => u.BindingId == binding.Id &&
                u.Kind == nameof(ExecutionEvidenceBodyKind.GraphExecution) && u.ExecutionId == execution.ExecutionId && u.Accepted)
                .OrderByDescending(u => u.Id).Select(u => u.CanonicalBytes).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (previous is not null && envelope.Correction is null)
            {
                var prior = GraphExecutionEvidenceJson.ParseEnvelope(previous).Value!.Execution!;
                if (GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(envelope with { Execution = prior }) != envelope.PayloadSha256)
                { return Invalid(GraphExecutionEvidenceReasonCodes.InvalidCorrection, "execution.executionId"); }
            }
            var frame = await _db.CentralFrames.AsNoTracking().SingleOrDefaultAsync(f =>
                f.DevicePublicId == binding.InstallationPublicId && f.FrameId == execution.CaptureId, cancellationToken).ConfigureAwait(false);
            if (frame is not null && !Owns(binding, frame)) { return Invalid(GraphExecutionEvidenceReasonCodes.InvalidIdentity, "execution.captureId"); }
        }
        return ExecutionEvidenceValidationResult.Success;
    }

    private async Task<GraphRevisionEvidenceV1?> RevisionAsync(Guid bindingId, string revisionId, CancellationToken cancellationToken)
    {
        var bytes = await _db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().Where(u => u.BindingId == bindingId &&
            u.Kind == nameof(ExecutionEvidenceBodyKind.GraphRevision) && u.RevisionId == revisionId && u.Accepted)
            .OrderByDescending(u => u.Id).Select(u => u.CanonicalBytes).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : GraphExecutionEvidenceJson.ParseEnvelope(bytes).Value!.GraphRevision;
    }

    internal static bool Owns(ExecutionEvidenceBinding binding, CentralFrame frame)
        => frame.RegistrationId == binding.RegistrationId && frame.ObservatoryId == binding.ObservatoryId &&
            frame.LogicalCameraInstallationId == binding.LogicalCameraInstallationId && frame.DevicePublicId == binding.InstallationPublicId;

    private static ExecutionEvidenceValidationResult Invalid(string code, string path)
        => ExecutionEvidenceValidationResult.Failure(code, path);

    private static IEnumerable<ExecutionEvidenceArtifactReferenceV1> ArtifactReferences(ExecutionEvidenceEnvelopeV1 envelope)
        => envelope.Execution is { } execution
            ? execution.Nodes.SelectMany(n => n.Inputs.Select(i => i.Artifact).Concat(n.Outputs.Select(o => o.Artifact)))
                .Prepend(new(ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion, execution.PrimaryArtifactId, execution.CaptureId))
            : envelope.Availability?.Observations.Select(o => o.Artifact) ?? [];

    private async Task AddReferencesAsync(Guid bindingId, long unitId, ExecutionEvidenceArtifactReferenceV1[] references,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var records = references.Select((reference, ordinal) => new ImportedExecutionEvidenceArtifact
        {
            BindingId = bindingId, UnitId = unitId, Ordinal = ordinal,
            ArtifactId = reference.ArtifactId, CaptureId = reference.CaptureId,
            OutputIdentitySha256 = reference.OutputIdentitySha256, PayloadSha256 = reference.PayloadSha256,
            DescriptorSha256 = reference.DescriptorSha256, PayloadLength = reference.PayloadLength,
            MediaType = reference.MediaType, Role = reference.Role?.ToString(), Variant = reference.Variant
        }).ToArray();
        _db.AddRange(records);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.AddRange(records.Select(r => new ExecutionEvidenceArtifactCheck { ArtifactReferenceId = r.Id, NextCheckAtUtc = now }));
    }
}
