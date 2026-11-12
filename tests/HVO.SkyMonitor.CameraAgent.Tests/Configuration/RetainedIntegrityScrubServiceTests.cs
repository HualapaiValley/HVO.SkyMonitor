using System.Collections.Concurrent;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
[TestCategory("Unit")]
public sealed class RetainedIntegrityScrubServiceTests
{
    private static readonly double[] CompleteBackoff = [1, 2, 4, 8];
    private static readonly double[] CancelledBackoff = [1];
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PermanentInitializationFailureHasFiniteBackoffAndShutdownCancelsPendingRetry(bool cancelDuringDelay)
    {
        var options = Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
            AgentId = "synthetic",
            Observatory = new ObservatoryLocation(35, -113, 0, "UTC"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        var configuration = await new FileCameraAgentConfigurationLoader(options,
            NullLogger<FileCameraAgentConfigurationLoader>.Instance).LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var accessor = new CameraAgentConfigurationAccessor();
        var loader = new QualifiedLoader(configuration);
        var pipeline = new FailingPipeline();
        var initializer = new CameraAgentConfigurationInitializer(loader, accessor, pipeline,
            NullLogger<CameraAgentConfigurationInitializer>.Instance);
        var clock = new RetryClock(cancelDuringDelay);
        var state = new CaptureLaneState(clock, options);
        var logger = new InitializationLogger();
        using var service = new RetainedIntegrityScrubService(loader, logger, clock, initializer, accessor,
            new QualifiedLocation(), captureState: state);
        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        if (cancelDuringDelay) await clock.DelayCreated.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        else await logger.FifthFailure.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(cancelDuringDelay ? 1 : 5, pipeline.Calls);
        CollectionAssert.AreEqual(cancelDuringDelay ? CancelledBackoff : CompleteBackoff, clock.Delays.ToArray());
        Assert.AreEqual(CaptureLaneAvailability.Unhealthy, state.Snapshot.Availability);
        Assert.IsFalse(accessor.IsConfigured);
        Assert.IsFalse(logger.Messages.Any(message => message.Contains("synthetic-private", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ScheduledFullScrubUsesThePreservedDeadlineAfterIncrementalRestart()
    {
        var options = Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
            AgentId = "synthetic",
            Observatory = new ObservatoryLocation(35, -113, 0, "UTC"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        var configuration = await new FileCameraAgentConfigurationLoader(options,
            NullLogger<FileCameraAgentConfigurationLoader>.Instance).LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var clock = new RetryClock(hold: true);
        using var service = new RetainedIntegrityScrubService(new QualifiedLoader(configuration),
            NullLogger<RetainedIntegrityScrubService>.Instance, clock,
            locations: new QualifiedLocation(DateTimeOffset.UnixEpoch.AddMinutes(5)));
        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(TimeSpan.FromMinutes(5), await clock.MaintenanceCreated.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class QualifiedLoader(CameraModuleConfig configuration) : ICameraAgentConfigurationLoader
    {
        public Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(configuration);
    }

    private sealed class QualifiedLocation(DateTimeOffset? refresh = null) : IDeploymentLocationStore
    {
        public DateTimeOffset? RetainedIntegrityRefreshUtc => refresh;
        public DeploymentLocationSnapshot? Active { get; } = DeploymentLocationSnapshot.Create("synthetic", 1, "configuration",
            null, DateTimeOffset.UnixEpoch, null, 35, -113, 0, "UTC");
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
            => ValueTask.FromResult(Active!);
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }

    private sealed class FailingPipeline : ICaptureProcessingPipelineFactory
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config)
        {
            Interlocked.Increment(ref _calls);
            throw new IOException("synthetic-private failure");
        }
        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) => throw new NotSupportedException();
    }

    private sealed class RetryClock(bool hold) : TimeProvider
    {
        internal ConcurrentQueue<double> Delays { get; } = new();
        internal TaskCompletionSource<TimeSpan> MaintenanceCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
        internal TaskCompletionSource DelayCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.FromSeconds(8))
            {
                MaintenanceCreated.TrySetResult(dueTime);
                return System.CreateTimer(callback, state, dueTime, period);
            }
            Delays.Enqueue(dueTime.TotalSeconds);
            DelayCreated.TrySetResult();
            if (!hold) ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new HeldTimer();
        }
    }

    private sealed class HeldTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InitializationLogger : ILogger<RetainedIntegrityScrubService>
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        internal TaskCompletionSource FifthFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 7312) return;
            Messages.Enqueue(formatter(state, exception));
            if (Messages.Count == 5) FifthFailure.TrySetResult();
        }
    }
}
