using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
public sealed class CalibrationCaptureProcessingStepTests
{
    [TestMethod]
    public async Task ProcessAsync_WhenFrameMissing_SkipsCalibration()
    {
        var step = CreateStep(new TestCalibrator());
        var context = CreateContext(frame: null);

        await step.ProcessAsync(context, CancellationToken.None);

        Assert.IsNull(context.Frame);
    }

    [TestMethod]
    public async Task ProcessAsync_ReplacesFrameWhenCalibrated()
    {
        var originalFrame = CreateFrame(10);
        var calibratedFrame = CreateFrame(20);
        var calibrator = new TestCalibrator(calibratedFrame);
        var step = CreateStep(calibrator);
        var context = CreateContext(originalFrame);

        await step.ProcessAsync(context, CancellationToken.None);

        Assert.AreSame(calibratedFrame, context.Frame);
        Assert.AreNotSame(originalFrame, context.Frame);
        Assert.AreSame(calibratedFrame, context.Submission.Result.Frame);
    }

    private static CaptureProcessingContext CreateContext(CameraFrame? frame)
    {
        var exposure = TimeSpan.FromMilliseconds(10);
        var setpoint = new CaptureSetpoint(exposure, 2.0, exposure, null);
        var result = new CaptureResult(frame, setpoint, TimeSpan.FromMilliseconds(2), CaptureMode.Still, false);
        var request = new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50), CaptureMode.Still);
        var submission = new CaptureLoopSubmission(request, result, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5));
        return new CaptureProcessingContext(CreateConfig(), submission);
    }

    private static CameraModuleConfig CreateConfig()
    {
        return TestCameraModuleConfigFactory.Create();
    }

    private static CameraFrame CreateFrame(byte seed)
        => new(
            DateTimeOffset.UtcNow,
            2,
            2,
            CameraPixelFormat.Mono8,
            new byte[] { seed, seed, seed, seed },
            new FrameMetadata(TimeSpan.FromMilliseconds(10), 2.0, 20));

    private static CalibrationCaptureProcessingStep CreateStep(ICaptureCalibrationProcessor processor)
        => new(
            new CaptureProcessingStepMetadata("Calibration", "Calibration", int.MinValue),
            new CalibrationProcessingStepOptions(),
            processor,
            NullLogger<CalibrationCaptureProcessingStep>.Instance);

    private sealed class TestCalibrator : ICaptureCalibrationProcessor
    {
        private readonly CameraFrame? _calibratedFrame;

        public TestCalibrator(CameraFrame? calibratedFrame = null)
        {
            _calibratedFrame = calibratedFrame;
        }

        public ValueTask<CameraFrame> ApplyCalibrationAsync(CameraModuleConfig config, CameraFrame frame, CancellationToken cancellationToken)
            => ValueTask.FromResult(_calibratedFrame ?? frame);
    }
}
