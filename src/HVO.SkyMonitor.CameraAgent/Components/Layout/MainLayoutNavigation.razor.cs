using System;
using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using HVO.SkyMonitor.CameraAgent.Security;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

public sealed partial class MainLayoutNavigation : ComponentBase, IDisposable
{
    private static readonly IReadOnlyList<NavigationLink> PrimaryLinks =
    [
        new NavigationLink("/", "Current sky", NavLinkMatch.All),
        new NavigationLink("/gallery", "Archive", NavLinkMatch.Prefix),
        new NavigationLink("/operations", "Operations", NavLinkMatch.Prefix)
    ];

    private static readonly string[] AdditionalOperationsRoutes =
    [
        "/schedule",
        "/calibration",
        "/system",
        "/environmental",
        "/transients",
        "/devices"
    ];

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    private IEnumerable<NavigationLink> PrimaryNavigationLinks => PrimaryLinks;

    private string CurrentReturnUrl = ReturnUrlHelper.NormalizeReturnUrl(null);

    private string SignInUrl => ReturnUrlHelper.BuildLoginPath(CurrentReturnUrl);

    private ElementReference _menuToggle;

    private bool _menuOpen;
    private string _currentPath = "/";

    protected override void OnInitialized()
    {
        UpdateReturnUrl(NavigationManager.Uri);
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdateReturnUrl(e.Location);
        _menuOpen = false;
        _ = InvokeAsync(StateHasChanged);
    }

    private void ToggleMenu() => _menuOpen = !_menuOpen;

    private async Task HandleMenuKeyDown(KeyboardEventArgs eventArgs)
    {
        if (_menuOpen && string.Equals(eventArgs.Key, "Escape", StringComparison.Ordinal))
        {
            _menuOpen = false;
            await _menuToggle.FocusAsync().ConfigureAwait(false);
        }
    }

    private void UpdateReturnUrl(string location)
    {
        var baseRelative = NavigationManager.ToBaseRelativePath(location);
        CurrentReturnUrl = ReturnUrlHelper.NormalizeReturnUrl(baseRelative);
        _currentPath = new Uri(location).AbsolutePath;
    }

    private string NavigationClass(NavigationLink link)
        => IsAdditionalOperationsRoute(link) ? "nav-badge active" : "nav-badge";

    private string? NavigationCurrent(NavigationLink link)
        => IsPrimaryRouteCurrent(link) ? "page" : null;

    private bool IsPrimaryRouteCurrent(NavigationLink link)
        => IsPathOrChild(_currentPath, link.Href) || IsAdditionalOperationsRoute(link);

    private bool IsAdditionalOperationsRoute(NavigationLink link)
        => string.Equals(link.Href, "/operations", StringComparison.Ordinal) &&
           !IsPathOrChild(_currentPath, "/operations") &&
           AdditionalOperationsRoutes.Any(path => IsPathOrChild(_currentPath, path));

    private static bool IsPathOrChild(string path, string candidate)
        => string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith($"{candidate}/", StringComparison.OrdinalIgnoreCase);

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

    private sealed record NavigationLink(string Href, string Label, NavLinkMatch Match);
}
