using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CentralTransientLifecycleTelemetryTests
{
    [TestMethod]
    public void PublishesBoundedMetricsAndIdentifierFreeSpans()
    {
        var measurements = new List<(string Name, string[] Tags)>();
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
            measurements.Add((instrument.Name, GetTagNames(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, GetTagNames(tags))));
        meterListener.Start();

        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CentralTransientLifecycleTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(activityListener);

        using var telemetry = new CentralTransientLifecycleTelemetry(
            NullLogger<CentralTransientLifecycleTelemetry>.Instance);
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

        measurements.Select(item => item.Name).Should().Contain([
            "skymonitor.central.transient.lifecycle.operations",
            "skymonitor.central.transient.lifecycle.duration",
            "skymonitor.central.transient.derivative.outputs",
            "skymonitor.central.transient.derivative.output.bytes"
        ]);
        measurements.SelectMany(item => item.Tags).Should()
            .OnlyContain(tag => new[] { "operation", "outcome", "kind" }.Contains(tag, StringComparer.Ordinal));
        activities.Should().OnlyContain(activity => !activity.TagObjects.Any());
        activities.Select(activity => activity.OperationName).Should().Contain([
            "central-transient.review",
            "central-transient.notification"
        ]);
        activities.Single(activity => activity.OperationName == "central-transient.notification")
            .Kind.Should().Be(ActivityKind.Consumer);
    }

    private static string[] GetTagNames(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var names = new string[tags.Length];
        for (var index = 0; index < tags.Length; index++)
        {
            names[index] = tags[index].Key;
        }
        return names;
    }
}
