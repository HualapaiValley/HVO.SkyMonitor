using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

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
    private CameraAgentGalleryCapture? _capture;
    private TransientStageEvent? _selectedTransient;
    private ElementReference _panel;
    private IJSObjectReference? _module;
    private Task<IJSObjectReference>? _moduleImport;
    private bool _disposed;

    [Parameter] public Guid ExecutionId { get; set; }

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal ICameraAgentTransientUiService TransientService { get; set; } = default!;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;

    [Inject] internal IOptions<CameraAgentHostOptions> HostOptions { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    private bool TransientEnabled => HostOptions.Value.TransientDetection.Mode is TransientOperatingMode.Edge or TransientOperatingMode.Hybrid;
    private string LogicHostState => HostOptions.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled
        ? "Disabled now / no receipt"
        : "No acknowledgement recorded for this execution";
    private long? _captureSequence;
    private string TriggerLabel => _view?.Execution is { } execution
        ? $"{(execution.TriggerReference is { } reference ? reference : "Recorded capture trigger")} at {execution.AcceptedUtc:HH:mm:ss} UTC"
        : "Unavailable";
    private string ScheduleLabel => _capture?.Detail?.Schedule is { } schedule
        ? $"schedule {ShortIdentity(schedule.RevisionId)} / {schedule.SetpointProfileId} / {schedule.Reason}"
        : "schedule identity unavailable";
    private static string ShortIdentity(string identity) => identity.Length <= 12 ? identity : identity[..12];

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
                    (RunCaptureLabel(run.CaptureId) + " " + ProcessingExecutionsPage.Short(run.CaptureId) + " " + run.CaptureId + " " + run.ExecutionClass + " " + run.Status)
                        .Contains(_runSearch, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(static run => run.AcceptedUtc).ToArray();

    private string TitleStatusClass => _view?.Execution.Status switch
    {
        ProcessingGraphExecutionStatus.Completed => "run-title__glyph--completed",
        ProcessingGraphExecutionStatus.Running => "run-title__glyph--running",
        ProcessingGraphExecutionStatus.Failed or ProcessingGraphExecutionStatus.Cancelled or ProcessingGraphExecutionStatus.Expired => "run-title__glyph--failed",
        _ => "run-title__glyph--pending"
    };

    private void SelectStageTab() => _tab = "stage";
    private void SelectArtifactsTab() => _tab = "artifacts";
    private void SelectAttemptsTab() => _tab = "attempts";
    private void SelectNode(string id) { _selectedNodeId = id; _selectedTransient = null; }
    private void SelectTransient(TransientStageEvent stage) { _selectedTransient = stage; _selectedNodeId = null; }

    private static string FormatBytes(long bytes) => bytes >= 1_048_576
        ? $"{(bytes / 1_048_576d).ToString("0.##", CultureInfo.InvariantCulture)} MiB"
        : $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} B";

    /// <summary>The recorded retention state of one artifact, distinct from its content availability.</summary>
    private string RetentionLabel(Guid artifactId) =>
        _capture?.Detail?.ArtifactStates.FirstOrDefault(state => state.ArtifactId == artifactId)?.RetentionState ?? "Not recorded";

    private static StageFact[] StageFacts(CameraAgentProcessingNodeView node)
    {
        var facts = new List<StageFact>();
        if (node.StartedUtc is { } start) facts.Add(new(start, "Started", "running", "Stage start recorded in the execution journal."));
        foreach (var attempt in node.Attempts)
        {
            facts.Add(new(attempt.StartedUtc, $"Attempt {attempt.AttemptNumber}", "running", $"Recorded {attempt.ExecutionRoute} attempt start."));
            if (attempt.CompletedUtc is { } completed)
                facts.Add(new(completed, PipelineRunPresentation.StatusLabel(attempt.Status), PipelineRunPresentation.StatusClass(attempt.Status),
                    attempt.Reason ?? attempt.Outcome ?? "Attempt completion recorded; no detailed outcome retained."));
        }
        if (node.CompletedUtc is { } end) facts.Add(new(end, PipelineRunPresentation.StatusLabel(node.Status), PipelineRunPresentation.StatusClass(node.Status), $"Recorded node state: {node.Status}."));
        if (node.Reason is { } reason) facts.Add(new(node.CompletedUtc, "Reason", "warning", reason));
        if (facts.Count == 0) facts.Add(new(null, "Unavailable", "pending", "No start, attempt or completion facts recorded."));
        return facts.OrderBy(static fact => fact.Time).ToArray();
    }

    private async Task TabKeyAsync(KeyboardEventArgs args, int index)
    {
        var next = args.Key switch { "ArrowRight" => (index + 1) % 3, "ArrowLeft" => (index + 2) % 3, "Home" => 0, "End" => 2, _ => -1 };
        if (next < 0) return;
        _tab = new[] { "stage", "artifacts", "attempts" }[next];
        await InvokeAsync(StateHasChanged);
        if (_module is not null)
        {
            try { await _module.InvokeVoidAsync("focusTab", _panel, $"run-tab-{_tab}"); }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // One in-flight import is shared by every render; overlapping renders must not each
        // start a new module handle that is then overwritten without being disposed.
        if (_module is not null || _moduleImport is not null || _disposed || _view is null) return;
        var import = JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/ProcessingExecutionDetailPage.razor.js").AsTask();
        _moduleImport = import;
        try
        {
            // Stay on the renderer thread so the disposal check and the field assignment keep the
            // same ordering as the rest of the component lifecycle.
            var module = await import;
            if (_disposed) await module.DisposeAsync();
            else _module = module;
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (TaskCanceledException) { }
        finally { _moduleImport = null; }
    }

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
        _capture = null;
        _selectedTransient = null;
        _transientUnavailable = false;
        try
        {
            var result = await GraphService.GetExecutionDetailAsync(requestedExecution, cancellation.Token).ConfigureAwait(false);
            if (!IsCurrent(generation, requestedExecution, cancellation)) return;
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                RevokeAccess();
                return;
            }
            if (result.IsSuccess && result.Value is not null && result.Value.Execution.ExecutionId == requestedExecution)
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
                // Recorded milestones remain inspectable after a later configuration disables acquisition.
                try
                {
                    var stages = await TransientService.GetCaptureStagesAsync(detail.Execution.CaptureId, cancellation.Token).ConfigureAwait(false);
                    if (!IsCurrent(generation, requestedExecution, cancellation)) return;
                    if (stages.Kind == OperatorUiResultKind.Unauthorized)
                    {
                        RevokeAccess();
                        return;
                    }
                    transient = stages.IsSuccess && stages.Value?.CaptureId == detail.Execution.CaptureId ? stages.Value : null;
                    transientUnavailable = !stages.IsSuccess || stages.Value?.CaptureId != detail.Execution.CaptureId;
                }
                catch (Exception) when (!cancellation.IsCancellationRequested)
                {
                    transientUnavailable = true;
                }
                if (!IsCurrent(generation, requestedExecution, cancellation)) return;
                _transient = transient;
                _transientUnavailable = transientUnavailable;
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                CameraAgentProcessingExecutionsView? recentView;
                try
                {
                    var recent = await GraphService.GetExecutionsAsync(10, cancellation.Token).ConfigureAwait(false);
                    if (!IsCurrent(generation, requestedExecution, cancellation)) return;
                    if (recent.Kind == OperatorUiResultKind.Unauthorized) { RevokeAccess(); return; }
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
                    if (!IsCurrent(generation, requestedExecution, cancellation)) return;
                    if (capture.Kind == OperatorUiResultKind.Unauthorized) { RevokeAccess(); return; }
                    sequence = capture.IsSuccess && capture.Value?.CaptureId == detail.Execution.CaptureId ? capture.Value.CaptureSequence : null;
                    _capture = capture.IsSuccess && capture.Value?.CaptureId == detail.Execution.CaptureId ? capture.Value : null;
                }
                catch (Exception) when (!cancellation.IsCancellationRequested)
                {
                    sequence = null;
                }
                if (!IsCurrent(generation, requestedExecution, cancellation)) return;
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
                            if (!IsCurrent(generation, requestedExecution, cancellation)) return;
                            if (item.Kind == OperatorUiResultKind.Unauthorized) { RevokeAccess(); return; }
                            if (item.IsSuccess && item.Value is { } capture && capture.CaptureId == run)
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

    private bool IsCurrent(long generation, Guid executionId, CancellationTokenSource cancellation) =>
        generation == Volatile.Read(ref _generation) && executionId == ExecutionId && !cancellation.IsCancellationRequested;

    private void RevokeAccess()
    {
        _view = null;
        _capture = null;
        _transient = null;
        _recent = null;
        _recentSequences = [];
        _selectedTransient = null;
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Interlocked.Increment(ref _generation);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
        if (_module is not null)
        {
            try { await _module.DisposeAsync().ConfigureAwait(false); }
            catch (JSDisconnectedException) { }
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
    private sealed record StageFact(DateTimeOffset? Time, string Label, string Tone, string Message);
}
