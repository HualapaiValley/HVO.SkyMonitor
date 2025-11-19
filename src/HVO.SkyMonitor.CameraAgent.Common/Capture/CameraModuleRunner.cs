using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class CameraModuleRunner
{
    private readonly ICameraModule _module;
    private readonly ICaptureHostContext _hostContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public CameraModuleRunner(
        ICameraModule module,
        ICaptureHostContext hostContext,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _module = module;
        _hostContext = hostContext;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Capture loop must continue after transient module failures.")]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var config = _hostContext.Configuration;
        var targetInterval = config.Rig.Pipeline.CaptureInterval;
        var nextRequest = new CaptureRequest(
            RequestedStartUtc: _timeProvider.GetUtcNow(),
            TargetInterval: targetInterval,
            Mode: CaptureMode.Still);

        while (!cancellationToken.IsCancellationRequested)
        {
            var loopStart = _timeProvider.GetUtcNow();
            CaptureResult? result = null;
            try
            {
                result = await _module.CaptureAsync(nextRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.CaptureLoopFailed(ex);
                continue;
            }

            if (result is null)
            {
                continue;
            }

            var elapsed = _timeProvider.GetUtcNow() - loopStart;
            var effectiveInterval = result.NextSetpoint.NextIntervalOverride ?? nextRequest.TargetInterval;

            var submission = new CaptureLoopSubmission(
                Request: nextRequest,
                Result: result,
                CaptureStartedUtc: loopStart,
                EffectiveInterval: effectiveInterval,
                LoopDuration: elapsed);

            await _hostContext.PublishAsync(submission, cancellationToken).ConfigureAwait(false);

            var delay = effectiveInterval - elapsed;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            nextRequest = new CaptureRequest(
                RequestedStartUtc: _timeProvider.GetUtcNow(),
                TargetInterval: effectiveInterval,
                Mode: result.Mode);
        }
    }
}
