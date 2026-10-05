using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

public sealed partial class TimeLapseLibraryView : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<CameraAgentTimeLapseSummary> _products = [];
    private string? _message;
    private bool _loading;
    private long _generation;
    [Inject] internal ICameraAgentNightlyProductUiService Products { get; set; } = default!;
    [Parameter] public DateOnly? Before { get; set; }
    private string OlderUrl => FormattableString.Invariant($"/archive/products?videoBefore={_products[^1].ReportDate:yyyy-MM-dd}");

    protected override async Task OnParametersSetAsync()
    {
        var generation = ++_generation;
        _loading = true;
        var result = await Products.ListTimeLapsesAsync(Before, _lifetime.Token);
        if (generation != _generation || _lifetime.IsCancellationRequested) return;
        _products = result.Value ?? [];
        _message = result.Message;
        _loading = false;
    }

    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); GC.SuppressFinalize(this); }
}
