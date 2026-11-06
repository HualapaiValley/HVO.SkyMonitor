using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

public sealed partial class ImportedArtifactIdentity : ComponentBase
{
    [Parameter, EditorRequired] public ExecutionEvidenceArtifactReferenceV1 Value { get; set; } = default!;
    [Parameter] public string? ReconciliationPath { get; set; }
}
