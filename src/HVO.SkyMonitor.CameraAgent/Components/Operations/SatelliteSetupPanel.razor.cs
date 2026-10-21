using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class SatelliteSetupPanel : ComponentBase
{
    [Inject] internal IServiceProvider Services { get; set; } = default!;
    [Parameter] public DateTimeOffset? AtUtc { get; set; }
    private SatelliteSetupView? _view;
    private string? _message;
    private bool _busy;
    private DateTimeOffset? _loadedUtc;
    private ICameraAgentSatelliteSetupUiService? Service => Services.GetService<ICameraAgentSatelliteSetupUiService>();

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedUtc != AtUtc || _view is null) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (Service is not { } service || AtUtc is not { } start) return;
        _busy = true;
        try
        {
            var result = await service.GetAsync(start, start.AddHours(6), CancellationToken.None);
            _view = result.IsSuccess ? result.Value : null;
            _loadedUtc = start;
            if (!result.IsSuccess) _message = result.Message;
        }
        finally { _busy = false; }
    }

    private async Task SaveAsync(CameraAgentSettingsGroupState group, string value)
    {
        if (_busy || Service is not { } service) return;
        _busy = true;
        try
        {
            var result = await service.SetAsync(group.Group, group.Version, value, CancellationToken.None);
            _message = result.Value?.Outcome switch
            {
                CameraAgentSettingsWriteOutcome.Applied => "Saved; in effect now.",
                CameraAgentSettingsWriteOutcome.PendingRestart => "Saved; applies after a CameraAgent restart. Restart, then continue setup in Software & Catalog.",
                CameraAgentSettingsWriteOutcome.Unchanged => "Already saved.",
                _ => result.Value?.Message ?? result.Message ?? "The switch could not be saved."
            };
            await LoadAsync();
        }
        finally { _busy = false; }
    }
}
