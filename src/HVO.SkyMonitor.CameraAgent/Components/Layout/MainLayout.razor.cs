using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

public sealed partial class MainLayout : LayoutComponentBase
{
    private bool _interactive;
    private ElementReference _mainContent;

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            _interactive = true;
            StateHasChanged();
        }
    }

    private async Task FocusMainContentAsync()
        => await _mainContent.FocusAsync().ConfigureAwait(false);
}
