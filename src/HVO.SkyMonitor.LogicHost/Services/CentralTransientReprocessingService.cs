using System.Data;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static class CentralTransientReprocessingRuntime
{
    public const string RecipeName = "central-transient-reprocessing";
    public const string RecipeVersion = "central-transient-reprocessing-v1";
    public const string RequestSchemaVersion = "central-transient-reprocessing-request-v1";
    public const int MaximumAttempts = 3;
    public const long MaximumFrozenInputBytes = 512L * 1024 * 1024;
}

internal sealed record CentralTransientReprocessingRequest(
    TransientDeterministicAssessmentOptionsV1 Options);

internal sealed record CentralTransientReprocessingResponse(
    Guid JobId,
    Guid SourceEventVersionId,
    string RequestIdentitySha256,
    string ETag,
    bool Replayed);

internal enum CentralTransientReprocessingStatus
{
    Scheduled,
    NotFound,
    Invalid,
    Ineligible,
    PreconditionFailed,
    IdempotencyConflict
}

internal sealed record CentralTransientReprocessingResult(
    CentralTransientReprocessingStatus Status,
    CentralTransientReprocessingResponse? Response = null);

internal interface ICentralTransientReprocessingService
{
    Task<CentralTransientReprocessingResult> ScheduleAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        byte[] expectedRowVersion,
        string idempotencyKey,
        CentralTransientReprocessingRequest request,
        CancellationToken cancellationToken);
}

