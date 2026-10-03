using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// The manual focus workspace. The page polls the one CameraAgent focus session every second, and while the signed-in
/// operator owns a running session each poll is also the owner heartbeat: closing the page, losing the browser
/// connection, or losing authorization stops the heartbeat, and the coordinator ends the session at its observer
/// timeout. The server keeps a disconnected circuit's timers running, so the heartbeat is sent only while
/// <see cref="CircuitConnectionState"/> reports a connected browser. Every number shown is measured
/// from the preview's own pixels; nothing here moves hardware.
/// </summary>
public sealed partial class FocusPage : ComponentBase, IAsyncDisposable
{
    internal const string MetricHfd = "hfd";
    internal const string MetricFwhm = "fwhm";
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);
    private static readonly int[] SafetyMinuteOptions = [5, 15, 30, 60];
    private const int RecentSampleCount = 12;
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private PeriodicTimer? _timer;
    private Task? _pollTask;
    private int _disposeStarted;
    private bool _denied;
    private long _commandGeneration;

    private FocusUiStatus? _status;
    private IReadOnlyList<ManualFocusSessionRecordSummary> _saved = [];
    private string? _savedListKey;
    private string? _selectedRecordId;
    private long _imageSequence = -1;
    private string? _overviewUrl;
    private string? _starUrl;
    private FocusViewer _viewer = FocusViewer.Star;

    private bool _formInitialized;
    private bool _dirty;
    private string _exposureText = FormatExposure(TimeSpan.FromSeconds(1));
    private double _gain = 110;
    private double _position;
    private int _safetyMinutes = 15;
    private string _metric = MetricHfd;

    private bool _busy;
    private string? _message;
    private bool _messageIsError;

    private string? _settingsError;
    private SettingsField _invalidField;
    private bool _focusInvalidField;
    private ElementReference _exposureInput;
    private ElementReference _gainInput;
    private ElementReference _positionInput;

    private ElementReference _fieldElement;
    private IJSObjectReference? _module;
    private DotNetObjectReference<FocusPage>? _reference;
    private bool _bindPicker;

    [Inject] internal ICameraAgentFocusUiService FocusService { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] internal CircuitConnectionState Connection { get; set; } = default!;

    private enum FocusViewer
    {
        Star,
        Field
    }

    private enum SettingsField
    {
        None,
        Exposure,
        Gain,
        Position
    }

    private static OperationsSection Section => OperationsSectionCatalog.Get("focus");

    private ManualFocusSessionSnapshot Session => _status?.Session ?? ManualFocusSessionSnapshot.Idle;

    private ManualFocusPreviewImages? Images => _status?.Images;

    private ManualFocusSessionLimits Limits => _status?.Limits ?? ManualFocusSessionLimits.Default;

    private bool RetentionAvailable => _status?.RetentionAvailable == true;

    private CameraFocusPreviewFidelity? Fidelity => Session.Fidelity ?? _status?.Availability.Fidelity;

    private CameraSimulatedFocusModel? SimulatedFocus => Session.SimulatedFocus ?? _status?.Availability.SimulatedFocus;

    private bool IsOwner => _status?.IsOwner == true;

    private bool HasEnded => Session.State is ManualFocusSessionState.Stopped or ManualFocusSessionState.TimedOut
        or ManualFocusSessionState.Faulted;

    private bool CanSave => HasEnded && IsOwner && RetentionAvailable && Session.History.Count > 0 &&
        Session.Retention == ManualFocusRetentionState.InMemoryOnly;

    private bool CanDiscard => HasEnded && IsOwner && Session.History.Count > 0 &&
        Session.Retention == ManualFocusRetentionState.InMemoryOnly;

    // Prerendered markup has no circuit behind it, so input given before the page is live would be silently lost.
    private bool Busy => _busy || !RendererInfo.IsInteractive;

    private bool CanStartSession => _status is { CanControl: true, Availability.Available: true } && !Session.IsRunning &&
        !CanSave && !Busy;

    private bool CanEditSettings => CanStartSession || (Session.IsRunning && IsOwner && !Busy);

    private bool CanAdjust => Session.IsRunning && IsOwner && !Busy;

    private bool CanApply => CanAdjust && _dirty;

    private bool PrimaryEnabled => !Busy && _status is not null && (Session.IsRunning ? IsOwner : CanSave || CanStartSession);

    private string PrimaryLabel => Session.IsRunning ? "End session"
        : CanSave ? "Save session result"
        : HasEnded ? "Start new session"
        : "Start focus session";

    private string? ControlReason
    {
        get
        {
            if (_status is null)
            {
                return null;
            }
            if (!RendererInfo.IsInteractive)
            {
                return "Connecting to CameraAgent; the controls are enabled when the page is live.";
            }
            if (!_status.CanControl)
            {
                return "Your account can view focus sessions but cannot start or change them.";
            }
            if (Session.IsRunning && !IsOwner)
            {
                return "Another operator owns the running focus session. It ends when they end it, leave the page, or its safety limit expires.";
            }
            if (!Session.IsRunning && !CanSave && !_status.Availability.Available)
            {
                return _status.Availability.Reason;
            }
            return null;
        }
    }

    private string StateTitle => _status is null ? "Loading focus session" : Session.State switch
    {
        ManualFocusSessionState.Running => "Measuring",
        ManualFocusSessionState.Stopped => "Session ended",
        ManualFocusSessionState.TimedOut => "Session timed out",
        ManualFocusSessionState.Faulted => "Session failed",
        _ => _status.Availability.Available ? "Ready to measure" : "Focus unavailable"
    };

    private string StateDetail => _status is null ? "Reading the CameraAgent focus session." : Session.State switch
    {
        ManualFocusSessionState.Running =>
            $"{Session.TotalSamples} {(Session.TotalSamples == 1 ? "sample" : "samples")}; safety limit {Session.SafetyDeadlineUtc.ToString("HH:mm:ss", Culture)} UTC",
        ManualFocusSessionState.Idle => "No focus session is active",
        _ => EndText(Session.EndReason) + (Session.Retention switch
        {
            ManualFocusRetentionState.Saved => " Result saved.",
            ManualFocusRetentionState.Discarded => " Result discarded.",
            _ => Session.History.Count > 0 ? " Result is in memory only." : string.Empty
        })
    };

    private string ChipText => _status is null ? "Loading" : Session.State switch
    {
        ManualFocusSessionState.Running => "Live",
        ManualFocusSessionState.Stopped => "Ended",
        ManualFocusSessionState.TimedOut => "Timed out",
        ManualFocusSessionState.Faulted => "Failed",
        _ => _status.Availability.Available ? "Idle" : "Unavailable"
    };

    private string ChipTone => Session.State switch
    {
        ManualFocusSessionState.Running => "success",
        ManualFocusSessionState.TimedOut => "warning",
        ManualFocusSessionState.Faulted => "danger",
        ManualFocusSessionState.Idle when _status is { Availability.Available: false } => "warning",
        _ => "neutral"
    };

    private string MetricHeading => _metric == MetricFwhm ? "FWHM / lower is better" : "Half-flux diameter / lower is better";

    private double? LatestMetric => Session.Latest is { Measurement.Status: FocusStarStatus.Valid } latest
        ? MetricOf(latest)
        : null;

    private string NoMetricText => Session.Latest is null ? "no sample" : "not measured";

    private IEnumerable<ManualFocusSample> RecentSamples => Session.History.Reverse().Take(RecentSampleCount);

    private IEnumerable<int> SafetyChoices => SafetyMinuteOptions.Where(minutes =>
        TimeSpan.FromMinutes(minutes) >= Limits.MinimumSafetyTimeout && TimeSpan.FromMinutes(minutes) <= Limits.MaximumSafetyTimeout);

    private string ApplyHelp => !Session.IsRunning ? "Settings apply when the session starts."
        : !IsOwner ? "Only the session owner can change settings."
        : _dirty ? "Changed settings take effect from the next exposure; the exposure in progress finishes first."
        : "Change exposure, gain, or the simulated position, then apply it to the next exposure.";

    private string RetentionText => Session.State == ManualFocusSessionState.Idle ? "No session" : Session.Retention switch
    {
        ManualFocusRetentionState.Saved => "Saved",
        ManualFocusRetentionState.Discarded => "Discarded",
        _ => RetentionAvailable ? "In memory until saved" : "In memory only; retention not configured"
    };

    private ManualFocusSessionRecordSummary? SelectedExport =>
        _saved.FirstOrDefault(record => record.Verified && string.Equals(record.RecordId, _selectedRecordId, StringComparison.Ordinal)) ??
        _saved.FirstOrDefault(record => record.Verified);

    private string HistoryNote => !RetentionAvailable
        ? "Focus session retention is not configured, so session results stay in CameraAgent memory and are lost on restart."
        : _saved.Count == 0
            ? "Save an ended session to keep its checksummed record here. Unsaved results are lost on restart or when a new session starts."
            : "Export evidence downloads the selected record exactly as stored, with its SHA-256. Unsaved results are lost on restart.";

    private string EmptyViewerText => Session.IsRunning
        ? "Waiting for the first measured preview."
        : "No preview. Start a focus session to expose the target region.";

    private string EmptyViewerLabel => Session.IsRunning ? "Target region / waiting for preview" : "Target region / no preview";

    private string StarAlt => Images is { } images
        ? $"Zoomed preview of the target star: source pixels {images.StarWindowX} to {images.StarWindowX + images.StarWindowWidth} by {images.StarWindowY} to {images.StarWindowY + images.StarWindowHeight}."
        : "Target star preview.";

    private string TargetLabel
    {
        get
        {
            var source = Session.TargetSource == ManualFocusTargetSource.Operator ? "operator pick" : "automatic";
            return Session.Latest is { Measurement.Centroid: { } centroid } latest
                ? $"Target {centroid.X.ToString("F1", Culture)}, {centroid.Y.ToString("F1", Culture)} / {source}" +
                  (latest.Measurement.HalfFluxDiameterPixels is { } hfd ? $" / HFD {hfd.ToString("F2", Culture)} px" : string.Empty)
                : $"Target region / {source} / no star measured";
        }
    }

    private PixelPoint? CurrentTarget => Session.Latest?.Measurement.Centroid ?? Session.Target;

    private (string? Line, IReadOnlyList<TrendPoint> Points) Trend
    {
        get
        {
            var valid = Session.History
                .Where(sample => sample.Measurement.Status == FocusStarStatus.Valid && MetricOf(sample) is not null)
                .ToArray();
            if (valid.Length == 0)
            {
                return (null, []);
            }
            var values = valid.Select(sample => MetricOf(sample)!.Value).ToArray();
            var min = values.Min();
            var max = values.Max();
            var range = max - min;
            double X(int index) => valid.Length == 1 ? 120 : 6 + index * 228d / (valid.Length - 1);
            double Y(double value) => range <= 0 ? 45 : 10 + (max - value) / range * 70;
            var line = string.Join(' ', values.Select((value, index) =>
                $"{(index == 0 || valid[index].ComparisonGroupId is null ||
                    valid[index].ComparisonGroupId != valid[index - 1].ComparisonGroupId ||
                    valid[index].Sequence != valid[index - 1].Sequence + 1 ? 'M' : 'L')}{X(index).ToString("F1", Culture)} {Y(value).ToString("F1", Culture)}"));
            var bestIndex = Session.Best is { } best ? Array.FindIndex(valid, sample => sample.Sequence == best.Sequence) : -1;
            var points = new List<TrendPoint>();
            if (bestIndex >= 0)
            {
                points.Add(new(X(bestIndex), Y(values[bestIndex]), "best"));
            }
            points.Add(new(X(values.Length - 1), Y(values[^1]), "latest"));
            return (line, points);
        }
    }

    private string TrendLabel
    {
        get
        {
            var count = Session.History.Count(sample => sample.Measurement.Status == FocusStarStatus.Valid);
            var name = _metric == MetricFwhm ? "FWHM" : "Half-flux diameter";
            return count == 0
                ? $"{name} trend: no valid samples yet."
                : $"{name} trend over the last {count} valid samples; lower is sharper within the same comparison group. " +
                    "Changed or unknown conditions are not connected.";
        }
    }

    private bool HasChangedComparisonConditions => Session.History.Select(sample => sample.ComparisonGroupId)
        .Append(Session.Best?.ComparisonGroupId).Where(static group => group is not null).Distinct().Skip(1).Any();

    private string BestLabel => HasChangedComparisonConditions ? "Session minimum (conditions differ)" : "Best valid";

    private string? ComparisonNote => Session.History.Any(static sample => sample.ComparisonGroupId is null)
        ? "Some comparison conditions are unknown. Those samples are not connected in the trend."
        : HasChangedComparisonConditions
            ? "Settings or target changed. Trend lines connect samples only within the same conditions; the session minimum across groups does not prove a focus improvement."
            : null;

    protected override async Task OnInitializedAsync()
    {
        _lifetime = new CancellationTokenSource();
        await RefreshAsync(_lifetime.Token);
        if (_denied)
        {
            return;
        }
        await LoadSavedAsync(_lifetime.Token);
        _timer = new PeriodicTimer(RefreshInterval, TimeProvider);
        _pollTask = PollAsync(_lifetime.Token);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusInvalidField && _disposeStarted == 0)
        {
            _focusInvalidField = false;
            try
            {
                // The rejected field may be far from the button that submitted it; focusing it brings the reason into view.
                await (_invalidField switch
                {
                    SettingsField.Gain => _gainInput,
                    SettingsField.Position => _positionInput,
                    _ => _exposureInput,
                }).FocusAsync();
            }
            catch (Exception exception) when (exception is JSException or JSDisconnectedException or OperationCanceledException)
            {
            }
        }
        if (!_bindPicker || _viewer != FocusViewer.Field || Images is null || _disposeStarted != 0)
        {
            return;
        }
        _bindPicker = false;
        try
        {
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/FocusPage.razor.js");
            _reference ??= DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("bindPicker", _fieldElement, _reference);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or OperationCanceledException)
        {
            // Catalog-star buttons and Automatic target remain available without the pointer picker.
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    // One refresh at a time; a refresh that overlaps a command is discarded so a stale snapshot never replaces the
    // command's result.
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_denied || _disposeStarted != 0 || !await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RefreshTimeout);
            var generation = Interlocked.Read(ref _commandGeneration);
            var result = await FocusService.GetStatusAsync(timeout.Token).ConfigureAwait(false);
            if (Connection.IsConnected &&
                result is { IsSuccess: true, Value: { Session.IsRunning: true, IsOwner: true } observed })
            {
                var heartbeat = await FocusService.ObserveAsync(observed.Session.SessionId, timeout.Token).ConfigureAwait(false);
                if (heartbeat.Kind == OperatorUiResultKind.Unauthorized)
                {
                    result = OperatorUiResult<FocusUiStatus>.Failure(heartbeat.Kind, heartbeat.Message ?? string.Empty);
                }
            }
            await InvokeAsync(async () =>
            {
                if (_disposeStarted != 0 || generation != Interlocked.Read(ref _commandGeneration))
                {
                    return;
                }
                if (Accept(result, "The focus session state is unavailable."))
                {
                    await ApplyStatusAsync(result.Value!, cancellationToken);
                }
                StateHasChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await InvokeAsync(() =>
            {
                SetMessage("The focus session state did not respond in time; retrying.", error: true);
                StateHasChanged();
            }).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task ApplyStatusAsync(FocusUiStatus status, CancellationToken cancellationToken)
    {
        if (status.Images?.Sequence != status.Session.Latest?.Sequence)
        {
            status = status with { Images = null };
        }
        var previous = _status;
        _status = status;
        if (previous?.Session.SessionId != status.Session.SessionId)
        {
            _imageSequence = -1;
        }
        SetDisplayImages(status.Images);
        SyncForm(status, previous);
        var savedKey = status.Session.SavedRecordId;
        if (savedKey is not null && !string.Equals(savedKey, _savedListKey, StringComparison.Ordinal))
        {
            _savedListKey = savedKey;
            await LoadSavedAsync(cancellationToken);
        }
    }

    private void SetDisplayImages(ManualFocusPreviewImages? latestImages, bool resetViewer = true)
    {
        if (latestImages is { } images && images.Sequence != _imageSequence)
        {
            _imageSequence = images.Sequence;
            _overviewUrl = DataUrl(images.OverviewJpeg);
            _starUrl = images.StarJpeg is { } star ? DataUrl(star) : null;
            _bindPicker = _viewer == FocusViewer.Field;
        }
        else if (latestImages is null)
        {
            _imageSequence = -1;
            _overviewUrl = null;
            _starUrl = null;
            if (resetViewer)
            {
                _viewer = FocusViewer.Star;
            }
        }
    }

    // The form shows what the session is exposing with unless the operator has unapplied edits.
    private void SyncForm(FocusUiStatus status, FocusUiStatus? previous)
    {
        var session = status.Session;
        var model = session.SimulatedFocus ?? status.Availability.SimulatedFocus;
        if (!_formInitialized)
        {
            _formInitialized = true;
            _safetyMinutes = (int)Math.Round(status.Limits.DefaultSafetyTimeout.TotalMinutes);
            _position = model?.DefaultPosition ?? 0;
        }
        else if (model is not null && previous?.Session.SimulatedFocus is null && previous?.Availability.SimulatedFocus is null)
        {
            _position = model.DefaultPosition;
        }
        if (_dirty || !session.IsRunning)
        {
            return;
        }
        _exposureText = FormatExposure(session.Settings.Exposure);
        _gain = session.Settings.Gain;
        if (session.SimulatedFocusPosition is { } position)
        {
            _position = position;
        }
    }

    private async Task LoadSavedAsync(CancellationToken cancellationToken)
    {
        var result = await FocusService.GetSavedSessionsAsync(cancellationToken);
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            Deny();
            return;
        }
        _saved = result.IsSuccess ? result.Value ?? [] : [];
        if (!result.IsSuccess)
        {
            SetMessage(result.Message ?? "Saved focus sessions are unavailable.", error: true);
        }
    }

    private Task PrimaryAsync()
    {
        if (Session.IsRunning)
        {
            return CommandAsync(token => FocusService.StopAsync(Session.SessionId, token), "Session ended. Save or discard its result.");
        }
        if (CanSave)
        {
            return SaveAsync();
        }
        return StartAsync();
    }

    private Task StartAsync()
    {
        if (!TryReadSettings(out var settings))
        {
            return Task.CompletedTask;
        }
        var request = new ManualFocusSessionRequest(
            settings,
            SimulatedFocus is not null ? _position : null,
            SafetyTimeout: TimeSpan.FromMinutes(_safetyMinutes));
        _viewer = FocusViewer.Star;
        return CommandAsync(token => FocusService.StartAsync(request, token), null, clearDirty: true);
    }

    private async Task SaveAsync()
    {
        await CommandAsync(token => FocusService.SaveAsync(Session.SessionId, token), null);
        if (Session.SavedRecordId is { } recordId)
        {
            _savedListKey = recordId;
            _selectedRecordId = recordId;
            await LoadSavedAsync(_lifetime?.Token ?? CancellationToken.None);
            SetMessage($"Session result saved as {recordId}. Its checksum is verified on every read and export.", error: false);
        }
    }

    private Task DiscardAsync()
        => CommandAsync(token => FocusService.DiscardAsync(Session.SessionId, token), "Session result discarded; nothing was retained.");

    private Task ApplyAsync()
    {
        if (!TryReadSettings(out var settings))
        {
            return Task.CompletedTask;
        }
        var adjustment = new ManualFocusAdjustment(
            settings == Session.Settings ? null : settings,
            SimulatedFocus is not null && _position != Session.SimulatedFocusPosition ? _position : null);
        if (adjustment.Settings is null && adjustment.SimulatedFocusPosition is null)
        {
            _dirty = false;
            return Task.CompletedTask;
        }
        return CommandAsync(token => FocusService.AdjustAsync(Session.SessionId, adjustment, token),
            "Applied; the next exposure uses the new settings.", clearDirty: true);
    }

    // Before a session starts a nudge only edits the form; while running it moves the simulated focus immediately,
    // the way an operator turns a focuser and watches the next sample.
    private Task NudgeAsync(double delta)
    {
        if (SimulatedFocus is not { } model)
        {
            return Task.CompletedTask;
        }
        _position = Math.Clamp(Math.Round(_position + delta), model.MinimumPosition, model.MaximumPosition);
        if (!Session.IsRunning)
        {
            return Task.CompletedTask;
        }
        var position = _position;
        return CommandAsync(token => FocusService.AdjustAsync(Session.SessionId, new ManualFocusAdjustment(SimulatedFocusPosition: position), token),
            null);
    }

    private Task RetargetAsync(PixelPoint target)
    {
        if (!CanAdjust)
        {
            return Task.CompletedTask;
        }
        _viewer = FocusViewer.Star;
        return CommandAsync(token => FocusService.AdjustAsync(Session.SessionId, new ManualFocusAdjustment(Target: target), token),
            $"Targeting the star nearest {target.X.ToString("F0", Culture)}, {target.Y.ToString("F0", Culture)} from the next exposure.");
    }

    private Task AutomaticTargetAsync()
    {
        if (!CanAdjust)
        {
            return Task.CompletedTask;
        }
        _viewer = FocusViewer.Star;
        return CommandAsync(token => FocusService.AdjustAsync(Session.SessionId, new ManualFocusAdjustment(ResetToAutomaticTarget: true), token),
            "Returning to the automatic target from the next exposure.");
    }

    /// <summary>A click on the whole-frame preview, as fractions of the rendered image.</summary>
    [JSInvokable]
    public Task PickAsync(double fractionX, double fractionY)
    {
        if (_disposeStarted != 0 || Images is not { } images || !double.IsFinite(fractionX) || !double.IsFinite(fractionY))
        {
            return Task.CompletedTask;
        }
        var target = new PixelPoint(
            Math.Clamp(fractionX * images.OverviewWidth * images.OverviewBinFactor, 0, images.FrameWidth - 0.5),
            Math.Clamp(fractionY * images.OverviewHeight * images.OverviewBinFactor, 0, images.FrameHeight - 0.5));
        return InvokeAsync(async () =>
        {
            await RetargetAsync(target);
            StateHasChanged();
        });
    }

    private void ToggleViewer()
    {
        _viewer = _viewer == FocusViewer.Field ? FocusViewer.Star : FocusViewer.Field;
        _bindPicker = _viewer == FocusViewer.Field;
    }

    private void MarkDirty() => _dirty = true;

    private async Task CommandAsync(
        Func<CancellationToken, ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>>> command,
        string? success,
        bool clearDirty = false)
    {
        if (_busy || _denied || _disposeStarted != 0)
        {
            return;
        }
        _busy = true;
        Interlocked.Increment(ref _commandGeneration);
        try
        {
            var result = await command(_lifetime?.Token ?? CancellationToken.None);
            if (!Accept(result, "The focus session command could not be completed."))
            {
                return;
            }
            // The coordinator accepts a command only from the session owner, so a successful result is this operator's.
            // Commands can return a newer sample than the last status read. Until the next atomic read, clear an older
            // image rather than pairing that crop with the command's newer measurement or a newly started session.
            var images = result.Value!.SessionId == _status!.Session.SessionId &&
                result.Value.Latest?.Sequence == _status.Images?.Sequence ? _status.Images : null;
            _status = _status with { Session = result.Value, Images = images, IsOwner = true };
            SetDisplayImages(images, resetViewer: false);
            if (clearDirty)
            {
                _dirty = false;
            }
            if (success is not null)
            {
                SetMessage(success, error: false);
            }
            else
            {
                _message = null;
            }
        }
        catch (OperationCanceledException) when (_disposeStarted != 0)
        {
        }
        finally
        {
            _busy = false;
            Interlocked.Increment(ref _commandGeneration);
        }
    }

    private bool TryReadSettings(out ManualFocusPreviewSettings settings)
    {
        settings = Session.Settings;
        if (!TryParseExposure(_exposureText, out var exposure) ||
            exposure < Limits.MinimumExposure || exposure > Limits.MaximumExposure)
        {
            return RejectSettings(SettingsField.Exposure, $"Enter a preview exposure from {Seconds(Limits.MinimumExposure)} to {Seconds(Limits.MaximumExposure)}, for example 1.5s or 500ms.");
        }
        if (!double.IsFinite(_gain) || _gain < 0 || _gain > Limits.MaximumGain)
        {
            return RejectSettings(SettingsField.Gain, $"Enter a preview gain from 0 to {Limits.MaximumGain.ToString(Culture)}.");
        }
        if (SimulatedFocus is { } model && !model.Contains(_position))
        {
            return RejectSettings(SettingsField.Position, $"Enter a simulated focus position from {model.MinimumPosition.ToString("0", Culture)} to {model.MaximumPosition.ToString("0", Culture)}.");
        }
        _settingsError = null;
        _invalidField = SettingsField.None;
        settings = new ManualFocusPreviewSettings(exposure, _gain);
        return true;
    }

    private bool RejectSettings(SettingsField field, string message)
    {
        // Shown beside the settings rather than in the page banner, which is off screen from the Apply button.
        _settingsError = message;
        _invalidField = field;
        _focusInvalidField = true;
        _message = null;
        return false;
    }

    private string? InvalidAttribute(SettingsField field) => _invalidField == field ? "true" : null;

    internal static bool TryParseExposure(string? text, out TimeSpan exposure)
    {
        exposure = default;
        var value = text?.Trim() ?? string.Empty;
        var scale = 1d;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2];
            scale = 0.001;
        }
        else if (value.EndsWith('s') || value.EndsWith('S'))
        {
            value = value[..^1];
        }
        if (!double.TryParse(value.Trim(), NumberStyles.Float, Culture, out var number) || !double.IsFinite(number) || number <= 0)
        {
            return false;
        }
        var seconds = number * scale;
        if (seconds > TimeSpan.MaxValue.TotalSeconds / 2)
        {
            return false;
        }
        exposure = TimeSpan.FromSeconds(seconds);
        return true;
    }

    internal static string FormatExposure(TimeSpan exposure) => exposure.TotalSeconds.ToString("F3", Culture) + "s";

    private bool Accept<T>(OperatorUiResult<T> result, string fallback)
    {
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            Deny();
            return false;
        }
        if (!result.IsSuccess || result.Value is null)
        {
            SetMessage(result.Message ?? fallback, error: true);
            return false;
        }
        return true;
    }

    private void Deny()
    {
        if (_denied)
        {
            return;
        }
        _denied = true;
        _status = null;
        _saved = [];
        _overviewUrl = null;
        _starUrl = null;
        _lifetime?.Cancel();
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    private double? MetricOf(ManualFocusSample sample)
        => _metric == MetricFwhm ? sample.Measurement.FwhmPixels : sample.Measurement.HalfFluxDiameterPixels;

    private static string DataUrl(byte[] jpeg) => "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

    private static string FieldPosition(ManualFocusPreviewImages images, PixelPoint point)
    {
        var width = (double)images.OverviewWidth * images.OverviewBinFactor;
        var height = (double)images.OverviewHeight * images.OverviewBinFactor;
        return $"left:{(point.X / width * 100).ToString("F3", Culture)}%;top:{(point.Y / height * 100).ToString("F3", Culture)}%";
    }

    private string ReticlePosition(ManualFocusPreviewImages images)
    {
        if (Session.Latest?.Measurement.Centroid is not { } centroid || images.StarWindowWidth <= 0 || images.StarWindowHeight <= 0)
        {
            return "left:50%;top:50%";
        }
        var x = Math.Clamp((centroid.X - images.StarWindowX) / images.StarWindowWidth * 100, 0, 100);
        var y = Math.Clamp((centroid.Y - images.StarWindowY) / images.StarWindowHeight * 100, 0, 100);
        return $"left:{x.ToString("F3", Culture)}%;top:{y.ToString("F3", Culture)}%";
    }

    private static string CatalogLabel(ManualFocusCatalogStar star)
        => $"Target {star.Name ?? star.Id}, magnitude {star.Magnitude.ToString("F1", Culture)}, at {star.Pixel.X.ToString("F0", Culture)}, {star.Pixel.Y.ToString("F0", Culture)}";

    private static string Value(double? value) => value is { } number ? number.ToString("F2", Culture) : "—";

    private static string Seconds(TimeSpan value) => value < TimeSpan.FromSeconds(1)
        ? $"{value.TotalMilliseconds.ToString("0.###", Culture)} ms"
        : value < TimeSpan.FromMinutes(2)
            ? $"{value.TotalSeconds.ToString("0.###", Culture)} seconds"
            : $"{value.TotalMinutes.ToString("0.#", Culture)} minutes";

    private static string SettingsText(ManualFocusPreviewSettings settings, double? position)
        => $"{FormatExposure(settings.Exposure)} / gain {settings.Gain.ToString("0.##", Culture)}" +
           (position is { } at ? $" / position {at.ToString("0.#", Culture)}" : string.Empty);

    private static string ExportHref(ManualFocusSessionRecordSummary record)
        => $"/api/v1/operations/focus-sessions/{Uri.EscapeDataString(record.RecordId)}/export";

    private static string RecordTitle(ManualFocusSessionRecordSummary record)
        => (record.SavedUtc is { } saved ? saved.UtcDateTime.ToString("yyyy-MM-dd HH:mm", Culture) + " UTC" : record.RecordId) +
           $" / {record.TotalSamples} {(record.TotalSamples == 1 ? "sample" : "samples")}";

    private static string RecordConditions(ManualFocusSessionRecordSummary record)
    {
        var fidelity = record.QualifiesPhysicalFocus ? "physical camera" : $"{record.FidelityKind ?? "unknown"} preview";
        return record.BestSettings is { } settings
            ? $"{SettingsText(settings, record.BestSimulatedFocusPosition)}, {fidelity}"
            : fidelity;
    }

    internal static string StatusText(FocusStarStatus status) => status switch
    {
        FocusStarStatus.Valid => "Measured",
        FocusStarStatus.NoStar => "No star",
        FocusStarStatus.Saturated => "Saturated",
        FocusStarStatus.ApertureTruncated => "Truncated",
        FocusStarStatus.NotContained => "Too broad",
        FocusStarStatus.BackgroundUnavailable => "No background",
        _ => "Unmeasured"
    };

    private static string StatusTone(FocusStarStatus status) => status switch
    {
        FocusStarStatus.Valid => "success",
        FocusStarStatus.Saturated => "danger",
        _ => "warning"
    };

    private static string SampleDetail(ManualFocusSample sample)
    {
        var measurement = sample.Measurement;
        return measurement.Status switch
        {
            FocusStarStatus.Valid =>
                $"Sample #{sample.Sequence}: HFD {Value(measurement.HalfFluxDiameterPixels)} px, FWHM {Value(measurement.FwhmPixels)} px, SNR {(measurement.SignalToNoiseUnbounded ? "unbounded (zero measured noise)" : measurement.SignalToNoise?.ToString("F0", Culture) ?? "—")}.",
            FocusStarStatus.NoStar when measurement.ReasonCode == FocusStarReasonCodes.CrowdedAutomaticTarget =>
                $"Sample #{sample.Sequence}: automatic selection found only crowded stars. Pick a more isolated field or select a target explicitly; no width was measured.",
            FocusStarStatus.NoStar =>
                $"Sample #{sample.Sequence}: no star is bright enough in the target region. Increase exposure or gain, or pick another star.",
            FocusStarStatus.Saturated when measurement.Centroid is null =>
                $"Sample #{sample.Sequence}: the sky itself is clipped, so no star can be found. Reduce exposure or gain.",
            FocusStarStatus.Saturated =>
                $"Sample #{sample.Sequence}: {measurement.SaturatedSampleCount} pixels are clipped, so the width is not measured. Reduce exposure or gain.",
            FocusStarStatus.ApertureTruncated when measurement.ReasonCode == FocusStarReasonCodes.ApertureMasked =>
                $"Sample #{sample.Sequence}: the aperture reaches masked pixels outside the image circle. Pick a star farther from the edge.",
            FocusStarStatus.ApertureTruncated =>
                $"Sample #{sample.Sequence}: the aperture leaves the frame. Pick a star farther from the edge.",
            FocusStarStatus.NotContained =>
                $"Sample #{sample.Sequence}: the star is too broad to bound. Move toward sharper focus.",
            FocusStarStatus.BackgroundUnavailable when measurement.Centroid is null =>
                $"Sample #{sample.Sequence}: too little unmasked sky in the search region to find a star. Pick a region inside the image circle.",
            FocusStarStatus.BackgroundUnavailable =>
                $"Sample #{sample.Sequence}: too little clean sky surrounds the star for a background estimate. Pick a more isolated star.",
            _ => $"Sample #{sample.Sequence} was not measured."
        };
    }

    internal static string EndText(string? reason) => reason switch
    {
        ManualFocusReasonCodes.StoppedByOperator => "Ended by the operator.",
        ManualFocusReasonCodes.SafetyTimeout => "The safety limit ended the session.",
        ManualFocusReasonCodes.ObserverLost => "The owner stopped observing (page closed, connection lost, or access removed), so the session ended.",
        ManualFocusReasonCodes.HostStopping => "CameraAgent stopped while the session was running.",
        ManualFocusReasonCodes.CameraWithdrawn => "The camera module was withdrawn by its owner.",
        ManualFocusReasonCodes.AdmissionUnavailable => "Capture admission became unavailable.",
        ManualFocusReasonCodes.RepeatedFailures => "Repeated preview acquisitions failed.",
        ManualFocusReasonCodes.SampleDeadlineExceeded => "A preview missed its deadline.",
        ManualFocusReasonCodes.PreviewUnmeasurable => "The preview format cannot be measured.",
        ManualFocusReasonCodes.LoopFailed => "The focus loop failed.",
        _ => "The session ended."
    };

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is null || Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _timer?.Dispose();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        _lifetime.Dispose();
        _refreshGate.Dispose();
        _reference?.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JSException or JSDisconnectedException or OperationCanceledException) { }
        }
    }

    private sealed record TrendPoint(double X, double Y, string Kind);
}
