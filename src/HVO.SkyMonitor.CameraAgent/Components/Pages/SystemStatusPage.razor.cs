using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class SystemStatusPage : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CameraAgentSystemStatus? _status;
    private string? _errorMessage;
    private bool _isLoading = true;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _isLoading = true;
        _errorMessage = null;
        try
        {
            var result = await OperatorService.GetSystemStatusAsync(_lifetime.Token);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _status = null;
                _errorMessage = null;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _status = result.Value;
            }
            else
            {
                _status = null;
                _errorMessage = result.Message ?? "The startup configuration snapshot is unavailable.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _isLoading = false;
        }
    }

    internal Task RefreshAuthorizationAsync() => LoadAsync();

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatOptionalNumber(double? value, string unit) => value is null
        ? "Unspecified"
        : FormattableString.Invariant($"{value.Value:0.###} {unit}");

    private static string FormatRange(double? minimum, double? maximum, string? unit) =>
        minimum is null || maximum is null
            ? "No adaptive envelope"
            : FormattableString.Invariant($"{minimum.Value:0.###}-{maximum.Value:0.###}{(unit is null ? string.Empty : $" {unit}")}");

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string SafeAlias(string value) =>
        value.Length is > 0 and <= 64 && value.All(static character => char.IsLetterOrDigit(character) || character is '-' or '_')
            ? value
            : "Unavailable";

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
