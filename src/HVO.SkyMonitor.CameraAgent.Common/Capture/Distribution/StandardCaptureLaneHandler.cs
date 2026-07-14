using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal sealed class StandardCaptureLaneHandler(
    ICaptureProcessingPipelineFactory pipelineFactory,
    ILogger<StandardCaptureLaneHandler> logger,
    IRawCaptureIngress rawCaptureIngress) : ICaptureLaneHandler, IDisposable
{
    private readonly ICaptureProcessingPipelineFactory _pipelineFactory = pipelineFactory;
    private readonly ILogger<StandardCaptureLaneHandler> _logger = logger;
    private readonly IRawIngressRecoveryControl? _rawIngressControl = rawCaptureIngress as IRawIngressRecoveryControl;
    private readonly object _pipelineGate = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private IReadOnlyList<ICaptureProcessingStep>? _steps;
    private string? _pipelineKey;

    public string Lane => "standard";

    public ValueTask<CaptureLaneHandlerResult> HandleAsync(
        CaptureLaneHandlerContext context,
        CancellationToken cancellationToken)
        => ProcessAsync(
            new FrameProcessingItem(context.Configuration, context.Submission, context.RawCapture),
            cancellationToken);

    internal ValueTask<CaptureLaneHandlerResult> ProcessEphemeralAsync(
        HVO.SkyMonitor.AgentCore.CameraModuleConfig configuration,
        HVO.SkyMonitor.AgentCore.CaptureLoopSubmission submission,
        CancellationToken cancellationToken)
        => ProcessAsync(new FrameProcessingItem(configuration, submission), cancellationToken);

    private async ValueTask<CaptureLaneHandlerResult> ProcessAsync(
        FrameProcessingItem item,
        CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pipelineKey = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(item.Config.ResolveProcessingSteps())));
            IReadOnlyList<ICaptureProcessingStep> steps;
            lock (_pipelineGate)
            {
                if (_steps is null || !string.Equals(_pipelineKey, pipelineKey, StringComparison.Ordinal))
                {
                    foreach (var disposable in _steps?.OfType<IDisposable>() ?? [])
                    {
                        disposable.Dispose();
                    }
                    _steps = _pipelineFactory.CreatePipeline(item.Config)
                        .OrderBy(static step => step.Order)
                        .ThenBy(static step => step.Name, StringComparer.Ordinal)
                        .ToArray();
                    _pipelineKey = pipelineKey;
                }
                steps = _steps;
            }
            try
            {
                return await FrameProcessingWorker.ProcessItemAsync(
                    item,
                    steps,
                    _logger,
                    cancellationToken).ConfigureAwait(false);
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
        foreach (var disposable in _steps?.OfType<IDisposable>() ?? [])
        {
            disposable.Dispose();
        }
        _executionGate.Dispose();
    }
}
