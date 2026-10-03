using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>
/// Owns at most one manual focus session. A running session takes back-to-back previews through
/// <see cref="IManualFocusPreviewSource"/> until its owner stops it, the labelled safety timeout elapses, the owner stops
/// observing it, the camera is withdrawn, or the host stops. Every exposure is a sample, including explicit invalid
/// measurements; the history keeps the newest <see cref="ManualFocusSessionLimits.HistoryCapacity"/> samples and the
/// best valid one, and its cap never stops the loop. Settings, focus position, and target changes apply from the next
/// exposure. Session state is in memory only until it is saved; after a restart no session resumes.
/// </summary>
public sealed partial class ManualFocusSessionCoordinator : IHostedService, IDisposable
{
    private readonly IManualFocusPreviewSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly ManualFocusSessionLimits _limits;
    private readonly IManualFocusSessionStore? _store;
    private readonly ILogger<ManualFocusSessionCoordinator>? _logger;
    private readonly object _sync = new();
    private Session? _session;
    private bool _disposed;

    public ManualFocusSessionCoordinator(
        IManualFocusPreviewSource source,
        TimeProvider timeProvider,
        ManualFocusSessionLimits? limits = null,
        IManualFocusSessionStore? store = null,
        ILogger<ManualFocusSessionCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _source = source;
        _timeProvider = timeProvider;
        _limits = limits ?? ManualFocusSessionLimits.Default;
        _store = store;
        _logger = logger;
    }

    public ManualFocusSessionLimits Limits => _limits;

    public ManualFocusSessionAvailability Availability => _source.GetAvailability();

    public bool RetentionAvailable => _store is not null;

