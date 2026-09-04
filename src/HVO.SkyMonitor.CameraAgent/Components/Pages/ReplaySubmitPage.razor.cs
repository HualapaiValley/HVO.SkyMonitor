using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Components.Shared;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// Archive-to-replay entry: reviews the inputs a replay request will freeze, chooses an eligible
/// immutable graph revision, and submits one durable request behind an explicit confirmation.
/// The page never prefetches artifact content and exposes no upload, promotion, or publication action.
/// </summary>
public sealed partial class ReplaySubmitPage : ComponentBase, IAsyncDisposable
{
    private ReplayCandidateView? _candidate;
    private ReplayRunnerFactsView _runnerFacts = default!;
    private ReplaySubmissionOutcome? _outcome;
    private Guid? _submittedExecutionId;
    private IJSObjectReference? _module;
    private ElementReference _confirmationDialog;
    private string? _revisionId;
    private string _reason = string.Empty;
    private string? _message;
    private string? _submitKey;
    private string? _submitSignature;
    private bool _messageIsError;
    private bool _loading = true;
    private bool _busy;
    private bool _confirming;
    private bool _focusConfirmation;
    private bool _restoreTriggerFocus;
    private bool _accessDenied;

    [Inject] internal ICameraAgentReplayUiService ReplayService { get; set; } = default!;

    [Inject] internal CameraAgentReplayRunnerFactsProjection RunnerFacts { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    [Parameter, SupplyParameterFromQuery(Name = "captureId")]
    public Guid CaptureId { get; set; }

    private string SourceCaptureUrl => CaptureId == Guid.Empty
        ? "/gallery"
        : $"/gallery/{CaptureId:D}";

    private PageStateNotice.PageStateKind OutcomeKind => _outcome == ReplaySubmissionOutcome.Accepted
        ? PageStateNotice.PageStateKind.Info
        : PageStateNotice.PageStateKind.Stale;

    private string OutcomeTitle => _outcome == ReplaySubmissionOutcome.Accepted
        ? "Replay request accepted"
        : "Existing replay request returned";

    protected override async Task OnParametersSetAsync()
    {
        // The capture identity arrives as a query parameter, so a redirect re-supplies parameters.
        // Once authorization has been denied the page must not read or redirect again.
        if (_accessDenied)
        {
            return;
        }
        _runnerFacts = RunnerFacts.Create();
        await LoadAsync().ConfigureAwait(false);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusConfirmation)
        {
            _focusConfirmation = false;
            await InvokeModuleAsync("showModal", _confirmationDialog).ConfigureAwait(false);
        }
        else if (_restoreTriggerFocus)
        {
            _restoreTriggerFocus = false;
            await InvokeModuleAsync("focusById", "replay-submit-trigger", "replay-submit-heading")
                .ConfigureAwait(false);
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _message = null;
        _messageIsError = false;
        _outcome = null;
        _submittedExecutionId = null;
        try
        {
            var result = await ReplayService.GetReplayCandidateAsync(CaptureId, CancellationToken.None)
                .ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _candidate = null;
                _accessDenied = true;
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (result.IsSuccess && result.Value is { } candidate)
            {
                _candidate = candidate;
                _revisionId = candidate.EligibleRevisions
                        .FirstOrDefault(static revision => revision.IsActive)?.RevisionId
                    ?? (candidate.EligibleRevisions.Count > 0 ? candidate.EligibleRevisions[0].RevisionId : null);
            }
            else
            {
                _candidate = null;
                _message = result.Message ?? "The replay freeze summary is unavailable.";
                _messageIsError = true;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void RevisionChanged(ChangeEventArgs eventArgs)
    {
        _revisionId = eventArgs?.Value?.ToString();
        ClearOutcome();
    }

    private void ReasonChanged() => ClearOutcome();

    private void ClearOutcome()
    {
        _outcome = null;
        _submittedExecutionId = null;
        _message = null;
        _messageIsError = false;
    }

    private void BeginSubmit()
    {
        if (_revisionId is null)
        {
            return;
        }
        _confirming = true;
        _focusConfirmation = true;
    }

    private void CancelConfirmation()
    {
        if (_busy)
        {
            return;
        }
        _confirming = false;
        _restoreTriggerFocus = true;
    }

    private async Task ConfirmSubmitAsync()
    {
        if (_candidate is null || _revisionId is null)
        {
            return;
        }
        var reason = string.IsNullOrWhiteSpace(_reason) ? null : _reason.Trim();
        var signature = string.Join('\n', _candidate.CaptureId.ToString("N"), _revisionId, reason);
        if (!string.Equals(_submitSignature, signature, StringComparison.Ordinal))
        {
            _submitSignature = signature;
            _submitKey = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        }
        _busy = true;
        OperatorUiResult<ReplaySubmissionView> result;
        try
        {
            result = await ReplayService.SubmitReplayAsync(
                _candidate.CaptureId,
                _revisionId,
                _submitKey!,
                reason,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            _accessDenied = true;
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        // A retryable rejection keeps the idempotency key so a retry cannot create a second request.
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _submitKey = null;
            _submitSignature = null;
        }
        _confirming = false;
        _restoreTriggerFocus = true;
        if (result.IsSuccess && result.Value is { } submission)
        {
            _outcome = submission.Outcome;
            _submittedExecutionId = submission.Execution.ExecutionId;
            _message = submission.Outcome == ReplaySubmissionOutcome.Accepted
                ? "The replay request was accepted and queued as a separate replay execution."
                : "An identical request already exists. The durable replay execution was returned unchanged; no second request was created.";
            _messageIsError = false;
        }
        else
        {
            _outcome = null;
            _submittedExecutionId = null;
            _message = result.Message ?? "The replay request could not be submitted.";
            _messageIsError = true;
        }
    }

    private async Task InvokeModuleAsync(string identifier, params object?[] arguments)
    {
        _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/ReplaySubmitPage.razor.js").ConfigureAwait(false);
        await _module.InvokeVoidAsync(identifier, arguments).ConfigureAwait(false);
    }

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
}
