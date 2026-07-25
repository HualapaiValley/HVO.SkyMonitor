using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Scheduling;

public sealed record CaptureScheduleRuntimeSnapshot(
    CaptureScheduleRevisionSnapshot Revision,
    string? PendingRevisionId,
    CameraModuleConfig Configuration,
    CaptureSchedulePreview Preview,
    CaptureLocationProvenance Location)
{
    public CaptureScheduleDecision? CurrentDecision { get; init; }
}

public sealed record CaptureScheduleGrant(
    CaptureScheduleRevisionSnapshot Revision,
    CaptureScheduleSetpointProfile Profile,
    string ProfileKey,
    CaptureScheduleDecision Decision,
    CaptureScheduleAdmissionEvidence Evidence);

public readonly record struct CaptureScheduleCaptureContext(
    CaptureScheduleRuntimeSnapshot Snapshot,
    CancellationToken RevisionChanged);

public sealed record CaptureScheduleOperatorState(
    long StateVersion,
    CaptureScheduleRevisionSnapshot ActiveRevision,
    CaptureScheduleRevisionSnapshot? PendingRevision,
    IReadOnlyList<CaptureScheduleRevisionSnapshot> History,
    CaptureScheduleDecision Decision,
    CaptureSchedulePreview Preview,
    IReadOnlyList<CaptureScheduleOverride> Overrides);

