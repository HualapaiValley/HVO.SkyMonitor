using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

public sealed partial class CurrentSkyView : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Each load owns and disposes its cancellation source.")]
    private CancellationTokenSource? activeLoad;
    private long generation;
    private bool disposed;
    private bool loading = true;
    private string message = "Selecting the latest eligible retained presentation…";
    private CameraCurrentSelection? selection;
    private OperationsCaptureDetail? detail;
    private ElementReference root;
    private IJSObjectReference? module;
    private DotNetObjectReference<CurrentSkyView>? reference;
    private PeriodicTimer? timer;
    private Task? revalidation;

    [Parameter] public Guid ObservatoryId { get; set; }
    [Parameter] public Guid CameraId { get; set; }
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<CurrentSkyView> Logger { get; set; } = default!;

    protected override void OnInitialized() => Authentication.AuthenticationStateChanged += AuthenticationChanged;
    protected override Task OnParametersSetAsync() => LoadAsync(null);

    [JSInvokable]
    public Task RefreshAsync() => InvokeAsync(() => LoadAndRenderAsync(null));
    private Task ContinueAsync() => LoadAndRenderAsync(selection?.NextCursor);

    private async Task LoadAndRenderAsync(string? cursor)
    {
        var pending = LoadAsync(cursor);
        StateHasChanged();
        await pending;
        if (!disposed) StateHasChanged();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Clear protected content on any read failure without exposing infrastructure diagnostics.")]
    private async Task LoadAsync(string? cursor)
    {
        if (disposed) return;
        var version = ++generation;
        var previous = activeLoad;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeLoad = cancellation;
        var token = cancellation.Token;
        var observatory = ObservatoryId;
        var camera = CameraId;
        // Remove the old image before awaiting authority or storage, even on a same-capture refresh.
        detail = null;
        selection = null;
        loading = true;
        message = "Selecting the latest eligible retained presentation…";
        try
        {
            if (previous is not null) await previous.CancelAsync();
            var principal = (await Authentication.GetAuthenticationStateAsync()).User;
            token.ThrowIfCancellationRequested();
            if (!CentralArtifactCredentialAccess.HasOwnerCredential(principal) ||
                !CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, observatory))
            {
                if (version == generation) message = "Camera not found or access was removed.";
                return;
            }
            await using var scope = ScopeFactory.CreateAsyncScope();
            var archive = scope.ServiceProvider.GetRequiredService<ILogicalCameraArchiveReadService>();
            var result = await archive.SelectCurrentAsync(principal, observatory, camera, cursor, token);
            OperationsCaptureDetail? capture = null;
            Guid? requestedCapture = result?.Capture?.CaptureId;
            if (result is { Status: "no-result" or "dependency-unavailable" })
            {
                var latest = await archive.ListCapturesAsync(principal, observatory, camera, new(), 1, null, token);
                requestedCapture = latest.Items.Count > 0 ? latest.Items[0].CaptureId : null;
            }
            if (requestedCapture is { } exactId)
                capture = await archive.GetCaptureAsync(principal, observatory, camera, exactId, token);
            token.ThrowIfCancellationRequested();
            if (version != generation || disposed) return;
            selection = result;
            if (capture is not null && capture.Capture.CaptureId == requestedCapture &&
                capture.Capture.ObservatoryId == observatory && capture.Capture.LogicalCameraId == camera)
                detail = capture;
            message = result switch
            {
                null => "Camera not found or access was removed.",
                { Status: "found" } when detail is not null => "Latest eligible retained presentation. Refresh to recheck content and access.",
                { Status: "found" } => "Selected capture not found or access was removed.",
                { Status: "continue" } => "No eligible presentation in this bounded search. Search older retained captures to continue.",
                { Status: "no-result" } => "No eligible retained presentation is available for this camera.",
                _ => "Presentation storage is temporarily unavailable. Refresh to try again."
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ArgumentException)
        {
            if (version == generation) message = "This selection continuation is no longer available. Refresh to start again.";
        }
        catch (Exception exception)
        {
            LogReadFailed(Logger, exception);
            if (version == generation) message = "Current Sky is temporarily unavailable. Refresh to try again.";
        }
        finally
        {
            if (ReferenceEquals(activeLoad, cancellation)) activeLoad = null;
            if (version == generation) loading = false;
        }
    }

    private void AuthenticationChanged(Task<AuthenticationState> state) => _ = RefreshAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || disposed) return;
        timer = new PeriodicTimer(TimeSpan.FromSeconds(30), Clock);
        revalidation = RevalidateAsync();
        try
        {
            module = await JS.InvokeAsync<IJSObjectReference>("import", lifetime.Token, "./Components/Shared/CurrentSkyView.razor.js");
            if (disposed)
            {
                await module.DisposeAsync();
                module = null;
                return;
            }
            reference = DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("attach", root, reference);
        }
        catch (JSException exception) { LogBrowserUnavailable(Logger, exception); }
        catch (OperationCanceledException) when (disposed) { }
    }

    private async Task RevalidateAsync()
    {
        try
        {
            while (await timer!.WaitForNextTickAsync(lifetime.Token)) await RefreshAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        ++generation;
        Authentication.AuthenticationStateChanged -= AuthenticationChanged;
        await lifetime.CancelAsync();
        timer?.Dispose();
        if (revalidation is not null) await revalidation;
        if (module is not null)
        {
            try
            {
                await module.InvokeVoidAsync("detach", root);
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (TaskCanceledException) { }
        }
        reference?.Dispose();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Current Sky selection read failed.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Debug, Message = "Current Sky browser hooks are unavailable.")]
    private static partial void LogBrowserUnavailable(ILogger logger, Exception exception);
}