    /// <summary>Current session view for any authorized reader. Reading does not count as owner observation.</summary>
    public ManualFocusSessionSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _session?.ToSnapshot() ?? ManualFocusSessionSnapshot.Idle;
            }
        }
    }

    /// <summary>Latest display images for any authorized reader, or null before the first exposure.</summary>
    public ManualFocusPreviewImages? LatestImages
    {
        get
        {
            lock (_sync)
            {
                return _session?.Images;
            }
        }
    }

    /// <summary>
    /// Records that the owner is still watching. A running session whose owner stops observing for
    /// <see cref="ManualFocusSessionLimits.ObserverTimeout"/> is stopped, which covers closed browsers, lost circuits, and
    /// revoked authorization (a revoked owner can no longer observe).
    /// </summary>
    public ManualFocusSessionSnapshot Observe(string sessionId, string actor)
    {
        ValidateActor(actor);
        ValidateSessionId(sessionId);
        lock (_sync)
        {
            var session = RequireOwned(sessionId, actor);
            session.LastObservedUtc = _timeProvider.GetUtcNow();
            return session.ToSnapshot();
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The safety-timeout source is owned by the session, which is disposed when it is replaced or the coordinator is disposed.")]
    public Task<ManualFocusSessionSnapshot> StartAsync(
        ManualFocusSessionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ValidateActor(actor);
        ValidateSettings(request.Settings);
        ValidateTarget(request.Target);
        var safetyTimeout = request.SafetyTimeout ?? _limits.DefaultSafetyTimeout;
        if (safetyTimeout < _limits.MinimumSafetyTimeout || safetyTimeout > _limits.MaximumSafetyTimeout)
        {
            throw new ManualFocusSessionValidationException("The safety timeout is outside the allowed range.");
        }
        var availability = _source.GetAvailability();
        if (!availability.Available)
        {
            throw new ManualFocusSessionUnavailableException(availability.Reason);
        }
        var position = ResolvePosition(availability, request.SimulatedFocusPosition);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { State: ManualFocusSessionState.Running })
            {
                throw new ManualFocusSessionConflictException(
                    "A manual focus session is already running; only its owner can stop it.");
            }
            var now = _timeProvider.GetUtcNow();
            var session = new Session(Guid.NewGuid().ToString("N"), actor, now, now + safetyTimeout,
                _limits.HistoryCapacity, new CancellationTokenSource(safetyTimeout, _timeProvider))
            {
                Settings = request.Settings,
                Position = position,
                Hint = request.Target,
                TargetSource = request.Target is null ? ManualFocusTargetSource.Automatic : ManualFocusTargetSource.Operator,
                ModuleType = availability.ModuleType,
                Fidelity = availability.Fidelity,
                SimulatedFocus = availability.SimulatedFocus,
                LastObservedUtc = now
            };
            _session?.Dispose();
            _session = session;
            session.Loop = Task.Run(() => RunAsync(session), CancellationToken.None);
            if (_logger is not null)
            {
                LogSessionStarted(_logger, session.Id, actor, request.Settings.Exposure, request.Settings.Gain, position);
            }
            return Task.FromResult(session.ToSnapshot());
        }
    }

    /// <summary>Applies new settings, focus position, or target from the next exposure. Owner only.</summary>
    public ManualFocusSessionSnapshot Adjust(string sessionId, string actor, ManualFocusAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        ValidateActor(actor);
        ValidateSessionId(sessionId);
        if (adjustment.Settings is { } settings)
        {
            ValidateSettings(settings);
        }
        ValidateTarget(adjustment.Target);
        if (adjustment.Target is not null && adjustment.ResetToAutomaticTarget)
        {
            throw new ManualFocusSessionValidationException("Choose either a target or automatic selection, not both.");
        }
        lock (_sync)
        {
            var session = RequireOwned(sessionId, actor);
            if (session.State != ManualFocusSessionState.Running)
            {
                throw new ManualFocusSessionStateException("The manual focus session is not running.");
            }
            if (adjustment.SimulatedFocusPosition is { } position)
            {
                if (session.SimulatedFocus is not { } model)
                {
                    throw new ManualFocusSessionValidationException(
                        "This camera declares no simulated focus control; focus by hand at the lens.");
                }
                if (!model.Contains(position))
                {
                    throw new ManualFocusSessionValidationException("The simulated focus position is outside the declared range.");
                }
                session.Position = position;
            }
            if (adjustment.Settings is { } changed)
            {
                session.Settings = changed;
            }
            if (adjustment.Target is { } target)
            {
                session.Hint = target;
                session.Target = null;
                session.TargetSource = ManualFocusTargetSource.Operator;
            }
            else if (adjustment.ResetToAutomaticTarget)
            {
                session.Hint = null;
                session.Target = null;
                session.TargetSource = ManualFocusTargetSource.Automatic;
            }
            session.LastObservedUtc = _timeProvider.GetUtcNow();
            return session.ToSnapshot();
        }
    }

    /// <summary>Stops the loop immediately and waits (bounded) for the in-flight preview to release the camera.</summary>
    public async Task<ManualFocusSessionSnapshot> StopAsync(string sessionId, string actor, CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        ValidateSessionId(sessionId);
        Session session;
        lock (_sync)
        {
            session = RequireOwned(sessionId, actor);
            if (session.State != ManualFocusSessionState.Running)
            {
                return session.ToSnapshot();
            }
            session.StopReason ??= ManualFocusReasonCodes.StoppedByOperator;
        }
        await StopSessionAsync(session, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            return session.ToSnapshot();
        }
    }

    /// <summary>Writes an ended session's bounded history as an immutable, checksummed record. Owner only.</summary>
    public async Task<ManualFocusSessionSnapshot> SaveAsync(string sessionId, string actor, CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        ValidateSessionId(sessionId);
        var store = _store ?? throw new ManualFocusSessionUnavailableException(
            "Focus session retention is not configured on this CameraAgent.");
        Session session;
        ManualFocusSessionRecord record;
        lock (_sync)
        {
            session = RequireOwned(sessionId, actor);
            if (session.State == ManualFocusSessionState.Running)
            {
                throw new ManualFocusSessionStateException("Stop the session before saving it.");
            }
            if (session.Retention != ManualFocusRetentionState.InMemoryOnly || session.Saving)
            {
                throw new ManualFocusSessionStateException("This session was already saved or discarded.");
            }
            if (session.TotalSamples == 0)
            {
                throw new ManualFocusSessionStateException("The session has no samples to save.");
            }
            session.Saving = true;
            record = ManualFocusSessionRecord.Create(session.ToSnapshot(), actor, _timeProvider.GetUtcNow());
        }
        try
        {
            var summary = await store.SaveAsync(record, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                session.Retention = ManualFocusRetentionState.Saved;
                session.SavedRecordId = summary.RecordId;
                if (_logger is not null)
                {
                    LogSessionRetention(_logger, session.Id, actor, session.Retention);
                }
                return session.ToSnapshot();
            }
        }
        finally
        {
            lock (_sync)
            {
                session.Saving = false;
            }
        }
    }

    /// <summary>Drops an ended session's in-memory history. Nothing is retained. Owner only.</summary>
    public ManualFocusSessionSnapshot Discard(string sessionId, string actor)
    {
        ValidateActor(actor);
        ValidateSessionId(sessionId);
        lock (_sync)
        {
            var session = RequireOwned(sessionId, actor);
            if (session.State == ManualFocusSessionState.Running)
            {
                throw new ManualFocusSessionStateException("Stop the session before discarding it.");
            }
            if (session.Saving)
            {
                throw new ManualFocusSessionStateException("The session is being saved.");
            }
            if (session.Retention == ManualFocusRetentionState.InMemoryOnly)
            {
                session.Discard();
                if (_logger is not null)
                {
                    LogSessionRetention(_logger, session.Id, actor, session.Retention);
                }
            }
            return session.ToSnapshot();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Host shutdown stops a running session so its preview releases the camera before the module closes.</summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        if (MarkRunningSessionStopping(ManualFocusReasonCodes.HostStopping) is { } running)
        {
            await StopSessionAsync(running, cancellationToken).ConfigureAwait(false);
        }
    }

    private Session? MarkRunningSessionStopping(string reason)
    {
        lock (_sync)
        {
            if (_session is not { State: ManualFocusSessionState.Running } session)
            {
                return null;
            }
            session.StopReason ??= reason;
            return session;
        }
    }

    private async Task StopSessionAsync(Session session, CancellationToken cancellationToken)
    {
        await session.Cancellation.CancelAsync().ConfigureAwait(false);
        if (session.Loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(_limits.StopWaitTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The loop keeps ending in the background; the snapshot stays Running until the camera is released.
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Any acquisition or measurement failure is recorded on the session and bounded by the consecutive-failure limit.")]
    private async Task RunAsync(Session session)
    {
        var token = session.Cancellation.Token;
        var failures = 0;
        var endState = ManualFocusSessionState.Stopped;
        string? endReason = null;
        long? previousStart = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (previousStart is { } started &&
                    _limits.MinimumSamplePeriod - _timeProvider.GetElapsedTime(started) is { Ticks: > 0 } remaining)
                {
                    await Task.Delay(remaining, _timeProvider, token).ConfigureAwait(false);
                }
                previousStart = _timeProvider.GetTimestamp();
                ManualFocusPreviewSettings settings;
                double? position;
                PixelPoint? target, hint;
                ManualFocusTargetSource targetSource;
                lock (_sync)
                {
                    if (_timeProvider.GetUtcNow() - session.LastObservedUtc > _limits.ObserverTimeout)
                    {
                        endState = ManualFocusSessionState.TimedOut;
                        endReason = ManualFocusReasonCodes.ObserverLost;
                        break;
                    }
                    settings = session.Settings;
                    position = session.Position;
                    target = session.Target;
                    hint = session.Hint;
                    targetSource = session.TargetSource;
                }

                ManualFocusPreview preview;
                string? failure = null;
                using (var deadline = new CancellationTokenSource(settings.Exposure + _limits.SampleDeadlineGrace, _timeProvider))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
                {
                    try
                    {
                        preview = await _source.AcquireAsync(settings, position, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        preview = null!;
                        failure = ManualFocusReasonCodes.SampleDeadlineExceeded;
                    }
                    catch (ManualFocusSessionUnavailableException exception)
                    {
                        endState = ManualFocusSessionState.Faulted;
                        endReason = exception.ReasonCode;
                        lock (_sync)
                        {
                            session.LastFailure = exception.Message;
                        }
                        break;
                    }
                    catch (Exception exception)
                    {
                        preview = null!;
                        failure = $"{exception.GetType().Name}: {exception.Message}";
                    }
                }
                if (failure is not null)
                {
                    failures++;
                    lock (_sync)
                    {
                        session.LastFailure = failure;
                    }
                    if (_logger is not null)
                    {
                        LogSampleFailed(_logger, session.Id, failure, failures);
                    }
                    if (failures >= _limits.MaximumConsecutiveFailures)
                    {
                        endState = ManualFocusSessionState.Faulted;
                        endReason = ManualFocusReasonCodes.RepeatedFailures;
                        break;
                    }
                    continue;
                }
                failures = 0;

                long sequence;
                lock (_sync)
                {
                    sequence = session.TotalSamples + 1;
                }
                ManualFocusPreviewOutcome outcome;
                try
                {
                    outcome = ManualFocusPreviewMeasurement.Measure(preview.Frame, sequence, target, hint, token,
                        preview.ImageCircle);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException)
                {
                    endState = ManualFocusSessionState.Faulted;
                    endReason = ManualFocusReasonCodes.PreviewUnmeasurable;
                    lock (_sync)
                    {
                        session.LastFailure =
                            $"The preview frame could not be measured ({exception.GetType().Name}: {exception.Message}).";
                    }
                    break;
                }
                var sample = CreateSample(sequence, settings, position, targetSource, preview, outcome);
                lock (_sync)
                {
                    session.Record(sample, outcome, target, hint);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_sync)
            {
                endReason = session.StopReason ?? ManualFocusReasonCodes.SafetyTimeout;
                endState = session.StopReason is null ? ManualFocusSessionState.TimedOut : ManualFocusSessionState.Stopped;
            }
        }
        catch (Exception exception)
        {
            endState = ManualFocusSessionState.Faulted;
            endReason = ManualFocusReasonCodes.LoopFailed;
            lock (_sync)
            {
                session.LastFailure = $"The focus loop failed unexpectedly ({exception.GetType().Name}).";
            }
            if (_logger is not null)
            {
                LogLoopFailed(_logger, exception, session.Id);
            }
        }
        lock (_sync)
        {
            session.End(endState, endReason ?? ManualFocusReasonCodes.StoppedByOperator, _timeProvider.GetUtcNow());
            if (_logger is not null)
            {
                LogSessionEnded(_logger, session.Id, session.State, session.EndReason!, session.TotalSamples);
            }
        }
    }

    private ManualFocusSample CreateSample(
        long sequence,
        ManualFocusPreviewSettings settings,
        double? position,
        ManualFocusTargetSource targetSource,
        ManualFocusPreview preview,
        ManualFocusPreviewOutcome outcome)
    {
        var frame = preview.Frame;
        var extra = frame.Metadata.Extra;
        var window = outcome.Window;
        var provenance = new ManualFocusSampleProvenance(
            frame.TimestampUtc,
            frame.Width,
            frame.Height,
            frame.PixelFormat,
            outcome.FrameSha256,
            extra is not null && extra.TryGetValue("sceneId", out var sceneId) ? sceneId : frame.Metadata.Scene?.SceneId,
            preview.ModuleType,
            preview.ModuleGeneration,
            preview.Fidelity.Kind,
            preview.Fidelity.QualifiesPhysicalFocus,
            preview.SimulatedFocus?.ModelId,
            preview.SimulatedFocus?.ParametersSha256,
            window?.OriginX ?? 0,
            window?.OriginY ?? 0,
            window?.Width ?? 0,
            window?.Height ?? 0,
            ManualFocusFrameSampler.AlgorithmVersion,
            FocusStarMeasurement.MetricDefinition,
            FocusStarMeasurement.Units,
            outcome.Measurement.AlgorithmVersion,
            outcome.Measurement.SettingsIdentitySha256,
            ManualFocusPreviewMeasurement.PreviewSettingsIdentity(settings, position),
            preview.RequestedUtc,
            preview.CompletedUtc,
            ManualFocusSceneProvenance.FromMetadata(frame.Metadata));
        return new ManualFocusSample(sequence, _timeProvider.GetUtcNow(), settings, position, targetSource,
            outcome.Measurement, provenance);
    }

    private Session RequireOwned(string sessionId, string actor)
    {
        if (_session is not { } session || !string.Equals(session.Id, sessionId, StringComparison.Ordinal))
        {
            throw new ManualFocusSessionStateException("No matching manual focus session exists.");
        }
        if (!string.Equals(session.OwnerId, actor, StringComparison.Ordinal))
        {
            throw new ManualFocusSessionConflictException("Another operator owns this manual focus session.");
        }
        return session;
    }

    private static double? ResolvePosition(ManualFocusSessionAvailability availability, double? requested)
    {
        if (availability.SimulatedFocus is not { } model)
        {
            return requested is null
                ? null
                : throw new ManualFocusSessionValidationException(
                    "This camera declares no simulated focus control; focus by hand at the lens.");
        }
        var position = requested ?? model.DefaultPosition;
        return model.Contains(position)
            ? position
            : throw new ManualFocusSessionValidationException("The simulated focus position is outside the declared range.");
    }

    private void ValidateSettings(ManualFocusPreviewSettings? settings)
    {
        if (settings is null)
        {
            throw new ManualFocusSessionValidationException("Preview settings are required.");
        }
        if (settings.Exposure < _limits.MinimumExposure || settings.Exposure > _limits.MaximumExposure)
        {
            throw new ManualFocusSessionValidationException("The preview exposure is outside the allowed range.");
        }
        if (!double.IsFinite(settings.Gain) || settings.Gain < 0 || settings.Gain > _limits.MaximumGain)
        {
            throw new ManualFocusSessionValidationException("The preview gain is outside the allowed range.");
        }
    }

    private static void ValidateTarget(PixelPoint? target)
    {
        if (target is { } point && (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0))
        {
            throw new ManualFocusSessionValidationException("The target must be a finite source-frame pixel.");
        }
    }

    private static void ValidateActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
        {
            throw new ManualFocusSessionValidationException("An actor is required and must not exceed 128 characters.");
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 64)
        {
            throw new ManualFocusSessionValidationException("A valid session identifier is required.");
        }
    }

    [LoggerMessage(EventId = 4700, Level = LogLevel.Information,
        Message = "Manual focus session {SessionId} started by {Actor} with exposure {Exposure}, gain {Gain}, simulated focus position {Position}.")]
    private static partial void LogSessionStarted(
        ILogger logger, string sessionId, string actor, TimeSpan exposure, double gain, double? position);

    [LoggerMessage(EventId = 4701, Level = LogLevel.Information,
        Message = "Manual focus session {SessionId} ended as {State} ({Reason}) after {SampleCount} samples.")]
    private static partial void LogSessionEnded(
        ILogger logger, string sessionId, ManualFocusSessionState state, string reason, long sampleCount);

    [LoggerMessage(EventId = 4702, Level = LogLevel.Warning,
        Message = "Manual focus session {SessionId} preview failed ({Failure}); {ConsecutiveFailures} consecutive.")]
    private static partial void LogSampleFailed(ILogger logger, string sessionId, string failure, int consecutiveFailures);

    [LoggerMessage(EventId = 4703, Level = LogLevel.Information,
        Message = "Manual focus session {SessionId} history marked {Retention} by {Actor}.")]
    private static partial void LogSessionRetention(
        ILogger logger, string sessionId, string actor, ManualFocusRetentionState retention);

    [LoggerMessage(EventId = 4704, Level = LogLevel.Error,
        Message = "Manual focus session {SessionId} loop failed unexpectedly.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception, string sessionId);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_session is not null)
            {
                _session.StopReason ??= ManualFocusReasonCodes.HostStopping;
            }
            _session?.Dispose();
        }
    }

    private sealed class Session(
        string id,
        string ownerId,
        DateTimeOffset startedUtc,
        DateTimeOffset safetyDeadlineUtc,
        int historyCapacity,
        CancellationTokenSource cancellation) : IDisposable
    {
        private readonly Queue<ManualFocusSample> _history = new(historyCapacity);

        public string Id { get; } = id;
        public string OwnerId { get; } = ownerId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Loop { get; set; }
        public ManualFocusSessionState State { get; private set; } = ManualFocusSessionState.Running;
        public required ManualFocusPreviewSettings Settings { get; set; }
        public double? Position { get; set; }
        public PixelPoint? Target { get; set; }
        public PixelPoint? Hint { get; set; }
        public ManualFocusTargetSource TargetSource { get; set; }
        public long TotalSamples { get; private set; }
        public ManualFocusSample? Best { get; private set; }
        public ManualFocusPreviewImages? Images { get; private set; }
        public string? LastFailure { get; set; }
        public string? StopReason { get; set; }
        public string? EndReason { get; private set; }
        public DateTimeOffset? EndedUtc { get; private set; }
        public string? ModuleType { get; init; }
        public AgentCore.CameraFocusPreviewFidelity? Fidelity { get; init; }
        public AgentCore.CameraSimulatedFocusModel? SimulatedFocus { get; init; }
        public DateTimeOffset LastObservedUtc { get; set; }
        public ManualFocusRetentionState Retention { get; set; }
        public string? SavedRecordId { get; set; }
        public bool Saving { get; set; }

        public void Record(ManualFocusSample sample, ManualFocusPreviewOutcome outcome, PixelPoint? usedTarget, PixelPoint? usedHint)
        {
            if (State != ManualFocusSessionState.Running)
            {
                return;
            }
            if (_history.Count == historyCapacity)
            {
                _history.Dequeue();
            }
            _history.Enqueue(sample);
            TotalSamples = sample.Sequence;
            LastFailure = null;
            Images = outcome.Images;
            if (sample.Measurement.HasMeasurement &&
                (Best is null || sample.Measurement.HalfFluxDiameterPixels < Best.Measurement.HalfFluxDiameterPixels))
            {
                Best = sample;
            }
            // Only advance tracking when the operator did not retarget while this exposure was in flight.
            if (Target == usedTarget && Hint == usedHint)
            {
                Target = outcome.Target;
                if (outcome.TargetSelected)
                {
                    Hint = null;
                }
            }
        }

        public void End(ManualFocusSessionState state, string reason, DateTimeOffset endedUtc)
        {
            State = state;
            EndReason = reason;
            EndedUtc = endedUtc;
        }

        public void Discard()
        {
            _history.Clear();
            Best = null;
            Images = null;
            Retention = ManualFocusRetentionState.Discarded;
        }

        public ManualFocusSessionSnapshot ToSnapshot() => new(
            Id, State, OwnerId, startedUtc, EndedUtc, safetyDeadlineUtc, Settings, Position, Target ?? Hint,
            TargetSource, TotalSamples, historyCapacity, _history.ToArray(), Best, LastFailure, EndReason, ModuleType,
            Fidelity, SimulatedFocus, Retention, SavedRecordId);

        public void Dispose()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
        }
    }
}
