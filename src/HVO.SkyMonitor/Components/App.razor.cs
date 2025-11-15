using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using HVO.SkyMonitor.Components.Shared;

namespace HVO.SkyMonitor.Components;

public partial class App : ComponentBase
{
    private AppErrorBoundary? _appErrorBoundary;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    private IComponentRenderMode? PageRenderMode =>
        HttpContext.AcceptsInteractiveRouting() ? RenderMode.InteractiveServer : null;

    private Task RecoverFromError()
    {
        _appErrorBoundary?.Recover();
        return Task.CompletedTask;
    }
}
