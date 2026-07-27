using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class GalleryDetail : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentGalleryCapture? _capture;
    private string? _errorMessage;
    private long _generation;
    private bool _isLoading;
    private string? _comparisonLeftArtifactId;
    private string? _comparisonRightArtifactId;

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
        _capture = null;
        try
        {
            var result = await OperatorService.GetGalleryCaptureAsync(CaptureId, cancellation.Token);
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
                _capture = result.Value;
                InitializeComparison(result.Value);
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

    private static bool SupportsPreview(CameraAgentGalleryArtifact artifact) =>
        artifact.MediaType is not null && IsSupportedPreviewMediaType(artifact.MediaType) &&
        artifact.Role is (HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw or
            HVO.SkyMonitor.AgentCore.FrameArtifactRole.Calibrated or
            HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined or
            HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview or
            HVO.SkyMonitor.AgentCore.FrameArtifactRole.AnnotatedPreview);

    private static bool IsSupportedPreviewMediaType(string mediaType) =>
        string.Equals(mediaType, "application/x-hvo-packed-image", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/x-skymonitor-mono8", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/x-skymonitor-mono16", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/x-skymonitor-rgb24", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/x-skymonitor-bayer-rggb16", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<CameraAgentGalleryArtifact> ComparisonArtifacts =>
        _capture?.Artifacts.Where(SupportsPreview).ToArray() ?? [];

    private CameraAgentGalleryArtifact? ComparisonLeft => FindComparisonArtifact(_comparisonLeftArtifactId);

    private CameraAgentGalleryArtifact? ComparisonRight => FindComparisonArtifact(_comparisonRightArtifactId);

    private void InitializeComparison(CameraAgentGalleryCapture capture)
    {
        var candidates = capture.Artifacts.Where(SupportsPreview).ToArray();
        if (candidates.Length < 2)
        {
            _comparisonLeftArtifactId = null;
            _comparisonRightArtifactId = null;
            return;
        }
        var preferredArtifactId = GalleryPage.PreferredPreview(capture)?.ArtifactId;
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
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
