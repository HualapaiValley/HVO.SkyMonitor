using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class FrameProcessingWorker
{
    private readonly FrameProcessingChannel _channel;
    private readonly IReadOnlyList<ICaptureProcessingStep> _steps;
    private readonly ILogger _logger;

    public FrameProcessingWorker(
        FrameProcessingChannel channel,
        IEnumerable<ICaptureProcessingStep> steps,
        ILogger logger)
    {
        _channel = channel;
        _steps = steps?
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.Name, StringComparer.Ordinal)
            .ToList() ?? throw new ArgumentNullException(nameof(steps));
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in _channel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Processing must continue even if individual steps fail.")]
    private async ValueTask ProcessItemAsync(FrameProcessingItem item, CancellationToken cancellationToken)
    {
        var context = new CaptureProcessingContext(item.Config, item.Submission);
        foreach (var step in _steps)
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
                _logger.CaptureProcessingStepFailed(step.Name, context.Submission.CaptureStartedUtc, ex);
                errorMessage = ex.Message;
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
    }
}