internal sealed class CentralTransientReprocessingService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    CentralTransientLifecycleTelemetry? telemetry = null) : ICentralTransientReprocessingService
{
    public async Task<CentralTransientReprocessingResult> ScheduleAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        byte[] expectedRowVersion,
        string idempotencyKey,
        CentralTransientReprocessingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var started = timeProvider.GetTimestamp();
        using var activity = telemetry?.Start("reprocess");
        var actor = CentralArtifactCredentialAccess.GetSubject(principal);
        if (!CentralArtifactCredentialAccess.HasScope(principal, "api.admin") ||
            string.IsNullOrWhiteSpace(actor) || expectedRowVersion.Length != 8 ||
            idempotencyKey.Length is < 1 or > 128 || request.Options is null)
        {
            telemetry?.RecordReprocessing("invalid", timeProvider.GetElapsedTime(started));
            return new(CentralTransientReprocessingStatus.Invalid);
        }
        var optionsJson = CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(request.Options)).GetRawText();
        var optionsIdentity = TransientAssessmentFactory.ComputeOptionsIdentitySha256(request.Options);
        var recipeIdentity = TransientAssessmentFactory.ComputeRecipeIdentitySha256(request.Options);
        var canonicalRequest = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            schemaVersion = CentralTransientReprocessingRuntime.RequestSchemaVersion,
            producerName = TransientAssessmentFactory.ProducerName,
            producerVersion = TransientAssessmentFactory.ProducerVersion,
            recipeIdentitySha256 = recipeIdentity,
            optionsIdentitySha256 = optionsIdentity,
            options = request.Options
        })).GetRawText();
        var canonicalBytes = Encoding.UTF8.GetBytes(canonicalRequest);
        var canonicalSha = ProcessingIdentity.ComputePayloadSha256(canonicalBytes);

        var preflightArtifactIds = await dbContext.CentralTransientObservationSources.AsNoTracking()
            .Where(item => item.Observation!.CentralTransientEventId == centralTransientEventId)
            .Select(item => item.CentralArtifactId)
            .Concat(dbContext.CentralTransientObservationBackgrounds.AsNoTracking()
                .Where(item => item.Observation!.CentralTransientEventId == centralTransientEventId)
                .Select(item => item.CentralArtifactId))
            .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var holdTargets = await CentralTransientPayloadHoldFence.ReadArtifactsAsync(
            dbContext, preflightArtifactIds, cancellationToken).ConfigureAwait(false);
        await using var holdScope = await CentralTransientPayloadHoldFence.AcquireAsync(
            dbContext, holdTargets, cancellationToken).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var acquired = await dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientEvents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {centralTransientEventId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (acquired != 1)
        {
            return new(CentralTransientReprocessingStatus.NotFound);
        }
        var replay = await dbContext.CentralTransientReprocessingRequests.AsNoTracking()
            .Include(item => item.ReprocessingJob)
            .SingleOrDefaultAsync(item =>
            item.CentralTransientEventId == centralTransientEventId && item.ActorIdentity == actor &&
            item.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (!string.Equals(replay.OptionsIdentitySha256, optionsIdentity, StringComparison.Ordinal) ||
                !string.Equals(replay.RecipeIdentitySha256, recipeIdentity, StringComparison.Ordinal))
            {
                telemetry?.RecordReprocessing("conflict", timeProvider.GetElapsedTime(started));
                return new(CentralTransientReprocessingStatus.IdempotencyConflict);
            }
            var currentReplay = await dbContext.CentralTransientEventCurrent.AsNoTracking().SingleAsync(
                item => item.CentralTransientEventId == centralTransientEventId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry?.RecordReprocessing("replayed", timeProvider.GetElapsedTime(started));
            return new(CentralTransientReprocessingStatus.Scheduled, new(
                replay.CentralDerivativeJobId,
                replay.ReprocessingJob!.SourceEventVersionId,
                replay.ReprocessingJob.RequestIdentitySha256,
                CentralTransientEventEtag.Create(currentReplay.RowVersion),
                true));
        }
        if (await dbContext.CentralTransientPayloadReleases.AsNoTracking().AnyAsync(item =>
                item.CentralTransientEventId == centralTransientEventId, cancellationToken).ConfigureAwait(false))
        {
            return new(CentralTransientReprocessingStatus.Ineligible);
        }
        var current = await dbContext.CentralTransientEventCurrent.AsNoTracking().SingleOrDefaultAsync(
            item => item.CentralTransientEventId == centralTransientEventId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new(CentralTransientReprocessingStatus.NotFound);
        }
        if (!current.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            telemetry?.RecordReprocessing("conflict", timeProvider.GetElapsedTime(started));
            return new(CentralTransientReprocessingStatus.PreconditionFailed);
        }
        var version = await dbContext.CentralTransientEventVersions.AsNoTracking().SingleAsync(
            item => item.EventVersionId == current.LatestEventVersionId, cancellationToken).ConfigureAwait(false);
        var parsed = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(version.CanonicalEventJson));
        var transientEvent = parsed.Value is not null && parsed.Validation.IsValid
            ? parsed.Value
            : throw new CentralDerivativeJobStateException("Persisted transient event history is invalid.");
        var observationIds = transientEvent.Observations.Select(item => item.ObservationId).ToArray();
        var sourceArtifactIds = await dbContext.CentralTransientObservationSources.AsNoTracking()
            .Where(item => item.Observation!.CentralTransientEventId == centralTransientEventId &&
                observationIds.Contains(item.ObservationId))
            .Select(item => item.CentralArtifactId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var backgroundArtifactIds = await dbContext.CentralTransientObservationBackgrounds.AsNoTracking()
            .Where(item => item.Observation!.CentralTransientEventId == centralTransientEventId &&
                observationIds.Contains(item.ObservationId))
            .Select(item => item.CentralArtifactId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var artifactIds = sourceArtifactIds.Concat(backgroundArtifactIds).Distinct().ToArray();
        var artifacts = await dbContext.CentralArtifacts
            .Where(item => artifactIds.Contains(item.Id))
            .Include(item => item.Frame)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (artifacts.Length == 0 || artifacts.Sum(item => item.ByteLength) >
                CentralTransientReprocessingRuntime.MaximumFrozenInputBytes || artifacts.Any(item =>
                item.ObjectState != CentralArtifactObjectState.Available ||
                item.ReconstructionState != CentralReconstructionState.Complete))
        {
            return new(CentralTransientReprocessingStatus.Invalid);
        }
        var selectedArtifactIds = artifacts.Select(item => item.Id).ToHashSet();
        var selectedHoldTargets = holdScope.Targets.Where(target => selectedArtifactIds.Contains(target.RecordId))
            .ToArray();
        if (selectedHoldTargets.Length != selectedArtifactIds.Count)
        {
            return new(CentralTransientReprocessingStatus.Invalid);
        }
        try
        {
            await CentralTransientPayloadHoldFence.ValidateAsync(
                dbContext, selectedHoldTargets, cancellationToken).ConfigureAwait(false);
        }
        catch (CentralTransientPayloadHoldRejectedException)
        {
            return new(CentralTransientReprocessingStatus.Ineligible);
        }
        canonicalRequest = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            schemaVersion = CentralTransientReprocessingRuntime.RequestSchemaVersion,
            centralTransientEventId,
            sourceEventVersionId = version.EventVersionId,
            sourceEventVersionSha256 = version.CanonicalEventSha256,
            producerName = TransientAssessmentFactory.ProducerName,
            producerVersion = TransientAssessmentFactory.ProducerVersion,
            recipeIdentitySha256 = recipeIdentity,
            optionsIdentitySha256 = optionsIdentity,
            options = request.Options,
            orderedEvidence = transientEvent.Observations.OrderBy(item => item.Ordinal).Select(item => new
            {
                item.ObservationId,
                item.Source.EvidenceId,
                source = item.Source.Locator.Artifact,
                backgrounds = item.BackgroundArtifacts
            })
        })).GetRawText();
        canonicalBytes = Encoding.UTF8.GetBytes(canonicalRequest);
        canonicalSha = ProcessingIdentity.ComputePayloadSha256(canonicalBytes);
        var requestIdentity = ProcessingIdentity.ComputePayloadSha256(Encoding.UTF8.GetBytes(string.Join('\n',
            CentralTransientReprocessingRuntime.RequestSchemaVersion,
            centralTransientEventId.ToString("N"),
            version.EventVersionId.ToString("N"),
            version.CanonicalEventSha256,
            recipeIdentity,
            optionsIdentity,
            canonicalSha)));
        var converged = await dbContext.CentralTransientReprocessingJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RequestIdentitySha256 == requestIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (converged is not null)
        {
            dbContext.CentralTransientReprocessingRequests.Add(new()
            {
                CentralTransientEventId = centralTransientEventId,
                CentralDerivativeJobId = converged.CentralDerivativeJobId,
                ActorIdentity = actor,
                IdempotencyKey = idempotencyKey,
                RecipeIdentitySha256 = recipeIdentity,
                OptionsIdentitySha256 = optionsIdentity,
                CreatedUtc = timeProvider.GetUtcNow()
            });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry?.RecordReprocessing("replayed", timeProvider.GetElapsedTime(started));
            return new(CentralTransientReprocessingStatus.Scheduled, new(
                converged.CentralDerivativeJobId,
                converged.SourceEventVersionId,
                converged.RequestIdentitySha256,
                CentralTransientEventEtag.Create(current.RowVersion),
                true));
        }
        var activeStates = new[]
        {
            CentralDerivativeJobStatus.Waiting,
            CentralDerivativeJobStatus.Pending,
            CentralDerivativeJobStatus.Leased,
            CentralDerivativeJobStatus.RetryableFailure,
            CentralDerivativeJobStatus.CancelRequested
        };
        if (await dbContext.CentralTransientReprocessingJobs.AsNoTracking().AnyAsync(item =>
                item.CentralTransientEventId == centralTransientEventId &&
                activeStates.Contains(item.Job!.Status), cancellationToken).ConfigureAwait(false))
        {
            return new(CentralTransientReprocessingStatus.Ineligible);
        }
        var source = artifacts.OrderBy(item => item.Id).First();
        var now = timeProvider.GetUtcNow();
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            TargetRole = FrameArtifactRole.Metadata,
            TargetRecipeVersion = CentralTransientReprocessingRuntime.RecipeVersion,
            TargetVariant = "transient-reprocessing",
            RecipeName = CentralTransientReprocessingRuntime.RecipeName,
            RecipeOptionsJson = optionsJson,
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = recipeIdentity,
            ExpectedRecipeIdentitySha256 = recipeIdentity,
            RequestIdentitySha256 = requestIdentity,
            Status = CentralDerivativeJobStatus.Pending,
            MaxAttempts = CentralTransientReprocessingRuntime.MaximumAttempts,
            AvailableAtUtc = now,
            ResolutionCompletedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        job.TraceParent = Activity.Current?.Id;
        job.TraceState = Activity.Current?.TraceStateString;
        var ordinal = 0;
        foreach (var artifact in artifacts.OrderBy(item => item.Id))
        {
            var requirement = new CentralDerivativeJobInputRequirement
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Ordinal = ordinal,
                BindingName = $"event-evidence-{ordinal}",
                SourceKind = CentralDerivativeInputSourceKind.Artifact,
                IsRequired = true,
                SelectorJson = "{}",
                CompatibilityMode = CentralDerivativeCompatibilityMode.None,
                ExpectedAgentId = artifact.Frame!.AgentId,
                ExpectedCentralArtifactId = artifact.Id,
                ResolutionState = CentralDerivativeInputResolutionState.Resolved,
                ResolvedAtUtc = now
            };
            job.InputRequirements.Add(requirement);
            job.Inputs.Add(new CentralDerivativeJobInput
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Requirement = requirement,
                CentralDerivativeJobInputRequirementId = requirement.Id,
                Ordinal = ordinal,
                CentralArtifactId = artifact.Id,
                Artifact = artifact,
                CaptureSequence = artifact.Frame.CaptureSequence,
                CompatibilityJson = CentralDerivativeWindowCompatibility.EmptySnapshot.Json,
                CompatibilitySha256 = CentralDerivativeWindowCompatibility.EmptySnapshot.Sha256,
                ByteLength = artifact.ByteLength,
                SelectedAtUtc = now
            });
            ordinal++;
        }
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(
            job.Inputs, job.CanonicalInputs);
        dbContext.CentralDerivativeJobs.Add(job);
        var reprocessingJob = new CentralTransientReprocessingJob
        {
            CentralDerivativeJobId = job.Id,
            Job = job,
            CentralTransientEventId = centralTransientEventId,
            SourceEventVersionId = version.EventVersionId,
            ActorIdentity = actor,
            IdempotencyKey = idempotencyKey,
            CanonicalRequestJson = canonicalRequest,
            CanonicalRequestSha256 = canonicalSha,
            CanonicalRequestByteLength = canonicalBytes.Length,
            RequestIdentitySha256 = requestIdentity,
            ProducerName = TransientAssessmentFactory.ProducerName,
            ProducerVersion = TransientAssessmentFactory.ProducerVersion,
            RecipeIdentitySha256 = recipeIdentity,
            OptionsIdentitySha256 = optionsIdentity,
            OptionsJson = optionsJson,
            CreatedUtc = now
        };
        dbContext.CentralTransientReprocessingJobs.Add(reprocessingJob);
        dbContext.CentralTransientReprocessingRequests.Add(new()
        {
            CentralTransientEventId = centralTransientEventId,
            CentralDerivativeJobId = job.Id,
            ReprocessingJob = reprocessingJob,
            ActorIdentity = actor,
            IdempotencyKey = idempotencyKey,
            RecipeIdentitySha256 = recipeIdentity,
            OptionsIdentitySha256 = optionsIdentity,
            CreatedUtc = now
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry?.RecordReprocessing("scheduled", timeProvider.GetElapsedTime(started));
        return new(CentralTransientReprocessingStatus.Scheduled, new(
            job.Id,
            version.EventVersionId,
            requestIdentity,
            CentralTransientEventEtag.Create(current.RowVersion),
            false));
    }
}

