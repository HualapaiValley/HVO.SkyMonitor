using System;
using HVO.SkyMonitor.LogicHost.Components.Account;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace HVO.SkyMonitor.LogicHost.Components.Account.IdentityShared;

public sealed partial class StatusMessage : ComponentBase
{
    private string? messageFromCookie;

    [Parameter]
    public string? Message { get; set; }

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    private string? DisplayMessage => Message ?? messageFromCookie;

    private string StatusMessageClass => DisplayMessage?.StartsWith("Error", StringComparison.OrdinalIgnoreCase) == true
        ? "danger"
        : "success";

    protected override void OnInitialized()
    {
        messageFromCookie = HttpContext.Request.Cookies[IdentityRedirectManager.StatusCookieName];

        if (messageFromCookie is not null)
        {
            HttpContext.Response.Cookies.Delete(IdentityRedirectManager.StatusCookieName);
        }
    }
}
