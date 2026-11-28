using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProcessingNodeConfigPage : ComponentBase, IDisposable
{
    private sealed record PreviewSnapshot(
        long DraftGeneration,
        string? SourceRevisionId,
        string? NodeId,
        string Name,
        string Revision,
        CapturePipelineConfig Pipeline,
        CaptureProcessingPlanPreview Plan,
        bool GraphChanged);

    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _routeCts;
    private CancellationTokenSource? _previewCts;
    private CameraAgentProcessingGraphRevisionDetail? _detail;
    private ProcessingGraphDraftModel _model = ProcessingGraphDraftModel.Empty();
    private ProcessingGraphDraftModel.NodeRow? _node;
    private int _nodeIndex;
    private string? _resolvedRevisionId;
    private PreviewSnapshot? _preview;
    private string? _message;
    private bool _messageIsError;
    private bool _loading = true;
    private bool _notFound;
    private bool _busy;
    private bool _disposed;
    private long _draftGeneration;
    private string? _savedRevisionId;
    private string? _saveKey;
    private string? _savePayload;

    [Parameter] public string? RevisionId { get; set; }

    [Parameter] public string? NodeId { get; set; }

    [SupplyParameterFromQuery(Name = "nodeId")]
    public string? QueryNodeId { get; set; }

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    private string Eyebrow => _node?.IsSystemManaged == true
        ? "Built-in pipeline stage"
        : "Filter and overlay configuration";

    private string PageHeading => _node switch
    {
        null => "Stage configuration",
        { IsSystemManaged: true } => ProcessingNodeSectionRegistry.DescribeBuiltInStage(_node.Type).Title,
        { OptionsForm: { } form } => $"{form.Descriptor.Title} — {_node.Id}",
        _ => $"{_node.Id} ({_node.Type})"
    };

    private string Lead => _node switch
    {
        null => "Typed, validated settings for one filter or overlay, saved as a new immutable graph revision.",
        { IsSystemManaged: true } => "Inspect fixed inputs and settings for this built-in stage. Built-in measurement stages cannot be disabled.",
        { OptionsForm: { } form } => form.Descriptor.Help,
        _ => "Inspect an undescribed processing step without altering its preserved options."
    };

    private string BackHref => _resolvedRevisionId is null
        ? "/operations/pipeline/graphs"
        : $"/operations/pipeline/graphs/{Uri.EscapeDataString(_resolvedRevisionId)}";

    private string BackLabel => _resolvedRevisionId is null ? "Back to named graphs" : "Back to revision";

    private string FullEditorHref => _resolvedRevisionId is null
        ? "/operations/pipeline/graphs/new"
        : _node?.OptionsForm is not null
            ? $"/operations/pipeline/graphs/new?from={Uri.EscapeDataString(_resolvedRevisionId)}#{ProcessingNodeSectionRegistry.OptionsSectionId(_model.Nodes, _node)}"
            : $"/operations/pipeline/graphs/new?from={Uri.EscapeDataString(_resolvedRevisionId)}";

    internal static string NodeConfigHref(string revisionId, string nodeId)
        => IsSafePathSegment(nodeId)
            ? $"/operations/pipeline/graphs/{Uri.EscapeDataString(revisionId)}/nodes/{Uri.EscapeDataString(nodeId)}"
            : $"/operations/pipeline/graphs/{Uri.EscapeDataString(revisionId)}/node?nodeId={Uri.EscapeDataString(nodeId)}";

    internal static string ActiveNodeConfigHref(string nodeId)
        => IsSafePathSegment(nodeId)
            ? $"/operations/pipeline/configure/{Uri.EscapeDataString(nodeId)}"
            : $"/operations/pipeline/configure?nodeId={Uri.EscapeDataString(nodeId)}";

    private static bool IsSafePathSegment(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] == '.' || value[^1] == '.' || value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var ch in value)
        {
            var allowed = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.';
            if (!allowed)
            {
                return false;
            }
        }
        return true;
    }

    protected override async Task OnParametersSetAsync()
    {
        var loadGeneration = ++_draftGeneration;
        CancelAndResetPreview();
        var routeCts = ResetRouteCancellation();
        var token = routeCts.Token;

        _loading = true;
        _notFound = false;
        _busy = false;
        _detail = null;
        _node = null;
        _nodeIndex = 0;
        _resolvedRevisionId = null;
        _model = ProcessingGraphDraftModel.Empty();
        _preview = null;
        _savedRevisionId = null;
        _saveKey = null;
        _savePayload = null;
        _message = null;
        _messageIsError = false;
        try
        {
            var revisionId = string.IsNullOrWhiteSpace(RevisionId) ? null : RevisionId.Trim();
            if (revisionId is null)
            {
                var registry = await GraphService.GetRegistryAsync(token).ConfigureAwait(true);
                if (IsStale(loadGeneration, token))
                {
                    return;
                }
                if (registry.Kind == OperatorUiResultKind.Unauthorized)
                {
                    NavigationManager.NavigateTo("/Account/AccessDenied");
                    return;
                }
                if (!registry.IsSuccess || string.IsNullOrWhiteSpace(registry.Value?.ActiveRevisionId))
                {
                    SetMessage(registry.Message ?? "The active graph revision could not be read.", error: true);
                    return;
                }
                revisionId = registry.Value.ActiveRevisionId;
            }
            _resolvedRevisionId = revisionId;

            var result = await GraphService.GetRevisionDetailAsync(revisionId, token).ConfigureAwait(true);
            if (IsStale(loadGeneration, token))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (!result.IsSuccess || result.Value is null)
            {
                _notFound = result.Kind == OperatorUiResultKind.NotFound;
                SetMessage(result.Message ?? "The graph revision could not be read.", error: !_notFound);
                return;
            }

            _detail = result.Value;
            _model = ProcessingGraphDraftModel.FromPipeline(
                _detail.Pipeline,
                _detail.State.RevisionId,
                _detail.State.Name,
                ProcessingGraphEditorPage.NextRevisionLabel(_detail.State.Revision));

            var targetId = (QueryNodeId ?? NodeId ?? string.Empty).Trim();
            _nodeIndex = _model.Nodes.FindIndex(row => string.Equals(row.Id, targetId, StringComparison.Ordinal));
            if (_nodeIndex < 0)
            {
                _nodeIndex = _model.Nodes.FindIndex(row => string.Equals(row.Id, targetId, StringComparison.OrdinalIgnoreCase));
            }
            if (_nodeIndex < 0)
            {
                _notFound = true;
                SetMessage($"Node '{targetId}' was not found in revision '{revisionId}'.", error: false);
                return;
            }
            _node = _model.Nodes[_nodeIndex];
        }
        catch (OperationCanceledException) when (_disposed || token.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposed && loadGeneration == _draftGeneration)
            {
                _loading = false;
            }
        }
    }

    private void OnNodeEnabledChanged(bool value)
    {
        if (_node is null || _node.Enabled == value)
        {
            return;
        }
        _node.Enabled = value;
        _model.EnsureRegionalVisibilityWiring(_node);
    }

    private void FormChanged()
    {
        _draftGeneration++;
        CancelAndResetPreview();
        _preview = null;
        _message = null;
        _savedRevisionId = null;
        _busy = false;
    }

    private async Task PreviewAsync()
    {
        _preview = null;
        if (!_model.TryBuild(out var pipeline, out var errors))
        {
            SetMessage(string.Join(' ', errors), error: true);
            return;
        }
        var generation = _draftGeneration;
        var sourceRevisionId = _model.SourceRevisionId;
        var nodeId = _node?.Id;
        var name = _model.Name.Trim();
        var revision = _model.Revision.Trim();
        var graphChanged = _model.HasGraphChanges(pipeline);
        var previewCts = ResetPreviewCancellation();
        var token = previewCts.Token;

        _busy = true;
        try
        {
            var result = await GraphService.PreviewAsync(pipeline, sourceRevisionId, token).ConfigureAwait(true);
            if (IsStale(generation, token) || !MatchesActiveDraft(sourceRevisionId, nodeId))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                _preview = new PreviewSnapshot(generation, sourceRevisionId, nodeId, name, revision, pipeline, result.Value, graphChanged);
                SetMessage(graphChanged
                    ? "The draft compiles. No durable state changed."
                    : "No graph changes. The source revision is unchanged; no new revision is needed.", error: false);
            }
            else
            {
                _preview = null;
                SetMessage(result.Message ?? "The draft does not compile.", error: true);
            }
        }
        catch (OperationCanceledException) when (_disposed || token.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposed && generation == _draftGeneration)
            {
                _busy = false;
            }
        }
    }

    private async Task SaveDraftAsync()
    {
        var snapshot = _preview;
        if (snapshot is null || !snapshot.GraphChanged || _savedRevisionId is not null ||
            snapshot.DraftGeneration != _draftGeneration ||
            !MatchesActiveDraft(snapshot.SourceRevisionId, snapshot.NodeId) ||
            !string.Equals(snapshot.Name, _model.Name.Trim(), StringComparison.Ordinal) ||
            !string.Equals(snapshot.Revision, _model.Revision.Trim(), StringComparison.Ordinal))
        {
            return;
        }
        var signature = string.Join('\n', snapshot.Name, snapshot.Revision, snapshot.SourceRevisionId, snapshot.NodeId, snapshot.Plan.DesiredSha256, snapshot.Plan.EffectiveSha256);
        if (!string.Equals(_savePayload, signature, StringComparison.Ordinal))
        {
            _savePayload = signature;
            _saveKey = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        }
        var token = (_routeCts ?? _lifetimeCts).Token;
        _busy = true;
        OperatorUiResult<ProcessingGraphRevisionState> result;
        try
        {
            result = await GraphService.CreateRevisionAsync(
                snapshot.Name,
                snapshot.Revision,
                snapshot.Pipeline,
                _saveKey!,
                snapshot.SourceRevisionId is null ? "node configuration draft" : $"node '{snapshot.NodeId}' draft from {snapshot.SourceRevisionId}",
                snapshot.SourceRevisionId,
                token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed || token.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (!_disposed && snapshot.DraftGeneration == _draftGeneration)
            {
                _busy = false;
            }
        }
        if (IsStale(snapshot.DraftGeneration, token) || !MatchesActiveDraft(snapshot.SourceRevisionId, snapshot.NodeId))
        {
            return;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _saveKey = null;
            _savePayload = null;
        }
        if (result.IsSuccess && result.Value is { } created)
        {
            _savedRevisionId = created.RevisionId;
            SetMessage($"Draft {created.Name} {created.Revision} saved as an immutable revision. Validate and activate it from Named graphs.", error: false);
        }
        else
        {
            SetMessage(result.Message ?? "The draft could not be saved.", error: true);
        }
    }

    private bool CanLinkToSourceRevision(ProcessingGraphDraftModel.NodeRow row)
        => _resolvedRevisionId is not null &&
           (_model.ExistsInSourceRevision(row) || (_preview is { GraphChanged: false } && _savedRevisionId is null));

    private bool IsStale(long generation, CancellationToken token)
        => _disposed || token.IsCancellationRequested || generation != _draftGeneration;

    private bool MatchesActiveDraft(string? sourceRevisionId, string? nodeId)
        => string.Equals(_model.SourceRevisionId, sourceRevisionId, StringComparison.Ordinal) &&
           string.Equals(_node?.Id, nodeId, StringComparison.Ordinal);

    private CancellationTokenSource ResetRouteCancellation()
    {
        var previous = _routeCts;
        _routeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
        return _routeCts;
    }

    private CancellationTokenSource ResetPreviewCancellation()
    {
        CancelAndResetPreview();
        var parent = _routeCts ?? _lifetimeCts;
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        return _previewCts;
    }

    private void CancelAndResetPreview()
    {
        var previous = _previewCts;
        _previewCts = null;
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _draftGeneration++;
        CancelAndResetPreview();
        if (_routeCts is not null)
        {
            _routeCts.Cancel();
            _routeCts.Dispose();
            _routeCts = null;
        }
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}
