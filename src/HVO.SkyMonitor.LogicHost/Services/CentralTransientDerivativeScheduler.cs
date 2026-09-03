using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Diagnostics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralTransientDerivativeScheduler
{
    Task EnsureScheduledAsync(
        IReadOnlyList<Guid> eventVersionIds,
        CancellationToken cancellationToken);
}

internal static class CentralTransientDerivativeRuntime
{
    public const string RecipeName = "central-transient-derivative-bundle";
    public const string RecipeVersion = "central-transient-derivative-bundle-v1";
    public const string ProducerSchemaVersion = "transient-derivative-producer-v1";
    public const string ProducerName = "hvo.linear16-transient-reconstruction";
    public const string ProducerVersion = "linear16-transient-reconstruction-v1";
    public const string RequestSchemaVersion = "central-transient-derivative-request-v1";
    public const int MaximumAttempts = 3;
    public const long MaximumFrozenInputBytes = 512L * 1024 * 1024;

    public static readonly string OptionsJson = CaptureContractJson.Canonicalize(
        CaptureContractJson.SerializeToElement(new
        {
            schemaVersion = "central-transient-derivative-options-v1",
            cropPaddingPixels = 16,
            jpegQuality = 90,
            reconstructionAlgorithm = ProducerVersion,
            productAlgorithm = Linear16TransientDerivativeProductFactory.AlgorithmVersion,
            jpegAlgorithm = JpegImageCodec.AlgorithmVersion,
            reconstructionMediaType = Linear16TransientDerivativeProductFactory.Linear16MediaType,
            maskMediaType = Linear16TransientDerivativeProductFactory.PackedMaskMediaType,
            maskPacking = "detector-row-major-lsb-first"
        })).GetRawText();

    public static readonly string OptionsIdentitySha256 = ProcessingIdentity.ComputePayloadSha256(
        Encoding.UTF8.GetBytes(OptionsJson));

    public static readonly string RecipeIdentitySha256 = ProcessingIdentity.CreateRecipeIdentity(
        new RecipeIdentityDescriptor(
            RecipeName,
            RecipeVersion,
            ProducerVersion,
            CaptureContractJson.SerializeToElement(new
            {
                schemaVersion = "central-transient-derivative-options-v1",
                cropPaddingPixels = 16,
                jpegQuality = 90,
                reconstructionAlgorithm = ProducerVersion,
                productAlgorithm = Linear16TransientDerivativeProductFactory.AlgorithmVersion,
                jpegAlgorithm = JpegImageCodec.AlgorithmVersion,
                reconstructionMediaType = Linear16TransientDerivativeProductFactory.Linear16MediaType,
                maskMediaType = Linear16TransientDerivativeProductFactory.PackedMaskMediaType,
                maskPacking = "detector-row-major-lsb-first"
            }),
            OptionsIdentitySha256)).IdentitySha256;
}

