using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal sealed class StandardCaptureLaneHandler(
    ICaptureProcessingPipelineFactory pipelineFactory,
    CaptureProcessingPersistence? processingPersistence,
    CaptureProcessingTelemetry processingTelemetry,
    ILogger<StandardCaptureLaneHandler> logger,
    IRawCaptureIngress rawCaptureIngress,
    IOptions<CameraAgentHostOptions>? hostOptions = null) : ICaptureLaneHandler, IDisposable
{
    private readonly ICaptureProcessingPipelineFactory _pipelineFactory = pipelineFactory;
    private readonly CaptureProcessingPersistence? _processingPersistence = processingPersistence;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The telemetry singleton is owned and disposed by the dependency injection container.")]
    private readonly CaptureProcessingTelemetry _processingTelemetry = processingTelemetry;
    private readonly ILogger<StandardCaptureLaneHandler> _logger = logger;
    private readonly IRawIngressRecoveryControl? _rawIngressControl = rawCaptureIngress as IRawIngressRecoveryControl;
    private readonly int _maximumAttempts = hostOptions?.Value.CaptureDistribution.MaximumAttempts ?? 5;
    private readonly object _pipelineGate = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private CaptureProcessingGraph? _graph;
    private string? _pipelineKey;
    private bool _ownsProcessingTelemetry;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The compatibility constructor transfers telemetry ownership to this disposable handler.")]
    internal StandardCaptureLaneHandler(
        ICaptureProcessingPipelineFactory pipelineFactory,
        ILogger<StandardCaptureLaneHandler> logger,
        IRawCaptureIngress rawCaptureIngress)
        : this(pipelineFactory, null, new CaptureProcessingTelemetry(), logger, rawCaptureIngress, null)
    {
        _ownsProcessingTelemetry = true;
    }

    public string Lane => "standard";

    public ValueTask<CaptureLaneHandlerResult> HandleAsync(
        CaptureLaneHandlerContext context,
        CancellationToken cancellationToken)
        => ProcessAsync(
            new FrameProcessingItem(
                context.Configuration,
                context.Submission,
                context.RawCapture,
                context.WorkId,
                context.LeaseToken),
            context.Attempt,
            cancellationToken);

    internal ValueTask<CaptureLaneHandlerResult> ProcessEphemeralAsync(
        HVO.SkyMonitor.AgentCore.CameraModuleConfig configuration,
        HVO.SkyMonitor.AgentCore.CaptureLoopSubmission submission,
        CancellationToken cancellationToken)
        => ProcessAsync(new FrameProcessingItem(configuration, submission), 1, cancellationToken);

    private async ValueTask<CaptureLaneHandlerResult> ProcessAsync(
        FrameProcessingItem item,
        int attempt,
        CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pipelineKey = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(item.Config.ResolveProcessingSteps())));
            CaptureProcessingGraph graph;
            lock (_pipelineGate)
            {
                if (_graph is null || !string.Equals(_pipelineKey, pipelineKey, StringComparison.Ordinal))
                {
                    _graph?.DisposeSteps();
                    _graph = _pipelineFactory.CreateGraph(item.Config);
                    _pipelineKey = pipelineKey;
                }
                graph = _graph;
            }
            try
            {
                return await FrameProcessingWorker.ProcessGraphItemAsync(
                    item,
                    graph,
                    item.RawCapture is null ? null : _processingPersistence,
                    _processingTelemetry,
                    attempt,
                    _logger,
                    cancellationToken,
                    _maximumAttempts).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _rawIngressControl?.InvalidateEvidence();
                return CaptureLaneHandlerResult.Terminal("evidence-unavailable");
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public void Dispose()
    {
        _graph?.DisposeSteps();
        if (_ownsProcessingTelemetry)
        {
            _processingTelemetry.Dispose();
        }
        _executionGate.Dispose();
    }
}
