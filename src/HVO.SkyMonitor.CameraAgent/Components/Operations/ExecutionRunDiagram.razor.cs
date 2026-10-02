using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class ExecutionRunDiagram : ComponentBase, IAsyncDisposable
{
    private const int NodeWidth = 204;
    private const int NodeHeight = 100;
    private const int ColumnGap = 56;
    private const int RowGap = 32;
    private const int Margin = 28;
    private const int HeaderHeight = 64;
    private static int _instances;
    private readonly int _instance = Interlocked.Increment(ref _instances);
    private List<PlacedNode> _layout = [];
    private List<GraphEdge> _edges = [];
    private List<GraphBand> _bands = [];
    private int _rowsEnd = HeaderHeight + NodeHeight;
    private int _sourceRow;
    private double _zoom = 0.5;
    private bool _fit = true;
    private bool _fullscreen;
    private bool _disposed;
    private double _viewportWidth;
    private double _viewportHeight;
    private ElementReference _root;
    private ElementReference _viewport;
    private ElementReference _fullscreenButton;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ExecutionRunDiagram>? _reference;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Parameter, EditorRequired] public IReadOnlyList<CameraAgentProcessingNodeView> Nodes { get; set; } = [];
    [Parameter] public string? SelectedNodeId { get; set; }
    [Parameter] public EventCallback<string> NodeSelected { get; set; }
    [Parameter] public IReadOnlyList<TransientStageEvent> TransientEvents { get; set; } = [];
    [Parameter] public bool TransientEnabled { get; set; }
    [Parameter] public bool TransientUnavailable { get; set; }
    [Parameter] public string GraphTitle { get; set; } = "Recorded capture pipeline";
    [Parameter] public string? SelectedTransientKey { get; set; }
    [Parameter] public EventCallback<TransientStageEvent> TransientSelected { get; set; }

    private string TitleId => $"execution-graph-title-{_instance}";
    private string DescriptionId => $"execution-graph-description-{_instance}";
    private string ArrowId => $"execution-graph-arrow-{_instance}";
    private int ZoomPercent => (int)Math.Round(_zoom * 100);
    private int EdgeX => Margin + NodeWidth + ColumnGap;
    private int PublicationX => _layout.Count == 0 ? EdgeX + NodeWidth + ColumnGap : _layout.Max(static item => item.X) + NodeWidth + ColumnGap;
    private int LogicHostX => PublicationX + NodeWidth + ColumnGap;
    private int SourceY => HeaderHeight + _sourceRow * (NodeHeight + RowGap);
    private int CloudTop => _rowsEnd + 28;
    private int TransientTop => CloudTop + (HasCloudProduct ? 0 : NodeHeight + 28);
    private int Height => TransientTop + 72 + Math.Max(1, TransientLanes.Length) * (NodeHeight + 40);
    private int Width => Math.Max(LogicHostX + NodeWidth + Margin,
        EdgeX + (CaptureLaneCount + TransientLanes.Where(static lane => lane.CandidateId is not null)
            .Select(static lane => lane.Events.Count).DefaultIfEmpty().Max()) * (NodeWidth + ColumnGap) + Margin);
    private string GraphSizeStyle => FormattableString.Invariant($"width:{Width}px;height:{Height}px;transform:scale({_zoom});");
    private string ScaleSizeStyle => FormattableString.Invariant($"width:{Width * _zoom}px;height:{Height * _zoom}px;");
    private bool HasCloudProduct => Nodes.Any(static node => node.OutputContracts.Any(contract =>
        contract.Role == FrameArtifactRole.Metadata && contract.Recipe?.Name == BuiltInProcessingRecipes.CloudAssessment &&
        node.Outputs.Any(output => output.Role == contract.Role && output.Variant == contract.Variant)));

    private string GraphDescription => string.Join(" ", Nodes.Select(node =>
        $"{node.NodeId}: {node.Status}; depends on {string.Join(", ", node.Dependencies.Select(static dependency => dependency.ProducerId))}.")) +
        (TransientEnabled ? TransientUnavailable ? " Transient evidence unavailable." : TransientEvents.Count == 0
            ? " No transient stage events recorded." : " Transient milestones: " + string.Join(" ", TransientLanes.Select(lane =>
                $"{LaneLabel(lane.CandidateId)}: {string.Join(", ", lane.Events.Select(stage => $"{TransientName(stage.StageKey)} {stage.State}"))}."))
            : " Local transient detector disabled.");

    protected override void OnParametersSet()
    {
        var byId = Nodes.ToDictionary(static node => node.NodeId, StringComparer.Ordinal);
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        int Rank(string id)
        {
            if (ranks.TryGetValue(id, out var known)) return known;
            if (!byId.TryGetValue(id, out var node) || !visiting.Add(id)) return 0;
            var rank = node.Dependencies.Where(dependency => byId.ContainsKey(dependency.ProducerId))
                .Select(dependency => Rank(dependency.ProducerId) + 1).DefaultIfEmpty().Max();
            visiting.Remove(id);
            return ranks[id] = rank;
        }
        foreach (var node in Nodes) Rank(node.NodeId);
        var counts = ranks.Values.GroupBy(static rank => rank).ToDictionary(static group => group.Key, static group => group.Count());
        var rowCount = Math.Max(1, counts.Values.DefaultIfEmpty(1).Max());
        _sourceRow = rowCount / 2;
        var columns = new Dictionary<int, int>();
        _layout = [];
        foreach (var node in Nodes)
        {
            var rank = ranks[node.NodeId];
            var row = (rowCount - counts[rank]) / 2 + columns.GetValueOrDefault(rank);
            columns[rank] = columns.GetValueOrDefault(rank) + 1;
            _layout.Add(new(node, rank, row, EdgeX + rank * (NodeWidth + ColumnGap), HeaderHeight + row * (NodeHeight + RowGap)));
        }
        _bands = [new(0, EdgeX, "Camera and acquisition"), new(EdgeX, PublicationX - EdgeX, "Edge processing"),
            new(PublicationX, LogicHostX - PublicationX, "Durable publication"), new(LogicHostX, NodeWidth + Margin, "LogicHost")];
        var rankCount = _layout.Select(static item => item.Rank + 1).DefaultIfEmpty().Max();
        var publication = rankCount + 1;
        var tracks = new List<GraphTrack> { new(Margin, NodeWidth) };
        tracks.AddRange(Enumerable.Range(0, rankCount).Select(rank => new GraphTrack(EdgeX + rank * (NodeWidth + ColumnGap), NodeWidth)));
        tracks.Add(new(PublicationX, NodeWidth));
        var rows = Enumerable.Range(0, rowCount).Select(static row => new GraphTrack(HeaderHeight + row * (NodeHeight + RowGap), NodeHeight)).ToArray();
        var occupied = _layout.Select(static item => (item.Rank + 1, item.Row)).Append((0, _sourceRow)).Append((publication, _sourceRow));
        var router = new PipelineEdgeRouter(tracks, rows, RowGap, occupied);
        var positions = _layout.ToDictionary(static item => item.Node.NodeId, StringComparer.Ordinal);
        _edges = [];
        foreach (var target in _layout)
        {
            foreach (var dependency in target.Node.Dependencies)
            {
                var highlighted = target.Node.NodeId == SelectedNodeId;
                if (dependency.ProducerId == "$raw")
                    _edges.Add(new(PipelineEdgeRouter.Path(router.Route(0, _sourceRow, target.Rank + 1, target.Row)), dependency.Required, highlighted));
                else if (positions.TryGetValue(dependency.ProducerId, out var source) && source.Rank < target.Rank)
                    _edges.Add(new(PipelineEdgeRouter.Path(router.Route(source.Rank + 1, source.Row, target.Rank + 1, target.Row)), dependency.Required, highlighted || source.Node.NodeId == SelectedNodeId));
            }
        }
        foreach (var source in _layout.Where(static item => item.Node.Outputs.Any(static output => output.Published)))
            _edges.Add(new(PipelineEdgeRouter.Path(router.Route(source.Rank + 1, source.Row, publication, _sourceRow)), true, source.Node.NodeId == SelectedNodeId));
        _rowsEnd = (int)Math.Ceiling(router.Rows[^1].End);
        ApplyFit();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed) return;
        try
        {
            var module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Operations/ExecutionRunDiagram.razor.js");
            if (_disposed) { await module.DisposeAsync(); return; }
            _module = module;
            _reference = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("observe", _root, _viewport, _fullscreenButton, _reference);
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (TaskCanceledException) { }
    }

    [JSInvokable]
    public Task ViewportChanged(double width, double height, bool fullscreen)
    {
        if (_disposed) return Task.CompletedTask;
        _viewportWidth = width;
        _viewportHeight = height;
        _fullscreen = fullscreen;
        ApplyFit();
        return InvokeAsync(StateHasChanged);
    }

    private void ApplyFit()
    {
        if (!_fit || _viewportWidth <= 0) return;
        var scale = Math.Min(1, (_viewportWidth - 24) / Width);
        if (_fullscreen && _viewportHeight > 0) scale = Math.Min(scale, (_viewportHeight - 24) / Height);
        // Keep labels inspectable; graphs wider than the measured viewport scroll at the prototype's 50% floor.
        _zoom = Math.Clamp(scale, 0.5, 1);
    }

    private async Task FitGraph()
    {
        _fit = true;
        ApplyFit();
        if (_module is not null)
        {
            try { await _module.InvokeVoidAsync("resetScroll", _viewport); }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
        }
    }
    private void ZoomOut() { _fit = false; _zoom = Math.Max(0.5, _zoom - 0.1); }
    private void ZoomIn() { _fit = false; _zoom = Math.Min(1.5, _zoom + 0.1); }
    private async Task ToggleFullscreenAsync()
    {
        if (_module is null) return;
        try { await _module.InvokeVoidAsync("toggleFullscreen", _root); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    private int CaptureLaneCount => TransientLanes.FirstOrDefault(static lane => lane.CandidateId is null)?.Events.Count ?? 0;
    private TransientStageEvent? CausalSuccess => TransientEvents.FirstOrDefault(static stage => stage.CandidateId is null && stage.StageKey == "causal-scan" && stage.State == "succeeded");
    private TransientLane[] TransientLanes => TransientEvents.GroupBy(static stage => stage.CandidateId)
        .OrderBy(static group => group.Key.HasValue).ThenBy(static group => group.Min(static stage => stage.SlotOrdinal ?? int.MaxValue)).ThenBy(static group => group.Key)
        .Select((group, index) => new TransientLane(group.Key, TransientTop + 72 + index * (NodeHeight + 40),
            group.OrderBy(static stage => StageRank(stage.StageKey)).ThenBy(static stage => stage.RecordedUtc).ToArray())).ToArray();
    private int TransientX(TransientLane lane, int index) => EdgeX + (lane.CandidateId is null ? index : CaptureLaneCount + index) * (NodeWidth + ColumnGap);
    private IEnumerable<string> TransientEdges
    {
        get
        {
            foreach (var lane in TransientLanes)
                for (var i = 0; i < lane.Events.Count; i++)
                {
                    var x = TransientX(lane, i);
                    if (i > 0 && IsRecordedPredecessor(lane.Events[i - 1], lane.Events[i]))
                        yield return FormattableString.Invariant($"M{x - ColumnGap},{lane.Y + NodeHeight / 2} H{x}");
                    if (i == 0 && lane.CandidateId is not null && CausalSuccess is not null && lane.Events[i].StageKey == "candidate-allocated")
                    {
                        var source = TransientLanes.First(static item => item.CandidateId is null);
                        var sourceIndex = source.Events.ToList().FindIndex(static item => item.StageKey == "causal-scan");
                        yield return PipelineEdgeRouter.Path([new(TransientX(source, sourceIndex) + NodeWidth, source.Y + NodeHeight / 2), new(x, lane.Y + NodeHeight / 2)]);
                    }
                }
        }
    }
    internal static string StageKey(TransientStageEvent stage) => $"{stage.CandidateId}:{stage.StageKey}:{stage.RecordedUtc.ToString("O", CultureInfo.InvariantCulture)}";
    private static int StageRank(string key) => key switch { "frame-staged" => 0, "causal-scan" => 1, "candidate-allocated" => 2, "candidate-persisted" => 3, "event-finalized" or "relay-pending" => 4, "central-acknowledged" => 5, _ => 6 };
    internal static string TransientName(string key) => key switch { "frame-staged" => "Durable frame window", "causal-scan" => "Causal candidate scan", "candidate-allocated" => "Candidate allocated", "candidate-persisted" => "Persist candidate", "event-finalized" => "Retain event evidence", "relay-pending" => "Relay queued", "central-acknowledged" => "Acknowledged", _ => key };
    private static bool StageSucceeded(TransientStageEvent stage) => stage.State is "succeeded" or "CandidatePersisted" or "Finalized" or "Acknowledged";
    private static string TransientStatus(TransientStageEvent stage) => StageSucceeded(stage) ? "success"
        : stage.State is "not-succeeded" or "quarantined" ? "warning" : "pending";
    private static bool IsRecordedPredecessor(TransientStageEvent previous, TransientStageEvent current) => ((previous.StageKey, current.StageKey) switch
    {
        ("frame-staged", "causal-scan") or ("candidate-allocated", "candidate-persisted") => true,
        ("candidate-persisted", "event-finalized") or ("candidate-persisted", "relay-pending") or ("relay-pending", "central-acknowledged") => previous.State is not "not-succeeded" and not "quarantined",
        _ => false
    });
    private string TransientEmptyLabel => !TransientEnabled ? "Local detector disabled" : TransientUnavailable ? "Evidence unavailable" : "No stage events recorded";
    private static string LaneLabel(Guid? id) => id is { } candidate ? $"Candidate {candidate:D}" : "Capture";
    private string NodeClass(CameraAgentProcessingNodeView node) => $"graph-node run-diagram__node run-diagram__node--{PipelineRunPresentation.StatusClass(node.Status)}{(SelectedNodeId == node.NodeId ? " selected run-diagram__node--selected" : "")}";
    private static string EdgeClass(GraphEdge edge) => $"run-diagram__edge{(edge.Required ? "" : " run-diagram__edge--optional")}{(edge.Highlighted ? " run-diagram__edge--highlighted" : " muted")}";

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is not null)
        {
            try { await _module.InvokeVoidAsync("dispose", _root); await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
            catch (TaskCanceledException) { }
        }
        _reference?.Dispose();
    }
    private sealed record PlacedNode(CameraAgentProcessingNodeView Node, int Rank, int Row, int X, int Y);
    private sealed record GraphEdge(string Path, bool Required, bool Highlighted);
    private sealed record GraphBand(int X, int Width, string Label);
    private sealed record TransientLane(Guid? CandidateId, int Y, IReadOnlyList<TransientStageEvent> Events);
}
