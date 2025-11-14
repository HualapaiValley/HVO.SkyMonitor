using System;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.Components.Account.Shared;

public partial class ShowRecoveryCodes : ComponentBase
{
    [Parameter]
    public string[] RecoveryCodes { get; set; } = Array.Empty<string>();

    [Parameter]
    public string? StatusMessage { get; set; }
}
