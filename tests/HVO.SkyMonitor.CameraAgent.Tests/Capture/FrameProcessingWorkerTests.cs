using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
public sealed class FrameProcessingWorkerTests
{
    [TestMethod]
    public async Task RunAsync_InvokesAllSteps()
    {
        var channel = new FrameProcessingChannel(2);
        var recordingStep = new RecordingStep();
        var worker = new FrameProcessingWorker(channel, new[] { recordingStep }, NullLogger.Instance);

        var submission = CreateSubmission();
        await channel.WriteAsync(new FrameProcessingItem(CreateConfig(), submission), CancellationToken.None);
        channel.Complete();

        await worker.RunAsync(CancellationToken.None);

        Assert.AreEqual(1, recordingStep.Invocations);
    }

    [TestMethod]
    public async Task RunAsync_ContinuesWhenStepThrows()
    {
        var channel = new FrameProcessingChannel(2);
        var throwingStep = new ThrowingStep();
        var recordingStep = new RecordingStep();
        var worker = new FrameProcessingWorker(channel, new ICaptureProcessingStep[] { throwingStep, recordingStep }, NullLogger.Instance);

        var submission = CreateSubmission();
        await channel.WriteAsync(new FrameProcessingItem(CreateConfig(), submission), CancellationToken.None);
        channel.Complete();

        await worker.RunAsync(CancellationToken.None);

        Assert.AreEqual(1, recordingStep.Invocations, "Recording step should execute even if earlier steps fail.");
    }

    [TestMethod]
    public async Task RunAsync_RespectsStepOrdering()
    {
        var channel = new FrameProcessingChannel(2);
        var invocations = new List<string>();
        var lateStep = new OrderedRecordingStep("Late", 100, invocations);
        var earlyStep = new OrderedRecordingStep("Early", -100, invocations);
        var worker = new FrameProcessingWorker(channel, new ICaptureProcessingStep[] { lateStep, earlyStep }, NullLogger.Instance);

        await channel.WriteAsync(new FrameProcessingItem(CreateConfig(), CreateSubmission()), CancellationToken.None);
        channel.Complete();

        await worker.RunAsync(CancellationToken.None);

        Assert.AreEqual(2, invocations.Count);
        Assert.AreEqual("Early", invocations[0]);
        Assert.AreEqual("Late", invocations[1]);
    }

    private static CaptureLoopSubmission CreateSubmission()
    {
        var frame = new CameraFrame(
            DateTimeOffset.UtcNow,
            4,
            4,
            CameraPixelFormat.Mono8,
            new byte[16],
            new FrameMetadata(TimeSpan.FromMilliseconds(10), 1.0, 20));

        var result = new CaptureResult(
            frame,
            new CaptureSetpoint(TimeSpan.FromMilliseconds(10), 1.0, TimeSpan.FromMilliseconds(5), null),
            TimeSpan.FromMilliseconds(2),
            CaptureMode.Still,
            false);

        var request = new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50), CaptureMode.Still);
        return new CaptureLoopSubmission(request, result, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5));
    }

    private static CameraModuleConfig CreateConfig()
    {
        return TestCameraModuleConfigFactory.Create();
    }

    private sealed class RecordingStep : ICaptureProcessingStep
    {
        public int Invocations { get; private set; }

        public string Name => nameof(RecordingStep);

        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OrderedRecordingStep : ICaptureProcessingStep
    {
        private readonly string _name;
        private readonly int _order;
        private readonly List<string> _invocations;

        public OrderedRecordingStep(string name, int order, List<string> invocations)
        {
            _name = name;
            _order = order;
            _invocations = invocations;
        }

        public string Name => _name;

        public int Order => _order;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            _invocations.Add(_name);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingStep : ICaptureProcessingStep
    {
        public string Name => nameof(ThrowingStep);

        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Processing failed.");
    }
}
