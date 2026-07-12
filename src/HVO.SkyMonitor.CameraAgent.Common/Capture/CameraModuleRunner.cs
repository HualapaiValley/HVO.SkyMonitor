using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Exposure;
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
            Mode: CaptureMode.Still,
            RequestedSetpoint: new CaptureSetpoint(config.Rig.Pipeline.NightExposure, config.Rig.Pipeline.NightGain, null, null));

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

            var brightness = MeasureNormalizedBrightness(result.Frame);
            var decision = ExposureController.Next(config.Rig.Pipeline, brightness, night: true);
            var nextSetpoint = ApplyControlPolicy(config.Rig.ControlPolicy, nextRequest.RequestedSetpoint!, decision.Setpoint);
            nextRequest = new CaptureRequest(
                RequestedStartUtc: _timeProvider.GetUtcNow(),
                TargetInterval: effectiveInterval,
                Mode: result.Mode,
                RequestedSetpoint: nextSetpoint);
        }
    }

    internal static CaptureSetpoint ApplyControlPolicy(
        CameraControlPolicy? policy,
        CaptureSetpoint current,
        CaptureSetpoint automatic)
        => automatic with
        {
            Exposure = policy?.AutoExposure == CameraFeatureDirective.Disabled
                ? current.Exposure
                : automatic.Exposure,
            Gain = policy?.AutoGain == CameraFeatureDirective.Disabled
                ? current.Gain
                : automatic.Gain
        };

    private static double? MeasureNormalizedBrightness(CameraFrame? frame)
    {
        if (frame is null || frame.PixelData.IsEmpty)
        {
            return null;
        }

        return frame.PixelFormat switch
        {
            CameraPixelFormat.Mono8 => AverageBytes(frame, 1),
            CameraPixelFormat.Mono16 => AverageMono16(frame),
            CameraPixelFormat.BayerRggb16 => AverageMono16(frame),
            CameraPixelFormat.Rgb24 => AverageBytes(frame, 3),
            _ => null
        };
    }

    private static double? AverageMono16(CameraFrame frame)
    {
        var data = frame.PixelData.Span;
        var packedStride = checked(frame.Width * 2);
        var stride = frame.StrideBytes ?? packedStride;
        if (frame.Width <= 0 || frame.Height <= 0 || stride < packedStride || data.Length < checked(stride * frame.Height))
        {
            return null;
        }

        ulong total = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var index = y * stride + x * 2;
                total += (ushort)(data[index] | data[index + 1] << 8);
            }
        }

        return total / (double)(frame.Width * frame.Height) / ushort.MaxValue;
    }

    private static double? AverageBytes(CameraFrame frame, int bytesPerPixel)
    {
        var data = frame.PixelData.Span;
        var packedStride = checked(frame.Width * bytesPerPixel);
        var stride = frame.StrideBytes ?? packedStride;
        if (frame.Width <= 0 || frame.Height <= 0 || stride < packedStride || data.Length < checked(stride * frame.Height))
        {
            return null;
        }

        ulong total = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            foreach (var value in data.Slice(y * stride, packedStride))
            {
                total += value;
            }
        }

        return total / (double)(packedStride * frame.Height) / byte.MaxValue;
    }
}
