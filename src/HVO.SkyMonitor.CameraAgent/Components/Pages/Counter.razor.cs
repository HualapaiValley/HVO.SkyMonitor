using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public partial class Counter : ComponentBase
{
    private int currentCount;

    private void IncrementCount()
    {
        currentCount++;
    }
}
