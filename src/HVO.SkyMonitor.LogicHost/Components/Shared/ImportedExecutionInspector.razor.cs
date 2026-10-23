using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

public sealed partial class ImportedExecutionInspector : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The owning load disposes each cancellation source.")]
    private CancellationTokenSource? active;
    private long generation;
    private bool disposed;
    private bool loading = true;
    private bool awaitingSession;
    private bool browserHooksAttempted;
    private string message = "Loading imported execution evidence…";
    private ImportedExecutionSnapshot? data;
    private string centralIdentity = "";
    private string? restoredLocation;
    private ElementReference root;
    private IJSObjectReference? module;
    private DotNetObjectReference<ImportedExecutionInspector>? reference;
    private PeriodicTimer? timer;
    private Task? revalidation;

    [Parameter] public Guid ObservatoryId { get; set; }
    [Parameter] public Guid CameraId { get; set; }
    [Parameter] public ImportedExecutionQuery Query { get; set; } = new();
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;
    [Inject] private ILogger<ImportedExecutionInspector> Logger { get; set; } = default!;

    private string Path => $"/app/network/observatories/{ObservatoryId:D}/cameras/{CameraId:D}/imported-executions";
    private string SessionCheckPath => Query.Installation is { } installation
        ? $"/api/internal/observatories/{ObservatoryId:D}/cameras/{CameraId:D}/installations/{installation:D}/execution-evidence/bindings" : "/api/v1.0/status/detailed";
    private string Url(ImportedExecutionQuery query) => QueryHelpers.AddQueryString(Path, new Dictionary<string, string?>
    {
        ["installation"] = query.Installation?.ToString("D"),
        ["binding"] = query.Binding?.ToString("D"),
        ["unit"] = query.Unit?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["node"] = query.Node,
        ["artifact"] = query.Artifact?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["central"] = query.Central?.ToString("D"),
        ["kind"] = query.Kind,
        ["origin"] = query.Origin,
        ["installationsPage"] = query.InstallationsPage,
        ["originsPage"] = query.OriginsPage,
        ["unitsPage"] = query.UnitsPage,
        ["artifactsPage"] = query.ArtifactsPage,
        ["nodesPage"] = query.NodesPage,
        ["attemptsPage"] = query.AttemptsPage,
        ["inputsPage"] = query.InputsPage,
        ["outputsPage"] = query.OutputsPage,
        ["centralNodesPage"] = query.CentralNodesPage,
        ["focus"] = query.Focus
    });
    private string SelectUnit(long unit) => Url(Query with
    {
        Unit = unit,
        Node = null,
        Artifact = null,
        Central = null,
        ArtifactsPage = null,
        NodesPage = null,
        AttemptsPage = null,
        InputsPage = null,
        OutputsPage = null,
        CentralNodesPage = null,
        Focus = "imported-unit-heading"
    });
    private string? Mapping(ExecutionEvidenceArtifactReferenceV1 value) => data?.Artifacts?.Artifacts
        .SingleOrDefault(a => ImportedExecutionInspectorReadService.Matches(a, value)) is { } exact
        ? Url(Query with { Artifact = exact.ArtifactReferenceId, Focus = "imported-observations-heading" }) : null;
    private static string Time(DateTimeOffset? value) => value?.ToString("u") ?? "Not recorded";
    private static string Text(string? value) => value ?? "Not recorded";
    private static string Document(System.Text.Json.JsonElement value) => value.GetRawText() is { } text && text.Length > 8192
        ? text[..8192] + "\n[Document display truncated at 8,192 characters; no complete-plan claim.]" : value.GetRawText();

    protected override void OnInitialized() => Authentication.AuthenticationStateChanged += AuthenticationChanged;
    protected override Task OnParametersSetAsync()
    {
        centralIdentity = Query.Central?.ToString("D") ?? "";
        return LoadAsync();
    }

    [JSInvokable]
    public Task RefreshAsync() => InvokeAsync(async () =>
    {
        var pending = LoadAsync();
        StateHasChanged();
        await pending;
        if (!disposed) StateHasChanged();
    });

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "All failed reads must remove protected evidence without exposing infrastructure diagnostics.")]
    private async Task LoadAsync()
    {
        if (disposed) return;
        if (module is null && RendererInfo.IsInteractive)
        {
            data = null;
            awaitingSession = !browserHooksAttempted;
            loading = awaitingSession;
            message = awaitingSession ? "Checking current browser session…" :
                "Session verification is unavailable. Reconnect or reload this page.";
            return;
        }
        var version = ++generation;
        var previous = active;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        active = cancellation;
        var token = cancellation.Token;
        var observatory = ObservatoryId;
        var camera = CameraId;
        var selection = Query;
        data = null;
        loading = true;
        message = "Loading imported execution evidence…";
        try
        {
            if (previous is not null) await previous.CancelAsync();
            var principal = (await Authentication.GetAuthenticationStateAsync()).User;
            token.ThrowIfCancellationRequested();
            if (!CentralArtifactCredentialAccess.HasOwnerCredential(principal) ||
                !CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, observatory))
            {
                if (version == generation) message = "Imported evidence not found or access was removed.";
                return;
            }
            // A circuit can outlive or differ from its browser cookie. The initial prerender is already
            // a fresh authorized request; later reads also check the current HTTP credential and actor.
            if (module is not null && !await module.InvokeAsync<bool>("hasSession", token, root, principal.Identity?.Name))
            {
                if (version == generation) message = "Session unavailable or access was removed. Sign in or reconnect, then refresh.";
                return;
            }
            token.ThrowIfCancellationRequested();
            await using var scope = ScopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IImportedExecutionInspectorReadService>()
                .ReadAsync(principal, observatory, camera, selection, token);
            token.ThrowIfCancellationRequested();
            if (disposed || version != generation) return;
            // The HTTP cookie may change while a read is in flight without notifying the circuit.
            // Verify the same browser actor again before publishing any protected snapshot.
            if (module is not null && !await module.InvokeAsync<bool>("hasSession", token, root, principal.Identity?.Name))
            {
                if (version == generation) message = "Session unavailable or access was removed. Sign in or reconnect, then refresh.";
                return;
            }
            token.ThrowIfCancellationRequested();
            if (disposed || version != generation) return;
            data = result;
            message = result is null ? "Imported evidence not found or access was removed." :
                "Retained immutable evidence. Refresh rechecks access and central observations; receipts never establish image availability.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ArgumentException)
        {
            if (version == generation) message = "This selection or continuation is unavailable. Start from the first page and select the exact identities again.";
        }
        catch (ExecutionEvidenceInvalidException)
        {
            if (version == generation) message = "This evidence filter is unavailable. Start from the first page.";
        }
        catch (Exception exception)
        {
            LogReadFailed(Logger, exception);
            if (version == generation) message = "Imported evidence is temporarily unavailable. Reconnect or refresh to try again.";
        }
        finally
        {
            if (ReferenceEquals(active, cancellation)) active = null;
            if (version == generation) loading = false;
        }
    }

    private void AuthenticationChanged(Task<AuthenticationState> state) => _ = RefreshAsync();

    [JSInvokable]
    public Task SuspendAsync() => InvokeAsync(async () =>
    {
        ++generation;
        data = null;
        loading = false;
        centralIdentity = "";
        message = "Session unavailable or access was removed. Sign in or reconnect, then refresh.";
        if (active is not null) await active.CancelAsync();
        if (!disposed) StateHasChanged();
    });
    private void Compare()
    {
        if (Guid.TryParse(centralIdentity, out var id))
            Navigation.NavigateTo(Url(Query with { Central = id, CentralNodesPage = null, Focus = "imported-comparison-heading" }));
        else message = "Enter one exact central execution UUID.";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        if (firstRender)
        {
            browserHooksAttempted = true;
            timer = new PeriodicTimer(TimeSpan.FromSeconds(30), Clock);
            revalidation = RevalidateAsync();
            try
            {
                module = await JS.InvokeAsync<IJSObjectReference>("import", lifetime.Token, "./Components/Shared/ImportedExecutionInspector.razor.js");
                if (disposed) { await module.DisposeAsync(); module = null; return; }
                reference = DotNetObjectReference.Create(this);
                await module.InvokeVoidAsync("attach", root, reference);
                if (awaitingSession)
                {
                    awaitingSession = false;
                    await RefreshAsync();
                }
            }
            catch (JSException exception)
            {
                LogBrowserUnavailable(Logger, exception);
                data = null;
                loading = false;
                message = "Session verification is unavailable. Reconnect or reload this page.";
                StateHasChanged();
            }
            catch (OperationCanceledException) when (disposed) { }
        }
        if (module is not null && !loading && restoredLocation != Navigation.Uri)
        {
            restoredLocation = Navigation.Uri;
            try { await module.InvokeVoidAsync("restoreFocus", root, Query.Focus); }
            catch (JSException exception) { LogBrowserUnavailable(Logger, exception); }
        }
    }

    private async Task RevalidateAsync()
    {
        try { while (await timer!.WaitForNextTickAsync(lifetime.Token)) await RefreshAsync(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        ++generation;
        data = null;
        Authentication.AuthenticationStateChanged -= AuthenticationChanged;
        await lifetime.CancelAsync();
        timer?.Dispose();
        if (revalidation is not null) await revalidation;
        if (module is not null)
        {
            try { await module.InvokeVoidAsync("detach", root); await module.DisposeAsync(); }
            catch (JSException exception) { LogBrowserUnavailable(Logger, exception); }
            catch (TaskCanceledException) { }
        }
        reference?.Dispose();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Imported execution inspector read failed.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Debug, Message = "Imported execution inspector browser hooks are unavailable.")]
    private static partial void LogBrowserUnavailable(ILogger logger, Exception exception);
}
