using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Presentation;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class GalleryPage : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentGalleryPage? _page;
    private string? _errorMessage;
    private string? _draftFrom;
    private string? _draftTo;
    private string? _draftOrigin;
    private string? _draftRole;
    private string? _draftRecipe;
    private string? _draftStatus;
    private string? _draftRawState;
    private long? _draftMinimumSequence;
    private long? _draftMaximumSequence;
    private int _draftPageSize = 24;
    private string _draftSearch = string.Empty;
    private string _draftOutcome = "all";
    private string _draftProduct = "all";
    private bool _compact;
    private long _generation;
    private bool _isLoading;
    private IReadOnlyList<CameraAgentGalleryCapture>? _visibleCache;
    private CameraAgentGalleryPage? _visibleCachePage;
    private string? _visibleCacheSearch;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal ICameraAgentCapturePresentationProjector CapturePresentation { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Parameter, SupplyParameterFromQuery(Name = "from")] public string? From { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "to")] public string? To { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "origin")] public string? Origin { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "role")] public string? Role { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "recipe")] public string? Recipe { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "status")] public string? Status { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "rawState")] public string? RawState { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "minSequence")] public long? MinimumSequence { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "maxSequence")] public long? MaximumSequence { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "pageSize")] public int? PageSize { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "cursor")] public string? Cursor { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "q")] public string? Search { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "outcome")] public string? Outcome { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "product")] public string? Product { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "view")] public string? View { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        CopyParametersToDraft();
        await LoadAsync();
    }

    // Search filters only the loaded bounded page; it never implies a server-side text query exists.
    internal IReadOnlyList<CameraAgentGalleryCapture> VisibleItems
    {
        get
        {
            if (_page is null)
            {
                return [];
            }
            if (_visibleCache is not null && ReferenceEquals(_visibleCachePage, _page) &&
                string.Equals(_visibleCacheSearch, _draftSearch, StringComparison.Ordinal))
            {
                return _visibleCache;
            }
            _visibleCachePage = _page;
            _visibleCacheSearch = _draftSearch;
            _visibleCache = FilterPage();
            return _visibleCache;
        }
    }

    private IReadOnlyList<CameraAgentGalleryCapture> FilterPage()
    {
        if (_page is null)
        {
            return [];
        }
        var query = _draftSearch.Trim();
        if (query.Length == 0)
        {
            return _page.Items;
        }
        return _page.Items.Where(capture =>
            FormattableString.Invariant($"#{capture.CaptureSequence}").Contains(query, StringComparison.OrdinalIgnoreCase) ||
            ArchiveCardFacts.ProductLabel(capture, CardPresentation(capture)).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            capture.RawState.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private bool SearchIsActive => _draftSearch.Trim().Length > 0;

    private int ArtifactCount => VisibleItems.Sum(static capture => capture.Artifacts.Count);

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
            if (!TryBuildQuery(out var query, out var validationMessage))
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    _page = null;
                    _errorMessage = validationMessage;
                }
                return;
            }
            var result = await OperatorService.GetGalleryPageAsync(query!, cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _page = null;
                _errorMessage = null;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _page = result.Value;
            }
            else
            {
                _page = null;
                _errorMessage = result.Message ?? "The gallery is temporarily unavailable.";
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

    private bool TryBuildQuery(out CameraAgentGalleryQuery? query, out string? validationMessage)
    {
        query = null;
        validationMessage = null;
        var effectiveRole = MapProductRole(Product) ?? (TryEnum(Role, out FrameArtifactRole? role) ? role : null);
        var effectiveStatus = !string.IsNullOrWhiteSpace(Outcome) && !string.Equals(Outcome, "all", StringComparison.OrdinalIgnoreCase)
            ? Outcome
            : Status;
        if (!TryDate(From, out var from) || !TryDate(To, out var to) ||
            !TryEnum(Origin, out GalleryEvidenceOrigin? origin) ||
            !TryEnum(Role, out FrameArtifactRole? _) ||
            MinimumSequence is < 1 || MaximumSequence is < 1 ||
            MinimumSequence > MaximumSequence ||
            PageSize is < 1 or > 100 ||
            !IsSupportedOutcome(Outcome) ||
            !IsSupportedProduct(Product))
        {
            validationMessage = "One or more gallery filters are invalid.";
            return false;
        }
        query = new CameraAgentGalleryQuery(
            PageSize ?? 24,
            Cursor,
            from,
            to,
            MinimumSequence,
            MaximumSequence,
            RawState: RawState,
            EvidenceOrigin: origin,
            ProcessingRole: effectiveRole,
            Recipe: Recipe,
            ProcessingStatus: effectiveStatus);
        return true;
    }

    private static FrameArtifactRole? MapProductRole(string? product) => product switch
    {
        "Combined" => FrameArtifactRole.Combined,
        "Calibrated" => FrameArtifactRole.Calibrated,
        "Raw" => FrameArtifactRole.Raw,
        _ => null
    };

    private static bool IsSupportedProduct(string? product) =>
        string.IsNullOrWhiteSpace(product) || product is "all" or "Combined" or "Calibrated" or "Raw";

    private static bool IsSupportedOutcome(string? outcome) =>
        string.IsNullOrWhiteSpace(outcome) || outcome is "all" or "Completed" or "Skipped" or "TerminalFailure" or "Running";

    private Task ApplyFiltersAsync()
    {
        // The primary product/outcome selects and the advanced role/status selects address the same query fields.
        // Clear the advanced value when the primary one is set so no visible control is silently ignored.
        if (_draftProduct != "all")
        {
            _draftRole = null;
        }
        if (_draftOutcome != "all")
        {
            _draftStatus = null;
        }
        var values = new Dictionary<string, object?>
        {
            ["from"] = EmptyToNull(_draftFrom),
            ["to"] = EmptyToNull(_draftTo),
            ["origin"] = EmptyToNull(_draftOrigin),
            ["role"] = EmptyToNull(_draftRole),
            ["recipe"] = EmptyToNull(_draftRecipe),
            ["status"] = EmptyToNull(_draftStatus),
            ["rawState"] = EmptyToNull(_draftRawState),
            ["minSequence"] = _draftMinimumSequence,
            ["maxSequence"] = _draftMaximumSequence,
            ["pageSize"] = _draftPageSize == 24 ? null : _draftPageSize,
            ["q"] = EmptyToNull(_draftSearch),
            ["outcome"] = _draftOutcome == "all" ? null : _draftOutcome,
            ["product"] = _draftProduct == "all" ? null : _draftProduct,
            ["view"] = _compact ? "compact" : null,
            ["cursor"] = null
        };
        NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters("/gallery", values));
        return Task.CompletedTask;
    }

    private void ClearFilters() => NavigationManager.NavigateTo("/gallery");

    private void SetCompact(bool compact)
    {
        _compact = compact;
        var values = new Dictionary<string, object?> { ["view"] = compact ? "compact" : null };
        NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters(NavigationManager.Uri, values));
    }

    private void ShowNewest() => NavigateToCursor(null);

    private void ShowNext() => NavigateToCursor(_page?.NextCursor);

    private void NavigateToCursor(string? cursor)
    {
        NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameter("cursor", cursor));
    }

    private Task ReloadAsync() => LoadAsync();

    internal Task RefreshAuthorizationAsync() => LoadAsync();

    private void CopyParametersToDraft()
    {
        _draftFrom = From;
        _draftTo = To;
        _draftOrigin = Origin;
        _draftRole = Role;
        _draftRecipe = Recipe;
        _draftStatus = Status;
        _draftRawState = RawState;
        _draftMinimumSequence = MinimumSequence;
        _draftMaximumSequence = MaximumSequence;
        _draftPageSize = PageSize is >= 1 and <= 100 ? PageSize.Value : 24;
        _draftSearch = Search ?? string.Empty;
        _draftOutcome = IsSupportedOutcome(Outcome) && !string.IsNullOrWhiteSpace(Outcome) ? Outcome! : "all";
        _draftProduct = IsSupportedProduct(Product) && !string.IsNullOrWhiteSpace(Product) ? Product! : "all";
        _compact = string.Equals(View, "compact", StringComparison.OrdinalIgnoreCase);
    }

    private void SetDraftFrom(ChangeEventArgs args) => _draftFrom = Convert.ToString(args.Value, CultureInfo.InvariantCulture);
    private void SetDraftTo(ChangeEventArgs args) => _draftTo = Convert.ToString(args.Value, CultureInfo.InvariantCulture);
    private void SetDraftSearch(ChangeEventArgs args) => _draftSearch = Convert.ToString(args.Value, CultureInfo.InvariantCulture) ?? string.Empty;
    private void SetDraftOutcome(ChangeEventArgs args) => _draftOutcome = Convert.ToString(args.Value, CultureInfo.InvariantCulture) ?? "all";
    private void SetDraftProduct(ChangeEventArgs args) => _draftProduct = Convert.ToString(args.Value, CultureInfo.InvariantCulture) ?? "all";

    private CameraAgentCapturePresentation CardPresentation(CameraAgentGalleryCapture capture) =>
        CameraAgentOperatorUiService.ProjectCaptureDetailPresentation(CapturePresentation.Project(capture));

    internal static string PreviewUrl(Guid artifactId) =>
        FormattableString.Invariant($"/api/v1/operations/artifacts/{artifactId:D}/preview");

    private bool HasAdvancedFilters =>
        !string.IsNullOrWhiteSpace(Origin) ||
        !string.IsNullOrWhiteSpace(Role) ||
        !string.IsNullOrWhiteSpace(Recipe) ||
        !string.IsNullOrWhiteSpace(Status) ||
        !string.IsNullOrWhiteSpace(RawState) ||
        MinimumSequence is not null ||
        MaximumSequence is not null ||
        PageSize is not null and not 24;

    private Uri DetailUrl(Guid captureId)
    {
        var relative = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var returnUrl = ReturnUrlHelper.NormalizeReturnUrl($"/{relative}");
        if (!string.Equals(returnUrl, "/gallery", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("/gallery?", StringComparison.Ordinal))
        {
            returnUrl = "/gallery";
        }
        return new Uri(QueryHelpers.AddQueryString(
            FormattableString.Invariant($"/gallery/{captureId:D}"),
            "returnUrl",
            returnUrl), UriKind.Relative);
    }

    internal static string FormatCaptureTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("MMM d, yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool TryDate(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
        {
            return false;
        }
        parsed = result.ToUniversalTime();
        return true;
    }

    private static bool TryEnum<T>(string? value, out T? parsed) where T : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!Enum.TryParse<T>(value, ignoreCase: true, out var result) || !Enum.IsDefined(result))
        {
            return false;
        }
        parsed = result;
        return true;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
