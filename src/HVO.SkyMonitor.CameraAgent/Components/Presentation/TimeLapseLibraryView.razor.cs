using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

public sealed partial class TimeLapseLibraryView : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<CameraAgentTimeLapseSummary> _products = [];
    private string? _message;
    private bool _loading;
    private bool _visible;
    private DateOnly? _olderBefore;
    private long _generation;
    [Inject] internal ICameraAgentNightlyProductUiService Products { get; set; } = default!;
    [Parameter] public DateOnly? Before { get; set; }
    [Parameter] public string? Type { get; set; }
    [Parameter] public string? Status { get; set; }
    [Parameter] public string? Day { get; set; }
    [Parameter] public string? StillBefore { get; set; }
    private string OlderUrl => QueryHelpers.AddQueryString(GeneratedProductsView.LibraryPath,
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["type"] = Type,
            ["status"] = Status,
            ["day"] = Day,
            ["before"] = StillBefore,
            ["videoBefore"] = _olderBefore?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
        });

    protected override async Task OnParametersSetAsync()
    {
        var generation = ++_generation;
        var (query, notice) = GeneratedProductsView.Resolve(Type, Status, Day, StillBefore);
        _products = [];
        _message = null;
        _olderBefore = null;
        // Only published daily products are represented here. Partial hourly periods and generation jobs stay on
        // the observing-day page; a final with capture gaps is still a produced daily video.
        _visible = notice is null && query is not null &&
            (string.IsNullOrEmpty(Type) || string.Equals(Type, "time-lapse", StringComparison.Ordinal));
        if (!_visible || query is null) return;
        if (query.State is NightlyProductLibraryState.Partial or NightlyProductLibraryState.NotProduced)
        {
            _loading = false;
            return;
        }
        _loading = true;
        var before = query.ObservingDate is { } day ? day < DateOnly.MaxValue ? day.AddDays(1) : (DateOnly?)null : Before;
        var result = await Products.ListTimeLapsesAsync(before, _lifetime.Token);
        if (generation != _generation || _lifetime.IsCancellationRequested) return;
        var products = result.Value ?? [];
        _products = query.ObservingDate is { } date ? products.Where(product => product.ReportDate == date).ToArray() : products;
        if (query.ObservingDate is null && products.Count == 31) _olderBefore = products[^1].ReportDate;
        _message = result.Message;
        _loading = false;
    }

    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); GC.SuppressFinalize(this); }
}
