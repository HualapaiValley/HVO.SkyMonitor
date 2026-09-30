using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class PipelineSummaryPage : ComponentBase, IAsyncDisposable
{
    private const string FocusFallbackId = "pipeline-refresh";
    private const string ToggleTriggerId = "pipeline-step-toggle";
    private const string ToggleReason = "operator pipeline step toggle";
    private const int ExecutionSample = 10;

    // Grid geometry in SVG user units, matching the scoped CSS tracks (10rem columns, 2.5rem gaps, 4.25rem rows,
    // 1rem row gaps) at 16 units per rem, so edges meet the node edges at any root font size. The lane title row is
    // 1.5rem.
    private const int ColumnWidth = 160;
    private const int ColumnGap = 40;
    private const int RowHeight = 68;
    private const int RowGap = 16;
    private const int LaneTitleHeight = 24;
    private const double UnitsPerRem = 16;

    private static readonly string RawKey = StageKey(MeteorDetectionLane.RawFrameKey);

    private readonly CancellationTokenSource _lifetime = new();
    private CameraAgentPipelineOperatorState? _pipeline;
    private CaptureScheduleOperatorState? _schedule;
    private NamedRigSelection? _rigSelection;
    private bool _rigAvailable;
    private ProcessingGraphRegistryState? _registry;
    private CameraAgentProcessingExecutionsView? _executions;
    private TransientOperationsView? _transients;
    private string? _error;
    private bool _loading = true;
    private bool _disposed;
    private int _generation;
    private bool _showDraft;
    private string? _requestedStep;
    private string? _selectedId;
    private string? _requestedStage;
    private string? _selectedStage;
    private string? _message;
    private bool _messageError;

    private bool _dialogOpen;
    private bool _showDialog;
    private string? _focusTargetId;
    private IJSObjectReference? _module;
    private ElementReference _dialogElement;
    private CaptureProcessingPlanNode? _toggleNode;
    private bool _toggleTarget;
    private bool _previewing;
    private bool _busy;
    private CameraAgentPipelineProfilePreview? _preview;
    private string? _dialogError;
    private string? _stagePayload;
    private string? _stageKey;
    private long _stageExpectedVersion;
    private CaptureScheduleRevisionSnapshot? _staged;
    private long _stagedVersion;
    private string? _activationKey;
    private int _dialogGeneration;
    private string? _commandNodeId;
    private bool _commandTarget;
    private long _commandStateVersion;

    [Inject] internal ICameraAgentScheduleUiService ScheduleService { get; set; } = default!;
    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;
    [Inject] internal ICameraAgentNamedRigUiService NamedRigService { get; set; } = default!;
    [Inject] internal ICameraAgentTransientOperationsUiService TransientService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "step")]
    public string? Step { get; set; }

    [SupplyParameterFromQuery(Name = "stage")]
    public string? Stage { get; set; }

    private CameraAgentPipelineRevisionPlan? ShownRevision => _pipeline is null
        ? null
        : _showDraft && _pipeline.Pending is { } pending ? pending : _pipeline.Active;

    private IReadOnlyList<CaptureProcessingPlanNode> ShownNodes => ShownRevision is { } revision
        ? CanvasNodes(revision.Plan)
        : [];

    private CaptureProcessingPlanNode? SelectedNode => ShownNodes.FirstOrDefault(
        node => string.Equals(node.Id, _selectedId, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<MeteorLaneStage> LaneStages => _transients is { } transients
        ? MeteorDetectionLane.Stages(transients.Mode)
        : [];

    private string? SelectedKey => _selectedStage is { } stage
        ? StageKey(stage)
        : _selectedId is { } id ? StepKey(id) : null;

    private CameraAgentProcessingExecutionSummary? LatestRun => _executions?.Live
        .OrderByDescending(static execution => execution.AcceptedUtc)
        .FirstOrDefault();

    private string LatestRunUnavailableReason => _executions is null
        ? "Processing executions could not be read."
        : "No live pipeline run has been recorded yet.";

    private ProcessingGraphRevisionState? NamedActive => _registry is { Mode: ProcessingGraphRegistryMode.Named } registry
        ? registry.Revisions.FirstOrDefault(revision => string.Equals(
            revision.RevisionId, registry.ActiveRevisionId, StringComparison.Ordinal))
        : null;

    protected override Task OnInitializedAsync() => LoadAsync();

    protected override void OnParametersSet()
    {
        _requestedStep = string.IsNullOrWhiteSpace(Step) ? null : Step.Trim();
        _requestedStage = string.IsNullOrWhiteSpace(Stage) ? null : Stage.Trim();
        EnsureSelection();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/PipelineSummaryPage.razor.js");
            await _module.InvokeVoidAsync("showModal", _dialogElement);
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/PipelineSummaryPage.razor.js");
            await _module.InvokeVoidAsync("focusById", target, FocusFallbackId);
        }
    }

    private Task RefreshAsync()
    {
        _message = null;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        var generation = ++_generation;
        _loading = true;
        _error = null;
        try
        {
            var token = _lifetime.Token;
            var pipeline = await ScheduleService.GetPipelineAsync(token);
            if (IsStale(generation) || Denied(pipeline.Kind))
            {
                return;
            }
            if (!pipeline.IsSuccess || pipeline.Value is null)
            {
                _pipeline = null;
                _schedule = null;
                ResetDialog();
                _error = pipeline.Message ?? "The processing graph could not be read.";
                return;
            }
            // Everything after the plan is optional context; each read fails on its own and only narrows what the
            // page offers (no toggle without schedule and rig state, no run link without executions).
            var schedule = await ScheduleService.GetAsync(token);
            if (IsStale(generation) || Denied(schedule.Kind))
            {
                return;
            }
            var rig = await NamedRigService.GetAsync(token);
            if (IsStale(generation) || Denied(rig.Kind))
            {
                return;
            }
            var registry = await GraphService.GetRegistryAsync(token);
            if (IsStale(generation) || Denied(registry.Kind))
            {
                return;
            }
            var executions = await GraphService.GetExecutionsAsync(ExecutionSample, token);
            if (IsStale(generation) || Denied(executions.Kind))
            {
                return;
            }
            var transients = await TransientService.GetOverviewAsync(token);
            if (IsStale(generation) || Denied(transients.Kind))
            {
                return;
            }
            _pipeline = pipeline.Value;
            _schedule = schedule.IsSuccess ? schedule.Value : null;
            _rigAvailable = rig.IsSuccess && rig.Value is not null;
            _rigSelection = rig.Value?.Selection;
            _registry = registry.IsSuccess ? registry.Value : null;
            _executions = executions.IsSuccess ? executions.Value : null;
            _transients = transients.IsSuccess ? transients.Value : null;
            if (_pipeline.Pending is null)
            {
                _showDraft = false;
            }
            EnsureSelection();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposed && generation == _generation)
            {
                _loading = false;
            }
        }
    }

    private bool IsStale(int generation) => _disposed || generation != _generation;

    private bool Denied(OperatorUiResultKind kind)
    {
        if (kind != OperatorUiResultKind.Unauthorized)
        {
            return false;
        }
        _pipeline = null;
        ResetDialog();
        NavigationManager.NavigateTo("/Account/AccessDenied");
        return true;
    }

    private void EnsureSelection()
    {
        var nodes = ShownNodes;
        // A lane stage is kept only while the current mode draws it; the lane is read after the plan, so an early
        // pass leaves the request in place for the pass after loading. The raw frame and the lane are drawn even
        // when the revision has no steps.
        if (_pipeline is not null && _requestedStage is { } stage && (
            string.Equals(stage, MeteorDetectionLane.RawFrameKey, StringComparison.Ordinal) ||
            LaneStages.Any(candidate => string.Equals(candidate.Key, stage, StringComparison.Ordinal))))
        {
            _selectedStage = stage;
            _selectedId = null;
            return;
        }
        _selectedStage = null;
        if (nodes.Count == 0)
        {
            _selectedId = null;
            return;
        }
        var chosen = Find(nodes, _requestedStep) ?? Find(nodes, _selectedId) ?? nodes[0];
        _selectedId = chosen.Id;

        static CaptureProcessingPlanNode? Find(IReadOnlyList<CaptureProcessingPlanNode> nodes, string? id)
            => id is null
                ? null
                : nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectNode(string id)
    {
        _requestedStep = id;
        _selectedId = id;
        _requestedStage = null;
        _selectedStage = null;
        NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters(
            new Dictionary<string, object?> { ["step"] = id, ["stage"] = null }), replace: true);
    }

    private void SelectStage(string key)
    {
        _requestedStage = key;
        _selectedStage = key;
        _requestedStep = null;
        _selectedId = null;
        NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters(
            new Dictionary<string, object?> { ["stage"] = key, ["step"] = null }), replace: true);
    }

    private void ShowRevision(bool draft)
    {
        if (draft && _pipeline?.Pending is null)
        {
            return;
        }
        _showDraft = draft;
        EnsureSelection();
    }

    /// <summary>
    /// Why the selected step cannot be turned on or off right now, or null when it can. The order puts the reason an
    /// operator can act on first: a draft, then stale or missing state, then work waiting elsewhere, then the schema.
    /// </summary>
    private string? ToggleUnavailableReason()
    {
        if (_pipeline is null)
        {
            return "The processing graph is not loaded.";
        }
        if (_showDraft)
        {
            return "You are viewing the draft. Steps are turned on or off in the active revision.";
        }
        if (_schedule is null)
        {
            return "Schedule state is unavailable, so steps cannot be changed. Refresh to try again.";
        }
        if (!string.Equals(_schedule.ActiveRevision.RevisionId, _pipeline.Active.RevisionId, StringComparison.Ordinal))
        {
            return "The active revision changed while this page was open. Refresh before changing steps.";
        }
        if (_schedule.PendingRevision is { } pending)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Draft revision {pending.RevisionNumber} is waiting in Schedule. Apply it there first; saving a step change would replace it.");
        }
        if (!_rigAvailable)
        {
            return "Named rig state is unavailable, so steps cannot be changed safely. Refresh to try again.";
        }
        if (_rigSelection?.PendingRevisionId is not null)
        {
            return "A named rig is waiting for CameraAgent to restart. Restart or cancel it first.";
        }
        if (!_pipeline.Active.CanToggle || !string.Equals(
                _schedule.ActiveRevision.Profile.SchemaVersion,
                LocalCaptureProfileDefinition.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return "This revision uses the legacy profile schema, which does not support turning steps on or off.";
        }
        if (SelectedNode is not { } node || !_pipeline.Active.Plan.DesiredNodes.Any(
                desired => string.Equals(desired.Id, node.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return "Select a configured step first.";
        }
        return DependencyBlockedReason(_pipeline.Active.Plan.DesiredNodes, node);
    }

    /// <summary>
    /// The profile rejects any step that is on while a step it depends on is off, so name the steps that stand in the
    /// way here rather than letting the operator open a change that can only fail.
    /// </summary>
    private static string? DependencyBlockedReason(IReadOnlyList<CaptureProcessingPlanNode> nodes, CaptureProcessingPlanNode node)
    {
        if (node.Enabled)
        {
            var dependents = nodes
                .Where(other => other.Enabled && (other.Dependencies ?? []).Contains(node.Id, StringComparer.OrdinalIgnoreCase))
                .Select(static other => Label(other.Id))
                .ToArray();
            return dependents.Length == 0
                ? null
                : $"{Names(dependents)} {(dependents.Length == 1 ? "depends" : "depend")} on this step. Turn {(dependents.Length == 1 ? "it" : "them")} off first.";
        }
        var off = (node.Dependencies ?? [])
            .Where(dependency => nodes.Any(other => !other.Enabled && string.Equals(other.Id, dependency, StringComparison.OrdinalIgnoreCase)))
            .Select(Label)
            .ToArray();
        return off.Length == 0
            ? null
            : $"This step depends on {Names(off)}, which {(off.Length == 1 ? "is" : "are")} off. Turn {(off.Length == 1 ? "it" : "them")} on first.";
    }

    private static string Names(string[] names)
        => names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";

    private async Task OpenToggleAsync()
    {
        if (_busy || SelectedNode is not { } node || _schedule is null || ToggleUnavailableReason() is not null)
        {
            return;
        }
        var schedule = _schedule;
        var generation = ++_dialogGeneration;
        _toggleNode = node;
        _toggleTarget = !node.Enabled;
        _preview = null;
        _dialogError = null;
        // A command whose outcome is unknown keeps its identity when the same change is reopened against the same
        // schedule state, so applying again replays it instead of issuing a second command. Any other change, or a
        // schedule that has been re-read at a different version since, starts a new command.
        if (!string.Equals(_commandNodeId, node.Id, StringComparison.OrdinalIgnoreCase)
            || _commandTarget != _toggleTarget
            || _commandStateVersion != schedule.StateVersion)
        {
            ClearCommand();
            _commandNodeId = node.Id;
            _commandTarget = _toggleTarget;
            _commandStateVersion = schedule.StateVersion;
        }
        else if (_staged is { } staged)
        {
            _dialogError = string.Create(CultureInfo.InvariantCulture,
                $"Draft revision {staged.RevisionNumber} was saved, but applying it did not complete. Apply to try again; the same request is repeated, so it cannot apply twice.");
        }
        else if (_stageKey is not null)
        {
            _dialogError = "The last attempt did not report whether it saved. Apply to try again; the same request is repeated, so it cannot save twice.";
        }
        _previewing = true;
        _dialogOpen = true;
        _showDialog = true;
        _message = null;
        try
        {
            var result = await ScheduleService.TogglePipelineAsync(
                CameraAgentScheduleUiService.SerializeProfile(schedule.ActiveRevision.Profile),
                schedule.ActiveRevision.RevisionId,
                node.Id,
                _toggleTarget,
                _lifetime.Token);
            if (_disposed || !_dialogOpen || generation != _dialogGeneration || Denied(result.Kind))
            {
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                _preview = result.Value;
            }
            else
            {
                _dialogError = result.Message ?? "The step change could not be previewed.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            // A preview from an earlier opening must not end the loading state of the current one.
            if (!_disposed && generation == _dialogGeneration)
            {
                _previewing = false;
            }
        }
    }

    /// <summary>
    /// Saves the previewed profile as a new revision and applies it in one step. Each command keeps its idempotency
    /// key while the outcome is unknown, so a retry after an unavailable result cannot create a second revision or
    /// apply twice; once the revision is saved, a retry only repeats the apply.
    /// </summary>
    private async Task ApplyToggleAsync()
    {
        if (_busy || _preview is null || _schedule is null || _toggleNode is null)
        {
            return;
        }
        var preview = _preview;
        var node = _toggleNode;
        var token = _lifetime.Token;
        _busy = true;
        _dialogError = null;
        try
        {
            if (_staged is null)
            {
                var payload = string.Concat(preview.BasisRevisionId, "\n", preview.ProfileJson);
                if (!string.Equals(_stagePayload, payload, StringComparison.Ordinal))
                {
                    _stagePayload = payload;
                    _stageKey = NewKey();
                    _stageExpectedVersion = _schedule.StateVersion;
                }
                var staged = await ScheduleService.StageAsync(
                    preview.ProfileJson, preview.BasisRevisionId, _stageExpectedVersion, _stageKey!, ToggleReason, token);
                if (_disposed || Denied(staged.Kind))
                {
                    return;
                }
                if (staged.Kind != OperatorUiResultKind.Unavailable)
                {
                    _stageKey = null;
                    _stagePayload = null;
                }
                if (!staged.IsSuccess || staged.Value is null)
                {
                    _dialogError = staged.Kind == OperatorUiResultKind.Conflict
                        ? "The schedule changed while this dialog was open. Close it and refresh to start from the current revision."
                        : staged.Message ?? "The step change could not be saved.";
                    return;
                }
                if (staged.Value.PendingRevision is not { } revision)
                {
                    await FinishAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{Label(node.Id)} is already {(_toggleTarget ? "on" : "off")} in the active revision."), error: false);
                    return;
                }
                _staged = revision;
                _stagedVersion = staged.Value.Version;
                _activationKey = NewKey();
            }
            var applied = await ScheduleService.ActivateAsync(
                _staged.RevisionId, _stagedVersion, _activationKey!, ToggleReason, token);
            if (_disposed || Denied(applied.Kind))
            {
                return;
            }
            if (applied.Kind == OperatorUiResultKind.Unavailable)
            {
                _dialogError = string.Create(CultureInfo.InvariantCulture,
                    $"Saved as draft revision {_staged.RevisionNumber}, but applying it did not complete ({applied.Message ?? "CameraAgent is unavailable"}). Try again to apply it.");
                return;
            }
            if (!applied.IsSuccess)
            {
                await FinishAsync(string.Create(CultureInfo.InvariantCulture,
                    $"Saved as draft revision {_staged.RevisionNumber} but not applied: {applied.Message ?? "the apply was rejected"}. Review and apply it from Schedule."), error: true);
                return;
            }
            await FinishAsync(string.Create(CultureInfo.InvariantCulture,
                $"Revision {_staged.RevisionNumber} is active: {Label(node.Id)} is {(_toggleTarget ? "on" : "off")}. It applied at a capture boundary, so no capture mixes the two graphs."), error: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposed)
            {
                _busy = false;
            }
        }
    }

    private async Task FinishAsync(string message, bool error)
    {
        ClearCommand();
        ResetDialog();
        _message = message;
        _messageError = error;
        _focusTargetId = ToggleTriggerId;
        await LoadAsync();
    }

    private void CloseDialog()
    {
        if (_busy)
        {
            return;
        }
        if (_dialogOpen)
        {
            _focusTargetId = ToggleTriggerId;
        }
        ResetDialog();
    }

    /// <summary>Closes the dialog. The command identity is kept until its outcome is known; see <see cref="ClearCommand"/>.</summary>
    private void ResetDialog()
    {
        _dialogGeneration++;
        _dialogOpen = false;
        _showDialog = false;
        _toggleNode = null;
        _preview = null;
        _previewing = false;
        _dialogError = null;
    }

    private void ClearCommand()
    {
        _commandNodeId = null;
        _stagePayload = null;
        _stageKey = null;
        _staged = null;
        _activationKey = null;
    }

    /// <summary>
    /// The steps the canvas draws. A legacy profile that configures no steps runs CameraAgent's default steps, so
    /// those are shown rather than an empty graph.
    /// </summary>
    internal static IReadOnlyList<CaptureProcessingPlanNode> CanvasNodes(CaptureProcessingPlanPreview plan)
        => plan.DesiredNodes.Count == 0 ? plan.EffectiveNodes : plan.DesiredNodes;

    internal static bool UsesDefaultSteps(CaptureProcessingPlanPreview plan)
        => plan.DesiredNodes.Count == 0 && plan.EffectiveNodes.Count > 0;

    /// <summary>
    /// Places the steps on the grid. A legacy step without declared dependencies is drawn from the dependencies
    /// CameraAgent inferred for it, which the effective graph records.
    /// </summary>
    internal static IReadOnlyList<PipelineGraphPlacement> Arrange(CaptureProcessingPlanPreview plan)
    {
        var nodes = CanvasNodes(plan)
            .Select(node => node.Dependencies is null && Effective(plan, node.Id) is { } effective
                ? node with { Dependencies = effective.Dependencies }
                : node)
            .ToArray();
        // Profile step ids are case-insensitive in CaptureProcessingPipelineFactory.
        return PipelineGraphLayout.Arrange(nodes, StringComparer.OrdinalIgnoreCase);
    }

    internal static CaptureProcessingPlanNode? Effective(CaptureProcessingPlanPreview plan, string id)
        => plan.EffectiveNodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase));

    internal sealed record PipelineCanvasEdge(string Path, string Source, string Target, bool Off, bool Transient);

    /// <summary>
    /// Everything the graph draws, in SVG user units on the same grid tracks the CSS lays the boxes on. Column 0 holds
    /// the raw frame; step ranks and lane columns start at column 1. The processing rows come first, then the lane's
    /// title row and its stage rows.
    /// </summary>
    internal sealed record PipelineCanvas(
        IReadOnlyList<PipelineGraphPlacement> Steps,
        IReadOnlyList<MeteorLaneStage> Stages,
        IReadOnlyList<PipelineCanvasEdge> Edges,
        IReadOnlyList<GraphTrack> Columns,
        IReadOnlyList<GraphTrack> Rows,
        int LaneTitleRow,
        int LaneRow)
    {
        internal bool HasInput(string key) => Edges.Any(edge => string.Equals(edge.Target, key, StringComparison.OrdinalIgnoreCase));

        internal bool HasOutput(string key) => Edges.Any(edge => string.Equals(edge.Source, key, StringComparison.OrdinalIgnoreCase));
    }

    internal static string StepKey(string id) => string.Concat("step:", id);

    internal static string StageKey(string key) => string.Concat("stage:", key);

    /// <summary>
    /// Lays out the raw frame, the steps and the meteor detection lane, and routes every edge around the boxes. A step
    /// reading <c>$raw</c> takes an edge from the raw frame; a dependency on a step in the same or a later column is
    /// not drawn, as before. Processing edges may use a row added below the steps; the lane starts below that.
    /// </summary>
    internal static PipelineCanvas Layout(IReadOnlyList<PipelineGraphPlacement> placements, IReadOnlyList<MeteorLaneStage> stages)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(stages);
        var stepColumns = placements.Select(static placement => placement.Rank + 1).DefaultIfEmpty(1).Max();
        var laneColumns = stages.Select(static stage => stage.Column + 1).DefaultIfEmpty(0).Max();
        var columnCount = 1 + Math.Max(1, Math.Max(stepColumns, laneColumns));
        var columns = Enumerable.Range(0, columnCount)
            .Select(static column => new GraphTrack(column * (ColumnWidth + ColumnGap), ColumnWidth))
            .ToArray();
        var stepRows = Math.Max(1, placements.Select(static placement => placement.Row + 1).DefaultIfEmpty(1).Max());
        var rows = Enumerable.Range(0, stepRows)
            .Select(static row => new GraphTrack(row * (RowHeight + RowGap), RowHeight))
            .ToList();

        var occupied = new List<(int Column, int Row)> { (0, 0) };
        occupied.AddRange(placements.Select(static placement => (placement.Rank + 1, placement.Row)));
        var router = new PipelineEdgeRouter(columns, rows, RowGap, occupied);
        var edges = new List<PipelineCanvasEdge>();
        var byId = new Dictionary<string, PipelineGraphPlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var placement in placements)
        {
            byId.TryAdd(placement.Node.Id, placement);
        }
        foreach (var target in placements)
        {
            var targetKey = StepKey(target.Node.Id);
            foreach (var dependency in target.Dependencies)
            {
                if (PipelineGraphLayout.IsRawInput(dependency))
                {
                    var route = router.Route(0, 0, target.Rank + 1, target.Row);
                    edges.Add(new(PipelineEdgeRouter.Path(route), RawKey, targetKey, !target.Node.Enabled, false));
                    continue;
                }
                if (!byId.TryGetValue(dependency, out var source) || source.Rank >= target.Rank)
                {
                    continue;
                }
                var path = PipelineEdgeRouter.Path(router.Route(source.Rank + 1, source.Row, target.Rank + 1, target.Row));
                edges.Add(new(path, StepKey(source.Node.Id), targetKey, !source.Node.Enabled || !target.Node.Enabled, false));
            }
        }

        rows = [.. router.Rows];
        var laneTitleRow = rows.Count;
        rows.Add(new GraphTrack(rows[^1].End + RowGap, LaneTitleHeight));
        var laneRow = rows.Count;
        var laneRows = Math.Max(1, stages.Select(static stage => stage.Row + 1).DefaultIfEmpty(1).Max());
        for (var row = 0; row < laneRows; row++)
        {
            rows.Add(new GraphTrack(rows[^1].End + RowGap, RowHeight));
        }
        if (stages.Count > 0)
        {
            occupied.AddRange(Enumerable.Range(1, columnCount - 1).Select(column => (column, laneTitleRow)));
            occupied.AddRange(stages.Select(stage => (stage.Column + 1, laneRow + stage.Row)));
            var lane = new PipelineEdgeRouter(columns, rows, RowGap, occupied);
            var byKey = stages.ToDictionary(static stage => stage.Key, StringComparer.Ordinal);
            foreach (var target in stages)
            {
                foreach (var input in target.Inputs)
                {
                    var (column, row, key) = byKey.TryGetValue(input, out var source)
                        ? (source.Column + 1, laneRow + source.Row, StageKey(source.Key))
                        : (0, 0, RawKey);
                    var route = lane.Route(column, row, target.Column + 1, laneRow + target.Row);
                    edges.Add(new(PipelineEdgeRouter.Path(route), key, StageKey(target.Key), false, true));
                }
            }
            rows = [.. lane.Rows];
        }
        return new PipelineCanvas(placements, stages, edges, columns, rows, laneTitleRow, laneRow);
    }

    private static string ViewBox(PipelineCanvas canvas)
        => string.Create(CultureInfo.InvariantCulture, $"0 0 {canvas.Columns[^1].End} {canvas.Rows[^1].End}");

    private static string GridStyle(PipelineCanvas canvas)
    {
        var rows = string.Join(' ', canvas.Rows.Select(static row => Rem(row.Size)));
        return string.Create(CultureInfo.InvariantCulture,
            $"--pipeline-columns:{canvas.Columns.Count};--pipeline-row-template:{rows}");
    }

    private static string Rem(double units) => string.Concat((units / UnitsPerRem).ToString("0.####", CultureInfo.InvariantCulture), "rem");

    private static string CellStyle(PipelineGraphPlacement placement)
        => string.Create(CultureInfo.InvariantCulture, $"grid-column:{placement.Rank + 2};grid-row:{placement.Row + 1}");

    private static string StageStyle(PipelineCanvas canvas, MeteorLaneStage stage)
        => string.Create(CultureInfo.InvariantCulture, $"grid-column:{stage.Column + 2};grid-row:{canvas.LaneRow + stage.Row + 1}");

    private static string RowStyle(int row, string columns)
        => string.Create(CultureInfo.InvariantCulture, $"grid-column:{columns};grid-row:{row + 1}");

    private static string BandStyle(PipelineCanvas canvas)
        => string.Create(CultureInfo.InvariantCulture, $"grid-column:1 / -1;grid-row:{canvas.LaneTitleRow + 1} / {canvas.Rows.Count + 1}");

    internal sealed record PipelineCheck(string Text, bool? Passed);

    /// <summary>
    /// Checks computed from the plan the service compiled. A plan only exists once CameraAgent built the graph, so
    /// these restate that result in operator terms; a legacy profile whose dependencies are inferred at startup is
    /// reported as inferred rather than as checked.
    /// </summary>
    internal static IReadOnlyList<PipelineCheck> Validate(CaptureProcessingPlanPreview plan)
    {
        var checks = new List<PipelineCheck>();
        var nodes = plan.DesiredNodes;
        if (UsesDefaultSteps(plan))
        {
            checks.Add(new("No steps are configured, so CameraAgent runs its default steps.", null));
        }
        var ids = nodes.Select(static node => node.Id).ToArray();
        var duplicate = ids.GroupBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        checks.Add(duplicate is null
            ? new("Step IDs are unique.", true)
            : new($"More than one step uses the ID {duplicate}.", false));
        var effectiveIds = plan.EffectiveNodes.Select(static node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = nodes.Where(node => node.Enabled && !effectiveIds.Contains(node.Id)).Select(static node => node.Id).ToArray();
        checks.Add(missing.Length == 0
            ? new("Every step that is on compiles into the effective graph.", true)
            : new($"Left out of the effective graph: {string.Join(", ", missing)}.", false));
        if (plan.DependencyPolicy == CapturePipelineDependencyPolicy.LegacyInference ||
            nodes.Any(static node => node.Dependencies is null))
        {
            checks.Add(new("Dependencies are inferred when CameraAgent starts (legacy pipeline schema).", null));
            return checks;
        }
        var known = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dangling = nodes
            .SelectMany(node => node.Dependencies!
                .Where(dependency => !PipelineGraphLayout.IsRawInput(dependency) && !known.Contains(dependency))
                .Select(dependency => (node.Id, Dependency: dependency)))
            .FirstOrDefault();
        checks.Add(dangling.Id is null
            ? new("Every dependency names a configured step or the raw frame.", true)
            : new($"{dangling.Id} depends on {dangling.Dependency}, which is not configured.", false));
        checks.Add(IsAcyclic(nodes)
            ? new("Dependencies form an acyclic graph.", true)
            : new("Dependencies contain a cycle.", false));
        var off = nodes.Where(static node => !node.Enabled).Select(static node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocked = nodes.FirstOrDefault(node => node.Enabled && node.Dependencies!.Any(off.Contains));
        checks.Add(blocked is null
            ? new("No step that is on depends on a step that is off.", true)
            : new($"{blocked.Id} is on but depends on a step that is off.", false));
        return checks;
    }

    private static bool IsAcyclic(IReadOnlyList<CaptureProcessingPlanNode> nodes)
    {
        var byId = new Dictionary<string, CaptureProcessingPlanNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            byId.TryAdd(node.Id, node);
        }
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Visit(string id)
        {
            if (done.Contains(id))
            {
                return true;
            }
            if (!visiting.Add(id))
            {
                return false;
            }
            foreach (var dependency in byId[id].Dependencies ?? [])
            {
                if (byId.ContainsKey(dependency) && !Visit(dependency))
                {
                    return false;
                }
            }
            visiting.Remove(id);
            done.Add(id);
            return true;
        }
        return byId.Keys.All(Visit);
    }

    private static string NodeClass(CaptureProcessingPlanNode node, bool active)
        => string.Concat("ops-pipeline-node step", active ? " active" : "", node.Enabled ? "" : " off", node.Required ? "" : " optional");

    private static string EdgeClass(PipelineCanvasEdge edge, string? selectedKey)
    {
        var selected = selectedKey is not null && (
            string.Equals(edge.Source, selectedKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(edge.Target, selectedKey, StringComparison.OrdinalIgnoreCase));
        return string.Concat("pipeline-edge", edge.Transient ? " transient" : "", edge.Off ? " off" : "", selected ? " selected" : "");
    }

    private static string SourceClass(string key, string? selectedKey)
        => string.Concat("ops-pipeline-node source", key == selectedKey ? " active" : "");

    private static string StageClass(MeteorLaneStage stage, string key, string? selectedKey)
        => string.Concat("ops-pipeline-node transient", stage.Central ? " central" : "", key == selectedKey ? " active" : "");

    private static string Pressed(string key, string? selectedKey) => key == selectedKey ? "true" : "false";

    private MeteorLaneStage? SelectedStage(PipelineCanvas canvas)
        => canvas.Stages.FirstOrDefault(stage => string.Equals(stage.Key, _selectedStage, StringComparison.Ordinal));

    private static string LaneSubtitle(TransientOperationsView? view) => view?.Mode switch
    {
        null => "State unavailable",
        TransientOperatingMode.Edge => "Edge mode / reads every raw frame on its own durable lane",
        TransientOperatingMode.Hybrid => "Hybrid mode / scans here, validates on LogicHost",
        TransientOperatingMode.Central => "Central mode / runs on LogicHost",
        _ => "Off on this agent",
    };

    private static (string Icon, string Title, string Detail) LaneNote(TransientOperationsView? view) => view is null
        ? ("warning", "Meteor detection state could not be read.", "The processing steps are unaffected.")
        : TransientOperationsPage.Banner(view);

    private static string StageInputs(PipelineCanvas canvas, MeteorLaneStage stage)
        => string.Join(", ", stage.Inputs.Select(input => string.Equals(input, MeteorDetectionLane.RawFrameKey, StringComparison.Ordinal)
            ? "Raw frame"
            : canvas.Stages.FirstOrDefault(other => string.Equals(other.Key, input, StringComparison.Ordinal))?.Title ?? input));

    private static string StageState(TransientOperationsView? view, MeteorLaneStage stage)
    {
        if (view is null)
        {
            return "Unavailable";
        }
        return stage.Measure switch
        {
            MeteorLaneMeasure.PendingFrames => Waiting(view.Worker.PendingFrames, "frame", "frames"),
            MeteorLaneMeasure.PendingCandidates => Waiting(view.Worker.PendingCandidates, "candidate", "candidates"),
            MeteorLaneMeasure.Delivery => string.Concat(
                Waiting(view.Delivery.PendingCount, "candidate", "candidates"), ", ", Count(view.Delivery.RetryingCount), " retrying"),
            _ => "Not measured on this agent",
        };

        static string Waiting(long count, string one, string many)
            => string.Concat(Count(count), " ", count == 1 ? one : many, " waiting");
    }

    private static string RawConsumers(IReadOnlyList<PipelineGraphPlacement> placements, PipelineCanvas canvas)
    {
        var names = placements
            .Where(static placement => placement.Dependencies.Any(PipelineGraphLayout.IsRawInput))
            .Select(static placement => Label(placement.Node.Id))
            .ToList();
        if (canvas.Stages.Count > 0)
        {
            names.Add("Meteor detection");
        }
        return names.Count == 0 ? "No step reads it directly" : string.Join(", ", names);
    }

    private static string CheckClass(PipelineCheck check) => check.Passed switch
    {
        true => "ops-validation-item",
        false => "ops-validation-item failed",
        null => "ops-validation-item neutral",
    };

    internal static string Label(string id) => OperationsPage.SplitWords(id);

    private static string Policy(CapturePipelineDependencyPolicy policy) => policy switch
    {
        CapturePipelineDependencyPolicy.RejectEnabledDependent => "Explicit dependencies",
        _ => "Inferred (legacy)",
    };

    private static string StepFacts(CaptureProcessingPlanNode node)
        => string.Concat(node.Alias, " / ", node.Required ? "required" : "optional", node.Enabled ? "" : " / off");

    private static string StepKind(CaptureProcessingPlanNode node)
        => string.Concat(node.Required ? "Required step" : "Optional step", node.Enabled ? "" : " / off");

    private static string StepDescription(CaptureProcessingPlanNode node)
    {
        if (!node.Enabled)
        {
            return "This step is off. It stays in the configured graph and is left out of the effective graph until it is turned back on.";
        }
        return node.Required
            ? "A retryable failure is retried; a terminal failure fails the run closed so no partial product is published."
            : "A failure is recorded as degraded instead of stopping the run; the steps that do not need it still complete.";
    }

    private static string Inputs(CaptureProcessingPlanPreview plan, CaptureProcessingPlanNode node)
    {
        if (node.Dependencies is { } declared)
        {
            return declared.Count == 0 ? "None" : string.Join(", ", declared.Select(Input));
        }
        var inferred = Effective(plan, node.Id)?.Dependencies;
        return inferred is { Count: > 0 }
            ? string.Concat(string.Join(", ", inferred.Select(Input)), " (inferred)")
            : "Inferred when CameraAgent starts";
    }

    private static string Input(string dependency)
        => PipelineGraphLayout.IsRawInput(dependency) ? "Raw frame" : dependency;

    private static string Outputs(CaptureProcessingPlanPreview plan, CaptureProcessingPlanNode node)
    {
        if (Effective(plan, node.Id) is not { } effective)
        {
            return "None while the step is off";
        }
        if (effective.OutputRole is not { } role)
        {
            return "No artifact (infrastructure step)";
        }
        var text = OperationsPage.SplitWords(role.ToString());
        return effective.OutputVariant is { Length: > 0 } variant ? string.Concat(text, " / ", variant) : text;
    }

    /// <summary>Option values can carry device identity or paths, so only how many are set is shown.</summary>
    internal static string Options(JsonElement? options)
    {
        var count = options is { ValueKind: JsonValueKind.Object } element ? element.EnumerateObject().Count() : 0;
        return count switch
        {
            0 => "Module defaults",
            1 => "1 set, value not shown",
            _ => string.Create(CultureInfo.InvariantCulture, $"{count} set, values not shown"),
        };
    }

    private string LastRun()
    {
        if (_executions is null)
        {
            return "Unavailable";
        }
        if (LatestRun is not { } run)
        {
            return "No runs yet";
        }
        var status = OperationsPage.SplitWords(run.Status.ToString());
        return run.Duration is { } duration
            ? run.Status == ProcessingGraphExecutionStatus.Completed
                ? Seconds(duration)
                : string.Concat(status, " after ", Seconds(duration))
            : status;
    }

    private static string Seconds(TimeSpan duration)
        => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, duration.TotalSeconds):0.00}s");

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Revision(long number) => number.ToString(CultureInfo.InvariantCulture);

    private static string NewKey() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
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
