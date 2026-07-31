using System.Data.Common;
using System.Security.Claims;
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
    [Parameter] public Guid CaptureId { get; set; }
    [Inject] internal INetworkOperationsReadService Operations { get; set; } = default!;
    [Inject] internal IPublicRecordPublicationService RecordPublication { get; set; } = default!;
    [Inject] internal IJSRuntime JS { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal Services.OperationsCaptureDetail? Detail { get; private set; }
    internal OperationsCaptureTrace? Trace { get; private set; }
    internal bool IsLoading { get; private set; } = true;
    internal bool IsBusy { get; private set; }
    internal bool IsLoadingMoreArtifacts { get; private set; }
    internal bool IsLoadingTrace { get; private set; }
    internal string? StatusMessage { get; private set; }

    protected override async Task OnParametersSetAsync()
    {
        IsLoading = true;
        IsLoadingMoreArtifacts = false;
        Detail = null;
        Trace = null;
        StatusMessage = null;
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
                await LoadAsync();
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

    private async Task LoadAsync()
    {
        var id = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        Detail = id is null ? null : await Operations.GetCaptureAsync(id, CaptureId);
    }

    public async ValueTask DisposeAsync()
    {
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
