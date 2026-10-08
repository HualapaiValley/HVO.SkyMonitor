using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

public sealed partial class TimeLapseDayProducts : ComponentBase
{
    private readonly HashSet<Guid> _queued = [];
    private bool _retrying;
    private string? _message;
    [Inject] internal ICameraAgentNightlyProductUiService Products { get; set; } = default!;
    [Parameter, EditorRequired] public CameraAgentTimeLapseDay Day { get; set; } = default!;
    [Parameter] public bool Enabled { get; set; }

    private async Task RetryAsync(CameraAgentTimeLapseJob job)
    {
        if (_retrying) return;
        _retrying = true;
        try
        {
            var result = await Products.RetryTimeLapseAsync(job.JobId, job.Revision, Guid.NewGuid(), CancellationToken.None);
            if (result.IsSuccess) _queued.Add(job.JobId);
            _message = result.IsSuccess ? "The retry is queued with its original inputs and settings." : result.Message;
        }
        finally { _retrying = false; }
    }

    internal static string StateLabel(CameraAgentTimeLapseState state) => state switch
    {
        CameraAgentTimeLapseState.Produced => "Final · published",
        CameraAgentTimeLapseState.NoSources => "No admitted images",
        CameraAgentTimeLapseState.Unavailable => "Encoder unavailable",
        CameraAgentTimeLapseState.Working => "Working",
        CameraAgentTimeLapseState.Failed => "Generation failed",
        _ => "Queued"
    };

    private static string WindowLabel(CameraAgentTimeLapseJob job)
    {
        var clock = new CameraAgentSiteTime(ObservingDayCalendar.Create(job.Window.ReportingPeriod.Site.TimeZoneId));
        return $"{clock.Format(job.Window.StartUtc)}–{clock.Format(job.Window.EndUtc)}";
    }
}
