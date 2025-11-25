using System;
using System.Collections.Generic;
using HVO.SkyMonitor.CameraAgent.Components.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Components.Account.IdentityShared;

public sealed partial class PasskeySubmit : ComponentBase
{
    private AntiforgeryTokenSet? tokens;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    [Parameter]
    [EditorRequired]
    public PasskeyOperation Operation { get; set; }

    [Parameter]
    [EditorRequired]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    public string? EmailName { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public IDictionary<string, object>? AdditionalAttributes { get; set; }

    protected override void OnInitialized()
    {
        tokens = Services.GetService<IAntiforgery>()?.GetTokens(HttpContext);
    }
}