internal sealed class CentralTransientDerivativeScheduler(ApplicationDbContext dbContext)
    : ICentralTransientDerivativeScheduler
{
    public async Task EnsureScheduledAsync(
        IReadOnlyList<Guid> eventVersionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventVersionIds);
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Transient derivative scheduling requires payload object locks before its transaction.");
        }
        var eventIds = await dbContext.CentralTransientEventVersions.AsNoTracking()
            .Where(version => eventVersionIds.Contains(version.EventVersionId))
            .Select(version => version.CentralTransientEventId)
            .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var artifactIds = await dbContext.CentralTransientObservationSources.AsNoTracking()
            .Where(source => eventIds.Contains(source.Observation!.CentralTransientEventId))
            .Select(source => source.CentralArtifactId)
            .Concat(dbContext.CentralTransientObservationBackgrounds.AsNoTracking()
                .Where(background => eventIds.Contains(background.Observation!.CentralTransientEventId))
                .Select(background => background.CentralArtifactId))
            .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var holdTargets = await CentralTransientPayloadHoldFence.ReadArtifactsAsync(
            dbContext, artifactIds, cancellationToken).ConfigureAwait(false);
        if (holdTargets.Count != artifactIds.Length)
        {
            throw new CentralTransientPayloadHoldRejectedException("transient-retention.hold-target-missing");
        }
        await using var holdScope = await CentralTransientPayloadHoldFence.AcquireAsync(
            dbContext, holdTargets, cancellationToken).ConfigureAwait(false);
        await EnsureScheduledCoreAsync(eventVersionIds, holdScope.Targets, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureScheduledCoreAsync(
        IReadOnlyList<Guid> eventVersionIds,
        IReadOnlyList<CentralTransientPayloadHoldTarget> holdTargets,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        foreach (var eventVersionId in eventVersionIds.Distinct())
        {
            var locked = await dbContext.Database.SqlQuery<int>(
                    $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientEventVersions] WITH (UPDLOCK, HOLDLOCK) WHERE [EventVersionId] = {eventVersionId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (locked != 1)
            {
                throw new CentralDerivativeJobStateException("The transient source event version was not found.");
            }
            if (await dbContext.CentralTransientDerivativeJobs.AnyAsync(item =>
                    item.SourceEventVersionId == eventVersionId &&
                    item.RecipeIdentitySha256 == CentralTransientDerivativeRuntime.RecipeIdentitySha256 &&
                    item.OptionsIdentitySha256 == CentralTransientDerivativeRuntime.OptionsIdentitySha256,
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var version = await dbContext.CentralTransientEventVersions.AsNoTracking()
                .Include(item => item.Event)
                .SingleAsync(item => item.EventVersionId == eventVersionId, cancellationToken)
                .ConfigureAwait(false);
            var eventLocked = await dbContext.Database.SqlQuery<int>(
                    $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientEvents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {version.CentralTransientEventId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (eventLocked != 1)
            {
                throw new CentralDerivativeJobStateException("The transient event was not found.");
            }
            if (await dbContext.CentralTransientPayloadReleases.AsNoTracking().AnyAsync(item =>
                    item.CentralTransientEventId == version.CentralTransientEventId &&
                    item.State != CentralTransientPayloadReleaseState.Failed, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            var parsed = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(version.CanonicalEventJson));
            var transientEvent = parsed.Value is not null && parsed.Validation.IsValid
                ? parsed.Value
                : throw new CentralDerivativeJobStateException("Persisted transient event history is invalid.");
            if (transientEvent.Observations.Count is < 1 or > 2)
            {
                continue;
            }

            var observationIds = transientEvent.Observations.Select(item => item.ObservationId).ToArray();
            var records = await dbContext.CentralTransientObservations.AsNoTracking()
                .Include(item => item.Source)!.ThenInclude(item => item!.Artifact)!.ThenInclude(item => item!.Frame)
                .Include(item => item.Backgrounds).ThenInclude(item => item.Artifact)!.ThenInclude(item => item!.Frame)
                .Where(item => item.CentralTransientEventId == version.CentralTransientEventId &&
                    observationIds.Contains(item.ObservationId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var byId = records.ToDictionary(item => item.ObservationId);
            var orderedRecords = transientEvent.Observations.Select(item => byId[item.ObservationId]).ToArray();
            var extractionIdentities = orderedRecords.Select(item => item.ExtractionReceiptIdentitySha256)
                .Distinct(StringComparer.Ordinal).ToArray();
            var extractionSources = await dbContext.CentralTransientExtractionSources.AsNoTracking()
                .Include(item => item.ExtractionReceipt)
                .Include(item => item.Artifact)!.ThenInclude(item => item!.Frame)
                .Where(item => extractionIdentities.Contains(item.ExtractionReceipt!.ExtractionIdentitySha256))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var artifacts = orderedRecords.SelectMany(observation => extractionSources
                    .Where(source => string.Equals(
                        source.ExtractionReceipt!.ExtractionIdentitySha256,
                        observation.ExtractionReceiptIdentitySha256,
                        StringComparison.Ordinal))
                    .OrderBy(source => source.Ordinal)
                    .Select(source => source.Artifact!))
                .DistinctBy(item => item.Id)
                .ToArray();
            if (artifacts.Any(item => item.ObjectState != CentralArtifactObjectState.Available ||
                    item.ReconstructionState != CentralReconstructionState.Complete) ||
                artifacts.Sum(item => item.ByteLength) > CentralTransientDerivativeRuntime.MaximumFrozenInputBytes)
            {
                continue;
            }
            var artifactIds = artifacts.Select(item => item.Id).ToHashSet();
            var selectedHoldTargets = holdTargets.Where(target => artifactIds.Contains(target.RecordId)).ToArray();
            if (selectedHoldTargets.Length != artifactIds.Count)
            {
                throw new CentralTransientPayloadHoldRejectedException("transient-retention.hold-target-changed");
            }
            await CentralTransientPayloadHoldFence.ValidateAsync(
                dbContext, selectedHoldTargets, cancellationToken).ConfigureAwait(false);

            var requestJson = CreateRequestJson(version, transientEvent, orderedRecords);
            var requestBytes = Encoding.UTF8.GetBytes(requestJson);
            var requestSha256 = ProcessingIdentity.ComputePayloadSha256(requestBytes);
            var requestIdentity = CreateRequestIdentity(version, requestSha256);
            var source = orderedRecords[0].Source!.Artifact!;
            var job = new CentralDerivativeJob
            {
                SourceCentralArtifactId = source.Id,
                TargetRole = FrameArtifactRole.Combined,
                TargetRecipeVersion = CentralTransientDerivativeRuntime.RecipeVersion,
                TargetVariant = "event-reconstruction",
                RecipeName = CentralTransientDerivativeRuntime.RecipeName,
                RecipeOptionsJson = CentralTransientDerivativeRuntime.OptionsJson,
                InputSelectorJson = "{}",
                RequestedRecipeIdentitySha256 = CentralTransientDerivativeRuntime.RecipeIdentitySha256,
                ExpectedRecipeIdentitySha256 = CentralTransientDerivativeRuntime.RecipeIdentitySha256,
                RequestIdentitySha256 = requestIdentity,
                Status = CentralDerivativeJobStatus.Pending,
                AttemptCount = 0,
                MaxAttempts = CentralTransientDerivativeRuntime.MaximumAttempts,
                AvailableAtUtc = version.VersionCreatedUtc,
                ResolutionCompletedAtUtc = version.VersionCreatedUtc,
                CreatedAtUtc = version.VersionCreatedUtc,
                UpdatedAtUtc = version.VersionCreatedUtc,
                TraceParent = Activity.Current?.Id,
                TraceState = Activity.Current?.TraceStateString
            };
            for (var ordinal = 0; ordinal < artifacts.Length; ordinal++)
            {
                var artifact = artifacts[ordinal];
                var requirement = new CentralDerivativeJobInputRequirement
                {
                    Job = job,
                    CentralDerivativeJobId = job.Id,
                    Ordinal = ordinal,
                    BindingName = $"event-source-{ordinal}",
                    SourceKind = CentralDerivativeInputSourceKind.Artifact,
                    IsRequired = true,
                    SelectorJson = "{}",
                    CompatibilityMode = CentralDerivativeCompatibilityMode.None,
                    ExpectedAgentId = artifact.Frame!.AgentId,
                    ExpectedRigId = artifact.Frame.RigId,
                    ExpectedCaptureSequence = artifact.Frame.CaptureSequence,
                    ExpectedCentralArtifactId = artifact.Id,
                    ResolutionState = CentralDerivativeInputResolutionState.Resolved,
                    ResolvedAtUtc = version.VersionCreatedUtc
                };
                job.InputRequirements.Add(requirement);
                var compatibility = CentralDerivativeWindowCompatibility.EmptySnapshot;
                job.Inputs.Add(new CentralDerivativeJobInput
                {
                    Job = job,
                    CentralDerivativeJobId = job.Id,
                    Requirement = requirement,
                    CentralDerivativeJobInputRequirementId = requirement.Id,
                    Ordinal = ordinal,
                    CentralArtifactId = artifact.Id,
                    CaptureSequence = artifact.Frame.CaptureSequence,
                    CompatibilityJson = compatibility.Json,
                    CompatibilitySha256 = compatibility.Sha256,
                    ByteLength = artifact.ByteLength,
                    SelectedAtUtc = version.VersionCreatedUtc
                });
            }
            var canonicalRequirement = new CentralDerivativeJobInputRequirement
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Ordinal = job.InputRequirements.Count,
                BindingName = "event-reconstruction-request",
                SourceKind = CentralDerivativeInputSourceKind.Canonical,
                IsRequired = true,
                SelectorJson = "{}",
                CompatibilityMode = CentralDerivativeCompatibilityMode.None,
                ExpectedAgentId = transientEvent.AgentId,
                ResolutionState = CentralDerivativeInputResolutionState.Resolved,
                ResolvedAtUtc = version.VersionCreatedUtc
            };
            job.InputRequirements.Add(canonicalRequirement);
            job.CanonicalInputs.Add(new CentralDerivativeJobCanonicalInput
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Requirement = canonicalRequirement,
                CentralDerivativeJobInputRequirementId = canonicalRequirement.Id,
                Ordinal = canonicalRequirement.Ordinal,
                SchemaVersion = CentralTransientDerivativeRuntime.RequestSchemaVersion,
                IdentitySha256 = requestSha256,
                CanonicalJson = requestJson,
                ByteLength = requestBytes.Length,
                SelectedAtUtc = version.VersionCreatedUtc
            });
            job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(
                job.Inputs, job.CanonicalInputs);
            var derivativeJob = new CentralTransientDerivativeJob
            {
                CentralDerivativeJobId = job.Id,
                Job = job,
                CentralTransientEventId = version.CentralTransientEventId,
                SourceEventVersionId = version.EventVersionId,
                RequestIdentitySha256 = requestIdentity,
                ProducerSchemaVersion = CentralTransientDerivativeRuntime.ProducerSchemaVersion,
                ProducerName = CentralTransientDerivativeRuntime.ProducerName,
                ProducerVersion = CentralTransientDerivativeRuntime.ProducerVersion,
                RecipeIdentitySha256 = CentralTransientDerivativeRuntime.RecipeIdentitySha256,
                OptionsIdentitySha256 = CentralTransientDerivativeRuntime.OptionsIdentitySha256,
                CanonicalRequestJson = requestJson,
                CanonicalRequestSha256 = requestSha256,
                CanonicalRequestByteLength = requestBytes.Length,
                ExpectedOutputCount = 5,
                CreatedAtUtc = version.VersionCreatedUtc
            };
            dbContext.CentralDerivativeJobs.Add(job);
            dbContext.CentralTransientDerivativeJobs.Add(derivativeJob);
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string CreateRequestJson(
        CentralTransientEventVersionRecord version,
        TransientEventV1 transientEvent,
        IReadOnlyList<CentralTransientObservationRecord> records)
    {
        var byId = records.ToDictionary(item => item.ObservationId);
        return CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            schemaVersion = CentralTransientDerivativeRuntime.RequestSchemaVersion,
            eventVersionId = version.EventVersionId,
            eventVersionSha256 = version.CanonicalEventSha256,
            recipeIdentitySha256 = CentralTransientDerivativeRuntime.RecipeIdentitySha256,
            optionsIdentitySha256 = CentralTransientDerivativeRuntime.OptionsIdentitySha256,
            observations = transientEvent.Observations.Select(observation => new
            {
                observation.ObservationId,
                observation.Ordinal,
                observation.Source.EvidenceId,
                sourceArtifact = observation.Source.Locator.Artifact,
                backgroundArtifacts = observation.BackgroundArtifacts,
                observation.Geometry.Bounds,
                observation.Provenance,
                extractionRecipeIdentitySha256 = observation.Extraction.RecipeIdentitySha256,
                extractionReceiptIdentitySha256 = byId[observation.ObservationId].ExtractionReceiptIdentitySha256,
                maskIdentity = byId[observation.ObservationId].MaskIdentity,
                normalizedSourceCentralArtifactId = byId[observation.ObservationId].Source!.CentralArtifactId,
                normalizedBackgroundCentralArtifactIds = byId[observation.ObservationId].Backgrounds
                    .OrderBy(item => item.Ordinal).Select(item => item.CentralArtifactId)
            }),
            activeAssessmentId = transientEvent.Assessments[^1].AssessmentId,
            latestReviewId = transientEvent.Reviews.LastOrDefault(item =>
                item.AssessmentId == transientEvent.Assessments[^1].AssessmentId)?.ReviewId
        })).GetRawText();
    }

    private static string CreateRequestIdentity(
        CentralTransientEventVersionRecord version,
        string requestSha256)
    {
        var value = string.Join('\n',
            CentralTransientDerivativeRuntime.RequestSchemaVersion,
            version.CentralTransientEventId.ToString("N"),
            version.EventVersionId.ToString("N"),
            version.CanonicalEventSha256,
            CentralTransientDerivativeRuntime.RecipeIdentitySha256,
            CentralTransientDerivativeRuntime.OptionsIdentitySha256,
            requestSha256);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
