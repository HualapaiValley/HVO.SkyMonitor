using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class TimeLapseDetailPage : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CameraAgentTimeLapsePresentation? _view;
    private string? _message;
    private bool _loading = true;
    [Inject] internal ICameraAgentNightlyProductUiService Products { get; set; } = default!;
    [Parameter] public Guid ProductId { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        var id = ProductId;
        var result = await Products.GetTimeLapseAsync(id, _lifetime.Token);
        if (id != ProductId || _lifetime.IsCancellationRequested) return;
        _view = result.Value;
        _message = result.Message;
        _loading = false;
    }

    private static string WindowLabel(DateTimeOffset start, DateTimeOffset end) => FormattableString.Invariant($"{start:u}–{end:u}");
    private static string DurationLabel(long ticks) => FormattableString.Invariant($"{ticks / 1_000_000d:0.###} s");
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); GC.SuppressFinalize(this); }
}
