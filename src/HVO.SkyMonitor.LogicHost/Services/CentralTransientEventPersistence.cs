using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralTransientEventPersistence
{
    Task<CentralTransientPersistenceCommit> AppendAsync(
        CentralTransientPersistenceRequest request,
        CancellationToken cancellationToken);
}

internal sealed record CentralTransientCanonicalPayload(
    ReadOnlyMemory<byte> Utf8Json,
    string Sha256,
    int ByteLength);

internal sealed record CentralTransientPersistenceRequest(
    Guid CentralDerivativeJobId,
    CentralTransientCanonicalPayload ExtractionReceipt,
    IReadOnlyList<CentralTransientCanonicalPayload> Events,
    IReadOnlyList<CentralTransientCanonicalPayload> AssessmentReceipts);

internal sealed record CentralTransientPersistenceCommit(
    Guid CentralDerivativeJobId,
    IReadOnlyList<Guid> EventVersionIds,
    IReadOnlyList<Guid> AssessmentIds);

internal sealed class CentralTransientPersistenceException : InvalidOperationException
{
    public CentralTransientPersistenceException()
        : this(CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload, "Transient persistence failed.")
    {
    }

    public CentralTransientPersistenceException(string message)
        : this(CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload, message)
    {
    }

    public CentralTransientPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload;
    }

    public CentralTransientPersistenceException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

internal static class CentralTransientPersistenceReasonCodes
{
    public const string InvalidCanonicalPayload = "transient-persistence.invalid-canonical-payload";
    public const string InvalidIdentityBinding = "transient-persistence.invalid-identity-binding";
    public const string InvalidHistory = "transient-persistence.invalid-history";
    public const string InvalidArtifactLineage = "transient-persistence.invalid-artifact-lineage";
    public const string ReplayConflict = "transient-persistence.replay-conflict";
    public const string JobInputConflict = "transient-persistence.job-input-conflict";
    public const string JobNotFound = "transient-persistence.job-not-found";
}

