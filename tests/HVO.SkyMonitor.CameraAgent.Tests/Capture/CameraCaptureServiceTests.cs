using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraCaptureServiceTests
{
    [TestMethod]
    public async Task StopAsync_CancelsCaptureAndDisposesModule()
    {
        var config = CreateConfig();
        var module = new GatedCameraModule();
        var distributor = new RecordingDistributor();
        var service = new CameraCaptureService(
            new ConfigurationAccessor(config),
            new ModuleFactory(module),
            new PassthroughRawIngress(),
            distributor,
            TimeProvider.System,
            NullLogger<CameraCaptureService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await module.SecondCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await module.CaptureCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(1, distributor.EphemeralCount);
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

    private sealed class PassthroughRawIngress : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
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

    private sealed class RecordingDistributor : ICaptureDistributor
    {
        public int EphemeralCount { get; private set; }

        public void NotifyCommittedCapture()
        {
        }

        public ValueTask ProcessEphemeralAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            EphemeralCount++;
            return ValueTask.CompletedTask;
        }
    }
}
