using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public sealed partial class OperationsImportedExecutions : ComponentBase
{
    [Parameter] public Guid ObservatoryId { get; set; }
    [Parameter] public Guid CameraId { get; set; }
    [SupplyParameterFromQuery] public Guid? Installation { get; set; }
    [SupplyParameterFromQuery] public Guid? Binding { get; set; }
    [SupplyParameterFromQuery] public long? Unit { get; set; }
    [SupplyParameterFromQuery] public string? Node { get; set; }
    [SupplyParameterFromQuery] public long? Artifact { get; set; }
    [SupplyParameterFromQuery] public Guid? Central { get; set; }
    [SupplyParameterFromQuery] public string? Kind { get; set; }
    [SupplyParameterFromQuery] public string? Origin { get; set; }
    [SupplyParameterFromQuery] public string? InstallationsPage { get; set; }
    [SupplyParameterFromQuery] public string? OriginsPage { get; set; }
    [SupplyParameterFromQuery] public string? UnitsPage { get; set; }
    [SupplyParameterFromQuery] public string? ArtifactsPage { get; set; }
    [SupplyParameterFromQuery] public string? NodesPage { get; set; }
    [SupplyParameterFromQuery] public string? AttemptsPage { get; set; }
    [SupplyParameterFromQuery] public string? InputsPage { get; set; }
    [SupplyParameterFromQuery] public string? OutputsPage { get; set; }
    [SupplyParameterFromQuery] public string? CentralNodesPage { get; set; }
    [SupplyParameterFromQuery] public string? Focus { get; set; }
    private ImportedExecutionQuery Selection => new()
    {
        Installation = Installation,
        Binding = Binding,
        Unit = Unit,
        Node = Node,
        Artifact = Artifact,
        Central = Central,
        Kind = Kind,
        Origin = Origin,
        InstallationsPage = InstallationsPage,
        OriginsPage = OriginsPage,
        UnitsPage = UnitsPage,
        ArtifactsPage = ArtifactsPage,
        NodesPage = NodesPage,
        AttemptsPage = AttemptsPage,
        InputsPage = InputsPage,
        OutputsPage = OutputsPage,
        CentralNodesPage = CentralNodesPage,
        Focus = Focus
    };
}
