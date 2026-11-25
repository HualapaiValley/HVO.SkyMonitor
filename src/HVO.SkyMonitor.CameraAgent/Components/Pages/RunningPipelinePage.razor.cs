using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class RunningPipelinePage : ComponentBase, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _lifetime = new();
    private CenteredPipelineRunningSnapshot? _snapshot;
    private Task? _refreshLoop;
    private bool _loading;

    [Inject] internal CenteredPipelineUiService PipelineService { get; set; } = default!;
    [Inject] internal NavigationManager Navigation { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;

    protected override Task OnInitializedAsync() => RefreshAsync();

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
            _refreshLoop = RefreshPeriodicallyAsync(_lifetime.Token);
    }

    private async Task RefreshPeriodicallyAsync(CancellationToken token)
    {
        // Re-read the durable pointer even when a product wake-up was missed.
        using var timer = new PeriodicTimer(RefreshInterval, TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await InvokeAsync(async () =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    await RefreshAsync();
                    if (!token.IsCancellationRequested)
                        StateHasChanged();
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAsync()
    {
        if (_loading || _lifetime.IsCancellationRequested)
            return;

        _loading = true;
        var token = _lifetime.Token;
        try
        {
            var result = await PipelineService.ReadRunningAsync(token);
            if (token.IsCancellationRequested)
                return;

            _snapshot = result.Value;
            if (result.Kind == OperatorUiResultKind.Unauthorized)
                Navigation.NavigateTo("/Account/AccessDenied");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _loading = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_refreshLoop is not null)
            await _refreshLoop.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
