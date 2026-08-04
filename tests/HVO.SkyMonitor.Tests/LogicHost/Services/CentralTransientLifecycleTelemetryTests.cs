using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CentralTransientLifecycleTelemetryTests
{
    [TestMethod]
    public void PublishesBoundedMetricsAndIdentifierFreeSpans()
    {
        var measurements = new List<(string Name, IReadOnlyDictionary<string, string?> Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CentralTransientLifecycleTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, GetTags(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, GetTags(tags))));
        meterListener.Start();

        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CentralTransientLifecycleTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(activityListener);

        var logger = new RecordingLogger<CentralTransientLifecycleTelemetry>();
        using var telemetry = new CentralTransientLifecycleTelemetry(logger);
        using (telemetry.Start("review"))
        {
            telemetry.RecordReview("applied", TimeSpan.FromMilliseconds(2));
        }
        using (telemetry.Start("notification", ActivityKind.Consumer))
        {
            telemetry.RecordNotification("sent", TimeSpan.FromMilliseconds(3));
        }
        telemetry.RecordDerivative("Reconstruction", "persisted", 1024);
        telemetry.RecordDerivativeBundle("persisted", 5, TimeSpan.FromMilliseconds(4));
        telemetry.RecordReprocessing("scheduled", TimeSpan.FromMilliseconds(1));
        telemetry.RecordRetention("completed", 2, TimeSpan.FromMilliseconds(1));
        telemetry.RecordRetentionCreationConflict("deadlock", "retry", 1);
        telemetry.RecordRetentionCreationConflict("private-identifier", "private-outcome", int.MaxValue);

        measurements.Select(item => item.Name).Should().Contain([
            "skymonitor.central.transient.lifecycle.operations",
            "skymonitor.central.transient.lifecycle.duration",
            "skymonitor.central.transient.derivative.outputs",
            "skymonitor.central.transient.derivative.output.bytes",
            "skymonitor.central.transient.retention.creation.conflicts"
        ]);
        measurements.SelectMany(item => item.Tags.Keys).Should()
            .OnlyContain(tag => new[] { "operation", "outcome", "kind", "reason" }
                .Contains(tag, StringComparer.Ordinal));
        var conflicts = measurements.Where(item =>
            item.Name == "skymonitor.central.transient.retention.creation.conflicts").ToArray();
        conflicts.Should().HaveCount(2);
        conflicts[0].Tags.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["reason"] = "deadlock",
            ["outcome"] = "retry"
        });
        conflicts[1].Tags.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["reason"] = "other",
            ["outcome"] = "other"
        });
        var conflictLogs = logger.Entries.Where(item => item.EventId.Id == 2168).ToArray();
        conflictLogs.Should().HaveCount(2);
        conflictLogs[1].Fields.Should().Contain(new KeyValuePair<string, object?>("Reason", "other"));
        conflictLogs[1].Fields.Should().Contain(new KeyValuePair<string, object?>("Outcome", "other"));
        conflictLogs[1].Fields.Should().Contain(new KeyValuePair<string, object?>("Attempt", 4));
        activities.Should().OnlyContain(activity => !activity.TagObjects.Any());
        activities.Select(activity => activity.OperationName).Should().Contain([
            "central-transient.review",
            "central-transient.notification"
        ]);
        activities.Single(activity => activity.OperationName == "central-transient.notification")
            .Kind.Should().Be(ActivityKind.Consumer);
    }

    private static IReadOnlyDictionary<string, string?> GetTags(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < tags.Length; index++)
        {
            values[tags[index].Key] = tags[index].Value?.ToString();
        }
        return values;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<(LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> Fields)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Add((logLevel, eventId, fields));
        }
    }
}
