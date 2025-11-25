using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages;

[Authorize]
public sealed partial class SecureArea : ComponentBase
{
    private ClaimsPrincipal? _user;

    [CascadingParameter]
    private Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;

    private IReadOnlyList<Claim> Claims { get; set; } = Array.Empty<Claim>();

    private bool IsProfileLoaded { get; set; }

    private string DisplayName =>
        _user?.FindFirst("name")?.Value ??
        _user?.Identity?.Name ??
        _user?.FindFirst(ClaimTypes.Email)?.Value ??
        "Unknown";

    private string Email =>
        _user?.FindFirst(ClaimTypes.Email)?.Value ??
        _user?.FindFirst("preferred_username")?.Value ??
        _user?.Identity?.Name ??
        "Unavailable";

    private string AuthenticationType =>
        !string.IsNullOrWhiteSpace(_user?.Identity?.AuthenticationType)
            ? _user!.Identity!.AuthenticationType!
            : "Unspecified";

    private string IdentityProvider =>
        _user?.FindFirst("idp")?.Value ??
        _user?.FindFirst("iss")?.Value ??
        "Local";

    protected override async Task OnInitializedAsync()
    {
        var state = await AuthenticationStateTask.ConfigureAwait(false);
        _user = state.User;

        if (_user?.Identity?.IsAuthenticated == true)
        {
            Claims = _user.Claims
                .OrderBy(c => c.Type, StringComparer.Ordinal)
                .ToArray();
        }
        else
        {
            Claims = Array.Empty<Claim>();
        }

        IsProfileLoaded = true;
    }
}
