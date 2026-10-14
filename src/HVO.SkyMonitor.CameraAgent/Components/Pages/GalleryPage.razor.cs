using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Presentation;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class GalleryPage : SiteTimeComponent, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentGalleryPage? _page;
    private string? _errorMessage;
    private string? _draftZone;
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
    private Dictionary<Guid, CameraAgentArchiveCardLinks> _cardLinks = [];
    private Dictionary<Guid, CameraAgentProduct> _cardProducts = [];

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal ICameraAgentCapturePresentationProjector CapturePresentation { get; set; } = default!;
    [Inject] internal ICameraAgentArchiveCardUiService ArchiveCards { get; set; } = default!;
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

    private int LinkedCandidateCount => VisibleItems.SelectMany(capture =>
        _cardLinks.TryGetValue(capture.CaptureId, out var links) ? links.CandidateIds : []).Distinct().Count();

    private bool CandidateLinksAvailable => VisibleItems.All(capture =>
        _cardLinks.TryGetValue(capture.CaptureId, out var links) && links.CandidateLinksAvailable);

    private CameraAgentArchiveCardLinks? CardLinks(Guid captureId)
        => _cardLinks.TryGetValue(captureId, out var links) ? links : null;

    private CameraAgentProduct? CardProduct(Guid captureId)
        => _cardProducts.TryGetValue(captureId, out var product) ? product : null;

    private Uri? RunUrl(Guid captureId)
        => CardLinks(captureId)?.ExecutionId is { } id
            ? new Uri(FormattableString.Invariant($"/operations/pipeline/executions/{id:D}"), UriKind.Relative)
            : null;

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
                AccessDenied();
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                var links = new Dictionary<Guid, CameraAgentArchiveCardLinks>();
                var products = new Dictionary<Guid, CameraAgentProduct>();
                foreach (var capture in result.Value.Items)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    links[capture.CaptureId] = await ArchiveCards.GetLinksAsync(capture.CaptureId, cancellation.Token);
                    if (generation != Volatile.Read(ref _generation))
                    {
                        return;
                    }
                    if (links[capture.CaptureId].AuthorizationDenied)
                    {
                        AccessDenied();
                        return;
                    }
                    if (ArchiveCardFacts.DisplayArtifact(capture, CardPresentation(capture)) is { } artifact)
                    {
                        var product = await OperatorService.GetProductDetailAsync(artifact.ArtifactId, cancellation.Token);
                        if (generation != Volatile.Read(ref _generation))
                        {
                            return;
                        }
                        if (product.Kind == OperatorUiResultKind.Unauthorized)
                        {
                            AccessDenied();
                            return;
                        }
                        if (product.IsSuccess && product.Value?.Product is { } retained &&
                            retained.CaptureId == capture.CaptureId && retained.ArtifactId == artifact.ArtifactId)
                        {
                            products[capture.CaptureId] = retained;
                        }
                    }
                }
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }
                _cardLinks = links;
                _cardProducts = products;
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

    private void AccessDenied()
    {
        _page = null;
        _cardLinks = [];
        _cardProducts = [];
        _errorMessage = null;
        NavigationManager.NavigateTo("/Account/AccessDenied");
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
        if (!string.Equals(_draftZone, SiteTime.Label, StringComparison.Ordinal))
        {
            _errorMessage = "The site time zone changed while these filters were open. Reload before entering times.";
            return Task.CompletedTask;
        }
        var fromValid = TryFilterInput(_draftFrom, From, out var fromUtc, out var fromError);
        var toValid = TryFilterInput(_draftTo, To, out var toUtc, out var toError);
        if (!fromValid || !toValid)
        {
            _errorMessage = fromError ?? toError;
            return Task.CompletedTask;
        }
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
            ["from"] = fromUtc?.ToString("O", CultureInfo.InvariantCulture),
            ["to"] = toUtc?.ToString("O", CultureInfo.InvariantCulture),
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

    private bool TryFilterInput(string? entered, string? recorded, out DateTimeOffset? utc, out string? error)
    {
        // An unchanged URL instant already identifies one occurrence of a repeated local hour.
        if (TryDate(recorded, out var original) && original is { } value &&
            string.Equals(entered, SiteTime.Input(value), StringComparison.Ordinal))
        {
            utc = value;
            error = null;
            return true;
        }
        return SiteTime.TryInput(entered, out utc, out error);
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
        _draftZone = SiteTime.Label;
        _draftFrom = TryDate(From, out var from) && from is { } first ? SiteTime.Input(first) : From;
        _draftTo = TryDate(To, out var to) && to is { } last ? SiteTime.Input(last) : To;
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