internal sealed class CentralTransientEventPersistence(ApplicationDbContext dbContext)
    : ICentralTransientEventPersistence
{
    private const string ExtractionOptionsSchema = "transient-candidate-extraction-options-v1";

    public async Task<CentralTransientPersistenceCommit> AppendAsync(
        CentralTransientPersistenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CentralDerivativeJobId == Guid.Empty || request.Events is null ||
            request.AssessmentReceipts is null)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding,
                "A validation job, event payload list, and assessment receipt list are required.");
        }

        var extraction = ParseExtraction(request.ExtractionReceipt);
        var events = request.Events.Select(ParseEvent).ToArray();
        var assessmentReceipts = request.AssessmentReceipts.Select(ParseAssessment).ToArray();
        if (events.Any(item => item.Value.Reviews.Count != 0 || item.Value.Notifications.Count != 0 ||
                item.Value.Derivatives.Count != 0))
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload,
                "Review, notification, and derivative history belongs to the later review boundary.");
        }

        await using var ownedTransaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        if (await AcquireValidationJobLockAsync(request.CentralDerivativeJobId, cancellationToken).ConfigureAwait(false) != 1)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.JobNotFound, "Transient validation job was not found.");
        }
        var validationJob = await dbContext.CentralTransientValidationJobs
            .Include(item => item.IdentitySlots)
            .Include(item => item.ExtractionReceipt)
            .SingleOrDefaultAsync(item => item.CentralDerivativeJobId == request.CentralDerivativeJobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure(CentralTransientPersistenceReasonCodes.JobNotFound, "Transient validation job was not found.");
        ValidateExecutionOptions(validationJob, extraction.Value, assessmentReceipts);
        var job = await dbContext.CentralDerivativeJobs
            .Include(item => item.InputRequirements)
            .Include(item => item.Inputs)
            .Include(item => item.CanonicalInputs)
            .SingleAsync(item => item.Id == request.CentralDerivativeJobId, cancellationToken).ConfigureAwait(false);
        ValidateJobBinding(job, validationJob.AgentId, extraction.Value);
        if (validationJob.CommittedAtUtc is not null || validationJob.ExtractionReceipt is not null)
        {
            var existing = await AdoptExistingCommitAsync(
                validationJob, extraction, events, assessmentReceipts, cancellationToken).ConfigureAwait(false);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return existing;
        }

        var slots = validationJob.IdentitySlots.OrderBy(item => item.Ordinal).ToArray();
        await ValidateIdentityBindingsAsync(
            validationJob, slots, extraction.Value, events, assessmentReceipts, cancellationToken)
            .ConfigureAwait(false);
        var artifacts = await ResolveAndLockArtifactsAsync(
            validationJob.AgentId, job.Inputs, extraction.Value, events.Select(item => item.Value), cancellationToken)
            .ConfigureAwait(false);

        var eventRecords = new Dictionary<Guid, CentralTransientEventRecord>();
        var versionRecords = new List<CentralTransientEventVersionRecord>(events.Length);
        var pendingVersionObservations = new List<CentralTransientEventVersionObservation>();
        var pendingVersionAssessments = new List<CentralTransientEventVersionAssessment>();
        var pendingAssessmentObservations = new List<CentralTransientAssessmentObservation>();
        var assessmentPayloadById = assessmentReceipts.ToDictionary(item => item.Value.Assessment.AssessmentId);
        var persistedEventEvidence = new List<PersistedEventEvidence>(events.Length);

        foreach (var eventPayload in events)
        {
            var transientEvent = eventPayload.Value;
            var eventRecord = await PrepareEventAsync(transientEvent, cancellationToken).ConfigureAwait(false);
            eventRecords.Add(transientEvent.EventId, eventRecord);
            _ = await LoadPreviousSnapshotAsync(eventRecord, transientEvent, cancellationToken)
                .ConfigureAwait(false);
            var existingObservationIds = await dbContext.CentralTransientObservations.AsNoTracking()
                .Where(item => item.CentralTransientEventId == eventRecord.Id)
                .Select(item => item.ObservationId).ToHashSetAsync(cancellationToken).ConfigureAwait(false);
            var existingAssessmentIds = await dbContext.CentralTransientAssessments.AsNoTracking()
                .Where(item => item.CentralTransientEventId == eventRecord.Id)
                .Select(item => item.AssessmentId).ToHashSetAsync(cancellationToken).ConfigureAwait(false);

            foreach (var observation in transientEvent.Observations)
            {
                if (!existingObservationIds.Contains(observation.ObservationId))
                {
                    var sourceArtifact = GetArtifact(artifacts, observation.Source.Locator.Artifact.ArtifactId);
                    var observationRecord = CreateObservation(eventRecord.Id, observation, extraction.Value, sourceArtifact,
                        artifacts);
                    dbContext.CentralTransientObservationSources.Add(observationRecord.Source!);
                    dbContext.CentralTransientObservations.Add(observationRecord);
                }
                pendingVersionObservations.Add(new CentralTransientEventVersionObservation
                {
                    CentralTransientEventId = eventRecord.Id,
                    EventVersionId = transientEvent.EventVersionId,
                    Ordinal = observation.Ordinal,
                    ObservationId = observation.ObservationId
                });
            }

            var assessmentCreatedUtc = await dbContext.CentralTransientAssessments.AsNoTracking()
                .Where(item => item.CentralTransientEventId == eventRecord.Id)
                .ToDictionaryAsync(item => item.AssessmentId, item => item.CreatedUtc, cancellationToken)
                .ConfigureAwait(false);
            for (var assessmentOrdinal = 0; assessmentOrdinal < transientEvent.Assessments.Count; assessmentOrdinal++)
            {
                var assessment = transientEvent.Assessments[assessmentOrdinal];
                if (!existingAssessmentIds.Contains(assessment.AssessmentId))
                {
                    if (!assessmentPayloadById.TryGetValue(assessment.AssessmentId, out var receipt))
                    {
                        throw Failure(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding,
                            "Every new assessment requires its exact canonical execution receipt.");
                    }
                    DateTimeOffset? supersededCreatedUtc = null;
                    if (assessment.SupersedesAssessmentId is { } predecessor)
                    {
                        if (!assessmentCreatedUtc.TryGetValue(predecessor, out var predecessorCreatedUtc))
                        {
                            throw Failure(CentralTransientPersistenceReasonCodes.InvalidHistory,
                                "Assessment predecessor was not committed earlier in the same event.");
                        }
                        supersededCreatedUtc = predecessorCreatedUtc;
                    }
                    var assessmentRecord = CreateAssessment(eventRecord.Id, assessment, receipt, supersededCreatedUtc);
                    dbContext.CentralTransientAssessments.Add(assessmentRecord);
                    assessmentCreatedUtc.Add(assessment.AssessmentId, assessment.CreatedUtc);
                    for (var ordinal = 0; ordinal < assessment.EvidenceObservationIds.Count; ordinal++)
                    {
                        pendingAssessmentObservations.Add(new CentralTransientAssessmentObservation
                        {
                            CentralTransientEventId = eventRecord.Id,
                            AssessmentId = assessment.AssessmentId,
                            Ordinal = ordinal,
                            ObservationId = assessment.EvidenceObservationIds[ordinal]
                        });
                    }
                }
                pendingVersionAssessments.Add(new CentralTransientEventVersionAssessment
                {
                    CentralTransientEventId = eventRecord.Id,
                    EventVersionId = transientEvent.EventVersionId,
                    Ordinal = assessmentOrdinal,
                    AssessmentId = assessment.AssessmentId
                });
            }

            var versionRecord = CreateVersion(eventRecord.Id, transientEvent, eventPayload);
            dbContext.CentralTransientEventVersions.Add(versionRecord);
            versionRecords.Add(versionRecord);
            persistedEventEvidence.Add(new PersistedEventEvidence(
                transientEvent.EventId,
                transientEvent.EventVersionId,
                transientEvent.State,
                eventPayload.Sha256));
        }

        if (events.Length == 0 && extraction.Value.CenteredContextConverged &&
            validationJob.ProvisionalCentralDerivativeJobId.HasValue)
        {
            await AppendCenteredNoCandidateRejectionsAsync(
                validationJob,
                extraction.Value,
                versionRecords,
                pendingVersionObservations,
                pendingVersionAssessments,
                persistedEventEvidence,
                cancellationToken).ConfigureAwait(false);
        }

        var extractionRecord = CreateExtractionReceipt(validationJob.CentralDerivativeJobId, extraction, artifacts);
        dbContext.CentralTransientExtractionReceipts.Add(extractionRecord);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        dbContext.AddRange(pendingVersionObservations);
        dbContext.AddRange(pendingVersionAssessments);
        dbContext.AddRange(pendingAssessmentObservations);
        var candidateCount = extraction.Value.Candidates.Count;
        for (var ordinal = 0; ordinal < slots.Length; ordinal++)
        {
            var slot = slots[ordinal];
            if (ordinal >= candidateCount)
            {
                slot.State = CentralTransientValidationIdentitySlotState.Unused;
                continue;
            }
            var effectiveEventId = slot.AdoptedEventId ?? slot.SubmittedEventId;
            var eventRecord = eventRecords[effectiveEventId];
            var version = versionRecords.Single(item => item.CentralTransientEventId == eventRecord.Id);
            slot.State = CentralTransientValidationIdentitySlotState.Committed;
            slot.CentralTransientEventId = eventRecord.Id;
            slot.PersistedEventId = effectiveEventId;
            slot.PersistedEventVersionId = version.EventVersionId;
            slot.PersistedObservationId = slot.ObservationId;
            slot.PersistedAssessmentId = slot.AssessmentId;
        }
        validationJob.CommittedAtUtc = versionRecords.Count == 0
            ? extraction.Value.OrderedSources.Max(item => item.Source.ObservationEndedUtc)
            : versionRecords.Max(item => item.VersionCreatedUtc);
        var outcomeState = ResolveOutcomeState(
            events.Select(item => item.Value.State).ToArray(), extraction.Value.CenteredContextConverged);
        var outcomeReason = events.Length == 0
            ? TransientCandidateExtractionReasonCodes.NoCandidate
            : CentralTransientRuntimeReasonCodes.Persisted;
        var outcomeEvidence = CanonicalJson(new
        {
            schemaVersion = "central-transient-validation-outcome-v1",
            state = outcomeState,
            reasonCode = outcomeReason,
            extractionIdentitySha256 = extraction.Value.ExtractionIdentitySha256,
            extractionReceiptSha256 = extraction.Sha256,
            events = persistedEventEvidence
        });
        validationJob.OutcomeState = outcomeState;
        validationJob.OutcomeReasonCode = outcomeReason;
        validationJob.OutcomeEvidenceJson = outcomeEvidence;
        validationJob.OutcomeEvidenceIdentitySha256 = ProcessingIdentity.ComputePayloadSha256(
            Encoding.UTF8.GetBytes(outcomeEvidence));
        validationJob.OutcomeRecordedAtUtc = validationJob.CommittedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [CentralTransientValidationOutcomeVersions]
                ([Id], [CentralDerivativeJobId], [Version], [State], [ReasonCode], [EvidenceJson],
                 [EvidenceIdentitySha256], [RecordedAtUtc])
            VALUES ({Guid.NewGuid()}, {validationJob.CentralDerivativeJobId}, {1}, {outcomeState.ToString()},
                    {outcomeReason}, {outcomeEvidence}, {validationJob.OutcomeEvidenceIdentitySha256},
                    {validationJob.OutcomeRecordedAtUtc.Value})
            """, cancellationToken).ConfigureAwait(false);
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var committedSlots = slots.Take(candidateCount).ToArray();
        return new CentralTransientPersistenceCommit(
            validationJob.CentralDerivativeJobId,
            candidateCount == 0
                ? versionRecords.Select(item => item.EventVersionId).ToArray()
                : committedSlots.Select(slot =>
                {
                    var eventRecordId = eventRecords[slot.AdoptedEventId ?? slot.SubmittedEventId].Id;
                    return versionRecords.Single(item => item.CentralTransientEventId == eventRecordId).EventVersionId;
                }).ToArray(),
            committedSlots.Select(item => item.AssessmentId).ToArray());
    }

    private async Task AppendCenteredNoCandidateRejectionsAsync(
        CentralTransientValidationJob validationJob,
        TransientCandidateExtractionDescriptorV1 extraction,
        List<CentralTransientEventVersionRecord> versionRecords,
        List<CentralTransientEventVersionObservation> pendingVersionObservations,
        List<CentralTransientEventVersionAssessment> pendingVersionAssessments,
        List<PersistedEventEvidence> persistedEventEvidence,
        CancellationToken cancellationToken)
    {
        var eventRecordIds = await dbContext.CentralTransientValidationIdentitySlots.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == validationJob.ProvisionalCentralDerivativeJobId &&
                item.State == CentralTransientValidationIdentitySlotState.Committed &&
                item.CentralTransientEventId != null)
            .Select(item => item.CentralTransientEventId!.Value)
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var receiptCreatedUtc = extraction.OrderedSources.Max(item => item.Source.ObservationEndedUtc).AddTicks(1);
        foreach (var eventRecordId in eventRecordIds)
        {
            var latest = await dbContext.CentralTransientEventVersions.AsNoTracking()
                .Where(item => item.CentralTransientEventId == eventRecordId &&
                    item.Event!.AgentId == validationJob.AgentId)
                .OrderByDescending(item => item.Version)
                .FirstAsync(cancellationToken).ConfigureAwait(false);
            var parsed = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(latest.CanonicalEventJson));
            var prior = parsed.Value ?? throw Failure(
                CentralTransientPersistenceReasonCodes.InvalidHistory,
                $"Persisted transient event failed canonical parsing: {parsed.Validation.ReasonCode}.");
            var next = prior with
            {
                EventVersionId = Guid.NewGuid(),
                Version = prior.Version + 1,
                PreviousEventVersionId = prior.EventVersionId,
                PreviousVersionCreatedUtc = prior.VersionCreatedUtc,
                State = TransientEventState.Rejected,
                VersionCreatedUtc = receiptCreatedUtc > prior.VersionCreatedUtc
                    ? receiptCreatedUtc
                    : prior.VersionCreatedUtc.AddTicks(1)
            };
            var canonical = TransientContractJson.Serialize(next);
            var payload = new ValidatedPayload<TransientEventV1>(
                next,
                Encoding.UTF8.GetString(canonical),
                Convert.ToHexString(SHA256.HashData(canonical)),
                canonical.Length);
            var version = CreateVersion(eventRecordId, next, payload);
            dbContext.CentralTransientEventVersions.Add(version);
            versionRecords.Add(version);
            foreach (var observation in next.Observations)
            {
                pendingVersionObservations.Add(new CentralTransientEventVersionObservation
                {
                    CentralTransientEventId = eventRecordId,
                    EventVersionId = next.EventVersionId,
                    Ordinal = observation.Ordinal,
                    ObservationId = observation.ObservationId
                });
            }
            for (var ordinal = 0; ordinal < next.Assessments.Count; ordinal++)
            {
                pendingVersionAssessments.Add(new CentralTransientEventVersionAssessment
                {
                    CentralTransientEventId = eventRecordId,
                    EventVersionId = next.EventVersionId,
                    Ordinal = ordinal,
                    AssessmentId = next.Assessments[ordinal].AssessmentId
                });
            }
            persistedEventEvidence.Add(new PersistedEventEvidence(
                next.EventId,
                next.EventVersionId,
                next.State,
                payload.Sha256));
        }
    }

    private async Task<int> AcquireValidationJobLockAsync(Guid jobId, CancellationToken cancellationToken)
        => await dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientValidationJobs] WITH (UPDLOCK, HOLDLOCK) WHERE [CentralDerivativeJobId] = {jobId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private static void ValidateJobBinding(
        CentralDerivativeJob job,
        string agentId,
        TransientCandidateExtractionDescriptorV1 extraction)
    {
        var inputs = job.Inputs.OrderBy(item => item.Ordinal).ToArray();
        var requirements = job.InputRequirements.ToDictionary(item => item.Id);
        var expectedRecipeIdentity = TransientCandidateExtractionFactory.ComputeRecipeIdentitySha256(extraction.Options);
        if (inputs.Length != extraction.OrderedSources.Count ||
            inputs.Select(item => item.Ordinal).SequenceEqual(Enumerable.Range(0, inputs.Length)) is false ||
            !string.Equals(job.InputSetIdentitySha256, CentralDerivativeWindowIdentity.CreateInputSetIdentity(inputs),
                StringComparison.Ordinal) ||
            !string.Equals(job.RequestedRecipeIdentitySha256, expectedRecipeIdentity, StringComparison.Ordinal) ||
            !string.Equals(job.ExpectedRecipeIdentitySha256, expectedRecipeIdentity, StringComparison.Ordinal) ||
            !string.Equals(job.RecipeOptionsJson, CanonicalJson(extraction.Options), StringComparison.Ordinal) ||
            job.CanonicalInputs.Count != 1)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.JobInputConflict,
                "Transient receipt recipe, options, or input-set identity differs from the durable job selection.");
        }
        var canonicalInput = job.CanonicalInputs.Single();
        var canonicalOptionsJson = CanonicalJson(new { schema = ExtractionOptionsSchema, options = extraction.Options });
        if (!requirements.TryGetValue(canonicalInput.CentralDerivativeJobInputRequirementId, out var canonicalRequirement) ||
            !canonicalRequirement.IsRequired || canonicalRequirement.SourceKind != CentralDerivativeInputSourceKind.Canonical ||
            canonicalRequirement.ResolutionState != CentralDerivativeInputResolutionState.Resolved ||
            !string.Equals(canonicalRequirement.ExpectedAgentId, agentId, StringComparison.Ordinal) ||
            canonicalInput.Ordinal != inputs.Length || canonicalRequirement.Ordinal != canonicalInput.Ordinal ||
            !string.Equals(canonicalInput.SchemaVersion, ExtractionOptionsSchema, StringComparison.Ordinal) ||
            !string.Equals(canonicalInput.IdentitySha256, extraction.OptionsIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(canonicalInput.CanonicalJson, canonicalOptionsJson, StringComparison.Ordinal) ||
            canonicalInput.ByteLength != Encoding.UTF8.GetByteCount(canonicalOptionsJson) ||
            !string.Equals(canonicalInput.IdentitySha256,
                ProcessingIdentity.ComputePayloadSha256(Encoding.UTF8.GetBytes(canonicalInput.CanonicalJson)),
                StringComparison.Ordinal))
        {
            throw Failure(CentralTransientPersistenceReasonCodes.JobInputConflict,
                "Transient options identity differs from the required canonical job input.");
        }
        for (var ordinal = 0; ordinal < inputs.Length; ordinal++)
        {
            var input = inputs[ordinal];
            if (!requirements.TryGetValue(input.CentralDerivativeJobInputRequirementId, out var requirement) ||
                !requirement.IsRequired || requirement.SourceKind != CentralDerivativeInputSourceKind.Artifact ||
                requirement.ResolutionState != CentralDerivativeInputResolutionState.Resolved ||
                requirement.Ordinal != input.Ordinal ||
                !string.Equals(requirement.ExpectedAgentId, agentId, StringComparison.Ordinal) ||
                requirement.ExpectedCentralArtifactId is { } expectedArtifactId &&
                    expectedArtifactId != input.CentralArtifactId ||
                requirement.ExpectedCaptureSequence != input.CaptureSequence ||
                !string.Equals(input.CompatibilitySha256,
                    ProcessingIdentity.ComputePayloadSha256(Encoding.UTF8.GetBytes(input.CompatibilityJson)),
                    StringComparison.Ordinal))
            {
                throw Failure(CentralTransientPersistenceReasonCodes.JobInputConflict,
                    "Transient ordered sources differ from the resolved durable job inputs.");
            }
        }
    }

    private static void ValidateExecutionOptions(
        CentralTransientValidationJob validationJob,
        TransientCandidateExtractionDescriptorV1 extraction,
        IReadOnlyList<ValidatedPayload<TransientAssessmentExecutionDescriptorV1>> assessments)
    {
        CentralTransientExecutionOptionsV1 execution;
        try
        {
            execution = CentralTransientExecutionOptionsJson.Deserialize(validationJob.ExecutionOptionsJson
                ?? throw new JsonException("Durable transient execution options are missing."));
        }
        catch (JsonException exception)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.JobInputConflict, exception.Message);
        }
        var canonical = CentralTransientExecutionOptionsJson.Serialize(execution);
        if (!string.Equals(execution.SchemaVersion, CentralTransientExecutionOptionsV1.CurrentSchemaVersion,
                StringComparison.Ordinal) ||
            !string.Equals(canonical.Json, validationJob.ExecutionOptionsJson, StringComparison.Ordinal) ||
            !string.Equals(canonical.Sha256, validationJob.ExecutionOptionsIdentitySha256, StringComparison.Ordinal) ||
            !CanonicalEqual(execution.Extraction, extraction.Options) ||
            assessments.Any(item => !CanonicalEqual(execution.Assessment, item.Value.Options)))
        {
            throw Failure(CentralTransientPersistenceReasonCodes.JobInputConflict,
                "Transient extraction, assessment, association, or mask policy differs from the durable job selection.");
        }
    }

    private async Task<CentralTransientPersistenceCommit> AdoptExistingCommitAsync(
        CentralTransientValidationJob validationJob,
        ValidatedPayload<TransientCandidateExtractionDescriptorV1> extraction,
        IReadOnlyList<ValidatedPayload<TransientEventV1>> events,
        IReadOnlyList<ValidatedPayload<TransientAssessmentExecutionDescriptorV1>> assessments,
        CancellationToken cancellationToken)
    {
        var slots = validationJob.IdentitySlots.OrderBy(item => item.Ordinal).ToArray();
        var candidateIds = extraction.Value.Candidates.Select(item => item.CandidateId).ToArray();
        var candidateEventIds = extraction.Value.Candidates.Select(item => item.EventId).ToArray();
        var eventIds = events.Select(item => item.Value.EventId).ToArray();
        var eventVersionIds = events.Select(item => item.Value.EventVersionId).ToArray();
        var assessmentIds = assessments.Select(item => item.Value.Assessment.AssessmentId).ToArray();
        var committedSlotIds = slots.Take(extraction.Value.Candidates.Count).ToArray();
        if (validationJob.CommittedAtUtc is null || validationJob.ExtractionReceipt is null ||
            !SamePayload(validationJob.ExtractionReceipt.CanonicalReceiptSha256,
                validationJob.ExtractionReceipt.CanonicalReceiptByteLength, extraction) ||
            validationJob.ExtractionReceipt.ExtractionIdentitySha256 != extraction.Value.ExtractionIdentitySha256 ||
            slots.Length < extraction.Value.Candidates.Count || events.Count != extraction.Value.Candidates.Count ||
            assessments.Count != extraction.Value.Candidates.Count ||
            candidateIds.Distinct().Count() != candidateIds.Length ||
            candidateEventIds.Distinct().Count() != candidateEventIds.Length ||
            eventIds.Distinct().Count() != eventIds.Length ||
            eventVersionIds.Distinct().Count() != eventVersionIds.Length ||
            assessmentIds.Distinct().Count() != assessmentIds.Length ||
            !candidateIds.ToHashSet().SetEquals(committedSlotIds.Select(item => item.CandidateId)) ||
            !candidateEventIds.ToHashSet().SetEquals(eventIds) ||
            !candidateEventIds.ToHashSet().SetEquals(committedSlotIds.Select(item =>
                item.AdoptedEventId ?? item.SubmittedEventId)) ||
            !assessmentIds.ToHashSet().SetEquals(committedSlotIds.Select(item => item.AssessmentId)))
        {
            throw ReplayConflict();
        }

        var eventById = events.ToDictionary(item => item.Value.EventId);
        var assessmentById = assessments.ToDictionary(item => item.Value.Assessment.AssessmentId);
        var persistedEventVersionIds = new List<Guid>(extraction.Value.Candidates.Count);
        var persistedAssessmentIds = new List<Guid>(extraction.Value.Candidates.Count);
        for (var ordinal = 0; ordinal < slots.Length; ordinal++)
        {
            var slot = slots[ordinal];
            if (ordinal >= extraction.Value.Candidates.Count)
            {
                if (slot.State != CentralTransientValidationIdentitySlotState.Unused ||
                    slot.CentralTransientEventId is not null || slot.PersistedEventVersionId is not null ||
                    slot.PersistedObservationId is not null || slot.PersistedAssessmentId is not null)
                {
                    throw ReplayConflict();
                }
                continue;
            }
            var candidate = extraction.Value.Candidates[ordinal];
            if (slot.State != CentralTransientValidationIdentitySlotState.Committed ||
                (slot.AdoptedEventId ?? slot.SubmittedEventId) != candidate.EventId ||
                slot.PersistedEventId != candidate.EventId || slot.CandidateId != candidate.CandidateId ||
                slot.PersistedObservationId != slot.ObservationId || slot.PersistedAssessmentId != slot.AssessmentId ||
                !eventById.TryGetValue(candidate.EventId, out var eventPayload) ||
                eventPayload.Value.EventVersionId != slot.PersistedEventVersionId ||
                !assessmentById.TryGetValue(slot.AssessmentId, out var assessmentPayload))
            {
                throw ReplayConflict();
            }
            var persistedVersion = await dbContext.CentralTransientEventVersions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.EventVersionId == slot.PersistedEventVersionId, cancellationToken)
                .ConfigureAwait(false);
            var persistedAssessment = await dbContext.CentralTransientAssessments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.AssessmentId == slot.PersistedAssessmentId, cancellationToken)
                .ConfigureAwait(false);
            if (persistedVersion is null || persistedAssessment is null ||
                !SamePayload(persistedVersion.CanonicalEventSha256, persistedVersion.CanonicalEventByteLength, eventPayload) ||
                !SamePayload(persistedAssessment.CanonicalReceiptSha256,
                    persistedAssessment.CanonicalReceiptByteLength, assessmentPayload) ||
                persistedAssessment.ExecutionIdentitySha256 != assessmentPayload.Value.ExecutionIdentitySha256)
            {
                throw ReplayConflict();
            }
            persistedEventVersionIds.Add(persistedVersion.EventVersionId);
            persistedAssessmentIds.Add(persistedAssessment.AssessmentId);
        }
        return new(validationJob.CentralDerivativeJobId, persistedEventVersionIds, persistedAssessmentIds);
    }

    private static bool SamePayload<T>(string sha256, int byteLength, ValidatedPayload<T> payload)
        => string.Equals(sha256, payload.Sha256, StringComparison.Ordinal) && byteLength == payload.ByteLength;

    private static CentralTransientPersistenceException ReplayConflict()
        => Failure(CentralTransientPersistenceReasonCodes.ReplayConflict,
            "Committed transient output conflicts with the replayed canonical receipt or persisted identities.");

    private static TransientEventState ResolveOutcomeState(
        TransientEventState[] states,
        bool centeredContextConverged)
    {
        if (states.Length == 0)
        {
            return centeredContextConverged ? TransientEventState.Rejected : TransientEventState.Pending;
        }
        if (states.All(static state => state == TransientEventState.Rejected))
        {
            return TransientEventState.Rejected;
        }
        if (states.Any(static state => state == TransientEventState.NeedsReview))
        {
            return TransientEventState.NeedsReview;
        }
        if (states.Any(static state => state == TransientEventState.Pending))
        {
            return TransientEventState.Pending;
        }
        return TransientEventState.Validated;
    }

    private async Task ValidateIdentityBindingsAsync(
        CentralTransientValidationJob validationJob,
        CentralTransientValidationIdentitySlot[] slots,
        TransientCandidateExtractionDescriptorV1 extraction,
        IReadOnlyList<ValidatedPayload<TransientEventV1>> events,
        IReadOnlyList<ValidatedPayload<TransientAssessmentExecutionDescriptorV1>> assessments,
        CancellationToken cancellationToken)
    {
        if (slots.Length < extraction.Candidates.Count || slots.Any(item => item.State != CentralTransientValidationIdentitySlotState.Reserved) ||
            events.Count != extraction.Candidates.Count || assessments.Count != extraction.Candidates.Count ||
            events.Select(item => item.Value.EventId).Distinct().Count() != events.Count ||
            assessments.Select(item => item.Value.Assessment.AssessmentId).Distinct().Count() != assessments.Count)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding,
                "Reserved identity slots and committed outputs do not form one atomic set.");
        }

        var eventById = events.ToDictionary(item => item.Value.EventId, item => item.Value);
        var assessmentById = assessments.ToDictionary(item => item.Value.Assessment.AssessmentId, item => item.Value);
        var requestedObservationIds = assessments.SelectMany(item => item.Value.OrderedObservationIds).Distinct().ToArray();
        var persistedExtractionIdentities = await dbContext.CentralTransientObservations.AsNoTracking()
            .Where(item => requestedObservationIds.Contains(item.ObservationId))
            .ToDictionaryAsync(
                item => item.ObservationId,
                item => item.ExtractionReceiptIdentitySha256,
                cancellationToken).ConfigureAwait(false);
        var currentObservationIds = slots.Take(extraction.Candidates.Count).Select(item => item.ObservationId).ToHashSet();
        for (var ordinal = 0; ordinal < extraction.Candidates.Count; ordinal++)
        {
            var slot = slots[ordinal];
            var candidate = extraction.Candidates[ordinal];
            var effectiveEventId = slot.AdoptedEventId ?? slot.SubmittedEventId;
            if (effectiveEventId != candidate.EventId || slot.CandidateId != candidate.CandidateId ||
                !string.Equals(candidate.AgentId, validationJob.AgentId, StringComparison.Ordinal) ||
                !eventById.TryGetValue(effectiveEventId, out var transientEvent) ||
                !string.Equals(transientEvent.AgentId, validationJob.AgentId, StringComparison.Ordinal) ||
                !assessmentById.TryGetValue(slot.AssessmentId, out var receipt) || receipt.EventId != effectiveEventId)
            {
                throw Failure(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding,
                    "Committed candidate, event, observation, or assessment identity differs from its reserved slot.");
            }
            var observation = transientEvent.Observations.SingleOrDefault(item => item.ObservationId == slot.ObservationId);
            var assessment = transientEvent.Assessments.SingleOrDefault(item => item.AssessmentId == slot.AssessmentId);
            if (observation is null || observation.Extraction.OriginatingCandidateId != slot.CandidateId ||
                assessment is null || !CanonicalEqual(assessment, receipt.Assessment))
            {
                throw Failure(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding,
                    "Reserved observation and assessment identities were not preserved in the event snapshot.");
            }
            var promoted = TransientObservationFactory.Create(new TransientObservationPromotionRequest(
                slot.CandidateId, slot.ObservationId, observation.Ordinal, extraction));
            if (!CanonicalEqual(promoted, observation))
            {
                throw Failure(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding,
                    "Event observation does not exactly match promotion from the extraction receipt.");
            }
            ValidateAssessmentLineage(
                receipt,
                transientEvent,
                extraction.ExtractionIdentitySha256,
                currentObservationIds,
                persistedExtractionIdentities);
        }
    }

    private static void ValidateAssessmentLineage(
        TransientAssessmentExecutionDescriptorV1 receipt,
        TransientEventV1 transientEvent,
        string extractionIdentitySha256,
        HashSet<Guid> currentObservationIds,
        Dictionary<Guid, string> persistedExtractionIdentities)
    {
        var observations = receipt.OrderedObservationIds.Select(id => transientEvent.Observations.Single(item =>
            item.ObservationId == id)).ToArray();
        var expectedObservationIdentities = observations.Select(observation =>
            CaptureContractJson.ComputeCanonicalJsonSha256(new TransientAssessmentObservationV1(
                transientEvent.EventId,
                currentObservationIds.Contains(observation.ObservationId)
                    ? extractionIdentitySha256
                    : persistedExtractionIdentities.TryGetValue(observation.ObservationId, out var persistedIdentity)
                        ? persistedIdentity
                        : string.Empty,
                observation))).ToArray();
        var priorAssessments = transientEvent.Assessments.TakeWhile(item => item.AssessmentId != receipt.Assessment.AssessmentId)
            .ToArray();
        var expectedPriorIdentities = priorAssessments.Select(assessment =>
            CaptureContractJson.ComputeCanonicalJsonSha256(assessment)).ToArray();
        if (!receipt.OrderedObservationIdentitySha256s.SequenceEqual(expectedObservationIdentities) ||
            !receipt.PriorAssessmentIds.SequenceEqual(priorAssessments.Select(item => item.AssessmentId)) ||
            !receipt.PriorAssessmentIdentitySha256s.SequenceEqual(expectedPriorIdentities))
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding,
                "Assessment receipt identities do not match normalized event evidence.");
        }
    }

    private async Task<CentralTransientEventRecord> PrepareEventAsync(
        TransientEventV1 transientEvent,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.CentralTransientEvents.SingleOrDefaultAsync(item =>
            item.AgentId == transientEvent.AgentId && item.EventId == transientEvent.EventId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            if (transientEvent.Version != 1 || transientEvent.PreviousEventVersionId is not null ||
                transientEvent.PreviousVersionCreatedUtc is not null)
            {
                throw Failure(CentralTransientPersistenceReasonCodes.InvalidHistory,
                    "A new event must begin with version one and no predecessor.");
            }
            existing = new CentralTransientEventRecord
            {
                AgentId = transientEvent.AgentId,
                EventId = transientEvent.EventId,
                EventCreatedUtc = transientEvent.EventCreatedUtc
            };
            dbContext.CentralTransientEvents.Add(existing);
            return existing;
        }
        if (existing.EventCreatedUtc != transientEvent.EventCreatedUtc)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidHistory,
                "Event creation time is immutable across versions.");
        }
        return existing;
    }

    private async Task<TransientEventV1?> LoadPreviousSnapshotAsync(
        CentralTransientEventRecord eventRecord,
        TransientEventV1 incoming,
        CancellationToken cancellationToken)
    {
        var previous = await dbContext.CentralTransientEventVersions.AsNoTracking()
            .Where(item => item.CentralTransientEventId == eventRecord.Id)
            .OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return null;
        }
        if (incoming.Version != previous.Version + 1 || incoming.PreviousEventVersionId != previous.EventVersionId ||
            incoming.PreviousVersionCreatedUtc != previous.VersionCreatedUtc)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidHistory,
                "Event versions must append to the actual latest N-1 predecessor.");
        }
        var parsed = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(previous.CanonicalEventJson));
        if (!parsed.Validation.IsValid || parsed.Value is null)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidHistory,
                "Previously committed canonical event history is invalid.");
        }
        if (!IsPrefix(parsed.Value.Observations, incoming.Observations) ||
            !IsPrefix(parsed.Value.Assessments, incoming.Assessments))
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidHistory,
                "A later event version cannot alter or remove prior observations or assessments.");
        }
        return parsed.Value;
    }

    private async Task<Dictionary<Guid, CentralArtifact>> ResolveAndLockArtifactsAsync(
        string agentId,
        IEnumerable<CentralDerivativeJobInput> selectedInputs,
        TransientCandidateExtractionDescriptorV1 extraction,
        IEnumerable<TransientEventV1> events,
        CancellationToken cancellationToken)
    {
        var inputs = selectedInputs.OrderBy(item => item.Ordinal).ToArray();
        var eventValues = events.ToArray();
        var claims = extraction.OrderedSources.Select(item => item.Source.Locator.Artifact)
            .Concat(eventValues.SelectMany(item => item.Observations).SelectMany(observation =>
                observation.BackgroundArtifacts.Prepend(observation.Source.Locator.Artifact))).ToArray();
        if (inputs.Length != extraction.OrderedSources.Count)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidArtifactLineage,
                "Every transient source must resolve to one durable job input.");
        }
        var historicalCentralIds = new HashSet<Guid>();
        foreach (var transientEvent in eventValues)
        {
            var observationIds = transientEvent.Observations.Select(item => item.ObservationId).ToArray();
            historicalCentralIds.UnionWith(await dbContext.CentralTransientObservations.AsNoTracking()
                .Where(item => item.Event!.AgentId == agentId && item.Event.EventId == transientEvent.EventId
                    && observationIds.Contains(item.ObservationId))
                .Select(item => item.Source!.CentralArtifactId)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
            historicalCentralIds.UnionWith(await dbContext.CentralTransientObservationBackgrounds.AsNoTracking()
                .Where(item => item.Observation!.Event!.AgentId == agentId
                    && item.Observation.Event.EventId == transientEvent.EventId
                    && observationIds.Contains(item.ObservationId))
                .Select(item => item.CentralArtifactId)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
        }
        var centralIds = inputs.Select(item => item.CentralArtifactId).Concat(historicalCentralIds).Distinct().ToArray();
        foreach (var centralId in centralIds.Order())
        {
            if (await CentralArtifactRetentionLock.AcquireAsync(dbContext, centralId, cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                throw Failure(CentralTransientPersistenceReasonCodes.InvalidArtifactLineage,
                    "A referenced artifact disappeared before its retention hold was committed.");
            }
        }
        var artifacts = await dbContext.CentralArtifacts.Include(item => item.Frame).Include(item => item.Recipe)
            .Where(item => centralIds.Contains(item.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (artifacts.Count != centralIds.Length)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidArtifactLineage,
                "A selected central artifact disappeared before its retention hold was committed.");
        }
        var byCentralId = artifacts.ToDictionary(item => item.Id);
        for (var ordinal = 0; ordinal < inputs.Length; ordinal++)
        {
            var artifact = byCentralId[inputs[ordinal].CentralArtifactId];
            var source = extraction.OrderedSources[ordinal].Source.Locator.Artifact;
            if (artifact.ArtifactId != source.ArtifactId || inputs[ordinal].ByteLength != artifact.ByteLength)
            {
                throw Failure(CentralTransientPersistenceReasonCodes.JobInputConflict,
                    "Transient ordered sources differ from the resolved durable job artifacts.");
            }
        }
        var byArtifactId = artifacts.ToDictionary(item => item.ArtifactId);
        foreach (var claim in claims)
        {
            if (!byArtifactId.TryGetValue(claim.ArtifactId, out var artifact))
            {
                throw Failure(CentralTransientPersistenceReasonCodes.InvalidArtifactLineage,
                    "Event evidence references an artifact outside the durable job input set.");
            }
            ValidateArtifact(artifact, agentId, claim);
        }
        return byArtifactId;
    }

    private static void ValidateArtifact(CentralArtifact artifact, string agentId, TransientArtifactReferenceV1 claim)
    {
        if (artifact.ObjectState != CentralArtifactObjectState.Available ||
            artifact.ReconstructionState != CentralReconstructionState.Complete || artifact.Frame is null ||
            !string.Equals(artifact.Frame.AgentId, agentId, StringComparison.Ordinal) || artifact.ArtifactId != claim.ArtifactId ||
            artifact.Role != claim.Role || !string.Equals(artifact.Variant, claim.Variant, StringComparison.Ordinal) ||
            !string.Equals(artifact.ChecksumSha256, claim.ChecksumSha256, StringComparison.OrdinalIgnoreCase) ||
            artifact.Recipe is null)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidArtifactLineage,
                "Transient artifact lineage does not match available central evidence.");
        }
        using var options = JsonDocument.Parse(artifact.Recipe.OptionsJson);
        var canonicalOptionsSha = CaptureContractJson.ComputeCanonicalJsonSha256(options.RootElement);
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(new RecipeIdentityDescriptor(
            artifact.Recipe.Name,
            artifact.Recipe.SemanticVersion,
            artifact.Recipe.ImplementationVersion,
            options.RootElement,
            artifact.Recipe.OptionsSha256)).IdentitySha256;
        if (!string.Equals(canonicalOptionsSha, artifact.Recipe.OptionsSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(recipeIdentity, claim.RecipeIdentitySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidArtifactLineage,
                "Transient artifact recipe lineage does not match central provenance.");
        }
    }

    private static CentralTransientObservationRecord CreateObservation(
        Guid eventRecordId,
        TransientObservationV1 observation,
        TransientCandidateExtractionDescriptorV1 extraction,
        CentralArtifact sourceArtifact,
        IReadOnlyDictionary<Guid, CentralArtifact> artifacts)
    {
        var source = observation.Source;
        var record = new CentralTransientObservationRecord
        {
            ObservationId = observation.ObservationId,
            CentralTransientEventId = eventRecordId,
            DetectorInputIdentitySha256 = observation.Provenance.DetectorInputIdentitySha256,
            CalibrationIdentity = observation.Provenance.CalibrationIdentity,
            MaskIdentity = observation.Provenance.MaskIdentity,
            ProcessingProfileIdentity = observation.Provenance.ProcessingProfileIdentity,
            OriginatingCandidateId = observation.Extraction.OriginatingCandidateId,
            ExtractionProducerSchemaVersion = observation.Extraction.Producer.SchemaVersion,
            ExtractionProducerKind = observation.Extraction.Producer.Kind,
            ExtractionProducerName = observation.Extraction.Producer.Name,
            ExtractionProducerVersion = observation.Extraction.Producer.Version,
            ExtractionRecipeIdentitySha256 = observation.Extraction.RecipeIdentitySha256,
            ExtractionReceiptIdentitySha256 = extraction.ExtractionIdentitySha256,
            GeometryJson = CanonicalJson(observation.Geometry),
            FeaturesJson = CanonicalJson(observation.Features),
            SourceReferenceId = observation.ObservationId,
            Source = CreateObservationSource(observation.ObservationId, source, sourceArtifact)
        };
        for (var ordinal = 0; ordinal < observation.BackgroundArtifacts.Count; ordinal++)
        {
            var background = observation.BackgroundArtifacts[ordinal];
            record.Backgrounds.Add(new CentralTransientObservationBackgroundReference
            {
                ObservationId = observation.ObservationId,
                Ordinal = ordinal,
                CentralArtifactId = GetArtifact(artifacts, background.ArtifactId).Id,
                ArtifactId = background.ArtifactId,
                ArtifactRole = background.Role,
                ArtifactVariant = background.Variant,
                ArtifactRecipeIdentitySha256 = background.RecipeIdentitySha256,
                ArtifactChecksumSha256 = background.ChecksumSha256
            });
        }
        return record;
    }

    private static CentralTransientObservationSourceReference CreateObservationSource(
        Guid observationId,
        TransientSourceEvidenceReferenceV1 source,
        CentralArtifact artifact)
        => new()
        {
            ObservationId = observationId,
            CentralArtifactId = artifact.Id,
            EvidenceSchemaVersion = source.SchemaVersion,
            EvidenceId = source.EvidenceId,
            LocatorSchemaVersion = source.Locator.SchemaVersion,
            LocatorKind = source.Locator.Kind,
            ArtifactId = source.Locator.Artifact.ArtifactId,
            ArtifactRole = source.Locator.Artifact.Role,
            ArtifactVariant = source.Locator.Artifact.Variant,
            ArtifactRecipeIdentitySha256 = source.Locator.Artifact.RecipeIdentitySha256,
            ArtifactChecksumSha256 = source.Locator.Artifact.ChecksumSha256,
            ObservationStartedUtc = source.ObservationStartedUtc,
            ObservationEndedUtc = source.ObservationEndedUtc,
            TimingQuality = source.TimingQuality,
            TimingProvenanceSource = source.TimingProvenance.Source,
            TimingProvenanceVersion = source.TimingProvenance.Version
        };

    private static CentralTransientAssessmentRecord CreateAssessment(
        Guid eventRecordId,
        TransientAssessmentV1 assessment,
        ValidatedPayload<TransientAssessmentExecutionDescriptorV1> receipt,
        DateTimeOffset? supersededCreatedUtc)
        => new()
        {
            AssessmentId = assessment.AssessmentId,
            CentralTransientEventId = eventRecordId,
            CreatedUtc = assessment.CreatedUtc,
            Authority = assessment.Authority,
            Classification = assessment.Classification,
            MeteorSeverity = assessment.MeteorSeverity,
            ConfidenceMillionths = assessment.ConfidenceMillionths,
            SupersedesAssessmentId = assessment.SupersedesAssessmentId,
            SupersedesAssessmentCreatedUtc = supersededCreatedUtc,
            ProducerSchemaVersion = assessment.Producer.SchemaVersion,
            ProducerKind = assessment.Producer.Kind,
            ProducerName = assessment.Producer.Name,
            ProducerVersion = assessment.Producer.Version,
            RecipeIdentitySha256 = assessment.RecipeIdentitySha256,
            ReceiptSchemaVersion = receipt.Value.SchemaVersion,
            ExecutionIdentitySha256 = receipt.Value.ExecutionIdentitySha256,
            OptionsIdentitySha256 = receipt.Value.OptionsIdentitySha256,
            CanonicalReceiptJson = receipt.Json,
            CanonicalReceiptSha256 = receipt.Sha256,
            CanonicalReceiptByteLength = receipt.ByteLength
        };

    private static CentralTransientEventVersionRecord CreateVersion(
        Guid eventRecordId,
        TransientEventV1 transientEvent,
        ValidatedPayload<TransientEventV1> payload)
        => new()
        {
            EventVersionId = transientEvent.EventVersionId,
            CentralTransientEventId = eventRecordId,
            Version = transientEvent.Version,
            PreviousVersionNumber = transientEvent.Version == 1 ? null : transientEvent.Version - 1,
            PreviousEventVersionId = transientEvent.PreviousEventVersionId,
            PreviousVersionCreatedUtc = transientEvent.PreviousVersionCreatedUtc,
            State = transientEvent.State,
            VersionCreatedUtc = transientEvent.VersionCreatedUtc,
            FirstObservedUtc = transientEvent.FirstObservedUtc,
            LastObservedUtc = transientEvent.LastObservedUtc,
            SchemaVersion = transientEvent.SchemaVersion,
            CanonicalEventJson = payload.Json,
            CanonicalEventSha256 = payload.Sha256,
            CanonicalEventByteLength = payload.ByteLength
        };

    private static CentralTransientExtractionReceipt CreateExtractionReceipt(
        Guid jobId,
        ValidatedPayload<TransientCandidateExtractionDescriptorV1> payload,
        IReadOnlyDictionary<Guid, CentralArtifact> artifacts)
    {
        var receipt = new CentralTransientExtractionReceipt
        {
            CentralDerivativeJobId = jobId,
            SchemaVersion = payload.Value.SchemaVersion,
            ExtractionIdentitySha256 = payload.Value.ExtractionIdentitySha256,
            OptionsIdentitySha256 = payload.Value.OptionsIdentitySha256,
            CanonicalReceiptJson = payload.Json,
            CanonicalReceiptSha256 = payload.Sha256,
            CanonicalReceiptByteLength = payload.ByteLength
        };
        for (var ordinal = 0; ordinal < payload.Value.OrderedSources.Count; ordinal++)
        {
            var source = payload.Value.OrderedSources[ordinal];
            var evidence = source.Source;
            receipt.Sources.Add(new CentralTransientExtractionSourceReference
            {
                CentralDerivativeJobId = jobId,
                Ordinal = ordinal,
                Position = source.Position,
                DetectorInputIdentitySha256 = source.DetectorInputIdentitySha256,
                CentralArtifactId = GetArtifact(artifacts, evidence.Locator.Artifact.ArtifactId).Id,
                EvidenceSchemaVersion = evidence.SchemaVersion,
                EvidenceId = evidence.EvidenceId,
                LocatorSchemaVersion = evidence.Locator.SchemaVersion,
                LocatorKind = evidence.Locator.Kind,
                ArtifactId = evidence.Locator.Artifact.ArtifactId,
                ArtifactRole = evidence.Locator.Artifact.Role,
                ArtifactVariant = evidence.Locator.Artifact.Variant,
                ArtifactRecipeIdentitySha256 = evidence.Locator.Artifact.RecipeIdentitySha256,
                ArtifactChecksumSha256 = evidence.Locator.Artifact.ChecksumSha256,
                ObservationStartedUtc = evidence.ObservationStartedUtc,
                ObservationEndedUtc = evidence.ObservationEndedUtc,
                TimingQuality = evidence.TimingQuality,
                TimingProvenanceSource = evidence.TimingProvenance.Source,
                TimingProvenanceVersion = evidence.TimingProvenance.Version
            });
        }
        return receipt;
    }

    private static ValidatedPayload<TransientEventV1> ParseEvent(CentralTransientCanonicalPayload payload)
    {
        ValidateDeclaredPayload(payload);
        var parsed = TransientContractJson.ParseEvent(payload.Utf8Json);
        if (!parsed.Validation.IsValid || parsed.Value is null)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload,
                $"Event payload is invalid: {parsed.Validation.ReasonCode}:{parsed.Validation.FieldPath}");
        }
        return ValidateCanonical(payload, parsed.Value, TransientContractJson.Serialize);
    }

    private static ValidatedPayload<TransientCandidateExtractionDescriptorV1> ParseExtraction(
        CentralTransientCanonicalPayload payload)
    {
        ValidateDeclaredPayload(payload);
        try
        {
            var parsed = TransientCandidateExtractionJson.Parse(payload.Utf8Json.Span);
            return ValidateCanonical(payload, parsed, TransientCandidateExtractionJson.Serialize);
        }
        catch (ArgumentException exception)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload, exception.Message);
        }
    }

    private static ValidatedPayload<TransientAssessmentExecutionDescriptorV1> ParseAssessment(
        CentralTransientCanonicalPayload payload)
    {
        ValidateDeclaredPayload(payload);
        try
        {
            var parsed = TransientAssessmentJson.Parse(payload.Utf8Json.Span);
            return ValidateCanonical(payload, parsed, TransientAssessmentJson.Serialize);
        }
        catch (ArgumentException exception)
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload, exception.Message);
        }
    }

    private static ValidatedPayload<T> ValidateCanonical<T>(
        CentralTransientCanonicalPayload payload,
        T value,
        Func<T, byte[]> serialize)
    {
        var canonical = serialize(value);
        if (!canonical.AsSpan().SequenceEqual(payload.Utf8Json.Span))
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload,
                "Payload bytes are valid JSON but are not the canonical Processing representation.");
        }
        return new(value, Encoding.UTF8.GetString(canonical), payload.Sha256, payload.ByteLength);
    }

    private static void ValidateDeclaredPayload(CentralTransientCanonicalPayload payload)
    {
        var actualSha256 = Convert.ToHexString(SHA256.HashData(payload.Utf8Json.Span));
        if (payload.ByteLength != payload.Utf8Json.Length || payload.ByteLength <= 0 ||
            !string.Equals(payload.Sha256, actualSha256, StringComparison.Ordinal))
        {
            throw Failure(CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload,
                "Declared canonical payload length or SHA-256 does not match the exact bytes.");
        }
    }

    private static bool IsPrefix<T>(IReadOnlyList<T> expectedPrefix, IReadOnlyList<T> values)
        => expectedPrefix.Count <= values.Count && expectedPrefix.Select((item, index) => CanonicalEqual(item, values[index])).All(item => item);

    private static bool CanonicalEqual<T>(T first, T second)
        => string.Equals(CanonicalJson(first), CanonicalJson(second), StringComparison.Ordinal);

    private static string CanonicalJson<T>(T value)
        => CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(value)).GetRawText();

    private static CentralArtifact GetArtifact(IReadOnlyDictionary<Guid, CentralArtifact> artifacts, Guid artifactId)
        => artifacts.TryGetValue(artifactId, out var artifact)
            ? artifact
            : throw Failure(CentralTransientPersistenceReasonCodes.InvalidArtifactLineage,
                "Referenced artifact is not part of the locked central evidence set.");

    private static CentralTransientPersistenceException Failure(string reasonCode, string message)
        => new(reasonCode, message);

    private sealed record ValidatedPayload<T>(T Value, string Json, string Sha256, int ByteLength);
    private sealed record PersistedEventEvidence(
        Guid EventId,
        Guid EventVersionId,
        TransientEventState State,
        string Sha256);
}
