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
    private IReadOnlyList<CameraAgentTimeLapseLibraryEntry> _products = [];
    private string? _message;
    private DateOnly? _searchedThrough;
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
        _visible = notice is null && query is not null &&
            (string.IsNullOrEmpty(Type) || string.Equals(Type, "time-lapse", StringComparison.Ordinal));
        if (!_visible || query is null) return;
        _loading = true;
        var videoQuery = query with { DateCount = 31, Before = query.ObservingDate is null ? Before : null };
        var result = await Products.ListTimeLapseLibraryAsync(videoQuery, _lifetime.Token);
        if (generation != _generation || _lifetime.IsCancellationRequested) return;
        _products = result.Value?.Entries ?? [];
        _olderBefore = result.Value?.NextBefore;
        _searchedThrough = result.Value?.SearchedThrough;
        _message = result.Message;
        _loading = false;
    }

    private static string Progress(CameraAgentTimeLapseLibraryEntry entry) => entry.State == NightlyProductLibraryState.Stalled
        ? $"Generation disabled; recorded job state: {entry.JobState}"
        : entry.JobState switch
        {
            CameraAgentTimeLapseState.Queued => entry.ReasonCode is "timelapse.recovering" or "timelapse.explicit-retry" ? "Retrying (queued)" : "Queued",
            CameraAgentTimeLapseState.Working => "Running",
            CameraAgentTimeLapseState.Failed => "Generation failed",
            CameraAgentTimeLapseState.Unavailable => "Generation unavailable",
            CameraAgentTimeLapseState.NoSources => "No admitted images",
            _ => entry.State.ToString()
        };

    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); GC.SuppressFinalize(this); }
}
