using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class QuarantinePage : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private OperatorOutboxPage? _page;
    private PendingAction? _pending;
    private IJSObjectReference? _module;
    private ElementReference _dialog;
    private string? _reasonCode;
    private string? _errorMessage;
    private string? _commandError;
    private string? _statusMessage;
    private string? _deploymentRunId;
    private string? _inventorySha256;
    private long _generation;
    private bool _isLoading;
    private bool _isSubmitting;
    private bool _showDialog;
    private bool _ownershipAcknowledged;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    [Parameter, SupplyParameterFromQuery(Name = "kind")] public string? Kind { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "alias")] public string? StorageAlias { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "cursor")] public string? Cursor { get; set; }

    protected override Task OnParametersSetAsync() => LoadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_showDialog)
        {
            return;
        }
        _showDialog = false;
        _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/QuarantinePage.razor.js");
        await _module.InvokeVoidAsync("showModal", _dialog);
    }

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
        try
        {
            var result = await OperatorService.GetQuarantinePageAsync(
                string.IsNullOrWhiteSpace(Kind) ? "Artifact" : Kind,
                StorageAlias,
                Cursor,
                25,
                cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                ClearRenderedData();
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _page = result.Value;
            }
            else
            {
                _page = null;
                _errorMessage = result.Message ?? "The quarantine page is temporarily unavailable.";
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

    private void BeginAction(OperatorOutboxActionRequest request)
    {
        if (_isSubmitting)
        {
            return;
        }
        _pending = new PendingAction(
            request.Item,
            request.Action,
            request.TriggerId,
            request.Action == OutboxOperationAction.Replay ? request.Item.ReplayToken! : request.Item.AbandonToken!,
            $"ui-{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}");
        _reasonCode = ReasonCodes[0].Code;
        _commandError = null;
        _statusMessage = null;
        ClearOwnershipEvidence();
        _showDialog = true;
    }

    private async Task BindOwnershipAsync()
    {
        if (_pending is null || _pending.Item.Kind != "TransientRuntime" || _pending.OwnershipBound ||
            _isSubmitting || _loadCancellation is null)
        {
            return;
        }
        _isSubmitting = true;
        _commandError = null;
        try
        {
            var result = await OperatorService.BindTransientRuntimeOwnershipAsync(
                _pending.ActionToken,
                _deploymentRunId ?? string.Empty,
                _inventorySha256 ?? string.Empty,
                _ownershipAcknowledged,
                _loadCancellation.Token);
            if (result.IsSuccess && result.Value is not null)
            {
                _deploymentRunId = result.Value.DeploymentRunId;
                _inventorySha256 = result.Value.InventorySha256;
                _pending = _pending with { ActionToken = result.Value.ActionToken, OwnershipBound = true };
            }
            else if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                ClearRenderedData();
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else
            {
                _commandError = result.Message ?? "The external ownership evidence could not be bound.";
            }
        }
        catch (OperationCanceledException) when (_loadCancellation?.IsCancellationRequested == true)
        {
        }
        finally
        {
            _isSubmitting = false;
        }
    }

    private async Task ConfirmAsync()
    {
        if (_pending is null || _isSubmitting || _loadCancellation is null ||
            _pending.Item.Kind == "TransientRuntime" && !_pending.OwnershipBound)
        {
            return;
        }
        var command = _pending;
        _isSubmitting = true;
        _commandError = null;
        try
        {
            var result = await OperatorService.ResolveOutboxAsync(
                command.Item.Kind,
                command.Action,
                command.ActionToken,
                _reasonCode!,
                command.IdempotencyKey,
                _loadCancellation.Token);
            if (result.IsSuccess && result.Value is not null)
            {
                await CloseAsync(command.TriggerId);
                _pending = null;
                _reasonCode = null;
                ClearOwnershipEvidence();
                _statusMessage = $"{result.Value.Action}: {result.Value.Disposition}. Current state: {result.Value.State}.";
                await LoadAsync();
            }
            else if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                ClearRenderedData();
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else
            {
                _commandError = result.Message ?? "The outbox command could not be completed.";
            }
        }
        catch (OperationCanceledException) when (_loadCancellation?.IsCancellationRequested == true)
        {
        }
        finally
        {
            _isSubmitting = false;
        }
    }

    private async Task CancelAsync()
    {
        var triggerId = _pending?.TriggerId;
        await CloseAsync(triggerId);
        _pending = null;
        _reasonCode = null;
        _commandError = null;
        ClearOwnershipEvidence();
    }

    private async Task CloseAsync(string? triggerId)
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("close", _dialog, triggerId, "mainContent");
        }
    }

    private Task ReloadAsync() => LoadAsync();

    internal Task RefreshAuthorizationAsync() => LoadAsync();

    private void ShowNewest() => NavigateToCursor(null);

    private void ShowNext() => NavigateToCursor(_page?.NextCursor);

    private void NavigateToCursor(string? cursor) =>
        NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameter("cursor", cursor));

    private string AliasUrl(string alias) => NavigationManager.GetUriWithQueryParameters(
        "/operations/quarantine",
        new Dictionary<string, object?> { ["alias"] = alias });

    private string SourceClass(string kind) =>
        string.Equals(_page?.Kind ?? (string.IsNullOrWhiteSpace(Kind) ? "Artifact" : Kind), kind, StringComparison.OrdinalIgnoreCase)
            ? "active"
            : string.Empty;

    private string? SourceCurrent(string kind) => SourceClass(kind) == "active" ? "page" : null;

    private string ListTitle => _page?.Kind == "Environmental"
        ? "Environmental quarantine and terminal records"
        : _page?.Kind == "TransientRuntime"
            ? "Transient runtime quarantine"
            : $"Artifact quarantine / {_page?.StorageAlias ?? "no configured storage"}";

    private string ConfirmationDescription => _pending?.Item.Kind == "TransientRuntime"
        ? "Abandonment records an audited loss decision for this exact immutable source identity before recomputing its retention hold. Other required holds remain unchanged."
        : _pending?.Action == OutboxOperationAction.Abandon
            ? "Abandonment releases the durable delivery hold and stops delivery by this outbox."
            : "Replay returns the item to bounded delivery using its existing durable evidence.";

    private void ClearRenderedData()
    {
        _page = null;
        _pending = null;
        _reasonCode = null;
        _errorMessage = null;
        _commandError = null;
        _statusMessage = null;
        _deploymentRunId = null;
        _inventorySha256 = null;
        _ownershipAcknowledged = false;
        _showDialog = false;
    }

    private void ClearOwnershipEvidence()
    {
        _deploymentRunId = null;
        _inventorySha256 = null;
        _ownershipAcknowledged = false;
    }

    private static IReadOnlyList<(string Code, string Label)> ReplayReasons { get; } =
    [
        ("configuration-corrected", "Configuration corrected"),
        ("evidence-restored", "Evidence restored"),
        ("upstream-recovered", "Upstream recovered")
    ];

    private static IReadOnlyList<(string Code, string Label)> AbandonReasons { get; } =
    [
        ("invalid-source", "Invalid source"),
        ("irrecoverable-evidence", "Irrecoverable evidence"),
        ("operator-approved-loss", "Operator-approved loss")
    ];

    private IReadOnlyList<(string Code, string Label)> ReasonCodes =>
        _pending?.Action == OutboxOperationAction.Abandon ? AbandonReasons : ReplayReasons;

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _generation);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private sealed record PendingAction(
        OperatorOutboxItem Item,
        OutboxOperationAction Action,
        string TriggerId,
        string ActionToken,
        string IdempotencyKey,
        bool OwnershipBound = false);
}
