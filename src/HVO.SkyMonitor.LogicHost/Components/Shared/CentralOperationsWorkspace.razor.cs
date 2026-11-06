using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

public sealed partial class CentralOperationsWorkspace : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The owning load disposes each cancellation source.")]
    private CancellationTokenSource? active;
    private long generation;
    private bool disposed;
    private bool loading = true;
    private bool awaitingSession;
    private bool browserHooksAttempted;
    private string message = "Loading central operation evidence…";
    private CentralOperationsSnapshot? data;
    private int? cloudThreshold;
    private string validation = "inherit";
    private bool busy;
    private string? outcome;
    private string? policyIdentity;
    private string? restoredLocation;
    private ElementReference root;
    private IJSObjectReference? module;
    private DotNetObjectReference<CentralOperationsWorkspace>? reference;
    private PeriodicTimer? timer;
    private Task? revalidation;

    [Parameter] public Guid ObservatoryId { get; set; }
    [Parameter] public Guid CameraId { get; set; }
    [Parameter] public CentralOperationsQuery Query { get; set; } = new();
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;
    [Inject] private ILogger<CentralOperationsWorkspace> Logger { get; set; } = default!;

    private string Path => $"/app/network/observatories/{ObservatoryId:D}/cameras/{CameraId:D}/central";
    private string SessionCheckPath => $"/api/v1.0/observatories/{ObservatoryId:D}/cameras/{CameraId:D}/processing-graph-executions?take=1";
    private string Url(CentralOperationsQuery query) => QueryHelpers.AddQueryString(Path, new Dictionary<string, string?>
    {
        ["view"] = query.View,
        ["target"] = query.Target,
        ["page"] = query.Page,
        ["execution"] = query.Execution?.ToString("D"),
        ["job"] = query.Job?.ToString("D"),
        ["proposal"] = query.Proposal?.ToString("D"),
        ["revision"] = query.Revision?.ToString("D"),
        ["event"] = query.Event?.ToString("D"),
        ["capture"] = query.Capture?.ToString("D"),
        ["nodesPage"] = query.NodesPage,
        ["attemptsPage"] = query.AttemptsPage,
        ["inputsPage"] = query.InputsPage,
        ["outputsPage"] = query.OutputsPage,
        ["factsPage"] = query.FactsPage,
        ["request"] = query.Request,
        ["focus"] = query.Focus,
    });
    private string Section(string view) => Url(new() { View = view is "executions" or "jobs" or "graphs" or "catalog" or "delivery" or "events" or "policy" ? view : "executions", Focus = "central-heading" });
    private string SelectExecution(Guid id) => Url((Query.View == "executions" ? Query : new CentralOperationsQuery()) with { Execution = id, NodesPage = null, Request = Guid.NewGuid().ToString("N"), Focus = "central-execution-heading" });
    private string SelectJob(Guid id) => Url(new() { View = "jobs", Job = id, Focus = "central-job-heading" });
    private string Artifacts(Guid capture) => Url(new() { View = "artifacts", Capture = capture, Focus = "central-artifacts-heading" });
    private bool CanManage => data?.Role is HVO.SkyMonitor.LogicHost.Data.ObservatoryMembershipRole.Manager or HVO.SkyMonitor.LogicHost.Data.ObservatoryMembershipRole.Owner;
    private bool CanCancel => data?.Execution is { } execution && (CanManage || execution.Execution.ExecutionClass == "Replay" &&
        execution.Execution.ActorId == actor);
    private string? actor;
    private static string Time(DateTimeOffset? value) => value?.ToString("u") ?? "Not recorded";
    private static string Text(string? value) => value ?? "Not recorded";
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static string Document(object value) => System.Text.Json.JsonSerializer.Serialize(value, JsonOptions);
    private static string Definition(System.Text.Json.JsonElement value) => value.GetRawText() is { } text && text.Length > 8192
        ? text[..8192] + "\n[Definition display truncated at 8,192 characters.]" : value.GetRawText();

    protected override void OnInitialized() => Authentication.AuthenticationStateChanged += AuthenticationChanged;
    protected override Task OnParametersSetAsync()
    {
        outcome = null;
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
        message = "Loading central operation evidence…";
        try
        {
            if (previous is not null) await previous.CancelAsync();
            var principal = (await Authentication.GetAuthenticationStateAsync()).User;
            actor = CentralArtifactCredentialAccess.GetOwnerId(principal);
            token.ThrowIfCancellationRequested();
            if (!CentralArtifactCredentialAccess.HasOwnerCredential(principal) ||
                !CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, observatory))
            {
                if (version == generation) message = "Central operations not found or access was removed.";
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
            var result = await scope.ServiceProvider.GetRequiredService<ICentralOperationsWorkspaceService>()
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
            message = result is null ? "Central operations not found or access was removed." :
                "Retained central facts. Refresh rechecks access and current state; received CameraAgent history remains immutable.";
            var currentPolicy = $"{observatory:D}/{camera:D}/{result?.Policy?.Version}";
            if (result?.Policy is { } policy && policyIdentity != currentPolicy)
            {
                policyIdentity = currentPolicy;
                cloudThreshold = policy.OverrideCloudTransmissionThresholdMillionths;
                validation = policy.OverrideCentralValidationEnabled switch { true => "enabled", false => "disabled", _ => "inherit" };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ArgumentException)
        {
            if (version == generation) message = "This selection or continuation is unavailable. Start from the first page and select the exact identities again.";
        }
        catch (Exception exception)
        {
            LogReadFailed(Logger, exception);
            if (version == generation) message = "Central operations is temporarily unavailable. Reconnect or refresh to try again.";
        }
        finally
        {
            if (ReferenceEquals(active, cancellation)) active = null;
            if (version == generation) loading = false;
        }
    }

    private void AuthenticationChanged(Task<AuthenticationState> state)
    {
        outcome = null;
        _ = RefreshAsync();
    }

    [JSInvokable]
    public Task SuspendAsync() => InvokeAsync(async () =>
    {
        ++generation;
        data = null;
        loading = false;
        cloudThreshold = null;
        validation = "inherit";
        outcome = null;
        policyIdentity = null;
        message = "Session unavailable or access was removed. Sign in or reconnect, then refresh.";
        if (active is not null) await active.CancelAsync();
        if (!disposed) StateHasChanged();
    });
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed UI mutation must clear protected data and report uncertain outcomes without infrastructure diagnostics.")]
    private async Task MutateAsync(CentralWorkspaceAction action, long renderedGeneration, Guid? artifact = null)
    {
        if (busy || disposed || data is null || renderedGeneration != generation) return;
        var selection = Query;
        var observatory = ObservatoryId;
        var camera = CameraId;
        var version = generation;
        var command = new CentralWorkspaceCommand(action, selection,
            action == CentralWorkspaceAction.ReplayExecution ? selection.Request ?? "" :
                action is CentralWorkspaceAction.ConfirmEvent or CentralWorkspaceAction.RejectEvent
                    ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{data.Event?.Summary.CentralTransientEventId:D}/{data.Event?.Summary.ETag}/{action}")))
                    : Guid.NewGuid().ToString("N"),
            data.Event?.Summary.ETag, data.Event?.Summary.ActiveAssessmentId, artifact,
            action == CentralWorkspaceAction.SavePolicy ? new(cloudThreshold, validation switch { "enabled" => true, "disabled" => false, _ => (bool?)null }, "central-workspace-policy") : null);
        busy = true;
        outcome = null;
        try
        {
            var principal = (await Authentication.GetAuthenticationStateAsync()).User;
            if (module is null || !await module.InvokeAsync<bool>("hasSession", lifetime.Token, root, principal.Identity?.Name))
            {
                await SuspendAsync();
                return;
            }
            if (disposed || version != generation) return;
            await using var scope = ScopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ICentralOperationsWorkspaceMutationService>()
                .ExecuteAsync(principal, observatory, camera, command, lifetime.Token);
            if (!disposed && version == generation)
            {
                outcome = result;
                await LoadAsync();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            LogReadFailed(Logger, exception);
            if (!disposed && version == generation)
            {
                data = null;
                message = "Operation outcome is unavailable. Refresh access and inspect durable state. Reuse the displayed replay request key for an uncertain replay.";
            }
        }
        finally { busy = false; }
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
                module = await JS.InvokeAsync<IJSObjectReference>("import", lifetime.Token, "./Components/Shared/CentralOperationsWorkspace.razor.js");
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
        try { while (await timer!.WaitForNextTickAsync(lifetime.Token)) { if (!busy) await RefreshAsync(); } }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Central operations workspace read failed.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Debug, Message = "Central operations workspace browser hooks are unavailable.")]
    private static partial void LogBrowserUnavailable(ILogger logger, Exception exception);
}
