using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// Reads the composed orbital status once through the scoped, authorized orbital UI service and shows it with
/// <see cref="OrbitalDataStatusNotice"/>. It never refreshes, resumes or reads the payload. A failed, denied, canceled
/// or unregistered read shows the notice's explicit "status unavailable" wording rather than any data claim.
/// </summary>
public sealed partial class ObservatoryOrbitalDataNotice : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private OrbitalDataStatusView? _view;
    private bool _loaded;
    private bool _disposed;

    [Inject] internal IServiceProvider Services { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        if (Services.GetService<ICameraAgentOrbitalUiService>() is not { } service)
        {
            _loaded = true;
            return;
        }
        try
        {
            var result = await service.GetAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            _view = result is { IsSuccess: true, Value: { } view } ? view : null;
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
            // A read canceled by this notice's own disposal updates nothing. Any other cancellation is a read that
            // did not complete, so the notice says the status is unavailable.
            if (!_disposed)
            {
                _view = null;
                _loaded = true;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