internal interface ICentralTransientReprocessingExecutor
{
    Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);
}

internal sealed class CentralTransientReprocessingExecutor(
    ApplicationDbContext dbContext,
    ICentralTransientEventVersionAppender versionAppender,
    ICentralTransientDerivativeScheduler derivativeScheduler,
    ICentralDerivativeJobService jobService,
    ICentralArtifactObjectReader objectReader,
    TimeProvider timeProvider) : ICentralTransientReprocessingExecutor
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var record = await dbContext.CentralTransientReprocessingJobs.AsNoTracking()
            .Include(item => item.SourceEventVersion)
            .SingleAsync(item => item.CentralDerivativeJobId == lease.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (record.CommittedUtc.HasValue)
        {
            if (!record.ResultEventVersionId.HasValue)
            {
                throw new CentralDerivativeJobStateException(
                    "Committed transient reprocessing is missing its result event version.");
            }
            await derivativeScheduler.EnsureScheduledAsync(
                [record.ResultEventVersionId.Value], cancellationToken).ConfigureAwait(false);
            return await CompleteAsync(lease, "transient-reprocessing.output-adopted", cancellationToken)
                .ConfigureAwait(false);
        }
        var canonicalRequestBytes = Encoding.UTF8.GetBytes(record.CanonicalRequestJson);
        if (canonicalRequestBytes.Length != record.CanonicalRequestByteLength ||
            !string.Equals(
                ProcessingIdentity.ComputePayloadSha256(canonicalRequestBytes),
                record.CanonicalRequestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDerivativeJobStateException("The frozen reprocessing request is invalid.");
        }
        var artifacts = await dbContext.CentralArtifacts.AsNoTracking()
            .Where(artifact => dbContext.CentralDerivativeJobInputs.Any(input =>
                input.CentralDerivativeJobId == lease.JobId && input.CentralArtifactId == artifact.Id))
            .OrderBy(artifact => artifact.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (artifacts.Length == 0 || artifacts.Any(artifact =>
                artifact.ObjectState != CentralArtifactObjectState.Available ||
                artifact.ReconstructionState != CentralReconstructionState.Complete))
        {
            throw new CentralDerivativeInputRejectedException(
                "The frozen reprocessing evidence is no longer available.");
        }
        foreach (var artifact in artifacts)
        {
            _ = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
        }
        var parsed = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(
            record.SourceEventVersion!.CanonicalEventJson));
        var sourceEvent = parsed.Value is not null && parsed.Validation.IsValid
            ? parsed.Value
            : throw new CentralDerivativeJobStateException("The reprocessing source event is invalid.");
        var options = JsonSerializer.Deserialize<TransientDeterministicAssessmentOptionsV1>(
            record.OptionsJson, SerializerOptions)
            ?? throw new CentralDerivativeJobStateException("The reprocessing options are invalid.");
        var extractionIdentities = await dbContext.CentralTransientObservations.AsNoTracking()
            .Where(item => item.CentralTransientEventId == record.CentralTransientEventId)
            .ToDictionaryAsync(item => item.ObservationId, item => item.ExtractionReceiptIdentitySha256,
                cancellationToken).ConfigureAwait(false);
        var observations = sourceEvent.Observations.OrderBy(item => item.Ordinal).Select(item =>
            new TransientAssessmentObservationV1(
                sourceEvent.EventId,
                extractionIdentities[item.ObservationId],
                item)).ToArray();
        var assessmentId = ProcessingIdentity.CreateArtifactId(record.RequestIdentitySha256);
        var createdUtc = timeProvider.GetUtcNow();
        if (createdUtc <= sourceEvent.VersionCreatedUtc)
        {
            createdUtc = sourceEvent.VersionCreatedUtc.AddTicks(1);
        }
        var assessment = TransientAssessmentFactory.Create(new TransientAssessmentExecutionRequest(
            sourceEvent.EventId,
            assessmentId,
            createdUtc,
            TransientAssessmentAuthority.Authoritative,
            observations,
            options,
            sourceEvent.Assessments,
            sourceEvent.Assessments[^1].AssessmentId), cancellationToken);
        if (assessment.Status != TransientAssessmentExecutionStatus.Produced || assessment.Descriptor is null ||
            !string.Equals(assessment.Descriptor.Assessment.RecipeIdentitySha256,
                record.RecipeIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(assessment.Descriptor.OptionsIdentitySha256,
                record.OptionsIdentitySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDerivativeJobStateException("The reprocessed assessment did not match its frozen identity.");
        }
        var descriptor = assessment.Descriptor;
        var receipt = TransientAssessmentJson.Serialize(descriptor);
        dbContext.ChangeTracker.Clear();
        CentralTransientEventAppendResult appended;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false))
        {
            var leasedJob = await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(job =>
                job.Id == lease.JobId &&
                job.Status == CentralDerivativeJobStatus.Leased &&
                job.LeaseToken == lease.LeaseToken &&
                job.LeaseOwner == lease.WorkerId &&
                job.LeaseExpiresAtUtc > timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (leasedJob is null)
            {
                throw new CentralDerivativeJobStateException("The reprocessing lease is no longer active.");
            }
            var mutable = await dbContext.CentralTransientReprocessingJobs.SingleAsync(item =>
                item.CentralDerivativeJobId == lease.JobId && item.CommittedUtc == null, cancellationToken)
                .ConfigureAwait(false);
            var predecessor = await dbContext.CentralTransientAssessments.AsNoTracking().SingleAsync(item =>
                item.CentralTransientEventId == record.CentralTransientEventId &&
                item.AssessmentId == descriptor.Assessment.SupersedesAssessmentId, cancellationToken).ConfigureAwait(false);
            var assessmentRecord = new CentralTransientAssessmentRecord
            {
                AssessmentId = descriptor.Assessment.AssessmentId,
                CentralTransientEventId = record.CentralTransientEventId,
                CreatedUtc = descriptor.Assessment.CreatedUtc,
                Authority = descriptor.Assessment.Authority,
                Classification = descriptor.Assessment.Classification,
                MeteorSeverity = descriptor.Assessment.MeteorSeverity,
                ConfidenceMillionths = descriptor.Assessment.ConfidenceMillionths,
                SupersedesAssessmentId = descriptor.Assessment.SupersedesAssessmentId,
                SupersedesAssessmentCreatedUtc = predecessor.CreatedUtc,
                ProducerSchemaVersion = descriptor.Assessment.Producer.SchemaVersion,
                ProducerKind = descriptor.Assessment.Producer.Kind,
                ProducerName = descriptor.Assessment.Producer.Name,
                ProducerVersion = descriptor.Assessment.Producer.Version,
                RecipeIdentitySha256 = descriptor.Assessment.RecipeIdentitySha256,
                ReceiptSchemaVersion = descriptor.SchemaVersion,
                ExecutionIdentitySha256 = descriptor.ExecutionIdentitySha256,
                OptionsIdentitySha256 = descriptor.OptionsIdentitySha256,
                CanonicalReceiptJson = Encoding.UTF8.GetString(receipt),
                CanonicalReceiptSha256 = ProcessingIdentity.ComputePayloadSha256(receipt),
                CanonicalReceiptByteLength = receipt.Length
            };
            for (var ordinal = 0; ordinal < observations.Length; ordinal++)
            {
                assessmentRecord.EvidenceObservations.Add(new CentralTransientAssessmentObservation
                {
                    CentralTransientEventId = record.CentralTransientEventId,
                    AssessmentId = assessmentId,
                    Ordinal = ordinal,
                    ObservationId = observations[ordinal].Observation.ObservationId
                });
            }
            dbContext.CentralTransientAssessments.Add(assessmentRecord);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            appended = await versionAppender.AppendGeneratedAsync(record.CentralTransientEventId, previous =>
            {
                var versionCreatedUtc = descriptor.Assessment.CreatedUtc <= previous.VersionCreatedUtc
                    ? previous.VersionCreatedUtc.AddTicks(1)
                    : descriptor.Assessment.CreatedUtc;
                return previous with
                {
                    EventVersionId = Guid.NewGuid(),
                    Version = previous.Version + 1,
                    PreviousEventVersionId = previous.EventVersionId,
                    PreviousVersionCreatedUtc = previous.VersionCreatedUtc,
                    VersionCreatedUtc = versionCreatedUtc,
                    State = StateFor(descriptor.Assessment),
                    Assessments = previous.Assessments.Append(descriptor.Assessment).ToArray()
                };
            }, resetReviewState: true, cancellationToken).ConfigureAwait(false);
            mutable.CommittedUtc = appended.Event.VersionCreatedUtc;
            mutable.ResultAssessmentId = assessmentId;
            mutable.ResultEventVersionId = appended.Event.EventVersionId;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        await derivativeScheduler.EnsureScheduledAsync([appended.Event.EventVersionId], cancellationToken)
            .ConfigureAwait(false);
        return await CompleteAsync(lease, "transient-reprocessing.persisted", cancellationToken).ConfigureAwait(false);
    }

    private async Task<CentralDerivativeExecutionResult> CompleteAsync(
        CentralDerivativeJobLease lease,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await jobService.CompleteWithoutArtifactAsync(
            lease.JobId, lease.LeaseToken, reasonCode, cancellationToken).ConfigureAwait(false);
        return new(ProcessingOutcomeStatus.Produced, null, reasonCode);
    }

    private static TransientEventState StateFor(TransientAssessmentV1 assessment)
        => assessment.Reasons.Any(item => item.Kind == TransientReasonKind.Limitation)
            ? TransientEventState.NeedsReview
            : assessment.Classification switch
            {
                TransientClassification.Meteor or TransientClassification.Satellite or TransientClassification.Aircraft =>
                    TransientEventState.Validated,
                TransientClassification.SensorArtifact or TransientClassification.EnvironmentalArtifact =>
                    TransientEventState.Rejected,
                _ => TransientEventState.NeedsReview
            };
}
