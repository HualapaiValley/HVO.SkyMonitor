using System.Data;
using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralTransientSubmissionService
{
    Task<CentralTransientSubmissionResult> SubmitAsync(
        DeviceRegistration registration,
        TransientCandidateSubmissionEnvelopeV1 envelope,
        string payloadSha256,
        CancellationToken cancellationToken);

    Task RecordRejectedAsync(
        DeviceRegistration registration,
        string payloadSha256,
        string reasonCode,
        Guid? candidateId,
        Guid? eventId,
        string? claimedSubmissionIdentitySha256,
        CancellationToken cancellationToken);
}

internal sealed record CentralTransientSubmissionResult(
    Guid CentralDerivativeJobId,
    TransientCandidateSubmissionAcknowledgementV1 Acknowledgement);

internal sealed class CentralTransientSubmissionRejectedException : Exception
{
    public CentralTransientSubmissionRejectedException()
        : this(CentralTransientSubmissionReasonCodes.InvalidContract, CentralTransientSubmissionRejectionKind.Conflict)
    {
    }

    public CentralTransientSubmissionRejectedException(string message)
        : base(message)
    {
        ReasonCode = CentralTransientSubmissionReasonCodes.InvalidContract;
    }

    public CentralTransientSubmissionRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = CentralTransientSubmissionReasonCodes.InvalidContract;
    }

    public CentralTransientSubmissionRejectedException(
        string reasonCode,
        CentralTransientSubmissionRejectionKind kind)
        : base("The Hybrid transient submission was rejected.")
    {
        ReasonCode = reasonCode;
        Kind = kind;
    }

    public string ReasonCode { get; }
    public CentralTransientSubmissionRejectionKind Kind { get; }
}

internal enum CentralTransientSubmissionRejectionKind
{
    Forbidden,
    NotFound,
    Conflict,
    Unavailable,
    Integrity,
    Timeout
}

