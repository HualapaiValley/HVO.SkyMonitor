using System.Data.Common;
using System.Security.Claims;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsCaptureDetail : ComponentBase, IAsyncDisposable
{
    private ClaimsPrincipal? principal;
    private IJSObjectReference? module;
    private ElementReference presentationRoot;
    private bool bindPresentation;
    private long presentationGeneration;
    private CancellationTokenSource? presentationSaveCancellation;
    [Parameter] public Guid CaptureId { get; set; }
    [Inject] internal INetworkOperationsReadService Operations { get; set; } = default!;
    [Inject] internal IPublicRecordPublicationService RecordPublication { get; set; } = default!;
    [Inject] internal ICentralLayeredPresentationService Presentations { get; set; } = default!;
    [Inject] internal ICentralPresentationMaterializer PresentationMaterializer { get; set; } = default!;
    [Inject] internal IJSRuntime JS { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal Services.OperationsCaptureDetail? Detail { get; private set; }
    internal OperationsCaptureTrace? Trace { get; private set; }
    internal bool IsLoading { get; private set; } = true;
    internal bool IsBusy { get; private set; }
    internal bool IsSavingPresentation { get; private set; }
    internal bool IsLoadingMoreArtifacts { get; private set; }
    internal bool IsLoadingTrace { get; private set; }
    internal string? StatusMessage { get; private set; }
    internal CentralLayeredPresentation? Presentation { get; private set; }
    internal MarkupString PresentationSvg => new(Encoding.UTF8.GetString(Presentation?.Svg ?? []));
    internal string? MaterializationMessage { get; private set; }

    protected override async Task OnParametersSetAsync()
    {
        IsLoading = true;
        if (presentationSaveCancellation is { } activeSave)
        {
            await activeSave.CancelAsync();
            activeSave.Dispose();
        }
        presentationSaveCancellation = null;
        presentationGeneration++;
        IsSavingPresentation = false;
        IsLoadingMoreArtifacts = false;
        Detail = null;
        Presentation = null;
        Trace = null;
        StatusMessage = null;
        MaterializationMessage = null;
        principal = (await AuthenticationStateTask).User;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var requestedCaptureId = CaptureId;
        try
        {
            var detail = id is null ? null : await Operations.GetCaptureAsync(id, requestedCaptureId);
            if (CaptureId == requestedCaptureId)
            {
                Detail = detail;
            }
            if (detail is not null && principal.Identity?.IsAuthenticated == true)
            {
                var presentation = await Presentations.GetAsync(requestedCaptureId, principal);
                if (CaptureId == requestedCaptureId && presentation.Status == CentralLayeredPresentationStatus.Found)
                {
                    Presentation = presentation.Presentation;
                    bindPresentation = Presentation is not null;
                }
            }
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            if (CaptureId == requestedCaptureId)
            {
                StatusMessage = "Capture evidence is temporarily unavailable.";
            }
        }
        finally
        {
            if (CaptureId == requestedCaptureId)
            {
                IsLoading = false;
            }
        }
    }

    internal async Task SaveSelectedStackAsync()
    {
        if (Presentation is null || principal is null || IsSavingPresentation)
        {
            return;
        }
        var requestedCaptureId = CaptureId;
        var requestedPresentationIdentity = Presentation.PresentationIdentitySha256;
        var generation = presentationGeneration;
        using var saveCancellation = new CancellationTokenSource();
        presentationSaveCancellation = saveCancellation;
        IsSavingPresentation = true;
        try
        {
            module ??= await JS.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/Operations/OperationsCaptureDetail.razor.js");
            var selected = await module.InvokeAsync<string[]>("selectedLayerIdentities", presentationRoot);
            if (!MatchesPresentation(generation, requestedCaptureId, requestedPresentationIdentity))
            {
                return;
            }
            var result = await PresentationMaterializer.SaveAsync(
                requestedCaptureId, selected, principal, saveCancellation.Token);
            if (!MatchesPresentation(generation, requestedCaptureId, requestedPresentationIdentity))
            {
                return;
            }
            MaterializationMessage = result.Status switch
            {
                CentralPresentationMaterializationStatus.Saved when result.Receipt!.Replayed =>
                    "This exact presentation stack was already materialized.",
                CentralPresentationMaterializationStatus.Saved =>
                    "The selected stack was saved as an immutable central artifact.",
                CentralPresentationMaterializationStatus.Invalid =>
                    "The selected layer set is invalid.",
                CentralPresentationMaterializationStatus.Conflict =>
                    "The retained presentation conflicts with the selected stack.",
                CentralPresentationMaterializationStatus.DependencyUnavailable =>
                    "Presentation storage is temporarily unavailable.",
                _ => "Structured layers are no longer available."
            };
            if (result.Status == CentralPresentationMaterializationStatus.Saved)
            {
                await LoadAsync(requestedCaptureId);
            }
        }
        catch (JSException)
        {
            MaterializationMessage = "The selected layers could not be read. Try again.";
        }
        catch (OperationCanceledException) when (saveCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(presentationSaveCancellation, saveCancellation))
            {
                presentationSaveCancellation = null;
            }
            if (generation == presentationGeneration)
            {
                IsSavingPresentation = false;
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!bindPresentation || Presentation is null)
        {
            return;
        }
        bindPresentation = false;
        module ??= await JS.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/Operations/OperationsCaptureDetail.razor.js");
        await module.InvokeVoidAsync("bindLayerToggles", presentationRoot);
    }

    internal async Task LoadTraceAsync()
    {
        var userId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Detail is null || Trace is not null || userId is null || IsLoadingTrace) return;
        var requestedCaptureId = CaptureId;
        IsLoadingTrace = true;
        try
        {
            var trace = await Operations.GetCaptureTraceAsync(userId, requestedCaptureId);
            if (CaptureId == requestedCaptureId)
            {
                Trace = trace;
            }
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            if (CaptureId == requestedCaptureId)
            {
                StatusMessage = "Deep capture trace is temporarily unavailable.";
            }
        }
        finally
        {
            IsLoadingTrace = false;
        }
    }

    internal async Task DownloadRawAsync(string contentPath)
    {
        if (principal is null) return;
        module ??= await JS.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/Operations/OperationsCaptureDetail.razor.js");
        try
        {
            await module.InvokeVoidAsync("authorizeRawDownload", contentPath);
        }
        catch (JSException)
        {
            StatusMessage = "The raw download could not be authorized. Try again.";
        }
    }

    internal async Task SetArtifactReleaseAsync(OperationsArtifactSummary artifact, bool release)
    {
        var actorUserId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Detail is null || actorUserId is null) return;
        IsBusy = true;
        try
        {
            var result = await RecordPublication.DecideAsync(
                Detail.Capture.ObservatoryId,
                actorUserId,
                new PublicRecordSubject(PublicRecordSubjectKind.Artifact, artifact.PublicationSubjectId),
                release ? PublicationDecisionState.Released : PublicationDecisionState.Withdrawn,
                "public-image-v1",
                release ? "owner-ui-release" : "owner-ui-withdrawal");
            StatusMessage = result.Outcome switch
            {
                PublicRecordPublicationOutcome.Applied => release
                    ? "The image is now publicly released."
                    : "The public image release was withdrawn.",
                PublicRecordPublicationOutcome.Unchanged => "The image publication state was already current.",
                PublicRecordPublicationOutcome.NotFoundOrDenied => "Owner access or publication eligibility changed.",
                PublicRecordPublicationOutcome.Conflict => "The image publication state changed. Reload and try again.",
                _ => "The image publication request was invalid."
            };
            if (result.Outcome is PublicRecordPublicationOutcome.Applied or PublicRecordPublicationOutcome.Unchanged)
            {
                await LoadAsync(CaptureId);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task LoadMoreArtifactsAsync()
    {
        var userId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var detail = Detail;
        if (userId is null || detail?.ArtifactsNextCursor is not { } cursor || IsLoadingMoreArtifacts) return;
        var requestedCaptureId = CaptureId;
        IsLoadingMoreArtifacts = true;
        try
        {
            var page = await Operations.ListCaptureArtifactsAsync(userId, requestedCaptureId, 50, cursor);
            if (CaptureId == requestedCaptureId && ReferenceEquals(Detail, detail))
            {
                Detail = detail with
                {
                    Artifacts = detail.Artifacts.Concat(page.Items).ToArray(),
                    ArtifactsNextCursor = page.NextCursor
                };
            }
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            if (CaptureId == requestedCaptureId)
            {
                StatusMessage = "More artifacts could not be loaded. Try again.";
            }
        }
        finally
        {
            if (CaptureId == requestedCaptureId)
            {
                IsLoadingMoreArtifacts = false;
            }
        }
    }

    private async Task LoadAsync(Guid requestedCaptureId)
    {
        var id = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var detail = id is null ? null : await Operations.GetCaptureAsync(id, requestedCaptureId);
        if (CaptureId == requestedCaptureId)
        {
            Detail = detail;
        }
    }

    private bool MatchesPresentation(long generation, Guid captureId, string presentationIdentity) =>
        generation == presentationGeneration && CaptureId == captureId &&
        string.Equals(Presentation?.PresentationIdentitySha256, presentationIdentity, StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (presentationSaveCancellation is { } activeSave)
        {
            await activeSave.CancelAsync();
        }
        presentationSaveCancellation?.Dispose();
        presentationSaveCancellation = null;
        if (module is not null)
        {
            try
            {
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
        GC.SuppressFinalize(this);
    }
}