public sealed class CaptureScheduleRuntimeCoordinator(
    SqliteCaptureScheduleStore store,
    CaptureAdmissionCoordinator admissionCoordinator,
    RawIngressState rawIngressState,
    CaptureLaneState captureLaneState,
    ICaptureProcessingPipelineFactory pipelineFactory,
    TimeProvider timeProvider,
    ISolarEventCalculator? solarEventCalculator = null) : IDisposable
{
    private static readonly TimeSpan MaximumClosedPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumPoll = TimeSpan.FromMilliseconds(1);
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The dependency injection container owns the schedule store.")]
    private readonly SqliteCaptureScheduleStore _store = store;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The dependency injection container owns the admission coordinator.")]
    private readonly CaptureAdmissionCoordinator _admissionCoordinator = admissionCoordinator;
    private readonly RawIngressState _rawIngressState = rawIngressState;
    private readonly CaptureLaneState _captureLaneState = captureLaneState;
    private readonly ICaptureProcessingPipelineFactory _pipelineFactory = pipelineFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ISolarEventCalculator _solarEvents = solarEventCalculator ?? new AstronomyEngineSolarEventCalculator();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private CaptureScheduleRuntimeSnapshot? _snapshot;
    private CancellationTokenSource _revisionChanged = new();

    public CaptureScheduleRuntimeSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    public CancellationToken RevisionChanged => _revisionChanged.Token;

    public CaptureScheduleCaptureContext CaptureContext
    {
        get
        {
            lock (_stateGate)
            {
                return new CaptureScheduleCaptureContext(
                    _snapshot ?? throw new InvalidOperationException("Capture schedule runtime is not initialized."),
                    _revisionChanged.Token);
            }
        }
    }

    public async Task<CaptureScheduleRuntimeSnapshot> InitializeAsync(
        CameraModuleConfig configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (Snapshot is { } current)
        {
            return current;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot is { } initialized)
            {
                return initialized;
            }
            var durable = await _store.InitializeAsync(configuration, cancellationToken).ConfigureAwait(false);
            var activeConfiguration = durable.ActiveRevision.Profile.ApplyTo(configuration);
            ValidateConfiguration(activeConfiguration);
            var snapshot = await CreateSnapshotAsync(
                durable.ActiveRevision,
                durable.PendingRevision?.RevisionId,
                activeConfiguration,
                _timeProvider.GetUtcNow(),
                cancellationToken)
                .ConfigureAwait(false);
            lock (_stateGate)
            {
                Volatile.Write(ref _snapshot, snapshot);
            }
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CaptureScheduleGrant> WaitForGrantAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = Snapshot ?? throw new InvalidOperationException("Capture schedule runtime is not initialized.");
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            snapshot = await EnsurePreviewAsync(snapshot, now, cancellationToken).ConfigureAwait(false);
            var overrides = await _store.GetActiveOverridesAsync(
                snapshot.Revision.RevisionId, now, cancellationToken).ConfigureAwait(false);
            var decision = CaptureScheduleEvaluator.Evaluate(
                snapshot.Revision.Definition,
                snapshot.Preview,
                new CaptureScheduleEvaluationRequest(
                    now,
                    ResolveSafetyState(_rawIngressState.Snapshot, _captureLaneState.Snapshot),
                    _admissionCoordinator.Snapshot.State != CaptureAdmissionState.Running,
                    overrides));
            if (!await RecordDecisionAsync(snapshot, decision, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            if (decision.Admitted)
            {
                return CreateGrant(snapshot, decision);
            }
            var delay = decision.NextTransitionUtc is { } transition
                ? transition - now
                : MaximumClosedPoll;
            delay = Min(Max(delay, MinimumPoll), MaximumClosedPoll);
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<CaptureScheduleGrant?> ConfirmGrantAsync(
        CaptureScheduleGrant grant,
        string admissionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var snapshot = Snapshot;
        if (snapshot is null || !string.Equals(
                snapshot.Revision.RevisionId, grant.Revision.RevisionId, StringComparison.Ordinal))
        {
            return null;
        }
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        snapshot = await EnsurePreviewAsync(snapshot, now, cancellationToken).ConfigureAwait(false);
        var overrides = await _store.GetActiveOverridesAsync(
            snapshot.Revision.RevisionId, now, cancellationToken).ConfigureAwait(false);
        var decision = CaptureScheduleEvaluator.Evaluate(
            snapshot.Revision.Definition,
            snapshot.Preview,
            new CaptureScheduleEvaluationRequest(
                now,
                ResolveSafetyState(_rawIngressState.Snapshot, _captureLaneState.Snapshot),
                _admissionCoordinator.Snapshot.State != CaptureAdmissionState.Running,
                overrides));
        if (!await RecordDecisionAsync(snapshot, decision, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (!decision.Admitted ||
            !string.Equals(decision.SetpointProfileId, grant.Profile.Id, StringComparison.Ordinal) ||
            !string.Equals(decision.Interval?.Id, grant.Decision.Interval?.Id, StringComparison.Ordinal) ||
            !string.Equals(decision.OverrideId, grant.Decision.OverrideId, StringComparison.Ordinal))
        {
            return null;
        }
        var consumed = await _store.GrantAdmissionAsync(
            admissionId,
            snapshot.Revision.RevisionId,
            decision.ConsumeOneShotOverride ? decision.OverrideId : null,
            decision.DecisionUtc,
            cancellationToken).ConfigureAwait(false);
        return consumed ? CreateGrant(snapshot, decision) : null;
    }

    public async Task<CaptureScheduleStoreSnapshot> StageAsync(
        LocalCaptureProfileDefinition profile,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot ?? throw new InvalidOperationException("Capture schedule runtime is not initialized.");
            var candidateConfiguration = profile.ApplyTo(current.Configuration);
            ValidateConfiguration(candidateConfiguration);
            var durable = await _store.StageAsync(
                profile, idempotencyKey, expectedVersion, actor, reason, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                Volatile.Write(ref _snapshot, current with
                {
                    PendingRevisionId = durable.PendingRevision?.RevisionId
                });
            }
            return durable;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CaptureScheduleOperatorState> GetOperatorStateAsync(
        CancellationToken cancellationToken)
    {
        var current = Snapshot ?? throw new InvalidOperationException("Capture schedule runtime is not initialized.");
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        current = await EnsurePreviewAsync(current, now, cancellationToken).ConfigureAwait(false);
        var overrides = await _store.GetActiveOverridesAsync(
            current.Revision.RevisionId, now, cancellationToken).ConfigureAwait(false);
        var decision = CaptureScheduleEvaluator.Evaluate(
            current.Revision.Definition,
            current.Preview,
            new CaptureScheduleEvaluationRequest(
                now,
                ResolveSafetyState(_rawIngressState.Snapshot, _captureLaneState.Snapshot),
                _admissionCoordinator.Snapshot.State != CaptureAdmissionState.Running,
                overrides));
        _ = await RecordDecisionAsync(current, decision, cancellationToken).ConfigureAwait(false);
        var durable = await _store.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var history = await _store.GetHistoryAsync(20, cancellationToken).ConfigureAwait(false);
        return new CaptureScheduleOperatorState(
            durable.Version,
            durable.ActiveRevision,
            durable.PendingRevision,
            history,
            decision,
            current.Preview,
            overrides);
    }

    public CaptureSchedulePreview Preview(LocalCaptureProfileDefinition profile, int dayCount)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (dayCount is < 1 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(dayCount));
        }
        var current = Snapshot ?? throw new InvalidOperationException("Capture schedule runtime is not initialized.");
        var candidate = profile.ApplyTo(current.Configuration);
        ValidateConfiguration(candidate);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var observer = candidate.ResolveObservatory(now);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(observer.TimeZoneId);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        return CaptureScheduleIntervalExpander.Expand(
            profile.Schedule, localDate, dayCount, timeZone, observer, _solarEvents);
    }

    public async Task<CaptureScheduleStoreSnapshot> ActivateAsync(
        string revisionId,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot ?? throw new InvalidOperationException("Capture schedule runtime is not initialized.");
            var target = await _store.GetRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false);
            var targetConfiguration = target.Profile.ApplyTo(current.Configuration);
            ValidateConfiguration(targetConfiguration);
            var prepared = await CreateSnapshotAsync(
                target,
                current.PendingRevisionId,
                targetConfiguration,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return await _admissionCoordinator.ExecuteCaptureBoundaryAsync(
                async boundaryToken =>
                {
                    var receipt = await _store.ActivateAsync(
                        revisionId, idempotencyKey, expectedVersion, actor, reason, boundaryToken).ConfigureAwait(false);
                    var actual = await _store.GetSnapshotAsync(boundaryToken).ConfigureAwait(false);
                    if (string.Equals(actual.ActiveRevision.RevisionId, target.RevisionId, StringComparison.Ordinal) &&
                        !string.Equals(current.Revision.RevisionId, target.RevisionId, StringComparison.Ordinal))
                    {
                        CancellationTokenSource priorSignal;
                        lock (_stateGate)
                        {
                            Volatile.Write(ref _snapshot, prepared with
                            {
                                PendingRevisionId = actual.PendingRevision?.RevisionId
                            });
                            priorSignal = Interlocked.Exchange(
                                ref _revisionChanged, new CancellationTokenSource());
                        }
                        await priorSignal.CancelAsync().ConfigureAwait(false);
                        priorSignal.Dispose();
                    }
                    return receipt;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static CaptureScheduleSafetyState ResolveSafetyState(
        RawIngressSnapshot ingress,
        CaptureLaneSnapshot lanes)
    {
        if (ingress.Availability == RawIngressAvailability.Unhealthy)
        {
            return ingress.Reason is "capacity-probe-failed" or "capacity-exhausted"
                ? CaptureScheduleSafetyState.StorageUnavailable
                : CaptureScheduleSafetyState.IngressUnavailable;
        }
        if (lanes.Availability == CaptureLaneAvailability.Unhealthy)
        {
            return CaptureScheduleSafetyState.RequiredLaneUnavailable;
        }
        if (ingress.Availability == RawIngressAvailability.Initializing ||
            lanes.Availability == CaptureLaneAvailability.Initializing)
        {
            return CaptureScheduleSafetyState.Unknown;
        }
        return CaptureScheduleSafetyState.Available;
    }

    public void Dispose()
    {
        _revisionChanged.Dispose();
        _gate.Dispose();
    }

    private async Task<CaptureScheduleRuntimeSnapshot> EnsurePreviewAsync(
        CaptureScheduleRuntimeSnapshot snapshot,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        if (utc >= snapshot.Preview.PreviewStartUtc && utc < snapshot.Preview.PreviewEndUtc)
        {
            return snapshot;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot ?? throw new InvalidOperationException("Capture schedule runtime is not initialized.");
            if (!string.Equals(current.Revision.RevisionId, snapshot.Revision.RevisionId, StringComparison.Ordinal))
            {
                return current;
            }
            if (utc >= current.Preview.PreviewStartUtc && utc < current.Preview.PreviewEndUtc)
            {
                return current;
            }
            var refreshed = await CreateSnapshotAsync(
                current.Revision,
                current.PendingRevisionId,
                current.Configuration,
                utc,
                cancellationToken).ConfigureAwait(false);
            refreshed = refreshed with { CurrentDecision = current.CurrentDecision };
            lock (_stateGate)
            {
                Volatile.Write(ref _snapshot, refreshed);
            }
            return refreshed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CaptureScheduleRuntimeSnapshot> CreateSnapshotAsync(
        CaptureScheduleRevisionSnapshot revision,
        string? pendingRevisionId,
        CameraModuleConfig configuration,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        var location = configuration.DeploymentLocation?.ToProvenance()
            ?? throw new InvalidOperationException("A validated deployment location is required for capture scheduling.");
        var persisted = await _store.TryReadPreviewAsync(
            revision, location, utc, cancellationToken).ConfigureAwait(false);
        if (persisted is not null)
        {
            return new CaptureScheduleRuntimeSnapshot(
                revision, pendingRevisionId, configuration, persisted, location);
        }
        var observer = configuration.ResolveObservatory(utc);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(observer.TimeZoneId);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, timeZone).DateTime);
        var preview = CaptureScheduleIntervalExpander.Expand(
            revision.Definition, localDate, 1, timeZone, observer, _solarEvents);
        await _store.PersistPreviewAsync(revision, location, preview, cancellationToken).ConfigureAwait(false);
        return new CaptureScheduleRuntimeSnapshot(
            revision, pendingRevisionId, configuration, preview, location);
    }

    private void ValidateConfiguration(CameraModuleConfig configuration)
    {
        FileCameraAgentConfigurationLoader.ValidateConfig(configuration);
        var graph = _pipelineFactory.CreateGraph(configuration);
        graph.DisposeSteps();
    }

    private async Task<bool> RecordDecisionAsync(
        CaptureScheduleRuntimeSnapshot snapshot,
        CaptureScheduleDecision decision,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.RecordDecisionAsync(
                snapshot.Revision.RevisionId, decision, cancellationToken).ConfigureAwait(false);
        }
        catch (CaptureScheduleStoreConflictException)
        {
            return false;
        }
        lock (_stateGate)
        {
            if (_snapshot is not { } current || !string.Equals(
                    current.Revision.RevisionId, snapshot.Revision.RevisionId, StringComparison.Ordinal))
            {
                return false;
            }
            Volatile.Write(ref _snapshot, current with { CurrentDecision = decision });
        }
        return true;
    }

    private static CaptureScheduleGrant CreateGrant(
        CaptureScheduleRuntimeSnapshot snapshot,
        CaptureScheduleDecision decision)
    {
        var interval = decision.Interval ?? throw new InvalidOperationException("An admitted schedule decision needs an interval.");
        var profile = snapshot.Revision.Definition.SetpointProfiles.Single(item =>
            string.Equals(item.Id, decision.SetpointProfileId, StringComparison.Ordinal));
        var evidence = new CaptureScheduleAdmissionEvidence(
            CaptureScheduleAdmissionEvidence.CurrentSchemaVersion,
            snapshot.Revision.RevisionId,
            snapshot.Revision.ScheduleSha256,
            profile.Id,
            decision.Reason,
            interval.Source,
            decision.DecisionUtc,
            interval.StartUtc,
            interval.EndUtc,
            interval.Id,
            snapshot.Preview.ExpansionAlgorithmVersion,
            snapshot.Preview.ExpansionSha256,
            snapshot.Preview.TimeZoneRuleSha256,
            snapshot.Location.LocationId,
            snapshot.Location.Version,
            interval.SolarAlgorithmVersion ??
                (snapshot.Preview.SolarAlgorithmVersion == "none" ? null : snapshot.Preview.SolarAlgorithmVersion),
            decision.OverrideId);
        return new CaptureScheduleGrant(
            snapshot.Revision,
            profile,
            string.Concat(snapshot.Revision.RevisionId, ":", profile.Id),
            decision,
            evidence);
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
