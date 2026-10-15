using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class QuarantineList : SiteTimeComponent
{
    [Parameter, EditorRequired] public string Title { get; set; } = string.Empty;

    [Parameter, EditorRequired] public IReadOnlyList<OperatorOutboxItem> Items { get; set; } = [];

    [Parameter, EditorRequired] public EventCallback<OperatorOutboxActionRequest> BeginAction { get; set; }

    private string Age(DateTimeOffset? createdUtc)
    {
        if (createdUtc is null)
        {
            return "Unavailable";
        }
        var now = (SiteServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var age = now > createdUtc.Value ? now - createdUtc.Value : TimeSpan.Zero;
        return age.TotalSeconds < 90
            ? FormattableString.Invariant($"{age.TotalSeconds:F0}s")
            : age.TotalMinutes < 90
                ? FormattableString.Invariant($"{age.TotalMinutes:F0} min")
                : age.TotalHours < 48
                    ? FormattableString.Invariant($"{age.TotalHours:F0} h")
                    : FormattableString.Invariant($"{age.TotalDays:F0} d");
    }

    private Task Start(OperatorOutboxItem item, OutboxOperationAction action, string triggerId) =>
        BeginAction.InvokeAsync(new OperatorOutboxActionRequest(item, action, triggerId));
}
