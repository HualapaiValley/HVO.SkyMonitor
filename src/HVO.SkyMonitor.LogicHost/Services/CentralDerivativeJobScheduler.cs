using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeJobScheduler
{
    Task EnsureRequiredJobsAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken);

    Task EnsureRequiredJobsAsync(
        Guid devicePublicId,
        Guid artifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task<Guid?> EnsureTransientContextConvergenceAsync(
        Guid provisionalJobId,
        DateTimeOffset now,
        CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    CentralDerivativeJob CreateHybridTransientJob(
        CentralDerivativeRecipe recipe,
        IReadOnlyList<CentralArtifact> orderedSources,
        TransientCandidateSubmissionEnvelopeV1 envelope,
        string authenticatedAgentId,
        DateTimeOffset now) => throw new NotSupportedException(
            "This scheduler does not support Hybrid transient submissions.");
}

internal sealed class CentralDerivativeJobScheduler(
    ApplicationDbContext dbContext,
    ICentralDerivativeRecipeCatalog recipeCatalog,
    ICentralDerivativeWindowResolver windowResolver,
    IEnvironmentalObservationQueryService? environmentalQuery = null,
    ICentralProcessingPolicyService? processingPolicy = null) : ICentralDerivativeJobScheduler
{
    internal const string SourceInvalidatedReason = "The derivative source artifact is not usable.";
    internal const string ResultInvalidatedReason = "The derivative result artifact is not usable.";
    internal const string LegacySourceSkippedReason = "The legacy derivative source is not reconstructable.";
    internal const string LocationUnresolvedReason = "location.reported-unresolved";
    internal const string LocationMismatchReason = "location.mismatch";
    private static readonly EnvironmentalObservationSourceKind[] EnvironmentalSourcePriority =
        Enum.GetValues<EnvironmentalObservationSourceKind>();
    private static readonly EnvironmentalObservationQuality[] EnvironmentalQualities =
        Enum.GetValues<EnvironmentalObservationQuality>();

    public async Task EnsureRequiredJobsAsync(
        Guid devicePublicId,
        Guid artifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var artifact = await dbContext.CentralArtifacts
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Location)
            .SingleAsync(item => item.DevicePublicId == devicePublicId && item.ArtifactId == artifactId,
                cancellationToken).ConfigureAwait(false);
        await EnsureRequiredJobsAsync(artifact, now, cancellationToken).ConfigureAwait(false);
    }

    public CentralDerivativeJob CreateHybridTransientJob(
        CentralDerivativeRecipe recipe,
        IReadOnlyList<CentralArtifact> orderedSources,
        TransientCandidateSubmissionEnvelopeV1 envelope,
        string authenticatedAgentId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(orderedSources);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedAgentId);
        if (recipe.Transient is null || recipe.Window?.Positions.Count != 5 || orderedSources.Count != 5)
        {
            throw new CentralDerivativeJobStateException("A Hybrid transient submission requires one exact five-source recipe window.");
        }
        var center = orderedSources[2];
        var job = CreateJob(center, recipe, result: null, now);
        job.RequestIdentitySha256 = CentralDerivativeJobIdentity.CreateHybridSubmissionRequestIdentity(
            center.DevicePublicId ?? throw new CentralDerivativeJobStateException(
                "The Hybrid transient center source has no device identity."),
            center.ArtifactId,
            envelope.SubmissionIdentitySha256,
            recipe);
        job.Status = CentralDerivativeJobStatus.Pending;
        job.AvailableAtUtc = now;
        job.ResolutionCompletedAtUtc = now;
        job.StateReasonCode = null;
        job.LastError = null;

        var requirements = job.InputRequirements.OrderBy(requirement => requirement.Ordinal).ToArray();
        for (var ordinal = 0; ordinal < orderedSources.Count; ordinal++)
        {
            var artifact = orderedSources[ordinal];
            var requirement = requirements[ordinal];
            var compatibility = CentralDerivativeWindowCompatibility.CreateSnapshot(artifact);
            requirement.ExpectedCentralArtifactId = artifact.Id;
            requirement.ResolutionState = CentralDerivativeInputResolutionState.Resolved;
            requirement.ResolutionReasonCode = null;
            requirement.ResolvedAtUtc = now;
            job.Inputs.Add(new CentralDerivativeJobInput
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Requirement = requirement,
                CentralDerivativeJobInputRequirementId = requirement.Id,
                Ordinal = ordinal,
                CentralArtifactId = artifact.Id,
                Artifact = artifact,
                CaptureSequence = artifact.Frame!.CaptureSequence,
                CompatibilityJson = compatibility.Json,
                CompatibilitySha256 = compatibility.Sha256,
                ByteLength = artifact.ByteLength,
                SelectedAtUtc = now
            });
        }
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        var validation = dbContext.CentralTransientValidationJobs.Local.Single(candidate =>
            candidate.CentralDerivativeJobId == job.Id);
        validation.AgentId = authenticatedAgentId;
        validation.SubmissionSchemaVersion = envelope.SchemaVersion;
        validation.SubmissionIdentitySha256 = envelope.SubmissionIdentitySha256;
        validation.SubmittedCandidateJson = System.Text.Encoding.UTF8.GetString(
            TransientContractJson.Serialize(envelope.Candidate));
        var submittedSlot = validation.IdentitySlots.Single(slot => slot.Ordinal == 0);
        submittedSlot.SubmittedEventId = envelope.EventId;
        submittedSlot.CandidateId = envelope.CandidateId;
        dbContext.CentralDerivativeJobs.Add(job);
        return job;
    }

    public async Task<Guid?> EnsureTransientContextConvergenceAsync(
        Guid provisionalJobId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, provisionalJobId, cancellationToken)
            .ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        var provisional = await dbContext.CentralTransientValidationJobs
            .Include(item => item.ContextDependencies)
            .Include(item => item.Job)!.ThenInclude(item => item!.SourceArtifact)!.ThenInclude(item => item!.Frame)
            .SingleOrDefaultAsync(item => item.CentralDerivativeJobId == provisionalJobId, cancellationToken)
            .ConfigureAwait(false);
        if (provisional?.Job?.SourceArtifact is not { } source || provisional.OutcomeRecordedAtUtc is null ||
            provisional.ContextDependencies.Count == 0 ||
            await dbContext.CentralTransientValidationJobs.AnyAsync(item =>
                item.ProvisionalCentralDerivativeJobId == provisionalJobId, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        foreach (var dependency in provisional.ContextDependencies.Where(item =>
                     item.RequiredCentralDerivativeJobId == null))
        {
            dependency.RequiredCentralDerivativeJobId = await dbContext.CentralDerivativeJobs
                .Where(job => job.SourceCentralArtifactId == dependency.ContextCentralArtifactId &&
                    job.RecipeName == CentralTransientRuntime.RecipeName &&
                    job.RequestedRecipeIdentitySha256 == dependency.RequestedRecipeIdentitySha256 &&
                    dbContext.CentralTransientValidationJobs.Any(validation =>
                        validation.CentralDerivativeJobId == job.Id &&
                        validation.ProvisionalCentralDerivativeJobId == null &&
                        validation.ExecutionOptionsIdentitySha256 == dependency.ExecutionOptionsIdentitySha256))
                .Select(job => (Guid?)job.Id)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        var requiredJobIds = provisional.ContextDependencies
            .Where(item => item.RequiredCentralDerivativeJobId.HasValue)
            .Select(item => item.RequiredCentralDerivativeJobId!.Value)
            .ToArray();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var settledDependencyCount = await dbContext.CentralTransientContextDependencies.AsNoTracking()
            .CountAsync(item => item.CentralDerivativeJobId == provisionalJobId &&
                item.RequiredCentralDerivativeJobId != null &&
                item.RequiredValidationJob!.CommittedAtUtc != null &&
                item.RequiredValidationJob.ExtractionReceipt != null &&
                item.RequiredValidationJob.ExecutionOptionsIdentitySha256 == item.ExecutionOptionsIdentitySha256,
                cancellationToken).ConfigureAwait(false);
        if (requiredJobIds.Length != provisional.ContextDependencies.Count ||
            settledDependencyCount != provisional.ContextDependencies.Count)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var recipe = recipeCatalog.GetRequiredRecipes(source.Role).Single(item =>
            string.Equals(item.RecipeName, CentralTransientRuntime.RecipeName, StringComparison.Ordinal) &&
            string.Equals(item.RequestedRecipeIdentitySha256, provisional.Job.RequestedRecipeIdentitySha256,
                StringComparison.Ordinal));
        var successor = CreateJob(source, recipe, result: null, now)
            ?? throw new CentralDerivativeJobStateException("The transient convergence job could not be created.");
        successor.RequestIdentitySha256 = CentralDerivativeJobIdentity.CreateReprocessRequestIdentity(
            provisionalJobId,
            source.DevicePublicId ?? throw new CentralDerivativeJobStateException(
                "The transient convergence source has no device identity."),
            source.ArtifactId,
            recipe);
        var successorValidation = dbContext.CentralTransientValidationJobs.Local.Single(item =>
            item.CentralDerivativeJobId == successor.Id);
        successorValidation.ProvisionalCentralDerivativeJobId = provisionalJobId;
        successorValidation.SubmissionIdentitySha256 = CreateTransientSubmissionIdentity(successor, successorValidation);
        dbContext.CentralDerivativeJobs.Add(successor);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        await windowResolver.ResolveAsync(successor.Id, now, cancellationToken).ConfigureAwait(false);
        return successor.Id;
    }

    public async Task EnsureRequiredJobsAsync(
        CentralArtifact artifact,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (dbContext.Database.CurrentTransaction is null)
            {
                ownedTransaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            }
            if (ownedTransaction is not null)
            {
                await CentralArtifactRetentionLock.AcquireAsync(dbContext, artifact.Id, cancellationToken)
                    .ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (!await dbContext.CentralArtifacts.AsNoTracking().AnyAsync(candidate =>
                    candidate.Id == artifact.Id
                    && candidate.ObjectState == CentralArtifactObjectState.Available
                    && candidate.ReconstructionState == CentralReconstructionState.Complete, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await ownedTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            await EnsureRequiredJobsCoreAsync(artifact, now, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
                ownedTransaction = null;
                if (artifact.Role == FrameArtifactRole.Raw && artifact.Frame?.CaptureSequence is not null)
                {
                    await windowResolver.ResolveAffectedAsync(artifact, now, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureRequiredJobsCoreAsync(
        CentralArtifact artifact,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var frame = artifact.Frame ?? throw new InvalidOperationException("The artifact frame must be loaded before scheduling derivatives.");
        if (artifact.ObjectState != CentralArtifactObjectState.Available
            || artifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            return;
        }
        var sourceRecipes = processingPolicy is null
            ? recipeCatalog.GetRequiredRecipes(artifact.Role)
            : await processingPolicy.ResolveRequiredRecipesAsync(
                frame.ObservatoryId, artifact.Role, cancellationToken).ConfigureAwait(false);
        if (artifact.Role == FrameArtifactRole.Raw ||
            artifact.Role == FrameArtifactRole.Calibrated && sourceRecipes.Count > 0)
        {
            foreach (var recipe in sourceRecipes)
            {
                if (recipe.Window is not null && frame.CaptureSequence is null)
                {
                    continue;
                }
                var requestIdentity = CentralDerivativeJobIdentity.CreateRequestIdentity(
                    frame.DevicePublicId, artifact.ArtifactId, recipe);
                var existing = dbContext.CentralDerivativeJobs.Local.FirstOrDefault(job =>
                    job.RequestIdentitySha256 == requestIdentity)
                    ?? await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(job =>
                        job.RequestIdentitySha256 == requestIdentity,
                        cancellationToken).ConfigureAwait(false);
                var isCloudAssessment = string.Equals(
                    recipe.RecipeName, BuiltInProcessingRecipes.CloudAssessment, StringComparison.Ordinal);
                var requiresResolvedLocation = string.Equals(
                    recipe.RecipeName, BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal);
                if (requiresResolvedLocation
                    && frame.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedResolved
                    && existing is not null
                    && IsLocationResolutionFailure(existing)
                    && existing.ResultCentralArtifactId is { } retainedResultId
                    && frame.Artifacts.FirstOrDefault(candidate => candidate.Id == retainedResultId) is
                    {
                        ObjectState: CentralArtifactObjectState.Available,
                        ReconstructionState: CentralReconstructionState.Quarantined
                    } retainedResult
                    && (retainedResult.StateReasonCode == LocationUnresolvedReason
                        || retainedResult.StateReasonCode == LocationMismatchReason)
                    && await IsCanonicalTargetAsync(artifact, retainedResult, recipe, cancellationToken)
                        .ConfigureAwait(false))
                {
                    retainedResult.StateReasonCode = null;
                    retainedResult.ReconstructionState = CentralReconstructionState.Complete;
                    Complete(existing, retainedResult, now);
                    continue;
                }
                var target = recipe.Window is null && !isCloudAssessment ? frame.Artifacts.FirstOrDefault(candidate =>
                    candidate.ManifestSchemaVersion == ArtifactUploadManifest.CurrentSchemaVersion
                    && candidate.Role == recipe.TargetRole
                    && candidate.RecipeVersion == recipe.RecipeVersion
                    && (candidate.Variant ?? string.Empty) == recipe.TargetVariant
                    && IsUsable(candidate)) : null;
                if (target is not null
                    && !await IsCanonicalTargetAsync(artifact, target, recipe, cancellationToken).ConfigureAwait(false))
                {
                    target = null;
                }
                if (requiresResolvedLocation
                    && frame.LocationEvidenceState is CentralCaptureLocationEvidenceState.ReportedUnresolved
                        or CentralCaptureLocationEvidenceState.Mismatch)
                {
                    if (existing is null)
                    {
                        existing = CreateJob(artifact, recipe, result: null, now);
                        dbContext.CentralDerivativeJobs.Add(existing);
                    }
                    var completedResult = existing.ResultCentralArtifactId is { } resultId
                        ? frame.Artifacts.FirstOrDefault(candidate => candidate.Id == resultId)
                        : null;
                    await QuarantineForLocationAsync(
                        existing,
                        completedResult ?? target,
                        frame.LocationEvidenceState,
                        now,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (existing is null)
                {
                    var job = isCloudAssessment
                        ? await CreateCloudAssessmentJobAsync(artifact, recipe, now, cancellationToken).ConfigureAwait(false)
                        : CreateJob(artifact, recipe, target, now);
                    if (job is not null)
                    {
                        dbContext.CentralDerivativeJobs.Add(job);
                    }
                }
                else if (target is not null
                    && (CanComplete(existing) || IsLocationResolutionFailure(existing))
                    && !IsInvalidationFailure(existing))
                {
                    if (IsLocationResolutionFailure(existing))
                    {
                        target.StateReasonCode = null;
                        target.ReconstructionState = CentralReconstructionState.Complete;
                    }
                    Complete(existing, target, now);
                }
                else
                {
                    Restore(existing, artifact, now);
                }
            }
            if (artifact.Role == FrameArtifactRole.Raw)
            {
                await EnsureWeatherCloudOverlayJobAsync(frame, now, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var sources = frame.Artifacts.Where(candidate => candidate.Role == FrameArtifactRole.Raw && IsUsable(candidate)).ToArray();
        await EnsureWeatherCloudOverlayJobAsync(frame, now, cancellationToken).ConfigureAwait(false);
        if (artifact.ManifestSchemaVersion != ArtifactUploadManifest.CurrentSchemaVersion)
        {
            return;
        }
        foreach (var source in sources)
        {
            var sourceRecipesForObservatory = processingPolicy is null
                ? recipeCatalog.GetRequiredRecipes(source.Role)
                : await processingPolicy.ResolveRequiredRecipesAsync(
                    frame.ObservatoryId, source.Role, cancellationToken).ConfigureAwait(false);
            var recipe = sourceRecipesForObservatory.FirstOrDefault(candidate =>
                candidate.RecipeName != BuiltInProcessingRecipes.CloudAssessment
                &&
                candidate.TargetRole == artifact.Role
                && candidate.RecipeVersion == artifact.RecipeVersion
                && candidate.TargetVariant == (artifact.Variant ?? string.Empty));
            if (recipe is null)
            {
                continue;
            }
            var canonicalTarget = await IsCanonicalTargetAsync(source, artifact, recipe, cancellationToken)
                .ConfigureAwait(false);
            var requestIdentity = CentralDerivativeJobIdentity.CreateRequestIdentity(
                frame.DevicePublicId, source.ArtifactId, recipe);
            var job = dbContext.CentralDerivativeJobs.Local.FirstOrDefault(candidate =>
                candidate.RequestIdentitySha256 == requestIdentity)
                ?? await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(candidate =>
                    candidate.RequestIdentitySha256 == requestIdentity,
                    cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                job = CreateJob(source, recipe, canonicalTarget ? artifact : null, now);
                dbContext.CentralDerivativeJobs.Add(job);
            }
            else if (IsInvalidationFailure(job))
            {
                Restore(job, source, now);
            }
            else if (canonicalTarget && CanComplete(job))
            {
                Complete(job, artifact, now);
            }
        }
    }

    private CentralDerivativeJob CreateJob(
        CentralArtifact source,
        CentralDerivativeRecipe recipe,
        CentralArtifact? result,
        DateTimeOffset now)
    {
        var frame = source.Frame!;
        var isWaiting = result is null && recipe.Window is not null;
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            SourceArtifact = source,
            TargetRole = recipe.TargetRole,
            TargetRecipeVersion = recipe.RecipeVersion,
            TargetVariant = recipe.TargetVariant,
            RecipeName = recipe.RecipeName,
            RecipeOptionsJson = CaptureContractJson.Canonicalize(recipe.Options).GetRawText(),
            InputSelectorJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(recipe.InputSelector)).GetRawText(),
            RequestedRecipeIdentitySha256 = recipe.RequestedRecipeIdentitySha256,
            ExpectedRecipeIdentitySha256 = recipe.RequestedRecipeIdentitySha256,
            RequestIdentitySha256 = CentralDerivativeJobIdentity.CreateRequestIdentity(
                source.Frame!.DevicePublicId, source.ArtifactId, recipe),
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString,
            Status = result is null
                ? isWaiting ? CentralDerivativeJobStatus.Waiting : CentralDerivativeJobStatus.Pending
                : CentralDerivativeJobStatus.Completed,
            AttemptCount = 0,
            MaxAttempts = recipe.MaxAttempts,
            AvailableAtUtc = result is null && !isWaiting && IsUsable(source) ? now : null,
            ResolutionDeadlineUtc = isWaiting ? now + recipe.Window!.Timeout : null,
            ResolutionStartedAtUtc = isWaiting ? now : null,
            ResolutionCompletedAtUtc = isWaiting ? null : now,
            MissingInputOutcome = recipe.Window?.MissingInputOutcome,
            StateReasonCode = isWaiting ? CentralDerivativeWindowReasonCodes.WaitingRequiredInput : null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CompletedAtUtc = result is null ? null : now,
            ResultCentralArtifactId = result?.Id,
            ResultArtifact = result
        };
        var positions = recipe.Window?.Positions
            ?? [new CentralDerivativeWindowPosition(0, IsRequired: true, recipe.InputSelector)];
        foreach (var position in CentralDerivativeJobIdentity.OrderWindowPositions(positions))
        {
            var requirement = new CentralDerivativeJobInputRequirement
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Ordinal = job.InputRequirements.Count,
                BindingName = "input",
                SourceKind = CentralDerivativeInputSourceKind.Artifact,
                SequenceOffset = position.SequenceOffset,
                IsRequired = position.IsRequired,
                SelectorJson = CaptureContractJson.Canonicalize(
                    CaptureContractJson.SerializeToElement(position.Selector)).GetRawText(),
                CompatibilityMode = position.CompatibilityMode,
                ExpectedAgentId = frame.AgentId,
                ExpectedRigId = frame.RigId,
                ExpectedCaptureSequence = AddSequenceOffset(frame.CaptureSequence, position.SequenceOffset),
                ExpectedCentralArtifactId = recipe.Window is null ? source.Id : null,
                ResolutionState = recipe.Window is null
                    ? CentralDerivativeInputResolutionState.Resolved
                    : CentralDerivativeInputResolutionState.Waiting,
                ResolvedAtUtc = recipe.Window is null ? now : null
            };
            job.InputRequirements.Add(requirement);
            if (recipe.Window is null)
            {
                var snapshot = CentralDerivativeWindowCompatibility.EmptySnapshot;
                job.Inputs.Add(new CentralDerivativeJobInput
                {
                    Job = job,
                    CentralDerivativeJobId = job.Id,
                    Requirement = requirement,
                    CentralDerivativeJobInputRequirementId = requirement.Id,
                    Ordinal = requirement.Ordinal,
                    CentralArtifactId = source.Id,
                    Artifact = source,
                    CaptureSequence = frame.CaptureSequence,
                    CompatibilityJson = snapshot.Json,
                    CompatibilitySha256 = snapshot.Sha256,
                    ByteLength = source.ByteLength,
                    SelectedAtUtc = now
                });
            }
        }
        if (recipe.Transient is { } transient)
        {
            AddTransientRuntimeState(job, recipe, transient, frame, now);
        }
        if (recipe.Window is null)
        {
            job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        }
        return job;
    }

    private void AddTransientRuntimeState(
        CentralDerivativeJob job,
        CentralDerivativeRecipe recipe,
        CentralTransientRecipeDefinition transient,
        CentralFrame frame,
        DateTimeOffset now)
    {
        var extractionOptions = CentralTransientExecutionOptionsJson.Deserialize(transient.ExecutionOptionsJson).Extraction;
        var canonicalElement = CaptureContractJson.SerializeToElement(new
        {
            schema = CentralTransientRuntime.ExtractionOptionsSchemaVersion,
            options = extractionOptions
        });
        var canonicalJson = CaptureContractJson.Canonicalize(canonicalElement).GetRawText();
        var canonicalBytes = Encoding.UTF8.GetBytes(canonicalJson);
        var canonicalIdentity = ProcessingIdentity.ComputePayloadSha256(canonicalBytes);
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = job.InputRequirements.Count,
            BindingName = "transient-extraction-options",
            SourceKind = CentralDerivativeInputSourceKind.Canonical,
            IsRequired = true,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = frame.AgentId,
            ExpectedRigId = frame.RigId,
            ExpectedCaptureSequence = frame.CaptureSequence,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = now
        };
        job.InputRequirements.Add(requirement);
        job.CanonicalInputs.Add(new CentralDerivativeJobCanonicalInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = requirement.Ordinal,
            SchemaVersion = CentralTransientRuntime.ExtractionOptionsSchemaVersion,
            IdentitySha256 = canonicalIdentity,
            CanonicalJson = canonicalJson,
            ByteLength = canonicalBytes.Length,
            SelectedAtUtc = now
        });

        var validation = new CentralTransientValidationJob
        {
            CentralDerivativeJobId = job.Id,
            Job = job,
            AgentId = frame.AgentId,
            SubmissionSchemaVersion = CentralTransientRuntime.SubmissionSchemaVersion,
            ExecutionOptionsJson = transient.ExecutionOptionsJson,
            ExecutionOptionsIdentitySha256 = transient.ExecutionOptionsIdentitySha256,
            CreatedAtUtc = now
        };
        for (var ordinal = 0; ordinal < transient.IdentitySlotCount; ordinal++)
        {
            validation.IdentitySlots.Add(new CentralTransientValidationIdentitySlot
            {
                CentralDerivativeJobId = job.Id,
                ValidationJob = validation,
                Ordinal = ordinal,
                State = CentralTransientValidationIdentitySlotState.Reserved,
                AgentId = frame.AgentId,
                SubmittedEventId = Guid.NewGuid(),
                CandidateId = Guid.NewGuid(),
                ObservationId = Guid.NewGuid(),
                AssessmentId = Guid.NewGuid()
            });
        }
        validation.SubmissionIdentitySha256 = CreateTransientSubmissionIdentity(job, validation);
        dbContext.CentralTransientValidationJobs.Add(validation);
    }

    private static string CreateTransientSubmissionIdentity(
        CentralDerivativeJob job,
        CentralTransientValidationJob validation)
    {
        var value = string.Join('\n',
            validation.SubmissionSchemaVersion,
            job.RequestIdentitySha256,
            validation.ExecutionOptionsIdentitySha256,
            string.Join('\n', validation.IdentitySlots.OrderBy(item => item.Ordinal).Select(item => string.Join(':',
                item.Ordinal,
                item.SubmittedEventId.ToString("N"),
                item.CandidateId.ToString("N"),
                item.ObservationId.ToString("N"),
                item.AssessmentId.ToString("N")))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private async Task<CentralDerivativeJob?> CreateCloudAssessmentJobAsync(
        CentralArtifact source,
        CentralDerivativeRecipe recipe,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var frame = source.Frame!;
        if (string.IsNullOrWhiteSpace(frame.RigId) || environmentalQuery is null)
        {
            return null;
        }
        var designation = await dbContext.CentralClearReferenceDesignations
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.Frame)
            .SingleOrDefaultAsync(item => item.RegistrationId == frame.RegistrationId
                && item.RigId == frame.RigId
                && item.Artifact!.ObjectState == CentralArtifactObjectState.Available
                && item.Artifact.ReconstructionState == CentralReconstructionState.Complete,
                cancellationToken).ConfigureAwait(false);
        if (designation?.Artifact is not { } clearReference)
        {
            return null;
        }

        await AcquireEnvironmentalRetentionLockAsync(cancellationToken).ConfigureAwait(false);
        var observation = await SelectPrecipitationAsync(frame.Id, cancellationToken).ConfigureAwait(false);
        var match = observation.Match;
        var environment = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            ParseSolarRegime(frame.CycleEvidenceJson),
            match.Status,
            match.Observation?.ObservationId,
            observation.ContentSha256,
            IsPrecipitationDetected(match.Observation));
        var environmentElement = CaptureContractJson.SerializeToElement(environment);
        var environmentPayload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(environmentElement));
        var environmentIdentity = ProcessingIdentity.ComputePayloadSha256(environmentPayload);
        var clearSelector = ProcessingInputSelector.Raw(clearReference.Variant);
        var auxiliaries = new ProcessingAuxiliaryInput[]
        {
            new("clear-reference", ProcessingAuxiliaryInputKind.Artifact, clearSelector,
                ArtifactId: clearReference.ArtifactId),
            new("environment", ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                IdentitySha256: environmentIdentity)
        };

        var job = CreateJob(source, recipe, result: null, now);
        job.ExpectedRecipeIdentitySha256 = BuiltInProcessingRecipes.CreateExecutionIdentity(
            recipe.RecipeName, recipe.Options, recipe.InputSelector, auxiliaryInputs: auxiliaries).IdentitySha256;
        var clearRequirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = job.InputRequirements.Count,
            BindingName = "clear-reference",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(clearSelector)).GetRawText(),
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = clearReference.Frame?.AgentId ?? frame.AgentId,
            ExpectedRigId = frame.RigId,
            ExpectedCaptureSequence = clearReference.Frame?.CaptureSequence,
            ExpectedCentralArtifactId = clearReference.Id,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = now
        };
        job.InputRequirements.Add(clearRequirement);
        job.Inputs.Add(CreateExactInput(job, clearRequirement, clearReference, now));

        var environmentRequirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = job.InputRequirements.Count,
            BindingName = "environment",
            SourceKind = CentralDerivativeInputSourceKind.EnvironmentalObservation,
            IsRequired = true,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = frame.AgentId,
            ExpectedRigId = frame.RigId,
            ExpectedCaptureSequence = frame.CaptureSequence,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = now
        };
        job.InputRequirements.Add(environmentRequirement);
        job.CanonicalInputs.Add(new CentralDerivativeJobCanonicalInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = environmentRequirement,
            CentralDerivativeJobInputRequirementId = environmentRequirement.Id,
            Ordinal = environmentRequirement.Ordinal,
            SchemaVersion = CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            IdentitySha256 = environmentIdentity,
            CanonicalJson = System.Text.Encoding.UTF8.GetString(environmentPayload),
            ByteLength = environmentPayload.Length,
            EnvironmentalObservationRecordId = observation.RecordId,
            SelectedAtUtc = now
        });
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        return job;
    }

    private async Task<EnvironmentalObservationSelection> SelectPrecipitationAsync(
        Guid frameId,
        CancellationToken cancellationToken)
    {
        var rain = await environmentalQuery!.SelectFrameAsync(
            frameId,
            CreateEnvironmentalSelector(EnvironmentalObservationKind.RainState),
            cancellationToken).ConfigureAwait(false);
        return rain.Match.Status != EnvironmentalObservationMatchStatus.Missing
            ? rain
            : await environmentalQuery.SelectFrameAsync(
                frameId,
                CreateEnvironmentalSelector(EnvironmentalObservationKind.PrecipitationRate),
                cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureWeatherCloudOverlayJobAsync(
        CentralFrame frame,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var preview = frame.Artifacts.FirstOrDefault(candidate =>
            candidate.Role == FrameArtifactRole.Preview
            && candidate.RecipeVersion == CentralDerivativeRecipeCatalog.PreviewRecipeVersion
            && candidate.Variant == CentralDerivativeRecipeCatalog.PreviewVariant
            && IsUsable(candidate));
        var assessment = frame.Artifacts.FirstOrDefault(candidate =>
            candidate.Role == FrameArtifactRole.Metadata
            && candidate.RecipeVersion == CentralDerivativeRecipeCatalog.CloudAssessmentRecipeVersion
            && candidate.Variant == CentralDerivativeRecipeCatalog.CloudAssessmentVariant
            && IsUsable(candidate));
        if (preview is null || assessment is null)
        {
            return;
        }
        var cloudJob = await dbContext.CentralDerivativeJobs
            .Include(job => job.CanonicalInputs)
            .SingleOrDefaultAsync(job => job.ResultCentralArtifactId == assessment.Id
                && job.RecipeName == BuiltInProcessingRecipes.CloudAssessment
                && job.Status == CentralDerivativeJobStatus.Completed,
                cancellationToken).ConfigureAwait(false);
        if (cloudJob?.CanonicalInputs.SingleOrDefault() is not { } environmentInput)
        {
            return;
        }
        var recipe = CentralDerivativeRecipeCatalog.WeatherCloudOverlayRecipe;
        var requestIdentity = CentralDerivativeJobIdentity.CreateRequestIdentity(
            frame.DevicePublicId, preview.ArtifactId, recipe);
        if (dbContext.CentralDerivativeJobs.Local.Any(job => job.RequestIdentitySha256 == requestIdentity)
            || await dbContext.CentralDerivativeJobs.AnyAsync(job => job.RequestIdentitySha256 == requestIdentity,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var assessmentEvidence = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
            .SingleOrDefaultAsync(evidence => evidence.CentralArtifactId == assessment.Id,
                cancellationToken).ConfigureAwait(false);
        if (assessmentEvidence is null)
        {
            return;
        }
        var assessmentSelector = ProcessingInputSelector.RecipeResult(
            FrameArtifactRole.Metadata,
            CentralDerivativeRecipeCatalog.CloudAssessmentVariant,
            assessmentEvidence.RecipeIdentitySha256);
        var auxiliaries = new ProcessingAuxiliaryInput[]
        {
            new("assessment", ProcessingAuxiliaryInputKind.Artifact, assessmentSelector,
                ArtifactId: assessment.ArtifactId),
            new("environment", ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: environmentInput.SchemaVersion,
                IdentitySha256: environmentInput.IdentitySha256)
        };
        var job = CreateJob(preview, recipe, result: null, now);
        job.ExpectedRecipeIdentitySha256 = BuiltInProcessingRecipes.CreateExecutionIdentity(
            recipe.RecipeName, recipe.Options, recipe.InputSelector, auxiliaryInputs: auxiliaries).IdentitySha256;
        var assessmentRequirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = job.InputRequirements.Count,
            BindingName = "assessment",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(assessmentSelector)).GetRawText(),
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = frame.AgentId,
            ExpectedRigId = frame.RigId,
            ExpectedCaptureSequence = frame.CaptureSequence,
            ExpectedCentralArtifactId = assessment.Id,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = now
        };
        job.InputRequirements.Add(assessmentRequirement);
        job.Inputs.Add(CreateExactInput(job, assessmentRequirement, assessment, now));
        var environmentRequirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = job.InputRequirements.Count,
            BindingName = "environment",
            SourceKind = CentralDerivativeInputSourceKind.EnvironmentalObservation,
            IsRequired = true,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = frame.AgentId,
            ExpectedRigId = frame.RigId,
            ExpectedCaptureSequence = frame.CaptureSequence,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = now
        };
        job.InputRequirements.Add(environmentRequirement);
        job.CanonicalInputs.Add(new CentralDerivativeJobCanonicalInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = environmentRequirement,
            CentralDerivativeJobInputRequirementId = environmentRequirement.Id,
            Ordinal = environmentRequirement.Ordinal,
            SchemaVersion = environmentInput.SchemaVersion,
            IdentitySha256 = environmentInput.IdentitySha256,
            CanonicalJson = environmentInput.CanonicalJson,
            ByteLength = environmentInput.ByteLength,
            EnvironmentalObservationRecordId = environmentInput.EnvironmentalObservationRecordId,
            SelectedAtUtc = now
        });
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        dbContext.CentralDerivativeJobs.Add(job);
    }

    private async Task AcquireEnvironmentalRetentionLockAsync(CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return;
        }
        var lockResource = EnvironmentalObservationLockNames.Retention;
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = {lockResource},
                @LockMode = 'Shared',
                @LockOwner = 'Transaction',
                @LockTimeout = 10000;
            IF @result < 0
                THROW 51007, 'Could not acquire the environmental retention lock.', 1;
            """, cancellationToken).ConfigureAwait(false);
    }

    private static EnvironmentalObservationSelector CreateEnvironmentalSelector(EnvironmentalObservationKind kind)
        => new(kind, EnvironmentalSourcePriority, EnvironmentalQualities, TimeSpan.FromHours(24));

    private static bool IsPrecipitationDetected(EnvironmentalObservationV1? observation)
        => observation?.Value.Kind switch
        {
            EnvironmentalObservationKind.RainState => observation.Value.BooleanValue == true,
            EnvironmentalObservationKind.PrecipitationRate => observation.Value.NumericValue > 0,
            _ => false
        };

    private static CaptureSolarRegime? ParseSolarRegime(string? cycleEvidenceJson)
        => string.IsNullOrWhiteSpace(cycleEvidenceJson)
            ? null
            : JsonSerializer.Deserialize<CaptureCycleEvidence>(cycleEvidenceJson)?.SolarRegime;

    private static CentralDerivativeJobInput CreateExactInput(
        CentralDerivativeJob job,
        CentralDerivativeJobInputRequirement requirement,
        CentralArtifact artifact,
        DateTimeOffset now)
    {
        var snapshot = CentralDerivativeWindowCompatibility.EmptySnapshot;
        return new CentralDerivativeJobInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = requirement.Ordinal,
            CentralArtifactId = artifact.Id,
            Artifact = artifact,
            CaptureSequence = artifact.Frame?.CaptureSequence,
            CompatibilityJson = snapshot.Json,
            CompatibilitySha256 = snapshot.Sha256,
            ByteLength = artifact.ByteLength,
            SelectedAtUtc = now
        };
    }

    private static long? AddSequenceOffset(long? captureSequence, int sequenceOffset)
    {
        if (!captureSequence.HasValue)
        {
            return null;
        }
        try
        {
            return checked(captureSequence.Value + sequenceOffset);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static void Complete(CentralDerivativeJob job, CentralArtifact result, DateTimeOffset now)
    {
        job.Status = CentralDerivativeJobStatus.Completed;
        job.ResultCentralArtifactId = result.Id;
        job.ResultArtifact = result;
        job.CompletedAtUtc ??= now;
        job.UpdatedAtUtc = now;
        job.AvailableAtUtc = null;
        job.LeaseToken = null;
        job.LeaseOwner = null;
        job.LeaseAcquiredAtUtc = null;
        job.LeaseExpiresAtUtc = null;
        job.StateReasonCode = null;
        job.LastError = null;
    }

    private static void Restore(CentralDerivativeJob job, CentralArtifact source, DateTimeOffset now)
    {
        if (!IsUsable(source)
            || job.AvailableAtUtc.HasValue
            || job.Status != CentralDerivativeJobStatus.Pending
                && job.Status != CentralDerivativeJobStatus.RetryableFailure
                && job.Status != CentralDerivativeJobStatus.Skipped
                && job.Status != CentralDerivativeJobStatus.Quarantined
            || job.Status == CentralDerivativeJobStatus.RetryableFailure && !IsInvalidationFailure(job)
            || job.Status == CentralDerivativeJobStatus.Skipped
                && !string.Equals(job.LastError, LegacySourceSkippedReason, StringComparison.Ordinal)
            || job.Status == CentralDerivativeJobStatus.Quarantined
                && !IsLocationResolutionFailure(job))
        {
            return;
        }
        job.Status = CentralDerivativeJobStatus.Pending;
        if (job.AttemptCount >= job.MaxAttempts)
        {
            job.MaxAttempts = checked(job.AttemptCount + CentralDerivativeRecipeCatalog.DefaultMaxAttempts);
        }
        job.AvailableAtUtc = now;
        job.ResolutionCompletedAtUtc = now;
        job.StateReasonCode = null;
        job.LastError = null;
        job.UpdatedAtUtc = now;
    }

    private async Task QuarantineForLocationAsync(
        CentralDerivativeJob job,
        CentralArtifact? result,
        CentralCaptureLocationEvidenceState evidenceState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reason = evidenceState == CentralCaptureLocationEvidenceState.ReportedUnresolved
            ? LocationUnresolvedReason
            : LocationMismatchReason;
        if (job.Status == CentralDerivativeJobStatus.Leased)
        {
            var attempt = dbContext.CentralDerivativeJobAttempts.Local.SingleOrDefault(item =>
                item.CentralDerivativeJobId == job.Id
                && item.AttemptNumber == job.AttemptCount
                && item.Outcome == CentralDerivativeAttemptOutcome.Leased)
                ?? await dbContext.CentralDerivativeJobAttempts.SingleOrDefaultAsync(item =>
                    item.CentralDerivativeJobId == job.Id
                    && item.AttemptNumber == job.AttemptCount
                    && item.Outcome == CentralDerivativeAttemptOutcome.Leased,
                    cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A leased derivative job has no active attempt.");
            attempt.Outcome = CentralDerivativeAttemptOutcome.Quarantined;
            attempt.ReasonCode = reason;
            attempt.EndedAtUtc = now;
        }
        if (result is
            {
                ObjectState: CentralArtifactObjectState.Available,
                ReconstructionState: CentralReconstructionState.Complete
            })
        {
            result.StateReasonCode = reason;
            result.ReconstructionState = CentralReconstructionState.Quarantined;
        }
        job.Status = CentralDerivativeJobStatus.Quarantined;
        job.AvailableAtUtc = null;
        job.LeaseToken = null;
        job.LeaseOwner = null;
        job.LeaseAcquiredAtUtc = null;
        job.LeaseExpiresAtUtc = null;
        job.ResolutionCompletedAtUtc = now;
        job.StateReasonCode = reason;
        job.LastError = reason;
        job.UpdatedAtUtc = now;
    }

    private static bool IsLocationResolutionFailure(CentralDerivativeJob job)
        => string.Equals(job.LastError, LocationUnresolvedReason, StringComparison.Ordinal)
            || string.Equals(job.LastError, LocationMismatchReason, StringComparison.Ordinal);

    private static bool IsInvalidationFailure(CentralDerivativeJob job)
        => string.Equals(job.LastError, SourceInvalidatedReason, StringComparison.Ordinal)
            || string.Equals(job.LastError, ResultInvalidatedReason, StringComparison.Ordinal);

    private async Task<bool> IsCanonicalTargetAsync(
        CentralArtifact source,
        CentralArtifact target,
        CentralDerivativeRecipe recipe,
        CancellationToken cancellationToken)
    {
        var evidence = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CentralArtifactId == target.Id
                && item.RequestedRecipeIdentitySha256 == recipe.RequestedRecipeIdentitySha256,
                cancellationToken).ConfigureAwait(false);
        if (evidence is null || ProcessingIdentity.CreateArtifactId(evidence.OutputIdentitySha256) != target.ArtifactId)
        {
            return false;
        }
        return await dbContext.CentralArtifactSources.AsNoTracking().AnyAsync(item =>
            item.CentralArtifactId == target.Id
            && item.SourceArtifactId == source.ArtifactId
            && item.ResolvedCentralArtifactId == source.Id,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool CanComplete(CentralDerivativeJob job)
        => job.Status is CentralDerivativeJobStatus.Pending
            or CentralDerivativeJobStatus.RetryableFailure
            or CentralDerivativeJobStatus.Skipped
            or CentralDerivativeJobStatus.Completed;

    private static bool IsUsable(CentralArtifact artifact)
        => artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete;
}
