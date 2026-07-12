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
    private static readonly TimeSpan DefaultInitialFailureDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultMaximumFailureDelay = TimeSpan.FromSeconds(30);
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
        var initialFailureDelay = config.Rig.Pipeline.CaptureFailureInitialDelay ?? DefaultInitialFailureDelay;
        var maximumFailureDelay = config.Rig.Pipeline.CaptureFailureMaximumDelay ?? DefaultMaximumFailureDelay;
        var nextRequest = new CaptureRequest(
            RequestedStartUtc: _timeProvider.GetUtcNow(),
            TargetInterval: targetInterval,
            Mode: CaptureMode.Still,
            RequestedSetpoint: new CaptureSetpoint(config.Rig.Pipeline.NightExposure, config.Rig.Pipeline.NightGain, null, null));
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var loopStart = _timeProvider.GetUtcNow();
            nextRequest = nextRequest with { RequestedStartUtc = loopStart };
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
                consecutiveFailures++;
                if (ShouldLogFailure(consecutiveFailures, initialFailureDelay, maximumFailureDelay))
                {
                    _logger.CaptureLoopFailed(ex);
                }
                if (!await DelayAfterFailureAsync(
                        consecutiveFailures, initialFailureDelay, maximumFailureDelay, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
                continue;
            }

            if (result is null)
            {
                consecutiveFailures++;
                if (!await DelayAfterFailureAsync(
                        consecutiveFailures, initialFailureDelay, maximumFailureDelay, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
                continue;
            }
            if (consecutiveFailures > 0)
            {
                _logger.CaptureRecovered(consecutiveFailures);
                consecutiveFailures = 0;
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

    private async Task<bool> DelayAfterFailureAsync(
        int consecutiveFailures,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        CancellationToken cancellationToken)
    {
        var delay = CalculateFailureDelay(consecutiveFailures, initialDelay, maximumDelay);
        if (ShouldLogFailure(consecutiveFailures, initialDelay, maximumDelay))
        {
            _logger.CaptureFailureBackoff(consecutiveFailures, delay.TotalMilliseconds);
        }
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    internal static TimeSpan CalculateFailureDelay(int consecutiveFailures)
        => CalculateFailureDelay(consecutiveFailures, DefaultInitialFailureDelay, DefaultMaximumFailureDelay);

    internal static TimeSpan CalculateFailureDelay(
        int consecutiveFailures,
        TimeSpan initialDelay,
        TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(consecutiveFailures, 1);
        var exponent = consecutiveFailures - 1d;
        var milliseconds = initialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, maximumDelay.TotalMilliseconds));
    }

    private static bool ShouldLogFailure(int failures, TimeSpan initialDelay, TimeSpan maximumDelay)
        => failures == 1 || CalculateFailureDelay(failures, initialDelay, maximumDelay) !=
            CalculateFailureDelay(failures - 1, initialDelay, maximumDelay);

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
