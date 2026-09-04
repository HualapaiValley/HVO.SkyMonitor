using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProcessingGraphsPage : ComponentBase, IAsyncDisposable
{
    private ProcessingGraphRegistryState? _registry;
    private PendingGraphAction? _pending;
    private string? _message;
    private bool _messageIsError;
    private bool _loading = true;
    private bool _busy;
    private IJSObjectReference? _module;
    private ElementReference _confirmationDialog;
    private bool _focusConfirmation;
    private bool _restoreTriggerFocus;
    private string? _triggerId;

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    internal enum GraphAction
    {
        Validate,
        Activate,
        Rollback,
        Retire
    }

    protected override async Task OnInitializedAsync() => await RefreshAsync().ConfigureAwait(false);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusConfirmation)
        {
            _focusConfirmation = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/SchedulePage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _confirmationDialog).ConfigureAwait(false);
        }
        else if (_restoreTriggerFocus)
        {
            _restoreTriggerFocus = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/SchedulePage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", _triggerId, "page-heading").ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync()
    {
        _busy = true;
        try
        {
            var result = await GraphService.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                _registry = result.Value;
            }
            else
            {
                SetMessage(result.Message ?? "Current graph registry data is unavailable.", error: true);
            }
        }
        finally
        {
            _loading = false;
            _busy = false;
        }
    }

    private void BeginAction(GraphAction action, ProcessingGraphRevisionState revision)
    {
        if (_registry is null)
        {
            return;
        }
        // The expected version and idempotency key are fixed when the dialog opens and reused on retry.
        _pending = new PendingGraphAction(action, revision, _registry.StateVersion, NewKey());
        _triggerId = TriggerId(revision.RevisionId, action.ToString().ToUpperInvariant());
        _focusConfirmation = true;
        _message = null;
    }

    private void CancelAction()
    {
        if (_busy)
        {
            return;
        }
        _pending = null;
        _restoreTriggerFocus = true;
    }

    private async Task ConfirmActionAsync()
    {
        if (_pending is null)
        {
            return;
        }
        var pending = _pending;
        _busy = true;
        OperatorUiResultKind kind;
        string? message;
        try
        {
            var reason = string.IsNullOrWhiteSpace(pending.Reason) ? null : pending.Reason.Trim();
            (kind, message) = pending.Action switch
            {
                GraphAction.Validate => Unwrap(await GraphService.ValidateAsync(pending.Revision.RevisionId, pending.Key, reason, CancellationToken.None).ConfigureAwait(false)),
                GraphAction.Activate => Unwrap(await GraphService.ActivateAsync(pending.Revision.RevisionId, pending.ExpectedVersion, pending.Key, reason, CancellationToken.None).ConfigureAwait(false)),
                GraphAction.Rollback => Unwrap(await GraphService.RollbackAsync(pending.Revision.RevisionId, pending.ExpectedVersion, pending.Key, reason, CancellationToken.None).ConfigureAwait(false)),
                _ => Unwrap(await GraphService.RetireAsync(pending.Revision.RevisionId, pending.ExpectedVersion, pending.Key, reason, CancellationToken.None).ConfigureAwait(false))
            };
        }
        finally
        {
            _busy = false;
        }
        if (kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (kind == OperatorUiResultKind.Unavailable)
        {
            // Keep the dialog, version, and key so a retry re-sends the same command.
            SetMessage(message ?? "The graph command could not be completed. Retry sends the same command.", error: true);
            return;
        }
        _pending = null;
        _restoreTriggerFocus = true;
        if (kind == OperatorUiResultKind.Success)
        {
            await RefreshAsync().ConfigureAwait(false);
            SetMessage($"{pending.Action} applied to {pending.Revision.Name} {pending.Revision.Revision}.", error: false);
        }
        else
        {
            await RefreshAsync().ConfigureAwait(false);
            SetMessage(message ?? "The graph command was rejected.", error: true);
        }
    }

    private static (OperatorUiResultKind Kind, string? Message) Unwrap<T>(OperatorUiResult<T> result) => (result.Kind, result.Message);

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    internal static string TriggerId(string revisionId, string action)
        => $"graph-{action.ToUpperInvariant()}-{revisionId}";

    private static string NewKey() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    internal static string Short(string value) => value.Length <= 12 ? value : value[..12];

    internal static string LifecycleClass(ProcessingGraphRevisionLifecycle lifecycle) => lifecycle switch
    {
        ProcessingGraphRevisionLifecycle.Active => "state-chip--active",
        ProcessingGraphRevisionLifecycle.Validated => "state-chip--validated",
        ProcessingGraphRevisionLifecycle.Draft => "state-chip--draft",
        _ => "state-chip--retired"
    };

    private static string Split(string value) => OperationsPage.SplitWords(value);

    public async ValueTask DisposeAsync()
    {
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

    internal sealed class PendingGraphAction(GraphAction action, ProcessingGraphRevisionState revision, long expectedVersion, string key)
    {
        public GraphAction Action { get; } = action;
        public ProcessingGraphRevisionState Revision { get; } = revision;
        public long ExpectedVersion { get; } = expectedVersion;
        public string Key { get; } = key;
        public string? Reason { get; set; }

        public string Heading => Action switch
        {
            GraphAction.Validate => "Validate this draft?",
            GraphAction.Activate => "Activate this revision?",
            GraphAction.Rollback => "Roll back to this revision?",
            _ => "Retire this revision?"
        };

        public string Description => Action switch
        {
            GraphAction.Validate => "Validation compiles the immutable definition against this CameraAgent and records the result. No live behaviour changes.",
            GraphAction.Activate => "The current live capture finishes before this revision becomes the single active graph. Retained evidence is never rewritten.",
            GraphAction.Rollback => "The previously retired revision becomes the single active graph again at the next capture boundary.",
            _ => "A retired revision can no longer be activated. Executions that already used it keep their lineage."
        };

        public string ConfirmLabel => Action switch
        {
            GraphAction.Validate => "Confirm validate",
            GraphAction.Activate => "Confirm activate",
            GraphAction.Rollback => "Confirm rollback",
            _ => "Confirm retire"
        };
    }
}
