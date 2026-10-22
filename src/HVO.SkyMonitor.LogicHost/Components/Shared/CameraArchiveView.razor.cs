using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

public sealed partial class CameraArchiveView : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Each load owns and disposes its cancellation source.")]
    private CancellationTokenSource? activeLoad;
    private long generation;
    private bool disposed;
    private bool loading = true;
    private string message = "Loading retained capture evidence…";
    private CameraArchivePage? page;
    private OperationsCaptureTrace? trace;
    private bool traceLoading;
    private string? restoredLocation;
    private OperationsCaptureDetail? detail;
    private ElementReference root;
    private IJSObjectReference? module;
    private DotNetObjectReference<CameraArchiveView>? reference;
    private PeriodicTimer? timer;
    private Task? revalidation;

    [Parameter] public Guid ObservatoryId { get; set; }
    [Parameter] public Guid CameraId { get; set; }
    [Parameter] public Guid? CaptureId { get; set; }
    [Parameter] public string? From { get; set; }
    [Parameter] public string? Until { get; set; }
    [Parameter] public string? Product { get; set; }
    [Parameter] public string? ObjectState { get; set; }
    [Parameter] public string? Reconstruction { get; set; }
    [Parameter] public string? Identity { get; set; }
    [Parameter] public string? Cursor { get; set; }
    [Parameter] public string? Focus { get; set; }
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<CameraArchiveView> Logger { get; set; } = default!;

    private string ArchivePath => $"/app/network/observatories/{ObservatoryId:D}/cameras/{CameraId:D}/archive";
    private string PageUrl(string? cursor, Guid? capture = null, string? focus = null) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            capture is { } id ? $"{ArchivePath}/{id:D}" : ArchivePath,
            new Dictionary<string, string?>
            {
                ["from"] = From,
                ["until"] = Until,
                ["product"] = Product,
                ["objectState"] = ObjectState,
                ["reconstruction"] = Reconstruction,
                ["identity"] = Identity,
                ["cursor"] = cursor,
                ["focus"] = focus
            });
    private string BackUrl => PageUrl(Cursor, focus: CaptureId?.ToString("D"));

    protected override void OnInitialized() => Authentication.AuthenticationStateChanged += AuthenticationChanged;
    protected override Task OnParametersSetAsync() => LoadAsync();
    [JSInvokable]
    public Task RefreshAsync() => InvokeAsync(async () =>
    {
        var pending = LoadAsync();
        StateHasChanged();
        await pending;
        if (!disposed) StateHasChanged();
    });

    private static DateTimeOffset? ParseDate(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() :
            throw new ArgumentException("Invalid capture interval.");
    private static T? ParseEnum<T>(string? value) where T : struct, Enum => string.IsNullOrWhiteSpace(value) ? null :
        Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new ArgumentException("Invalid filter.");

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Clear protected content on any read failure without exposing infrastructure diagnostics.")]
    private async Task LoadAsync()
    {
        if (disposed) return;
        var version = ++generation;
        var previous = activeLoad;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeLoad = cancellation;
        var token = cancellation.Token;
        var observatory = ObservatoryId;
        var camera = CameraId;
        var captureId = CaptureId;
        // Remove the old image before awaiting authority or storage, even on a same-capture refresh.
        detail = null;
        page = null;
        trace = null;
        traceLoading = false;
        loading = true;
        message = "Loading retained capture evidence…";
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
            if (captureId is { } exactId)
            {
                var capture = await archive.GetCaptureAsync(principal, observatory, camera, exactId, token);
                token.ThrowIfCancellationRequested();
                if (version != generation || disposed) return;
                if (capture is not null && capture.Capture.CaptureId == exactId &&
                    capture.Capture.ObservatoryId == observatory && capture.Capture.LogicalCameraId == camera)
                {
                    detail = capture;
                }
                message = detail is null ? "Capture not found or access was removed." :
                    "Exact retained capture. Refresh to recheck content and access.";
            }
            else
            {
                var filter = new CameraArchiveFilter(ParseDate(From), ParseDate(Until), ParseEnum<AgentCore.FrameArtifactRole>(Product),
                    ParseEnum<Data.CentralArtifactObjectState>(ObjectState), ParseEnum<Data.CentralReconstructionState>(Reconstruction), Identity);
                var result = await archive.ListCapturesAsync(principal, observatory, camera, filter, 25, Cursor, token);
                token.ThrowIfCancellationRequested();
                if (version != generation || disposed) return;
                page = result;
                message = result.Items.Count == 0 ? "No retained captures on this page." :
                    "One bounded page, newest capture time first. Recorded content states do not verify stored bytes.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ArgumentException)
        {
            if (version == generation) message = "This archive filter or continuation is no longer available. Start from the first page or change the filters.";
        }
        catch (Exception exception)
        {
            LogReadFailed(Logger, exception);
            if (version == generation) message = "Archive evidence is temporarily unavailable. Refresh to try again.";
        }
        finally
        {
            if (ReferenceEquals(activeLoad, cancellation)) activeLoad = null;
            if (version == generation) loading = false;
        }
    }

    private void AuthenticationChanged(Task<AuthenticationState> state) => _ = RefreshAsync();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Clear all protected evidence when a trace or authorization read fails.")]
    private async Task LoadTraceAsync()
    {
        if (detail is null || traceLoading) return;
        var version = generation;
        var observatory = ObservatoryId;
        var camera = CameraId;
        var exactId = detail.Capture.CaptureId;
        traceLoading = true;
        try
        {
            var principal = (await Authentication.GetAuthenticationStateAsync()).User;
            await using var scope = ScopeFactory.CreateAsyncScope();
            var archive = scope.ServiceProvider.GetRequiredService<ILogicalCameraArchiveReadService>();
            var authorized = await archive.GetCaptureAsync(principal, observatory, camera, exactId, lifetime.Token);
            OperationsCaptureTrace? result = null;
            if (authorized is not null)
            {
                var user = CentralArtifactCredentialAccess.GetOwnerId(principal)!;
                result = await scope.ServiceProvider.GetRequiredService<INetworkOperationsReadService>()
                    .GetCaptureTraceAsync(user, exactId, lifetime.Token);
                authorized = await archive.GetCaptureAsync(principal, observatory, camera, exactId, lifetime.Token);
            }
            if (version != generation || disposed) return;
            if (authorized is null || result is null)
            {
                detail = null;
                message = "Capture not found or access was removed.";
            }
            else trace = result;
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception exception)
        {
            LogReadFailed(Logger, exception);
            if (version == generation)
            {
                detail = null;
                message = "Capture trace is temporarily unavailable. Refresh to try again.";
            }
        }
        finally { if (version == generation) traceLoading = false; }
    }

    private async Task DownloadRawAsync(string contentPath)
    {
        if (module is null || detail is null) return;
        var version = generation;
        try
        {
            await module.InvokeVoidAsync("authorizeRawDownload", contentPath);
            if (version != generation || disposed) return;
            message = "A short-lived raw download was authorized. Downloads recheck access; authorize again if the grant expires or content changes.";
        }
        catch (JSException)
        {
            if (version != generation || disposed) return;
            detail = null;
            trace = null;
            message = "Raw download authorization failed or expired. Refresh access and authorize again.";
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        if (firstRender)
        {
            timer = new PeriodicTimer(TimeSpan.FromSeconds(30), Clock);
            revalidation = RevalidateAsync();
            try
            {
                module = await JS.InvokeAsync<IJSObjectReference>("import", lifetime.Token, "./Components/Shared/CameraArchiveView.razor.js");
                if (disposed) { await module.DisposeAsync(); module = null; return; }
                reference = DotNetObjectReference.Create(this);
                await module.InvokeVoidAsync("attach", root, reference);
            }
            catch (JSException exception) { LogBrowserUnavailable(Logger, exception); }
            catch (OperationCanceledException) when (disposed) { }
        }
        if (module is not null && !loading && restoredLocation != Navigation.Uri)
        {
            restoredLocation = Navigation.Uri;
            try { await module.InvokeVoidAsync("restoreArchiveFocus", root, Focus); }
            catch (JSException exception) { LogBrowserUnavailable(Logger, exception); }
        }
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
            catch (JSException exception) { LogBrowserUnavailable(Logger, exception); }
            catch (TaskCanceledException) { }
        }
        reference?.Dispose();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Camera archive read failed.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Debug, Message = "Camera archive browser hooks are unavailable.")]
    private static partial void LogBrowserUnavailable(ILogger logger, Exception exception);
}
