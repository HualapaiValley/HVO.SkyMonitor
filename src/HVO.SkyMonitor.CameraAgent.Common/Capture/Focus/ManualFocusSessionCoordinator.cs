using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>
/// Owns one exclusive manual focus session at a time. A session holds exclusive camera acquisition only while a
/// preview sample is being measured, so normal capture resumes between samples and can never interleave with a
/// preview. Preview frames are measured in memory and are never admitted to the capture pipeline. Session state is
/// in memory only; after a restart no session is resumed and no retained receipt is claimed.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The type implements IDisposable and disposes its sample gate.")]
public sealed partial class ManualFocusSessionCoordinator : IDisposable
{
    private readonly IManualFocusExclusiveAcquisition _acquisition;
    private readonly IManualFocusPreviewFrameSource _frameSource;
    private readonly TimeProvider _timeProvider;
    private readonly ManualFocusSessionLimits _limits;
    private readonly ILogger<ManualFocusSessionCoordinator>? _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private ManualFocusSessionSnapshot _snapshot = ManualFocusSessionSnapshot.Idle;
    private bool _disposed;

    public ManualFocusSessionCoordinator(
        IManualFocusExclusiveAcquisition acquisition,
        IManualFocusPreviewFrameSource frameSource,
        TimeProvider timeProvider,
        ManualFocusSessionLimits? limits = null,
        ILogger<ManualFocusSessionCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(frameSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _acquisition = acquisition;
        _frameSource = frameSource;
        _timeProvider = timeProvider;
        _limits = limits ?? ManualFocusSessionLimits.Default;
        _logger = logger;
    }

    public ManualFocusSessionAvailability Availability => _frameSource.GetAvailability();

    /// <summary>Current session view. An expired session is reclaimed as <see cref="ManualFocusSessionState.TimedOut"/>.</summary>
    public ManualFocusSessionSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            ReclaimExpiredLocked(_timeProvider.GetUtcNow());
            return _snapshot;
        }
    }

    public Task<ManualFocusSessionSnapshot> StartAsync(
        ManualFocusSessionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request, actor);
        var availability = _frameSource.GetAvailability();
        if (!availability.Available)
        {
            throw new ManualFocusSessionUnavailableException(availability.Reason);
        }
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            ReclaimExpiredLocked(now);
            if (_snapshot.State == ManualFocusSessionState.Active)
            {
                throw new ManualFocusSessionConflictException("A manual focus session is already active.");
            }
            var sessionId = Guid.NewGuid().ToString("N");
            _snapshot = new ManualFocusSessionSnapshot(
                sessionId,
                ManualFocusSessionState.Active,
                now,
                null,
                now + request.SessionTimeout,
                request.Settings,
                request.Region,
                request.MinimumAcceptedSources,
                [],
                null,
                null);
            if (_logger is not null)
            {
                LogSessionStarted(_logger, sessionId, actor, request.Settings.Exposure, request.Settings.Gain);
            }
            return Task.FromResult(_snapshot);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed preview must be recorded as a session failure reason and surfaced, never hidden.")]
    public async Task<ManualFocusSample> SampleAsync(
        string sessionId,
        string actor,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateActor(actor);
        ValidateSessionId(sessionId);
        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ManualFocusSessionSnapshot current;
            lock (_sync)
            {
                ReclaimExpiredLocked(_timeProvider.GetUtcNow());
                current = _snapshot;
                if (current.State != ManualFocusSessionState.Active)
                {
                    throw new ManualFocusSessionStateException("No manual focus session is active.");
                }
                if (!string.Equals(current.SessionId, sessionId, StringComparison.Ordinal))
                {
                    throw new ManualFocusSessionStateException(
                        "The manual focus session identifier does not match the active session.");
                }
                if (current.Samples.Count >= _limits.MaximumSamples)
                {
                    throw new ManualFocusSessionValidationException("The session reached its sample limit.");
                }
            }

            ManualFocusSample sample;
            try
            {
                sample = await _acquisition.ExecuteExclusiveAsync(
                    token => AcquireSampleAsync(current, token), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ManualFocusSessionConflictException)
            {
                RecordFailureLocked(sessionId, ManualFocusReasonCodes.CameraBusy);
                throw;
            }
            catch (Exception exception)
            {
                RecordFailureLocked(sessionId, exception.Message);
                throw;
            }

            lock (_sync)
            {
                // Discard the sample if the session ended while the preview was in flight.
                if (_snapshot.State == ManualFocusSessionState.Active &&
                    string.Equals(_snapshot.SessionId, sessionId, StringComparison.Ordinal))
                {
                    var samples = new List<ManualFocusSample>(_snapshot.Samples) { sample };
                    _snapshot = _snapshot with { Samples = samples, FailureReason = null };
                    return sample;
                }
            }
            throw new ManualFocusSessionStateException("The manual focus session ended before the sample was recorded.");
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    public Task<ManualFocusSessionSnapshot> EndAsync(
        string sessionId,
        string actor,
        ManualFocusSessionDisposition disposition,
        string? reason,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateActor(actor);
        ValidateSessionId(sessionId);
        if (!Enum.IsDefined(disposition))
        {
            throw new ManualFocusSessionValidationException("The session disposition is invalid.");
        }
        if (reason?.Length > 512)
        {
            throw new ManualFocusSessionValidationException("The session end reason is too long.");
        }
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            ReclaimExpiredLocked(now);
            if (_snapshot.State != ManualFocusSessionState.Active ||
                !string.Equals(_snapshot.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new ManualFocusSessionStateException("No matching manual focus session is active.");
            }
            var retained = disposition == ManualFocusSessionDisposition.Retained && _snapshot.Samples.Count > 0;
            _snapshot = _snapshot with
            {
                State = retained ? ManualFocusSessionState.Completed : ManualFocusSessionState.Cancelled,
                EndedUtc = now,
                Samples = retained ? _snapshot.Samples : [],
                FailureReason = null,
                EndReason = reason ?? (retained ? ManualFocusReasonCodes.Retained : ManualFocusReasonCodes.Discarded)
            };
            if (_logger is not null)
            {
                LogSessionEnded(_logger, sessionId, actor, _snapshot.State, _snapshot.Samples.Count);
            }
            return Task.FromResult(_snapshot);
        }
    }

    public Task<ManualFocusSessionSnapshot> CancelAsync(
        string sessionId,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateActor(actor);
        ValidateSessionId(sessionId);
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            ReclaimExpiredLocked(now);
            if (_snapshot.State == ManualFocusSessionState.Active &&
                string.Equals(_snapshot.SessionId, sessionId, StringComparison.Ordinal))
            {
                _snapshot = _snapshot with
                {
                    State = ManualFocusSessionState.Cancelled,
                    EndedUtc = now,
                    Samples = [],
                    FailureReason = null,
                    EndReason = reason ?? ManualFocusReasonCodes.Cancelled
                };
                if (_logger is not null)
                {
                    LogSessionCancelled(_logger, sessionId, actor);
                }
            }
            return Task.FromResult(_snapshot);
        }
    }

    private async Task<ManualFocusSample> AcquireSampleAsync(
        ManualFocusSessionSnapshot session,
        CancellationToken cancellationToken)
    {
        var frame = await _frameSource.AcquireAsync(session.Settings, session.Region, cancellationToken)
            .ConfigureAwait(false);
        var cropped = Crop(frame, session.Region);
        var options = new FocusSharpnessOptions
        {
            MinimumAcceptedSources = session.MinimumAcceptedSources
        };
        var measurement = FocusSharpnessAnalyzer.Analyze(
            cropped.Pixels, cropped.ValidMask, cropped.SaturatedMask, cropped.Width, cropped.Height,
            options, cancellationToken);
        var provenance = new ManualFocusSampleProvenance(
            frame.FrameId,
            frame.AgentId,
            frame.RigId,
            cropped.Width,
            cropped.Height,
            cropped.Region,
            FocusSharpnessMeasurement.MetricDefinition,
            FocusSharpnessMeasurement.Units,
            FocusSharpnessAnalyzer.AlgorithmVersion,
            measurement.SettingsIdentitySha256,
            PreviewSettingsIdentity(session.Settings, cropped.Region));
        return new ManualFocusSample(
            session.Samples.Count,
            _timeProvider.GetUtcNow(),
            measurement,
            provenance);
    }

    private void ReclaimExpiredLocked(DateTimeOffset now)
    {
        if (_snapshot.State == ManualFocusSessionState.Active && now >= _snapshot.DeadlineUtc)
        {
            if (_logger is not null)
            {
                LogSessionTimedOut(_logger, _snapshot.SessionId);
            }
            _snapshot = _snapshot with
            {
                State = ManualFocusSessionState.TimedOut,
                EndedUtc = _snapshot.DeadlineUtc,
                Samples = [],
                FailureReason = ManualFocusReasonCodes.TimedOut,
                EndReason = ManualFocusReasonCodes.TimedOut
            };
        }
    }

    private void RecordFailureLocked(string sessionId, string reason)
    {
        if (_snapshot.State == ManualFocusSessionState.Active &&
            string.Equals(_snapshot.SessionId, sessionId, StringComparison.Ordinal))
        {
            _snapshot = _snapshot with { FailureReason = reason };
        }
    }

    private static CroppedFrame Crop(ManualFocusPreviewFrame frame, ManualFocusRegion? requested)
    {
        if (frame.Width <= 0 || frame.Height <= 0 ||
            frame.Pixels.Length != checked(frame.Width * frame.Height))
        {
            throw new ManualFocusSessionValidationException("The preview frame dimensions or pixel count are invalid.");
        }
        if (frame.ValidMask.Length != frame.Pixels.Length)
        {
            throw new ManualFocusSessionValidationException("The preview frame valid mask does not match the pixels.");
        }
        if (frame.SaturatedMask.Length != 0 && frame.SaturatedMask.Length != frame.Pixels.Length)
        {
            throw new ManualFocusSessionValidationException("The preview frame saturation mask does not match the pixels.");
        }
        var region = requested ?? new ManualFocusRegion(0, 0, frame.Width, frame.Height);
        if (region.X < 0 || region.Y < 0 || region.Width < 16 || region.Height < 16 ||
            region.X + region.Width > frame.Width || region.Y + region.Height > frame.Height)
        {
            throw new ManualFocusSessionValidationException("The focus region is outside the preview frame.");
        }
        if (region.X == 0 && region.Y == 0 && region.Width == frame.Width && region.Height == frame.Height)
        {
            return new(frame.Pixels, frame.ValidMask, frame.SaturatedMask, region, frame.Width, frame.Height);
        }
        var count = checked(region.Width * region.Height);
        var pixels = new double[count];
        var valid = new bool[count];
        var saturated = frame.SaturatedMask.Length == 0 ? [] : new bool[count];
        for (var y = 0; y < region.Height; y++)
        {
            var sourceRow = (region.Y + y) * frame.Width + region.X;
            var destinationRow = y * region.Width;
            Array.Copy(frame.Pixels, sourceRow, pixels, destinationRow, region.Width);
            Array.Copy(frame.ValidMask, sourceRow, valid, destinationRow, region.Width);
            if (saturated.Length > 0)
            {
                Array.Copy(frame.SaturatedMask, sourceRow, saturated, destinationRow, region.Width);
            }
        }
        return new(pixels, valid, saturated, region, region.Width, region.Height);
    }

    private void ValidateRequest(ManualFocusSessionRequest request, string actor)
    {
        ValidateActor(actor);
        if (request.Settings.Exposure < _limits.MinimumExposure ||
            request.Settings.Exposure > _limits.MaximumExposure)
        {
            throw new ManualFocusSessionValidationException("The preview exposure is outside the allowed range.");
        }
        if (!double.IsFinite(request.Settings.Gain) || request.Settings.Gain < 0 ||
            request.Settings.Gain > _limits.MaximumGain)
        {
            throw new ManualFocusSessionValidationException("The preview gain is outside the allowed range.");
        }
        if (request.SessionTimeout < _limits.MinimumSessionTimeout ||
            request.SessionTimeout > _limits.MaximumSessionTimeout)
        {
            throw new ManualFocusSessionValidationException("The session timeout is outside the allowed range.");
        }
        if (request.MinimumAcceptedSources < 1)
        {
            throw new ManualFocusSessionValidationException("At least one accepted source is required.");
        }
        if (request.Region is { } region &&
            (region.X < 0 || region.Y < 0 || region.Width < 16 || region.Height < 16))
        {
            throw new ManualFocusSessionValidationException("The focus region is invalid.");
        }
    }

    private static void ValidateActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
        {
            throw new ManualFocusSessionValidationException(
                "An actor is required and must not exceed 128 characters.");
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 64)
        {
            throw new ManualFocusSessionValidationException("A valid session identifier is required.");
        }
    }

    private static string PreviewSettingsIdentity(ManualFocusPreviewSettings settings, ManualFocusRegion region)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "manual-focus-preview-settings-v1",
            exposureTicks = settings.Exposure.Ticks,
            gain = settings.Gain,
            region.X,
            region.Y,
            region.Width,
            region.Height
        })));

    [LoggerMessage(EventId = 4700, Level = LogLevel.Information,
        Message = "Manual focus session {SessionId} started by {Actor} with exposure {Exposure} and gain {Gain}.")]
    private static partial void LogSessionStarted(
        ILogger logger, string sessionId, string actor, TimeSpan exposure, double gain);

    [LoggerMessage(EventId = 4701, Level = LogLevel.Information,
        Message = "Manual focus session {SessionId} ended by {Actor} as {State} with {SampleCount} samples.")]
    private static partial void LogSessionEnded(
        ILogger logger, string sessionId, string actor, ManualFocusSessionState state, int sampleCount);

    [LoggerMessage(EventId = 4702, Level = LogLevel.Information,
        Message = "Manual focus session {SessionId} cancelled by {Actor}.")]
    private static partial void LogSessionCancelled(ILogger logger, string sessionId, string actor);

    [LoggerMessage(EventId = 4703, Level = LogLevel.Warning,
        Message = "Manual focus session {SessionId} timed out and was reclaimed.")]
    private static partial void LogSessionTimedOut(ILogger logger, string sessionId);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _sampleGate.Dispose();
    }

    private sealed record CroppedFrame(
        double[] Pixels,
        bool[] ValidMask,
        bool[] SaturatedMask,
        ManualFocusRegion Region,
        int Width,
        int Height);
}
