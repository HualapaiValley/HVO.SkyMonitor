using HVO.SkyMonitor.CameraAgent.Components.Operations;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// The manual focus workspace. No focus-session capability exists on this CameraAgent, so every slot the
/// prototype fills with previews, measurements and history renders empty, and every control is disabled with the
/// catalog's capability note as its accessible reason.
/// </summary>
public sealed partial class FocusPage : ComponentBase
{
    private static OperationsSection Section => OperationsSectionCatalog.Get("focus");
}
