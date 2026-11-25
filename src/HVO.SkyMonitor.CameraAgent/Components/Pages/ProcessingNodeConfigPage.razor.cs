using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProcessingNodeConfigPage : ComponentBase
{
    private CameraAgentProcessingGraphRevisionDetail? _detail;
    private ProcessingGraphDraftModel _model = ProcessingGraphDraftModel.Empty();
    private ProcessingGraphDraftModel.NodeRow? _node;
    private int _nodeIndex;
    private string? _resolvedRevisionId;
    private CaptureProcessingPlanPreview? _plan;
    private CapturePipelineConfig? _previewedPipeline;
    private string? _message;
    private bool _messageIsError;
    private bool _loading = true;
    private bool _notFound;
    private bool _busy;
    private bool _graphChanged;
    private long _draftGeneration;
    private string? _savedRevisionId;
    private string? _saveKey;
    private string? _savePayload;

    [Parameter] public string? RevisionId { get; set; }

    [Parameter] public string NodeId { get; set; } = string.Empty;

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
        : _node?.OptionsForm is { } form
            ? $"/operations/pipeline/graphs/new?from={Uri.EscapeDataString(_resolvedRevisionId)}#{ProcessingNodeSectionRegistry.SectionForAlias(form.Descriptor.StepAlias)}"
            : $"/operations/pipeline/graphs/new?from={Uri.EscapeDataString(_resolvedRevisionId)}";

    internal static string NodeConfigHref(string revisionId, string nodeId)
        => $"/operations/pipeline/graphs/{Uri.EscapeDataString(revisionId)}/nodes/{Uri.EscapeDataString(nodeId)}";

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        _notFound = false;
        _detail = null;
        _node = null;
        _nodeIndex = 0;
        _resolvedRevisionId = null;
        _model = ProcessingGraphDraftModel.Empty();
        _plan = null;
        _previewedPipeline = null;
        _graphChanged = false;
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
                var registry = await GraphService.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
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

            var result = await GraphService.GetRevisionDetailAsync(revisionId, CancellationToken.None).ConfigureAwait(false);
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

            var targetId = NodeId.Trim();
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
        finally
        {
            _loading = false;
        }
    }

    private void FormChanged()
    {
        _draftGeneration++;
        _plan = null;
        _previewedPipeline = null;
        _message = null;
        _savedRevisionId = null;
        _graphChanged = false;
    }

    private async Task PreviewAsync()
    {
        _plan = null;
        _previewedPipeline = null;
        if (!_model.TryBuild(out var pipeline, out var errors))
        {
            SetMessage(string.Join(' ', errors), error: true);
            return;
        }
        _busy = true;
        var generation = _draftGeneration;
        try
        {
            var result = await GraphService.PreviewAsync(pipeline, CancellationToken.None).ConfigureAwait(true);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (generation != _draftGeneration) return;
            if (result.IsSuccess && result.Value is not null)
            {
                _plan = result.Value;
                _previewedPipeline = pipeline;
                _graphChanged = _model.HasGraphChanges(pipeline);
                SetMessage(_graphChanged
                    ? "The draft compiles. No durable state changed."
                    : "No graph changes. The source revision is unchanged; no new revision is needed.", error: false);
            }
            else
            {
                _plan = null;
                _previewedPipeline = null;
                SetMessage(result.Message ?? "The draft does not compile.", error: true);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveDraftAsync()
    {
        if (_previewedPipeline is null || !_graphChanged || _savedRevisionId is not null)
        {
            return;
        }
        var signature = string.Join('\n', _model.Name, _model.Revision, _model.SourceRevisionId, _plan?.DesiredSha256, _plan?.EffectiveSha256);
        if (!string.Equals(_savePayload, signature, StringComparison.Ordinal))
        {
            _savePayload = signature;
            _saveKey = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        }
        _busy = true;
        OperatorUiResult<ProcessingGraphRevisionState> result;
        try
        {
            result = await GraphService.CreateRevisionAsync(
                _model.Name.Trim(),
                _model.Revision.Trim(),
                _previewedPipeline,
                _saveKey!,
                _model.SourceRevisionId is null ? "node configuration draft" : $"node '{_node?.Id}' draft from {_model.SourceRevisionId}",
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

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }
}
