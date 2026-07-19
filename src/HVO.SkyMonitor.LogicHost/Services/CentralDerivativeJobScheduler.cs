using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Diagnostics;
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
}

internal sealed class CentralDerivativeJobScheduler(
    ApplicationDbContext dbContext,
    ICentralDerivativeRecipeCatalog recipeCatalog,
    ICentralDerivativeWindowResolver windowResolver,
    IEnvironmentalObservationQueryService? environmentalQuery = null) : ICentralDerivativeJobScheduler
{
    internal const string SourceInvalidatedReason = "The derivative source artifact is not usable.";
    internal const string ResultInvalidatedReason = "The derivative result artifact is not usable.";
    internal const string LegacySourceSkippedReason = "The legacy derivative source is not reconstructable.";
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
            .SingleAsync(item => item.DevicePublicId == devicePublicId && item.ArtifactId == artifactId,
                cancellationToken).ConfigureAwait(false);
        await EnsureRequiredJobsAsync(artifact, now, cancellationToken).ConfigureAwait(false);
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
        if (artifact.Role == FrameArtifactRole.Raw)
        {
            foreach (var recipe in recipeCatalog.GetRequiredRecipes(artifact.Role))
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
                else if (target is not null && CanComplete(existing) && !IsInvalidationFailure(existing))
                {
                    Complete(existing, target, now);
                }
                else
                {
                    Restore(existing, artifact, now);
                }
            }
            await EnsureWeatherCloudOverlayJobAsync(frame, now, cancellationToken).ConfigureAwait(false);
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
            var recipe = recipeCatalog.GetRequiredRecipes(source.Role).FirstOrDefault(candidate =>
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

    private static CentralDerivativeJob CreateJob(
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
        if (recipe.Window is null)
        {
            job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        }
        return job;
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
        job.LastError = null;
    }

    private static void Restore(CentralDerivativeJob job, CentralArtifact source, DateTimeOffset now)
    {
        if (!IsUsable(source)
            || job.AvailableAtUtc.HasValue
            || job.Status != CentralDerivativeJobStatus.Pending
                && job.Status != CentralDerivativeJobStatus.RetryableFailure
                && job.Status != CentralDerivativeJobStatus.Skipped
            || job.Status == CentralDerivativeJobStatus.RetryableFailure && !IsInvalidationFailure(job)
            || job.Status == CentralDerivativeJobStatus.Skipped
                && !string.Equals(job.LastError, LegacySourceSkippedReason, StringComparison.Ordinal))
        {
            return;
        }
        job.Status = CentralDerivativeJobStatus.Pending;
        if (job.AttemptCount >= job.MaxAttempts)
        {
            job.MaxAttempts = checked(job.AttemptCount + CentralDerivativeRecipeCatalog.DefaultMaxAttempts);
        }
        job.AvailableAtUtc = now;
        job.LastError = null;
        job.UpdatedAtUtc = now;
    }

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
