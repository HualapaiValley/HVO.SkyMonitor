using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProcessingExecutionDetailPage : ComponentBase, IAsyncDisposable
{
    private CameraAgentProcessingExecutionDetailView? _view;
    private string? _message;
    private bool _loading = true;
    private bool _notFound;
    private string? _selectedNodeId;
    private string _tab = "stage";
    private string _runSearch = string.Empty;
    private string _outcomeFilter = "all";
    private bool _requiredOnly;
    private Dictionary<Guid, long> _recentSequences = [];
    private TransientCaptureStageView? _transient;
    private CameraAgentProcessingExecutionsView? _recent;
    private bool _transientUnavailable;
    private CancellationTokenSource? _loadCancellation;
    private long _generation;

    [Parameter] public Guid ExecutionId { get; set; }

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal ICameraAgentTransientUiService TransientService { get; set; } = default!;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;

    [Inject] internal IOptions<CameraAgentHostOptions> HostOptions { get; set; } = default!;

    private bool TransientEnabled => HostOptions.Value.TransientDetection.Mode is TransientOperatingMode.Edge or TransientOperatingMode.Hybrid;
    private string LogicHostState => HostOptions.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled
        ? "Disabled"
        : "No acknowledgement recorded for this execution";
    private long? _captureSequence;

    private CameraAgentProcessingNodeView? SelectedNode => _view?.Nodes.FirstOrDefault(node => node.NodeId == _selectedNodeId);
    private string RunCaptureLabel(Guid captureId) => _recentSequences.TryGetValue(captureId, out var sequence)
        ? $"Capture #{sequence}" : $"Capture {captureId:D}";

    private string GraphRevisionLabel => _view?.Execution.GraphRevisionId is { } revision
        ? revision.Length <= 12 ? revision : revision[..12]
        : "unavailable";
    private IReadOnlyList<CameraAgentProcessingExecutionSummary> FilteredRuns => _recent is null ? [] :
        _recent.Live.Concat(_recent.Replay)
            .Where(run => (_outcomeFilter == "all" || run.Status.ToString() == _outcomeFilter) &&
                (string.IsNullOrWhiteSpace(_runSearch) ||
                    ("Capture " + ProcessingExecutionsPage.Short(run.CaptureId) + " " + run.CaptureId + " " + run.ExecutionClass + " " + run.Status)
                        .Contains(_runSearch, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(static run => run.AcceptedUtc).ToArray();

    private static string StatusGlyph(string status) => status switch
    {
        "Completed" => "✓",
        "Running" => "●",
        "Skipped" => "○",
        "Failed" or "TerminalFailure" => "!",
        _ => "·"
    };

    private string TitleStatusClass => _view?.Execution.Status switch
    {
        ProcessingGraphExecutionStatus.Completed => "run-title__glyph--completed",
        ProcessingGraphExecutionStatus.Running => "run-title__glyph--running",
        ProcessingGraphExecutionStatus.Failed or ProcessingGraphExecutionStatus.Cancelled or ProcessingGraphExecutionStatus.Expired => "run-title__glyph--failed",
        _ => "run-title__glyph--pending"
    };

    private string TransientLabel => !TransientEnabled ? "Disabled"
        : _transientUnavailable ? "State unavailable"
        : _transient is null || _transient.Events.Count == 0 ? "Not recorded for this capture"
        : $"{_transient.Events.Count} recorded milestone{(_transient.Events.Count == 1 ? "" : "s")}";

    private void SelectStageTab() => _tab = "stage";
    private void SelectArtifactsTab() => _tab = "artifacts";
    private void SelectAttemptsTab() => _tab = "attempts";

    protected override async Task OnParametersSetAsync() => await RefreshAsync().ConfigureAwait(false);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional transient, recent-run and gallery reads cannot conceal the primary execution journal.")]
    private async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        var requestedExecution = ExecutionId;
        var cancellation = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _loadCancellation, cancellation);
        if (prior is not null)
        {
            await prior.CancelAsync().ConfigureAwait(false);
            prior.Dispose();
        }
        _loading = true;
        _view = null;
        _recent = null;
        _recentSequences = [];
        _transient = null;
        _captureSequence = null;
        try
        {
            var result = await GraphService.GetExecutionDetailAsync(requestedExecution, cancellation.Token).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation) || requestedExecution != ExecutionId) return;
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                var detail = result.Value;
                _view = detail;
                _notFound = false;
                _message = null;
                if (_selectedNodeId is null || detail.Nodes.All(node => node.NodeId != _selectedNodeId))
                    _selectedNodeId = detail.Nodes.Count > 0 ? detail.Nodes[0].NodeId : null;
                _loading = false;
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                TransientCaptureStageView? transient = null;
                var transientUnavailable = false;
                if (TransientEnabled)
                {
                    try
                    {
                        var stages = await TransientService.GetCaptureStagesAsync(detail.Execution.CaptureId, cancellation.Token).ConfigureAwait(false);
                        if (stages.Kind == OperatorUiResultKind.Unauthorized)
                        {
                            NavigationManager.NavigateTo("/Account/AccessDenied");
                            return;
                        }
                        transient = stages.IsSuccess ? stages.Value : null;
                        transientUnavailable = !stages.IsSuccess;
                    }
                    catch (Exception) when (!cancellation.IsCancellationRequested)
                    {
                        transientUnavailable = true;
                    }
                }
                if (generation != Volatile.Read(ref _generation)) return;
                _transient = transient;
                _transientUnavailable = transientUnavailable;
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                CameraAgentProcessingExecutionsView? recentView;
                try
                {
                    var recent = await GraphService.GetExecutionsAsync(10, cancellation.Token).ConfigureAwait(false);
                    if (generation != Volatile.Read(ref _generation)) return;
                    recentView = recent.IsSuccess ? recent.Value : null;
                }
                catch (Exception) when (!cancellation.IsCancellationRequested)
                {
                    recentView = null;
                }
                long? sequence;
                try
                {
                    var capture = await OperatorService.GetGalleryCaptureAsync(detail.Execution.CaptureId, cancellation.Token).ConfigureAwait(false);
                    if (generation != Volatile.Read(ref _generation)) return;
                    sequence = capture.IsSuccess ? capture.Value?.CaptureSequence : null;
                }
                catch (Exception) when (!cancellation.IsCancellationRequested)
                {
                    sequence = null;
                }
                if (generation != Volatile.Read(ref _generation) || requestedExecution != ExecutionId) return;
                _recent = recentView;
                _captureSequence = sequence;
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                if (recentView is not null)
                {
                    foreach (var run in recentView.Live.Concat(recentView.Replay).Select(static run => run.CaptureId).Distinct())
                    {
                        if (generation != Volatile.Read(ref _generation) || cancellation.IsCancellationRequested) return;
                        try
                        {
                            var item = await OperatorService.GetGalleryCaptureAsync(run, cancellation.Token).ConfigureAwait(false);
                            if (generation != Volatile.Read(ref _generation) || requestedExecution != ExecutionId) return;
                            if (item.IsSuccess && item.Value is { } capture)
                            {
                                _recentSequences[run] = capture.CaptureSequence;
                                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                            }
                        }
                        catch (Exception) when (!cancellation.IsCancellationRequested)
                        {
                            // The complete capture ID remains visible when a bounded enrichment fails.
                        }
                    }
                }
            }
            else
            {
                _notFound = result.Kind == OperatorUiResultKind.NotFound;
                _view = null;
                _message = result.Message ?? "The execution could not be read.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation)) _loading = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _generation);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    internal static string NodeStatusClass(string status) => status switch
    {
        "Completed" => "state-chip--completed",
        "Running" => "state-chip--running",
        "Pending" or "RetryableFailure" => "state-chip--pending",
        "Skipped" => "state-chip--pending",
        _ => "state-chip--failed"
    };
}
