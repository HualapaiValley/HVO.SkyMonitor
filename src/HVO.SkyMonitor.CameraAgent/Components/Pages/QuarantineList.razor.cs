using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class QuarantineList : ComponentBase
{
    [Parameter, EditorRequired] public string Title { get; set; } = string.Empty;

    [Parameter, EditorRequired] public IReadOnlyList<OperatorOutboxItem> Items { get; set; } = [];

    [Parameter, EditorRequired] public EventCallback<OperatorOutboxActionRequest> BeginAction { get; set; }

    private Task Start(OperatorOutboxItem item, OutboxOperationAction action, string triggerId) =>
        BeginAction.InvokeAsync(new OperatorOutboxActionRequest(item, action, triggerId));
}
