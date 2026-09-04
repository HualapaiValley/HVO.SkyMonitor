using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// Read-mostly view of one durable replay execution: progress, ordered node evidence, terminal
/// results, and a confirmed idempotent cancellation. Polling stops at the terminal state and on
/// dispose, artifact content is never prefetched, and no upload, promotion, or gallery-preference
/// action is exposed.
/// </summary>
public sealed partial class ReplayExecutionPage : ComponentBase, IAsyncDisposable
{
    /// <summary>Replay progress polling never runs faster than this; live acquisition keeps priority.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private ReplayExecutionView? _execution;
    private ReplayRunnerFactsView _runnerFacts = default!;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private IJSObjectReference? _module;
    private ElementReference _confirmationDialog;
    private Guid _loadedExecutionId;
    private string? _message;
    private string? _cancelKey;
    private bool _messageIsError;
    private bool _loading = true;
    private bool _busy;
    private bool _confirming;
    private bool _focusConfirmation;
    private bool _restoreTriggerFocus;

    [Inject] internal ICameraAgentReplayUiService ReplayService { get; set; } = default!;

    [Inject] internal CameraAgentReplayRunnerFactsProjection RunnerFacts { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    [Parameter] public Guid ExecutionId { get; set; }

    private string SourceCaptureUrl => _execution is null || _execution.CaptureId == Guid.Empty
        ? "/gallery"
        : $"/gallery/{_execution.CaptureId:D}";

    protected override async Task OnParametersSetAsync()
    {
        _runnerFacts = RunnerFacts.Create();
        if (_loadedExecutionId == ExecutionId && !_loading)
        {
            return;
        }
        _loadedExecutionId = ExecutionId;
        await StopPollingAsync().ConfigureAwait(false);
        _loading = true;
        await LoadAsync(reportFailure: true).ConfigureAwait(false);
        _loading = false;
        StartPolling();
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
            await InvokeModuleAsync("focusById", "replay-cancel-trigger", "replay-execution-heading")
                .ConfigureAwait(false);
        }
    }

    /// <summary>The only route replay output content is reachable through: execution-scoped and authorized.</summary>
    private string OutputContentUrl(Guid artifactId)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"/api/v1/operations/processing-graphs/executions/{ExecutionId:D}/outputs/{artifactId:D}/content");

    private async Task RefreshAsync()
    {
        _busy = true;
        try
        {
            await LoadAsync(reportFailure: true).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        StartPolling();
    }

    private async Task LoadAsync(bool reportFailure)
    {
        var result = await ReplayService.GetReplayExecutionAsync(ExecutionId, CancellationToken.None)
            .ConfigureAwait(false);
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            _execution = null;
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (result.IsSuccess && result.Value is { } execution)
        {
            _execution = execution;
            if (reportFailure)
            {
                _message = null;
                _messageIsError = false;
            }
            return;
        }
        if (reportFailure)
        {
            _execution = null;
            _message = result.Message ?? "Replay execution progress is unavailable.";
            _messageIsError = true;
        }
    }

    private void StartPolling()
    {
        if (_execution is null || _execution.IsTerminal || _pollCancellation is not null)
        {
            return;
        }
        var cancellation = new CancellationTokenSource();
        // The guard above means there is no prior source; the exchange keeps ownership single-valued.
        Interlocked.Exchange(ref _pollCancellation, cancellation)?.Dispose();
        _pollTask = PollAsync(cancellation.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var result = await ReplayService.GetReplayExecutionAsync(ExecutionId, cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (result.IsSuccess && result.Value is { } execution)
                {
                    _execution = execution;
                    await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                    if (execution.IsTerminal)
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task StopPollingAsync()
    {
        var cancellation = Interlocked.Exchange(ref _pollCancellation, null);
        var poll = Interlocked.Exchange(ref _pollTask, null);
        if (cancellation is null)
        {
            return;
        }
        await cancellation.CancelAsync().ConfigureAwait(false);
        if (poll is not null)
        {
            try
            {
                await poll.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        cancellation.Dispose();
    }

    private void BeginCancel()
    {
        if (_execution is null || _execution.IsTerminal)
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

    private async Task ConfirmCancelAsync()
    {
        if (_execution is null)
        {
            return;
        }
        // One durable cancellation key per execution: a retry is idempotent, never a second command.
        _cancelKey ??= Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _busy = true;
        OperatorUiResult<ReplayExecutionView> result;
        try
        {
            result = await ReplayService.CancelReplayAsync(
                _execution.ExecutionId,
                _cancelKey,
                "operator cancellation",
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        _confirming = false;
        _restoreTriggerFocus = true;
        if (result.IsSuccess && result.Value is { } execution)
        {
            _execution = execution;
            _message = "Cancellation was recorded against the durable replay request.";
            _messageIsError = false;
            if (execution.IsTerminal)
            {
                await StopPollingAsync().ConfigureAwait(false);
            }
        }
        else
        {
            _message = result.Message ?? "The replay cancellation could not be completed.";
            _messageIsError = true;
        }
    }

    private async Task InvokeModuleAsync(string identifier, params object?[] arguments)
    {
        _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/ReplayExecutionPage.razor.js").ConfigureAwait(false);
        await _module.InvokeVoidAsync(identifier, arguments).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var cancellation = Interlocked.Exchange(ref _pollCancellation, null);
        var poll = Interlocked.Exchange(ref _pollTask, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
        if (poll is not null)
        {
            try
            {
                await poll.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
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
}
