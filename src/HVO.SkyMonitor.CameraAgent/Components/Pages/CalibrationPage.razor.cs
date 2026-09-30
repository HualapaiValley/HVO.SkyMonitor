using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class CalibrationPage : ComponentBase, IAsyncDisposable
{
    private const int PageSize = 100;
    private const string SelectedReason = "calibration.library.selected";
    private const string StartTriggerId = "start-calibration-acquisition";
    private const string RollbackTriggerId = "review-calibration-rollback";
    private const string InspectActiveTriggerId = "inspect-calibration-active";
    private const string FocusFallbackId = "calibration-refresh";
    private CalibrationUiStatus? _status;
    private CalibrationUiBundlePage? _page;
    private CalibrationUiBundleDetail? _activeDetail;
    private CalibrationUiBundleDetail? _detail;
    private CalibrationUiAcquisitionRequest? _pendingAcquisition;
    private CalibrationDialog _dialog;
    private string? _pendingTarget;
    private string? _pendingKey;
    private string? _pendingReason;
    private long _pendingExpectedVersion;
    private bool _rollback;
    private bool _activationSent;
    private string? _activationReason;
    private string? _cancelKey;
    private string? _cancelJobId;
    private long _cancelExpectedVersion;
    private string? _cursor;
    private string? _message;
    private string? _reason;
    private string _effectiveFrom = string.Empty;
    private string _effectiveUntil = string.Empty;
    private double _gain = 82;
    private double _offset = 1;
    private double _temperatureC = -10;
    private double _biasSeconds = 0.001;
    private double _darkSeconds = 10;
    private double _flatSeconds = 2;
    private double _defectSeconds = 0.001;
    private double _lightSeconds = 5;
    private int _seed = 676;
    private bool _messageIsError;
    private bool _loading = true;
    private bool _busy;
    private bool _showDialog;
    private bool _acquisitionRunning;
    private bool _disposed;
    private Task? _acquisitionTask;
    private IJSObjectReference? _module;
    private ElementReference _dialogElement;
    private string? _dialogTriggerId;
    private string? _focusTargetId;

    private enum CalibrationDialog
    {
        None,
        Acquire,
        Activation,
        Detail
    }

    [Inject] internal ICameraAgentCalibrationUiService CalibrationService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    private string? RollbackTarget => _status?.LastActivation?.FromBundleId is { } target &&
        !string.Equals(target, _status.ActiveBundle?.BundleId, StringComparison.Ordinal)
            ? target
            : null;

    private bool AcquisitionInProgress => _acquisitionRunning || _status?.PendingAcquisition is not null;

    private IEnumerable<CalibrationUiArtifact> ActiveMasters => _activeDetail is null
        ? []
        : OrderedArtifacts(_activeDetail.Artifacts.Where(static artifact =>
            artifact.Role == CalibrationLibraryArtifactRoles.Master));

    protected override async Task OnInitializedAsync()
    {
        _effectiveFrom = FormatLocalInput(TimeProvider.GetUtcNow());
        await RefreshAsync().ConfigureAwait(false);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/CalibrationPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _dialogElement).ConfigureAwait(false);
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/CalibrationPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", target, FocusFallbackId).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync()
    {
        _busy = true;
        try
        {
            var status = await CalibrationService.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            if (!Accept(status))
            {
                return;
            }
            _status = status.Value;
            if (!await LoadActiveDetailAsync().ConfigureAwait(false))
            {
                return;
            }
            var page = await CalibrationService.GetBundlesAsync(PageSize, _cursor, CancellationToken.None)
                .ConfigureAwait(false);
            if (!Accept(page))
            {
                return;
            }
            _page = page.Value;
            _message = null;
        }
        finally
        {
            _loading = false;
            _busy = false;
        }
    }

    /// <summary>
    /// Loads the active bundle's references for the library view. Published bundles are immutable, so the detail is
    /// fetched once per active bundle; a failure leaves the summary in place and the next refresh retries it.
    /// </summary>
    private async Task<bool> LoadActiveDetailAsync()
    {
        var activeId = _status?.ActiveBundle?.BundleId;
        if (activeId is null)
        {
            _activeDetail = null;
            return true;
        }
        if (string.Equals(_activeDetail?.Summary.BundleId, activeId, StringComparison.Ordinal))
        {
            return true;
        }
        _activeDetail = null;
        var result = await CalibrationService.GetBundleAsync(activeId, CancellationToken.None).ConfigureAwait(false);
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            Deny();
            return false;
        }
        if (result.IsSuccess)
        {
            _activeDetail = result.Value;
        }
        return true;
    }

    private void OpenAcquireDialog()
    {
        if (_status is null || AcquisitionInProgress || _status.AcquisitionUnavailableReason is not null)
        {
            return;
        }
        _pendingAcquisition = null;
        _pendingKey = null;
        OpenDialog(CalibrationDialog.Acquire, StartTriggerId);
    }

    private async Task ConfirmAcquireAsync()
    {
        if (_status is null || _busy)
        {
            return;
        }
        if (_pendingAcquisition is null)
        {
            if (!TryCreateAcquisition(out var request, out var error))
            {
                SetMessage(error ?? "Calibration acquisition input is invalid.", error: true);
                return;
            }
            // The request, key and version are pinned on the first send so a retry after an unknown result replays
            // the identical durable command instead of acquiring a second set of references.
            _pendingAcquisition = request;
            _pendingExpectedVersion = _status.Version;
            _pendingKey = NewKey();
        }
        await StartAcquisitionAsync().ConfigureAwait(false);
    }

    private void BeginActivation(string bundleId, string triggerId, bool rollback)
    {
        if (_status is null || AcquisitionInProgress)
        {
            return;
        }
        _pendingTarget = bundleId;
        _pendingExpectedVersion = _status.Version;
        _pendingKey = NewKey();
        _pendingReason = null;
        _activationReason = null;
        _activationSent = false;
        _rollback = rollback;
        OpenDialog(CalibrationDialog.Activation, triggerId);
    }

    private async Task ConfirmActivationAsync()
    {
        if (_pendingTarget is null || _pendingKey is null || _busy)
        {
            return;
        }
        if (!_activationSent)
        {
            _pendingReason = string.IsNullOrWhiteSpace(_activationReason) ? null : _activationReason.Trim();
            _activationSent = true;
        }
        _busy = true;
        var result = _rollback
            ? await CalibrationService.RollbackAsync(
                _pendingTarget, _pendingExpectedVersion, _pendingKey, _pendingReason, CancellationToken.None)
                .ConfigureAwait(false)
            : await CalibrationService.ActivateAsync(
                _pendingTarget, _pendingExpectedVersion, _pendingKey, _pendingReason, CancellationToken.None)
                .ConfigureAwait(false);
        _busy = false;
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            Deny();
            return;
        }
        if (result.Kind == OperatorUiResultKind.Unavailable)
        {
            SetMessage(result.Message ?? "The command result is unavailable. Retry uses the same durable key.", error: true);
            return;
        }
        CloseDialog(restoreFocus: true);
        if (!result.IsSuccess)
        {
            if (result.Kind == OperatorUiResultKind.Conflict)
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            SetMessage(result.Message ?? "The calibration command failed.", error: true);
            return;
        }
        await RefreshAsync().ConfigureAwait(false);
        SetMessage("Calibration command completed durably.", error: false);
    }

    private async Task StartAcquisitionAsync()
    {
        var request = _pendingAcquisition!;
        var expectedVersion = _pendingExpectedVersion;
        var key = _pendingKey!;
        // Close the dialog and schedule the single focus restore before the observer starts: a synchronous result
        // must not restore focus a second time, and an unavailable result reopens the dialog instead.
        _dialog = CalibrationDialog.None;
        _showDialog = false;
        _message = null;
        _focusTargetId = StartTriggerId;
        _acquisitionRunning = true;
        _acquisitionTask = ObserveAcquisitionAsync(request, expectedVersion, key);
        while (!_acquisitionTask.IsCompleted && !_disposed)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            if (_disposed || _acquisitionTask.IsCompleted)
            {
                break;
            }
            await RefreshStatusAsync().ConfigureAwait(false);
            if (_status?.PendingAcquisition is not null)
            {
                break;
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The detached UI observer must consume failures after its circuit is disposed.")]
    private async Task ObserveAcquisitionAsync(
        CalibrationUiAcquisitionRequest request,
        long expectedVersion,
        string key)
    {
        try
        {
            var result = await CalibrationService.AcquireAsync(
                request, expectedVersion, key, CancellationToken.None).ConfigureAwait(false);
            if (_disposed)
            {
                return;
            }
            await InvokeAsync(async () =>
            {
                _acquisitionRunning = false;
                if (result.Kind == OperatorUiResultKind.Unauthorized)
                {
                    Deny();
                    return;
                }
                if (result.Kind == OperatorUiResultKind.Unavailable)
                {
                    _focusTargetId = null;
                    _dialog = CalibrationDialog.Acquire;
                    _dialogTriggerId = StartTriggerId;
                    _showDialog = true;
                    SetMessage(
                        result.Message ?? "The acquisition result is unavailable. Retry uses the same durable key.",
                        error: true);
                    return;
                }
                var cancelled = result.Kind == OperatorUiResultKind.Conflict &&
                    result.Message?.Contains("cancelled", StringComparison.OrdinalIgnoreCase) == true;
                _pendingAcquisition = null;
                _pendingKey = null;
                await RefreshAsync().ConfigureAwait(false);
                SetMessage(
                    result.IsSuccess
                        ? "Calibration acquisition published durably."
                        : result.Message ?? "Calibration acquisition failed.",
                    error: !result.IsSuccess && !cancelled);
            }).ConfigureAwait(false);
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _acquisitionRunning = false;
            if (_disposed)
            {
                return;
            }
            try
            {
                await InvokeAsync(() =>
                {
                    SetMessage("The calibration acquisition result could not be observed.", error: true);
                    StateHasChanged();
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The renderer can disappear while the detached durable command continues.
            }
        }
    }

    private async Task RefreshStatusAsync()
    {
        var result = await CalibrationService.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        if (Accept(result))
        {
            _status = result.Value;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    // A method group keeps one event handler id across the status poll's re-renders.
    private Task CancelPendingAcquisitionAsync()
        => _status?.PendingAcquisition is { } pending ? CancelAcquisitionAsync(pending) : Task.CompletedTask;

    private async Task CancelAcquisitionAsync(CalibrationUiAcquisition acquisition)
    {
        if (_status is null || _busy)
        {
            return;
        }
        if (!string.Equals(_cancelJobId, acquisition.JobId, StringComparison.Ordinal))
        {
            _cancelJobId = acquisition.JobId;
            _cancelExpectedVersion = _status.Version;
            _cancelKey = NewKey();
        }
        _busy = true;
        var result = await CalibrationService.CancelAsync(
            acquisition.JobId,
            _cancelExpectedVersion,
            _cancelKey!,
            null,
            CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            Deny();
            return;
        }
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _cancelJobId = null;
            _cancelKey = null;
        }
        if (!result.IsSuccess)
        {
            if (result.Kind == OperatorUiResultKind.Conflict)
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            SetMessage(result.Message ?? "Cancellation failed.", error: true);
            return;
        }
        await RefreshAsync().ConfigureAwait(false);
        SetMessage("Calibration acquisition cancelled.", error: false);
    }

    private async Task ShowDetailAsync(string bundleId, string triggerId)
    {
        if (_busy)
        {
            return;
        }
        CalibrationUiBundleDetail detail;
        if (string.Equals(_activeDetail?.Summary.BundleId, bundleId, StringComparison.Ordinal))
        {
            detail = _activeDetail!;
        }
        else
        {
            _busy = true;
            var result = await CalibrationService.GetBundleAsync(bundleId, CancellationToken.None).ConfigureAwait(false);
            _busy = false;
            if (!Accept(result))
            {
                return;
            }
            detail = result.Value!;
        }
        _detail = detail;
        OpenDialog(CalibrationDialog.Detail, triggerId);
    }

    private async Task LoadOlderAsync()
    {
        if (_page?.NextCursor is null)
        {
            return;
        }
        _cursor = _page.NextCursor;
        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task LoadNewestAsync()
    {
        _cursor = null;
        await RefreshAsync().ConfigureAwait(false);
    }

    private bool TryCreateAcquisition(
        out CalibrationUiAcquisitionRequest request,
        out string? error)
    {
        request = null!;
        error = null;
        var exposures = new[] { _biasSeconds, _darkSeconds, _flatSeconds, _defectSeconds, _lightSeconds };
        if (!double.IsFinite(_gain) || _gain < 0 || !double.IsFinite(_offset) ||
            !double.IsFinite(_temperatureC) || exposures.Any(static value =>
                !double.IsFinite(value) || value <= 0 || value > TimeSpan.MaxValue.TotalSeconds) ||
            _seed < 0)
        {
            error = "Gain, offset, temperature, seed, and positive finite exposures are required.";
            return false;
        }
        if (!TryParseUtc(_effectiveFrom, out var effectiveFrom) ||
            !string.IsNullOrWhiteSpace(_effectiveUntil) && !TryParseUtc(_effectiveUntil, out _))
        {
            error = "Effective UTC boundaries are invalid.";
            return false;
        }
        DateTimeOffset? effectiveUntil = null;
        if (!string.IsNullOrWhiteSpace(_effectiveUntil))
        {
            _ = TryParseUtc(_effectiveUntil, out var parsedUntil);
            effectiveUntil = parsedUntil;
            if (effectiveUntil <= effectiveFrom)
            {
                error = "Effective-until UTC must be later than effective-from UTC.";
                return false;
            }
        }
        request = new CalibrationUiAcquisitionRequest(
            _gain,
            _offset,
            _temperatureC,
            TimeSpan.FromSeconds(_biasSeconds),
            TimeSpan.FromSeconds(_darkSeconds),
            TimeSpan.FromSeconds(_flatSeconds),
            TimeSpan.FromSeconds(_defectSeconds),
            TimeSpan.FromSeconds(_lightSeconds),
            effectiveFrom,
            effectiveUntil,
            new VirtualCalibrationSourceModelV1 { Seed = _seed },
            string.IsNullOrWhiteSpace(_reason) ? null : _reason.Trim());
        return true;
    }

    private bool Accept<T>(OperatorUiResult<T> result)
    {
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            Deny();
            return false;
        }
        if (!result.IsSuccess)
        {
            SetMessage(result.Message ?? "Calibration data is unavailable.", error: true);
            return false;
        }
        return true;
    }

    private void Deny()
    {
        _status = null;
        _page = null;
        _activeDetail = null;
        CloseDialog(restoreFocus: false);
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    private void OpenDialog(CalibrationDialog dialog, string triggerId)
    {
        _message = null;
        _focusTargetId = null;
        _dialogTriggerId = triggerId;
        _dialog = dialog;
        _showDialog = true;
    }

    /// <summary>Ignored while a durable command is in flight so its result is always observed in the dialog.</summary>
    private void CancelConfirmation()
    {
        if (!_busy)
        {
            CloseDialog(restoreFocus: true);
        }
    }

    private void CloseDetail() => CloseDialog(restoreFocus: true);

    private void CloseDialog(bool restoreFocus)
    {
        if (_dialog != CalibrationDialog.None)
        {
            // Opening a dialog clears the page message, so anything shown now was written inside the dialog and
            // closes with it. Callers that report an outcome on the page set it after closing.
            _message = null;
            _focusTargetId = restoreFocus ? _dialogTriggerId : null;
        }
        _dialog = CalibrationDialog.None;
        _showDialog = false;
        _detail = null;
        _pendingTarget = null;
        _pendingAcquisition = null;
        _pendingKey = null;
        _pendingReason = null;
        _activationSent = false;
        _dialogTriggerId = null;
    }

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    private void EffectiveFromChanged(ChangeEventArgs args)
        => _effectiveFrom = Convert.ToString(args.Value, CultureInfo.InvariantCulture) ?? string.Empty;

    private void EffectiveUntilChanged(ChangeEventArgs args)
        => _effectiveUntil = Convert.ToString(args.Value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool CanActivate(CalibrationUiBundleSummary bundle)
        => bundle.PublicationState == "published";

    private bool CanActivateOther(CalibrationUiBundleSummary bundle)
        => CanActivate(bundle) && !string.Equals(bundle.BundleId, _status?.ActiveBundle?.BundleId, StringComparison.Ordinal);

    private string ActiveKindsLabel(CalibrationUiBundleSummary active)
        => _activeDetail is null
            ? $"{active.SourceCount} source frames"
            : string.Join(" / ", ActiveMasters.Select(static master => master.Kind));

    private string ReviewLabel(CalibrationUiBundleSummary? active)
    {
        if (active is null)
        {
            return "Not applicable";
        }
        if (active.Applicability.EffectiveUntilUtc is not { } until)
        {
            return "None set";
        }
        var days = (until - TimeProvider.GetUtcNow()).TotalDays;
        return days switch
        {
            <= 0 => "Expired",
            < 1 => "Today",
            _ => string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(days):0} day{(Math.Floor(days) == 1 ? "" : "s")}")
        };
    }

    private static string ReviewDetail(CalibrationUiBundleSummary? active)
        => active?.Applicability.EffectiveUntilUtc is { } until
            ? $"Validity interval ends {until.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : active is null ? "No active bundle" : "Validity is open-ended";

    private static (string Label, string Detail, string Chip) Compatibility(string? reason) => reason switch
    {
        null => ("Not checked", "No capture has requested references yet", "pending"),
        SelectedReason => ("Compatible", "The last capture matched the active bundle", "success"),
        CalibrationLibraryReasonCodes.Missing => ("No references", "No bundle is active", "warning"),
        CalibrationLibraryReasonCodes.Inactive => ("Not active", "No usable bundle is active", "warning"),
        CalibrationLibraryReasonCodes.Stale => ("Outside validity", "The capture fell outside the validity interval", "warning"),
        CalibrationLibraryReasonCodes.IncompatibleIdentity => ("Rig mismatch", "Agent, rig or sensor profile differs", "failure"),
        CalibrationLibraryReasonCodes.IncompatibleReadout => ("Readout mismatch", "Geometry, depth or CFA differs", "failure"),
        CalibrationLibraryReasonCodes.IncompatibleConditions => ("Conditions mismatch", "Gain, offset or temperature is out of range", "warning"),
        CalibrationLibraryReasonCodes.IncompatibleExposure => ("Exposure mismatch", "The light exposure is out of range", "warning"),
        CalibrationLibraryReasonCodes.IncompatibleCodeSpace => ("Code-space mismatch", "Stored sample codes differ", "failure"),
        CalibrationLibraryReasonCodes.Corrupt => ("Corrupt", "The active bundle failed verification", "failure"),
        CalibrationLibraryReasonCodes.Incomplete => ("Incomplete", "The active bundle is missing evidence", "failure"),
        CalibrationLibraryReasonCodes.Ambiguous => ("Ambiguous", "More than one reference matched", "failure"),
        _ => (reason, "Reported by the last selection", "pending")
    };

    private static string PublicationLabel(CalibrationUiBundleSummary bundle, bool isActive)
        => isActive && bundle.PublicationState == "published"
            ? "Active"
            : bundle.PublicationState switch
            {
                "published" => "Retained",
                "incomplete" => "Incomplete",
                "corrupt" => "Corrupt",
                "quarantined" => "Quarantined",
                _ => bundle.PublicationState
            };

    private static string PublicationChip(CalibrationUiBundleSummary bundle, bool isActive)
        => bundle.PublicationState switch
        {
            "published" => isActive ? "success" : "pending",
            "incomplete" => "warning",
            _ => "failure"
        };

    private static IEnumerable<CalibrationUiArtifact> OrderedArtifacts(IEnumerable<CalibrationUiArtifact> artifacts)
        => artifacts
            .OrderBy(static artifact => KindOrder(artifact.Kind))
            .ThenBy(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master ? 0 : 1)
            .ThenBy(static artifact => artifact.SourceIndex ?? -1);

    private static int KindOrder(string kind)
    {
        for (var index = 0; index < CalibrationReferenceKinds.All.Count; index++)
        {
            if (CalibrationReferenceKinds.All[index] == kind)
            {
                return index;
            }
        }
        return int.MaxValue;
    }

    private static string KindTitle(string kind)
        => kind.Length == 0 ? kind : string.Concat(char.ToUpperInvariant(kind[0]).ToString(), kind[1..]);

    private static RenderFragment KindGlyph(string kind) => builder =>
    {
        builder.OpenElement(0, "svg");
        builder.AddAttribute(1, "viewBox", "0 0 20 20");
        builder.AddAttribute(2, "aria-hidden", "true");
        switch (kind)
        {
            case CalibrationReferenceKinds.Dark:
                builder.OpenElement(3, "circle");
                builder.AddAttribute(4, "cx", "10");
                builder.AddAttribute(5, "cy", "10");
                builder.AddAttribute(6, "r", "5");
                builder.CloseElement();
                break;
            case CalibrationReferenceKinds.Flat:
                builder.OpenElement(7, "circle");
                builder.AddAttribute(8, "cx", "10");
                builder.AddAttribute(9, "cy", "10");
                builder.AddAttribute(10, "r", "7");
                builder.CloseElement();
                builder.OpenElement(11, "circle");
                builder.AddAttribute(12, "cx", "10");
                builder.AddAttribute(13, "cy", "10");
                builder.AddAttribute(14, "r", "2");
                builder.CloseElement();
                break;
            case CalibrationReferenceKinds.Defect:
                builder.OpenElement(15, "path");
                builder.AddAttribute(16, "d", "m4 4 12 12M16 4 4 16");
                builder.CloseElement();
                break;
            default:
                builder.OpenElement(17, "path");
                builder.AddAttribute(18, "d", "M4 10h12M10 4v12");
                builder.CloseElement();
                break;
        }
        builder.CloseElement();
    };

    private static string SourceLabel(string source) => source switch
    {
        CalibrationLibraryBundleSources.VirtualAcquisitionV1 => "Software-generated (VirtualSky)",
        CalibrationLibraryBundleSources.SyntheticReferencesV1 => "Synthetic references",
        _ => source
    };

    private static string Cfa(FrameLayoutDescriptor layout)
        => layout.CfaPattern == ColorFilterArrayPattern.None ? "Mono" : layout.CfaPattern.ToString().ToUpperInvariant();

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan value)
        => string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:0.000}s");

    private static string Temperature(double? value)
        => value is { } temperature
            ? string.Create(CultureInfo.InvariantCulture, $"{temperature:0.#} C")
            : "temperature not recorded";

    private static string Range(double minimum, double maximum)
        => minimum == maximum ? Number(minimum) : $"{Number(minimum)} to {Number(maximum)}";

    private static string Range(double? minimum, double? maximum) => (minimum, maximum) switch
    {
        (null, null) => "Any",
        ({ } low, { } high) => Range(low, high),
        ({ } low, null) => $"{Number(low)} or more",
        (null, { } high) => $"up to {Number(high)}"
    };

    private static string TemperatureRange(double? minimum, double? maximum)
        => minimum is null && maximum is null ? "Any" : $"{Range(minimum, maximum)} C";

    private static string SecondsRange(TimeSpan? minimum, TimeSpan? maximum) => (minimum, maximum) switch
    {
        (null, null) => "Any",
        ({ } low, { } high) when low == high => Seconds(low),
        ({ } low, { } high) => $"{Seconds(low)} to {Seconds(high)}",
        ({ } low, null) => $"{Seconds(low)} or more",
        (null, { } high) => $"up to {Seconds(high)}"
    };

    private static string Validity(CalibrationApplicabilityV1 applicability)
        => $"{When(applicability.EffectiveFromUtc)} to {(applicability.EffectiveUntilUtc is { } until ? When(until) : "open-ended")}";

    private static string When(DateTimeOffset? value)
        => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "never";

    private static bool TryParseUtc(string value, out DateTimeOffset result)
    {
        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            result = DateTimeOffset.FromUnixTimeMilliseconds(new DateTimeOffset(parsed).ToUnixTimeMilliseconds());
            return true;
        }
        result = default;
        return false;
    }

    private static string FormatLocalInput(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    private static string NewKey() => $"ui-{Guid.NewGuid():N}{Guid.NewGuid():N}";

    private static string ShortHash(string value) => value.Length <= 12 ? value : value[..12];

    private static string AcquisitionStateLabel(string state) => state switch
    {
        CalibrationAcquisitionStates.Planned => "Acquisition planned",
        CalibrationAcquisitionStates.Acquiring => "Acquiring source frames",
        CalibrationAcquisitionStates.Building => "Building masters",
        CalibrationAcquisitionStates.Publishing => "Publishing the bundle",
        _ => $"Acquisition {state}"
    };

    private static string ChangeLabel(CalibrationLibraryActivationSnapshot change) => change.CommandKind switch
    {
        "rollback" => $"Rolled back to {change.ToBundleId}",
        "activate" => $"Activated {change.ToBundleId}",
        _ => $"{change.CommandKind} {change.ToBundleId}"
    };

    private static string FormatLayout(FrameLayoutDescriptor layout)
        => $"{layout.Width} x {layout.Height} {layout.PixelFormat}, {layout.SampleDepthBits}-in-{layout.ContainerDepthBits}, {Cfa(layout)}";

    private static string ActivationTriggerId(string bundleId) => $"review-calibration-activate-{bundleId}";

    private static string InspectTriggerId(string bundleId) => $"inspect-calibration-{bundleId}";

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
