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
        artifact.Role is HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview or
            HVO.SkyMonitor.AgentCore.FrameArtifactRole.AnnotatedPreview;

    private CameraAgentGalleryArtifactState? FindArtifactState(Guid artifactId) =>
        _capture?.Detail?.ArtifactStates.SingleOrDefault(state => state.ArtifactId == artifactId);

    private CameraAgentGalleryProcessingNodeDetail? FindNodeDetail(string nodeId) =>
        _capture?.Detail?.ProcessingNodes.SingleOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal));

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
