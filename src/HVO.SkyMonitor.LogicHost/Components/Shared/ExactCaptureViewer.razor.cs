using System.Data.Common;
using System.Security.Claims;
using System.Text;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

/// <summary>
/// Renders one exact capture: its header metadata, its own layered presentation and its provenance.
/// The host names the capture by <see cref="CaptureId"/> and cascades the detail it has already read
/// through an authorized service. The viewer renders only a detail and presentation whose capture
/// identity matches <see cref="CaptureId"/> and never substitutes another capture's metadata or image.
/// </summary>
public sealed partial class ExactCaptureViewer : ComponentBase, IAsyncDisposable
{
    internal const string ModulePath = "./Components/Shared/ExactCaptureViewer.razor.js";
    private ClaimsPrincipal? principal;
    private IJSObjectReference? module;
    private ElementReference presentationRoot;
    private bool bindPresentation;
    private Guid? captureId;
    private Task<AuthenticationState>? authenticationState;
    private long presentationGeneration;
    private long baseImageAttempt;
    private CancellationTokenSource? presentationSaveCancellation;
    [Parameter, EditorRequired] public Guid CaptureId { get; set; }
    [Parameter] public string? StatusMessage { get; set; }
    [Parameter] public EventCallback<Guid> StackSaved { get; set; }
    [Inject] internal ICentralLayeredPresentationService Presentations { get; set; } = default!;
    [Inject] internal ICentralPresentationMaterializer PresentationMaterializer { get; set; } = default!;
    [Inject] internal IJSRuntime JS { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;

    // Blazor parameters must be public and the detail's types are internal, so the host cascades it.
    [CascadingParameter] internal OperationsCaptureDetail? CascadedDetail { get; set; }

    // The host's detail, only when it describes the exact capture this viewer was asked to show.
    internal OperationsCaptureDetail? Detail =>
        CascadedDetail is { } detail && detail.Capture.CaptureId == CaptureId ? detail : null;

    internal Guid? DisplayedCaptureId => captureId;
    internal bool IsSavingPresentation { get; private set; }
    internal string? PresentationStatusMessage { get; private set; }
    internal CentralLayeredPresentation? Presentation { get; private set; }
    internal MarkupString PresentationSvg => new(Encoding.UTF8.GetString(Presentation?.Svg ?? []));
    internal string? MaterializationMessage { get; private set; }
    internal bool IsBaseImageUnavailable { get; private set; }

    // Each rendered base image is keyed to the presentation generation, identity and explicit retry
    // attempt, so a new attempt gets a new element and an error raised by a superseded one is ignored.
    internal string BaseImageKey => FormattableString.Invariant(
        $"{presentationGeneration}:{Presentation?.PresentationIdentitySha256}:{baseImageAttempt}");

    protected override async Task OnParametersSetAsync()
    {
        Guid? requestedCaptureId = Detail is null ? null : CaptureId;
        var requestedAuthenticationState = AuthenticationStateTask;
        // A refreshed detail for the same exact capture and the same authentication state keeps its presentation,
        // layer and image state. Sign-out or another actor starts over, so nothing read or saved for the previous
        // actor survives.
        if (captureId == requestedCaptureId && ReferenceEquals(authenticationState, requestedAuthenticationState))
        {
            return;
        }
        // Everything the previous capture or actor held is invalidated before the first await, so an update
        // that is superseded while it waits can never restore its own actor or presentation over a newer one.
        captureId = requestedCaptureId;
        authenticationState = requestedAuthenticationState;
        principal = null;
        var generation = ++presentationGeneration;
        IsSavingPresentation = false;
        IsBaseImageUnavailable = false;
        bindPresentation = false;
        Presentation = null;
        PresentationStatusMessage = null;
        MaterializationMessage = null;
        var activeSave = presentationSaveCancellation;
        presentationSaveCancellation = null;
        if (activeSave is not null)
        {
            await activeSave.CancelAsync();
            activeSave.Dispose();
        }
        if (generation != presentationGeneration || requestedCaptureId is not { } exactCaptureId)
        {
            return;
        }
        var user = (await requestedAuthenticationState).User;
        if (generation != presentationGeneration || user.Identity?.IsAuthenticated != true)
        {
            return;
        }
        principal = user;
        try
        {
            var presentation = await Presentations.GetAsync(exactCaptureId, user);
            if (generation == presentationGeneration &&
                presentation is { Status: CentralLayeredPresentationStatus.Found, Presentation: { } found } &&
                found.CaptureId == exactCaptureId)
            {
                Presentation = found;
                bindPresentation = true;
            }
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            if (generation == presentationGeneration)
            {
                PresentationStatusMessage = "Capture evidence is temporarily unavailable.";
            }
        }
    }

    internal async Task SaveSelectedStackAsync()
    {
        if (Presentation is null || principal is not { } actor || captureId is not { } requestedCaptureId ||
            IsSavingPresentation || IsBaseImageUnavailable)
        {
            return;
        }
        var requestedPresentationIdentity = Presentation.PresentationIdentitySha256;
        var requestedManifestIdentity = Presentation.ManifestIdentitySha256;
        var generation = presentationGeneration;
        using var saveCancellation = new CancellationTokenSource();
        presentationSaveCancellation = saveCancellation;
        IsSavingPresentation = true;
        try
        {
            module ??= await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            var selected = await module.InvokeAsync<string[]>("selectedLayerIdentities", presentationRoot);
            if (!MatchesPresentation(generation, requestedCaptureId, requestedPresentationIdentity) || IsBaseImageUnavailable)
            {
                return;
            }
            var result = await PresentationMaterializer.SaveAsync(
                requestedCaptureId, requestedManifestIdentity, selected, actor, saveCancellation.Token);
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
                await StackSaved.InvokeAsync(requestedCaptureId);
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

    // The browser's image error event carries no reliable HTTP status, so the display only records that
    // the exact base image could not be shown; it never infers a reason or substitutes another image.
    internal void OnBaseImageError(string baseImageKey)
    {
        if (Presentation is not null && string.Equals(baseImageKey, BaseImageKey, StringComparison.Ordinal))
        {
            IsBaseImageUnavailable = true;
        }
    }

    internal void RetryBaseImage()
    {
        if (!IsBaseImageUnavailable || Presentation is null)
        {
            return;
        }
        baseImageAttempt++;
        IsBaseImageUnavailable = false;
        bindPresentation = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!bindPresentation || Presentation is null)
        {
            return;
        }
        bindPresentation = false;
        module ??= await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
        await module.InvokeVoidAsync("bindLayerToggles", presentationRoot);
    }

    private bool MatchesPresentation(long generation, Guid requestedCaptureId, string presentationIdentity) =>
        generation == presentationGeneration && captureId == requestedCaptureId &&
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
    }
}
