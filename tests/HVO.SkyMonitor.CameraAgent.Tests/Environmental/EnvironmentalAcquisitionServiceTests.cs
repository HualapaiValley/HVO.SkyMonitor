using System.Collections.Concurrent;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalAcquisitionServiceTests
{
    [TestMethod]
    public async Task Retention_WhenFirstAttemptFails_RetriesWithoutReportingOutage()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(Path.GetTempPath(), $"hvo-environmental-{Guid.NewGuid():N}")
        });
        var factory = new EnvironmentalSourceFactory(services, []);
        using var coordinator = new EnvironmentalAcquisitionCoordinator(
            factory,
            Mock.Of<IEnvironmentalObservationPublisher>(),
            Mock.Of<IDeploymentLocationStore>(),
            options,
            TimeProvider.System);
        var retention = new FailOnceRetentionStore();
        var logger = new RecordingLogger<EnvironmentalAcquisitionService>();
        using var telemetry = new EnvironmentalAcquisitionTelemetry();
        using var service = new EnvironmentalAcquisitionService(
            coordinator,
            Mock.Of<IEnvironmentalAcquisitionStateStore>(),
            retention,
            telemetry,
            TimeProvider.System,
            options,
            logger);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await retention.Retried.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, retention.AttemptCount);
        Assert.IsFalse(logger.Events.Any(static entry => entry.EventId == 2526));
    }

    private sealed class FailOnceRetentionStore : ILocalEnvironmentalRetentionStore
    {
        private readonly TaskCompletionSource _retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public Task Retried => _retried.Task;

        public ValueTask<LocalEnvironmentalRetentionResult> RetainLocalAsync(
            string root,
            DateTimeOffset recordedBeforeUtc,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attemptCount) == 1)
            {
                return ValueTask.FromException<LocalEnvironmentalRetentionResult>(
                    new IOException("Injected transient retention failure."));
            }
            _retried.TrySetResult();
            return ValueTask.FromResult(new LocalEnvironmentalRetentionResult(0, 0, 0, 0));
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, int EventId)> Events { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Enqueue((logLevel, eventId.Id));
    }
}
