using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class TransientPage : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentTransientOperatorPage? _page;
    private string? _errorMessage;
    private bool _isLoading;
    private long _generation;

    [Inject] internal ICameraAgentTransientUiService TransientService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Parameter, SupplyParameterFromQuery(Name = "cursor")] public string? Cursor { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "pageSize")] public int? PageSize { get; set; }

    protected override Task OnParametersSetAsync() => LoadAsync();

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
        try
        {
            var result = await TransientService.GetPageAsync(
                new CameraAgentTransientOperatorQuery(PageSize, Cursor), cancellation.Token);
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
                _errorMessage = result.Message ?? "Transient evidence is unavailable.";
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

    private void ShowNewest() => NavigateToCursor(null);
    private void ShowOlder() => NavigateToCursor(_page?.NextCursor);
    private void NavigateToCursor(string? cursor) => NavigationManager.NavigateTo(
        NavigationManager.GetUriWithQueryParameter("cursor", cursor));

    private string DetailUrl(Guid candidateId)
    {
        var relative = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var returnUrl = ReturnUrlHelper.NormalizeReturnUrl($"/{relative}");
        if (!string.Equals(returnUrl, "/transients", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("/transients?", StringComparison.Ordinal))
        {
            returnUrl = "/transients";
        }
        return QueryHelpers.AddQueryString(
            FormattableString.Invariant($"/transients/{candidateId:D}"), "returnUrl", returnUrl);
    }

    private static string StageClass(string state) => state switch
    {
        "Available" => "stage stage--available",
        "Pending" => "stage stage--pending",
        _ => "stage stage--absent"
    };

    internal static string FormatTime(DateTimeOffset value)
        => value.ToLocalTime().ToString("MMM d, yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string SplitWords(string value)
        => OperationsPage.SplitWords(value.Replace('_', ' '));

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
