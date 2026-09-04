using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Shared;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProductsPage : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentProductPage? _page;
    private string? _errorMessage;
    private PageStateNotice.PageStateKind _errorKind = PageStateNotice.PageStateKind.Error;
    private bool _isLoading = true;
    private long _generation;
    private string _draftRole = string.Empty;
    private string _draftKind = string.Empty;
    private string _draftAvailability = string.Empty;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Parameter, SupplyParameterFromQuery(Name = "role")] public string? Role { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "kind")] public string? Kind { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "availability")] public string? Availability { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "recipe")] public string? Recipe { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "cursor")] public string? Cursor { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "pageSize")] public int? PageSize { get; set; }

    protected override Task OnParametersSetAsync()
    {
        _draftRole = Role ?? string.Empty;
        _draftKind = Kind ?? string.Empty;
        _draftAvailability = Availability ?? string.Empty;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        var cancellation = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _loadCancellation, cancellation);
        if (prior is not null)
        {
            await prior.CancelAsync();
            prior.Dispose();
        }
        _isLoading = true;
        _errorMessage = null;
        _errorKind = PageStateNotice.PageStateKind.Error;
        try
        {
            FrameArtifactRole? role = null;
            if (!string.IsNullOrWhiteSpace(Role))
            {
                if (!Enum.TryParse<FrameArtifactRole>(Role, true, out var parsedRole))
                {
                    _page = null;
                    _errorMessage = "The role filter is not a retained artifact role.";
                    return;
                }
                role = parsedRole;
            }
            var result = await OperatorService.GetProductPageAsync(
                new CameraAgentProductQuery(PageSize, Cursor, role, Kind, Recipe, Availability), cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _page = null;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _page = result.Value;
            }
            else
            {
                _page = null;
                _errorKind = result.Kind == OperatorUiResultKind.Invalid ? PageStateNotice.PageStateKind.Info : PageStateNotice.PageStateKind.Error;
                _errorMessage = result.Message ?? "The product list is unavailable.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _isLoading = false;
            }
        }
    }

    private void ApplyFilters() => NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters("/archive/products",
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = EmptyToNull(_draftRole),
            ["kind"] = EmptyToNull(_draftKind),
            ["availability"] = EmptyToNull(_draftAvailability),
            ["recipe"] = Recipe,
            ["pageSize"] = PageSize,
            ["cursor"] = null
        }));

    private void ClearFilters() => NavigationManager.NavigateTo("/archive/products");
    private void ShowNewest() => NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameter("cursor", (string?)null));
    private void ShowOlder() => NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameter("cursor", _page?.NextCursor));

    private string DetailUrl(Guid artifactId)
    {
        var relative = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var returnUrl = ReturnUrlHelper.NormalizeReturnUrl($"/{relative}");
        if (!string.Equals(returnUrl, "/archive/products", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("/archive/products?", StringComparison.Ordinal))
        {
            returnUrl = "/archive/products";
        }
        return QueryHelpers.AddQueryString(FormattableString.Invariant($"/archive/products/{artifactId:D}"), "returnUrl", returnUrl);
    }

    private static string AvailabilityClass(string availability) => availability switch
    {
        "Available" => "avail avail--ok",
        "Quarantined" => "avail avail--danger",
        _ => "avail avail--warning"
    };

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _generation);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
