using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class CalibrationPage : ComponentBase, IAsyncDisposable
{
    private const int PageSize = 100;
    private CalibrationUiStatus? _status;
    private CalibrationUiBundlePage? _page;
    private CalibrationUiBundleDetail? _detail;
    private CalibrationUiAcquisitionRequest? _pendingAcquisition;
    private string? _confirmation;
    private string? _pendingTarget;
    private string? _pendingKey;
    private string? _pendingReason;
    private long _pendingExpectedVersion;
    private string? _cancelKey;
    private string? _cancelJobId;
    private string? _cancelReason;
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
    private bool _focusConfirmation;
    private bool _restoreHeadingFocus;
    private bool _acquisitionRunning;
    private bool _disposed;
    private Task? _acquisitionTask;
    private ElementReference _confirmationPanel;
    private ElementReference _heading;

    [Inject] internal ICameraAgentCalibrationUiService CalibrationService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;

    private string? RollbackTarget => _status?.LastActivation?.FromBundleId is { } target &&
        !string.Equals(target, _status.ActiveBundle?.BundleId, StringComparison.Ordinal)
            ? target
            : null;

    private string ConfirmationHeading => _confirmation switch
    {
        "acquire" => "Acquire virtual references?",
        "rollback" => "Rollback active calibration?",
        _ => "Activate calibration bundle?"
    };

    private string ConfirmationDescription => _confirmation switch
    {
        "acquire" => "Twelve deterministic source frames and four masters will be published. This does not activate the new bundle.",
        "rollback" => $"Bundle {_pendingTarget} will become active after the current capture publishes durably.",
        _ => $"Bundle {_pendingTarget} will become active after the current capture publishes durably."
    };

    protected override async Task OnInitializedAsync()
    {
        _effectiveFrom = FormatLocalInput(TimeProvider.GetUtcNow());
        await RefreshAsync().ConfigureAwait(false);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusConfirmation)
        {
            _focusConfirmation = false;
            await _confirmationPanel.FocusAsync().ConfigureAwait(false);
        }
        else if (_restoreHeadingFocus)
        {
            _restoreHeadingFocus = false;
            await _heading.FocusAsync().ConfigureAwait(false);
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

    private void BeginAcquire()
    {
        if (_status is null)
        {
            return;
        }
        if (!TryCreateAcquisition(out var request, out var error))
        {
            SetMessage(error ?? "Calibration acquisition input is invalid.", error: true);
            return;
        }
        _pendingAcquisition = request;
        _pendingTarget = null;
        _pendingExpectedVersion = _status.Version;
        _pendingKey = NewKey();
        _pendingReason = request.Reason;
        _confirmation = "acquire";
        _focusConfirmation = true;
    }

    private void BeginActivation(string bundleId, bool rollback)
    {
        if (_status is null)
        {
            return;
        }
        _pendingAcquisition = null;
        _pendingTarget = bundleId;
        _pendingExpectedVersion = _status.Version;
        _pendingKey = NewKey();
        _pendingReason = string.IsNullOrWhiteSpace(_reason) ? null : _reason.Trim();
        _confirmation = rollback ? "rollback" : "activate";
        _focusConfirmation = true;
    }

    private async Task ConfirmAsync()
    {
        if (_confirmation is null || _pendingKey is null)
        {
            return;
        }
        if (_confirmation == "acquire" && _pendingAcquisition is not null)
        {
            await StartAcquisitionAsync().ConfigureAwait(false);
            return;
        }
        _busy = true;
        OperatorUiResultKind kind;
        string? failure;
        if (_pendingTarget is not null)
        {
            var result = _confirmation == "rollback"
                ? await CalibrationService.RollbackAsync(
                    _pendingTarget, _pendingExpectedVersion, _pendingKey, _pendingReason, CancellationToken.None)
                    .ConfigureAwait(false)
                : await CalibrationService.ActivateAsync(
                    _pendingTarget, _pendingExpectedVersion, _pendingKey, _pendingReason, CancellationToken.None)
                    .ConfigureAwait(false);
            kind = result.Kind;
            failure = result.Message;
        }
        else
        {
            _busy = false;
            return;
        }
        _busy = false;
        if (kind == OperatorUiResultKind.Unauthorized)
        {
            Deny();
            return;
        }
        if (kind == OperatorUiResultKind.Unavailable)
        {
            SetMessage(failure ?? "The command result is unavailable. Retry uses the same durable key.", error: true);
            return;
        }
        var succeeded = kind == OperatorUiResultKind.Success;
        ClearConfirmation();
        if (!succeeded)
        {
            if (kind == OperatorUiResultKind.Conflict)
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            SetMessage(failure ?? "The calibration command failed.", error: true);
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
        _confirmation = null;
        _restoreHeadingFocus = true;
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
                    _confirmation = "acquire";
                    _focusConfirmation = true;
                    SetMessage(
                        result.Message ?? "The acquisition result is unavailable. Retry uses the same durable key.",
                        error: true);
                    return;
                }
                var cancelled = result.Kind == OperatorUiResultKind.Conflict &&
                    result.Message?.Contains("cancelled", StringComparison.OrdinalIgnoreCase) == true;
                ClearConfirmation();
                await RefreshAsync().ConfigureAwait(false);
                SetMessage(
                    result.IsSuccess
                        ? "Calibration acquisition published durably."
                        : result.Message ?? "Calibration acquisition failed.",
                    error: !result.IsSuccess && !cancelled);
            }).ConfigureAwait(false);
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

    private async Task CancelAcquisitionAsync(CalibrationUiAcquisition acquisition)
    {
        if (_status is null)
        {
            return;
        }
        if (!string.Equals(_cancelJobId, acquisition.JobId, StringComparison.Ordinal))
        {
            _cancelJobId = acquisition.JobId;
            _cancelExpectedVersion = _status.Version;
            _cancelKey = NewKey();
            _cancelReason = string.IsNullOrWhiteSpace(_reason) ? null : _reason.Trim();
        }
        _busy = true;
        var result = await CalibrationService.CancelAsync(
            acquisition.JobId,
            _cancelExpectedVersion,
            _cancelKey!,
            _cancelReason,
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
            _cancelReason = null;
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

    private async Task ShowDetailAsync(string bundleId)
    {
        _busy = true;
        var result = await CalibrationService.GetBundleAsync(bundleId, CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        if (!Accept(result))
        {
            return;
        }
        _detail = result.Value;
    }

    private async Task LoadOlderAsync()
    {
        if (_page?.NextCursor is null)
        {
            return;
        }
        _cursor = _page.NextCursor;
        _detail = null;
        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task LoadNewestAsync()
    {
        _cursor = null;
        _detail = null;
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
        _detail = null;
        ClearConfirmation();
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    private void CancelConfirmation() => ClearConfirmation();

    private void ClearConfirmation()
    {
        _confirmation = null;
        _pendingTarget = null;
        _pendingAcquisition = null;
        _pendingKey = null;
        _pendingReason = null;
        _restoreHeadingFocus = true;
    }

    private void CloseDetail() => _detail = null;

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    private void EffectiveFromChanged(ChangeEventArgs args)
        => _effectiveFrom = Convert.ToString(args.Value, CultureInfo.InvariantCulture) ?? string.Empty;

    private void EffectiveUntilChanged(ChangeEventArgs args)
        => _effectiveUntil = Convert.ToString(args.Value, CultureInfo.InvariantCulture) ?? string.Empty;

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

    private static string FormatUtc(DateTimeOffset? value) => value?.ToString("u") ?? "No recorded result";

    private static string Split(string value)
        => string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));

    private static string FormatLayout(FrameLayoutDescriptor layout)
        => $"{layout.Width} x {layout.Height} {layout.PixelFormat}, {layout.SampleDepthBits}-in-{layout.ContainerDepthBits}, {layout.CfaPattern}";

    private static string FormatConditions(CalibrationApplicabilityV1 applicability)
        => string.Create(CultureInfo.InvariantCulture,
            $"{applicability.MinimumGain:g} / {applicability.MinimumOffset?.ToString("g", CultureInfo.InvariantCulture) ?? "unknown"} / {applicability.MinimumTemperatureC?.ToString("g", CultureInfo.InvariantCulture) ?? "unknown"} C");

    private static string FormatApplicability(CalibrationApplicabilityV1 applicability)
        => string.Create(CultureInfo.InvariantCulture,
            $"{applicability.MinimumLightExposure?.TotalSeconds.ToString("g", CultureInfo.InvariantCulture) ?? "any"} s / {applicability.EffectiveFromUtc:u} to {applicability.EffectiveUntilUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "open"}");

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
