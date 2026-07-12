using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.AgentCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
public sealed class CameraModuleRunnerTests
{
    [TestMethod]
    [DataRow(1, 250)]
    [DataRow(2, 500)]
    [DataRow(3, 1000)]
    [DataRow(7, 16000)]
    [DataRow(8, 30000)]
    [DataRow(100, 30000)]
    public void CalculateFailureDelay_GrowsExponentiallyAndCaps(int failures, int expectedMilliseconds)
        => Assert.AreEqual(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            CameraModuleRunner.CalculateFailureDelay(failures));

    [TestMethod]
    public void CalculateFailureDelay_RejectsNonPositiveFailureCount()
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CameraModuleRunner.CalculateFailureDelay(0));

    [TestMethod]
    public void CalculateFailureDelay_UsesConfiguredInitialAndMaximum()
        => Assert.AreEqual(
            TimeSpan.FromSeconds(5),
            CameraModuleRunner.CalculateFailureDelay(
                4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));

    [TestMethod]
    public async Task RunAsync_RetriesExceptionAndNullWithFreshRequestedTimestamps()
    {
        using var cancellation = new CancellationTokenSource();
        var module = new SequenceModule();
        var context = new RecordingHostContext(CreateConfig(TimeSpan.FromMilliseconds(1)), cancellation);
        var runner = new CameraModuleRunner(module, context, TimeProvider.System, NullLogger.Instance);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(3, module.Requests.Count);
        Assert.IsTrue(module.Requests[1].RequestedStartUtc > module.Requests[0].RequestedStartUtc);
        Assert.IsTrue(module.Requests[2].RequestedStartUtc > module.Requests[1].RequestedStartUtc);
        Assert.AreEqual(module.Requests[2].RequestedStartUtc, context.Submission!.Request.RequestedStartUtc);
    }

    [TestMethod]
    public async Task RunAsync_CancellationDuringConfiguredBackoffPreventsAnotherAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var module = new AlwaysFailModule();
        var runner = new CameraModuleRunner(
            module, new RecordingHostContext(CreateConfig(TimeSpan.FromSeconds(5)), cancellation),
            TimeProvider.System, NullLogger.Instance);
        var run = runner.RunAsync(cancellation.Token);
        await module.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        await cancellation.CancelAsync().ConfigureAwait(false);
        await run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.AreEqual(1, module.Attempts);
    }

    private static CameraModuleConfig CreateConfig(TimeSpan initialBackoff)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0), new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1), 0, 0,
                    CaptureFailureInitialDelay: initialBackoff,
                    CaptureFailureMaximumDelay: TimeSpan.FromSeconds(5))));

    private sealed class RecordingHostContext(
        CameraModuleConfig configuration,
        CancellationTokenSource cancellation) : ICaptureHostContext
    {
        public CameraModuleConfig Configuration => configuration;
        public CaptureLoopSubmission? Submission { get; private set; }

        public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            Submission = submission;
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private sealed class SequenceModule : ICameraModule
    {
        public List<CaptureRequest> Requests { get; } = [];
        public string Id => "test";
        public string DisplayName => "Test";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Requests.Count switch
            {
                1 => Task.FromException<CaptureResult>(new IOException("failure")),
                2 => Task.FromResult<CaptureResult>(null!),
                _ => Task.FromResult(new CaptureResult(null,
                    new CaptureSetpoint(TimeSpan.FromMilliseconds(1), 0, null, null),
                    TimeSpan.Zero, CaptureMode.Still, false))
            };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AlwaysFailModule : ICameraModule
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; private set; }
        public string Id => "test";
        public string DisplayName => "Test";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            Attempts++;
            Attempted.TrySetResult();
            return Task.FromException<CaptureResult>(new IOException("failure"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
