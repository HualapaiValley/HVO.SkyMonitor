using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class AutomationRunProducts : IDisposable
{
    [Parameter, EditorRequired] public LocalAutomationOccurrence Occurrence { get; set; } = default!;
    [Inject] internal IServiceProvider Services { get; set; } = default!;
    [Inject] internal NavigationManager Navigation { get; set; } = default!;

    private readonly CancellationTokenSource _lifetime = new();
    private bool _loading;
    private string? _message;
    private IReadOnlyList<(string Url, string Label)> _products = [];

    private async Task LoadAsync()
    {
        if (_loading || Occurrence.SourceWindow is not { } window)
        {
            return;
        }
        _loading = true;
        _message = null;
        _products = [];
        try
        {
            var service = Services.GetRequiredService<ICameraAgentNightlyProductUiService>();
            var result = await service.GetDayAsync(window.ReportingPeriod.ReportDate, _lifetime.Token);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                Navigation.NavigateTo("/Account/AccessDenied");
            }
            else if (!result.IsSuccess || result.Value is not { } view)
            {
                _message = result.Message ?? "Published products are unavailable.";
            }
            else
            {
                _products = ProductsFor(view, Occurrence);
                _message = _products.Count == 0 ? "No published product is retained for this occurrence." : null;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _loading = false;
        }
    }

    internal static IReadOnlyList<(string Url, string Label)> ProductsFor(CameraAgentNightlyDayView view, LocalAutomationOccurrence occurrence)
    {
        if (occurrence.Definition.TaskKind == LocalAutomationTaskKind.TimeLapseGeneration)
        {
            return view.TimeLapses?.Jobs
                .Where(job => job.Occurrence.IdentitySha256 == occurrence.IdentitySha256 && job.ParentJobId is null &&
                    job.State == CameraAgentTimeLapseState.Produced && job.ProductId is not null)
                .Select(job => ($"/archive/products/time-lapse/{job.ProductId!.Value:D}",
                    job.Window.Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay ? "Daily time-lapse" : "Hourly time-lapse")).ToArray()
                ?? [];
        }
        return view.Day.Windows
            .Where(record => record.Status.Occurrence.IdentitySha256 == occurrence.IdentitySha256)
            .Select(static record => record.FinalProduct).OfType<NightlyProductSummary>()
            .Select(static product => ($"/archive/products/nightly/{product.ProductId:D}", "Final still product")).ToArray();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
