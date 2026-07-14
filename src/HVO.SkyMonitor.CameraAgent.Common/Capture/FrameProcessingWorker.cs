using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class FrameProcessingWorker
{
    private readonly FrameProcessingChannel _channel;
    private readonly IReadOnlyList<ICaptureProcessingStep> _steps;
    private readonly ILogger _logger;
    private readonly IRawIngressRecoveryControl? _rawIngressControl;

    public FrameProcessingWorker(
        FrameProcessingChannel channel,
        IEnumerable<ICaptureProcessingStep> steps,
        ILogger logger,
        IRawIngressRecoveryControl? rawIngressControl = null)
    {
        _channel = channel;
        _steps = steps?
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.Name, StringComparer.Ordinal)
            .ToList() ?? throw new ArgumentNullException(nameof(steps));
        _logger = logger;
        _rawIngressControl = rawIngressControl;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in _channel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessItemAsync(item, _steps, _logger, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _rawIngressControl?.InvalidateEvidence();
                _channel.Complete();
                throw;
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Processing must continue even if individual steps fail.")]
    internal static async ValueTask<CaptureLaneHandlerResult> ProcessItemAsync(
        FrameProcessingItem item,
        IReadOnlyList<ICaptureProcessingStep> steps,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var submission = item.Submission;
        var rawCapture = item.RawCapture;
        if (rawCapture is { } receipt)
        {
            var sidecar = await File.ReadAllBytesAsync(
                Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json"), cancellationToken).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(sidecar);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } persistedManifest)
            {
                throw new InvalidDataException($"Committed raw sidecar could not be parsed ({parsed.Validation.ReasonCode}).");
            }
            receipt = receipt with { Manifest = persistedManifest };
            rawCapture = receipt;
            var payload = await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath, cancellationToken).ConfigureAwait(false);
            var reconstruction = FrameReconstructor.TryReconstruct(receipt.Manifest.Descriptor, payload, out var frame);
            if (!reconstruction.IsValid || frame is null)
            {
                throw new InvalidDataException($"Committed raw evidence could not be reconstructed ({reconstruction.ReasonCode}).");
            }
            if (receipt.Manifest.Scene is not null)
            {
                frame = frame with { Metadata = frame.Metadata with { Scene = receipt.Manifest.Scene } };
            }
            var artifact = new FrameArtifact(
                receipt.Manifest.Descriptor.Artifact.ArtifactId,
                FrameArtifactRole.Raw,
                frame);
            submission = submission with
            {
                Result = submission.Result with
                {
                    Frame = frame,
                    Artifacts = new FrameArtifactSet(artifact)
                }
            };
        }
        var context = new CaptureProcessingContext(item.Config, submission, rawCapture);
        var failed = false;
        foreach (var step in steps)
        {
            var stopwatch = Stopwatch.StartNew();
            var succeeded = false;
            string? errorMessage = null;
            try
            {
                await step.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
                succeeded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.CaptureProcessingStepFailed(step.Name, context.Submission.CaptureStartedUtc, ex);
                errorMessage = ex.Message;
                failed = true;
            }
            finally
            {
                stopwatch.Stop();
                context.AddStepTelemetry(new CaptureProcessingStepTelemetry(
                    step.Name,
                    stopwatch.Elapsed,
                    succeeded,
                    errorMessage));
            }
        }
        return failed
            ? CaptureLaneHandlerResult.Retry("processing-step")
            : CaptureLaneHandlerResult.Success;
    }
}
