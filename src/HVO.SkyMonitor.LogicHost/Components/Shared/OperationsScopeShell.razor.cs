using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

public sealed partial class OperationsScopeShell : ComponentBase, IAsyncDisposable
{
    internal const int PageSize = 25;
    private readonly CancellationTokenSource lifetime = new();
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The owning LoadAsync disposes its cancellation source with using; this field only requests cancellation of that load.")]
    private CancellationTokenSource? loadCancellation;
    private long generation;
    private bool disposed;
    private bool refreshing;
    private ElementReference root;
    private IJSObjectReference? module;
    private DotNetObjectReference<OperationsScopeShell>? reference;
    private PeriodicTimer? timer;
    private Task? revalidation;
    private string? restoredUri;
    private ScopeState state = ScopeState.Loading;
    private OperationsObservatorySummary? observatory;
    private OperationsCameraDetail? camera;
    private IReadOnlyList<OperationsObservatorySummary> observatories = [];
    private IReadOnlyList<OperationsCameraFleetSummary> cameras = [];
    private string? nextObservatoryPage;
    private string? nextCameraPage;

    [Parameter] public Guid? ObservatoryId { get; set; }
    [Parameter] public Guid? CameraId { get; set; }
    [Parameter] public string? ObservatoryPage { get; set; }
    [Parameter] public string? CameraPage { get; set; }
    [Parameter] public string? NavigationFocus { get; set; }
    [Parameter] public string? ActiveSection { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] private IDataProtectionProvider Protection { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<OperationsScopeShell> Logger { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    private string ScopePath => CameraId is { } cameraId && ObservatoryId is { } observatoryId
        ? $"/app/network/observatories/{observatoryId:D}/cameras/{cameraId:D}"
        : ObservatoryId is { } id ? $"/app/network/observatories/{id:D}" : "/app/network";
    private string CurrentScopeUrl => PageUrl(ObservatoryPage, CameraPage);
    private string FirstPageUrl => ScopePath;
    private string NetworkUrl => Url("/app/network", ObservatoryPage, null);
    private string ObservatoryUrl(Guid id) => Url($"/app/network/observatories/{id:D}", ObservatoryPage,
        id == ObservatoryId ? CameraPage : null);
    private string CameraUrl(Guid id) => Url($"/app/network/observatories/{ObservatoryId:D}/cameras/{id:D}", ObservatoryPage, CameraPage);
    private string PageUrl(string? observatoryPage, string? cameraPage, string? focus = null)
        => Url(ScopePath, observatoryPage, cameraPage, focus);
    private static string Url(string path, string? observatoryPage, string? cameraPage, string? focus = null)
        => QueryHelpers.AddQueryString(path, new Dictionary<string, string?>
        {
            ["observatoryPage"] = observatoryPage,
            ["cameraPage"] = cameraPage,
            ["navigationFocus"] = focus
        });

    private string StateMessage => state switch
    {
        ScopeState.Denied => "Scope not found or access was removed.",
        ScopeState.InvalidPage => "This navigation page is no longer available. Start from the first page.",
        _ => "Scope navigation is temporarily unavailable. Refresh access to try again."
    };

    protected override void OnInitialized() => Authentication.AuthenticationStateChanged += AuthenticationChanged;

    protected override Task OnParametersSetAsync() => LoadAsync();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This protected UI boundary must clear labels and children on any read failure without exposing service diagnostics.")]
    private async Task LoadAsync(bool preserveView = false)
    {
        if (disposed) return;
        var version = ++generation;
        var previousCancellation = loadCancellation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loadCancellation = cancellation;
        var token = cancellation.Token;
        var requestedObservatory = ObservatoryId;
        var requestedCamera = CameraId;
        var requestedObservatoryPage = ObservatoryPage;
        var requestedCameraPage = CameraPage;
        refreshing = preserveView;
        if (!preserveView)
        {
            Clear();
            state = ScopeState.Loading;
        }
        try
        {
            if (previousCancellation is not null) await previousCancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            var principal = (await Authentication.GetAuthenticationStateAsync()).User;
            var userId = CentralArtifactCredentialAccess.GetOwnerId(principal);
            if (userId is null || requestedCamera is not null && requestedObservatory is null
                || requestedObservatory is { } scopeId && !CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, scopeId))
            {
                SetState(version, ScopeState.Denied);
                return;
            }
            var cursor = new OperationsScopeCursor(Protection);
            var observatoryCursor = cursor.Read(requestedObservatoryPage, userId, null);
            if (requestedObservatory is null && requestedCameraPage is not null)
                throw new ArgumentException("A camera page requires an observatory.");
            var cameraCursor = cursor.Read(requestedCameraPage, userId, requestedObservatory);

            // Each load has its own DbContext. Blazor can start a new navigation while an
            // older asynchronous read is completing; only the current generation may publish.
            await using var scope = ScopeFactory.CreateAsyncScope();
            var operations = scope.ServiceProvider.GetRequiredService<INetworkOperationsReadService>();
            OperationsObservatorySummary? selectedObservatory = null;
            OperationsCameraDetail? selectedCamera = null;
            if (requestedObservatory is { } observatoryId)
            {
                selectedObservatory = await operations.GetObservatoryAsync(userId, observatoryId, token);
                if (selectedObservatory is null)
                {
                    SetState(version, ScopeState.Denied);
                    return;
                }
                if (requestedCamera is { } cameraId)
                {
                    selectedCamera = await operations.GetCameraAsync(userId, cameraId, token);
                    if (selectedCamera is null || selectedCamera.ObservatoryId != observatoryId)
                    {
                        SetState(version, ScopeState.Denied);
                        return;
                    }
                }
            }
            OperationsObservatoryPage observatoryChoices;
            if (CentralArtifactCredentialAccess.GetObservatoryScope(principal) is { } credentialScope)
            {
                var allowed = selectedObservatory ?? await operations.GetObservatoryAsync(userId, credentialScope, token);
                observatoryChoices = new(allowed is null ? [] : [allowed], null);
            }
            else
            {
                observatoryChoices = await operations.ListObservatoriesAsync(userId, PageSize, observatoryCursor, token);
            }
            var cameraChoices = requestedObservatory is { } selectedId
                ? await operations.ListObservatoryCamerasAsync(userId, selectedId, PageSize, cameraCursor, token)
                : new OperationsCameraFleetPage([], null);
            token.ThrowIfCancellationRequested();
            if (version != generation) return;
            observatory = selectedObservatory;
            camera = selectedCamera;
            observatories = observatoryChoices.Items;
            cameras = cameraChoices.Items;
            nextObservatoryPage = cursor.Protect(observatoryChoices.NextCursor, userId, null);
            nextCameraPage = cursor.Protect(cameraChoices.NextCursor, userId, requestedObservatory);
            state = ScopeState.Ready;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ArgumentException)
        {
            SetState(version, ScopeState.InvalidPage);
        }
        catch (Exception exception)
        {
            LogReadFailed(Logger, exception);
            SetState(version, ScopeState.Failed);
        }
        finally
        {
            if (ReferenceEquals(loadCancellation, cancellation)) loadCancellation = null;
            if (version == generation) refreshing = false;
        }
    }

    private void SetState(long version, ScopeState value)
    {
        if (version != generation || disposed) return;
        Clear();
        state = value;
    }

    private void Clear()
    {
        observatory = null;
        camera = null;
        observatories = [];
        cameras = [];
        nextObservatoryPage = null;
        nextCameraPage = null;
    }

    [JSInvokable]
    public Task RefreshAsync() => InvokeAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync()
    {
        if (disposed || state == ScopeState.Loading || refreshing) return;
        var loading = LoadAsync(preserveView: state == ScopeState.Ready);
        StateHasChanged();
        await loading;
        if (!disposed) StateHasChanged();
    }

    private void AuthenticationChanged(Task<AuthenticationState> authenticationState)
        => _ = InvokeAsync(async () =>
        {
            var loading = LoadAsync();
            StateHasChanged();
            await loading;
            if (!disposed) StateHasChanged();
        });

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        if (firstRender)
        {
            timer = new PeriodicTimer(TimeSpan.FromSeconds(30), Clock);
            revalidation = RevalidateAsync();
            try
            {
                module = await JS.InvokeAsync<IJSObjectReference>("import", lifetime.Token, "./Components/Shared/OperationsScopeShell.razor.js");
                if (disposed)
                {
                    if (module is not null) await module.DisposeAsync();
                    return;
                }
                reference = DotNetObjectReference.Create(this);
                await module.InvokeVoidAsync("attach", root, reference);
            }
            catch (JSException exception) { LogBrowserUnavailable(Logger, exception); }
            catch (OperationCanceledException) when (disposed) { }
        }
        if (module is not null && state != ScopeState.Loading && restoredUri != Navigation.Uri)
        {
            restoredUri = Navigation.Uri;
            try { await module.InvokeVoidAsync("restoreFocus", root, NavigationFocus); }
            catch (JSException exception) { LogBrowserUnavailable(Logger, exception); }
        }
    }

    private async Task RevalidateAsync()
    {
        try
        {
            while (await timer!.WaitForNextTickAsync(lifetime.Token))
                await InvokeAsync(RefreshAsync);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operations scope navigation read failed.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Debug, Message = "Operations scope browser hooks are unavailable.")]
    private static partial void LogBrowserUnavailable(ILogger logger, Exception exception);
    private enum ScopeState { Loading, Ready, Denied, InvalidPage, Failed }
}
