using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class AutomationsPage : ComponentBase
{
    private const int MaximumIntervals = 12;
    private CaptureScheduleOperatorState? _schedule;
    private EnvironmentalUiStatus? _environment;
    private string? _message;
    private bool _loading = true;

    [Inject] internal ICameraAgentScheduleUiService ScheduleService { get; set; } = default!;

    [Inject] internal ICameraAgentEnvironmentalUiService EnvironmentalService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnInitializedAsync() => await LoadAsync().ConfigureAwait(false);

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var schedule = await ScheduleService.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var environment = await EnvironmentalService.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            if (schedule.Kind == OperatorUiResultKind.Unauthorized || environment.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            _schedule = schedule.IsSuccess ? schedule.Value : null;
            _environment = environment.IsSuccess ? environment.Value : null;
            var failures = new List<string>(2);
            if (!schedule.IsSuccess)
            {
                failures.Add(schedule.Message ?? "The capture schedule could not be read.");
            }
            if (!environment.IsSuccess)
            {
                failures.Add(environment.Message ?? "The environmental sources could not be read.");
            }
            _message = failures.Count == 0 ? null : string.Join(' ', failures);
        }
        finally
        {
            _loading = false;
        }
    }

    private static string FormatUtc(DateTimeOffset? value) => value?.ToString("u") ?? "Not scheduled";

    private static string Split(string value) => OperationsPage.SplitWords(value);
}
