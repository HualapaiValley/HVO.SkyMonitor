using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class SkyMapPage : ComponentBase
{
    private CameraAgentSkyMapProjectionResult? _state;
    private DateTime? _instantInput;
    private string? _message;
    private bool _invalid;
    private bool _loading = true;

    [Inject] internal ICameraAgentSkyMapUiService SkyMapService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnInitializedAsync() => await LoadAsync().ConfigureAwait(false);

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
        }
        finally
        {
            _loading = false;
        }
    }

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
}
