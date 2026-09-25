using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class GalleryDetail : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentGalleryCapture? _capture;
    private CameraAgentCapturePresentation? _capturePresentation;
    private CameraAgentCaptureDetailView? _view;
    private string? _errorMessage;
    private long _generation;
    private bool _isLoading;
    private EvidenceTab _evidenceTab;
    private Guid? _openDownloadMenu;
    private readonly Dictionary<Guid, ElementReference> _downloadMenuButtons = [];
    private Guid? _previousCaptureId;
    private Guid? _nextCaptureId;
    private readonly Dictionary<EvidenceTab, ElementReference> _evidenceTabButtons = [];

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
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
    protected override Task OnParametersSetAsync() => LoadAsync();

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
        _view = null;
        _capturePresentation = null;
        _capture = null;
        _previousCaptureId = null;
        _nextCaptureId = null;
        _evidenceTab = EvidenceTab.Overview;
        _openDownloadMenu = null;
        _downloadMenuButtons.Clear();
        var captureId = CaptureId;
        try
        {
            var result = await OperatorService.GetCaptureDetailViewAsync(captureId, cancellation.Token);
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
            else if (result.IsSuccess && result.Value is not null && result.Value.Capture.CaptureId == captureId)
            {
                _view = result.Value;
                _capture = result.Value.Capture;
                _capturePresentation = result.Value.Presentation;
                await LoadCaptureNavigationAsync(captureId, generation, cancellation.Token);
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



    private string CaptureUrl(Guid captureId) => QueryHelpers.AddQueryString(
        FormattableString.Invariant($"/gallery/{captureId:D}"),
        "returnUrl",
        BackUrl);

    private async Task LoadCaptureNavigationAsync(Guid captureId, long generation, CancellationToken cancellationToken)
    {
        if (!TryBuildArchiveQuery(BackUrl, out var query))
        {
            return;
        }
        // Neighbours follow the originating archive filters through bounded
        // keyset reads rather than re-scanning a loaded page.
        var result = await OperatorService.GetGalleryNeighboursAsync(
            captureId, query with { Cursor = null, PageSize = null }, cancellationToken);
        if (generation != Volatile.Read(ref _generation) || CaptureId != captureId) return;
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            _capture = null;
            _view = null;
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (!result.IsSuccess || result.Value is null || result.Value.CaptureId != captureId)
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
        // Archive search is page-local. Neighbours cannot truthfully reproduce it with a server keyset query.
        if (GetQueryValue(parsed, "q") is not null) return false;
        var product = GetQueryValue(parsed, "product");
        var outcome = GetQueryValue(parsed, "outcome");
        if (product is not (null or "all" or "Combined" or "Calibrated" or "Raw") ||
            outcome is not (null or "all" or "Completed" or "Skipped" or "TerminalFailure" or "Running")) return false;
        if (!TryDate(fromText, out var from) || !TryDate(toText, out var to) ||
            from > to ||
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
            ProcessingRole: product is null or "all" ? role : Enum.Parse<FrameArtifactRole>(product),
            Recipe: GetQueryValue(parsed, "recipe"),
            ProcessingStatus: outcome is null or "all" ? GetQueryValue(parsed, "status") : outcome);
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

    private bool IsProjectedArtifact(Guid artifactId) => _capturePresentation?.Stages.Any(slot =>
        slot.ArtifactId == artifactId && slot.Availability == CameraAgentPresentationSlotAvailability.Available) == true;

    private static readonly EvidenceTab[] EvidenceTabs = Enum.GetValues<EvidenceTab>();

    private static string EvidenceTabLabel(EvidenceTab tab) => tab switch
    {
        EvidenceTab.Technical => "Technical",
        _ => tab.ToString()
    };

    // WAI-ARIA tabs pattern: arrow keys, Home and End select and focus a sibling tab.
    private async Task MoveEvidenceTabAsync(KeyboardEventArgs args, EvidenceTab current)
    {
        var index = Array.IndexOf(EvidenceTabs, current);
        EvidenceTab? next = args.Key switch
        {
            "ArrowRight" => EvidenceTabs[(index + 1) % EvidenceTabs.Length],
            "ArrowLeft" => EvidenceTabs[(index + EvidenceTabs.Length - 1) % EvidenceTabs.Length],
            "Home" => EvidenceTabs[0],
            "End" => EvidenceTabs[^1],
            _ => null
        };
        if (next is not { } selected) return;
        SelectEvidenceTab(selected);
        if (_evidenceTabButtons.TryGetValue(selected, out var button))
        {
            try { await button.FocusAsync(); }
            catch (Exception exception) when (exception is Microsoft.JSInterop.JSException or InvalidOperationException) { }
        }
    }

    private int CompletedNodeCount => _capture?.ProcessingNodes.Count(static node =>
        string.Equals(node.Status, "Completed", StringComparison.OrdinalIgnoreCase)) ?? 0;

    private string CloudSummary => _capture?.Detail?.CloudAssessment is not { } cloud
        ? "Unavailable"
        : cloud.CoverageMillionths is null
            ? OperationsPage.SplitWords(cloud.Status ?? cloud.Availability)
            : $"{FormatPercent(cloud.CoverageMillionths)} coverage ({cloud.Quality ?? "quality unavailable"})";

    private static string ArtifactLabel(FrameArtifactRole role) => role switch
    {
        FrameArtifactRole.Raw => "Raw frame",
        FrameArtifactRole.Calibrated => "Calibrated frame",
        FrameArtifactRole.Combined => "Live mean",
        FrameArtifactRole.Preview => "Processed preview",
        FrameArtifactRole.AnnotatedPreview => "Annotated preview",
        FrameArtifactRole.Metadata => "Capture metadata",
        _ => OperationsPage.SplitWords(role.ToString())
    };

    private void SelectEvidenceTab(EvidenceTab tab)
    {
        _evidenceTab = tab;
        _openDownloadMenu = null;
    }

    private void ToggleDownloadMenu(Guid artifactId) =>
        _openDownloadMenu = _openDownloadMenu == artifactId ? null : artifactId;

    // Choosing a format closes the menu; the browser still follows the download link.
    private void CloseDownloadMenu() => _openDownloadMenu = null;

    private async Task CloseDownloadMenuOnEscapeAsync(KeyboardEventArgs args, Guid artifactId)
    {
        if (args.Key != "Escape" || _openDownloadMenu != artifactId) return;
        _openDownloadMenu = null;
        if (_downloadMenuButtons.TryGetValue(artifactId, out var button))
        {
            try { await button.FocusAsync(); }
            catch (Exception exception) when (exception is Microsoft.JSInterop.JSException or InvalidOperationException) { }
        }
    }

    // Formats are declared here so new exports only add an entry; unsupported ones stay visible but disabled.
    private IEnumerable<DownloadOption> DownloadOptions(CameraAgentGalleryArtifact artifact)
    {
        if (artifact.Role is FrameArtifactRole.Metadata)
        {
            // Metadata and overlay layers are structured JSON; image formats do not apply.
            yield return new("original", "JSON file", OriginalDescription(artifact.MediaType), ContentUrl(artifact.ArtifactId));
            yield break;
        }
        var jpeg = string.Equals(artifact.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase);
        if (!jpeg)
        {
            yield return IsProjectedArtifact(artifact.ArtifactId)
                ? new("jpeg", "JPEG image", "8-bit, adjusted for display",
                    FormattableString.Invariant($"/api/v1/operations/artifacts/{artifact.ArtifactId:D}/preview?download=1"))
                : new("jpeg", "JPEG image", "Not available for this artifact", null);
            yield return new("fits", "FITS", "Not yet available", null);
        }
        yield return new("original", jpeg ? "JPEG image (original)" : "Original file",
            OriginalDescription(artifact.MediaType), ContentUrl(artifact.ArtifactId));
    }

    private static string OriginalDescription(string? mediaType) => mediaType?.ToUpperInvariant() switch
    {
        "APPLICATION/VND.HVO.PRESENTATION-LAYER-PAYLOAD+JSON" => "Overlay layer geometry (.json)",
        "APPLICATION/VND.HVO.OVERLAY-MANIFEST+JSON" => "Overlay layer manifest (.json)",
        "APPLICATION/VND.HVO.PROJECTED-SCENE+JSON" => "Projected sky scene (.json)",
        "APPLICATION/VND.HVO.PRESENTATION-METADATA-FACTS+JSON" => "Capture facts for overlays (.json)",
        { } json when json.EndsWith("+JSON", StringComparison.Ordinal) || json == "APPLICATION/JSON" => "Structured metadata (.json)",
        "IMAGE/JPEG" => "Retained bytes, as produced",
        "APPLICATION/X-SKYMONITOR-MONO8" => "Exact retained bytes, raw 8-bit mono (.bin)",
        "APPLICATION/X-SKYMONITOR-MONO16" => "Exact retained bytes, raw 16-bit mono (.bin)",
        "APPLICATION/X-SKYMONITOR-RGB24" => "Exact retained bytes, raw 24-bit RGB (.bin)",
        "APPLICATION/X-SKYMONITOR-BAYER-RGGB16" => "Exact retained bytes, raw 16-bit Bayer RGGB (.bin)",
        "APPLICATION/X-HVO-LINEAR-FRAME" => "Exact retained bytes, SkyMonitor linear frame (.bin)",
        "APPLICATION/X-HVO-PACKED-IMAGE" => "Exact retained bytes, SkyMonitor packed image (.bin)",
        _ => "Exact retained bytes"
    };

    private sealed record DownloadOption(string Format, string Label, string Description, string? Url);

    private enum EvidenceTab { Overview, Artifacts, Processing, Technical, Replay }

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

    private static string InputEvidence(CameraAgentGalleryProcessingNodeInput input)
        => FormattableString.Invariant(
            $"#{input.Ordinal} {input.Kind}; name={input.Name ?? "Unavailable"}; artifact={input.ArtifactId?.ToString() ?? "Unavailable"}; role={input.Role?.ToString() ?? "Unavailable"}; variant={input.Variant ?? "Unavailable"}; recipe={input.RecipeIdentitySha256 ?? "Unavailable"}; schema={input.SchemaVersion ?? "Unavailable"}; identity={input.IdentitySha256 ?? "Unavailable"}; selected={(input.Selected ? "yes" : "no")}");

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
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
