using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProcessingGraphEditorPage : ComponentBase, IAsyncDisposable
{
    private ProcessingGraphDraftModel _model = ProcessingGraphDraftModel.Empty();
    private CaptureProcessingPlanPreview? _plan;
    private CapturePipelineConfig? _previewedPipeline;
    private string? _from;
    private string _newType = string.Empty;
    private string? _message;
    private bool _messageIsError;
    private bool _loading = true;
    private bool _busy;
    private string? _savedRevisionId;
    private string? _saveKey;
    private string? _savePayload;
    private IJSObjectReference? _module;

    [SupplyParameterFromQuery(Name = "from")]
    public string? From { get; set; }

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        _from = string.IsNullOrWhiteSpace(From) ? null : From.Trim();
        _newType = GraphService.StepAliases.Count > 0 ? GraphService.StepAliases[0] : string.Empty;
        if (_from is null)
        {
            _model = ProcessingGraphDraftModel.Empty();
            _loading = false;
            return;
        }
        var result = await GraphService.GetRevisionDetailAsync(_from, CancellationToken.None).ConfigureAwait(false);
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (result.IsSuccess && result.Value is { } detail)
        {
            _model = ProcessingGraphDraftModel.FromPipeline(detail.Pipeline, detail.State.RevisionId, detail.State.Name, NextRevisionLabel(detail.State.Revision));
        }
        else
        {
            _model = ProcessingGraphDraftModel.Empty();
            SetMessage(result.Message ?? "The source revision could not be read; starting an empty draft.", error: true);
        }
        _loading = false;
    }

    private void FormChanged()
    {
        _plan = null;
        _previewedPipeline = null;
        _message = null;
        _savedRevisionId = null;
    }

    private void AddNode()
    {
        if (string.IsNullOrWhiteSpace(_newType))
        {
            return;
        }
        _model.AddNode(_newType);
        FormChanged();
    }

    private void Remove(ProcessingGraphDraftModel.NodeRow node)
    {
        _model.Remove(node);
        FormChanged();
    }

    private void Move(ProcessingGraphDraftModel.NodeRow node, int delta)
    {
        _model.Move(node, delta);
        FormChanged();
    }

    private void ToggleDependency(ProcessingGraphDraftModel.NodeRow node, string dependency, bool selected)
    {
        node.ToggleDependency(dependency, selected);
        FormChanged();
    }

    private async Task PreviewAsync()
    {
        if (!_model.TryBuild(out var pipeline, out var errors))
        {
            SetMessage(string.Join(' ', errors), error: true);
            return;
        }
        _busy = true;
        try
        {
            var result = await GraphService.PreviewAsync(pipeline, CancellationToken.None).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                _plan = result.Value;
                _previewedPipeline = pipeline;
                SetMessage("The draft compiles. No durable state changed.", error: false);
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
        if (_previewedPipeline is null)
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
                _model.Name.Trim(), _model.Revision.Trim(), _previewedPipeline, _saveKey!, _model.SourceRevisionId is null ? "operator draft" : $"draft from {_model.SourceRevisionId}", CancellationToken.None).ConfigureAwait(false);
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
            SetMessage($"Draft {created.Name} {created.Revision} saved as an immutable revision. Validate and activate it from the named graphs list.", error: false);
        }
        else
        {
            SetMessage(result.Message ?? "The draft could not be saved.", error: true);
        }
    }

    private async Task FocusRowAsync(string nodeId)
    {
        var row = _model.Nodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }
        _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/SchedulePage.razor.js").ConfigureAwait(false);
        await _module.InvokeVoidAsync("focusById", RowId(row), "page-heading").ConfigureAwait(false);
    }

    internal static string RowId(ProcessingGraphDraftModel.NodeRow node) => $"graph-node-{node.Id}";

    internal static string NextRevisionLabel(string current)
        => int.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? (number + 1).ToString(CultureInfo.InvariantCulture)
            : $"{current}-next";

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
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
