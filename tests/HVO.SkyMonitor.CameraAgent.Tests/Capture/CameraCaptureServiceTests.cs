using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Extensions.Logging.Abstractions;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraCaptureServiceTests
{
    [TestMethod]
    public async Task DisposalFailure_FailsClosedWithoutOpeningReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-capture-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = CreateConfig();
            var ingress = new PassthroughRawIngress(root);
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = new CaptureAdmissionCoordinator(
                ingress,
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    RawIngressSqliteBusyTimeoutSeconds = 1
                }),
                TimeProvider.System,
                telemetry);
            var failing = new FailingModule();
            var factory = new SequenceModuleFactory(failing, new GatedCameraModule());
            using var applicationLifetime = new TestHostApplicationLifetime();
            var service = new CameraCaptureService(
                new ConfigurationAccessor(config),
                factory,
                ingress,
                new RecordingDistributor(),
                TimeProvider.System,
                new AstronomyEnginePlanetEphemeris(),
                telemetry,
                coordinator,
                new FleetRuntimeState(TimeProvider.System),
                applicationLifetime,
                NullLogger<CameraCaptureService>.Instance);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await applicationLifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            applicationLifetime.NotifyStarted();

            await failing.DisposalAttempted.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            Assert.AreEqual(1, factory.CreateCalls);
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task StopAsync_CancelsCaptureAndDisposesModule()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-capture-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = CreateConfig();
            var distributor = new RecordingDistributor();
            var ingress = new PassthroughRawIngress(root);
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = new CaptureAdmissionCoordinator(
                ingress,
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    RawIngressSqliteBusyTimeoutSeconds = 1
                }),
                TimeProvider.System,
                telemetry);
            var waitingModule = new GatedCameraModule();
            using (var waitingLifetime = new TestHostApplicationLifetime())
            {
                var waitingService = new CameraCaptureService(
                    new ConfigurationAccessor(config),
                    new ModuleFactory(waitingModule),
                    ingress,
                    distributor,
                    TimeProvider.System,
                    new AstronomyEnginePlanetEphemeris(),
                    telemetry,
                    coordinator,
                    new FleetRuntimeState(TimeProvider.System),
                    waitingLifetime,
                    NullLogger<CameraCaptureService>.Instance);

                await waitingService.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await waitingLifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
                await waitingService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
                Assert.IsFalse(waitingModule.InitializationStarted.Task.IsCompleted,
                    "Stopping before host startup must not initialize the camera.");
            }

            var module = new GatedCameraModule();
            using var applicationLifetime = new TestHostApplicationLifetime();
            var service = new CameraCaptureService(
                new ConfigurationAccessor(config),
                new ModuleFactory(module),
                ingress,
                distributor,
                TimeProvider.System,
                new AstronomyEnginePlanetEphemeris(),
                telemetry,
                coordinator,
                new FleetRuntimeState(TimeProvider.System),
                applicationLifetime,
                NullLogger<CameraCaptureService>.Instance);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await applicationLifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Assert.IsFalse(module.InitializationStarted.Task.IsCompleted,
                "Camera initialization must wait until the host has fully started.");
            applicationLifetime.NotifyStarted();
            await module.SecondCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await module.CaptureCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual(1, distributor.EphemeralCount);
            Assert.IsTrue(module.IsDisposed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
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

    private sealed class SequenceModuleFactory(params ICameraModule[] modules) : ICameraModuleFactory
    {
        private readonly Queue<ICameraModule> _modules = new(modules);

        public int CreateCalls { get; private set; }

        public ICameraModule Create(string moduleType)
        {
            CreateCalls++;
            return _modules.Dequeue();
        }
    }

    private sealed class FailingModule : ICameraModule
    {
        public TaskCompletionSource DisposalAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "failing";
        public string DisplayName => "Failing";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Injected initialization failure.");

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposalAttempted.TrySetResult();
            return ValueTask.FromException(new InvalidOperationException("Injected disposal failure."));
        }
    }

    private sealed class PassthroughRawIngress(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

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

        public TaskCompletionSource InitializationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "test";

        public string DisplayName => "Test";

        public string ModuleType => "Test";

        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
        {
            InitializationStarted.TrySetResult();
            return Task.CompletedTask;
        }

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

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();

        public TaskCompletionSource ApplicationStartedObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted
        {
            get
            {
                ApplicationStartedObserved.TrySetResult();
                return _started.Token;
            }
        }

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }

        public void NotifyStarted() => _started.Cancel();

        public void Dispose() => _started.Dispose();
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
