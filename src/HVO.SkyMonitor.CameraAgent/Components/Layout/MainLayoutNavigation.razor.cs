using System;
using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Security;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

public sealed partial class MainLayoutNavigation : ComponentBase, IDisposable
{
    private static readonly IReadOnlyList<NavigationLink> PrimaryLinks =
    [
        new NavigationLink("/", "Dashboard", "bi bi-house", NavLinkMatch.All),
        new NavigationLink("/weather", "Weather", "bi bi-cloud-moon", NavLinkMatch.Prefix),
        new NavigationLink("/auth", "Secure Area", "bi bi-shield-lock", NavLinkMatch.Prefix)
    ];

    private static readonly IReadOnlyList<ToolbarAction> ToolbarActions =
    [
        new ToolbarAction("bi bi-bell", "Notifications"),
        new ToolbarAction("bi bi-gear", "Configuration"),
        new ToolbarAction("bi bi-question-circle", "Help"),
        new ToolbarAction("bi bi-chat-dots", "Feedback")
    ];

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ICentralIdentityNavigationService CentralIdentityNavigationService { get; set; } = default!;

    private IEnumerable<NavigationLink> PrimaryNavigationLinks => PrimaryLinks;

    private IEnumerable<ToolbarAction> AuxiliaryActions => ToolbarActions;

    private string CurrentReturnUrl = ReturnUrlHelper.NormalizeReturnUrl(null);

    private string SignInUrl => ReturnUrlHelper.BuildLoginPath(CurrentReturnUrl);

    private string AccountManagementUrl => CentralIdentityNavigationService
        .GetAccountManagementUrl()
        .ToString();

    protected override void OnInitialized()
    {
        UpdateReturnUrl(NavigationManager.Uri);
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdateReturnUrl(e.Location);
        _ = InvokeAsync(StateHasChanged);
    }

    private void UpdateReturnUrl(string location)
    {
        var baseRelative = NavigationManager.ToBaseRelativePath(location);
        CurrentReturnUrl = ReturnUrlHelper.NormalizeReturnUrl(baseRelative);
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }


    private static string GetUserDisplayName(ClaimsPrincipal principal)
    {
        return principal.FindFirstValue("name")
            ?? principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.Identity?.Name
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? "Account";
    }

    private static string GetUserEmail(ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("preferred_username")
            ?? principal.Identity?.Name
            ?? "unknown";
    }

    private static string GetUserInitials(ClaimsPrincipal principal)
    {
        var displayName = GetUserDisplayName(principal);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = GetUserEmail(principal);
        }

        var parts = displayName
            .Replace("@", " ", StringComparison.Ordinal)
            .Replace(".", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 2)
        {
            return string.Create(2, parts, static (span, source) =>
            {
                span[0] = char.ToUpperInvariant(source[0][0]);
                span[1] = char.ToUpperInvariant(source[^1][0]);
            });
        }

        if (parts.Length == 1 && parts[0].Length >= 1)
        {
            var firstChar = char.ToUpperInvariant(parts[0][0]);
            var secondChar = parts[0].Length > 1 ? char.ToUpperInvariant(parts[0][1]) : firstChar;
            return new string(new[] { firstChar, secondChar });
        }

        return "??";
    }

    private sealed record ToolbarAction(string IconClass, string Tooltip);

    private sealed record NavigationLink(string Href, string Label, string IconClass, NavLinkMatch Match);
}
