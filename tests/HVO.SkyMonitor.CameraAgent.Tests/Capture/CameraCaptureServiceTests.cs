using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
public sealed class CameraCaptureServiceTests
{
    [TestMethod]
    public async Task StopAsync_DrainsAcceptedFrameBeforeDisposingModule()
    {
        var config = CreateConfig();
        var module = new GatedCameraModule();
        var step = new GatedProcessingStep();
        var service = new CameraCaptureService(
            new ConfigurationAccessor(config),
            new ModuleFactory(module),
            new PipelineFactory(step),
            TimeProvider.System,
            NullLogger<CameraCaptureService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await step.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await module.SecondCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var stopTask = service.StopAsync(CancellationToken.None);
        await module.CaptureCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false);

        Assert.IsFalse(stopTask.IsCompleted);
        Assert.IsFalse(module.IsDisposed);
        Assert.IsFalse(step.ProcessingTokenWasCanceled);

        step.Release.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(1, step.ProcessedCount);
        Assert.IsTrue(module.IsDisposed);
    }

    [TestMethod]
    public async Task StopAsync_WhenDrainDeadlineExpires_AbortsProcessingAndDisposesModule()
    {
        var module = new GatedCameraModule();
        var step = new GatedProcessingStep();
        var service = new CameraCaptureService(
            new ConfigurationAccessor(CreateConfig()), new ModuleFactory(module), new PipelineFactory(step),
            TimeProvider.System, NullLogger<CameraCaptureService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await step.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await module.SecondCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await service.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await module.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(0, step.ProcessedCount);
        Assert.IsTrue(module.IsDisposed);
    }

    private static CameraModuleConfig CreateConfig()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1), 0, 0)));

    private sealed class ConfigurationAccessor(CameraModuleConfig config) : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => true;

        public void SetConfiguration(CameraModuleConfig value) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(config);
    }

    private sealed class ModuleFactory(ICameraModule module) : ICameraModuleFactory
    {
        public ICameraModule Create(string moduleType) => module;
    }

    private sealed class PipelineFactory(ICaptureProcessingStep step) : ICaptureProcessingPipelineFactory
    {
        public IReadOnlyList<ICaptureProcessingStep> CreatePipeline(CameraModuleConfig config) => [step];
    }

    private sealed class GatedCameraModule : ICameraModule
    {
        private int _captureCount;

        public TaskCompletionSource SecondCaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CaptureCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "test";

        public string DisplayName => "Test";

        public string ModuleType => "Test";

        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _captureCount) == 1)
            {
                var frame = new CameraFrame(
                    DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                    new FrameMetadata(TimeSpan.FromMilliseconds(1), 0, 0));
                return new CaptureResult(
                    frame, new CaptureSetpoint(TimeSpan.FromMilliseconds(1), 0, null, null),
                    TimeSpan.Zero, CaptureMode.Still, false);
            }

            SecondCaptureStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Infinite delay completed without cancellation.");
            }
            catch (OperationCanceledException)
            {
                CaptureCancellationObserved.TrySetResult();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedProcessingStep : ICaptureProcessingStep
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ProcessingTokenWasCanceled { get; private set; }

        public int ProcessedCount { get; private set; }

        public string Name => "Gated";

        public int Order => 0;

        public async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ProcessingTokenWasCanceled = cancellationToken.IsCancellationRequested;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            ProcessedCount++;
        }
    }
}
