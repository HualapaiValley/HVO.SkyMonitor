using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

public sealed partial class CaptureImage : ComponentBase
{
    private string? _failedSource;
    private DateTimeOffset? _failedAtObservation;

    [Parameter] public Uri? Source { get; set; }
    [Parameter] public string Alt { get; set; } = "Sky capture";
    [Parameter] public bool Loading { get; set; }
    [Parameter] public DateTimeOffset? RetryObservedUtc { get; set; }
    [Parameter] public string EmptyTitle { get; set; } = "No sky image is available yet";
    [Parameter] public string EmptyMessage { get; set; } = "The latest safe capture facts will appear here after durable ingress.";
    [Parameter] public string FailureMessage { get; set; } = "The capture facts remain available. Try the preview again after the next refresh.";
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public RenderFragment? PersistentContent { get; set; }
    [Parameter] public bool ShowPersistentContent { get; set; }

    protected override void OnParametersSet()
    {
        if (_failedSource is not null &&
            (_failedSource != Source?.OriginalString || _failedAtObservation != RetryObservedUtc))
        {
            _failedSource = null;
            _failedAtObservation = null;
        }
    }

    private void HandleError()
    {
        _failedSource = Source?.OriginalString;
        _failedAtObservation = RetryObservedUtc;
    }
}
