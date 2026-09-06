using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class RawCaptureIngress :
    IRawCaptureIngress,
    IRawIngressRetentionHolds,
    IRawIngressRecoveryControl,
    IRawIngressPressureReporter,
    IRawIngressWakeupReporter,
    ICaptureLaneStore,
    IOperationsQueueSnapshotRefresher,
    IProjectedSceneStageOwnerProvider,
    IDisposable
{
    private readonly CameraAgentHostOptions _options;
    private readonly IStorageCapacityProvider _capacityProvider;
    private readonly RawIngressState _state;
    private readonly TimeProvider _timeProvider;
    private readonly RawIngressTelemetry _telemetry;
    private readonly ILogger<RawCaptureIngress> _logger;
    private readonly IRawIngressFaultInjector _faultInjector;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _acceptGate = new(1, 1);
    private readonly SqliteRawCaptureJournal _journal;
    private readonly RawIngressFileStore _files;
    private readonly CaptureLanePolicy _lanePolicy;
    private readonly SqliteCaptureLaneStore _laneStore;
    private readonly CaptureLaneState? _laneState;
    private readonly CaptureLaneTelemetry? _laneTelemetry;
    private readonly CapturePipelineTraceStore? _captureTraceStore;
    private bool _initialized;
    private bool _journalValidated;
    private bool _capacityRevalidationRequired;
    private long _minimumRecoveryCapacityBytes;
    private FileStream? _processLock;
    private readonly IProjectedSceneStagingReconciler? _projectedSceneStaging;
    private readonly ProjectedSceneStageLifecycleCoordinator? _projectedSceneLifecycle;
    private readonly ProcessingGraphOperationsCoordinator? _graphOperations;

    public RawCaptureIngress(
        IOptions<CameraAgentHostOptions> options,
        IStorageCapacityProvider capacityProvider,
        RawIngressState state,
        TimeProvider timeProvider,
        RawIngressTelemetry telemetry,
        ILogger<RawCaptureIngress> logger,
        IRawIngressFaultInjector faultInjector,
        CaptureLanePolicy? lanePolicy = null,
        ICaptureLaneFaultInjector? laneFaultInjector = null,
        CaptureLaneState? laneState = null,
        CaptureLaneTelemetry? laneTelemetry = null,
        CapturePipelineTraceStore? captureTraceStore = null,
        IProjectedSceneStagingReconciler? projectedSceneStaging = null,
        ProjectedSceneStageLifecycleCoordinator? projectedSceneLifecycle = null,
        ProcessingGraphOperationsCoordinator? graphOperations = null)
    {
        _options = options.Value;
        _capacityProvider = capacityProvider;
        _state = state;
        _timeProvider = timeProvider;
        _telemetry = telemetry;
        _logger = logger;
        _faultInjector = faultInjector;
        _lanePolicy = lanePolicy ?? new CaptureLanePolicy(options);
        _laneState = laneState;
        _laneTelemetry = laneTelemetry;
        _captureTraceStore = captureTraceStore;
        _projectedSceneStaging = projectedSceneStaging;
        _projectedSceneLifecycle = projectedSceneLifecycle;
        _graphOperations = graphOperations;
        var resolvedLaneFaultInjector = laneFaultInjector ?? new NullCaptureLaneFaultInjector();
        var root = Path.GetFullPath(_options.RawIngressRoot);
        _journal = new SqliteRawCaptureJournal(
            Path.Combine(root, "journal", "raw-ingress.db"),
            _options.RawIngressSqliteBusyTimeoutSeconds,
            telemetry.RecordLockWait,
            faultInjector,
            telemetry.RecordTransaction,
            telemetry.RecordCheckpoint,
            _options.CaptureDistribution,
            resolvedLaneFaultInjector,
            timeProvider.GetUtcNow,
            _options.TransientDetection);
        _files = new RawIngressFileStore(
            root,
            faultInjector,
            telemetry.RecordFileFlush,
            telemetry.RecordDirectorySync);
        _laneStore = new SqliteCaptureLaneStore(
            root,
            _options.RawIngressSqliteBusyTimeoutSeconds,
            _options.CaptureDistribution,
            _lanePolicy,
            timeProvider,
            resolvedLaneFaultInjector,
            laneTelemetry is null ? null : new Action<TimeSpan>(laneTelemetry.RecordLockWait));
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SemaphoreSlim? lifecycleGate = null;
        var lifecycleAcquired = false;
        try
        {
            if (_initialized)
            {
                return;
            }
            _faultInjector.Inject(RawIngressFaultPoint.BeforeInitializationLifecycleLock);
            lifecycleGate = RawIngressLifecycleLock.ForRoot(_options.RawIngressRoot);
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lifecycleAcquired = true;
            var wasUnhealthy = _state.Snapshot.Availability == RawIngressAvailability.Unhealthy;
            try
            {
                _files.EnsureRootIsPhysical();
                _processLock ??= AcquireProcessLock();
                using var initializationActivity = RawIngressTelemetry.ActivitySource.StartActivity("raw-ingress.initialize");
                Volatile.Write(ref _journalValidated, false);
                await SqliteTransientRuntimeStore.ValidateExistingRuntimeSchemaAsync(
                    _options.RawIngressRoot,
                    _options.RawIngressSqliteBusyTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                await _journal.InitializeAsync(_lanePolicy.Definitions, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _journalValidated, true);
                initializationActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                using var reconciliationActivity = RawIngressTelemetry.ActivitySource.StartActivity("raw-ingress.reconcile");
                var reconciliation = await new RawIngressReconciler(
                    _options.RawIngressRoot,
                    _journal,
                    _telemetry.RecordFileFlush,
                    _telemetry.RecordDirectorySync,
                    _lanePolicy.Definitions)
                     .RunAsync(cancellationToken).ConfigureAwait(false);
                var projectedSceneBacklog = 0;
                if (_projectedSceneStaging is not null)
                {
                    using var stageLease = _projectedSceneLifecycle is null
                        ? null
                        : await _projectedSceneLifecycle.AcquireReconciliationLeaseAsync(cancellationToken).ConfigureAwait(false);
                    var ownedStageKeys = await GetOwnedStageKeysAsync(cancellationToken).ConfigureAwait(false);
                    var protectedStageKeys = new HashSet<string>(ownedStageKeys, StringComparer.Ordinal);
                    if (stageLease is not null) protectedStageKeys.UnionWith(stageLease.PendingStageKeys);
                    var staged = await _projectedSceneStaging.ReconcileAsync(protectedStageKeys, cancellationToken).ConfigureAwait(false);
                    projectedSceneBacklog = staged.BacklogCount;
                    reconciliation = reconciliation with { ProjectedSceneStageBacklog = projectedSceneBacklog };
                }
                await _laneStore.InitializeLanesAsync(cancellationToken).ConfigureAwait(false);
                await RefreshLaneStateAsync(cancellationToken).ConfigureAwait(false);
                reconciliationActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                _telemetry.RecordReconciliation(reconciliation);
                _logger.RawIngressSqliteResult("checkpoint", "success");
                var held = await _journal.ReadHeldTotalsAsync(cancellationToken).ConfigureAwait(false);
                var health = await _journal.ReadHealthTotalsAsync(cancellationToken).ConfigureAwait(false);
                RevalidateCapacityAfterFailure();
                var baseAvailability = health.FailureCount > 0
                    ? RawIngressAvailability.Unhealthy
                    : health.QuarantineCount > 0 || reconciliation.IndexProjectionFailures > 0
                        ? RawIngressAvailability.Degraded
                        : RawIngressAvailability.Accepting;
                var baseReason = baseAvailability == RawIngressAvailability.Accepting
                    ? "accepting"
                    : reconciliation.IndexProjectionFailures > 0
                        ? "index-projection-failed"
                        : "reconciliation-findings";
                _state.Set(
                    baseAvailability,
                    baseReason,
                    held.Count,
                    held.Bytes,
                    health.QuarantineCount,
                    health.QuarantineBytes,
                    held.Oldest);
                _state.SetProjectedSceneBacklog(projectedSceneBacklog);
                if (baseAvailability == RawIngressAvailability.Unhealthy)
                {
                    throw new InvalidDataException("Raw ingress has committed records with missing evidence.");
                }
                _logger.RawIngressReconciled(
                    reconciliation.Inspected,
                    reconciliation.Recovered,
                    reconciliation.Cleaned,
                    reconciliation.Quarantined,
                    reconciliation.MissingEvidence);
                if (reconciliation.Quarantined > 0)
                {
                    _logger.RawIngressQuarantined(reconciliation.Quarantined, reconciliation.QuarantineBytes);
                }
                if (reconciliation.IndexProjectionFailures > 0)
                {
                    _logger.RawIngressIndexProjectionFailed();
                }
                _logger.RawIngressInitialized(SqliteRawCaptureJournal.CurrentSchemaVersion, held.Count);
                if (wasUnhealthy)
                {
                    _logger.RawIngressRecovered();
                }
                Volatile.Write(ref _initialized, true);
            }
            catch (OperationCanceledException)
            {
                // Shutdown during initialization is not an integrity failure: no Critical log, availability untouched.
                throw;
            }
            catch (Exception exception)
            {
                var reason = FailureReason(exception);
                SetAvailabilityPreservingTotals(RawIngressAvailability.Unhealthy, "initialization-failed");
                _telemetry.RecordFailure("initialization", reason);
                _logger.RawIngressIntegrityFailed(reason);
                if (exception is Microsoft.Data.Sqlite.SqliteException)
                {
                    _logger.RawIngressSqliteResult("initialization", "failure");
                }
                throw;
            }
        }
        finally
        {
            if (lifecycleAcquired)
            {
                lifecycleGate!.Release();
            }
            _initializeGate.Release();
        }
    }

    public async ValueTask<IReadOnlySet<string>> GetOwnedStageKeysAsync(
        CancellationToken cancellationToken)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(entry.State, "committed", StringComparison.Ordinal)) continue;
            var parsed = CaptureContractJson.ParseManifest(entry.ManifestJson);
            if (parsed.IsValid && parsed.Document?.Manifest?.Scene is
                {
                    ProjectedSceneStageKey: { Length: 64 } stageKey
                } && stageKey.All(Uri.IsHexDigit))
                owned.Add(stageKey);
        }
        return owned;
    }

    public async ValueTask<RawCaptureReceipt?> AcceptAsync(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Result.Frame is not { } frame)
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.AgentId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var activity = RawIngressTelemetry.ActivitySource.StartActivity("raw-ingress.accept");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await _acceptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_options.RawIngressRoot);
        var lifecycleAcquired = false;
        var payloadPublished = false;
        try
        {
            var stableIds = RawCaptureDescriptorFactory.CreateStableIds(configuration, submission);
            var existingCapture = await _journal.ReadCommittedCaptureStateAsync(
                stableIds.CaptureId, cancellationToken).ConfigureAwait(false);
            if (existingCapture.Exists && !existingCapture.EvidenceRetained)
            {
                throw new RawIngressConflictException("Committed capture evidence is no longer retained.");
            }
            if (!existingCapture.Exists)
            {
                await _laneStore.EnsureCanAcceptAsync(frame.PixelData.Length, cancellationToken).ConfigureAwait(false);
            }
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lifecycleAcquired = true;
            if (!existingCapture.Exists)
            {
                EnsureCapacity(frame.PixelData.Length);
            }
            var payloadSha256 = Convert.ToHexString(SHA256.HashData(frame.PixelData.Span));
            _faultInjector.Inject(RawIngressFaultPoint.BeforeIdentityReservation);
            cancellationToken.ThrowIfCancellationRequested();

            // Sequence reservation is the point of no cancellation. From here forward the identity may become
            // externally visible in an immutable sidecar, so publication and journal commit must converge rather
            // than consume a sequence without a durable capture or risk reusing a visible identity.
            var identity = await _journal.ReserveIdentityAsync(
                configuration.AgentId,
                stableIds.CaptureId,
                stableIds.ArtifactId,
                CancellationToken.None).ConfigureAwait(false);
            _faultInjector.Inject(RawIngressFaultPoint.AfterIdentityReservation);
            var paths = _files.GetPaths(
                RawCaptureDescriptorFactory.ResolveExposureStartedUtc(submission, frame),
                identity.ArtifactId);
            ArtifactManifestV2 manifest;
            byte[] manifestJson;
            if (File.Exists(paths.PayloadAbsolutePath) || File.Exists(paths.SidecarAbsolutePath))
            {
                manifest = await RawIngressFileStore.ReadAndValidateExistingAsync(
                    paths,
                    frame.PixelData,
                    identity,
                    CancellationToken.None).ConfigureAwait(false);
                var expectedDescriptor = RawCaptureDescriptorFactory.Create(
                    configuration,
                    submission,
                    identity,
                    payloadSha256,
                    manifest.Descriptor.Timing.DurableIngressUtc);
                if (manifest.Descriptor.CycleEvidence is null)
                {
                    expectedDescriptor = expectedDescriptor with { CycleEvidence = null };
                }
                if (manifest.Descriptor.Location is null)
                {
                    expectedDescriptor = expectedDescriptor with { Location = null };
                }
                if (manifest.Descriptor.Timing.SetpointAppliedUtc is null)
                {
                    expectedDescriptor = expectedDescriptor with
                    {
                        Timing = expectedDescriptor.Timing with { SetpointAppliedUtc = null }
                    };
                }
                var existingLayout = manifest.Descriptor.Layout;
                var expectedLayout = expectedDescriptor.Layout;
                expectedDescriptor = expectedDescriptor with
                {
                    Layout = expectedLayout with
                    {
                        SampleDepthBits = existingLayout.StoredCodeTransform is null
                            ? existingLayout.SampleDepthBits
                            : expectedLayout.SampleDepthBits,
                        Readout = existingLayout.Readout is null ? null : expectedLayout.Readout,
                        StoredCodeTransform = existingLayout.StoredCodeTransform is null
                            ? null
                            : expectedLayout.StoredCodeTransform,
                        LevelCodeSpace = existingLayout.LevelCodeSpace is null
                            ? null
                            : expectedLayout.LevelCodeSpace
                    }
                };
                if (!string.Equals(
                        CaptureContractJson.ComputeDescriptorSha256(expectedDescriptor),
                        CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor),
                        StringComparison.Ordinal))
                {
                    throw new RawIngressConflictException("Existing immutable descriptor differs from the retry facts.");
                }
                manifestJson = CaptureContractJson.Serialize(manifest);
            }
            else
            {
                var previewDescriptor = RawCaptureDescriptorFactory.Create(
                    configuration,
                    submission,
                    identity,
                    payloadSha256,
                    _timeProvider.GetUtcNow());
                var previewManifest = new ArtifactManifestV2(
                    ArtifactManifestV2.CurrentSchemaVersion,
                    previewDescriptor,
                    paths.PayloadRelativePath,
                    frame.Metadata.Scene);
                var validation = previewManifest.Validate();
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException($"Raw ingress descriptor is invalid ({validation.ReasonCode}).");
                }
                _faultInjector.Inject(RawIngressFaultPoint.ValidationCompleted);
                using (var payloadActivity = RawIngressTelemetry.ActivitySource.StartActivity("payload.publish"))
                {
                    await _files.PublishPayloadAsync(paths, frame.PixelData, CancellationToken.None).ConfigureAwait(false);
                    payloadPublished = true;
                    payloadActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                }
                var descriptor = RawCaptureDescriptorFactory.Create(
                    configuration,
                    submission,
                    identity,
                    payloadSha256,
                    _timeProvider.GetUtcNow());
                manifest = new ArtifactManifestV2(
                    ArtifactManifestV2.CurrentSchemaVersion,
                    descriptor,
                    paths.PayloadRelativePath,
                    frame.Metadata.Scene);
                validation = manifest.Validate();
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException($"Raw ingress descriptor is invalid ({validation.ReasonCode}).");
                }
                manifestJson = CaptureContractJson.Serialize(manifest);
                using (var sidecarActivity = RawIngressTelemetry.ActivitySource.StartActivity("sidecar.publish"))
                {
                    await _files.PublishSidecarAsync(paths, manifestJson, CancellationToken.None).ConfigureAwait(false);
                    sidecarActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                }
            }

            var committedDescriptor = manifest.Descriptor;
            var manifestSha256 = CaptureContractJson.ComputeManifestSha256(manifestJson);
            var entry = new RawIngressJournalEntry(
                identity.AgentId,
                identity.CaptureSequence,
                identity.CaptureId,
                identity.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(committedDescriptor),
                manifestSha256,
                payloadSha256,
                frame.PixelData.Length,
                paths.PayloadRelativePath,
                paths.SidecarRelativePath,
                manifestJson,
                    committedDescriptor.Timing.ExposureStartedUtc,
                    committedDescriptor.Timing.DurableIngressUtc,
                    EvidenceOrigin: GalleryEvidenceClassifier.Classify(manifest));
            var committedTiming = submission.Result.AcquisitionTiming is null
                ? null
                : submission.Result.AcquisitionTiming with
                {
                    SetpointAppliedUtc = committedDescriptor.Timing.SetpointAppliedUtc
                };
            var committedSubmission = submission with
            {
                CycleEvidence = committedDescriptor.CycleEvidence,
                Result = submission.Result with { AcquisitionTiming = committedTiming }
            };
            ProcessingLiveExecutionSeed? liveExecution = null;
            var durableConfiguration = configuration;
            if (_graphOperations is not null)
            {
                var prepared = await _graphOperations.PrepareLiveExecutionAsync(
                    configuration,
                    identity.CaptureId,
                    identity.ArtifactId,
                    committedDescriptor.Timing.DurableIngressUtc,
                    CancellationToken.None).ConfigureAwait(false);
                liveExecution = prepared.Seed;
                durableConfiguration = prepared.Configuration;
            }
            var context = CaptureLaneEnvelopeSerializer.Serialize(
                durableConfiguration,
                committedSubmission);
            RawIngressOutcome outcome;
            using (var commitActivity = RawIngressTelemetry.ActivitySource.StartActivity("sqlite.commit"))
            {
                _faultInjector.Inject(RawIngressFaultPoint.BeforeJournalCommit);
                outcome = await _journal.CommitAsync(
                    entry,
                    context.Json,
                    context.Sha256,
                    _lanePolicy.Definitions,
                    liveExecution,
                    CancellationToken.None).ConfigureAwait(false);
                if (outcome == RawIngressOutcome.Committed)
                {
                    _graphOperations?.NotifyLiveWorkAccepted();
                }
                if (outcome == RawIngressOutcome.Committed && _laneTelemetry is not null)
                {
                    foreach (var laneOutcome in await _journal.ReadLaneOutcomesAsync(
                                 entry.CaptureId, CancellationToken.None).ConfigureAwait(false))
                    {
                        _laneTelemetry.RecordWork(laneOutcome.Lane, laneOutcome.Required, laneOutcome.State);
                        _logger.CaptureLaneWorkCreated(laneOutcome.Lane, laneOutcome.Required, laneOutcome.State);
                    }
                }
                _faultInjector.Inject(RawIngressFaultPoint.AfterJournalCommit);
                commitActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            }
            _logger.RawIngressSqliteResult("commit", "success");
            var held = await _journal.ReadHeldTotalsAsync(CancellationToken.None).ConfigureAwait(false);
            _state.UpdateBaseHeldData(held.Count, held.Bytes, held.Oldest);
            var prior = _state.GetBaseSnapshot();
            var receipt = new RawCaptureReceipt(
                outcome,
                manifest with { Scene = null },
                new StoredFrameReference(
                    paths.PayloadRelativePath,
                    paths.PayloadAbsolutePath,
                    committedDescriptor.Timing.ExposureStartedUtc,
                    FrameArtifactRole.Raw),
                manifestSha256);
            if (outcome == RawIngressOutcome.Committed || prior.Availability == RawIngressAvailability.Degraded)
            {
                try
                {
                    _faultInjector.Inject(RawIngressFaultPoint.BeforeIndexProjection);
                    if (outcome == RawIngressOutcome.Committed)
                    {
                        await RawIngressFileStore.AppendCompatibilityIndexAsync(
                            _options.RawIngressRoot,
                            receipt,
                            CancellationToken.None,
                            _telemetry.RecordFileFlush).ConfigureAwait(false);
                    }
                    else
                    {
                        await RawIngressFileStore.EnsureCompatibilityIndexAsync(
                            _options.RawIngressRoot,
                            manifestJson,
                            CancellationToken.None,
                            _telemetry.RecordFileFlush).ConfigureAwait(false);
                    }
                    _faultInjector.Inject(RawIngressFaultPoint.AfterIndexProjection);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _state.UpdateBaseStatus(RawIngressAvailability.Degraded, "index-projection-failed");
                    _logger.RawIngressIndexProjectionFailed();
                }
            }
            _faultInjector.Inject(RawIngressFaultPoint.BeforeWakeUpNotification);
            var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            _telemetry.RecordCommit(outcome, frame.PixelData.Length, duration);
            await RefreshLaneStateAsync(CancellationToken.None).ConfigureAwait(false);
            if (outcome == RawIngressOutcome.Committed)
            {
                _logger.RawIngressCommitted(frame.PixelData.Length, duration.TotalMilliseconds);
            }
            else
            {
                _logger.RawIngressExisting(duration.TotalMilliseconds);
            }
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            if (activity is not null)
            {
                _captureTraceStore?.Record(
                    identity.CaptureSequence,
                    identity.CaptureId,
                    identity.ArtifactId,
                    activity.Context);
            }
            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own cancellation (host shutdown, a revision change, a timeout) is not a storage failure:
            // availability is left alone and nothing is logged at Error. A cancellation that is not the caller's
            // falls through to the failure path below: every store call here either honours the caller's token or
            // runs on CancellationToken.None, so a stray cancellation is a store anomaly wherever it surfaces.
            _telemetry.RecordCancellation(lifecycleAcquired ? "accept" : "admission");
            if (lifecycleAcquired)
            {
                _logger.RawIngressCanceled("accept");
                if (payloadPublished)
                {
                    // The journal commit cannot be interrupted, but a payload published without its sidecar or its
                    // commit would make every retry of this capture a conflict until the reconciler recovers or
                    // quarantines it, so the next accept re-initializes first.
                    Volatile.Write(ref _initialized, false);
                }
            }
            throw;
        }
        catch (CaptureLaneBackpressureException)
        {
            throw;
        }
        catch (RawIngressConflictException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (_state.Snapshot.Availability != RawIngressAvailability.Unhealthy)
            {
                await SetFailureAvailabilityAsync("accept-failed").ConfigureAwait(false);
            }
            var reason = FailureReason(exception);
            _telemetry.RecordFailure("accept", reason);
            _logger.RawIngressRefused("accept", reason);
            if (exception is Microsoft.Data.Sqlite.SqliteException)
            {
                _logger.RawIngressSqliteResult("commit", "failure");
            }
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, reason);
            Volatile.Write(ref _initialized, false);
            throw;
        }
        finally
        {
            if (lifecycleAcquired)
            {
                lifecycleGate.Release();
            }
            _acceptGate.Release();
        }
    }

    async ValueTask<RawCapturePublicationState> IRawCaptureIngress.GetPublicationStateAsync(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Result.Frame is null) return RawCapturePublicationState.DefinitelyNotCommitted;
        if (!Volatile.Read(ref _journalValidated)) return RawCapturePublicationState.Unknown;
        try
        {
            var ids = RawCaptureDescriptorFactory.CreateStableIds(configuration, submission);
            if (await _journal.IsCaptureArtifactCommittedAsync(ids.CaptureId, ids.ArtifactId, cancellationToken)
                    .ConfigureAwait(false))
                return RawCapturePublicationState.Committed;
            var frame = submission.Result.Frame;
            var paths = _files.GetPaths(RawCaptureDescriptorFactory.ResolveExposureStartedUtc(submission, frame), ids.ArtifactId);
            return DurablePathExists(paths.PayloadAbsolutePath) || DurablePathExists(paths.SidecarAbsolutePath)
                ? RawCapturePublicationState.Unknown
                : RawCapturePublicationState.DefinitelyNotCommitted;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return RawCapturePublicationState.Unknown;
        }
    }

    async ValueTask IRawCaptureIngress.BindRecoveredLiveExecutionsAsync(
        CameraModuleConfig configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_graphOperations is null) return;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_options.RawIngressRoot);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in await _journal.ReadUnboundLiveCapturesAsync(cancellationToken).ConfigureAwait(false))
            {
                var parsed = CaptureContractJson.ParseManifest(entry.ManifestJson);
                if (!parsed.IsValid || parsed.Document?.Manifest.Descriptor is not { } descriptor)
                    throw new InvalidDataException("A recovered raw capture has an invalid committed manifest.");
                var existingEnvelope = await _journal.ReadRecoveredLaneEnvelopeAsync(entry, cancellationToken)
                    .ConfigureAwait(false);
                var prepared = await _graphOperations.PrepareLiveExecutionAsync(
                    (existingEnvelope?.Configuration ?? configuration) with { AgentId = descriptor.Capture.AgentId },
                    descriptor.Capture.CaptureId,
                    descriptor.Artifact.ArtifactId,
                    descriptor.Timing.DurableIngressUtc,
                    cancellationToken).ConfigureAwait(false);
                var envelope = existingEnvelope is null
                    ? CreateRecoveredEnvelope(prepared.Configuration, descriptor)
                    : existingEnvelope with { Configuration = prepared.Configuration };
                var context = CaptureLaneEnvelopeSerializer.Serialize(envelope.Configuration, envelope.Submission);
                await _journal.BindRecoveredLiveExecutionAsync(
                    entry, context.Json, context.Sha256, prepared.Seed, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private static CaptureLaneEnvelope CreateRecoveredEnvelope(
        CameraModuleConfig configuration,
        ReconstructionDescriptor descriptor)
    {
        var requested = new CaptureSetpoint(
            descriptor.Controls.RequestedExposure,
            descriptor.Controls.RequestedGain,
            null,
            null);
        var effective = new CaptureSetpoint(
            descriptor.Controls.EffectiveExposure,
            descriptor.Controls.EffectiveGain,
            null,
            null);
        var request = new CaptureRequest(
            descriptor.Timing.RequestedStartUtc,
            configuration.Rig.Pipeline.CaptureInterval,
            CaptureMode.Still,
            requested);
        var result = new CaptureResult(null, effective, TimeSpan.Zero, CaptureMode.Still, false)
        {
            AcquisitionTiming = new CaptureAcquisitionTiming(
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.ExposureEndedUtc,
                descriptor.Timing.ReadoutCompletedUtc)
            {
                SetpointAppliedUtc = descriptor.Timing.SetpointAppliedUtc
            }
        };
        return new CaptureLaneEnvelope(
            configuration with { AgentId = descriptor.Capture.AgentId },
            new CaptureLoopSubmission(
                request,
                result,
                descriptor.CycleEvidence?.ModuleCallStartedUtc ?? descriptor.Timing.RequestedStartUtc,
                configuration.Rig.Pipeline.CaptureInterval,
                TimeSpan.Zero)
            {
                CycleEvidence = descriptor.CycleEvidence
            });
    }

    private static bool DurablePathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public async ValueTask<IReadOnlyList<RawIngressRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        if (!PathsEqual(storageRoot, _options.RawIngressRoot))
        {
            return Array.Empty<RawIngressRetentionHold>();
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _journal.ReadRetentionHoldsAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyList<RawIngressRetentionHold>> GetRetentionHoldsUnderLifecycleLockAsync(
        string storageRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        if (!PathsEqual(storageRoot, _options.RawIngressRoot))
        {
            return Array.Empty<RawIngressRetentionHold>();
        }
        if (!Volatile.Read(ref _journalValidated))
        {
            throw new InvalidOperationException(
                "Raw ingress must be initialized before retention reads are made under the lifecycle lock.");
        }
        return await _journal.ReadRetentionHoldsAsync(cancellationToken).ConfigureAwait(false);
    }

    public void InvalidateEvidence()
    {
        SetAvailabilityPreservingTotals(RawIngressAvailability.Unhealthy, "committed-evidence-unavailable");
        _telemetry.RecordFailure("processing", "evidence-unavailable");
        _logger.RawIngressRefused("processing", "evidence-unavailable");
        Volatile.Write(ref _initialized, false);
    }

    public void ReportPressure(bool underPressure)
    {
        var baseline = _state.GetBaseSnapshot();
        if (baseline.Availability is RawIngressAvailability.Initializing or RawIngressAvailability.Unhealthy)
        {
            return;
        }
        var degraded = underPressure || baseline.QuarantineCount > 0 || baseline.Reason == "index-projection-failed";
        _state.UpdateBaseStatus(
            degraded ? RawIngressAvailability.Degraded : RawIngressAvailability.Accepting,
            underPressure ? "disk-pressure" : degraded ? baseline.Reason : "accepting");
    }

    public void ReportWakeup(bool queued) => _telemetry.RecordWakeup(queued);

    public async ValueTask InitializeLanesAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _laneStore.InitializeLanesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EnsureCanAcceptAsync(long payloadLength, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _laneStore.EnsureCanAcceptAsync(payloadLength, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RefreshLaneStateAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public ValueTask<CaptureLaneLease?> ClaimAsync(
        CaptureLaneDefinition lane,
        string owner,
        CameraModuleConfig fallbackConfiguration,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        return ClaimCoreAsync(lane, owner, fallbackConfiguration, started, cancellationToken);
    }

    public ValueTask<bool> RenewAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
        => _laneStore.RenewAsync(lease, cancellationToken);

    public async ValueTask CompleteAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await _laneStore.CompleteAsync(lease, cancellationToken).ConfigureAwait(false);
        _logger.CaptureLaneCompleted(lease.Lane);
        _laneTelemetry?.RecordAcknowledgement(
            lease,
            CaptureLaneHandlerOutcome.Completed,
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
        await RefreshHeldStateAsync(cancellationToken).ConfigureAwait(false);
        await RefreshLaneStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CaptureLaneHandlerOutcome> FailAsync(
        CaptureLaneLease lease,
        CaptureLaneHandlerResult result,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var actualOutcome = await _laneStore.FailAsync(lease, result, cancellationToken).ConfigureAwait(false);
        var reason = NormalizeLaneReason(result.Reason);
        if (actualOutcome is CaptureLaneHandlerOutcome.RetryableFailure or CaptureLaneHandlerOutcome.Deferred)
        {
            _logger.CaptureLaneRetryScheduled(
                lease.Lane,
                actualOutcome == CaptureLaneHandlerOutcome.Deferred ? lease.Attempt : lease.Attempt + 1,
                reason);
        }
        else
        {
            _logger.CaptureLaneTerminal(lease.Lane, "quarantined", reason);
        }
        _laneTelemetry?.RecordAcknowledgement(
            lease,
            actualOutcome,
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
        await RefreshHeldStateAsync(cancellationToken).ConfigureAwait(false);
        await RefreshLaneStateAsync(cancellationToken).ConfigureAwait(false);
        return actualOutcome;
    }

    public async ValueTask ReleaseAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
    {
        await _laneStore.ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
        await RefreshHeldStateAsync(cancellationToken).ConfigureAwait(false);
        await RefreshLaneStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<CaptureLaneBacklog>> ReadBacklogsAsync(CancellationToken cancellationToken)
        => _laneStore.ReadBacklogsAsync(cancellationToken);

    private static string FailureReason(Exception exception) => exception switch
    {
        RawIngressConflictException => "conflict",
        UnauthorizedAccessException => "permission",
        InvalidDataException => "integrity",
        InvalidOperationException => "invalid-state",
        IOException => "io",
        Microsoft.Data.Sqlite.SqliteException => "sqlite",
        OperationCanceledException => "canceled",
        _ => "unexpected"
    };

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private FileStream AcquireProcessLock()
    {
        var path = Path.Combine(Path.GetFullPath(_options.RawIngressRoot), "journal", "raw-ingress.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, path);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        try
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void EnsureCapacity(long payloadLength)
    {
        StorageCapacity capacity;
        try
        {
            capacity = _capacityProvider.GetCapacity(_options.RawIngressRoot);
        }
        catch
        {
            _capacityRevalidationRequired = true;
            SetAvailabilityPreservingTotals(RawIngressAvailability.Unhealthy, "capacity-probe-failed");
            throw;
        }
        var requiredBytes = checked(payloadLength + _options.RawIngressReserveBytes);
        if (capacity.AvailableBytes < requiredBytes)
        {
            _capacityRevalidationRequired = true;
            Interlocked.Exchange(ref _minimumRecoveryCapacityBytes, requiredBytes);
            SetAvailabilityPreservingTotals(RawIngressAvailability.Unhealthy, "capacity-exhausted");
            throw new IOException("Raw ingress does not have enough available capacity for the next capture.");
        }
        _capacityRevalidationRequired = false;
        Interlocked.Exchange(ref _minimumRecoveryCapacityBytes, 0);
    }

    private void RevalidateCapacityAfterFailure()
    {
        if (!_capacityRevalidationRequired)
        {
            return;
        }
        var capacity = _capacityProvider.GetCapacity(_options.RawIngressRoot);
        var requiredBytes = Interlocked.Read(ref _minimumRecoveryCapacityBytes);
        if (requiredBytes > 0 && capacity.AvailableBytes < requiredBytes)
        {
            throw new IOException("Raw ingress capacity has not recovered enough for the last refused capture.");
        }
        _capacityRevalidationRequired = false;
        Interlocked.Exchange(ref _minimumRecoveryCapacityBytes, 0);
    }

    private void SetAvailabilityPreservingTotals(RawIngressAvailability availability, string reason)
        => _state.UpdateBaseStatus(availability, reason);

    private async Task SetFailureAvailabilityAsync(string reason)
    {
        try
        {
            var held = await _journal.ReadHeldTotalsAsync(CancellationToken.None).ConfigureAwait(false);
            var health = await _journal.ReadHealthTotalsAsync(CancellationToken.None).ConfigureAwait(false);
            _state.Set(
                RawIngressAvailability.Unhealthy,
                reason,
                held.Count,
                held.Bytes,
                health.QuarantineCount,
                health.QuarantineBytes,
                held.Oldest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            SetAvailabilityPreservingTotals(RawIngressAvailability.Unhealthy, reason);
        }
    }

    private async Task RefreshHeldStateAsync(CancellationToken cancellationToken)
    {
        var held = await _journal.ReadHeldTotalsAsync(cancellationToken).ConfigureAwait(false);
        _state.UpdateBaseHeldData(held.Count, held.Bytes, held.Oldest);
    }

    private async ValueTask<CaptureLaneLease?> ClaimCoreAsync(
        CaptureLaneDefinition lane,
        string owner,
        CameraModuleConfig fallbackConfiguration,
        long started,
        CancellationToken cancellationToken)
    {
        using var activity = CaptureLaneTelemetry.ActivitySource.StartActivity("capture-lanes.claim");
        var lease = await _laneStore.ClaimAsync(
            lane, owner, fallbackConfiguration, cancellationToken).ConfigureAwait(false);
        _laneTelemetry?.RecordClaim(
            lane,
            lease is not null,
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
        activity?.SetTag("lane", lane.Name);
        activity?.SetTag("result", lease is null ? "empty" : "claimed");
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        if (lease is not null)
        {
            _logger.CaptureLaneClaimed(lane.Name, lease.Attempt);
            if (lease.Attempt > 1)
            {
                _logger.CaptureLaneRecovered(lane.Name, lease.Attempt);
            }
        }
        await RefreshLaneStateAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    private async Task RefreshLaneStateAsync(CancellationToken cancellationToken)
    {
        if (_laneState is null)
        {
            return;
        }
        try
        {
            var prior = _laneState.Snapshot;
            _laneState.Update(await _laneStore.ReadBacklogsAsync(cancellationToken).ConfigureAwait(false));
            var current = _laneState.Snapshot;
            if (current.Availability != prior.Availability || !string.Equals(current.Reason, prior.Reason, StringComparison.Ordinal))
            {
                if (current.Availability == CaptureLaneAvailability.Healthy)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.CaptureLanePressureRecovered(current.Availability.ToString());
                    }
                }
                else
                {
                    _logger.CaptureLanePressureChanged(current.Availability.ToString(), current.Reason);
                }
            }
        }
        catch
        {
            _laneState.SetUnhealthy("lane-state-unavailable");
            throw;
        }
    }

    public async ValueTask RefreshOperationsQueueSnapshotsAsync(CancellationToken cancellationToken)
    {
        await RefreshHeldStateAsync(cancellationToken).ConfigureAwait(false);
        await RefreshLaneStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeLaneReason(string reason)
        => !string.IsNullOrWhiteSpace(reason) && reason.Length <= 64 &&
           reason.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            ? reason
            : "handler-failure";

    public void Dispose()
    {
        _initializeGate.Dispose();
        _acceptGate.Dispose();
        _processLock?.Dispose();
    }
}
