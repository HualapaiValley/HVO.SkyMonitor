using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Storage;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class StorageLifecycleTelemetryTests
{
    [TestMethod]
    public async Task WaitAsync_RecordsCallerDurationAndWarnsOnlyAboveBudget()
    {
        var observations = new List<(double Duration, string Caller)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CaptureProcessingTelemetry.MeterName &&
                instrument.Name == "camera_agent.storage.lifecycle.wait.duration")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var caller = tags.ToArray().Single().Value!.ToString()!;
            observations.Add((value, caller));
        });
        listener.Start();
        using var gate = new SemaphoreSlim(0, 1);
        using var activity = new Activity("storage-wait-test").Start();
        var clock = new WaitClock();
        var logger = new WaitLogger();
        var first = StorageLifecycleLock.WaitAsync(gate, "capture-storage", logger, CancellationToken.None, clock).AsTask();
        clock.Advance(StorageLifecycleTelemetry.WaitBudget);
        gate.Release();
        await first.ConfigureAwait(false);
        Assert.HasCount(1, observations);
        Assert.AreEqual((0.25, "capture-storage"), observations[0]);
        Assert.IsFalse(logger.Levels.Contains(LogLevel.Warning));
        var second = StorageLifecycleLock.WaitAsync(gate, "artifact-read", logger, CancellationToken.None, clock).AsTask();
        clock.Advance(StorageLifecycleTelemetry.WaitBudget + TimeSpan.FromMilliseconds(1));
        gate.Release();
        await second.ConfigureAwait(false);
        gate.Release();
        Assert.AreEqual((0.251, "artifact-read"), observations[1]);
        Assert.AreEqual(2, logger.Levels.Count(level => level == LogLevel.Debug));
        Assert.AreEqual(1, logger.Levels.Count(level => level == LogLevel.Warning));
        Assert.HasCount(2, activity.Events);
        Assert.IsFalse(activity.Events.SelectMany(item => item.Tags).Any(tag => tag.Key.Contains("path", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task WaitAsync_CancellationDoesNotRecordAcquisitionOrReleaseAnotherOwner()
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        using var activity = new Activity("storage-cancelled-test").Start();
        var logger = new WaitLogger();
        var waiting = StorageLifecycleLock.WaitAsync(gate, "reconciliation", logger, cancellation.Token, new WaitClock()).AsTask();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting).ConfigureAwait(false);
        Assert.AreEqual(0, gate.CurrentCount);
        CollectionAssert.AreEqual(new[] { LogLevel.Debug }, logger.Levels);
        Assert.AreEqual("storage.lifecycle.lock.cancelled", activity.Events.Single().Name);
    }

    private sealed class WaitClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class WaitLogger : ILogger
    {
        internal List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
