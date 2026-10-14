using HVO.SkyMonitor.CameraAgent.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

public sealed partial class MainLayout : LayoutComponentBase
{
    private bool _interactive;
    private ElementReference _mainContent;
    private AppErrorBoundary? _pageErrorBoundary;

    protected override void OnParametersSet() => RecoverFromError();

    private void RecoverFromError() => _pageErrorBoundary?.Recover();

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
