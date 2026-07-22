using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralTransientValidationExecutor
{
    Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);
}

internal sealed partial class CentralTransientValidationExecutor(
    ApplicationDbContext dbContext,
    ICentralDerivativeJobInputReader inputReader,
    ICentralTransientEventPersistence persistence,
    ICentralDerivativeJobService jobService,
    ICentralTransientMaskFactory maskFactory,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<CentralTransientValidationExecutor> logger) : ICentralTransientValidationExecutor
{
    private static readonly TransientTemporalPosition[] Positions =
    [
        TransientTemporalPosition.NMinus2,
        TransientTemporalPosition.NMinus1,
        TransientTemporalPosition.N,
        TransientTemporalPosition.NPlus1,
        TransientTemporalPosition.NPlus2
    ];

    public async Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var validation = await LoadValidationAsync(lease, cancellationToken).ConfigureAwait(false);
        if (validation.OutcomeRecordedAtUtc.HasValue)
        {
            if (validation.OutcomeState is null || string.IsNullOrWhiteSpace(validation.OutcomeReasonCode) ||
                string.IsNullOrWhiteSpace(validation.OutcomeEvidenceIdentitySha256) ||
                validation.IdentitySlots.Any(slot => slot.State == CentralTransientValidationIdentitySlotState.Reserved) ||
                validation.CommittedAtUtc.HasValue && validation.ExtractionReceipt is null)
            {
                return await FailAsync(lease, CentralTransientRuntimeReasonCodes.InconsistentCommittedOutput,
                    cancellationToken).ConfigureAwait(false);
            }
            var adoptedReason = validation.CommittedAtUtc.HasValue
                ? CentralTransientRuntimeReasonCodes.OutputAdopted
                : validation.OutcomeReasonCode;
            await jobService.CompleteWithoutArtifactAsync(
                lease.JobId, lease.LeaseToken, adoptedReason, cancellationToken)
                .ConfigureAwait(false);
            telemetry.RecordRecovery("adopted");
            telemetry.RecordTransientValidation(
                validation.CommittedAtUtc.HasValue ? "adopted" : "needs-review-adopted", 0, []);
            return new CentralDerivativeExecutionResult(
                validation.CommittedAtUtc.HasValue ? ProcessingOutcomeStatus.Produced : ProcessingOutcomeStatus.Skipped,
                null,
                adoptedReason);
        }

        CentralTransientExecutionOptionsV1 executionOptions;
        try
        {
            executionOptions = ParseExecutionOptions(validation);
        }
        catch (CentralDerivativeInputRejectedException)
        {
            return await CompleteNeedsReviewAsync(
                lease, CentralTransientRuntimeReasonCodes.InvalidExecutionPolicy, cancellationToken).ConfigureAwait(false);
        }
        var inputs = await inputReader.ReadAsync(lease, cancellationToken).ConfigureAwait(false);
        if (inputs.ProcessingInputs.Count != Positions.Length || lease.Inputs is not { Count: 5 })
        {
            return await FailAsync(lease, CentralTransientRuntimeReasonCodes.InvalidWindow, cancellationToken)
                .ConfigureAwait(false);
        }
        var inputIds = await dbContext.CentralDerivativeJobInputs.AsNoTracking()
            .Where(input => input.CentralDerivativeJobId == lease.JobId)
            .OrderBy(input => input.Ordinal)
            .Select(input => input.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (inputIds.Length != Positions.Length)
        {
            return await FailAsync(lease, CentralTransientRuntimeReasonCodes.InvalidWindow, cancellationToken)
                .ConfigureAwait(false);
        }

        var detectStarted = timeProvider.GetTimestamp();
        using var detectActivity = telemetry.StartStage("detect", CentralTransientRuntime.RecipeName);
        var detectorSources = new List<DetectorSource>(Positions.Length);
        for (var ordinal = 0; ordinal < Positions.Length; ordinal++)
        {
            var processingInput = inputs.ProcessingInputs[ordinal];
            var leaseInput = lease.Inputs[ordinal];
            if (leaseInput.CaptureSequence is not { } sequence || processingInput.Descriptor is null ||
                leaseInput.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated))
            {
                return await FailAsync(lease, CentralTransientRuntimeReasonCodes.InvalidWindow, cancellationToken)
                    .ConfigureAwait(false);
            }
            var artifact = CreateProcessingArtifact(processingInput);
            var evidence = CreateEvidence(inputIds[ordinal], artifact, leaseInput.ChecksumSha256);
            var levels = CreateLevels(artifact.Layout!);
            var created = TransientDetectorInputFactory.Create(artifact, evidence, levels, cancellationToken);
            if (!created.Validation.IsValid || created.Input is null)
            {
                return await FailAsync(
                    lease, created.Validation.ReasonCode ?? CentralTransientRuntimeReasonCodes.InvalidDetectorInput,
                    cancellationToken).ConfigureAwait(false);
            }
            detectorSources.Add(new DetectorSource(
                Positions[ordinal], sequence, created.Input,
                new TransientSensitivityV1($"central-exact-{leaseInput.CompatibilitySha256}", 1, 1),
                processingInput.Descriptor));
        }
        var masks = await maskFactory.CreateAsync(detectorSources.Select(item => new CentralTransientMaskSource(
            item.Position,
            lease.Inputs.Single(input => input.CaptureSequence == item.CaptureSequence).CentralArtifactId,
            item.Input,
            item.Descriptor)).ToArray(), executionOptions, cancellationToken)
            .ConfigureAwait(false);
        if (masks is null)
        {
            return await CompleteNeedsReviewAsync(
                lease, CentralTransientRuntimeReasonCodes.MaskEvidenceUnavailable, cancellationToken).ConfigureAwait(false);
        }
        var sources = detectorSources.ToDictionary(
            item => item.Position,
            item => new TransientTemporalSource(
                item.Position, item.CaptureSequence, item.Input, item.Sensitivity, masks));
        var target = sources[TransientTemporalPosition.N];
        var context = Positions.Where(position => position != TransientTemporalPosition.N)
            .Select(position => sources[position]).ToArray();
        var contextState = await ResolveContextStateAsync(
            lease, validation, inputIds, cancellationToken).ConfigureAwait(false);
        var background = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
            TransientTemporalBackgroundKind.CenteredFinal,
            target,
            context,
            contextState.KnownEventEvidenceIds,
            TimeSpan.FromTicks(executionOptions.MaximumAdjacentStartIntervalTicks)), cancellationToken);
        if (background.Status != TransientTemporalBackgroundStatus.Produced || background.Product is null)
        {
            return await FailAsync(
                lease, background.ReasonCode ?? CentralTransientRuntimeReasonCodes.InvalidWindow,
                cancellationToken).ConfigureAwait(false);
        }

        var slots = validation.IdentitySlots.OrderBy(slot => slot.Ordinal).ToArray();
        var createdUtc = sources.Values.Max(source => source.Input.Descriptor.Source.ObservationEndedUtc).AddTicks(1);
        var orderedSources = background.Product.Descriptor.Sources.Select(lineage => sources.Values.Single(source =>
            source.Input.Descriptor.Source.EvidenceId == lineage.EvidenceId)).ToArray();
        var extractionIdentities = validation.SubmittedCandidateJson is null
            ? slots.Select(slot => new TransientCandidateIdentitySlot(
                slot.CandidateId, slot.AdoptedEventId ?? slot.SubmittedEventId)).ToArray()
            : slots.Select(_ => new TransientCandidateIdentitySlot(Guid.NewGuid(), Guid.NewGuid())).ToArray();
        var extraction = TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            validation.AgentId,
            createdUtc,
            target,
            background.Product,
            orderedSources,
            extractionIdentities,
            executionOptions.Extraction,
            CenteredContextConverged: contextState.Converged), cancellationToken);
        telemetry.RecordStage("detect", CentralTransientRuntime.RecipeName,
            extraction.Status.ToString().ToLowerInvariant(), timeProvider.GetElapsedTime(detectStarted), inputs.ByteLength);
        if (extraction.Status is TransientCandidateExtractionStatus.Invalid or TransientCandidateExtractionStatus.LimitExceeded ||
            extraction.Descriptor is null)
        {
            return await FailAsync(
                lease, extraction.ReasonCode ?? CentralTransientRuntimeReasonCodes.InvalidExtraction,
                cancellationToken).ConfigureAwait(false);
        }
        if (validation.SubmittedCandidateJson is not null)
        {
            var binding = BindHybridSubmittedCandidate(
                validation, extraction, executionOptions, target, background.Product, orderedSources, slots,
                createdUtc, contextState.Converged, cancellationToken);
            if (binding.Outcome is null)
            {
                return await CompleteNeedsReviewAsync(lease, binding.ReasonCode!, cancellationToken)
                    .ConfigureAwait(false);
            }
            extraction = binding.Outcome;
        }

        var convergenceStarted = timeProvider.GetTimestamp();
        using var convergenceActivity = telemetry.StartStage("converge", CentralTransientRuntime.RecipeName);
        await using var associationTransaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await CentralTransientValidationOutcome.AcquireEventSerializationLockAsync(
            dbContext, validation.AgentId, cancellationToken).ConfigureAwait(false);
        var associated = await AssociateAsync(
            lease, validation, extraction, executionOptions, target,
            detectorSources.Single(item => item.Position == TransientTemporalPosition.N).Descriptor,
            background.Product, orderedSources, slots,
            createdUtc, cancellationToken).ConfigureAwait(false);
        extraction = associated.Extraction;
        var extractionDescriptor = extraction.Descriptor
            ?? throw new CentralDerivativeJobStateException("Transient extraction did not retain its descriptor.");
        var events = new List<TransientEventV1>(extraction.Candidates.Count);
        var assessmentReceipts = new List<TransientAssessmentExecutionDescriptorV1>(extraction.Candidates.Count);
        for (var ordinal = 0; ordinal < extraction.Candidates.Count; ordinal++)
        {
            var candidate = extraction.Candidates[ordinal];
            var slot = slots.Single(item => item.CandidateId == candidate.CandidateId);
            var prior = associated.PriorEvents.GetValueOrDefault(candidate.CandidateId);
            var currentObservation = TransientObservationFactory.CreateAssessmentObservation(
                new TransientObservationPromotionRequest(
                    candidate.CandidateId, slot.ObservationId, prior?.Event.Observations.Count ?? 0, extractionDescriptor));
            var assessmentObservations = prior is null
                ? [currentObservation]
                : prior.Event.Observations.Select(observation => new TransientAssessmentObservationV1(
                        prior.Event.EventId,
                        prior.ExtractionIdentityByObservationId[observation.ObservationId],
                        observation))
                    .Append(currentObservation).ToArray();
            var versionCreatedUtc = prior is null
                ? createdUtc.AddTicks(1)
                : Max(createdUtc.AddTicks(1), prior.Event.VersionCreatedUtc.AddTicks(1));
            var priorAssessment = prior?.Event.Assessments.LastOrDefault(assessment =>
                assessment.Producer.Kind == TransientAssessmentProducerKind.DeterministicAlgorithm &&
                string.Equals(assessment.Producer.Name, TransientAssessmentFactory.ProducerName, StringComparison.Ordinal) &&
                string.Equals(assessment.Producer.Version, TransientAssessmentFactory.ProducerVersion, StringComparison.Ordinal));
            var assessment = TransientAssessmentFactory.Create(new TransientAssessmentExecutionRequest(
                candidate.EventId,
                slot.AssessmentId,
                versionCreatedUtc,
                TransientAssessmentAuthority.Authoritative,
                assessmentObservations,
                executionOptions.Assessment,
                prior?.Event.Assessments ?? [],
                priorAssessment?.AssessmentId), cancellationToken);
            if (assessment.Status != TransientAssessmentExecutionStatus.Produced || assessment.Descriptor is null)
            {
                throw new CentralDerivativeInputRejectedException(
                    assessment.ReasonCode ?? CentralTransientRuntimeReasonCodes.InvalidAssessment);
            }
            assessmentReceipts.Add(assessment.Descriptor);
            var observations = prior is null
                ? [currentObservation.Observation]
                : prior.Event.Observations.Append(currentObservation.Observation).ToArray();
            var assessments = prior is null
                ? [assessment.Descriptor.Assessment]
                : prior.Event.Assessments.Append(assessment.Descriptor.Assessment).ToArray();
            events.Add(new TransientEventV1(
                TransientEventV1.CurrentSchemaVersion,
                candidate.EventId,
                Guid.NewGuid(),
                (prior?.Event.Version ?? 0) + 1,
                prior?.Event.EventVersionId,
                prior?.Event.VersionCreatedUtc,
                validation.AgentId,
                StateFor(
                    candidate.State,
                    assessment.Descriptor.Assessment,
                    associated.AmbiguousCandidateIds.Contains(candidate.CandidateId)),
                prior?.Event.EventCreatedUtc ?? candidate.CreatedUtc,
                versionCreatedUtc,
                observations.Min(observation => observation.Source.ObservationStartedUtc),
                observations.Max(observation => observation.Source.ObservationEndedUtc),
                observations,
                assessments,
                prior?.Event.Reviews ?? [],
                prior?.Event.Notifications ?? [],
                prior?.Event.Derivatives ?? []));
        }
        telemetry.RecordStage("converge", CentralTransientRuntime.RecipeName, "completed",
            timeProvider.GetElapsedTime(convergenceStarted));

        var extractionPayload = Canonical(TransientCandidateExtractionJson.Serialize(extractionDescriptor));
        var eventPayloads = events.Select(value => Canonical(TransientContractJson.Serialize(value))).ToArray();
        var assessmentPayloads = assessmentReceipts.Select(value => Canonical(TransientAssessmentJson.Serialize(value)))
            .ToArray();
        var persistStarted = timeProvider.GetTimestamp();
        using (telemetry.StartStage("persist", CentralTransientRuntime.RecipeName))
        {
            _ = await persistence.AppendAsync(new CentralTransientPersistenceRequest(
                lease.JobId, extractionPayload, eventPayloads, assessmentPayloads), cancellationToken)
                .ConfigureAwait(false);
        }
        await associationTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        var completionReason = extraction.Candidates.Count == 0
            ? TransientCandidateExtractionReasonCodes.NoCandidate
            : CentralTransientRuntimeReasonCodes.Persisted;
        await jobService.CompleteWithoutArtifactAsync(
            lease.JobId, lease.LeaseToken, completionReason, cancellationToken).ConfigureAwait(false);
        telemetry.RecordStage("persist", CentralTransientRuntime.RecipeName, completionReason,
            timeProvider.GetElapsedTime(persistStarted));
        telemetry.RecordTransientValidation(
            extraction.Candidates.Count == 0 ? "no-candidate" : "persisted",
            extraction.Candidates.Count,
            assessmentReceipts.Select(receipt => receipt.Assessment.Classification));
        Log.Completed(logger, lease.JobId, extraction.Candidates.Count, completionReason);
        return new CentralDerivativeExecutionResult(
            extraction.Candidates.Count == 0 ? ProcessingOutcomeStatus.Skipped : ProcessingOutcomeStatus.Produced,
            null,
            extraction.Candidates.Count == 0 ? completionReason : null);
    }

    private async Task<AssociationResult> AssociateAsync(
        CentralDerivativeJobLease lease,
        CentralTransientValidationJob validation,
        TransientCandidateExtractionOutcome extraction,
        CentralTransientExecutionOptionsV1 options,
        TransientTemporalSource target,
        ReconstructionDescriptor targetDescriptor,
        TransientTemporalBackgroundProduct background,
        IReadOnlyList<TransientTemporalSource> orderedSources,
        CentralTransientValidationIdentitySlot[] slots,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        if (extraction.Candidates.Count == 0 || lease.Inputs![2].CaptureSequence is not { } centerSequence)
        {
            return new AssociationResult(extraction, new Dictionary<Guid, PriorEvent>(), new HashSet<Guid>());
        }
        var adjacentSequences = new[]
        {
            centerSequence > long.MinValue ? centerSequence - 1 : (long?)null,
            centerSequence < long.MaxValue ? centerSequence + 1 : null
        }.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        var provisionalEventRecordIds = validation.ProvisionalCentralDerivativeJobId is not { } provisionalJobId
            ? []
            : await dbContext.CentralTransientValidationIdentitySlots.AsNoTracking()
                .Where(item => item.CentralDerivativeJobId == provisionalJobId &&
                    item.State == CentralTransientValidationIdentitySlotState.Committed &&
                    item.CentralTransientEventId != null)
                .Select(item => item.CentralTransientEventId!.Value)
                .Distinct()
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var adjacentEvents = await dbContext.CentralTransientEvents.AsNoTracking()
            .Where(item => item.AgentId == validation.AgentId &&
                (provisionalEventRecordIds.Contains(item.Id) || item.Observations.Any(observation =>
                    observation.Source!.Artifact!.Frame!.CaptureSequence != null &&
                    adjacentSequences.Contains(observation.Source.Artifact.Frame.CaptureSequence.Value))))
            .Include(item => item.Versions)
            .Include(item => item.Observations).ThenInclude(item => item.Source)!.ThenInclude(item => item!.Artifact)!
                .ThenInclude(item => item!.Frame)
            .AsSplitQuery()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var adjacentArtifactIds = adjacentEvents.SelectMany(item => item.Observations)
            .Select(item => item.Source!.CentralArtifactId).Distinct().ToArray();
        var adjacentArtifacts = await dbContext.CentralArtifacts.AsNoTracking()
            .Where(item => adjacentArtifactIds.Contains(item.Id))
            .Include(item => item.Layout)
            .Include(item => item.Recipe)
            .Include(item => item.Sources)
            .Include(item => item.Frame)!.ThenInclude(item => item!.Timing)
            .Include(item => item.Frame)!.ThenInclude(item => item!.Control)
            .Include(item => item.Frame)!.ThenInclude(item => item!.Profiles)
            .AsSplitQuery()
            .ToDictionaryAsync(item => item.Id, cancellationToken).ConfigureAwait(false);
        var matchesByCandidate = new Dictionary<Guid, List<PriorEvent>>();
        foreach (var candidate in extraction.Candidates)
        {
            var matches = new List<PriorEvent>();
            foreach (var eventRecord in adjacentEvents)
            {
                var latest = eventRecord.Versions.OrderByDescending(version => version.Version).FirstOrDefault();
                if (latest is null)
                {
                    continue;
                }
                var parsed = TransientContractJson.ParseEvent(System.Text.Encoding.UTF8.GetBytes(latest.CanonicalEventJson));
                var transientEvent = parsed.Value ?? throw new CentralDerivativeJobStateException(
                    $"Persisted transient event failed canonical parsing: {parsed.Validation.ReasonCode}.");
                var extractionIdentities = eventRecord.Observations.ToDictionary(
                    observation => observation.ObservationId,
                    observation => observation.ExtractionReceiptIdentitySha256);
                var associationCompatibility = eventRecord.Observations.ToDictionary(
                    observation => observation.ObservationId,
                    observation => CreateAssociationCompatibility(
                        CentralReconstructionDescriptorFactory.Create(
                            adjacentArtifacts[observation.Source!.CentralArtifactId].Frame!,
                            adjacentArtifacts[observation.Source.CentralArtifactId])));
                var isProvisionalEvent = provisionalEventRecordIds.Contains(eventRecord.Id);
                var matchingObservations = transientEvent.Observations.Where(observation => eventRecord.Observations.Any(record =>
                        record.ObservationId == observation.ObservationId &&
                        record.Source!.Artifact!.Frame!.CaptureSequence != null &&
                        (adjacentSequences.Contains(record.Source.Artifact.Frame.CaptureSequence.Value) ||
                         isProvisionalEvent && record.Source.Artifact.Frame.CaptureSequence.Value == centerSequence)))
                    .Where(observation => IsAssociationMatch(
                        observation,
                        associationCompatibility[observation.ObservationId],
                        candidate,
                        targetDescriptor,
                        options,
                        allowSameDetectorInput: isProvisionalEvent &&
                            eventRecord.Observations.Any(record => record.ObservationId == observation.ObservationId &&
                                record.Source!.Artifact!.Frame!.CaptureSequence == centerSequence)))
                    .ToArray();
                if (matchingObservations.Length == 1)
                {
                    matches.Add(new PriorEvent(
                        transientEvent,
                        extractionIdentities,
                        associationCompatibility,
                        matchingObservations[0].ObservationId));
                }
            }
            matchesByCandidate.Add(candidate.CandidateId, matches);
        }
        var eventMatchCounts = matchesByCandidate.Values.SelectMany(static value => value)
            .GroupBy(static value => value.Event.EventId)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var candidates = matchesByCandidate
            .Where(item => item.Value.Count == 1 && eventMatchCounts[item.Value[0].Event.EventId] == 1)
            .ToDictionary(static item => item.Key, static item => item.Value[0]);
        var ambiguousCandidateIds = matchesByCandidate.Where(item => item.Value.Count > 1 ||
                item.Value.Count == 1 && eventMatchCounts[item.Value[0].Event.EventId] > 1)
            .Select(static item => item.Key).ToHashSet();
        foreach (var slot in slots.Where(item => item.AdoptedEventId.HasValue &&
                     extraction.Candidates.Any(candidate => candidate.CandidateId == item.CandidateId)))
        {
            var retained = matchesByCandidate[slot.CandidateId].SingleOrDefault(item =>
                item.Event.EventId == slot.AdoptedEventId);
            if (retained is null)
            {
                throw new CentralDerivativeJobStateException(
                    "The retained transient association no longer matches its immutable adjacent evidence.");
            }
            candidates[slot.CandidateId] = retained;
        }
        if (candidates.Count == 0)
        {
            return new AssociationResult(extraction, candidates, ambiguousCandidateIds);
        }

        foreach (var candidate in extraction.Candidates)
        {
            if (candidates.TryGetValue(candidate.CandidateId, out var prior))
            {
                var slot = slots.Single(item => item.CandidateId == candidate.CandidateId);
                var associationIdentity = CreateAssociationIdentity(
                    slot.ObservationId,
                    prior.MatchedObservationId);
                if (slot.AdoptedEventId.HasValue)
                {
                    if (slot.AdoptedEventId != prior.Event.EventId ||
                        !string.Equals(slot.AssociationIdentitySha256, associationIdentity, StringComparison.Ordinal))
                    {
                        throw new CentralDerivativeJobStateException(
                            "The retained transient association differs from the measured adjacent event.");
                    }
                    continue;
                }
                var affected = await dbContext.CentralTransientValidationIdentitySlots.Where(item =>
                        item.CentralDerivativeJobId == lease.JobId && item.CandidateId == candidate.CandidateId &&
                        item.State == CentralTransientValidationIdentitySlotState.Reserved &&
                        item.AdoptedEventId == null && item.AssociationIdentitySha256 == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.AdoptedEventId, prior.Event.EventId)
                        .SetProperty(item => item.AssociationIdentitySha256, associationIdentity), cancellationToken)
                    .ConfigureAwait(false);
                if (affected != 1)
                {
                    throw new CentralDerivativeJobStateException(
                        "The transient association binding changed before its compare-and-swap commit.");
                }
            }
        }
        dbContext.ChangeTracker.Clear();
        var activeCandidateIds = extraction.Candidates.Select(candidate => candidate.CandidateId).ToHashSet();
        var fullCandidates = extraction.Descriptor?.Candidates
            ?? throw new CentralDerivativeJobStateException("Transient association lost its full extraction receipt.");
        var rerun = TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            validation.AgentId,
            createdUtc,
            target,
            background,
            orderedSources,
            fullCandidates.Select(candidate =>
            {
                var slot = slots.Single(item => item.CandidateId == candidate.CandidateId);
                return new TransientCandidateIdentitySlot(
                    slot.CandidateId,
                    candidates.TryGetValue(slot.CandidateId, out var prior)
                        ? prior.Event.EventId
                        : slot.AdoptedEventId ?? slot.SubmittedEventId);
            }).Concat(slots.Where(slot => fullCandidates.All(candidate => candidate.CandidateId != slot.CandidateId))
                .Select(slot => new TransientCandidateIdentitySlot(
                    slot.CandidateId, slot.AdoptedEventId ?? slot.SubmittedEventId))).ToArray(),
            options.Extraction,
            CenteredContextConverged: extraction.Descriptor?.CenteredContextConverged ?? false), cancellationToken);
        var activeCandidates = rerun.Candidates.Where(candidate => activeCandidateIds.Contains(candidate.CandidateId)).ToArray();
        if (rerun.Status != extraction.Status || rerun.Candidates.Count != fullCandidates.Count ||
            activeCandidates.Length != extraction.Candidates.Count || rerun.Descriptor is null)
        {
            throw new CentralDerivativeJobStateException(
                "Transient association probe and retained-identity extraction did not converge.");
        }
        return new AssociationResult(rerun with { Candidates = activeCandidates }, candidates, ambiguousCandidateIds);
    }

    private static HybridCandidateBinding BindHybridSubmittedCandidate(
        CentralTransientValidationJob validation,
        TransientCandidateExtractionOutcome probe,
        CentralTransientExecutionOptionsV1 options,
        TransientTemporalSource target,
        TransientTemporalBackgroundProduct background,
        IReadOnlyList<TransientTemporalSource> orderedSources,
        CentralTransientValidationIdentitySlot[] slots,
        DateTimeOffset createdUtc,
        bool contextConverged,
        CancellationToken cancellationToken)
    {
        var parsed = TransientContractJson.ParseCandidate(
            System.Text.Encoding.UTF8.GetBytes(validation.SubmittedCandidateJson!));
        var submitted = parsed.Value ?? throw new CentralDerivativeJobStateException(
            $"The durable Hybrid candidate is invalid: {parsed.Validation.ReasonCode}.");
        var matches = probe.Candidates.Select((candidate, ordinal) => (candidate, ordinal))
            .Where(item => HybridCandidateMatches(submitted, item.candidate)).ToArray();
        if (matches.Length != 1)
        {
            return new HybridCandidateBinding(
                null,
                matches.Length == 0
                    ? CentralTransientRuntimeReasonCodes.HybridCandidateNotFound
                    : CentralTransientRuntimeReasonCodes.HybridCandidateAmbiguous);
        }
        var submittedSlot = slots.SingleOrDefault(slot =>
            slot.CandidateId == submitted.CandidateId && slot.SubmittedEventId == submitted.EventId);
        if (submittedSlot is null)
        {
            throw new CentralDerivativeJobStateException(
                "The durable Hybrid candidate does not have one reserved submitted identity slot.");
        }
        var remainingSlots = new Queue<CentralTransientValidationIdentitySlot>(
            slots.Where(slot => slot.Id != submittedSlot.Id).OrderBy(slot => slot.Ordinal));
        var identities = new List<TransientCandidateIdentitySlot>(slots.Length);
        for (var ordinal = 0; ordinal < slots.Length; ordinal++)
        {
            var slot = ordinal == matches[0].ordinal ? submittedSlot : remainingSlots.Dequeue();
            identities.Add(new TransientCandidateIdentitySlot(
                slot.CandidateId, slot.AdoptedEventId ?? slot.SubmittedEventId));
        }
        var bound = TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            validation.AgentId,
            createdUtc,
            target,
            background,
            orderedSources,
            identities,
            options.Extraction,
            CenteredContextConverged: contextConverged), cancellationToken);
        if (bound.Status != probe.Status || bound.Descriptor is null ||
            bound.Candidates.Count != probe.Candidates.Count ||
            matches[0].ordinal >= bound.Candidates.Count ||
            bound.Candidates[matches[0].ordinal].CandidateId != submitted.CandidateId ||
            bound.Candidates[matches[0].ordinal].EventId != submitted.EventId ||
            !HybridCandidateMatches(submitted, bound.Candidates[matches[0].ordinal]))
        {
            throw new CentralDerivativeJobStateException(
                "The centered Hybrid extraction did not preserve its exact submitted candidate binding.");
        }
        return new HybridCandidateBinding(bound with { Candidates = [bound.Candidates[matches[0].ordinal]] }, null);
    }

    private static bool HybridCandidateMatches(TransientCandidateV1 submitted, TransientCandidateV1 centered)
    {
        if (submitted.Geometry is null || centered.Geometry is null)
        {
            return false;
        }
        var submittedCenter = submitted.ContextSources.SingleOrDefault(source =>
            source.EvidenceId == submitted.CenterEvidenceId)?.Locator.Artifact;
        var centeredCenter = centered.ContextSources.SingleOrDefault(source =>
            source.EvidenceId == centered.CenterEvidenceId)?.Locator.Artifact;
        if (submittedCenter is null || centeredCenter is null || submittedCenter != centeredCenter)
        {
            return false;
        }
        // Detector-input and mask identities include host-local evidence; the artifact plus these profile/producer
        // identities are the stable cross-host provenance for the same measured geometry.
        var submittedIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            submitted.Geometry.CoordinateWidth,
            submitted.Geometry.CoordinateHeight,
            submitted.Geometry.Bounds,
            submitted.Geometry.Polyline,
            submitted.Provenance.CalibrationIdentity,
            submitted.Provenance.ProcessingProfileIdentity,
            submitted.Extraction.Producer,
            submitted.Extraction.RecipeIdentitySha256
        });
        var centeredIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            centered.Geometry.CoordinateWidth,
            centered.Geometry.CoordinateHeight,
            centered.Geometry.Bounds,
            centered.Geometry.Polyline,
            centered.Provenance.CalibrationIdentity,
            centered.Provenance.ProcessingProfileIdentity,
            centered.Extraction.Producer,
            centered.Extraction.RecipeIdentitySha256
        });
        return string.Equals(submittedIdentity, centeredIdentity, StringComparison.Ordinal);
    }

    private static bool IsAssociationMatch(
        TransientObservationV1 prior,
        AssociationCompatibility priorCompatibility,
        TransientCandidateV1 candidate,
        ReconstructionDescriptor currentInput,
        CentralTransientExecutionOptionsV1 options,
        bool allowSameDetectorInput = false)
    {
        if (candidate.Geometry is null || prior.Geometry.Polyline.Count < 2 || candidate.Geometry.Polyline.Count < 2)
        {
            return false;
        }
        if (!allowSameDetectorInput &&
                prior.Provenance.DetectorInputIdentitySha256 == candidate.Provenance.DetectorInputIdentitySha256 ||
            prior.Source.Locator.Artifact.Role != currentInput.Artifact.Role ||
            !IsAssociationCompatible(
                priorCompatibility.Role,
                priorCompatibility.Layout,
                priorCompatibility.Processing,
                currentInput.Artifact.Role,
                currentInput.Layout,
                LogicHostRecipeExecutionAdapter.CreateCompatibility(currentInput)) ||
            !string.Equals(prior.Provenance.CalibrationIdentity, candidate.Provenance.CalibrationIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(prior.Provenance.MaskIdentity, candidate.Provenance.MaskIdentity, StringComparison.Ordinal) ||
            !string.Equals(prior.Provenance.ProcessingProfileIdentity,
                candidate.Provenance.ProcessingProfileIdentity, StringComparison.Ordinal))
        {
            return false;
        }
        var current = candidate.ContextSources.Single(source => source.EvidenceId == candidate.CenterEvidenceId);
        var gap = current.ObservationStartedUtc >= prior.Source.ObservationEndedUtc
            ? current.ObservationStartedUtc - prior.Source.ObservationEndedUtc
            : prior.Source.ObservationStartedUtc >= current.ObservationEndedUtc
                ? prior.Source.ObservationStartedUtc - current.ObservationEndedUtc
                : TimeSpan.Zero;
        if (gap > TimeSpan.FromTicks(options.MaximumAssociationTimeGapTicks))
        {
            return false;
        }
        var priorStart = prior.Geometry.Polyline[0];
        var priorEnd = prior.Geometry.Polyline[^1];
        var currentStart = candidate.Geometry.Polyline[0];
        var currentEnd = candidate.Geometry.Polyline[^1];
        var endpointGap = new[]
        {
            Distance(priorStart, currentStart), Distance(priorStart, currentEnd),
            Distance(priorEnd, currentStart), Distance(priorEnd, currentEnd)
        }.Min();
        if (endpointGap > options.MaximumAssociationEndpointGapPixels)
        {
            return false;
        }
        var priorX = priorEnd.X - priorStart.X;
        var priorY = priorEnd.Y - priorStart.Y;
        var currentX = currentEnd.X - currentStart.X;
        var currentY = currentEnd.Y - currentStart.Y;
        var denominator = Math.Sqrt((priorX * priorX + priorY * priorY) * (currentX * currentX + currentY * currentY));
        return denominator > 0 && Math.Abs((priorX * currentX + priorY * currentY) / denominator)
            >= options.MinimumAssociationAlignmentCosine;
    }

    private static string CreateAssociationIdentity(Guid firstObservationId, Guid secondObservationId)
    {
        var ordered = new[] { firstObservationId, secondObservationId }.Order().ToArray();
        var value = $"central-transient-association-v1\n{ordered[0]:N}\n{ordered[1]:N}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    private static AssociationCompatibility CreateAssociationCompatibility(ReconstructionDescriptor descriptor)
        => new(
            descriptor.Artifact.Role,
            descriptor.Layout,
            LogicHostRecipeExecutionAdapter.CreateCompatibility(descriptor));

    internal static bool IsAssociationCompatible(
        FrameArtifactRole priorRole,
        FrameLayoutDescriptor priorLayout,
        ProcessingCompatibilityIdentity priorProcessing,
        FrameArtifactRole currentRole,
        FrameLayoutDescriptor currentLayout,
        ProcessingCompatibilityIdentity currentProcessing)
        => priorRole == currentRole && priorLayout == currentLayout && priorProcessing == currentProcessing;

    private async Task<CentralTransientValidationJob> LoadValidationAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
        => await dbContext.CentralTransientValidationJobs.AsNoTracking()
            .Include(item => item.IdentitySlots)
            .Include(item => item.ExtractionReceipt)
            .SingleOrDefaultAsync(item => item.CentralDerivativeJobId == lease.JobId
                && item.AgentId == lease.AgentId
                && item.Job!.Status == CentralDerivativeJobStatus.Leased
                && item.Job.LeaseToken == lease.LeaseToken,
                cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The transient validation lease is stale or invalid.");

    private static CentralTransientExecutionOptionsV1 ParseExecutionOptions(CentralTransientValidationJob validation)
    {
        CentralTransientExecutionOptionsV1 options;
        try
        {
            options = CentralTransientExecutionOptionsJson.Deserialize(validation.ExecutionOptionsJson
                ?? throw new JsonException("Durable transient execution options are missing."));
        }
        catch (JsonException exception)
        {
            throw new CentralDerivativeInputRejectedException("The durable transient execution options are invalid.", exception);
        }
        var canonical = CentralTransientExecutionOptionsJson.Serialize(options);
        if (!string.Equals(canonical.Json, validation.ExecutionOptionsJson, StringComparison.Ordinal) ||
            !string.Equals(canonical.Sha256, validation.ExecutionOptionsIdentitySha256, StringComparison.Ordinal) ||
            options.SchemaVersion != CentralTransientExecutionOptionsV1.CurrentSchemaVersion ||
            options.MaskPolicy != CentralTransientMaskPolicyV1.ProfileBoundProjectedStarsV1)
        {
            throw new CentralDerivativeInputRejectedException("The durable transient execution options failed integrity validation.");
        }
        return options;
    }

    private async Task<CentralDerivativeExecutionResult> FailAsync(
        CentralDerivativeJobLease lease,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await jobService.FailAsync(lease.JobId, lease.LeaseToken, reasonCode, retryable: false, cancellationToken)
            .ConfigureAwait(false);
        return new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.TerminalFailure, null, reasonCode);
    }

    private async Task<CentralDerivativeExecutionResult> CompleteNeedsReviewAsync(
        CentralDerivativeJobLease lease,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await CentralTransientValidationOutcome.RecordNeedsReviewAsync(
            dbContext, lease.JobId, reasonCode, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await jobService.CompleteWithoutArtifactAsync(lease.JobId, lease.LeaseToken, reasonCode, cancellationToken)
            .ConfigureAwait(false);
        telemetry.RecordTransientValidation("needs-review", 0, []);
        return new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.Skipped, null, reasonCode);
    }

    private static ProcessingArtifact CreateProcessingArtifact(LogicHostProcessingInput input)
    {
        var descriptor = input.Descriptor!;
        var started = descriptor.Timing.ExposureStartedUtc;
        var ended = ProcessingArtifact.ResolveObservationEndedUtc(
            started, descriptor.Timing.ExposureEndedUtc, descriptor.Controls.EffectiveExposure);
        return new ProcessingArtifact(
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
            descriptor.Artifact.MediaType,
            descriptor.Layout,
            input.Payload,
            started,
            descriptor.Controls.EffectiveExposure,
            LogicHostRecipeExecutionAdapter.CreateCompatibility(descriptor),
            descriptor.Capture.CaptureSequence,
            descriptor.Artifact.SourceArtifactIds,
            started,
            ended);
    }

    private async Task<ContextState> ResolveContextStateAsync(
        CentralDerivativeJobLease lease,
        CentralTransientValidationJob validation,
        Guid[] evidenceIds,
        CancellationToken cancellationToken)
    {
        var contextInputs = lease.Inputs!.Where(item => item.Ordinal != 2).ToArray();
        var contextArtifactIds = contextInputs.Select(item => item.CentralArtifactId).ToArray();
        var eventArtifactIds = await dbContext.CentralTransientObservationSources.AsNoTracking()
            .Where(item => contextArtifactIds.Contains(item.CentralArtifactId))
            .Select(item => item.CentralArtifactId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var knownEvidenceIds = contextInputs.Where(item => eventArtifactIds.Contains(item.CentralArtifactId))
            .Select(item => evidenceIds[item.Ordinal]).ToArray();
        var contextJobs = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(item => contextArtifactIds.Contains(item.SourceCentralArtifactId) &&
                item.RecipeName == CentralTransientRuntime.RecipeName &&
                item.RequestedRecipeIdentitySha256 == lease.RequestedRecipeIdentitySha256 &&
                dbContext.CentralTransientValidationJobs.Any(candidate =>
                    candidate.CentralDerivativeJobId == item.Id &&
                    candidate.ProvisionalCentralDerivativeJobId == null &&
                    candidate.ExecutionOptionsIdentitySha256 == validation.ExecutionOptionsIdentitySha256))
            .Select(item => new
            {
                item.Id,
                item.SourceCentralArtifactId,
                Settled = dbContext.CentralTransientValidationJobs.Any(candidate =>
                    candidate.CentralDerivativeJobId == item.Id &&
                    candidate.ProvisionalCentralDerivativeJobId == null &&
                    candidate.ExecutionOptionsIdentitySha256 == validation.ExecutionOptionsIdentitySha256 &&
                    candidate.CommittedAtUtc != null &&
                    candidate.ExtractionReceipt != null)
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var converged = contextArtifactIds.Distinct().All(artifactId =>
            contextJobs.Any(item => item.SourceCentralArtifactId == artifactId && item.Settled));
        if (!converged && !await dbContext.CentralTransientContextDependencies.AsNoTracking()
                .AnyAsync(item => item.CentralDerivativeJobId == lease.JobId, cancellationToken).ConfigureAwait(false))
        {
            var ordinal = 0;
            foreach (var contextInput in contextInputs.OrderBy(item => item.Ordinal))
            {
                var requiredJobId = contextJobs.SingleOrDefault(item =>
                    item.SourceCentralArtifactId == contextInput.CentralArtifactId)?.Id;
                dbContext.CentralTransientContextDependencies.Add(new CentralTransientContextDependency
                {
                    CentralDerivativeJobId = lease.JobId,
                    Ordinal = ordinal++,
                    ContextCentralArtifactId = contextInput.CentralArtifactId,
                    RequiredCentralDerivativeJobId = requiredJobId,
                    RequestedRecipeIdentitySha256 = lease.RequestedRecipeIdentitySha256,
                    ExecutionOptionsIdentitySha256 = validation.ExecutionOptionsIdentitySha256!,
                    CreatedAtUtc = timeProvider.GetUtcNow()
                });
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
        }
        return new ContextState(knownEvidenceIds, converged);
    }

    private static TransientSourceEvidenceReferenceV1 CreateEvidence(
        Guid evidenceId,
        ProcessingArtifact artifact,
        string checksumSha256)
        => new(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            evidenceId,
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                new TransientArtifactReferenceV1(
                    artifact.ArtifactId,
                    artifact.Role,
                    artifact.Variant,
                    artifact.RecipeIdentitySha256,
                    checksumSha256)),
            artifact.ObservationStartedUtc!.Value,
            artifact.ObservationEndedUtc!.Value,
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("central-capture-manifest", "v2"));

    private static TransientLinearLevelsV1 CreateLevels(FrameLayoutDescriptor layout)
    {
        if (layout.BlackLevel is not { } black || layout.WhiteLevel is not { } white ||
            black < 0 || white > ushort.MaxValue || black != Math.Truncate(black) || white != Math.Truncate(white))
        {
            throw new CentralDerivativeInputRejectedException("Transient detector levels are not exact 16-bit values.");
        }
        return new TransientLinearLevelsV1((ushort)black, (ushort)white, (ushort)white);
    }

    private static CentralTransientCanonicalPayload Canonical(byte[] bytes)
        => new(bytes, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);

    internal static TransientEventState StateFor(
        TransientCandidateState candidateState,
        TransientAssessmentV1 assessment,
        bool associationAmbiguous)
        => associationAmbiguous
            ? TransientEventState.NeedsReview
            : candidateState != TransientCandidateState.Complete
            ? TransientEventState.Pending
            : assessment.Reasons.Any(reason => reason.Kind == TransientReasonKind.Limitation)
            ? TransientEventState.NeedsReview
            : assessment.Classification switch
            {
                TransientClassification.Meteor or TransientClassification.Satellite or TransientClassification.Aircraft =>
                    TransientEventState.Validated,
                TransientClassification.SensorArtifact or TransientClassification.EnvironmentalArtifact =>
                    TransientEventState.Rejected,
                _ => TransientEventState.NeedsReview
            };

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;
    private static double Distance(TransientPointV1 first, TransientPointV1 second)
        => Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private sealed record PriorEvent(
        TransientEventV1 Event,
        IReadOnlyDictionary<Guid, string> ExtractionIdentityByObservationId,
        IReadOnlyDictionary<Guid, AssociationCompatibility> AssociationCompatibilityByObservationId,
        Guid MatchedObservationId);

    private sealed record AssociationCompatibility(
        FrameArtifactRole Role,
        FrameLayoutDescriptor Layout,
        ProcessingCompatibilityIdentity Processing);

    private sealed record AssociationResult(
        TransientCandidateExtractionOutcome Extraction,
        IReadOnlyDictionary<Guid, PriorEvent> PriorEvents,
        IReadOnlySet<Guid> AmbiguousCandidateIds);

    private sealed record HybridCandidateBinding(
        TransientCandidateExtractionOutcome? Outcome,
        string? ReasonCode);

    private sealed record DetectorSource(
        TransientTemporalPosition Position,
        long CaptureSequence,
        TransientDetectorInput Input,
        TransientSensitivityV1 Sensitivity,
        ReconstructionDescriptor Descriptor);

    private sealed record ContextState(
        IReadOnlyList<Guid> KnownEventEvidenceIds,
        bool Converged);

    private static partial class Log
    {
        [LoggerMessage(2161, LogLevel.Information,
            "Central transient validation completed: JobId={JobId}, Candidates={CandidateCount}, Outcome={Outcome}")]
        public static partial void Completed(ILogger logger, Guid jobId, int candidateCount, string outcome);
    }
}

internal static class CentralTransientRuntimeReasonCodes
{
    public const string Persisted = "transient-validation.persisted";
    public const string OutputAdopted = "transient-validation.output-adopted";
    public const string InconsistentCommittedOutput = "transient-validation.inconsistent-committed-output";
    public const string InvalidWindow = "transient-validation.invalid-window";
    public const string InvalidDetectorInput = "transient-validation.invalid-detector-input";
    public const string InvalidExtraction = "transient-validation.invalid-extraction";
    public const string InvalidAssessment = "transient-validation.invalid-assessment";
    public const string InvalidExecutionPolicy = "transient-validation.invalid-execution-policy";
    public const string MaskEvidenceUnavailable = "transient-validation.mask-evidence-unavailable";
    public const string HybridCandidateNotFound = "transient-validation.hybrid-candidate-not-found";
    public const string HybridCandidateAmbiguous = "transient-validation.hybrid-candidate-ambiguous";
    public const string AttemptsExhausted = "transient-validation.attempts-exhausted";
}