internal sealed class CentralTransientSubmissionService(
    ApplicationDbContext dbContext,
    ICentralArtifactRetrievalService retrievalService,
    ICentralArtifactObjectReader objectReader,
    ICentralDerivativeRecipeCatalog recipeCatalog,
    ICentralDerivativeJobScheduler jobScheduler,
    IOptions<CentralTransientOptions> options,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider) : ICentralTransientSubmissionService
{
    private const int SubmittedSourceCount = 3;
    private const int CenteredSourceCount = 5;

    public async Task<CentralTransientSubmissionResult> SubmitAsync(
        DeviceRegistration registration,
        TransientCandidateSubmissionEnvelopeV1 envelope,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        var devicePublicId = registration.DevicePublicId
            ?? throw new CentralTransientSubmissionRejectedException(
                CentralTransientSubmissionReasonCodes.AuthenticationRejected,
                CentralTransientSubmissionRejectionKind.Forbidden);
        if (!string.Equals(envelope.Candidate.AgentId, registration.DeviceId, StringComparison.Ordinal))
        {
            await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.AuthenticationRejected,
                envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
        }
        var existing = await FindExistingAsync(
                envelope.SubmissionIdentitySha256, devicePublicId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return CreateDuplicate(existing, envelope);
        }
        if (options.Value.Mode != TransientDetectorExecutionMode.Hybrid)
        {
            await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.ModeDisabled,
                envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
        }

        var sourceReferences = envelope.Candidate.ContextSources;
        if (sourceReferences.Count != SubmittedSourceCount ||
            sourceReferences[2].EvidenceId != envelope.Candidate.CenterEvidenceId)
        {
            await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.InvalidWindow,
                envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
        }
        var sourceRole = sourceReferences[0].Locator.Artifact.Role;
        var recipe = recipeCatalog.GetTransientRecipe(sourceRole);
        if (recipe?.Transient is null || !string.Equals(
                recipe.RequestedRecipeIdentitySha256,
                envelope.RequestedRecipeIdentitySha256,
                StringComparison.Ordinal))
        {
            await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.RecipeIdentity,
                envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(
                envelope.Candidate.Extraction.RecipeIdentitySha256,
                envelope.RequestedRecipeIdentitySha256,
                StringComparison.Ordinal) || !string.Equals(
                envelope.Candidate.Provenance.ProcessingProfileIdentity,
                envelope.RequestedProcessingProfileIdentity,
                StringComparison.Ordinal))
        {
            await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.ProfileIdentity,
                envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
        }

        var verified = new List<VerifiedSource>(CenteredSourceCount);
        foreach (var sourceReference in sourceReferences)
        {
            var artifactReference = sourceReference.Locator.Artifact;
            var lookup = await retrievalService.FindForDeviceSubmissionAsync(
                devicePublicId, registration.DeviceId, artifactReference.ArtifactId, cancellationToken)
                .ConfigureAwait(false);
            if (lookup.Status == CentralArtifactLookupStatus.NotFound || lookup.Artifact is null)
            {
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceMissing,
                    envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
            }
            if (lookup.Status != CentralArtifactLookupStatus.Found)
            {
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceUnavailable,
                    envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
            }
            var artifact = lookup.Artifact!;
            var descriptor = CentralReconstructionDescriptorFactory.Create(artifact.Frame!, artifact);
            if (!SourceMatches(sourceReference, artifact, descriptor, envelope.RequestedProcessingProfileIdentity))
            {
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceConflict,
                    envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                var snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
                verified.Add(new VerifiedSource(artifact, snapshot,
                    CentralDerivativeWindowCompatibility.CreateSnapshot(descriptor)));
            }
            catch (CentralArtifactMissingException)
            {
                await retrievalService.MarkUnavailableAsync(
                    artifact, "hybrid-submission.object-missing", quarantine: false, CancellationToken.None)
                    .ConfigureAwait(false);
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceMissing,
                    envelope, existingJobId: null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (CentralArtifactIntegrityException)
            {
                await retrievalService.MarkUnavailableAsync(
                    artifact, "hybrid-submission.object-integrity", quarantine: true, CancellationToken.None)
                    .ConfigureAwait(false);
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceIntegrity,
                    envelope, existingJobId: null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (CentralArtifactStorageException)
            {
                telemetry.RecordDependencyFailure("storage", timeProvider.GetUtcNow());
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceTimeout,
                    envelope, existingJobId: null, CancellationToken.None).ConfigureAwait(false);
            }
        }
        await ResolveFutureSourcesAsync(
            registration, envelope, verified, payloadSha256, cancellationToken).ConfigureAwait(false);
        await ValidateWindowAsync(registration, envelope, verified, payloadSha256, cancellationToken)
            .ConfigureAwait(false);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await AcquireSubmissionLocksAsync(envelope, registration.DeviceId, cancellationToken).ConfigureAwait(false);
        existing = await FindExistingAsync(
                envelope.SubmissionIdentitySha256, devicePublicId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CreateDuplicate(existing, envelope);
        }
        var conflict = await dbContext.CentralTransientValidationIdentitySlots.AsNoTracking()
            .Where(slot => slot.CandidateId == envelope.CandidateId ||
                slot.AgentId == registration.DeviceId && slot.SubmittedEventId == envelope.EventId)
            .Select(slot => new { slot.CentralDerivativeJobId, slot.CandidateId, slot.SubmittedEventId })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            await AddAuditAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.IdentityConflict,
                envelope.CandidateId, envelope.EventId, envelope.SubmissionIdentitySha256,
                conflict.CentralDerivativeJobId, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordOperation("transient-submit", "rejected");
            throw new CentralTransientSubmissionRejectedException(
                CentralTransientSubmissionReasonCodes.IdentityConflict,
                CentralTransientSubmissionRejectionKind.Conflict);
        }

        foreach (var source in verified.OrderBy(item => item.Artifact.Id))
        {
            await CentralArtifactRetentionLock.AcquireAsync(dbContext, source.Artifact.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        var sourceIds = verified.Select(item => item.Artifact.Id).ToArray();
        var usable = await dbContext.CentralArtifacts.AsNoTracking().CountAsync(artifact =>
            sourceIds.Contains(artifact.Id) && artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete, cancellationToken)
            .ConfigureAwait(false);
        if (usable != CenteredSourceCount)
        {
            await AddAuditAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceUnavailable,
                envelope.CandidateId, envelope.EventId, envelope.SubmissionIdentitySha256,
                existingJobId: null, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordOperation("transient-submit", "rejected");
            throw new CentralTransientSubmissionRejectedException(
                CentralTransientSubmissionReasonCodes.EvidenceUnavailable,
                CentralTransientSubmissionRejectionKind.Unavailable);
        }
        foreach (var source in verified)
        {
            bool isCurrent;
            try
            {
                isCurrent = await objectReader.IsCurrentGenerationAsync(
                    source.Artifact, source.Snapshot.StorageETag, cancellationToken).ConfigureAwait(false);
            }
            catch (CentralArtifactStorageException)
            {
                telemetry.RecordDependencyFailure("storage", timeProvider.GetUtcNow());
                await AddAuditAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceTimeout,
                    envelope.CandidateId, envelope.EventId, envelope.SubmissionIdentitySha256,
                    existingJobId: null, cancellationToken).ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                telemetry.RecordOperation("transient-submit", "rejected");
                throw new CentralTransientSubmissionRejectedException(
                    CentralTransientSubmissionReasonCodes.EvidenceTimeout,
                    CentralTransientSubmissionRejectionKind.Timeout);
            }
            if (!isCurrent)
            {
                await AddAuditAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceConflict,
                    envelope.CandidateId, envelope.EventId, envelope.SubmissionIdentitySha256,
                    existingJobId: null, cancellationToken).ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                telemetry.RecordOperation("transient-submit", "rejected");
                throw new CentralTransientSubmissionRejectedException(
                    CentralTransientSubmissionReasonCodes.EvidenceConflict,
                    CentralTransientSubmissionRejectionKind.Conflict);
            }
        }

        var now = timeProvider.GetUtcNow();
        var job = jobScheduler.CreateHybridTransientJob(
            recipe!, verified.Select(item => item.Artifact).ToArray(), envelope, registration.DeviceId, now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordOperation("transient-submit", "accepted");
        return new CentralTransientSubmissionResult(job.Id, CreateAcknowledgement(
            envelope, now, TransientCandidateSubmissionDisposition.Accepted));
    }

    public async Task RecordRejectedAsync(
        DeviceRegistration registration,
        string payloadSha256,
        string reasonCode,
        Guid? candidateId,
        Guid? eventId,
        string? claimedSubmissionIdentitySha256,
        CancellationToken cancellationToken)
    {
        await AddAuditAsync(registration, payloadSha256, reasonCode, candidateId, eventId,
            IsSha256(claimedSubmissionIdentitySha256) ? claimedSubmissionIdentitySha256 : null,
            existingJobId: null, cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
        }
        telemetry.RecordOperation("transient-submit", "rejected");
    }

    private async Task RejectAsync(
        DeviceRegistration registration,
        string payloadSha256,
        string reasonCode,
        TransientCandidateSubmissionEnvelopeV1 envelope,
        Guid? existingJobId,
        CancellationToken cancellationToken)
    {
        await AddAuditAsync(registration, payloadSha256, reasonCode, envelope.CandidateId, envelope.EventId,
            envelope.SubmissionIdentitySha256, existingJobId, cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
        }
        telemetry.RecordOperation("transient-submit", "rejected");
        throw new CentralTransientSubmissionRejectedException(reasonCode, KindFor(reasonCode));
    }

    private async Task ValidateWindowAsync(
        DeviceRegistration registration,
        TransientCandidateSubmissionEnvelopeV1 envelope,
        IReadOnlyList<VerifiedSource> sources,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        var centerSequence = sources[2].Artifact.Frame!.CaptureSequence;
        var valid = centerSequence.HasValue
            && sources.All(source => source.Artifact.Role == sources[0].Artifact.Role)
            && sources.All(source => string.Equals(
                source.Compatibility.Sha256, sources[0].Compatibility.Sha256, StringComparison.Ordinal))
            && sources.Select(source => source.Artifact.Frame!.RigId).Distinct(StringComparer.Ordinal).Count() == 1
            && sources.Select((source, ordinal) => source.Artifact.Frame!.CaptureSequence == centerSequence + ordinal - 2)
                .All(result => result);
        if (!valid)
        {
            await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.InvalidWindow,
                envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private async Task ResolveFutureSourcesAsync(
        DeviceRegistration registration,
        TransientCandidateSubmissionEnvelopeV1 envelope,
        List<VerifiedSource> verified,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        if (verified.Count != SubmittedSourceCount || verified[2].Artifact.Frame?.CaptureSequence is not { } centerSequence)
        {
            await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.InvalidWindow,
                envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
            return;
        }
        var center = verified[2];
        var devicePublicId = registration.DevicePublicId!.Value;
        for (var offset = 1; offset <= 2; offset++)
        {
            long expectedSequence;
            try
            {
                expectedSequence = checked(centerSequence + offset);
            }
            catch (OverflowException)
            {
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.InvalidWindow,
                    envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
                return;
            }
            var candidates = await dbContext.CentralArtifacts
                .Include(item => item.Layout)
                .Include(item => item.Recipe)
                .Include(item => item.Sources)
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Timing)
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Control)
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Profiles)
                .Where(item => item.DevicePublicId == devicePublicId && item.Frame!.AgentId == registration.DeviceId &&
                    item.Frame.CaptureSequence == expectedSequence && item.Frame.RigId == center.Artifact.Frame!.RigId &&
                    item.Role == center.Artifact.Role)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var compatible = candidates.Select(item => new
            {
                Artifact = item,
                Descriptor = CentralReconstructionDescriptorFactory.Create(item.Frame!, item),
                Compatibility = CentralDerivativeWindowCompatibility.CreateSnapshot(
                        CentralReconstructionDescriptorFactory.Create(item.Frame!, item))
            })
                .Where(item => string.Equals(item.Compatibility.Sha256, center.Compatibility.Sha256, StringComparison.Ordinal))
                .ToArray();
            if (compatible.Length == 0)
            {
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceMissing,
                    envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
            }
            if (compatible.Length != 1)
            {
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceConflict,
                    envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
            }
            var selected = compatible[0];
            if (selected.Artifact.ObjectState != CentralArtifactObjectState.Available ||
                selected.Artifact.ReconstructionState != CentralReconstructionState.Complete)
            {
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceUnavailable,
                    envelope, existingJobId: null, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                var snapshot = await objectReader.VerifyAsync(selected.Artifact, cancellationToken).ConfigureAwait(false);
                verified.Add(new VerifiedSource(selected.Artifact, snapshot, selected.Compatibility));
            }
            catch (CentralArtifactMissingException)
            {
                await retrievalService.MarkUnavailableAsync(
                    selected.Artifact, "hybrid-submission.object-missing", quarantine: false, CancellationToken.None)
                    .ConfigureAwait(false);
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceMissing,
                    envelope, existingJobId: null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (CentralArtifactIntegrityException)
            {
                await retrievalService.MarkUnavailableAsync(
                    selected.Artifact, "hybrid-submission.object-integrity", quarantine: true, CancellationToken.None)
                    .ConfigureAwait(false);
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceIntegrity,
                    envelope, existingJobId: null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (CentralArtifactStorageException)
            {
                telemetry.RecordDependencyFailure("storage", timeProvider.GetUtcNow());
                await RejectAsync(registration, payloadSha256, CentralTransientSubmissionReasonCodes.EvidenceTimeout,
                    envelope, existingJobId: null, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<CentralTransientValidationJob?> FindExistingAsync(
        string submissionIdentitySha256,
        Guid devicePublicId,
        CancellationToken cancellationToken)
        => await dbContext.CentralTransientValidationJobs.AsNoTracking()
            .Include(validation => validation.IdentitySlots)
            .Include(validation => validation.Job)!.ThenInclude(job => job!.SourceArtifact)
            .SingleOrDefaultAsync(validation =>
                validation.SubmissionIdentitySha256 == submissionIdentitySha256
                && validation.Job!.SourceArtifact!.DevicePublicId == devicePublicId, cancellationToken)
            .ConfigureAwait(false);

    private CentralTransientSubmissionResult CreateDuplicate(
        CentralTransientValidationJob validation,
        TransientCandidateSubmissionEnvelopeV1 envelope)
    {
        var slot = validation.IdentitySlots.SingleOrDefault(candidate => candidate.Ordinal == 0);
        if (!string.Equals(validation.AgentId, envelope.Candidate.AgentId, StringComparison.Ordinal)
            || slot?.CandidateId != envelope.CandidateId || slot.SubmittedEventId != envelope.EventId)
        {
            throw new CentralTransientSubmissionRejectedException(
                CentralTransientSubmissionReasonCodes.IdentityConflict,
                CentralTransientSubmissionRejectionKind.Conflict);
        }
        telemetry.RecordOperation("transient-submit", "duplicate");
        return new CentralTransientSubmissionResult(validation.CentralDerivativeJobId, CreateAcknowledgement(
            envelope, validation.CreatedAtUtc, TransientCandidateSubmissionDisposition.Duplicate));
    }

    private async Task AcquireSubmissionLocksAsync(
        TransientCandidateSubmissionEnvelopeV1 envelope,
        string authenticatedAgentId,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return;
        }
        foreach (var resource in new[]
                 {
                     $"hybrid-submission:{envelope.SubmissionIdentitySha256}",
                     $"hybrid-candidate:{envelope.CandidateId:N}",
                     $"hybrid-event:{authenticatedAgentId}:{envelope.EventId:N}"
                 }.Order(StringComparer.Ordinal))
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = {resource},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 10000;
                IF @result < 0
                    THROW 51011, 'Could not acquire the Hybrid transient submission lock.', 1;
                """, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AddAuditAsync(
        DeviceRegistration registration,
        string payloadSha256,
        string reasonCode,
        Guid? candidateId,
        Guid? eventId,
        string? claimedSubmissionIdentitySha256,
        Guid? existingJobId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dbContext.CentralTransientSubmissionAudits.Local.Any(audit =>
                audit.DevicePublicId == registration.DevicePublicId
                && audit.PayloadSha256 == payloadSha256
                && audit.ReasonCode == reasonCode)
            || await dbContext.CentralTransientSubmissionAudits.AsNoTracking().AnyAsync(audit =>
                audit.DevicePublicId == registration.DevicePublicId
                && audit.PayloadSha256 == payloadSha256
                && audit.ReasonCode == reasonCode, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        dbContext.CentralTransientSubmissionAudits.Add(new CentralTransientSubmissionAudit
        {
            DevicePublicId = registration.DevicePublicId ?? Guid.Empty,
            AgentId = registration.DeviceId,
            CandidateId = candidateId,
            EventId = eventId,
            ClaimedSubmissionIdentitySha256 = claimedSubmissionIdentitySha256,
            PayloadSha256 = payloadSha256,
            ReasonCode = reasonCode,
            ExistingCentralDerivativeJobId = existingJobId,
            RecordedAtUtc = timeProvider.GetUtcNow()
        });
    }

    private static bool SourceMatches(
        TransientSourceEvidenceReferenceV1 source,
        CentralArtifact artifact,
        ReconstructionDescriptor descriptor,
        string processingProfileIdentity)
    {
        var reference = source.Locator.Artifact;
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256;
        return reference.Role is FrameArtifactRole.Raw or FrameArtifactRole.Calibrated
            && reference.ArtifactId == artifact.ArtifactId
            && reference.Role == artifact.Role
            && string.Equals(reference.Variant, artifact.Variant ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(reference.ChecksumSha256, artifact.ChecksumSha256, StringComparison.Ordinal)
            && string.Equals(reference.RecipeIdentitySha256, recipeIdentity, StringComparison.Ordinal)
            && string.Equals(descriptor.Profiles.Processing.Sha256, processingProfileIdentity, StringComparison.Ordinal)
            && source.ObservationStartedUtc == descriptor.Timing.ExposureStartedUtc
            && source.ObservationEndedUtc == descriptor.Timing.ExposureEndedUtc;
    }

    private static TransientCandidateSubmissionAcknowledgementV1 CreateAcknowledgement(
        TransientCandidateSubmissionEnvelopeV1 envelope,
        DateTimeOffset receivedAtUtc,
        TransientCandidateSubmissionDisposition disposition)
        => new(
            TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
            envelope.CandidateId,
            envelope.EventId,
            envelope.SubmissionIdentitySha256,
            receivedAtUtc,
            disposition);

    private static CentralTransientSubmissionRejectionKind KindFor(string reasonCode) => reasonCode switch
    {
        CentralTransientSubmissionReasonCodes.EvidenceMissing => CentralTransientSubmissionRejectionKind.NotFound,
        CentralTransientSubmissionReasonCodes.EvidenceUnavailable => CentralTransientSubmissionRejectionKind.Unavailable,
        CentralTransientSubmissionReasonCodes.EvidenceIntegrity => CentralTransientSubmissionRejectionKind.Integrity,
        CentralTransientSubmissionReasonCodes.EvidenceTimeout => CentralTransientSubmissionRejectionKind.Timeout,
        CentralTransientSubmissionReasonCodes.AuthenticationRejected => CentralTransientSubmissionRejectionKind.Forbidden,
        _ => CentralTransientSubmissionRejectionKind.Conflict
    };

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is SqlException { Number: 2601 or 2627 };

    private sealed record VerifiedSource(
        CentralArtifact Artifact,
        CentralArtifactObjectSnapshot Snapshot,
        CentralDerivativeCompatibilitySnapshot Compatibility);
}

internal static class CentralTransientSubmissionReasonCodes
{
    public const string AuthenticationRejected = "hybrid-submission.authentication-rejected";
    public const string ModeDisabled = "hybrid-submission.mode-disabled";
    public const string RecipeIdentity = "hybrid-submission.recipe-identity";
    public const string ProfileIdentity = "hybrid-submission.profile-identity";
    public const string InvalidWindow = "hybrid-submission.invalid-window";
    public const string EvidenceMissing = "hybrid-submission.evidence-missing";
    public const string EvidenceUnavailable = "hybrid-submission.evidence-unavailable";
    public const string EvidenceConflict = "hybrid-submission.evidence-conflict";
    public const string EvidenceIntegrity = "hybrid-submission.evidence-integrity";
    public const string EvidenceTimeout = "hybrid-submission.evidence-timeout";
    public const string IdentityConflict = "hybrid-submission.identity-conflict";
    public const string InvalidContract = "hybrid-submission.invalid-contract";
    public const string PayloadTooLarge = "hybrid-submission.payload-too-large";
}
