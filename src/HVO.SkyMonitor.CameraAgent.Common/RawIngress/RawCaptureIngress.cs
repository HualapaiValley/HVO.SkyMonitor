using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class RawCaptureIngress :
    IRawCaptureIngress,
    IRawIngressRetentionHolds,
    IRawIngressRecoveryControl,
    IRawIngressPressureReporter,
    IRawIngressWakeupReporter,
    ICaptureLaneStore,
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
    private bool _initialized;
    private bool _capacityRevalidationRequired;
    private long _minimumRecoveryCapacityBytes;
    private FileStream? _processLock;

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
        CaptureLaneTelemetry? laneTelemetry = null)
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
        try
        {
            if (_initialized)
            {
                return;
            }
            var wasUnhealthy = _state.Snapshot.Availability == RawIngressAvailability.Unhealthy;
            try
            {
                _files.EnsureRootIsPhysical();
                _processLock ??= AcquireProcessLock();
                using var migrationActivity = RawIngressTelemetry.ActivitySource.StartActivity("raw-ingress.migrate");
                await _journal.InitializeAsync(_lanePolicy.Definitions, cancellationToken).ConfigureAwait(false);
                migrationActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                using var reconciliationActivity = RawIngressTelemetry.ActivitySource.StartActivity("raw-ingress.reconcile");
                var reconciliation = await new RawIngressReconciler(
                    _options.RawIngressRoot,
                    _journal,
                    _telemetry.RecordFileFlush,
                    _telemetry.RecordDirectorySync,
                    _lanePolicy.Definitions)
                     .RunAsync(cancellationToken).ConfigureAwait(false);
                await _laneStore.InitializeLanesAsync(cancellationToken).ConfigureAwait(false);
                await RefreshLaneStateAsync(cancellationToken).ConfigureAwait(false);
                reconciliationActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                _telemetry.RecordReconciliation(reconciliation);
                _logger.RawIngressSqliteResult("checkpoint", "success");
                var held = await _journal.ReadHeldTotalsAsync(cancellationToken).ConfigureAwait(false);
                var health = await _journal.ReadHealthTotalsAsync(cancellationToken).ConfigureAwait(false);
                RevalidateCapacityAfterFailure();
                var availability = health.FailureCount > 0
                    ? RawIngressAvailability.Unhealthy
                    : health.QuarantineCount > 0 || reconciliation.IndexProjectionFailures > 0
                        ? RawIngressAvailability.Degraded
                        : RawIngressAvailability.Accepting;
                _state.Set(
                    availability,
                    availability == RawIngressAvailability.Accepting
                        ? "accepting"
                        : reconciliation.IndexProjectionFailures > 0
                            ? "index-projection-failed"
                            : "reconciliation-findings",
                    held.Count,
                    held.Bytes,
                    health.QuarantineCount,
                    health.QuarantineBytes,
                    held.Oldest);
                if (availability == RawIngressAvailability.Unhealthy)
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
            catch (Exception exception)
            {
                await SetFailureAvailabilityAsync("initialization-failed").ConfigureAwait(false);
                _telemetry.RecordFailure("initialization", FailureReason(exception));
                _logger.RawIngressIntegrityFailed(FailureReason(exception));
                if (exception is Microsoft.Data.Sqlite.SqliteException)
                {
                    _logger.RawIngressSqliteResult("initialization", "failure");
                }
                throw;
            }
        }
        finally
        {
            _initializeGate.Release();
        }
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
            var identity = await _journal.ReserveIdentityAsync(
                configuration.AgentId,
                stableIds.CaptureId,
                stableIds.ArtifactId,
                cancellationToken).ConfigureAwait(false);
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
                    cancellationToken).ConfigureAwait(false);
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
                if (manifest.Descriptor.Timing.SetpointAppliedUtc is null)
                {
                    expectedDescriptor = expectedDescriptor with
                    {
                        Timing = expectedDescriptor.Timing with { SetpointAppliedUtc = null }
                    };
                }
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
                    await _files.PublishPayloadAsync(paths, frame.PixelData, cancellationToken).ConfigureAwait(false);
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
                    await _files.PublishSidecarAsync(paths, manifestJson, cancellationToken).ConfigureAwait(false);
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
                committedDescriptor.Timing.DurableIngressUtc);
            var committedTiming = submission.Result.AcquisitionTiming is null
                ? null
                : submission.Result.AcquisitionTiming with
                {
                    SetpointAppliedUtc = committedDescriptor.Timing.SetpointAppliedUtc
                };
            var context = CaptureLaneEnvelopeSerializer.Serialize(
                configuration,
                submission with
                {
                    CycleEvidence = committedDescriptor.CycleEvidence,
                    Result = submission.Result with { AcquisitionTiming = committedTiming }
                });
            RawIngressOutcome outcome;
            using (var commitActivity = RawIngressTelemetry.ActivitySource.StartActivity("sqlite.commit"))
            {
                _faultInjector.Inject(RawIngressFaultPoint.BeforeJournalCommit);
                outcome = await _journal.CommitAsync(
                    entry,
                    context.Json,
                    context.Sha256,
                    _lanePolicy.Definitions,
                    CancellationToken.None).ConfigureAwait(false);
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
            var prior = _state.Snapshot;
            var remainsDegraded = prior.Availability == RawIngressAvailability.Degraded || prior.QuarantineCount > 0;
            _state.Set(
                remainsDegraded ? RawIngressAvailability.Degraded : RawIngressAvailability.Accepting,
                remainsDegraded ? prior.Reason : "accepting",
                held.Count,
                held.Bytes,
                prior.QuarantineCount,
                prior.QuarantineBytes,
                held.Oldest);
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
                    _state.Set(
                        RawIngressAvailability.Degraded,
                        "index-projection-failed",
                        held.Count,
                        held.Bytes,
                        prior.QuarantineCount,
                        prior.QuarantineBytes,
                        held.Oldest);
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
            return receipt;
        }
        catch (OperationCanceledException) when (!lifecycleAcquired)
        {
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

    public void InvalidateEvidence()
    {
        SetAvailabilityPreservingTotals(RawIngressAvailability.Unhealthy, "committed-evidence-unavailable");
        _telemetry.RecordFailure("processing", "evidence-unavailable");
        _logger.RawIngressRefused("processing", "evidence-unavailable");
        Volatile.Write(ref _initialized, false);
    }

    public void ReportPressure(bool underPressure)
    {
        var snapshot = _state.Snapshot;
        if (snapshot.Availability is RawIngressAvailability.Initializing or RawIngressAvailability.Unhealthy)
        {
            return;
        }
        var degraded = underPressure || snapshot.QuarantineCount > 0 || snapshot.Reason == "index-projection-failed";
        _state.Set(
            degraded ? RawIngressAvailability.Degraded : RawIngressAvailability.Accepting,
            underPressure ? "disk-pressure" : degraded ? snapshot.Reason : "accepting",
            snapshot.PendingCount,
            snapshot.PendingBytes,
            snapshot.QuarantineCount,
            snapshot.QuarantineBytes,
            snapshot.OldestPendingUtc);
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
        if (actualOutcome == CaptureLaneHandlerOutcome.RetryableFailure)
        {
            _logger.CaptureLaneRetryScheduled(lease.Lane, lease.Attempt + 1, reason);
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
    {
        var snapshot = _state.Snapshot;
        _state.Set(
            availability,
            reason,
            snapshot.PendingCount,
            snapshot.PendingBytes,
            snapshot.QuarantineCount,
            snapshot.QuarantineBytes,
            snapshot.OldestPendingUtc);
    }

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
        var snapshot = _state.Snapshot;
        _state.Set(
            snapshot.Availability,
            snapshot.Reason,
            held.Count,
            held.Bytes,
            snapshot.QuarantineCount,
            snapshot.QuarantineBytes,
            held.Oldest);
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
                    _logger.CaptureLanePressureRecovered(current.Availability.ToString());
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
