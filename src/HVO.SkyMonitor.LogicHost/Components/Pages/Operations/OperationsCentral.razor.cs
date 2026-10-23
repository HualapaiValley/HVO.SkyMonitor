using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public sealed partial class OperationsCentral : ComponentBase
{
    [Parameter] public Guid ObservatoryId { get; set; }
    [Parameter] public Guid CameraId { get; set; }
    [SupplyParameterFromQuery] public string? View { get; set; }
    [SupplyParameterFromQuery] public string? Target { get; set; }
    [SupplyParameterFromQuery] public string? Page { get; set; }
    [SupplyParameterFromQuery] public Guid? Execution { get; set; }
    [SupplyParameterFromQuery] public Guid? Job { get; set; }
    [SupplyParameterFromQuery] public Guid? Proposal { get; set; }
    [SupplyParameterFromQuery] public Guid? Revision { get; set; }
    [SupplyParameterFromQuery] public Guid? Event { get; set; }
    [SupplyParameterFromQuery] public Guid? Capture { get; set; }
    [SupplyParameterFromQuery] public string? NodesPage { get; set; }
    [SupplyParameterFromQuery] public string? AttemptsPage { get; set; }
    [SupplyParameterFromQuery] public string? InputsPage { get; set; }
    [SupplyParameterFromQuery] public string? OutputsPage { get; set; }
    [SupplyParameterFromQuery] public string? FactsPage { get; set; }
    [SupplyParameterFromQuery] public string? Request { get; set; }
    [SupplyParameterFromQuery] public string? Focus { get; set; }
    private CentralOperationsQuery Selection => new()
    {
        View = View ?? "executions",
        Target = Target ?? "Central",
        Page = Page,
        Execution = Execution,
        Job = Job,
        Proposal = Proposal,
        Revision = Revision,
        Event = Event,
        Capture = Capture,
        NodesPage = NodesPage,
        AttemptsPage = AttemptsPage,
        InputsPage = InputsPage,
        OutputsPage = OutputsPage,
        FactsPage = FactsPage,
        Request = Request,
        Focus = Focus,
    };
}
