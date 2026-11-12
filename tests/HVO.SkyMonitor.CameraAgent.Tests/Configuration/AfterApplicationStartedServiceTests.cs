using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class AfterApplicationStartedServiceTests
{
    [TestMethod]
    public async Task HostStartsBeforeRecoveryAndStopsTheInitializedConsumer()
    {
        using var lifetime = new Lifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new Consumer(_ => { entered.SetResult(); return Task.CompletedTask; });
        using var service = new AfterApplicationStartedService<Consumer>(consumer, lifetime, CreateState(),
            NullLogger<AfterApplicationStartedService<Consumer>>.Instance);
        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(entered.Task.IsCompleted);
        lifetime.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsTrue(consumer.Stopped);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ShutdownCancelsPendingStartupAndDrainsTheConsumer(bool applicationStarted)
    {
        using var lifetime = new Lifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new Consumer(async cancellationToken =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        });
        using var service = new AfterApplicationStartedService<Consumer>(consumer, lifetime, CreateState(),
            NullLogger<AfterApplicationStartedService<Consumer>>.Instance);
        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        if (applicationStarted)
        {
            lifetime.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(applicationStarted, entered.Task.IsCompleted);
        Assert.IsTrue(consumer.Stopped);
    }

    [TestMethod]
    public async Task InitializationFailureKeepsRecoveryHostAvailableAndMarksReadinessUnhealthy()
    {
        using var lifetime = new Lifetime();
        var state = CreateState();
        var logger = new FailureLogger();
        var consumer = new Consumer(_ => throw new IOException("private-token /private/location"));
        using var service = new AfterApplicationStartedService<Consumer>(consumer, lifetime, state, logger);
        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        lifetime.Start();
        var message = await logger.Message.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneAvailability.Unhealthy, state.Snapshot.Availability);
        Assert.IsFalse(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.IsFalse(message.Contains("private-token", StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("/private/location", StringComparison.Ordinal));
        StringAssert.Contains(message, nameof(IOException), StringComparison.Ordinal);
        await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static CaptureLaneState CreateState() => new(TimeProvider.System, Options.Create(new CameraAgentHostOptions()));

    private sealed class Consumer(Func<CancellationToken, Task> start) : IHostedService
    {
        internal bool Stopped { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => start(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) { Stopped = true; return Task.CompletedTask; }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        internal void Start() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() { _started.Dispose(); _stopping.Dispose(); _stopped.Dispose(); }
    }

    private sealed class FailureLogger : ILogger<AfterApplicationStartedService<Consumer>>
    {
        internal TaskCompletionSource<string> Message { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Message.TrySetResult(formatter(state, exception));
    }
}
