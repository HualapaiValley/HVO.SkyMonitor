using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class GalleryDetail : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentGalleryCapture? _capture;
    private CameraAgentCapturePresentation? _capturePresentation;
    private CameraAgentLayeredPresentation? _presentation;
    private IJSObjectReference? _module;
    private ElementReference _presentationRoot;
    private string? _errorMessage;
    private string? _presentationMessage;
    private string? _saveError;
    private string? _saveMessage;
    private long _generation;
    private long _saveGeneration;
    private bool _isLoading;
    private bool _bindPresentation;
    private bool _isSaving;
    private bool _viewerOpen;
    private string? _comparisonLeftArtifactId;
    private string? _comparisonRightArtifactId;
    private Guid? _previousCaptureId;
    private Guid? _nextCaptureId;
    private CameraAgentPresentationStage? _selectedStage;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Parameter] public Guid CaptureId { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "returnUrl")]
    [SuppressMessage("Design", "CA1056:Uri properties should not be strings", Justification = "The raw query value is validated as an application-local return URL before use.")]
    public string? ReturnUrl { get; set; }

    private string BackUrl
    {
        get
        {
            var value = ReturnUrlHelper.NormalizeReturnUrl(ReturnUrl);
            return string.Equals(value, "/gallery", StringComparison.Ordinal) ||
                value.StartsWith("/gallery?", StringComparison.Ordinal)
                    ? value
                    : "/gallery";
        }
    }

    /// <summary>Archive-to-replay entry point; the submit page resolves and freezes the exact inputs.</summary>
    private string ReplayUrl => $"/operations/pipeline/replays/new?captureId={CaptureId:D}";
    private Guid? _liveExecutionId;

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    protected override Task OnParametersSetAsync() => LoadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_bindPresentation)
        {
            return;
        }
        _bindPresentation = false;
        _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/GalleryDetail.razor.js");
        await _module.InvokeVoidAsync("bindLayerToggles", _presentationRoot);
    }

    internal Task RefreshAuthorizationAsync() => LoadAsync();

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
        _presentation = null;
        _capturePresentation = null;
        _presentationMessage = null;
        _capture = null;
        _liveExecutionId = null;
        _previousCaptureId = null;
        _nextCaptureId = null;
        _selectedStage = null;
        _viewerOpen = false;
        Interlocked.Increment(ref _saveGeneration);
        _isSaving = false;
        _saveError = null;
        _saveMessage = null;
        try
        {
            var result = await OperatorService.GetCaptureDetailViewAsync(CaptureId, cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _capture = null;
                _errorMessage = null;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _capture = result.Value.Capture;
                var liveRun = await GraphService.GetLiveExecutionIdAsync(CaptureId, cancellation.Token).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _generation)) return;
                if (liveRun.Kind == OperatorUiResultKind.Unauthorized)
                {
                    _capture = null;
                    NavigationManager.NavigateTo("/Account/AccessDenied");
                    return;
                }
                _liveExecutionId = liveRun.IsSuccess ? liveRun.Value?.ExecutionId : null;
                _capturePresentation = result.Value.Presentation;
                _selectedStage = result.Value.Presentation.SelectedStage;
                InitializeComparison();
                await LoadCaptureNavigationAsync(cancellation.Token);
                var presentation = await OperatorService.GetLayeredPresentationAsync(CaptureId, cancellation.Token);
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }
                if (presentation.Kind == OperatorUiResultKind.Unauthorized)
                {
                    _capture = null;
                    NavigationManager.NavigateTo("/Account/AccessDenied");
                }
                else if (presentation.IsSuccess && presentation.Value is not null)
                {
                    _presentation = presentation.Value;
                    _bindPresentation = true;
                }
                else
                {
                    _presentationMessage = presentation.Message ??
                        "Structured layers were not retained for this capture.";
                }
            }
            else
            {
                _errorMessage = result.Message ?? "The capture detail is temporarily unavailable.";
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

    private static string ContentUrl(Guid artifactId) =>
        FormattableString.Invariant($"/api/v1/operations/artifacts/{artifactId:D}/content");

    private MarkupString PresentationSvg => new(
        _presentation is null ? string.Empty : Encoding.UTF8.GetString(_presentation.Svg.Span));

    private static string PreviewUrl(Guid artifactId) =>
        FormattableString.Invariant($"/api/v1/operations/artifacts/{artifactId:D}/preview");

    private static string LayerLabel(string kind) => kind switch
    {
        "scene-annotation" or "star-annotations" => "Star annotations",
        "scene-cardinals" or "cardinal-directions" => "Cardinal directions",
        "scene-image-circle" or "image-circle" => "Image circle",
        "scene-constellations" or "constellations" => "Constellations",
        "environment" or "corner-annotations" => "Corner annotations",
        _ => OperationsPage.SplitWords(kind)
    };

    private CameraAgentPresentationSlot? SelectedSlot => _capturePresentation?.Stages
        .SingleOrDefault(slot => slot.Stage == _selectedStage &&
            slot.Availability == CameraAgentPresentationSlotAvailability.Available);

    private string SelectedStageLabel => SelectedSlot?.Label ?? "Image unavailable";

    private string ViewerTitle => _capture is null
        ? "Capture image"
        : $"{SelectedStageLabel} sky / {GalleryPage.FormatCaptureTime(_capture.ExposureStartedUtc)}";

    private string ImageAlt => _capture is null
        ? "Retained sky capture"
        : $"Capture {_capture.CaptureSequence} {SelectedStageLabel} image";

    private string CaptureSummary => _capture?.Detail?.Controls is { } controls
        ? FormattableString.Invariant($"{controls.EffectiveExposureMilliseconds / 1000d:0.###}-second sky capture")
        : "Retained sky capture";

    private string ExposureSummary => _capture?.Detail?.Controls is { } controls
        ? FormattableString.Invariant($"{controls.EffectiveExposureMilliseconds / 1000d:0.###} sec")
        : "Unavailable";

    private string GainSummary => _capture?.Detail?.Controls is { } controls
        ? controls.EffectiveGain.ToString("0.###", CultureInfo.InvariantCulture)
        : "Unavailable";

    private string SensorSummary => _capture?.Detail?.Controls?.EffectiveTemperatureC is { } temperature
        ? FormattableString.Invariant($"{temperature:0.0} C")
        : "Unavailable";

    private string ConditionsSummary => _capture?.Detail?.CloudAssessment is { } cloud &&
        string.Equals(cloud.Availability, "Available", StringComparison.OrdinalIgnoreCase)
            ? cloud.CoverageMillionths is { } coverage
                ? FormattableString.Invariant($"{OperationsPage.SplitWords(cloud.Quality ?? cloud.Status ?? "Available")} / {coverage / 10_000d:0.#}% cloud")
                : OperationsPage.SplitWords(cloud.Quality ?? cloud.Status ?? "Available")
            : "Unavailable";

    private Task SelectStageAsync(CameraAgentPresentationStage stage)
    {
        var slot = _capturePresentation?.Stages.SingleOrDefault(candidate => candidate.Stage == stage);
        if (slot?.Availability == CameraAgentPresentationSlotAvailability.Available)
        {
            _selectedStage = stage;
        }
        return Task.CompletedTask;
    }

    private void OpenViewer() => _viewerOpen = SelectedSlot is not null;

    private string CaptureUrl(Guid captureId) => QueryHelpers.AddQueryString(
        FormattableString.Invariant($"/gallery/{captureId:D}"),
        "returnUrl",
        BackUrl);

    private async Task LoadCaptureNavigationAsync(CancellationToken cancellationToken)
    {
        if (!TryBuildArchiveQuery(BackUrl, out var query))
        {
            return;
        }
        // Neighbours follow the originating archive filters through bounded
        // keyset reads rather than re-scanning a loaded page.
        var result = await OperatorService.GetGalleryNeighboursAsync(
            CaptureId, query with { Cursor = null, PageSize = null }, cancellationToken);
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (!result.IsSuccess || result.Value is null)
        {
            return;
        }
        _nextCaptureId = result.Value.NewerCaptureId;
        _previousCaptureId = result.Value.OlderCaptureId;
    }

    private static bool TryBuildArchiveQuery(string returnUrl, [NotNullWhen(true)] out CameraAgentGalleryQuery? query)
    {
        query = null;
        var parsed = QueryHelpers.ParseQuery(new Uri(new Uri("https://cameraagent.invalid"), returnUrl).Query);
        var fromText = GetQueryValue(parsed, "from");
        var toText = GetQueryValue(parsed, "to");
        if (!TryDate(fromText, out var from) || !TryDate(toText, out var to) ||
            !TryEnum(GetQueryValue(parsed, "origin"), out GalleryEvidenceOrigin? origin) ||
            !TryEnum(GetQueryValue(parsed, "role"), out FrameArtifactRole? role) ||
            !TryLong(GetQueryValue(parsed, "minSequence"), out var minimumSequence) ||
            !TryLong(GetQueryValue(parsed, "maxSequence"), out var maximumSequence) ||
            !TryInt(GetQueryValue(parsed, "pageSize"), out var pageSize) ||
            minimumSequence is < 1 || maximumSequence is < 1 || minimumSequence > maximumSequence ||
            pageSize is < 1 or > 100)
        {
            return false;
        }
        query = new CameraAgentGalleryQuery(
            pageSize ?? 24,
            GetQueryValue(parsed, "cursor"),
            from,
            to,
            minimumSequence,
            maximumSequence,
            RawState: GetQueryValue(parsed, "rawState"),
            EvidenceOrigin: origin,
            ProcessingRole: role,
            Recipe: GetQueryValue(parsed, "recipe"),
            ProcessingStatus: GetQueryValue(parsed, "status"));
        return true;
    }

    private static string? GetQueryValue(
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> values,
        string name)
        => values.TryGetValue(name, out var value) && value.Count == 1 && !string.IsNullOrWhiteSpace(value[0])
            ? value[0]
            : null;

    private static bool TryDate(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (value is null)
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
        if (value is null)
        {
            return true;
        }
        if (!Enum.TryParse<T>(value, true, out var result) || !Enum.IsDefined(result))
        {
            return false;
        }
        parsed = result;
        return true;
    }

    private static bool TryLong(string? value, out long? parsed)
    {
        parsed = null;
        if (value is null)
        {
            return true;
        }
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            return false;
        }
        parsed = result;
        return true;
    }

    private static bool TryInt(string? value, out int? parsed)
    {
        parsed = null;
        if (value is null)
        {
            return true;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            return false;
        }
        parsed = result;
        return true;
    }

    private async Task SaveSelectedStackAsync()
    {
        if (_presentation is null || _isSaving)
        {
            return;
        }
        var loadGeneration = Volatile.Read(ref _generation);
        var saveGeneration = Interlocked.Increment(ref _saveGeneration);
        var captureId = CaptureId;
        var cancellation = _loadCancellation;
        if (cancellation is null)
        {
            return;
        }
        _isSaving = true;
        _saveError = null;
        _saveMessage = null;
        try
        {
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/GalleryDetail.razor.js");
            var selected = await _module.InvokeAsync<string[]>("selectedLayerIdentities", _presentationRoot) ?? [];
            if (!IsCurrentSave(loadGeneration, saveGeneration, captureId, cancellation))
            {
                return;
            }
            var result = await OperatorService.SaveLayeredPresentationAsync(
                captureId, selected, cancellation.Token);
            if (!IsCurrentSave(loadGeneration, saveGeneration, captureId, cancellation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is { } receipt)
            {
                _saveMessage = receipt.Replayed
                    ? "This exact flattened stack was already saved."
                    : "Flattened stack saved as a new immutable artifact.";
            }
            else
            {
                _saveError = result.Message ?? "The presentation stack could not be saved.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (saveGeneration == Volatile.Read(ref _saveGeneration))
            {
                _isSaving = false;
            }
        }
    }

    private bool IsCurrentSave(
        long loadGeneration,
        long saveGeneration,
        Guid captureId,
        CancellationTokenSource cancellation)
        => loadGeneration == Volatile.Read(ref _generation) &&
           saveGeneration == Volatile.Read(ref _saveGeneration) &&
           captureId == CaptureId &&
           ReferenceEquals(cancellation, _loadCancellation) &&
           !cancellation.IsCancellationRequested;

    private IReadOnlyList<CameraAgentGalleryArtifact> ComparisonArtifacts =>
        _capturePresentation?.Stages
            .Where(static slot => slot.Availability == CameraAgentPresentationSlotAvailability.Available && slot.ArtifactId is not null)
            .Select(slot => _capture?.Artifacts.SingleOrDefault(artifact => artifact.ArtifactId == slot.ArtifactId))
            .OfType<CameraAgentGalleryArtifact>()
            .ToArray() ?? [];

    private bool IsProjectedArtifact(Guid artifactId) => _capturePresentation?.Stages.Any(slot =>
        slot.ArtifactId == artifactId && slot.Availability == CameraAgentPresentationSlotAvailability.Available) == true;

    private CameraAgentGalleryArtifact? ComparisonLeft => FindComparisonArtifact(_comparisonLeftArtifactId);

    private CameraAgentGalleryArtifact? ComparisonRight => FindComparisonArtifact(_comparisonRightArtifactId);

    private void InitializeComparison()
    {
        var candidates = ComparisonArtifacts;
        if (candidates.Count < 2)
        {
            _comparisonLeftArtifactId = null;
            _comparisonRightArtifactId = null;
            return;
        }
        var preferredArtifactId = SelectedSlot?.ArtifactId;
        var preferred = candidates.FirstOrDefault(artifact => artifact.ArtifactId == preferredArtifactId) ?? candidates[^1];
        _comparisonRightArtifactId = preferred.ArtifactId.ToString("D");
        _comparisonLeftArtifactId = candidates.First(artifact => artifact.ArtifactId != preferred.ArtifactId)
            .ArtifactId.ToString("D");
    }

    private CameraAgentGalleryArtifact? FindComparisonArtifact(string? value)
        => Guid.TryParse(value, out var artifactId)
            ? ComparisonArtifacts.SingleOrDefault(artifact => artifact.ArtifactId == artifactId)
            : null;

    private void SelectComparisonLeft(ChangeEventArgs args) => SelectComparison(args.Value?.ToString(), left: true);

    private void SelectComparisonRight(ChangeEventArgs args) => SelectComparison(args.Value?.ToString(), left: false);

    private void SelectComparison(string? value, bool left)
    {
        var selected = FindComparisonArtifact(value);
        if (selected is null || string.Equals(
                value,
                left ? _comparisonRightArtifactId : _comparisonLeftArtifactId,
                StringComparison.Ordinal))
        {
            return;
        }
        if (left)
        {
            _comparisonLeftArtifactId = value;
        }
        else
        {
            _comparisonRightArtifactId = value;
        }
    }

    private CameraAgentGalleryProcessingNodeDetail? FindArtifactNode(CameraAgentGalleryArtifact artifact)
        => artifact.ProcessingNodeId is null ? null : FindNodeDetail(artifact.ProcessingNodeId);

    private CameraAgentGalleryProcessingNode? FindArtifactNodeSummary(CameraAgentGalleryArtifact artifact)
        => artifact.ProcessingNodeId is null
            ? null
            : _capture?.ProcessingNodes.SingleOrDefault(node => string.Equals(
                node.NodeId, artifact.ProcessingNodeId, StringComparison.Ordinal));

    private CameraAgentGalleryArtifactState? FindArtifactState(Guid artifactId) =>
        _capture?.Detail?.ArtifactStates.SingleOrDefault(state => state.ArtifactId == artifactId);

    private CameraAgentGalleryProcessingNodeDetail? FindNodeDetail(string nodeId) =>
        _capture?.Detail?.ProcessingNodes.SingleOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal));

    private static string FormatNodeOutcome(
        CameraAgentGalleryProcessingNode node,
        CameraAgentGalleryProcessingNodeDetail? detail)
        => detail?.Inputs is null
            ? "Unavailable for legacy processing record"
            : detail.Outcome is not null
                ? OperationsPage.SplitWords(detail.Outcome)
                : node.RecipeName is null
                    ? "Not applicable (infrastructure node)"
                    : "Recipe did not return an outcome";

    private static string FormatNodeStarted(CameraAgentGalleryProcessingNodeDetail? detail)
        => detail?.StartedUtc is { } startedUtc
            ? GalleryPage.FormatCaptureTime(startedUtc)
            : detail?.Inputs is null
                ? "Unavailable for legacy processing record"
                : "Not executed";

    private static string FormatNodeDuration(CameraAgentGalleryProcessingNodeDetail? detail)
        => detail?.DurationMilliseconds is { } duration
            ? FormattableString.Invariant($"{duration:F3} ms")
            : detail?.Inputs is null
                ? "Unavailable for legacy processing record"
                : "Not executed";

    private static string FormatNodeProfile(CameraAgentGalleryProcessingNodeDetail? detail)
        => detail?.ProcessingProfileIdentitySha256 ?? (detail?.Inputs is null
            ? "Unavailable for legacy processing record"
            : "Unavailable: execution profile not verified");

    private static string FormatArtifactDuration(
        CameraAgentGalleryArtifact artifact,
        CameraAgentGalleryProcessingNodeDetail? detail)
        => artifact.ProcessingNodeId is null
            ? "Not applicable (acquisition)"
            : detail is null
                ? "Unavailable"
                : FormatNodeDuration(detail);

    private static string FormatComparisonOutcome(
        CameraAgentGalleryArtifact artifact,
        CameraAgentGalleryProcessingNodeDetail? detail)
        => artifact.ProcessingNodeId is null
            ? "Not applicable (acquisition)"
            : detail?.Outcome is null
                ? "Unavailable"
                : OperationsPage.SplitWords(detail.Outcome);

    private static string InputEvidence(CameraAgentGalleryProcessingNodeInput input)
        => FormattableString.Invariant(
            $"#{input.Ordinal} {input.Kind}; name={input.Name ?? "Unavailable"}; artifact={input.ArtifactId?.ToString() ?? "Unavailable"}; role={input.Role?.ToString() ?? "Unavailable"}; variant={input.Variant ?? "Unavailable"}; recipe={input.RecipeIdentitySha256 ?? "Unavailable"}; schema={input.SchemaVersion ?? "Unavailable"}; identity={input.IdentitySha256 ?? "Unavailable"}; selected={(input.Selected ? "yes" : "no")}");

    private string FormatArtifactProfile(
        CameraAgentGalleryArtifact artifact,
        CameraAgentGalleryProcessingNodeDetail? detail)
        => artifact.ProcessingNodeId is null
            ? _capture?.Detail?.ProcessingProfile?.Sha256 ?? "Unavailable"
            : detail is null
                ? "Unavailable"
                : FormatNodeProfile(detail);

    private static string FormatPercent(int? millionths) => millionths is null
        ? "Unavailable"
        : FormattableString.Invariant($"{millionths.Value / 10_000d:0.##}%");

    private static string EvidenceLabel(GalleryEvidenceOrigin origin) => origin switch
    {
        GalleryEvidenceOrigin.Simulated => "Simulated evidence",
        GalleryEvidenceOrigin.DeveloperFixture => "Developer fixture evidence",
        _ => "Origin unknown / not physical"
    };

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _saveGeneration);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
