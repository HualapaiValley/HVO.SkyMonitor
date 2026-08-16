using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

internal sealed record TransientAssociationDecision(Guid? ExistingEventId, bool Ambiguous);

internal sealed class TransientWorkerService(
    IRawCaptureIngress rawIngress,
    ICameraAgentConfigurationAccessor configurationAccessor,
    IOptions<CameraAgentHostOptions> hostOptions,
    SqliteTransientRuntimeStore store,
    ITransientCandidateJournal candidateJournal,
    TransientDetectorRuntime detector,
    TransientWorkerWakeup wakeup,
    TransientWorkerState state,
    TransientWorkerTelemetry telemetry,
    ITransientRuntimeFaultInjector faultInjector,
    TimeProvider timeProvider,
    ILogger<TransientWorkerService> logger) : BackgroundService
{
    private readonly TransientDetectionOptions _options = hostOptions.Value.TransientDetection;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private CameraModuleConfig? _configuration;
    private long _reportedFrames;
    private long _reportedCandidates;
    private bool _storageUnavailable;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The hosted worker records unhealthy state and must remain available for durable recovery after an iteration failure.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Mode is TransientOperatingMode.Off or TransientOperatingMode.Central)
        {
            state.Set(TransientWorkerAvailability.Disabled, "mode-disabled", 0, 0);
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                await InitializeAsync(stoppingToken).ConfigureAwait(false);
                if (state.Snapshot.Availability == TransientWorkerAvailability.Starting)
                {
                    state.Set(TransientWorkerAvailability.Healthy, "ready", 0, 0);
                }
                _storageUnavailable = false;
                worked = await ProcessCandidateAsync(stoppingToken).ConfigureAwait(false);
                if (!worked && !_storageUnavailable)
                {
                    worked = await ProcessFrameAsync(stoppingToken).ConfigureAwait(false);
                }
                if (!_storageUnavailable)
                {
                    var totals = await store.ReadTotalsAsync(stoppingToken).ConfigureAwait(false);
                    telemetry.RecordBacklog(totals.Frames - _reportedFrames, "frames");
                    telemetry.RecordBacklog(totals.Candidates - _reportedCandidates, "candidates");
                    _reportedFrames = totals.Frames;
                    _reportedCandidates = totals.Candidates;
                    state.Set(
                        totals.Quarantined > 0 ? TransientWorkerAvailability.Degraded : TransientWorkerAvailability.Healthy,
                        totals.Quarantined > 0 ? "quarantined-work" : "ready",
                        totals.Frames,
                        totals.Candidates);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                state.Set(TransientWorkerAvailability.Unhealthy, "worker-failure", 0, 0);
                TransientWorkerLog.Failed(logger, exception.GetType().Name, exception);
            }
            if (!worked)
            {
                await wakeup.WaitAsync(
                    TimeSpan.FromMilliseconds(_options.WorkerPollIntervalMilliseconds),
                    stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async ValueTask<bool> ProcessFrameAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var frame = await store.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return false;
        }
        var started = Stopwatch.GetTimestamp();
        var failureStage = "load";
        using var activity = TransientWorkerTelemetry.ActivitySource.StartActivity("transient.causal");
        try
        {
            var loaded = await store.LoadWindowAsync(
                frame.AgentId, frame.CaptureSequence, [-2, -1, 0], cancellationToken).ConfigureAwait(false);
            if (!loaded.ContainsKey(0))
            {
                throw new InvalidDataException("Transient target evidence is missing.");
            }
            if (loaded.Count != 3)
            {
                await CompleteFrameAsync(
                    frame, "causal-context-pending", causalSucceeded: false, cancellationToken).ConfigureAwait(false);
                Record("causal", "pending-context", started, activity);
                return true;
            }
            failureStage = "input";
            var sources = await detector.CreateSourcesAsync(
                _configuration!, _options, loaded, cancellationToken).ConfigureAwait(false);
            failureStage = "background";
            var background = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
                TransientTemporalBackgroundKind.CausalProvisional,
                sources[0],
                [sources[-2], sources[-1]],
                [],
                TimeSpan.FromSeconds(_options.MaximumAdjacentStartIntervalSeconds)), cancellationToken);
            if (background.Status != TransientTemporalBackgroundStatus.Produced || background.Product is null)
            {
                await CompleteFrameAsync(
                    frame,
                    background.ReasonCode ?? "causal-background-failed",
                    causalSucceeded: false,
                    cancellationToken)
                    .ConfigureAwait(false);
                Record("causal", background.Status.ToString(), started, activity);
                return true;
            }
            failureStage = "probe";
            var probeSlots = CreateProbeSlots(32);
            var probe = TransientDetectorRuntime.Extract(
                frame.AgentId,
                timeProvider.GetUtcNow(),
                background.Product,
                sources,
                probeSlots,
                centered: false,
                cancellationToken);
            if (probe.Status == TransientCandidateExtractionStatus.NoCandidate)
            {
                await CompleteFrameAsync(
                    frame,
                    TransientCandidateExtractionReasonCodes.NoCandidate,
                    causalSucceeded: true,
                    cancellationToken)
                    .ConfigureAwait(false);
                Record("causal", "no-candidate", started, activity);
                return true;
            }
            if (IsBoundedSceneLimit(probe))
            {
                await CompleteFrameAsync(
                    frame,
                    probe.ReasonCode ?? "causal-extraction-limit-exceeded",
                    causalSucceeded: false,
                    cancellationToken)
                    .ConfigureAwait(false);
                Record("causal", "limit-exceeded", started, activity);
                return true;
            }
            if (probe.Status != TransientCandidateExtractionStatus.Produced)
            {
                throw new TransientWorkerExecutionException(probe.ReasonCode ?? "causal-extraction-failed", retryable: false);
            }
            failureStage = "allocation";
            var allocations = await store.ReadAllocationsAsync(frame.RawCaptureRowId, cancellationToken).ConfigureAwait(false);
            if (allocations.Count == 0)
            {
                allocations = await AllocateCandidatesAsync(frame, probe.Candidates, cancellationToken).ConfigureAwait(false);
            }
            if (allocations.Count != probe.Candidates.Count)
            {
                throw new TransientWorkerExecutionException("transient-runtime.allocation-count-conflict", retryable: false);
            }
            var slots = CreateProbeSlots(32);
            for (var index = 0; index < allocations.Count; index++)
            {
                slots[index] = new TransientCandidateIdentitySlot(
                    allocations[index].CandidateId,
                    allocations[index].EventId);
            }
            failureStage = "canonical";
            var canonical = TransientDetectorRuntime.Extract(
                frame.AgentId,
                allocations[0].AllocatedUtc,
                background.Product,
                sources,
                slots,
                centered: false,
                cancellationToken);
            if (canonical.Status != TransientCandidateExtractionStatus.Produced || canonical.Descriptor is null ||
                canonical.Candidates.Count != allocations.Count)
            {
                throw new TransientWorkerExecutionException("transient-runtime.canonical-causal-conflict", retryable: false);
            }
            var reservationSources = sources.OrderBy(static pair => pair.Key)
                .Select(static pair => pair.Value.Input.Descriptor.Source).ToArray();
            for (var index = 0; index < allocations.Count; index++)
            {
                var allocation = allocations[index];
                failureStage = "reservation";
                await candidateJournal.ReserveAsync(new TransientCandidateReservation(
                    allocation.CandidateId,
                    allocation.EventId,
                    frame.AgentId,
                    reservationSources), cancellationToken).ConfigureAwait(false);
                failureStage = "extraction-persist";
                await store.PersistCausalExtractionAsync(
                    allocation.CandidateId, canonical.Descriptor, cancellationToken).ConfigureAwait(false);
                failureStage = "candidate-persist";
                await candidateJournal.PersistCandidateAsync(
                    allocation.CandidateId,
                    allocation.EventId,
                    canonical.Candidates[index],
                    cancellationToken).ConfigureAwait(false);
                faultInjector.Inject(TransientRuntimeFaultPoint.AfterCandidateJournalCommit);
                if (_options.Mode == TransientOperatingMode.Hybrid)
                {
                    failureStage = "handoff";
                    await PersistHybridSubmissionAsync(canonical.Candidates[index], cancellationToken).ConfigureAwait(false);
                    faultInjector.Inject(TransientRuntimeFaultPoint.AfterHandoffJournalCommit);
                    await store.MarkCandidateCompletedAsync(allocation.CandidateId, cancellationToken).ConfigureAwait(false);
                }
            }
            await CompleteFrameAsync(
                frame, "causal-candidate-persisted", causalSucceeded: true, cancellationToken).ConfigureAwait(false);
            wakeup.Signal();
            Record("causal", "candidate", started, activity);
            return true;
        }
        catch (SqliteException exception) when (IsIntegrityFailure(exception))
        {
            await store.QuarantineAsync(
                frame.RawCaptureRowId, "transient-runtime.sqlite-integrity", cancellationToken).ConfigureAwait(false);
            Record("causal", "quarantined", started, activity);
            return true;
        }
        catch (SqliteException exception)
        {
            MarkStorageUnavailable(
                "causal", SqliteStorageReason(exception), started, activity);
            return false;
        }
        catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException or
                                          TransientCandidateIdentityConflictException or TransientWorkerExecutionException)
        {
            var reason = exception is TransientWorkerExecutionException runtime
                ? runtime.ReasonCode ?? "transient-runtime.execution-invalid"
                : exception is FileNotFoundException
                    ? "transient-runtime.evidence-missing"
                    : $"transient-runtime.{failureStage}-invalid";
            await store.QuarantineAsync(frame.RawCaptureRowId, reason, cancellationToken).ConfigureAwait(false);
            state.Set(TransientWorkerAvailability.Degraded, reason, 0, 0);
            TransientWorkerLog.Outcome(logger, "causal", "quarantined", reason);
            Record("causal", "quarantined", started, activity);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MarkStorageUnavailable("causal", "transient-runtime.storage-io", started, activity);
            return false;
        }
    }

    internal static bool IsBoundedSceneLimit(TransientCandidateExtractionOutcome outcome)
        => outcome.Status == TransientCandidateExtractionStatus.LimitExceeded &&
           (string.Equals(
                outcome.ReasonCode,
                TransientCandidateExtractionReasonCodes.CandidateLimit,
                StringComparison.Ordinal) ||
            string.Equals(outcome.Field, "options.maximumForegroundPixels", StringComparison.Ordinal));

    internal async ValueTask<bool> ProcessCandidateAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var allocations = await store.ReadPendingCandidatesAsync(cancellationToken).ConfigureAwait(false);
        var allocation = allocations.FirstOrDefault(static value => value.CausalExtraction is not null);
        if (allocation is null)
        {
            return false;
        }
        try
        {
            var entry = await candidateJournal.ReadAsync(allocation.CandidateId, cancellationToken).ConfigureAwait(false);
            if (entry?.Candidate is null)
            {
                return false;
            }
            if (_options.Mode == TransientOperatingMode.Hybrid)
            {
                await PersistHybridSubmissionAsync(entry.Candidate, cancellationToken).ConfigureAwait(false);
                faultInjector.Inject(TransientRuntimeFaultPoint.AfterHandoffJournalCommit);
                await store.MarkCandidateCompletedAsync(allocation.CandidateId, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var frame = await store.ReadFrameAsync(allocation.TargetRawCaptureRowId, cancellationToken).ConfigureAwait(false);
            var deadlineExpired = timeProvider.GetUtcNow() >= entry.TimeoutUtc;
            var causalWindowComplete = await store.IsCausalWindowCompleteAsync(
                frame.AgentId, frame.CaptureSequence, cancellationToken).ConfigureAwait(false);
            if (!causalWindowComplete && !deadlineExpired)
            {
                return false;
            }
            var loaded = causalWindowComplete
                ? await store.LoadWindowAsync(
                    frame.AgentId, frame.CaptureSequence, [-2, -1, 0, 1, 2], cancellationToken).ConfigureAwait(false)
                : new Dictionary<int, TransientLoadedFrame>();
            var started = Stopwatch.GetTimestamp();
            using var activity = TransientWorkerTelemetry.ActivitySource.StartActivity("transient.centered");
            TransientCandidateExtractionDescriptorV1 observationExtraction = allocation.CausalExtraction!;
            var forcedState = deadlineExpired ? TransientEventState.NeedsReview : (TransientEventState?)null;
            if (causalWindowComplete && loaded.Count == 5)
            {
                var sources = await detector.CreateSourcesAsync(
                    _configuration!, _options, loaded, cancellationToken).ConfigureAwait(false);
                var knownEvents = await store.ReadKnownEventEvidenceIdsAsync(
                    frame.AgentId, frame.CaptureSequence, cancellationToken).ConfigureAwait(false);
                var background = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
                    TransientTemporalBackgroundKind.CenteredFinal,
                    sources[0],
                    [sources[-2], sources[-1], sources[1], sources[2]],
                    knownEvents,
                    TimeSpan.FromSeconds(_options.MaximumAdjacentStartIntervalSeconds),
                    deadlineExpired), cancellationToken);
                if (background.Status == TransientTemporalBackgroundStatus.Produced && background.Product is not null)
                {
                    var probe = TransientDetectorRuntime.Extract(
                        frame.AgentId,
                        allocation.AllocatedUtc,
                        background.Product,
                        sources,
                        CreateProbeSlots(32),
                        centered: true,
                        cancellationToken);
                    if (probe.Status == TransientCandidateExtractionStatus.NoCandidate)
                    {
                        forcedState = TransientEventState.Rejected;
                    }
                    else if (probe.Status == TransientCandidateExtractionStatus.Produced)
                    {
                        var matches = probe.Candidates
                            .Select((candidate, index) => (candidate, index))
                            .Where(value => GeometryMatches(entry.Candidate, value.candidate, _options.Association))
                            .ToArray();
                        if (matches.Length == 1)
                        {
                            var slots = CreateProbeSlots(32);
                            slots[matches[0].index] = new TransientCandidateIdentitySlot(
                                allocation.CandidateId,
                                allocation.EventId);
                            var centered = TransientDetectorRuntime.Extract(
                                frame.AgentId,
                                allocation.AllocatedUtc,
                                background.Product,
                                sources,
                                slots,
                                centered: true,
                                cancellationToken);
                            if (centered.Status == TransientCandidateExtractionStatus.Produced && centered.Descriptor is not null)
                            {
                                observationExtraction = centered.Descriptor;
                            }
                            else
                            {
                                forcedState = TransientEventState.NeedsReview;
                            }
                        }
                        else
                        {
                            forcedState = TransientEventState.NeedsReview;
                        }
                    }
                    else
                    {
                        forcedState = TransientEventState.NeedsReview;
                    }
                }
                else
                {
                    forcedState = TransientEventState.NeedsReview;
                }
            }
            await store.PersistObservationExtractionAsync(
                allocation.CandidateId, observationExtraction, cancellationToken).ConfigureAwait(false);
            var eventCandidates = await store.ReadEventCandidatesAsync(
                allocation.EventId, cancellationToken).ConfigureAwait(false);
            var assessmentObservations = eventCandidates
                .Where(static candidate => candidate.ObservationExtraction is not null)
                .Select((candidate, ordinal) => TransientObservationFactory.CreateAssessmentObservation(
                    new TransientObservationPromotionRequest(
                        candidate.CandidateId,
                        candidate.ObservationId,
                        ordinal,
                        candidate.ObservationExtraction!)))
                .ToArray();
            if (!assessmentObservations.Any(value =>
                    value.Observation.Extraction.OriginatingCandidateId == allocation.CandidateId))
            {
                throw new InvalidDataException("Accumulated transient event omitted the current candidate observation.");
            }
            var eventHistory = await store.ReadEventHistoryAsync(
                allocation.EventId, allocation.CandidateId, cancellationToken).ConfigureAwait(false);
            var previousEvent = eventHistory.OrderBy(static value => value.Version).LastOrDefault();
            var priorAssessments = eventCandidates
                .Where(candidate => candidate.CandidateId != allocation.CandidateId && candidate.AssessmentExecution is not null)
                .Select(static candidate => candidate.AssessmentExecution!.Assessment)
                .ToArray();
            var assessment = TransientAssessmentFactory.Create(new TransientAssessmentExecutionRequest(
                allocation.EventId,
                allocation.AssessmentId,
                allocation.AllocatedUtc,
                TransientAssessmentAuthority.Authoritative,
                assessmentObservations,
                TransientDetectorRuntime.AssessmentOptions,
                priorAssessments), cancellationToken);
            if (assessment.Status != TransientAssessmentExecutionStatus.Produced || assessment.Descriptor is null)
            {
                forcedState = TransientEventState.NeedsReview;
            }
            var assessmentValue = assessment.Descriptor?.Assessment
                ?? throw new InvalidDataException("Transient assessment did not produce a durable descriptor.");
            await store.PersistAssessmentExecutionAsync(
                allocation.CandidateId, assessment.Descriptor!, cancellationToken).ConfigureAwait(false);
            var eventState = forcedState ?? ResolveEventState(
                assessmentValue,
                entry.Candidate,
                allocation.AssociationAmbiguous);
            var eventObservations = assessmentObservations
                .Select(static value => value.Observation)
                .ToArray();
            var eventValue = new TransientEventV1(
                TransientEventV1.CurrentSchemaVersion,
                allocation.EventId,
                allocation.EventVersionId,
                (previousEvent?.Version ?? 0) + 1,
                previousEvent?.EventVersionId,
                previousEvent?.VersionCreatedUtc,
                frame.AgentId,
                eventState,
                previousEvent?.EventCreatedUtc ?? allocation.AllocatedUtc,
                timeProvider.GetUtcNow(),
                eventObservations.Min(static value => value.Source.ObservationStartedUtc),
                eventObservations.Max(static value => value.Source.ObservationEndedUtc),
                eventObservations,
                assessment.AssessmentHistory.ToArray(),
                [],
                [],
                []);
            var receipt = new TransientFinalizationReceiptV1(
                TransientFinalizationReceiptV1.CurrentSchemaVersion,
                allocation.CandidateId,
                allocation.EventId,
                eventValue,
                new string('0', 64));
            receipt = receipt with
            {
                ReceiptIdentitySha256 = TransientCandidateDeliveryJson.ComputeFinalizationIdentitySha256(receipt)
            };
            await candidateJournal.PersistFinalizationAsync(
                allocation.CandidateId, allocation.EventId, receipt, cancellationToken).ConfigureAwait(false);
            faultInjector.Inject(TransientRuntimeFaultPoint.AfterFinalizationJournalCommit);
            await store.MarkCandidateCompletedAsync(allocation.CandidateId, cancellationToken).ConfigureAwait(false);
            Record("centered", eventState.ToString(), started, activity);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (IsIntegrityFailure(exception))
        {
            await store.QuarantineCandidateAsync(
                allocation.CandidateId,
                allocation.EventId,
                "transient-runtime.centered-sqlite-integrity",
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException exception)
        {
            MarkStorageUnavailable(
                "centered", SqliteStorageReason(exception), Stopwatch.GetTimestamp(), activity: null);
            return false;
        }
        catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException or
                                          TransientCandidateIdentityConflictException or TransientWorkerExecutionException)
        {
            var reason = exception is TransientWorkerExecutionException runtime
                ? runtime.ReasonCode ?? "transient-runtime.centered-invalid"
                : exception is FileNotFoundException
                    ? "transient-runtime.centered-evidence-missing"
                    : "transient-runtime.centered-evidence-invalid";
            await store.QuarantineCandidateAsync(
                allocation.CandidateId, allocation.EventId, reason, cancellationToken).ConfigureAwait(false);
            TransientWorkerLog.Outcome(logger, "centered", "quarantined", reason);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MarkStorageUnavailable(
                "centered", "transient-runtime.centered-storage-io", Stopwatch.GetTimestamp(), activity: null);
            return false;
        }
    }

    private async ValueTask<IReadOnlyList<TransientRuntimeCandidate>> AllocateCandidatesAsync(
        TransientRuntimeFrame frame,
        IReadOnlyList<TransientCandidateV1> probes,
        CancellationToken cancellationToken)
    {
        var previous = frame.CaptureSequence > 1
            ? await store.ReadAdjacentCandidatesAsync(
                frame.AgentId, frame.CaptureSequence - 1, cancellationToken).ConfigureAwait(false)
            : [];
        var decisions = ResolveAssociations(previous, probes, _options.Association);
        var allocatedUtc = timeProvider.GetUtcNow();
        var output = new List<TransientRuntimeCandidate>(probes.Count);
        for (var index = 0; index < probes.Count; index++)
        {
            var decision = decisions[index];
            var allocation = new TransientRuntimeCandidate(
                Guid.NewGuid(),
                decision.ExistingEventId ?? Guid.NewGuid(),
                frame.RawCaptureRowId,
                index,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                allocatedUtc,
                decision.Ambiguous,
                0,
                allocatedUtc,
                null,
                null,
                null);
            output.Add(allocation);
        }
        return await store.AllocateBatchAsync(frame.RawCaptureRowId, output, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<TransientAssociationDecision> ResolveAssociations(
        IReadOnlyList<TransientCandidateV1> previous,
        IReadOnlyList<TransientCandidateV1> probes,
        TransientCandidateAssociationOptions options)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(options);
        var matchesByProbe = probes
            .Select(probe => previous
                .Select((candidate, index) => (candidate, index))
                .Where(value => AssociationMatches(value.candidate, probe, options))
                .ToArray())
            .ToArray();
        var previousDegrees = Enumerable.Range(0, previous.Count)
            .Select(previousIndex => matchesByProbe.Count(matches =>
                matches.Any(value => value.index == previousIndex)))
            .ToArray();
        var output = new TransientAssociationDecision[probes.Count];
        for (var index = 0; index < probes.Count; index++)
        {
            var matches = matchesByProbe[index];
            var uniqueMatch = matches.Length == 1 && previousDegrees[matches[0].index] == 1;
            var ambiguous = matches.Length > 1 || matches.Length == 1 && !uniqueMatch;
            output[index] = new TransientAssociationDecision(
                uniqueMatch ? matches[0].candidate.EventId : null,
                ambiguous);
        }
        return output;
    }

    private async ValueTask PersistHybridSubmissionAsync(
        TransientCandidateV1 candidate,
        CancellationToken cancellationToken)
    {
        using var activity = TransientWorkerTelemetry.ActivitySource.StartActivity("transient.handoff");
        var envelope = new TransientCandidateSubmissionEnvelopeV1(
            TransientCandidateSubmissionEnvelopeV1.CurrentSchemaVersion,
            candidate.CandidateId,
            candidate.EventId,
            candidate,
            candidate.Extraction.RecipeIdentitySha256,
            candidate.Provenance.ProcessingProfileIdentity,
            new string('0', 64));
        envelope = envelope with
        {
            SubmissionIdentitySha256 = TransientCandidateDeliveryJson.ComputeSubmissionIdentitySha256(envelope)
        };
        await candidateJournal.PersistSubmissionAsync(
            candidate.CandidateId, candidate.EventId, envelope, cancellationToken).ConfigureAwait(false);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    private async ValueTask CompleteFrameAsync(
        TransientRuntimeFrame frame,
        string reason,
        bool causalSucceeded,
        CancellationToken cancellationToken)
    {
        await store.MarkCausalCompletionAsync(
            frame.RawCaptureRowId, reason, causalSucceeded, cancellationToken).ConfigureAwait(false);
        await store.RetireBeforeAsync(frame.AgentId, frame.CaptureSequence - 1, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsRetryableStorageFailure(SqliteException exception)
        => exception.SqliteErrorCode is 5 or 6 or 9 or 10 or 13 or 14 or 15;

    internal static bool IsIntegrityFailure(SqliteException exception)
        => exception.SqliteErrorCode is 19 or 20 or 24;

    internal static bool IsDatabaseCorruption(SqliteException exception)
        => exception.SqliteErrorCode is 11 or 26;

    private static string SqliteStorageReason(SqliteException exception)
        => IsRetryableStorageFailure(exception)
            ? $"transient-runtime.sqlite-storage-{exception.SqliteErrorCode}"
            : $"transient-runtime.sqlite-unavailable-{exception.SqliteErrorCode}";

    private void MarkStorageUnavailable(string stage, string reason, long started, Activity? activity)
    {
        _storageUnavailable = true;
        state.Set(TransientWorkerAvailability.Unhealthy, reason, 0, 0);
        telemetry.Record(stage, "storage-unavailable", Stopwatch.GetElapsedTime(started));
        activity?.SetTag("transient.stage", stage);
        activity?.SetTag("transient.outcome", "storage-unavailable");
        activity?.SetStatus(ActivityStatusCode.Error, reason);
        TransientWorkerLog.Outcome(logger, stage, "storage-unavailable", reason);
    }

    private static TransientCandidateIdentitySlot[] CreateProbeSlots(int count)
        => Enumerable.Range(0, count)
            .Select(static _ => new TransientCandidateIdentitySlot(Guid.NewGuid(), Guid.NewGuid()))
            .ToArray();

    internal static bool AssociationMatches(
        TransientCandidateV1 previous,
        TransientCandidateV1 current,
        TransientCandidateAssociationOptions options)
    {
        var previousSource = previous.ContextSources.Single(source => source.EvidenceId == previous.CenterEvidenceId);
        var currentSource = current.ContextSources.Single(source => source.EvidenceId == current.CenterEvidenceId);
        return currentSource.ObservationStartedUtc > previousSource.ObservationStartedUtc &&
               currentSource.ObservationStartedUtc - previousSource.ObservationStartedUtc <=
                   TimeSpan.FromSeconds(options.MaximumStartIntervalSeconds) &&
               GeometryMatches(previous, current, options);
    }

    internal static bool GeometryMatches(
        TransientCandidateV1 previous,
        TransientCandidateV1 current,
        TransientCandidateAssociationOptions options)
    {
        if (previous.Geometry?.Polyline is not { Count: >= 2 } first ||
            current.Geometry?.Polyline is not { Count: >= 2 } second)
        {
            return false;
        }
        var firstAxis = Axis(first);
        var secondAxis = Axis(second);
        var alignment = Math.Abs(firstAxis.X * secondAxis.X + firstAxis.Y * secondAxis.Y);
        var endpointGap = new[]
        {
            Distance(first[0], second[0]), Distance(first[0], second[^1]),
            Distance(first[^1], second[0]), Distance(first[^1], second[^1])
        }.Min();
        return alignment >= options.MinimumAbsolutePrincipalAxisAlignment &&
               endpointGap <= options.MaximumEndpointGapPixels;
    }

    private static (double X, double Y) Axis(IReadOnlyList<TransientPointV1> points)
    {
        var x = points[^1].X - points[0].X;
        var y = points[^1].Y - points[0].Y;
        var length = Math.Sqrt(x * x + y * y);
        return length > 0 ? (x / length, y / length) : (0, 0);
    }

    private static double Distance(TransientPointV1 first, TransientPointV1 second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static TransientEventState ResolveEventState(
        TransientAssessmentV1 assessment,
        TransientCandidateV1 candidate,
        bool associationAmbiguous)
    {
        if (associationAmbiguous || assessment.Classification == TransientClassification.Unknown ||
            candidate.Reasons.Any(static reason => reason.Kind == TransientReasonKind.Limitation))
        {
            return TransientEventState.NeedsReview;
        }
        return assessment.Classification is TransientClassification.Meteor or
            TransientClassification.Satellite or TransientClassification.Aircraft
                ? TransientEventState.Validated
                : TransientEventState.Rejected;
    }

    private void Record(string stage, string outcome, long started, Activity? activity)
    {
        var duration = Stopwatch.GetElapsedTime(started);
        telemetry.Record(stage, outcome, duration);
        activity?.SetTag("transient.stage", stage);
        activity?.SetTag("transient.outcome", outcome);
        activity?.SetStatus(ActivityStatusCode.Ok);
        TransientWorkerLog.Outcome(logger, stage, outcome, "none");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "The second check is required after asynchronous gate acquisition because another caller can initialize concurrently.")]
    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_configuration is not null)
        {
            return;
        }
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_configuration is null)
            {
                await rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
                var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
                await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _configuration = configuration;
            }
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public override void Dispose()
    {
        _initializeGate.Dispose();
        base.Dispose();
    }
}

internal static partial class TransientWorkerLog
{
    [LoggerMessage(EventId = 2200, Level = LogLevel.Information,
        Message = "Transient worker stage {Stage} completed with {Outcome}; reason {Reason}")]
    internal static partial void Outcome(ILogger logger, string stage, string outcome, string reason);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Error,
        Message = "Transient worker iteration failed with {FailureType}")]
    internal static partial void Failed(ILogger logger, string failureType, Exception exception);
}
