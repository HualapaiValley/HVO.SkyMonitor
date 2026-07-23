using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
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
    private long _generation;
    private bool _isLoading;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
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

    protected override async Task OnParametersSetAsync()
    {
        CopyParametersToDraft();
        await LoadAsync();
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
        if (!TryDate(From, out var from) || !TryDate(To, out var to) ||
            !TryEnum(Origin, out GalleryEvidenceOrigin? origin) ||
            !TryEnum(Role, out FrameArtifactRole? role) ||
            MinimumSequence is < 1 || MaximumSequence is < 1 ||
            MinimumSequence > MaximumSequence ||
            PageSize is < 1 or > 100)
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
            ProcessingRole: role,
            Recipe: Recipe,
            ProcessingStatus: Status);
        return true;
    }

    private Task ApplyFiltersAsync()
    {
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
            ["cursor"] = null
        };
        NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters("/gallery", values));
        return Task.CompletedTask;
    }

    private void ClearFilters() => NavigationManager.NavigateTo("/gallery");

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
    }

    private void SetDraftFrom(ChangeEventArgs args) => _draftFrom = Convert.ToString(
        args.Value,
        CultureInfo.InvariantCulture);

    private void SetDraftTo(ChangeEventArgs args) => _draftTo = Convert.ToString(
        args.Value,
        CultureInfo.InvariantCulture);

    internal static CameraAgentGalleryArtifact? PreferredPreview(CameraAgentGalleryCapture capture) =>
        capture.Artifacts.FirstOrDefault(static artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview) ??
        capture.Artifacts.FirstOrDefault(static artifact => artifact.Role == FrameArtifactRole.Preview);

    internal static string PreviewUrl(Guid artifactId) =>
        FormattableString.Invariant($"/api/v1/operations/artifacts/{artifactId:D}/preview");

    private string DetailUrl(Guid captureId)
    {
        var relative = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var returnUrl = ReturnUrlHelper.NormalizeReturnUrl($"/{relative}");
        if (!string.Equals(returnUrl, "/gallery", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("/gallery?", StringComparison.Ordinal))
        {
            returnUrl = "/gallery";
        }
        return QueryHelpers.AddQueryString(
            FormattableString.Invariant($"/gallery/{captureId:D}"),
            "returnUrl",
            returnUrl);
    }

    private static string EvidenceLabel(GalleryEvidenceOrigin origin) => origin switch
    {
        GalleryEvidenceOrigin.Simulated => "Simulated evidence",
        GalleryEvidenceOrigin.DeveloperFixture => "Developer fixture",
        _ => "Origin unknown / not physical"
    };

    private static string EvidenceClass(GalleryEvidenceOrigin origin) => origin switch
    {
        GalleryEvidenceOrigin.Simulated => "evidence--simulated",
        GalleryEvidenceOrigin.DeveloperFixture => "evidence--fixture",
        _ => "evidence--unknown"
    };

    private static string ProcessingSummary(CameraAgentGalleryCapture capture)
    {
        if (capture.ProcessingNodes.Count == 0)
        {
            return "No nodes";
        }
        var complete = capture.ProcessingNodes.Count(static node => string.Equals(node.Status, "completed", StringComparison.OrdinalIgnoreCase));
        return FormattableString.Invariant($"{complete}/{capture.ProcessingNodes.Count} complete");
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
