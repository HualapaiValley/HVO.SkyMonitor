using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
public sealed class CameraModuleRunnerTests
{
    [TestMethod]
    public async Task RunAsync_PublishesSubmissionsUntilCancelled()
    {
        // Arrange
        var config = CreateConfig();
        using var cts = new CancellationTokenSource();
        var hostContext = new TestHostContext(config, signalThreshold: 2);
        var module = new TestCameraModule();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var runner = new CameraModuleRunner(module, hostContext, timeProvider, NullLogger.Instance);

        // Act
        var runTask = runner.RunAsync(cts.Token);
        await hostContext.WaitForSignalAsync().ConfigureAwait(false);
        await cts.CancelAsync();
        await runTask.ConfigureAwait(false);

        // Assert
        Assert.AreEqual(2, hostContext.Submissions.Count, "Runner should publish submissions before cancellation.");
        Assert.AreEqual(2, module.CaptureCount, "Module should have been invoked twice.");
    }

    private static CameraModuleConfig CreateConfig()
    {
        using var specific = JsonDocument.Parse("{}");
        var descriptor = new CameraModuleDescriptor(
            typeof(HVO.SkyMonitor.CameraAgent.Common.Modules.NoOp.NoOpCameraModule).FullName ?? "NoOpCamera",
            specific.RootElement.Clone());

        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            descriptor,
            new CameraRigConfig(
                new SensorProfile("Sensor", 16, 16, 3.2, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("Equidistant", 2.8, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), 1, 10)),
            Array.Empty<CaptureProcessingStepConfig>());
    }

    private sealed class TestCameraModule : ICameraModule
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string DisplayName => "Test Module";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public int CaptureCount { get; private set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            var frame = new CameraFrame(
                DateTimeOffset.UtcNow,
                16,
                16,
                CameraPixelFormat.Mono8,
                new byte[16 * 16],
                new FrameMetadata(TimeSpan.FromMilliseconds(10), 1.0, 20));

            var result = new CaptureResult(
                frame,
                new CaptureSetpoint(TimeSpan.FromMilliseconds(10), 1.0, TimeSpan.FromMilliseconds(5), null),
                TimeSpan.FromMilliseconds(2),
                request.Mode,
                false);

            return Task.FromResult(result);
        }
    }

    private sealed class TestHostContext : ICaptureHostContext
    {
        private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _signalThreshold;

        public TestHostContext(CameraModuleConfig config, int signalThreshold)
        {
            Configuration = config;
            _signalThreshold = signalThreshold;
        }

        public CameraModuleConfig Configuration { get; }
        public List<CaptureLoopSubmission> Submissions { get; } = new();
        public Task<bool> WaitForSignalAsync() => _tcs.Task;

        public ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            Submissions.Add(submission);
            if (Submissions.Count >= _signalThreshold)
            {
                _tcs.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
