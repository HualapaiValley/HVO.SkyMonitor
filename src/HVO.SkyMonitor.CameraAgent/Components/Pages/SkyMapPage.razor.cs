using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class SkyMapPage : ComponentBase, IAsyncDisposable
{
    internal const string EditTriggerId = "sky-map-edit-coordinates";

    private CameraAgentSkyMapProjectionResult? _state;
    private ManualDeploymentLocationState? _manual;
    private DateTime? _instantInput;
    private string? _message;
    private bool _invalid;
    private bool _loading = true;
    private string _latitudeInput = string.Empty;
    private string _longitudeInput = string.Empty;
    private string _elevationInput = string.Empty;
    private string _timeZoneInput = string.Empty;
    private string _reasonInput = string.Empty;
    private bool _formDirty;
    private bool _confirming;
    private bool _busy;
    private string? _manualMessage;
    private bool _manualMessageIsError;
    private string? _manualKey;
    private string? _manualPayload;
    private long _manualExpectedVersion;
    private long _manualExpectedSequence;
    private IJSObjectReference? _module;
    private ElementReference _confirmationDialog;
    private bool _focusConfirmation;
    private bool _restoreEditFocus;

    [Inject] internal ICameraAgentSkyMapUiService SkyMapService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    protected override async Task OnInitializedAsync() => await LoadAsync().ConfigureAwait(false);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusConfirmation)
        {
            _focusConfirmation = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/SkyMapPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _confirmationDialog).ConfigureAwait(false);
        }
        else if (_restoreEditFocus)
        {
            _restoreEditFocus = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/SkyMapPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", EditTriggerId, "sky-observer").ConfigureAwait(false);
        }
    }

    private async Task ApplyInstantAsync() => await LoadAsync().ConfigureAwait(false);

    private async Task UseNowAsync()
    {
        _instantInput = null;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _invalid = false;
        try
        {
            var atUtc = _instantInput is { } local
                ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc))
                : (DateTimeOffset?)null;
            var result = await SkyMapService.GetSkyMapAsync(atUtc, CancellationToken.None).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                _state = result.Value;
                _message = null;
            }
            else
            {
                _state = null;
                _invalid = result.Kind == OperatorUiResultKind.Invalid;
                _message = result.Message ?? "The sky map projection could not be read.";
            }
            var manual = await SkyMapService.GetManualLocationAsync(CancellationToken.None).ConfigureAwait(false);
            if (manual.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            _manual = manual.IsSuccess ? manual.Value : null;
            SeedForm();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Fills the entry fields from durable state while the operator has not edited them.</summary>
    private void SeedForm()
    {
        if (_formDirty || _confirming)
        {
            return;
        }
        if (_manual?.Override is { } pending)
        {
            _latitudeInput = Number(pending.LatitudeDegrees, 6);
            _longitudeInput = Number(pending.LongitudeDegrees, 6);
            _elevationInput = Number(pending.ElevationMeters, 2);
            _timeZoneInput = pending.TimeZoneId;
            return;
        }
        if (_state?.Observer is { } observer)
        {
            _latitudeInput = Number(observer.LatitudeDegrees, 6);
            _longitudeInput = Number(observer.LongitudeDegrees, 6);
            _elevationInput = Number(observer.ElevationMeters, 2);
            _timeZoneInput = observer.TimeZoneId;
        }
    }

    private void FormChanged()
    {
        _formDirty = true;
        _manualMessage = null;
    }

    private void BeginEdit()
    {
        if (_manual is not { Supported: true } || !TryParseForm(out _, out _, out _, out _))
        {
            return;
        }
        _confirming = true;
        _focusConfirmation = true;
    }

    private void CancelEdit()
    {
        if (_busy)
        {
            return;
        }
        _confirming = false;
        _restoreEditFocus = true;
    }

    private async Task ConfirmEditAsync()
    {
        if (_manual is not { Supported: true } manual ||
            !TryParseForm(out var latitude, out var longitude, out var elevation, out var timeZoneId))
        {
            return;
        }
        var reason = string.IsNullOrWhiteSpace(_reasonInput) ? null : _reasonInput.Trim();
        var signature = string.Join(
            '|',
            latitude.ToString("R", CultureInfo.InvariantCulture),
            longitude.ToString("R", CultureInfo.InvariantCulture),
            elevation.ToString("R", CultureInfo.InvariantCulture),
            timeZoneId,
            reason ?? string.Empty);
        // A retry after an unavailable command must reuse the same key and expected version so the
        // store recognizes the replay instead of appending a second version.
        if (!string.Equals(_manualPayload, signature, StringComparison.Ordinal))
        {
            _manualPayload = signature;
            _manualKey = NewKey();
            _manualExpectedVersion = manual.KnownVersion;
            _manualExpectedSequence = manual.ManualSequence;
        }
        _busy = true;
        OperatorUiResult<ManualDeploymentLocationResult> result;
        try
        {
            result = await SkyMapService.ApplyManualLocationAsync(
                latitude,
                longitude,
                elevation,
                timeZoneId,
                _manualExpectedVersion,
                _manualExpectedSequence,
                _manualKey!,
                reason,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _manualKey = null;
            _manualPayload = null;
            _confirming = false;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (result.IsSuccess && result.Value is { } applied)
        {
            _formDirty = false;
            _reasonInput = string.Empty;
            await LoadAsync().ConfigureAwait(false);
            SetManualMessage(Describe(applied), error: false);
        }
        else
        {
            if (result.Kind == OperatorUiResultKind.Conflict)
            {
                // Re-read durable state so the next attempt carries the current expected version.
                await LoadAsync().ConfigureAwait(false);
            }
            SetManualMessage(result.Message ?? "The coordinate change failed.", error: true);
        }
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _restoreEditFocus = true;
        }
    }

    private static string Describe(ManualDeploymentLocationResult result) => result.Status switch
    {
        ManualDeploymentLocationStatus.Applied =>
            $"Recorded the coordinate entry. The next CameraAgent start appends it as deployment version "
            + $"{result.State.NextVersion}; captures already recorded keep version {result.State.ActiveVersion}."
            + (result.State.CentralAcknowledgementRequired
                ? " Central integration is enabled, so the new version is proposed for acknowledgement first "
                  + "and activates only after LogicHost acknowledges it."
                : string.Empty),
        ManualDeploymentLocationStatus.Replayed =>
            "This coordinate change was already recorded. No additional deployment version was created.",
        _ => "These coordinates already govern this deployment, so no new version was created."
    };

    private bool TryParseForm(
        out double latitude,
        out double longitude,
        out double elevation,
        out string timeZoneId)
    {
        longitude = 0;
        elevation = 0;
        timeZoneId = _timeZoneInput?.Trim() ?? string.Empty;
        if (!TryParseNumber(_latitudeInput, -90, 90, out latitude))
        {
            SetManualMessage("Latitude must be a number between -90 and 90 degrees.", error: true);
            return false;
        }
        if (!TryParseNumber(_longitudeInput, -180, 180, out longitude))
        {
            SetManualMessage("Longitude must be a number between -180 and 180 degrees, east positive.", error: true);
            return false;
        }
        if (!TryParseNumber(
                _elevationInput,
                ManualDeploymentLocationContract.MinimumElevationMeters,
                ManualDeploymentLocationContract.MaximumElevationMeters,
                out elevation))
        {
            SetManualMessage(
                $"Elevation must be a number between {ManualDeploymentLocationContract.MinimumElevationMeters:F0} "
                + $"and {ManualDeploymentLocationContract.MaximumElevationMeters:F0} metres.",
                error: true);
            return false;
        }
        if (timeZoneId.Length == 0)
        {
            SetManualMessage(
                "Time zone must be an IANA identifier this host can resolve, such as America/Phoenix or UTC.",
                error: true);
            return false;
        }
        return true;
    }

    /// <summary>Parses within the same bounds the message advertises, so the guidance is truthful.</summary>
    private static bool TryParseNumber(string value, double minimum, double maximum, out double parsed)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
           && double.IsFinite(parsed)
           && parsed >= minimum
           && parsed <= maximum;

    private void SetManualMessage(string message, bool error)
    {
        _manualMessage = message;
        _manualMessageIsError = error;
    }

    /// <summary>
    /// Describes the pending manual entry from the version it will actually occupy: once central
    /// integration has turned it into a candidate the pending version already exists, and it activates
    /// on acknowledgement rather than simply at the next start.
    /// </summary>
    private string PendingManualSummary()
    {
        if (_manual?.Override is not { PendingRestart: true } pending)
        {
            return "None";
        }
        var version = _manual.PendingVersion ?? _manual.NextVersion;
        var recorded = pending.RecordedAtUtc.ToString("u", CultureInfo.InvariantCulture);
        return _manual.PendingVersion is not null && _manual.CandidateAwaitingAcknowledgement
            ? $"Version {version} entered {recorded} awaits central acknowledgement, then a restart"
            : $"Version {version} entered {recorded} activates at the next start";
    }

    private static string NewKey() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static string Number(double value, int decimals)
        => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Split(string value) => OperationsPage.SplitWords(value);

    // The dial is a zenith-centred equidistant plot: the outer ring is the
    // geometric horizon and the centre is the zenith, so the plotted radius is
    // proportional to zenith distance and independent of the rig's own optics.
    private static double DialRadiusFromAltitude(double altitudeDegrees)
        => 92d * Math.Clamp(90d - altitudeDegrees, 0d, 90d) / 90d;

    private static double DialX(double altitudeDegrees, double azimuthDegrees)
        => 100d + DialRadiusFromAltitude(altitudeDegrees) * Math.Sin(azimuthDegrees * Math.PI / 180d);

    private static double DialY(double altitudeDegrees, double azimuthDegrees)
        => 100d - DialRadiusFromAltitude(altitudeDegrees) * Math.Cos(azimuthDegrees * Math.PI / 180d);

    private static double DialRadius(double magnitude)
        => Math.Clamp(2.6d - 0.3d * magnitude, 0.5d, 3.2d);

    public async ValueTask DisposeAsync()
    {
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
